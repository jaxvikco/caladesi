using System.Collections.Immutable;

namespace Caladesi.Compiler.Bound;

/// <summary>Base class for passes that rebuild bound trees. Unchanged subtrees are reused.</summary>
public abstract class BoundTreeRewriter
{
    public virtual BoundNode? Visit(BoundNode? node)
    {
        return node switch
        {
            null => null,
            BoundBadExpression n => VisitBad(n),
            BoundLiteral n => VisitLiteral(n),
            BoundLocal n => VisitLocal(n),
            BoundParameter n => VisitParameter(n),
            BoundThis n => VisitThis(n),
            BoundBaseReference n => n,
            BoundFieldAccess n => VisitFieldAccess(n),
            BoundPropertyAccess n => VisitPropertyAccess(n),
            BoundCall n => VisitCall(n),
            BoundObjectCreation n => VisitObjectCreation(n),
            BoundDefaultValue n => n,
            BoundArrayCreation n => VisitArrayCreation(n),
            BoundArrayAccess n => VisitArrayAccess(n),
            BoundConversion n => VisitConversion(n),
            BoundUnary n => VisitUnary(n),
            BoundBinary n => VisitBinary(n),
            BoundLogicalBinary n => VisitLogical(n),
            BoundAssignment n => VisitAssignment(n),
            BoundConditional n => VisitConditional(n),
            BoundCoalesce n => VisitCoalesce(n),
            BoundConditionalReceiver n => VisitConditionalReceiver(n),
            BoundConditionalAccess n => VisitConditionalAccess(n),
            BoundTypeTest n => VisitTypeTest(n),
            BoundLambda n => VisitLambda(n),
            BoundDelegateCreation n => VisitDelegateCreation(n),
            BoundSequence n => VisitSequence(n),
            BoundTypeOf n => n,
            BoundAddressOf n => VisitAddressOf(n),
            BoundIsNull n => VisitIsNull(n),
            BoundAwait n => VisitAwait(n),
            BoundThrowExpression n => VisitThrowExpression(n),
            BoundTypeExpression or BoundNamespaceExpression or BoundMethodGroup or BoundUnbound or BoundModuleExpression => node,
            BoundBlock n => VisitBlock(n),
            BoundStatementList n => VisitStatementList(n),
            BoundExpressionStatement n => VisitExpressionStatement(n),
            BoundLocalDeclaration n => VisitLocalDeclaration(n),
            BoundIf n => VisitIf(n),
            BoundWhile n => VisitWhile(n),
            BoundDoWhile n => VisitDoWhile(n),
            BoundFor n => VisitFor(n),
            BoundYieldBreak yb => VisitYieldBreak(yb),
            BoundGoto or BoundLabel or BoundNoOp => node,
            BoundConditionalGoto n => VisitConditionalGoto(n),
            BoundReturn n => VisitReturn(n),
            BoundThrow n => VisitThrow(n),
            BoundTry n => VisitTry(n),
            BoundYieldReturn n => VisitYieldReturn(n),
            BoundSequencePoint n => VisitSequencePoint(n),
            _ => throw new InvalidOperationException("Unhandled bound node " + node.GetType().Name),
        };
    }

    public BoundExpression VisitExpr(BoundExpression e)
    {
        return (BoundExpression)Visit(e)!;
    }

    public BoundExpression? VisitExprOpt(BoundExpression? e)
    {
        return e == null ? null : (BoundExpression)Visit(e)!;
    }

    public BoundStatement VisitStmt(BoundStatement s)
    {
        return (BoundStatement)Visit(s)!;
    }

    public BoundStatement? VisitStmtOpt(BoundStatement? s)
    {
        return s == null ? null : (BoundStatement)Visit(s)!;
    }

    protected ImmutableArray<BoundExpression> VisitExprs(ImmutableArray<BoundExpression> list)
    {
        if (list.IsDefaultOrEmpty)
        {
            return list;
        }

        ImmutableArray<BoundExpression>.Builder? b = null;
        for (int i = 0; i < list.Length; i++)
        {
            BoundExpression n = VisitExpr(list[i]);
            if (!ReferenceEquals(n, list[i]) && b == null)
            {
                b = ImmutableArray.CreateBuilder<BoundExpression>(list.Length);
                for (int j = 0; j < i; j++)
                {
                    b.Add(list[j]);
                }
            }
            b?.Add(n);
        }
        return b?.MoveToImmutable() ?? list;
    }

