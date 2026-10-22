using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Emit.Lowering;

/// <summary>Answers "does this subtree contain an await or a yield?" and caches the answer for every node.</summary>
internal sealed class SuspensionFinder : BoundTreeRewriter
{
    private readonly Dictionary<BoundNode, bool> _cache = new(ReferenceEqualityComparer.Instance);
    private bool _found;

    public bool Has(BoundNode? n)
    {
        if (n == null)
        {
            return false;
        }

        if (_cache.TryGetValue(n, out bool r))
        {
            return r;
        }

        _ = Visit(n);
        return _cache.TryGetValue(n, out r) && r;
    }

    public override BoundNode? Visit(BoundNode? node)
    {
        if (node == null)
        {
            return null;
        }

        if (_cache.TryGetValue(node, out bool known))
        {
            _found |= known;
            return node;
        }
        bool saved = _found;
        _found = node is BoundAwait or BoundYieldReturn or BoundYieldBreak;
        _ = base.Visit(node);
        _cache[node] = _found;
        _found |= saved;
        return node;
    }

    protected override BoundNode VisitTry(BoundTry n)
    {
        // catch bodies are not reached through the generic walk
        _ = Visit(n.Body);
        foreach (BoundCatch c in n.Catches)
        {
            _ = Visit(c.Body);
        }

        if (n.Finally != null)
        {
            _ = Visit(n.Finally);
        }

        return n;
    }
}

/// <summary>
/// Rewrites a method body so that every await is a statement of one of the shapes <c>await e;</c> or <c>tmp = await e;</c>
/// whose operand is free of awaits, moving everything evaluated before an await into temporaries (the evaluation stack must be
/// empty when a state machine suspends).
/// </summary>
internal sealed class SuspensionSpiller(SuspensionFinder finder, Func<TypeSymbol, LocalSymbol> newTemp, TypeSymbol boolType)
{
    private readonly SuspensionFinder _finder = finder;
    private readonly Func<TypeSymbol, LocalSymbol> _newTemp = newTemp;
    private readonly TypeSymbol _boolType = boolType;

    private bool Has(BoundNode? n)
    {
        return _finder.Has(n);
    }

    public BoundBlock Spill(BoundBlock body)
    {
        if (!Has(body))
        {
            return body;
        }

        List<BoundStatement> list = [];
        SpillStatement(body, list);
        return list.Count == 1 && list[0] is BoundBlock b ? b : new BoundBlock([], [.. list]);
    }

    // ------------------------------------------------------------------ statements

    private BoundStatement SpillToOne(BoundStatement s)
    {
        if (!Has(s))
        {
            return s;
        }

        List<BoundStatement> list = [];
        SpillStatement(s, list);
        return list.Count == 1 ? list[0] : new BoundBlock([], [.. list]);
    }

    private BoundBlock SpillBlock(BoundBlock b)
    {
        if (!Has(b))
        {
            return b;
        }

        List<BoundStatement> list = [];
        foreach (BoundStatement s in b.Statements)
        {
            SpillStatement(s, list);
        }

        return new BoundBlock(b.Locals, [.. list]) { Syntax = b.Syntax };
    }

    private static BoundExpressionStatement Assign(BoundExpression target, BoundExpression value)
    {
        return new BoundExpressionStatement(new BoundAssignment(target, value) { IsDiscarded = true });
    }

