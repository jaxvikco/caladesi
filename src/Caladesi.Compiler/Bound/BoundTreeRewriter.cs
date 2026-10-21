using System.Collections.Immutable;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Bound;

/// <summary>Base class for passes that rebuild bound trees. Unchanged subtrees are reused.</summary>
public abstract class BoundTreeRewriter
{
    public virtual BoundNode? Visit(BoundNode? node)
    {
        switch (node)
        {
            case null: return null;
            case BoundBadExpression n: return VisitBad(n);
            case BoundLiteral n: return VisitLiteral(n);
            case BoundLocal n: return VisitLocal(n);
            case BoundParameter n: return VisitParameter(n);
            case BoundThis n: return VisitThis(n);
            case BoundBaseReference n: return n;
            case BoundFieldAccess n: return VisitFieldAccess(n);
            case BoundPropertyAccess n: return VisitPropertyAccess(n);
            case BoundCall n: return VisitCall(n);
            case BoundObjectCreation n: return VisitObjectCreation(n);
            case BoundDefaultValue n: return n;
            case BoundArrayCreation n: return VisitArrayCreation(n);
            case BoundArrayAccess n: return VisitArrayAccess(n);
            case BoundConversion n: return VisitConversion(n);
            case BoundUnary n: return VisitUnary(n);
            case BoundBinary n: return VisitBinary(n);
            case BoundLogicalBinary n: return VisitLogical(n);
            case BoundAssignment n: return VisitAssignment(n);
            case BoundConditional n: return VisitConditional(n);
            case BoundCoalesce n: return VisitCoalesce(n);
            case BoundConditionalReceiver n: return VisitConditionalReceiver(n);
            case BoundConditionalAccess n: return VisitConditionalAccess(n);
            case BoundTypeTest n: return VisitTypeTest(n);
            case BoundLambda n: return VisitLambda(n);
            case BoundDelegateCreation n: return VisitDelegateCreation(n);
            case BoundSequence n: return VisitSequence(n);
            case BoundTypeOf n: return n;
            case BoundAddressOf n: return VisitAddressOf(n);
            case BoundIsNull n: return VisitIsNull(n);
            case BoundAwait n: return VisitAwait(n);
            case BoundThrowExpression n: return VisitThrowExpression(n);
            case BoundTypeExpression or BoundNamespaceExpression or BoundMethodGroup or BoundUnbound or BoundModuleExpression: return node;
            case BoundBlock n: return VisitBlock(n);
            case BoundStatementList n: return VisitStatementList(n);
            case BoundExpressionStatement n: return VisitExpressionStatement(n);
            case BoundLocalDeclaration n: return VisitLocalDeclaration(n);
            case BoundIf n: return VisitIf(n);
            case BoundWhile n: return VisitWhile(n);
            case BoundDoWhile n: return VisitDoWhile(n);
            case BoundFor n: return VisitFor(n);
            case BoundGoto or BoundLabel or BoundYieldBreak or BoundNoOp: return node;
            case BoundConditionalGoto n: return VisitConditionalGoto(n);
            case BoundReturn n: return VisitReturn(n);
            case BoundThrow n: return VisitThrow(n);
            case BoundTry n: return VisitTry(n);
            case BoundYieldReturn n: return VisitYieldReturn(n);
            case BoundSequencePoint n: return VisitSequencePoint(n);
            default: throw new InvalidOperationException("Unhandled bound node " + node.GetType().Name);
        }
    }

    public BoundExpression VisitExpr(BoundExpression e) => (BoundExpression)Visit(e)!;
    public BoundExpression? VisitExprOpt(BoundExpression? e) => e == null ? null : (BoundExpression)Visit(e)!;
    public BoundStatement VisitStmt(BoundStatement s) => (BoundStatement)Visit(s)!;
    public BoundStatement? VisitStmtOpt(BoundStatement? s) => s == null ? null : (BoundStatement)Visit(s)!;

