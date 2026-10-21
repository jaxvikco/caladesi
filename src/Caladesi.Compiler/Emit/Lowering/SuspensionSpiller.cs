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
        if (n == null) return false;
        if (_cache.TryGetValue(n, out var r)) return r;
        Visit(n);
        return _cache.TryGetValue(n, out r) && r;
    }

    public override BoundNode? Visit(BoundNode? node)
    {
        if (node == null) return null;
        if (_cache.TryGetValue(node, out var known))
        {
            _found |= known;
            return node;
        }
        var saved = _found;
        _found = node is BoundAwait or BoundYieldReturn or BoundYieldBreak;
        base.Visit(node);
        _cache[node] = _found;
        _found |= saved;
        return node;
    }

    protected override BoundNode VisitTry(BoundTry n)
    {
        // catch bodies are not reached through the generic walk
        Visit(n.Body);
        foreach (var c in n.Catches) Visit(c.Body);
        if (n.Finally != null) Visit(n.Finally);
        return n;
    }
}

/// <summary>
/// Rewrites a method body so that every await is a statement of one of the shapes <c>await e;</c> or <c>tmp = await e;</c>
/// whose operand is free of awaits, moving everything evaluated before an await into temporaries (the evaluation stack must be
/// empty when a state machine suspends).
/// </summary>
internal sealed class SuspensionSpiller
{
    private readonly SuspensionFinder _finder;
    private readonly Func<TypeSymbol, LocalSymbol> _newTemp;
    private readonly TypeSymbol _boolType;

    public SuspensionSpiller(SuspensionFinder finder, Func<TypeSymbol, LocalSymbol> newTemp, TypeSymbol boolType)
    {
        _finder = finder;
        _newTemp = newTemp;
        _boolType = boolType;
    }

    private bool Has(BoundNode? n) => _finder.Has(n);

    public BoundBlock Spill(BoundBlock body)
    {
        if (!Has(body)) return body;
        var list = new List<BoundStatement>();
        SpillStatement(body, list);
        return list.Count == 1 && list[0] is BoundBlock b ? b : new BoundBlock(ImmutableArray<LocalSymbol>.Empty, list.ToImmutableArray());
    }

    // ------------------------------------------------------------------ statements

    private BoundStatement SpillToOne(BoundStatement s)
    {
        if (!Has(s)) return s;
        var list = new List<BoundStatement>();
        SpillStatement(s, list);
        return list.Count == 1 ? list[0] : new BoundBlock(ImmutableArray<LocalSymbol>.Empty, list.ToImmutableArray());
    }

    private BoundBlock SpillBlock(BoundBlock b)
    {
        if (!Has(b)) return b;
        var list = new List<BoundStatement>();
        foreach (var s in b.Statements) SpillStatement(s, list);
        return new BoundBlock(b.Locals, list.ToImmutableArray()) { Syntax = b.Syntax };
    }