    private void SpillStatement(BoundStatement s, List<BoundStatement> output)
    {
        if (!Has(s))
        {
            output.Add(s);
            return;
        }
        switch (s)
        {
            case BoundBlock b:
                output.Add(SpillBlock(b));
                break;
            case BoundStatementList sl:
                foreach (BoundStatement x in sl.Statements)
                {
                    SpillStatement(x, output);
                }

                break;
            case BoundSequencePoint sp:
                {
                    List<BoundStatement> inner = [];
                    SpillStatement(sp.Statement, inner);
                    output.Add(new BoundSequencePoint(inner.Count == 1 ? inner[0] : new BoundStatementList([.. inner]), sp.SyntaxNodeForSpan) { Syntax = sp.Syntax });
                    break;
                }
            case BoundExpressionStatement es:
                if (es.Expression is BoundAwait { Type.IsVoid: true } voidAwait)
                {
                    BoundExpression operand = SpillExpr(voidAwait.Operand, output);
                    output.Add(new BoundExpressionStatement(new BoundAwait(operand, voidAwait.Info, voidAwait.Type!) { Syntax = voidAwait.Syntax }) { Syntax = es.Syntax });
                }
                else
                {
                    BoundExpression e = SpillExpr(es.Expression, output);
                    output.Add(new BoundExpressionStatement(e) { Syntax = es.Syntax });
                }
                break;
            case BoundLocalDeclaration ld:
                {
                    BoundExpression init = SpillExpr(ld.Initializer!, output);
                    output.Add(new BoundLocalDeclaration(ld.Local, init) { Syntax = ld.Syntax });
                    break;
                }
            case BoundIf i:
                {
                    BoundExpression c = SpillExpr(i.Condition, output);
                    output.Add(new BoundIf(c, SpillToOne(i.Consequence), i.Alternative == null ? null : SpillToOne(i.Alternative)) { Syntax = i.Syntax });
                    break;
                }
            case BoundWhile w when Has(w.Condition):
                {
                    output.Add(new BoundLabel(w.ContinueLabel));
                    BoundExpression c = SpillExpr(w.Condition, output);
                    output.Add(new BoundConditionalGoto(c, w.BreakLabel, jumpIfTrue: false));
                    SpillStatement(w.Body, output);
                    output.Add(new BoundGoto(w.ContinueLabel));
                    output.Add(new BoundLabel(w.BreakLabel));
                    break;
                }
            case BoundWhile w:
                output.Add(new BoundWhile(w.Condition, SpillToOne(w.Body), w.BreakLabel, w.ContinueLabel) { Syntax = w.Syntax });
                break;
            case BoundDoWhile dw when Has(dw.Condition):
                {
                    LabelSymbol top = new("<top>");
                    output.Add(new BoundLabel(top));
                    SpillStatement(dw.Body, output);
                    output.Add(new BoundLabel(dw.ContinueLabel));
                    BoundExpression c = SpillExpr(dw.Condition, output);
                    output.Add(new BoundConditionalGoto(c, top, jumpIfTrue: true));
                    output.Add(new BoundLabel(dw.BreakLabel));
                    break;
                }
            case BoundDoWhile dw:
                output.Add(new BoundDoWhile(dw.Condition, SpillToOne(dw.Body), dw.BreakLabel, dw.ContinueLabel) { Syntax = dw.Syntax });
                break;
            case BoundFor f when Has(f.Condition) || Has(f.Increment):
                {
                    List<BoundStatement> inner = [];
                    if (f.Initializer != null)
                    {
                        SpillStatement(f.Initializer, inner);
                    }

                    LabelSymbol top = new("<top>");
                    inner.Add(new BoundLabel(top));
                    if (f.Condition != null)
                    {
                        BoundExpression c = SpillExpr(f.Condition, inner);
                        inner.Add(new BoundConditionalGoto(c, f.BreakLabel, jumpIfTrue: false));
                    }
                    SpillStatement(f.Body, inner);
                    inner.Add(new BoundLabel(f.ContinueLabel));
                    if (f.Increment != null)
                    {
                        SpillStatement(f.Increment, inner);
                    }

                    inner.Add(new BoundGoto(top));
                    inner.Add(new BoundLabel(f.BreakLabel));
                    output.Add(new BoundBlock(f.Locals, [.. inner]) { Syntax = f.Syntax });
                    break;
                }
            case BoundFor f:
                output.Add(new BoundFor(f.Locals, f.Initializer == null ? null : SpillToOne(f.Initializer), f.Condition, f.Increment, SpillToOne(f.Body), f.BreakLabel, f.ContinueLabel) { Syntax = f.Syntax });
                break;
            case BoundConditionalGoto cg:
                output.Add(new BoundConditionalGoto(SpillExpr(cg.Condition, output), cg.Label, cg.JumpIfTrue) { Syntax = cg.Syntax });
                break;
            case BoundReturn r:
                output.Add(new BoundReturn(r.Expression == null ? null : SpillExpr(r.Expression, output)) { Syntax = r.Syntax });
                break;
            case BoundThrow t:
                output.Add(new BoundThrow(t.Expression == null ? null : SpillExpr(t.Expression, output)) { Syntax = t.Syntax });
                break;
            case BoundYieldReturn y:
                output.Add(new BoundYieldReturn(SpillExpr(y.Expression, output)) { Syntax = y.Syntax });
                break;
            case BoundTry t:
                {
                    ImmutableArray<BoundCatch>.Builder catches = ImmutableArray.CreateBuilder<BoundCatch>(t.Catches.Length);
                    foreach (BoundCatch c in t.Catches)
                    {
                        catches.Add(new BoundCatch(c.Local, c.ExceptionType, SpillBlock(c.Body)));
                    }

                    output.Add(new BoundTry(SpillBlock(t.Body), catches.MoveToImmutable(), t.Finally == null ? null : SpillBlock(t.Finally)) { Syntax = t.Syntax });
                    break;
                }
            default:
                output.Add(s);
                break;
        }
    }

