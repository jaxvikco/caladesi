using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Emit.Lowering;

/// <summary>
/// Makes try statements that contain awaits or yields expressible in a resumable method:
/// <list type="bullet">
/// <item><c>try/finally</c> becomes a catch-all that parks the exception, then the finally code runs as ordinary statements,
/// then the parked exception is rethrown and pending jumps (return, break, ...) are replayed.</item>
/// <item>catch bodies that contain suspensions run after the try statement instead of inside the handler.</item>
/// </list>
/// Try regions that still contain suspensions in their protected body are re-entered through state dispatch.
/// </summary>
internal sealed class TryRewriter(Compilation c, SuspensionFinder finder, Func<TypeSymbol, LocalSymbol> newTemp, TypeSymbol? returnType,
    Func<BoundExpression>? disposing, LabelSymbol endLabel) : BoundTreeRewriter
{
    private readonly Compilation _c = c;
    private readonly SuspensionFinder _finder = finder;
    private readonly Func<TypeSymbol, LocalSymbol> _newTemp = newTemp;
    private readonly TypeSymbol? _returnType = returnType;
    private readonly Func<BoundExpression>? _disposing = disposing;
    private readonly LabelSymbol _endLabel = endLabel;
    private readonly Stack<LabelSymbol> _finallyLabels = new();

    /// <summary>Where a resumed iterator that is being disposed continues (the innermost enclosing rewritten finally, or null for the end of the method).</summary>
    public Dictionary<BoundYieldReturn, LabelSymbol?> DisposeTargets { get; } = new(ReferenceEqualityComparer.Instance);

    private bool Has(BoundNode? n)
    {
        return _finder.Has(n);
    }

    private TypeSymbol Bool => _c.GetSpecialType(SpecialType.Boolean);
    private TypeSymbol Int => _c.GetSpecialType(SpecialType.Int32);
    private NamedTypeSymbol ExceptionType => _c.GetWellKnownType("System.Exception")!;

    private static BoundExpressionStatement Assign(BoundExpression target, BoundExpression value)
    {
        return new BoundExpressionStatement(new BoundAssignment(target, value) { IsDiscarded = true });
    }

    private static BoundBlock Block(IEnumerable<BoundStatement> stmts)
    {
        return new([], [.. stmts]);
    }

    protected override BoundNode VisitYieldReturn(BoundYieldReturn n)
    {
        DisposeTargets[n] = _finallyLabels.Count > 0 ? _finallyLabels.Peek() : null;
        return base.VisitYieldReturn(n);
    }

    protected override BoundNode VisitTry(BoundTry t)
    {
        if (!Has(t))
        {
            return t;
        }

        if (t.Finally != null && t.Catches.Length > 0)
        {
            BoundTry inner = new(t.Body, t.Catches, null) { Syntax = t.Syntax };
            t = new BoundTry(Block([inner]), [], t.Finally) { Syntax = t.Syntax };
        }
        return t.Finally != null ? RewriteFinally(t) : RewriteCatches(t);
    }

    // ------------------------------------------------------------------ finally

    private BoundBlock RewriteFinally(BoundTry t)
    {
        LabelSymbol finallyLabel = new("<finally>");
        NamedTypeSymbol exType = ExceptionType;
        LocalSymbol pending = _newTemp(exType);
        LocalSymbol branch = _newTemp(Int);
        LocalSymbol? retval = _returnType is { IsVoid: false } rt ? _newTemp(rt) : null;

        _finallyLabels.Push(finallyLabel);
        BoundBlock body = (BoundBlock)Visit(t.Body)!;
        _ = _finallyLabels.Pop();
        BoundBlock fin = (BoundBlock)Visit(t.Finally!)!;

        List<BoundStatement> jumps = [];
        BoundBlock rewrittenBody = (BoundBlock)new JumpOutRewriter(this, finallyLabel, branch, retval, jumps).Rewrite(body);

        List<BoundStatement> stmts =
        [
            Assign(new BoundLocal(pending), new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance)),
            Assign(new BoundLocal(branch), new BoundLiteral(new ConstantValue(0), Int)),
        ];
        LocalSymbol exTemp = _newTemp(exType);
        stmts.Add(new BoundTry(rewrittenBody,
            [new BoundCatch(exTemp, exType, Block([Assign(new BoundLocal(pending), new BoundLocal(exTemp))]))], null));
        stmts.Add(new BoundLabel(finallyLabel));
        stmts.Add(fin);
        stmts.Add(new BoundIf(new BoundUnary(UnaryOperatorKind.Not, new BoundIsNull(new BoundLocal(pending), Bool), Bool), Block([RethrowPending(pending)]), null));
        if (_disposing != null)
        {
            LabelSymbol target = _finallyLabels.Count > 0 ? _finallyLabels.Peek() : _endLabel;
            stmts.Add(new BoundIf(_disposing(), Block([new BoundGoto(target)]), null));
        }
        for (int k = 0; k < jumps.Count; k++)
        {
            stmts.Add(new BoundIf(
                new BoundBinary(BinaryOperatorKind.Equal, new BoundLocal(branch), new BoundLiteral(new ConstantValue(k + 1), Int), Bool, Int),
                Block([jumps[k]]), null));
        }
        return new BoundBlock([], [.. stmts]) { Syntax = t.Syntax };
    }

    private BoundExpressionStatement RethrowPending(LocalSymbol pending)
    {
        NamedTypeSymbol edi = _c.GetWellKnownType("System.Runtime.ExceptionServices.ExceptionDispatchInfo")!;
        MethodSymbol capture = edi.GetMembers("Capture").OfType<MethodSymbol>().First();
        MethodSymbol throwM = edi.GetMembers("Throw").OfType<MethodSymbol>().First(m => m.Parameters.Length == 0);
        BoundCall captured = new(null, capture, [new BoundLocal(pending)]);
        return new BoundExpressionStatement(new BoundCall(captured, throwM, []));
    }

    /// <summary>Replaces jumps that leave a try body with "remember where, go to the finally code".</summary>
    private sealed class JumpOutRewriter(TryRewriter o, LabelSymbol finallyLabel, LocalSymbol branch, LocalSymbol? retval, List<BoundStatement> jumps) : BoundTreeRewriter
    {
        private readonly HashSet<LabelSymbol> _inside = [];

        public BoundNode Rewrite(BoundBlock body)
        {
            _ = new LabelCollector(_inside).Visit(body);
            return Visit(body)!;
        }

        private sealed class LabelCollector(HashSet<LabelSymbol> labels) : BoundTreeRewriter
        {
            protected override BoundNode VisitWhile(BoundWhile n) { _ = labels.Add(n.BreakLabel); _ = labels.Add(n.ContinueLabel); return base.VisitWhile(n); }
            protected override BoundNode VisitDoWhile(BoundDoWhile n) { _ = labels.Add(n.BreakLabel); _ = labels.Add(n.ContinueLabel); return base.VisitDoWhile(n); }
            protected override BoundNode VisitFor(BoundFor n) { _ = labels.Add(n.BreakLabel); _ = labels.Add(n.ContinueLabel); return base.VisitFor(n); }
            public override BoundNode? Visit(BoundNode? node)
            {
                if (node is BoundLabel l)
                {
                    _ = labels.Add(l.Label);
                }

                return base.Visit(node);
            }
        }

        private int Register(BoundStatement replay)
        {
            jumps.Add(replay);
            return jumps.Count;
        }

        private BoundBlock Leave(int k)
        {
            return Block([
            Assign(new BoundLocal(branch), new BoundLiteral(new ConstantValue(k), o.Int)),
            new BoundGoto(finallyLabel),
        ]);
        }

        public override BoundNode? Visit(BoundNode? node)
        {
            switch (node)
            {
                case BoundGoto g when !_inside.Contains(g.Label):
                    return Leave(Register(g));
                case BoundConditionalGoto cg when !_inside.Contains(cg.Label):
                    {
                        int k = Register(new BoundGoto(cg.Label));
                        BoundExpression cond = cg.JumpIfTrue ? cg.Condition : new BoundUnary(UnaryOperatorKind.Not, cg.Condition, o.Bool);
                        return new BoundIf(cond, Leave(k), null);
                    }
                case BoundReturn r:
                    {
                        List<BoundStatement> stmts = [];
                        if (r.Expression != null && retval != null)
                        {
                            stmts.Add(Assign(new BoundLocal(retval), r.Expression));
                        }

                        int k = Register(new BoundReturn(r.Expression != null && retval != null ? new BoundLocal(retval) : null));
                        stmts.Add(Leave(k));
                        return Block(stmts);
                    }
                case BoundYieldBreak yb:
                    return Leave(Register(yb));
            }
            return base.Visit(node);
        }
    }

    // ------------------------------------------------------------------ catch

    private BoundNode RewriteCatches(BoundTry t)
    {
        BoundBlock body = (BoundBlock)Visit(t.Body)!;
        List<BoundCatch> catches = [.. t.Catches.Select(c => new BoundCatch(c.Local, c.ExceptionType, (BoundBlock)Visit(c.Body)!))];
        if (!catches.Any(c => Has(c.Body)))
        {
            return new BoundTry(body, [.. catches], null) { Syntax = t.Syntax };
        }

        NamedTypeSymbol exType = ExceptionType;
        LocalSymbol pending = _newTemp(exType);
        LocalSymbol caught = _newTemp(Int);
        ImmutableArray<BoundCatch>.Builder newCatches = ImmutableArray.CreateBuilder<BoundCatch>();
        List<BoundStatement> after = [];
        for (int i = 0; i < catches.Count; i++)
        {
            BoundCatch c = catches[i];
            LocalSymbol tmp = _newTemp(c.ExceptionType);
            newCatches.Add(new BoundCatch(tmp, c.ExceptionType, Block([
                Assign(new BoundLocal(pending), Convert(new BoundLocal(tmp), exType)),
                Assign(new BoundLocal(caught), new BoundLiteral(new ConstantValue(i + 1), Int)),
            ])));
        }
        BoundStatement? chain = null;
        for (int i = catches.Count - 1; i >= 0; i--)
        {
            BoundCatch c = catches[i];
            List<BoundStatement> stmts = [];
            if (c.Local != null)
            {
                stmts.Add(new BoundLocalDeclaration(c.Local, Convert(new BoundLocal(pending), c.ExceptionType)));
            }

            BoundBlock rethrowFixed = (BoundBlock)new RethrowReplacer(this, pending).Visit(c.Body)!;
            stmts.Add(rethrowFixed);
            chain = new BoundIf(
                new BoundBinary(BinaryOperatorKind.Equal, new BoundLocal(caught), new BoundLiteral(new ConstantValue(i + 1), Int), Bool, Int),
                Block(stmts), chain);
        }
        List<BoundStatement> all =
        [
            Assign(new BoundLocal(caught), new BoundLiteral(new ConstantValue(0), Int)),
            new BoundTry(body, newCatches.ToImmutable(), null),
            chain!,
        ];
        return new BoundBlock([], [.. all]) { Syntax = t.Syntax };
    }

    private BoundExpression Convert(BoundExpression e, TypeSymbol to)
    {
        if (e.Type!.Equals(to))
        {
            return e;
        }

        ConversionKind kind = to.Equals(ExceptionType) ? ConversionKind.ImplicitReference : ConversionKind.ExplicitReference;
        return new BoundConversion(e, kind, to);
    }

    private sealed class RethrowReplacer(TryRewriter o, LocalSymbol pending) : BoundTreeRewriter
    {
        protected override BoundNode VisitThrow(BoundThrow n)
        {
            return n.Expression == null ? o.RethrowPending(pending) : n;
        }

        protected override BoundNode VisitTry(BoundTry n)
        {
            // handlers of nested tries keep their own `throw;`
            BoundBlock body = (BoundBlock)Visit(n.Body)!;
            BoundBlock? fin = n.Finally == null ? null : (BoundBlock)Visit(n.Finally)!;
            return new BoundTry(body, n.Catches, fin) { Syntax = n.Syntax };
        }
    }
}