    private static BoundStatement Assign(BoundExpression target, BoundExpression value) =>
        new BoundExpressionStatement(new BoundAssignment(target, value) { IsDiscarded = true });

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
                foreach (var x in sl.Statements) SpillStatement(x, output);
                break;
            case BoundSequencePoint sp:
            {
                var inner = new List<BoundStatement>();
                SpillStatement(sp.Statement, inner);
                output.Add(new BoundSequencePoint(inner.Count == 1 ? inner[0] : new BoundStatementList(inner.ToImmutableArray()), sp.SyntaxNodeForSpan) { Syntax = sp.Syntax });
                break;
            }
            case BoundExpressionStatement es:
                if (es.Expression is BoundAwait { Type.IsVoid: true } voidAwait)
                {
                    var operand = SpillExpr(voidAwait.Operand, output);
                    output.Add(new BoundExpressionStatement(new BoundAwait(operand, voidAwait.Info, voidAwait.Type!) { Syntax = voidAwait.Syntax }) { Syntax = es.Syntax });
                }
                else
                {
                    var e = SpillExpr(es.Expression, output);
                    output.Add(new BoundExpressionStatement(e) { Syntax = es.Syntax });
                }
                break;
            case BoundLocalDeclaration ld:
            {
                var init = SpillExpr(ld.Initializer!, output);
                output.Add(new BoundLocalDeclaration(ld.Local, init) { Syntax = ld.Syntax });
                break;
            }
            case BoundIf i:
            {
                var c = SpillExpr(i.Condition, output);
                output.Add(new BoundIf(c, SpillToOne(i.Consequence), i.Alternative == null ? null : SpillToOne(i.Alternative)) { Syntax = i.Syntax });
                break;
            }
            case BoundWhile w when Has(w.Condition):
            {
                output.Add(new BoundLabel(w.ContinueLabel));
                var c = SpillExpr(w.Condition, output);
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
                var top = new LabelSymbol("<top>");
                output.Add(new BoundLabel(top));
                SpillStatement(dw.Body, output);
                output.Add(new BoundLabel(dw.ContinueLabel));
                var c = SpillExpr(dw.Condition, output);
                output.Add(new BoundConditionalGoto(c, top, jumpIfTrue: true));
                output.Add(new BoundLabel(dw.BreakLabel));
                break;
            }
            case BoundDoWhile dw:
                output.Add(new BoundDoWhile(dw.Condition, SpillToOne(dw.Body), dw.BreakLabel, dw.ContinueLabel) { Syntax = dw.Syntax });
                break;
            case BoundFor f when Has(f.Condition) || Has(f.Increment):
            {
                var inner = new List<BoundStatement>();
                if (f.Initializer != null) SpillStatement(f.Initializer, inner);
                var top = new LabelSymbol("<top>");
                inner.Add(new BoundLabel(top));
                if (f.Condition != null)
                {
                    var c = SpillExpr(f.Condition, inner);
                    inner.Add(new BoundConditionalGoto(c, f.BreakLabel, jumpIfTrue: false));
                }
                SpillStatement(f.Body, inner);
                inner.Add(new BoundLabel(f.ContinueLabel));
                if (f.Increment != null) SpillStatement(f.Increment, inner);
                inner.Add(new BoundGoto(top));
                inner.Add(new BoundLabel(f.BreakLabel));
                output.Add(new BoundBlock(f.Locals, inner.ToImmutableArray()) { Syntax = f.Syntax });
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
                var catches = ImmutableArray.CreateBuilder<BoundCatch>(t.Catches.Length);
                foreach (var c in t.Catches) catches.Add(new BoundCatch(c.Local, c.ExceptionType, SpillBlock(c.Body)));
                output.Add(new BoundTry(SpillBlock(t.Body), catches.MoveToImmutable(), t.Finally == null ? null : SpillBlock(t.Finally)) { Syntax = t.Syntax });
                break;
            }
            default:
                output.Add(s);
                break;
        }
    }

    // ------------------------------------------------------------------ expressions

    private LocalSymbol Temp(TypeSymbol t) => _newTemp(t);

    private static bool IsStable(BoundExpression e) =>
        e is BoundLiteral or BoundThis or BoundLocal or BoundParameter or BoundDefaultValue or BoundTypeExpression or BoundBaseReference
        || e is BoundFieldAccess { Field.IsConst: true };

    /// <summary>Evaluates <paramref name="e"/> into a fresh temporary (after spilling any awaits it contains).</summary>
    private BoundExpression ToTemp(BoundExpression e, List<BoundStatement> prefix)
    {
        e = SpillExpr(e, prefix);
        if (IsStable(e) && e is not BoundLocal) return e;
        var t = Temp(e.Type!);
        prefix.Add(Assign(new BoundLocal(t), e));
        return new BoundLocal(t);
    }

    /// <summary>Spills a list of operands that are evaluated left to right.</summary>
    private BoundExpression[] SpillOperands(IReadOnlyList<BoundExpression?> ops, List<BoundStatement> prefix)
    {
        var result = new BoundExpression[ops.Count];
        var last = -1;
        for (var i = 0; i < ops.Count; i++) if (Has(ops[i])) last = i;
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op == null) continue;
            if (i < last) result[i] = IsStable(op) ? op : ToTemp(op, prefix);
            else if (i == last) result[i] = SpillExpr(op, prefix);
            else result[i] = op;
        }
        return result;
    }

    public BoundExpression SpillExpr(BoundExpression e, List<BoundStatement> prefix)
    {
        if (!Has(e)) return e;
        switch (e)
        {
            case BoundAwait a:
            {
                var operand = SpillExpr(a.Operand, prefix);
                var t = Temp(a.Type!);
                prefix.Add(new BoundExpressionStatement(new BoundAssignment(new BoundLocal(t), new BoundAwait(operand, a.Info, a.Type!) { Syntax = a.Syntax }) { IsDiscarded = true }) { Syntax = a.Syntax });
                return new BoundLocal(t);
            }
            case BoundCall c:
            {
                var ops = new List<BoundExpression?> { c.Receiver };
                ops.AddRange(c.Arguments);
                var r = SpillOperands(ops, prefix);
                return new BoundCall(c.Receiver == null ? null : r[0], c.Method, r.Skip(1).ToImmutableArray(), c.Type) { Syntax = c.Syntax, IsBaseCall = c.IsBaseCall };
            }
            case BoundPropertyAccess pa:
            {
                var ops = new List<BoundExpression?> { pa.Receiver };
                ops.AddRange(pa.Arguments);
                var r = SpillOperands(ops, prefix);
                return new BoundPropertyAccess(pa.Receiver == null ? null : r[0], pa.Property, r.Skip(1).ToImmutableArray()) { Syntax = pa.Syntax, IsBaseAccess = pa.IsBaseAccess };
            }
            case BoundFieldAccess fa:
            {
                var r = SpillExpr(fa.Receiver!, prefix);
                return new BoundFieldAccess(r, fa.Field) { Syntax = fa.Syntax };
            }
            case BoundObjectCreation oc:
            {
                var ops = new List<BoundExpression?>(oc.Arguments);
                if (!oc.Initializers.IsDefaultOrEmpty) ops.AddRange(oc.Initializers.Select(i => (BoundExpression?)i.Value));
                var r = SpillOperands(ops, prefix);
                var args = r.Take(oc.Arguments.Length).ToImmutableArray();
                var inits = oc.Initializers.IsDefaultOrEmpty
                    ? oc.Initializers
                    : oc.Initializers.Select((i, k) => new BoundMemberInit(i.Member, r[oc.Arguments.Length + k])).ToImmutableArray();
                return new BoundObjectCreation(oc.Constructor, oc.Type!, args, inits) { Syntax = oc.Syntax };
            }
            case BoundArrayCreation ac:
            {
                var ops = new List<BoundExpression?> { ac.Length };
                ops.AddRange(ac.Initializers);
                var r = SpillOperands(ops, prefix);
                return new BoundArrayCreation((ArrayTypeSymbol)ac.Type!, ac.Length == null ? null : r[0], r.Skip(1).ToImmutableArray()) { Syntax = ac.Syntax };
            }
            case BoundArrayAccess aa:
            {
                var r = SpillOperands([aa.Array, aa.Index], prefix);
                return new BoundArrayAccess(r[0], r[1], aa.Type!) { Syntax = aa.Syntax };
            }
            case BoundConversion cv:
                return new BoundConversion(SpillExpr(cv.Operand, prefix), cv.Kind, cv.Type!, cv.Method) { Syntax = cv.Syntax, CaseIndex = cv.CaseIndex, ConstantFolded = cv.ConstantFolded };
            case BoundUnary u:
                return new BoundUnary(u.Kind, SpillExpr(u.Operand, prefix), u.Type!, u.Method) { Syntax = u.Syntax, Folded = u.Folded };
            case BoundBinary b:
            {
                var r = SpillOperands([b.Left, b.Right], prefix);
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
                foreach (var s in seq.SideEffects) SpillStatement(s, prefix);
                var v = SpillExpr(seq.Value, prefix);
                if (seq.Locals.IsDefaultOrEmpty) return v;
                // the sequence's locals stay declared through a wrapper with no side effects
                return new BoundSequence(seq.Locals, ImmutableArray<BoundStatement>.Empty, v) { Syntax = seq.Syntax };
            }
            case BoundLogicalBinary lb:
            {
                if (!Has(lb.Right))
                    return new BoundLogicalBinary(lb.IsAnd, SpillExpr(lb.Left, prefix), lb.Right, lb.Type!) { Syntax = lb.Syntax };
                var t = Temp(lb.Type!);
                prefix.Add(Assign(new BoundLocal(t), SpillExpr(lb.Left, prefix)));
                var inner = new List<BoundStatement>();
                inner.Add(Assign(new BoundLocal(t), SpillExpr(lb.Right, inner)));
                BoundExpression cond = new BoundLocal(t);
                if (!lb.IsAnd) cond = new BoundUnary(UnaryOperatorKind.Not, cond, _boolType);
                prefix.Add(new BoundIf(cond, new BoundBlock(ImmutableArray<LocalSymbol>.Empty, inner.ToImmutableArray()), null));
                return new BoundLocal(t);
            }
            case BoundConditional co:
            {
                if (!Has(co.WhenTrue) && !Has(co.WhenFalse))
                    return new BoundConditional(SpillExpr(co.Condition, prefix), co.WhenTrue, co.WhenFalse, co.Type!) { Syntax = co.Syntax };
                var t = Temp(co.Type!);
                var c = SpillExpr(co.Condition, prefix);
                var a = new List<BoundStatement>();
                a.Add(Assign(new BoundLocal(t), SpillExpr(co.WhenTrue, a)));
                var bl = new List<BoundStatement>();
                bl.Add(Assign(new BoundLocal(t), SpillExpr(co.WhenFalse, bl)));
                prefix.Add(new BoundIf(c, new BoundBlock(ImmutableArray<LocalSymbol>.Empty, a.ToImmutableArray()), new BoundBlock(ImmutableArray<LocalSymbol>.Empty, bl.ToImmutableArray())));
                return new BoundLocal(t);
            }
            case BoundCoalesce cq:
            {
                if (!Has(cq.Right))
                    return new BoundCoalesce(SpillExpr(cq.Left, prefix), cq.Right, cq.Type!) { Syntax = cq.Syntax };
                var left = ToTemp(cq.Left, prefix);
                var res = Temp(cq.Type!);
                var whenNull = new List<BoundStatement>();
                whenNull.Add(Assign(new BoundLocal(res), SpillExpr(cq.Right, whenNull)));
                var whenValue = Assign(new BoundLocal(res), new BoundCoalesce(left, new BoundDefaultValue(cq.Type!), cq.Type!));
                prefix.Add(new BoundIf(new BoundIsNull(left, _boolType), new BoundBlock(ImmutableArray<LocalSymbol>.Empty, whenNull.ToImmutableArray()), whenValue));
                return new BoundLocal(res);
            }
            case BoundConditionalAccess ca when !Has(ca.WhenNotNull):
                return new BoundConditionalAccess(SpillExpr(ca.Receiver, prefix), ca.Placeholder, ca.WhenNotNull, ca.Type) { Syntax = ca.Syntax };
        }
        throw new NotSupportedException("await is not supported inside this kind of expression: " + e.GetType().Name);
    }

    private BoundExpression SpillAssignment(BoundAssignment asg, List<BoundStatement> prefix)
    {
        var target = asg.Target;
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
                    pa.Arguments.Select(a => Stabilize(a, prefix)).ToImmutableArray()) { Syntax = pa.Syntax, IsBaseAccess = pa.IsBaseAccess };
                break;
        }
        var value = SpillExpr(asg.Value, prefix);
        return new BoundAssignment(newTarget, value) { Syntax = asg.Syntax, IsDiscarded = asg.IsDiscarded };
    }

    private BoundExpression Stabilize(BoundExpression e, List<BoundStatement> prefix) =>
        IsStable(e) && !Has(e) ? e : ToTemp(e, prefix);
}