    // ------------------------------------------------------------------ expressions

    private LocalSymbol Temp(TypeSymbol t)
    {
        return _newTemp(t);
    }

    private static bool IsStable(BoundExpression e)
    {
        return e is BoundLiteral or BoundThis or BoundLocal or BoundParameter or BoundDefaultValue or BoundTypeExpression or BoundBaseReference
        or BoundFieldAccess { Field.IsConst: true };
    }

    /// <summary>Evaluates <paramref name="e"/> into a fresh temporary (after spilling any awaits it contains).</summary>
    private BoundExpression ToTemp(BoundExpression e, List<BoundStatement> prefix)
    {
        e = SpillExpr(e, prefix);
        if (IsStable(e) && e is not BoundLocal)
        {
            return e;
        }

        LocalSymbol t = Temp(e.Type!);
        prefix.Add(Assign(new BoundLocal(t), e));
        return new BoundLocal(t);
    }

    /// <summary>Spills a list of operands that are evaluated left to right.</summary>
    private BoundExpression[] SpillOperands(List<BoundExpression?> ops, List<BoundStatement> prefix)
    {
        BoundExpression[] result = new BoundExpression[ops.Count];
        int last = -1;
        for (int i = 0; i < ops.Count; i++)
        {
            if (Has(ops[i]))
            {
                last = i;
            }
        }

        for (int i = 0; i < ops.Count; i++)
        {
            BoundExpression? op = ops[i];
            if (op == null)
            {
                continue;
            }

            result[i] = i < last ? IsStable(op) ? op : ToTemp(op, prefix) : i == last ? SpillExpr(op, prefix) : op;
        }
        return result;
    }