    protected ImmutableArray<BoundExpression> VisitExprs(ImmutableArray<BoundExpression> list)
    {
        if (list.IsDefaultOrEmpty) return list;
        ImmutableArray<BoundExpression>.Builder? b = null;
        for (var i = 0; i < list.Length; i++)
        {
            var n = VisitExpr(list[i]);
            if (!ReferenceEquals(n, list[i]) && b == null)
            {
                b = ImmutableArray.CreateBuilder<BoundExpression>(list.Length);
                for (var j = 0; j < i; j++) b.Add(list[j]);
            }
            b?.Add(n);
        }
        return b?.MoveToImmutable() ?? list;
    }

    protected ImmutableArray<BoundStatement> VisitStmts(ImmutableArray<BoundStatement> list)
    {
        if (list.IsDefaultOrEmpty) return list;
        ImmutableArray<BoundStatement>.Builder? b = null;
        for (var i = 0; i < list.Length; i++)
        {
            var n = VisitStmt(list[i]);
            if (!ReferenceEquals(n, list[i]) && b == null)
            {
                b = ImmutableArray.CreateBuilder<BoundStatement>(list.Length);
                for (var j = 0; j < i; j++) b.Add(list[j]);
            }
            b?.Add(n);
        }
        return b?.MoveToImmutable() ?? list;
    }

    protected virtual BoundNode VisitBad(BoundBadExpression n) => n;
    protected virtual BoundNode VisitLiteral(BoundLiteral n) => n;
    protected virtual BoundNode VisitLocal(BoundLocal n) => n;
    protected virtual BoundNode VisitParameter(BoundParameter n) => n;
    protected virtual BoundNode VisitThis(BoundThis n) => n;