    protected ImmutableArray<BoundStatement> VisitStmts(ImmutableArray<BoundStatement> list)
    {
        if (list.IsDefaultOrEmpty)
        {
            return list;
        }

        ImmutableArray<BoundStatement>.Builder? b = null;
        for (int i = 0; i < list.Length; i++)
        {
            BoundStatement n = VisitStmt(list[i]);
            if (!ReferenceEquals(n, list[i]) && b == null)
            {
                b = ImmutableArray.CreateBuilder<BoundStatement>(list.Length);
                for (int j = 0; j < i; j++)
                {
                    b.Add(list[j]);
                }
            }
            b?.Add(n);
        }
        return b?.MoveToImmutable() ?? list;
    }

    protected virtual BoundNode VisitBad(BoundBadExpression n)
    {
        return n;
    }

    protected virtual BoundNode VisitLiteral(BoundLiteral n)
    {
        return n;
    }

    protected virtual BoundNode VisitLocal(BoundLocal n)
    {
        return n;
    }

    protected virtual BoundNode VisitParameter(BoundParameter n)
    {
        return n;
    }

    protected virtual BoundNode VisitThis(BoundThis n)
    {
        return n;
    }

    protected virtual BoundNode VisitFieldAccess(BoundFieldAccess n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression? r = VisitExprOpt(n.Receiver);
        return ReferenceEquals(r, n.Receiver) ? n : new BoundFieldAccess(r, n.Field) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitPropertyAccess(BoundPropertyAccess n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression? r = VisitExprOpt(n.Receiver);
        ImmutableArray<BoundExpression> a = VisitExprs(n.Arguments);
        return ReferenceEquals(r, n.Receiver) && a == n.Arguments ? n : new BoundPropertyAccess(r, n.Property, a) { Syntax = n.Syntax, IsBaseAccess = n.IsBaseAccess };
    }

    protected virtual BoundNode VisitCall(BoundCall n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression? r = VisitExprOpt(n.Receiver);
        ImmutableArray<BoundExpression> a = VisitExprs(n.Arguments);
        return ReferenceEquals(r, n.Receiver) && a == n.Arguments ? n : new BoundCall(r, n.Method, a, n.Type) { Syntax = n.Syntax, IsBaseCall = n.IsBaseCall };
    }

    protected virtual BoundNode VisitObjectCreation(BoundObjectCreation n)
    {
        ArgumentNullException.ThrowIfNull(n);
        ImmutableArray<BoundExpression> a = VisitExprs(n.Arguments);
        bool changed = a != n.Arguments;
        ImmutableArray<BoundMemberInit> inits = n.Initializers;
        if (!inits.IsDefaultOrEmpty)
        {
            ImmutableArray<BoundMemberInit>.Builder b = ImmutableArray.CreateBuilder<BoundMemberInit>(inits.Length);
            foreach (BoundMemberInit i in inits)
            {
                BoundExpression v = VisitExpr(i.Value);
                if (!ReferenceEquals(v, i.Value)) { changed = true; b.Add(new BoundMemberInit(i.Member, v)); }
                else
                {
                    b.Add(i);
                }
            }
            inits = b.MoveToImmutable();
        }
        return changed ? new BoundObjectCreation(n.Constructor, n.Type!, a, inits) { Syntax = n.Syntax } : n;
    }

    protected virtual BoundNode VisitArrayCreation(BoundArrayCreation n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression? l = VisitExprOpt(n.Length);
        ImmutableArray<BoundExpression> i = VisitExprs(n.Initializers);
        return ReferenceEquals(l, n.Length) && i == n.Initializers ? n : new BoundArrayCreation(n.Type, l, i) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitArrayAccess(BoundArrayAccess n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression a = VisitExpr(n.Array);
        BoundExpression i = VisitExpr(n.Index);
        return ReferenceEquals(a, n.Array) && ReferenceEquals(i, n.Index) ? n : new BoundArrayAccess(a, i, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitConversion(BoundConversion n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundConversion(o, n.Kind, n.Type!, n.Method) { Syntax = n.Syntax, CaseIndex = n.CaseIndex, ConstantFolded = n.ConstantFolded };
    }

    protected virtual BoundNode VisitUnary(BoundUnary n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundUnary(n.Kind, o, n.Type!, n.Method) { Syntax = n.Syntax, Folded = n.Folded };
    }

    protected virtual BoundNode VisitBinary(BoundBinary n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression l = VisitExpr(n.Left);
        BoundExpression r = VisitExpr(n.Right);
        return ReferenceEquals(l, n.Left) && ReferenceEquals(r, n.Right) ? n : new BoundBinary(n.Kind, l, r, n.Type!, n.OperandType, n.Method) { Syntax = n.Syntax, Folded = n.Folded };
    }

    protected virtual BoundNode VisitLogical(BoundLogicalBinary n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression l = VisitExpr(n.Left);
        BoundExpression r = VisitExpr(n.Right);
        return ReferenceEquals(l, n.Left) && ReferenceEquals(r, n.Right) ? n : new BoundLogicalBinary(n.IsAnd, l, r, n.Type!) { Syntax = n.Syntax, Folded = n.Folded };
    }

    protected virtual BoundNode VisitAssignment(BoundAssignment n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression t = VisitExpr(n.Target);
        BoundExpression v = VisitExpr(n.Value);
        return ReferenceEquals(t, n.Target) && ReferenceEquals(v, n.Value) ? n : new BoundAssignment(t, v) { Syntax = n.Syntax, IsDiscarded = n.IsDiscarded };
    }

    protected virtual BoundNode VisitConditional(BoundConditional n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression c = VisitExpr(n.Condition);
        BoundExpression a = VisitExpr(n.WhenTrue);
        BoundExpression b = VisitExpr(n.WhenFalse);
        return ReferenceEquals(c, n.Condition) && ReferenceEquals(a, n.WhenTrue) && ReferenceEquals(b, n.WhenFalse) ? n : new BoundConditional(c, a, b, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitCoalesce(BoundCoalesce n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression l = VisitExpr(n.Left);
        BoundExpression r = VisitExpr(n.Right);
        return ReferenceEquals(l, n.Left) && ReferenceEquals(r, n.Right) ? n : new BoundCoalesce(l, r, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitConditionalReceiver(BoundConditionalReceiver n)
    {
        return n;
    }

    protected virtual BoundNode VisitConditionalAccess(BoundConditionalAccess n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression r = VisitExpr(n.Receiver);
        BoundExpression w = VisitExpr(n.WhenNotNull);
        return ReferenceEquals(r, n.Receiver) && ReferenceEquals(w, n.WhenNotNull) ? n : new BoundConditionalAccess(r, n.Placeholder, w, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitTypeTest(BoundTypeTest n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundTypeTest(o, n.Target, n.Type!, n.Kind, n.CaseIndex) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitLambda(BoundLambda n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundBlock b = (BoundBlock)Visit(n.Body)!;
        return ReferenceEquals(b, n.Body) ? n : new BoundLambda(n.Symbol, b, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitDelegateCreation(BoundDelegateCreation n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression? r = VisitExprOpt(n.Receiver);
        return ReferenceEquals(r, n.Receiver) ? n : new BoundDelegateCreation(r, n.Method, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitSequence(BoundSequence n)
    {
        ArgumentNullException.ThrowIfNull(n);
        ImmutableArray<BoundStatement> s = VisitStmts(n.SideEffects);
        BoundExpression v = VisitExpr(n.Value);
        return s == n.SideEffects && ReferenceEquals(v, n.Value) ? n : new BoundSequence(n.Locals, s, v) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitIsNull(BoundIsNull n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundIsNull(o, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitAddressOf(BoundAddressOf n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundAddressOf(o) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitAwait(BoundAwait n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundAwait(o, n.Info, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitThrowExpression(BoundThrowExpression n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundThrowExpression(o, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitBlock(BoundBlock n)
    {
        ArgumentNullException.ThrowIfNull(n);
        ImmutableArray<BoundStatement> s = VisitStmts(n.Statements);
        return s == n.Statements ? n : new BoundBlock(n.Locals, s) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitStatementList(BoundStatementList n)
    {
        ArgumentNullException.ThrowIfNull(n);
        ImmutableArray<BoundStatement> s = VisitStmts(n.Statements);
        return s == n.Statements ? n : new BoundStatementList(s) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitExpressionStatement(BoundExpressionStatement n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression e = VisitExpr(n.Expression);
        return ReferenceEquals(e, n.Expression) ? n : new BoundExpressionStatement(e) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitLocalDeclaration(BoundLocalDeclaration n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression? i = VisitExprOpt(n.Initializer);
        return ReferenceEquals(i, n.Initializer) ? n : new BoundLocalDeclaration(n.Local, i) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitIf(BoundIf n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression c = VisitExpr(n.Condition);
        BoundStatement a = VisitStmt(n.Consequence);
        BoundStatement? b = VisitStmtOpt(n.Alternative);
        return ReferenceEquals(c, n.Condition) && ReferenceEquals(a, n.Consequence) && ReferenceEquals(b, n.Alternative) ? n : new BoundIf(c, a, b) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitWhile(BoundWhile n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression c = VisitExpr(n.Condition);
        BoundStatement b = VisitStmt(n.Body);
        return ReferenceEquals(c, n.Condition) && ReferenceEquals(b, n.Body) ? n : new BoundWhile(c, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitDoWhile(BoundDoWhile n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression c = VisitExpr(n.Condition);
        BoundStatement b = VisitStmt(n.Body);
        return ReferenceEquals(c, n.Condition) && ReferenceEquals(b, n.Body) ? n : new BoundDoWhile(c, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitFor(BoundFor n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundStatement? i = VisitStmtOpt(n.Initializer);
        BoundExpression? c = VisitExprOpt(n.Condition);
        BoundStatement? inc = VisitStmtOpt(n.Increment);
        BoundStatement b = VisitStmt(n.Body);
        return ReferenceEquals(i, n.Initializer) && ReferenceEquals(c, n.Condition) && ReferenceEquals(inc, n.Increment) && ReferenceEquals(b, n.Body)
            ? n : new BoundFor(n.Locals, i, c, inc, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitConditionalGoto(BoundConditionalGoto n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression c = VisitExpr(n.Condition);
        return ReferenceEquals(c, n.Condition) ? n : new BoundConditionalGoto(c, n.Label, n.JumpIfTrue) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitReturn(BoundReturn n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression? e = VisitExprOpt(n.Expression);
        return ReferenceEquals(e, n.Expression) ? n : new BoundReturn(e) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitThrow(BoundThrow n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression? e = VisitExprOpt(n.Expression);
        return ReferenceEquals(e, n.Expression) ? n : new BoundThrow(e) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitTry(BoundTry n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundBlock body = (BoundBlock)Visit(n.Body)!;
        bool changed = !ReferenceEquals(body, n.Body);
        ImmutableArray<BoundCatch>.Builder catches = ImmutableArray.CreateBuilder<BoundCatch>(n.Catches.Length);
        foreach (BoundCatch c in n.Catches)
        {
            BoundBlock cb = (BoundBlock)Visit(c.Body)!;
            if (!ReferenceEquals(cb, c.Body)) { changed = true; catches.Add(new BoundCatch(c.Local, c.ExceptionType, cb)); }
            else
            {
                catches.Add(c);
            }
        }
        BoundBlock? fin = n.Finally == null ? null : (BoundBlock)Visit(n.Finally)!;
        if (!ReferenceEquals(fin, n.Finally))
        {
            changed = true;
        }

        return changed ? new BoundTry(body, catches.MoveToImmutable(), fin) { Syntax = n.Syntax } : n;
    }

    protected virtual BoundNode VisitYieldBreak(BoundYieldBreak n)
    {
        return n;
    }

    protected virtual BoundNode VisitYieldReturn(BoundYieldReturn n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundExpression e = VisitExpr(n.Expression);
        return ReferenceEquals(e, n.Expression) ? n : new BoundYieldReturn(e) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitSequencePoint(BoundSequencePoint n)
    {
        ArgumentNullException.ThrowIfNull(n);
        BoundStatement s = VisitStmt(n.Statement);
        return ReferenceEquals(s, n.Statement) ? n : new BoundSequencePoint(s, n.SyntaxNodeForSpan) { Syntax = n.Syntax };
    }
}