    public BoundExpression SpillExpr(BoundExpression e, List<BoundStatement> prefix)
    {
        if (!Has(e))
        {
            return e;
        }

        switch (e)
        {
            case BoundAwait a:
                {
                    BoundExpression operand = SpillExpr(a.Operand, prefix);
                    LocalSymbol t = Temp(a.Type!);
                    prefix.Add(new BoundExpressionStatement(new BoundAssignment(new BoundLocal(t), new BoundAwait(operand, a.Info, a.Type!) { Syntax = a.Syntax }) { IsDiscarded = true }) { Syntax = a.Syntax });
                    return new BoundLocal(t);
                }
            case BoundCall c:
                {
                    List<BoundExpression?> ops = [c.Receiver, .. c.Arguments];
                    BoundExpression[] r = SpillOperands(ops, prefix);
                    return new BoundCall(c.Receiver == null ? null : r[0], c.Method, [.. r.Skip(1)], c.Type) { Syntax = c.Syntax, IsBaseCall = c.IsBaseCall };
                }
            case BoundPropertyAccess pa:
                {
                    List<BoundExpression?> ops = [pa.Receiver, .. pa.Arguments];
                    BoundExpression[] r = SpillOperands(ops, prefix);
                    return new BoundPropertyAccess(pa.Receiver == null ? null : r[0], pa.Property, [.. r.Skip(1)]) { Syntax = pa.Syntax, IsBaseAccess = pa.IsBaseAccess };
                }
            case BoundFieldAccess fa:
                {
                    BoundExpression r = SpillExpr(fa.Receiver!, prefix);
                    return new BoundFieldAccess(r, fa.Field) { Syntax = fa.Syntax };
                }
            case BoundObjectCreation oc:
                {
                    List<BoundExpression?> ops = [.. oc.Arguments];
                    if (!oc.Initializers.IsDefaultOrEmpty)
                    {
                        ops.AddRange(oc.Initializers.Select(i => (BoundExpression?)i.Value));
                    }

                    BoundExpression[] r = SpillOperands(ops, prefix);
                    ImmutableArray<BoundExpression> args = [.. r.Take(oc.Arguments.Length)];
                    ImmutableArray<BoundMemberInit> inits = oc.Initializers.IsDefaultOrEmpty
                        ? oc.Initializers
                        : [.. oc.Initializers.Select((i, k) => new BoundMemberInit(i.Member, r[oc.Arguments.Length + k]))];
                    return new BoundObjectCreation(oc.Constructor, oc.Type!, args, inits) { Syntax = oc.Syntax };
                }
            case BoundArrayCreation ac:
                {
                    List<BoundExpression?> ops = [ac.Length, .. ac.Initializers];
                    BoundExpression[] r = SpillOperands(ops, prefix);
                    return new BoundArrayCreation(ac.Type!, ac.Length == null ? null : r[0], [.. r.Skip(1)]) { Syntax = ac.Syntax };
                }
            case BoundArrayAccess aa:
                {
                    BoundExpression[] r = SpillOperands([aa.Array, aa.Index], prefix);
                    return new BoundArrayAccess(r[0], r[1], aa.Type!) { Syntax = aa.Syntax };
                }
            case BoundConversion cv:
                return new BoundConversion(SpillExpr(cv.Operand, prefix), cv.Kind, cv.Type!, cv.Method) { Syntax = cv.Syntax, CaseIndex = cv.CaseIndex, ConstantFolded = cv.ConstantFolded };
            case BoundUnary u:
                return new BoundUnary(u.Kind, SpillExpr(u.Operand, prefix), u.Type!, u.Method) { Syntax = u.Syntax, Folded = u.Folded };
            case BoundBinary b:
                {
                    BoundExpression[] r = SpillOperands([b.Left, b.Right], prefix);
                    return new BoundBinary(b.Kind, r[0], r[1], b.Type!, b.OperandType, b.Method) { Syntax = b.Syntax, Folded = b.Folded };
                }
            case BoundTypeTest tt:
                return new BoundTypeTest(SpillExpr(tt.Operand, prefix), tt.Target, tt.Type!, tt.Kind, tt.CaseIndex) { Syntax = tt.Syntax };
            case BoundDelegateCreation dc:
                return new BoundDelegateCreation(SpillExpr(dc.Receiver!, prefix), dc.Method, dc.Type!) { Syntax = dc.Syntax };
            case BoundThrowExpression te:
                return new BoundThrowExpression(SpillExpr(te.Operand, prefix), te.Type!) { Syntax = te.Syntax };
            case BoundAddressOf ad:
                return new BoundAddressOf(SpillExpr(ad.Operand, prefix)) { Syntax = ad.Syntax };
            case BoundIsNull isn:
                return new BoundIsNull(SpillExpr(isn.Operand, prefix), isn.Type!) { Syntax = isn.Syntax };
            case BoundAssignment asg:
                return SpillAssignment(asg, prefix);
            case BoundSequence seq:
                {
                    foreach (BoundStatement s in seq.SideEffects)
                    {
                        SpillStatement(s, prefix);
                    }

                    BoundExpression v = SpillExpr(seq.Value, prefix);
                    if (seq.Locals.IsDefaultOrEmpty)
                    {
                        return v;
                    }
                    // the sequence's locals stay declared through a wrapper with no side effects
                    return new BoundSequence(seq.Locals, [], v) { Syntax = seq.Syntax };
                }
            case BoundLogicalBinary lb:
                {
                    if (!Has(lb.Right))
                    {
                        return new BoundLogicalBinary(lb.IsAnd, SpillExpr(lb.Left, prefix), lb.Right, lb.Type!) { Syntax = lb.Syntax };
                    }

                    LocalSymbol t = Temp(lb.Type!);
                    prefix.Add(Assign(new BoundLocal(t), SpillExpr(lb.Left, prefix)));
                    List<BoundStatement> inner = [];
                    inner.Add(Assign(new BoundLocal(t), SpillExpr(lb.Right, inner)));
                    BoundExpression cond = new BoundLocal(t);
                    if (!lb.IsAnd)
                    {
                        cond = new BoundUnary(UnaryOperatorKind.Not, cond, _boolType);
                    }

                    prefix.Add(new BoundIf(cond, new BoundBlock([], [.. inner]), null));
                    return new BoundLocal(t);
                }
            case BoundConditional co:
                {
                    if (!Has(co.WhenTrue) && !Has(co.WhenFalse))
                    {
                        return new BoundConditional(SpillExpr(co.Condition, prefix), co.WhenTrue, co.WhenFalse, co.Type!) { Syntax = co.Syntax };
                    }

                    LocalSymbol t = Temp(co.Type!);
                    BoundExpression c = SpillExpr(co.Condition, prefix);
                    List<BoundStatement> a = [];
                    a.Add(Assign(new BoundLocal(t), SpillExpr(co.WhenTrue, a)));
                    List<BoundStatement> bl = [];
                    bl.Add(Assign(new BoundLocal(t), SpillExpr(co.WhenFalse, bl)));
                    prefix.Add(new BoundIf(c, new BoundBlock([], [.. a]), new BoundBlock([], [.. bl])));
                    return new BoundLocal(t);
                }
            case BoundCoalesce cq:
                {
                    if (!Has(cq.Right))
                    {
                        return new BoundCoalesce(SpillExpr(cq.Left, prefix), cq.Right, cq.Type!) { Syntax = cq.Syntax };
                    }

                    BoundExpression left = ToTemp(cq.Left, prefix);
                    LocalSymbol res = Temp(cq.Type!);
                    List<BoundStatement> whenNull = [];
                    whenNull.Add(Assign(new BoundLocal(res), SpillExpr(cq.Right, whenNull)));
                    BoundStatement whenValue = Assign(new BoundLocal(res), new BoundCoalesce(left, new BoundDefaultValue(cq.Type!), cq.Type!));
                    prefix.Add(new BoundIf(new BoundIsNull(left, _boolType), new BoundBlock([], [.. whenNull]), whenValue));
                    return new BoundLocal(res);
                }
            case BoundConditionalAccess ca when !Has(ca.WhenNotNull):
                return new BoundConditionalAccess(SpillExpr(ca.Receiver, prefix), ca.Placeholder, ca.WhenNotNull, ca.Type) { Syntax = ca.Syntax };
        }
        throw new LoweringNotSupportedException("'await' is not supported inside this kind of expression (" + e.GetType().Name.Replace("Bound", "") + ")", e.Syntax);
    }