    protected virtual BoundNode VisitFieldAccess(BoundFieldAccess n)
    {
        var r = VisitExprOpt(n.Receiver);
        return ReferenceEquals(r, n.Receiver) ? n : new BoundFieldAccess(r, n.Field) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitPropertyAccess(BoundPropertyAccess n)
    {
        var r = VisitExprOpt(n.Receiver);
        var a = VisitExprs(n.Arguments);
        return ReferenceEquals(r, n.Receiver) && a == n.Arguments ? n : new BoundPropertyAccess(r, n.Property, a) { Syntax = n.Syntax, IsBaseAccess = n.IsBaseAccess };
    }

    protected virtual BoundNode VisitCall(BoundCall n)
    {
        var r = VisitExprOpt(n.Receiver);
        var a = VisitExprs(n.Arguments);
        return ReferenceEquals(r, n.Receiver) && a == n.Arguments ? n : new BoundCall(r, n.Method, a, n.Type) { Syntax = n.Syntax, IsBaseCall = n.IsBaseCall };
    }

    protected virtual BoundNode VisitObjectCreation(BoundObjectCreation n)
    {
        var a = VisitExprs(n.Arguments);
        var changed = a != n.Arguments;
        var inits = n.Initializers;
        if (!inits.IsDefaultOrEmpty)
        {
            var b = ImmutableArray.CreateBuilder<BoundMemberInit>(inits.Length);
            foreach (var i in inits)
            {
                var v = VisitExpr(i.Value);
                if (!ReferenceEquals(v, i.Value)) { changed = true; b.Add(new BoundMemberInit(i.Member, v)); }
                else b.Add(i);
            }
            inits = b.MoveToImmutable();
        }
        return changed ? new BoundObjectCreation(n.Constructor, n.Type!, a, inits) { Syntax = n.Syntax } : n;
    }

    protected virtual BoundNode VisitArrayCreation(BoundArrayCreation n)
    {
        var l = VisitExprOpt(n.Length);
        var i = VisitExprs(n.Initializers);
        return ReferenceEquals(l, n.Length) && i == n.Initializers ? n : new BoundArrayCreation(n.Type, l, i) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitArrayAccess(BoundArrayAccess n)
    {
        var a = VisitExpr(n.Array);
        var i = VisitExpr(n.Index);
        return ReferenceEquals(a, n.Array) && ReferenceEquals(i, n.Index) ? n : new BoundArrayAccess(a, i, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitConversion(BoundConversion n)
    {
        var o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundConversion(o, n.Kind, n.Type!, n.Method) { Syntax = n.Syntax, CaseIndex = n.CaseIndex, ConstantFolded = n.ConstantFolded };
    }

    protected virtual BoundNode VisitUnary(BoundUnary n)
    {
        var o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundUnary(n.Kind, o, n.Type!, n.Method) { Syntax = n.Syntax, Folded = n.Folded };
    }

    protected virtual BoundNode VisitBinary(BoundBinary n)
    {
        var l = VisitExpr(n.Left);
        var r = VisitExpr(n.Right);
        return ReferenceEquals(l, n.Left) && ReferenceEquals(r, n.Right) ? n : new BoundBinary(n.Kind, l, r, n.Type!, n.OperandType, n.Method) { Syntax = n.Syntax, Folded = n.Folded };
    }

    protected virtual BoundNode VisitLogical(BoundLogicalBinary n)
    {
        var l = VisitExpr(n.Left);
        var r = VisitExpr(n.Right);
        return ReferenceEquals(l, n.Left) && ReferenceEquals(r, n.Right) ? n : new BoundLogicalBinary(n.IsAnd, l, r, n.Type!) { Syntax = n.Syntax, Folded = n.Folded };
    }

    protected virtual BoundNode VisitAssignment(BoundAssignment n)
    {
        var t = VisitExpr(n.Target);
        var v = VisitExpr(n.Value);
        return ReferenceEquals(t, n.Target) && ReferenceEquals(v, n.Value) ? n : new BoundAssignment(t, v) { Syntax = n.Syntax, IsDiscarded = n.IsDiscarded };
    }

    protected virtual BoundNode VisitConditional(BoundConditional n)
    {
        var c = VisitExpr(n.Condition);
        var a = VisitExpr(n.WhenTrue);
        var b = VisitExpr(n.WhenFalse);
        return ReferenceEquals(c, n.Condition) && ReferenceEquals(a, n.WhenTrue) && ReferenceEquals(b, n.WhenFalse) ? n : new BoundConditional(c, a, b, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitCoalesce(BoundCoalesce n)
    {
        var l = VisitExpr(n.Left);
        var r = VisitExpr(n.Right);
        return ReferenceEquals(l, n.Left) && ReferenceEquals(r, n.Right) ? n : new BoundCoalesce(l, r, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitConditionalReceiver(BoundConditionalReceiver n) => n;

    protected virtual BoundNode VisitConditionalAccess(BoundConditionalAccess n)
    {
        var r = VisitExpr(n.Receiver);
        var w = VisitExpr(n.WhenNotNull);
        return ReferenceEquals(r, n.Receiver) && ReferenceEquals(w, n.WhenNotNull) ? n : new BoundConditionalAccess(r, n.Placeholder, w, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitTypeTest(BoundTypeTest n)
    {
        var o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundTypeTest(o, n.Target, n.Type!, n.Kind, n.CaseIndex) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitLambda(BoundLambda n)
    {
        var b = (BoundBlock)Visit(n.Body)!;
        return ReferenceEquals(b, n.Body) ? n : new BoundLambda(n.Symbol, b, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitDelegateCreation(BoundDelegateCreation n)
    {
        var r = VisitExprOpt(n.Receiver);
        return ReferenceEquals(r, n.Receiver) ? n : new BoundDelegateCreation(r, n.Method, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitSequence(BoundSequence n)
    {
        var s = VisitStmts(n.SideEffects);
        var v = VisitExpr(n.Value);
        return s == n.SideEffects && ReferenceEquals(v, n.Value) ? n : new BoundSequence(n.Locals, s, v) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitIsNull(BoundIsNull n)
    {
        var o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundIsNull(o, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitAddressOf(BoundAddressOf n)
    {
        var o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundAddressOf(o) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitAwait(BoundAwait n)
    {
        var o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundAwait(o, n.Info, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitThrowExpression(BoundThrowExpression n)
    {
        var o = VisitExpr(n.Operand);
        return ReferenceEquals(o, n.Operand) ? n : new BoundThrowExpression(o, n.Type!) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitBlock(BoundBlock n)
    {
        var s = VisitStmts(n.Statements);
        return s == n.Statements ? n : new BoundBlock(n.Locals, s) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitStatementList(BoundStatementList n)
    {
        var s = VisitStmts(n.Statements);
        return s == n.Statements ? n : new BoundStatementList(s) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitExpressionStatement(BoundExpressionStatement n)
    {
        var e = VisitExpr(n.Expression);
        return ReferenceEquals(e, n.Expression) ? n : new BoundExpressionStatement(e) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitLocalDeclaration(BoundLocalDeclaration n)
    {
        var i = VisitExprOpt(n.Initializer);
        return ReferenceEquals(i, n.Initializer) ? n : new BoundLocalDeclaration(n.Local, i) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitIf(BoundIf n)
    {
        var c = VisitExpr(n.Condition);
        var a = VisitStmt(n.Consequence);
        var b = VisitStmtOpt(n.Alternative);
        return ReferenceEquals(c, n.Condition) && ReferenceEquals(a, n.Consequence) && ReferenceEquals(b, n.Alternative) ? n : new BoundIf(c, a, b) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitWhile(BoundWhile n)
    {
        var c = VisitExpr(n.Condition);
        var b = VisitStmt(n.Body);
        return ReferenceEquals(c, n.Condition) && ReferenceEquals(b, n.Body) ? n : new BoundWhile(c, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitDoWhile(BoundDoWhile n)
    {
        var c = VisitExpr(n.Condition);
        var b = VisitStmt(n.Body);
        return ReferenceEquals(c, n.Condition) && ReferenceEquals(b, n.Body) ? n : new BoundDoWhile(c, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitFor(BoundFor n)
    {
        var i = VisitStmtOpt(n.Initializer);
        var c = VisitExprOpt(n.Condition);
        var inc = VisitStmtOpt(n.Increment);
        var b = VisitStmt(n.Body);
        return ReferenceEquals(i, n.Initializer) && ReferenceEquals(c, n.Condition) && ReferenceEquals(inc, n.Increment) && ReferenceEquals(b, n.Body)
            ? n : new BoundFor(n.Locals, i, c, inc, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitConditionalGoto(BoundConditionalGoto n)
    {
        var c = VisitExpr(n.Condition);
        return ReferenceEquals(c, n.Condition) ? n : new BoundConditionalGoto(c, n.Label, n.JumpIfTrue) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitReturn(BoundReturn n)
    {
        var e = VisitExprOpt(n.Expression);
        return ReferenceEquals(e, n.Expression) ? n : new BoundReturn(e) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitThrow(BoundThrow n)
    {
        var e = VisitExprOpt(n.Expression);
        return ReferenceEquals(e, n.Expression) ? n : new BoundThrow(e) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitTry(BoundTry n)
    {
        var body = (BoundBlock)Visit(n.Body)!;
        var changed = !ReferenceEquals(body, n.Body);
        var catches = ImmutableArray.CreateBuilder<BoundCatch>(n.Catches.Length);
        foreach (var c in n.Catches)
        {
            var cb = (BoundBlock)Visit(c.Body)!;
            if (!ReferenceEquals(cb, c.Body)) { changed = true; catches.Add(new BoundCatch(c.Local, c.ExceptionType, cb)); }
            else catches.Add(c);
        }
        var fin = n.Finally == null ? null : (BoundBlock)Visit(n.Finally)!;
        if (!ReferenceEquals(fin, n.Finally)) changed = true;
        return changed ? new BoundTry(body, catches.MoveToImmutable(), fin) { Syntax = n.Syntax } : n;
    }

    protected virtual BoundNode VisitYieldReturn(BoundYieldReturn n)
    {
        var e = VisitExpr(n.Expression);
        return ReferenceEquals(e, n.Expression) ? n : new BoundYieldReturn(e) { Syntax = n.Syntax };
    }

    protected virtual BoundNode VisitSequencePoint(BoundSequencePoint n)
    {
        var s = VisitStmt(n.Statement);
        return ReferenceEquals(s, n.Statement) ? n : new BoundSequencePoint(s, n.SyntaxNodeForSpan) { Syntax = n.Syntax };
    }
}