    private BoundAssignment SpillAssignment(BoundAssignment asg, List<BoundStatement> prefix)
    {
        BoundExpression target = asg.Target;
        BoundExpression newTarget = target;
        switch (target)
        {
            case BoundFieldAccess { Receiver: { } recv } fa:
                newTarget = new BoundFieldAccess(IsStable(recv) && !Has(recv) ? recv : ToTemp(recv, prefix), fa.Field) { Syntax = fa.Syntax };
                break;
            case BoundArrayAccess aa:
                newTarget = new BoundArrayAccess(Stabilize(aa.Array, prefix), Stabilize(aa.Index, prefix), aa.Type!) { Syntax = aa.Syntax };
                break;
            case BoundPropertyAccess pa:
                newTarget = new BoundPropertyAccess(pa.Receiver == null ? null : Stabilize(pa.Receiver, prefix), pa.Property,
                    [.. pa.Arguments.Select(a => Stabilize(a, prefix))])
                { Syntax = pa.Syntax, IsBaseAccess = pa.IsBaseAccess };
                break;
        }
        BoundExpression value = SpillExpr(asg.Value, prefix);
        return new BoundAssignment(newTarget, value) { Syntax = asg.Syntax, IsDiscarded = asg.IsDiscarded };
    }

    private BoundExpression Stabilize(BoundExpression e, List<BoundStatement> prefix)
    {
        return IsStable(e) && !Has(e) ? e : ToTemp(e, prefix);
    }
}

/// <summary>Thrown by lowering for constructs it cannot rewrite; reported as CAL0325.</summary>
public sealed class LoweringNotSupportedException : Exception
{
    public Syntax.SyntaxNode? Syntax { get; }

    public LoweringNotSupportedException()
    {
    }

    public LoweringNotSupportedException(string message)
        : base(message)
    {
    }

    public LoweringNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LoweringNotSupportedException(string message, Syntax.SyntaxNode? syntax)
        : base(message)
    {
        Syntax = syntax;
    }
}
