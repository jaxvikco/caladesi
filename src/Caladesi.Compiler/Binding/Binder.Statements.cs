using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    private readonly Dictionary<BoundStatement, bool> _flow = new(ReferenceEqualityComparer.Instance);
    private string? _pendingLabel;
    private bool _inCatch;

    // ------------------------------------------------------------------ statement lists / blocks

    internal ImmutableArray<BoundStatement> BindStatementsInNewScope(SyntaxList<StatementSyntax> statements, out ImmutableArray<LocalSymbol> locals)
    {
        Scope scope = PushScope(ScopeKind.Block);
        ImmutableArray<BoundStatement> result = BindStatementList(statements);
        locals = [.. scope.Locals];
        PopScope();
        return result;
    }

    private ImmutableArray<BoundStatement> BindStatementList(IReadOnlyList<StatementSyntax> statements, int start = 0)
    {
        ImmutableArray<BoundStatement>.Builder result = ImmutableArray.CreateBuilder<BoundStatement>();
        // hoist nested function declarations of this block
        HoistLocalFunctions(statements, start, result);
        for (int i = start; i < statements.Count; i++)
        {
            StatementSyntax s = statements[i];
            if (s is FunctionDeclarationSyntax)
            {
                continue; // already bound by hoisting
            }

            if (s is VariableStatementSyntax { Declaration.IsUsing: true } us)
            {
                result.Add(BindUsing(us, statements, i + 1));
                return result.ToImmutable();
            }
            BoundStatement bound = BindStatement(s);
            result.Add(bound);
        }
        return result.ToImmutable();
    }

    private void HoistLocalFunctions(IReadOnlyList<StatementSyntax> statements, int start, ImmutableArray<BoundStatement>.Builder result)
    {
        List<(FunctionDeclarationSyntax Syntax, LocalSymbol Local)> funcs = [];
        for (int i = start; i < statements.Count; i++)
        {
            if (statements[i] is not FunctionDeclarationSyntax f || f.Identifier.IsMissing)
            {
                continue;
            }

            if (f.Body == null) { Report(Descriptors.FunctionNeedsBody, f.Identifier, f.Identifier.Text); continue; }

            TypeSymbol? delegateType = TryGetLocalFunctionType(f, out _);
            LocalSymbol local = new(f.Identifier.Text, delegateType ?? ErrorType, true, LocalOwner, f);
            DeclareSymbol(f.Identifier.Text, local, f.Identifier);
            funcs.Add((f, local));
        }
        // bind bodies after all names are declared so the functions can reference each other
        foreach ((FunctionDeclarationSyntax? f, LocalSymbol? local) in funcs)
        {
            BoundLambda? lambda = BindLocalFunctionBody(f, local);
            if (lambda != null)
            {
                if (local.Type.IsErrorType && lambda.Type != null)
                {
                    local.Type = lambda.Type;
                }

                result.Add(new BoundLocalDeclaration(local, lambda) { Syntax = f });
            }
        }
    }

    private TypeSymbol? TryGetLocalFunctionType(FunctionDeclarationSyntax f, out bool fullyAnnotated)
    {
        fullyAnnotated = false;
        if (f.TypeParameters != null) { Report(Descriptors.FeatureNotSupported, f.TypeParameters, "Generic nested functions"); return null; }
        ImmutableArray<TypeSymbol>.Builder ptypes = ImmutableArray.CreateBuilder<TypeSymbol>();
        foreach (ParameterSyntax p in f.Parameters.Parameters)
        {
            if (p.TypeAnnotation == null)
            {
                return null;
            }

            TypeSymbol t = BindType(p.TypeAnnotation.Type);
            if (p.QuestionToken != null)
            {
                t = MakeNullable(t, p);
            }

            ptypes.Add(t);
        }
        if (f.ReturnType == null)
        {
            return null;
        }

        _ = new ModifierSet(f.Modifiers).IsAsync;
        TypeSymbol ret = BindType(f.ReturnType.Type, allowVoid: true);
        fullyAnnotated = true;
        return MakeDelegateType(ptypes.ToImmutable(), ret, f);
    }

    private BoundLambda? BindLocalFunctionBody(FunctionDeclarationSyntax f, LocalSymbol local)
    {
        ModifierSet mods = new(f.Modifiers);
        ImmutableArray<string> names = [.. f.Parameters.Parameters.Select(p => p.Identifier?.Text ?? "_")];
        ImmutableArray<TypeSymbol>.Builder ptypes = ImmutableArray.CreateBuilder<TypeSymbol>();
        foreach (ParameterSyntax p in f.Parameters.Parameters)
        {
            TypeSymbol t;
            if (p.TypeAnnotation != null)
            {
                t = BindType(p.TypeAnnotation.Type);
            }
            else if (p.Initializer != null)
            {
                t = InferVariableType(p.Initializer.Value);
            }
            else { Report(Descriptors.ParameterTypeRequired, p, p.Identifier?.Text ?? "?"); t = ErrorType; }
            if (p.QuestionToken != null)
            {
                t = MakeNullable(t, p);
            }

            ptypes.Add(t);
        }
        TypeSymbol? delegateReturn = null;
        NamedTypeSymbol? delegateType = null;
        if (!local.Type.IsErrorType && local.Type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } d)
        {
            delegateType = d;
            delegateReturn = d.DelegateInvokeMethod!.ReturnType;
        }
        BoundLambda? lambda = BindLambdaBody(f, names, f.Parameters, ptypes.ToImmutable(), delegateReturn, delegateType, mods.IsAsync, f.AsteriskToken != null, f.Body!);
        return lambda;
    }

    internal BoundStatement BindStatement(StatementSyntax s)
    {
        BoundStatement bound = BindStatementCore(s);
        if (bound.Syntax == null) { }
        return bound;
    }

    private BoundStatement BindStatementCore(StatementSyntax s)
    {
        switch (s)
        {
            case BlockSyntax b:
                {
                    ImmutableDictionary<NarrowPath, TypeSymbol> saved = _narrow;
                    ImmutableArray<BoundStatement> stmts = BindStatementsInNewScope(b.Statements, out ImmutableArray<LocalSymbol> locals);
                    BoundBlock blk = new(locals, stmts) { Syntax = b };
                    _narrow = FilterNarrowingToOuterScope(saved);
                    return blk;
                }
            case EmptyStatementSyntax:
                return new BoundNoOp { Syntax = s };
            case ExpressionStatementSyntax es:
                return BindExpressionStatement(es);
            case VariableStatementSyntax vs:
                return BindVariableStatement(vs);
            case IfStatementSyntax ifs: return BindIf(ifs);
            case WhileStatementSyntax ws: return BindWhile(ws);
            case DoStatementSyntax ds: return BindDo(ds);
            case ForStatementSyntax fs: return BindFor(fs);
            case ForOfStatementSyntax fo: return BindForOf(fo);
            case ForInStatementSyntax fi:
                _ = BindValue(fi.Expression);
                return new BoundNoOp { Syntax = s };
            case ReturnStatementSyntax rs: return BindReturn(rs);
            case ThrowStatementSyntax ts: return BindThrow(ts);
            case BreakStatementSyntax bs: return BindBreak(bs);
            case ContinueStatementSyntax cs: return BindContinue(cs);
            case TryStatementSyntax tr: return BindTry(tr);
            case SwitchStatementSyntax sw: return BindSwitch(sw);
            case LabeledStatementSyntax ls: return BindLabeled(ls);
            case FunctionDeclarationSyntax:
                return new BoundNoOp { Syntax = s };
            case ClassDeclarationSyntax or InterfaceDeclarationSyntax or EnumDeclarationSyntax or TypeAliasDeclarationSyntax or ImportDeclarationSyntax:
                Report(Descriptors.DeclarationNotAllowedHere, s);
                return new BoundNoOp { Syntax = s };
        }
        Report(Descriptors.FeatureNotSupported, s, s.Kind.ToString());
        return new BoundNoOp { Syntax = s };
    }

    private ImmutableDictionary<NarrowPath, TypeSymbol> FilterNarrowingToOuterScope(ImmutableDictionary<NarrowPath, TypeSymbol> _1)
    {
        // narrowings of variables declared in the closed block are meaningless outside; keep the outer ones as they were after the block
        ImmutableDictionary<NarrowPath, TypeSymbol> result = _narrow;
        foreach (NarrowPath? k in result.Keys.ToList())
        {
            if (k.Root is LocalSymbol l && !IsInScope(l))
            {
                result = result.Remove(k);
            }
        }

        return result;
    }

    private bool IsInScope(LocalSymbol l)
    {
        for (Scope? s = _scope; s != null; s = s.Parent)
        {
            if (s.Symbols.TryGetValue(l.Name, out Symbol? x) && ReferenceEquals(x, l))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ expression statements

    private BoundStatement BindExpressionStatement(ExpressionStatementSyntax es)
    {
        ExpressionSyntax expr = es.Expression;
        if (expr is YieldExpressionSyntax y)
        {
            return BindYield(y);
        }

        BoundExpression e = BindStatementExpression(expr);
        return new BoundExpressionStatement(e) { Syntax = es };
    }

    /// <summary>Binds an expression whose value is discarded.</summary>
    internal BoundExpression BindStatementExpression(ExpressionSyntax expr)
    {
        ExpressionSyntax inner = Unparenthesize(expr);
        BoundExpression e;
        switch (inner)
        {
            case AssignmentExpressionSyntax a: e = BindAssignment(a, valueUsed: false); break;
            case PostfixUnaryExpressionSyntax pf: e = BindPostfixUnary(pf, valueUsed: false); break;
            case PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken } pu:
                e = BindIncrementDecrement(pu.Operand, pu.OperatorToken.Kind == SyntaxKind.PlusPlusToken, true, false, pu);
                break;
            default:
                e = BindValue(expr);
                if (e is BoundUnbound)
                {
                    e = ConvertImplicit(e, e.Type ?? ObjectType, expr);
                }
                else if (e.Type == null && e is not BoundBadExpression)
                {
                    ReportNotAValue(e, expr);
                    e = Bad(expr, e);
                }
                break;
        }
        return e;
    }

    private BoundStatement BindYield(YieldExpressionSyntax y)
    {
        if (!_isIteratorContext)
        {
            Report(Descriptors.YieldNotAllowedHere, y);
            if (y.Operand != null)
            {
                _ = BindValue(y.Operand);
            }

            return new BoundNoOp { Syntax = y };
        }
        if (y.Operand == null)
        {
            Report(Descriptors.ExpectedExpression, y);
            return new BoundNoOp { Syntax = y };
        }
        if (y.IsDelegating)
        {
            BoundExpression src = BindValue(y.Operand);
            if (src is BoundBadExpression)
            {
                return new BoundNoOp { Syntax = y };
            }

            TypeSymbol? it = src.Type != null ? GetIterationType(src.Type, _isAsyncContext) : null;
            if (it == null) { Report(Descriptors.NotIterable, y.Operand, src.Type?.ToDisplayString() ?? "?"); return new BoundNoOp { Syntax = y }; }
            _yieldExprs?.Add(new BoundDefaultValue(it));
            TypeSymbol elemType = _yieldType ?? it;
            return BuildForEachLoop(src, it, y, cur =>
            {
                BoundExpression v = ConvertImplicit(new BoundLocal(cur), elemType, y);
                return new BoundYieldReturn(v) { Syntax = y };
            }, isAsync: _isAsyncContext);
        }
        BoundExpression value;
        if (_yieldType != null)
        {
            value = BindToType(y.Operand, _yieldType);
        }
        else
        {
            value = BindRValue(y.Operand);
            _yieldExprs?.Add(value);
        }
        return new BoundYieldReturn(value) { Syntax = y };
    }

    // ------------------------------------------------------------------ variables

    private BoundStatement BindVariableStatement(VariableStatementSyntax vs)
    {
        VariableDeclarationListSyntax decl = vs.Declaration;
        if (!vs.Modifiers.IsEmpty() && vs.Modifiers.Any(m => m.Kind == SyntaxKind.ExportKeyword))
        {
            Report(Descriptors.ModifierNotAllowed, vs.Modifiers[0], vs.Modifiers[0].Text);
        }

        ImmutableArray<BoundStatement>.Builder list = ImmutableArray.CreateBuilder<BoundStatement>();
        foreach (VariableDeclaratorSyntax d in decl.Declarators)
        {
            list.Add(BindDeclarator(d, decl.IsConst, decl));
        }

        return list.Count == 1 ? list[0] : new BoundStatementList(list.ToImmutable()) { Syntax = vs };
    }

    private BoundStatement BindDeclarator(VariableDeclaratorSyntax d, bool isConst, VariableDeclarationListSyntax _1)
    {
        if (d.Pattern != null)
        {
            return BindDestructuringDeclaration(d, isConst);
        }

        if (d.Identifier is not { IsMissing: false } id)
        {
            return new BoundNoOp { Syntax = d };
        }

        string name = id.Text;
        TypeSymbol? declared = d.TypeAnnotation != null ? BindType(d.TypeAnnotation.Type) : null;
        LocalSymbol? local = null;
        if (declared != null)
        {
            local = new LocalSymbol(name, declared, isConst, LocalOwner, d);
            DeclareSymbol(name, local, id);
        }
        BoundExpression? init = null;
        if (d.Initializer != null)
        {
            BoundExpression v = BindValue(d.Initializer.Value, declared);
            if (declared != null)
            {
                BoundExpression cv = ConvertImplicit(v, declared, d.Initializer.Value);
                init = cv;
                AfterAssignmentToNew(local!, v, declared);
            }
            else
            {
                if (v is BoundUnbound u && HasNaturalType(u))
                {
                    v = u.BindToType(null);
                }

                TypeSymbol t = WidenInferredType(v, d.Initializer.Value, report: true);
                init = t.IsErrorType ? Bad(d, v) : ConvertImplicit(v, t, d.Initializer.Value);
                local = new LocalSymbol(name, t, isConst, LocalOwner, d);
                DeclareSymbol(name, local, id);
            }
        }
        else if (declared == null)
        {
            Report(Descriptors.TypeAnnotationRequired, d);
            local = new LocalSymbol(name, ErrorType, isConst, LocalOwner, d);
            DeclareSymbol(name, local, id);
        }
        else if (isConst)
        {
            Report(Descriptors.ConstNeedsInitializer, id);
        }
        if (local != null && isConst && init != null && init.ConstantValue is { } cval && !IsError(local.Type))
        {
            local.ConstantValue = cval;
        }

        return new BoundLocalDeclaration(local!, init) { Syntax = d };
    }

    private void AfterAssignmentToNew(LocalSymbol local, BoundExpression rawValue, TypeSymbol declared)
    {
        NarrowPath path = new([local]);
        _narrow = RemovePathsStartingWith(_narrow, path);
        if (rawValue.Type == null)
        {
            return;
        }

        TypeSymbol vt = rawValue.Type;
        if (declared is NullableTypeSymbol dn && vt is not NullableTypeSymbol && !vt.IsNullType && ClassifyImplicit(null, vt, dn.Underlying).Exists)
        {
            _narrow = _narrow.SetItem(path, vt.Equals(dn.Underlying) ? dn.Underlying : vt);
        }
        else if (declared is NamedTypeSymbol { IsUnionType: true } u && u.UnionCases.Any(c => c.Equals(vt)))
        {
            _narrow = _narrow.SetItem(path, vt);
        }
    }

    // ------------------------------------------------------------------ using

    private BoundStatementList BindUsing(VariableStatementSyntax us, IReadOnlyList<StatementSyntax> all, int nextIndex)
    {
        VariableDeclarationListSyntax decl = us.Declaration;
        bool isAsync = decl.AwaitKeyword != null;
        ImmutableArray<BoundStatement>.Builder inits = ImmutableArray.CreateBuilder<BoundStatement>();
        List<LocalSymbol> locals = [];
        foreach (VariableDeclaratorSyntax d in decl.Declarators)
        {
            BoundStatement st = BindDeclarator(d, true, decl);
            inits.Add(st);
            if (st is BoundLocalDeclaration { Local: var l } && l != null)
            {
                l.IsUsingVariable = true;
                locals.Add(l);
            }
        }
        ImmutableArray<BoundStatement> rest = BindStatementList(all, nextIndex);
        BoundStatement result = new BoundBlock([], rest) { Syntax = us };
        // wrap from the last declared variable outwards
        for (int i = locals.Count - 1; i >= 0; i--)
        {
            LocalSymbol l = locals[i];
            BoundStatement dispose = BuildDispose(l, isAsync, us);
            BoundTry tryStmt = new(AsBlock(result), [], AsBlock(dispose)) { Syntax = us };
            result = tryStmt;
        }
        List<BoundStatement> all2 = inits.ToList();
        all2.Add(result);
        return new BoundStatementList([.. all2]) { Syntax = us };
    }

    private static BoundBlock AsBlock(BoundStatement s)
    {
        return s as BoundBlock ?? new BoundBlock([], [s]) { Syntax = s.Syntax };
    }

    private BoundStatement BuildDispose(LocalSymbol l, bool isAsync, SyntaxNode syntax)
    {
        TypeSymbol t = l.Type;
        string disposableName = isAsync ? "System.IAsyncDisposable" : "System.IDisposable";
        string methodName = isAsync ? "DisposeAsync" : "Dispose";
        NamedTypeSymbol? iface = Compilation.GetWellKnownType(disposableName);
        MethodSymbol? method = null;
        BoundExpression recv = new BoundLocal(l) { Syntax = syntax };
        if (t is NamedTypeSymbol nt && iface != null && ImplementsInterface(nt, iface))
        {
            method = iface.GetMembers(methodName).OfType<MethodSymbol>().FirstOrDefault();
            if (t.IsValueType)
            {
                MethodSymbol? direct = nt.GetMembers(methodName).OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0 && !m.IsStatic);
                if (direct != null)
                {
                    method = direct;
                }
            }
        }
        else if (t is TypeParameterSymbol && iface != null)
        {
            method = iface.GetMembers(methodName).OfType<MethodSymbol>().FirstOrDefault();
        }

        if (method == null)
        {
            if (!t.IsErrorType)
            {
                Report(Descriptors.NotDisposable, syntax, t.ToDisplayString(), disposableName);
            }

            return new BoundNoOp { Syntax = syntax };
        }
        BoundExpression call = new BoundCall(recv, method, []) { Syntax = syntax };
        if (isAsync)
        {
            AwaitInfo? info = GetAwaitInfo(method.ReturnType, syntax);
            if (info == null)
            {
                return new BoundNoOp { Syntax = syntax };
            }

            call = new BoundAwait(call, info, info.GetResult.ReturnType) { Syntax = syntax };
        }
        BoundStatement stmt = new BoundExpressionStatement(call) { Syntax = syntax };
        if (!t.IsValueType)
        {
            BoundBinary notNull = new(BinaryOperatorKind.NotEqual, new BoundLocal(l), new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance), BoolType, ObjectType);
            stmt = new BoundIf(notNull, stmt, null) { Syntax = syntax };
        }
        return stmt;
    }

    // ------------------------------------------------------------------ if / loops

    private BoundIf BindIf(IfStatementSyntax s)
    {
        BoundExpression cond = ConvertToBool(BindValue(s.Condition), s.Condition);
        (Narrowings tN, Narrowings fN) = GetNarrowing(cond);
        ImmutableDictionary<NarrowPath, TypeSymbol> saved = _narrow;
        _narrow = ApplyNarrowings(saved, tN);
        BoundStatement then = BindEmbedded(s.Then);
        ImmutableDictionary<NarrowPath, TypeSymbol> thenEnd = _narrow;
        _narrow = ApplyNarrowings(saved, fN);
        BoundStatement? els = null;
        if (s.Else != null)
        {
            els = BindEmbedded(s.Else);
        }

        ImmutableDictionary<NarrowPath, TypeSymbol> elseEnd = _narrow;

        bool thenCompletes = CanCompleteNormally(then);
        bool elseCompletes = els == null || CanCompleteNormally(els);
        _narrow = !thenCompletes && elseCompletes
            ? elseEnd
            : thenCompletes && !elseCompletes ? thenEnd : thenCompletes && elseCompletes ? MergeNarrowing(thenEnd, elseEnd) : saved;

        BoundIf node = new(cond, then, els) { Syntax = s };
        if (!thenCompletes && !elseCompletes && els != null)
        {
            _flow[node] = false;
        }

        return node;
    }

    private BoundStatement BindEmbedded(StatementSyntax s)
    {
        if (s is BlockSyntax)
        {
            return BindStatement(s);
        }
        // a single embedded statement gets its own scope
        _ = PushScope(ScopeKind.Block);
        BoundStatement st = BindStatement(s);
        ImmutableArray<LocalSymbol> locals = [.. _scope.Locals];
        PopScope();
        return locals.IsEmpty ? st : new BoundBlock(locals, [st]) { Syntax = s };
    }

    private static ImmutableDictionary<NarrowPath, TypeSymbol> MergeNarrowing(ImmutableDictionary<NarrowPath, TypeSymbol> a, ImmutableDictionary<NarrowPath, TypeSymbol> b)
    {
        ImmutableDictionary<NarrowPath, TypeSymbol> r = [];
        foreach (KeyValuePair<NarrowPath, TypeSymbol> kv in a)
        {
            if (b.TryGetValue(kv.Key, out TypeSymbol? t) && t.Equals(kv.Value))
            {
                r = r.Add(kv.Key, kv.Value);
            }
        }

        return r;
    }

    private (LabelSymbol brk, LabelSymbol cont) PushLoopScope()
    {
        Scope scope = PushScope(ScopeKind.Loop);
        scope.BreakLabel = new LabelSymbol("break");
        scope.ContinueLabel = new LabelSymbol("continue");
        scope.LabelName = _pendingLabel;
        _pendingLabel = null;
        return (scope.BreakLabel, scope.ContinueLabel);
    }

    private BoundWhile BindWhile(WhileStatementSyntax ws)
    {
        InvalidateAssignedIn(ws);
        BoundExpression cond = ConvertToBool(BindValue(ws.Condition), ws.Condition);
        (Narrowings tN, Narrowings fN) = GetNarrowing(cond);
        ImmutableDictionary<NarrowPath, TypeSymbol> saved = _narrow;
        (LabelSymbol? brk, LabelSymbol? cont) = PushLoopScope();
        _narrow = ApplyNarrowings(saved, tN);
        BoundStatement body = BindEmbedded(ws.Body);
        bool breakUsed = _scope.BreakUsed;
        PopScope();
        _narrow = saved;
        InvalidateAssignedIn(ws);
        BoundWhile node = new(cond, body, brk, cont) { Syntax = ws };
        if (cond.ConstantValue is { Value: true } && !breakUsed)
        {
            _flow[node] = false;
        }
        else
        {
            _narrow = ApplyNarrowings(_narrow, fN);
        }

        return node;
    }

    private BoundDoWhile BindDo(DoStatementSyntax ds)
    {
        InvalidateAssignedIn(ds);
        ImmutableDictionary<NarrowPath, TypeSymbol> saved = _narrow;
        (LabelSymbol? brk, LabelSymbol? cont) = PushLoopScope();
        BoundStatement body = BindEmbedded(ds.Body);
        bool breakUsed = _scope.BreakUsed;
        PopScope();
        InvalidateAssignedIn(ds);
        BoundExpression cond = ConvertToBool(BindValue(ds.Condition), ds.Condition);
        BoundDoWhile node = new(cond, body, brk, cont) { Syntax = ds };
        if (cond.ConstantValue is { Value: true } && !breakUsed)
        {
            _flow[node] = false;
        }

        _ = saved;
        return node;
    }

    private BoundFor BindFor(ForStatementSyntax fs)
    {
        Scope outer = PushScope(ScopeKind.Block);
        BoundStatement? init = null;
        switch (fs.Initializer)
        {
            case VariableDeclarationListSyntax vdl:
                {
                    ImmutableArray<BoundStatement>.Builder list = ImmutableArray.CreateBuilder<BoundStatement>();
                    foreach (VariableDeclaratorSyntax d in vdl.Declarators)
                    {
                        list.Add(BindDeclarator(d, vdl.IsConst, vdl));
                    }

                    init = list.Count == 1 ? list[0] : new BoundStatementList(list.ToImmutable()) { Syntax = vdl };
                    break;
                }
            case ExpressionSyntax e:
                init = new BoundExpressionStatement(BindStatementExpression(e)) { Syntax = e };
                break;
        }
        InvalidateAssignedIn(fs);
        BoundExpression? cond = fs.Condition != null ? ConvertToBool(BindValue(fs.Condition), fs.Condition) : null;
        (Narrowings tN, Narrowings fN) = cond != null ? GetNarrowing(cond) : (Narrowings.Empty, Narrowings.Empty);
        ImmutableDictionary<NarrowPath, TypeSymbol> saved = _narrow;
        (LabelSymbol? brk, LabelSymbol? cont) = PushLoopScope();
        _narrow = ApplyNarrowings(saved, tN);
        BoundStatement body = BindEmbedded(fs.Body);
        bool breakUsed = _scope.BreakUsed;
        PopScope();
        BoundStatement? incr = fs.Incrementor != null ? new BoundExpressionStatement(BindStatementExpression(fs.Incrementor)) { Syntax = fs.Incrementor } : null;
        ImmutableArray<LocalSymbol> locals = [.. outer.Locals];
        PopScope();
        _narrow = saved;
        InvalidateAssignedIn(fs);
        BoundFor node = new(locals, init, cond, incr, body, brk, cont) { Syntax = fs };
        if ((cond == null || cond.ConstantValue is { Value: true }) && !breakUsed)
        {
            _flow[node] = false;
        }
        else
        {
            _narrow = ApplyNarrowings(_narrow, fN);
        }

        return node;
    }

    private BoundStatement BindForOf(ForOfStatementSyntax fo)
    {
        bool isAsync = fo.AwaitKeyword != null;
        if (isAsync && !_isAsyncContext)
        {
            Report(Descriptors.AwaitOutsideAsync, fo.AwaitKeyword!.Span);
        }
        Scope outerScope = PushScope(ScopeKind.Block);
        BoundExpression collection = BindValue(fo.Expression);
        if (collection is BoundBadExpression)
        {
            PopScope();
            return new BoundNoOp { Syntax = fo };
        }
        if (collection is BoundUnbound u)
        {
            collection = u.BindToType(null);
        }

        TypeSymbol? ct = collection.Type;
        if (ct == null) { ReportNotAValue(collection, fo.Expression); PopScope(); return new BoundNoOp { Syntax = fo }; }
        if (ct is NullableTypeSymbol)
        {
            Report(Descriptors.PossiblyNull, fo.Expression, fo.Expression.ToFullString().Trim());
            collection = new BoundConversion(collection, ConversionKind.FromNullable, ct.StripNullable()) { Syntax = fo.Expression };
            ct = ct.StripNullable();
        }
        TypeSymbol? elementType = GetIterationType(ct, isAsync);
        if (elementType == null)
        {
            Report(Descriptors.NotIterable, fo.Expression, ct.ToDisplayString());
            PopScope();
            return new BoundNoOp { Syntax = fo };
        }
        InvalidateAssignedIn(fo);
        ImmutableDictionary<NarrowPath, TypeSymbol> saved = _narrow;
        (LabelSymbol? brk, LabelSymbol? cont) = PushLoopScope();

        BoundStatement loop = BuildForEachLoop(collection, elementType, fo, cur =>
        {
            ImmutableArray<BoundStatement>.Builder stmts = ImmutableArray.CreateBuilder<BoundStatement>();
            ImmutableArray<LocalSymbol>.Builder locals = ImmutableArray.CreateBuilder<LocalSymbol>();
            // bind the loop variable(s)
            Scope bodyScope = PushScope(ScopeKind.Block);
            switch (fo.Left)
            {
                case VariableDeclarationListSyntax vdl when vdl.Declarators.Count == 1:
                    {
                        VariableDeclaratorSyntax d = vdl.Declarators[0];
                        bool isConst = vdl.IsConst;
                        if (d.Pattern != null)
                        {
                            stmts.Add(BindDestructuringFrom(d.Pattern, new BoundLocal(cur) { Syntax = d }, cur.Type, isConst, d));
                        }
                        else if (d.Identifier is { IsMissing: false } id)
                        {
                            TypeSymbol varType = elementType;
                            BoundExpression init = new BoundLocal(cur) { Syntax = d };
                            if (d.TypeAnnotation != null)
                            {
                                varType = BindType(d.TypeAnnotation.Type);
                                init = ConvertImplicit(init, varType, d);
                            }
                            LocalSymbol lv = new(id.Text, varType, isConst, LocalOwner, d);
                            DeclareSymbol(id.Text, lv, id);
                            stmts.Add(new BoundLocalDeclaration(lv, init) { Syntax = d });
                        }
                        break;
                    }
                case ExpressionSyntax target:
                    {
                        BoundExpression t = BindAssignmentTarget(target);
                        if (t is not BoundBadExpression)
                        {
                            BoundExpression v = ConvertImplicit(new BoundLocal(cur), t.Type!, target);
                            stmts.Add(new BoundExpressionStatement(new BoundAssignment(t, v) { Syntax = fo, IsDiscarded = true }) { Syntax = fo });
                        }
                        break;
                    }
                default:
                    Report(Descriptors.FeatureNotSupported, fo.Left, "Multiple declarations in for-of");
                    break;
            }
            BoundStatement body = BindEmbedded(fo.Body);
            stmts.Add(body);
            locals.AddRange(bodyScope.Locals);
            PopScope();
            return new BoundBlock(locals.ToImmutable(), stmts.ToImmutable()) { Syntax = fo };
        }, brk, cont, isAsync);

        bool breakUsed = _scope.BreakUsed;
        PopScope(); // loop scope
        ImmutableArray<LocalSymbol> outerLocals = [.. outerScope.Locals];
        PopScope(); // outer
        _narrow = saved;
        InvalidateAssignedIn(fo);
        _ = breakUsed;
        return outerLocals.IsEmpty ? loop : new BoundBlock(outerLocals, [loop]) { Syntax = fo };
    }

    // ------------------------------------------------------------------ foreach lowering

    /// <summary>
    /// Builds a loop over <paramref name="collection"/>: arrays and strings use an index loop, everything else the
    /// GetEnumerator/MoveNext/Current/Dispose pattern. <paramref name="makeBody"/> receives the local holding the current element.
    /// </summary>
    internal BoundStatement BuildForEachLoop(BoundExpression collection, TypeSymbol elementType, SyntaxNode syntax, Func<LocalSymbol, BoundStatement> makeBody,
        LabelSymbol? breakLabel = null, LabelSymbol? continueLabel = null, bool isAsync = false)
    {
        breakLabel ??= new LabelSymbol("break");
        continueLabel ??= new LabelSymbol("continue");
        TypeSymbol ct = collection.Type!.StripNullable();
        Symbol? owner = LocalOwner;
        LocalSymbol cur = new("<current>", elementType, false, owner) { IsSynthesized = true };

        if (!isAsync && (ct is ArrayTypeSymbol { Rank: 1 } || ct.SpecialType == SpecialType.String))
        {
            LocalSymbol colLocal = new("<col>", ct, false, owner) { IsSynthesized = true };
            LocalSymbol idx = new("<i>", IntType, false, owner) { IsSynthesized = true };
            BoundExpression lengthExpr;
            BoundExpression elemExpr;
            if (ct is ArrayTypeSymbol)
            {
                PropertySymbol lenProp = Compilation.GetSpecialType(SpecialType.Array).GetMembers("Length").OfType<PropertySymbol>().First();
                lengthExpr = new BoundPropertyAccess(new BoundLocal(colLocal), lenProp, []) { Syntax = syntax };
                elemExpr = new BoundArrayAccess(new BoundLocal(colLocal), new BoundLocal(idx), elementType) { Syntax = syntax };
            }
            else
            {
                NamedTypeSymbol s = Special(SpecialType.String);
                PropertySymbol lenProp = s.GetMembers("Length").OfType<PropertySymbol>().First();
                PropertySymbol chars = s.GetMembers().OfType<PropertySymbol>().First(p => p.IsIndexer);
                lengthExpr = new BoundPropertyAccess(new BoundLocal(colLocal), lenProp, []) { Syntax = syntax };
                elemExpr = new BoundPropertyAccess(new BoundLocal(colLocal), chars, [new BoundLocal(idx)]) { Syntax = syntax };
            }
            BoundBinary cond = new(BinaryOperatorKind.LessThan, new BoundLocal(idx), lengthExpr, BoolType, IntType) { Syntax = syntax };
            BoundExpressionStatement incr = new(new BoundAssignment(new BoundLocal(idx),
                new BoundBinary(BinaryOperatorKind.Add, new BoundLocal(idx), new BoundLiteral(new ConstantValue(1), IntType), IntType, IntType))
            { IsDiscarded = true })
            { Syntax = syntax };
            BoundBlock body = new([cur], [new BoundLocalDeclaration(cur, elemExpr) { Syntax = syntax }, makeBody(cur)]) { Syntax = syntax };
            BoundFor forLoop = new([],
                new BoundLocalDeclaration(idx, new BoundLiteral(new ConstantValue(0), IntType)) { Syntax = syntax }, cond, incr, body, breakLabel, continueLabel)
            { Syntax = syntax };
            return new BoundBlock([colLocal, idx], [new BoundLocalDeclaration(colLocal, collection) { Syntax = syntax }, forLoop]) { Syntax = syntax };
        }

        // enumerator pattern
        string getEnumName = isAsync ? "GetAsyncEnumerator" : "GetEnumerator";
        MethodSymbol? getEnum = LookupMembers(ct, getEnumName).OfType<MethodSymbol>().FirstOrDefault(m => !m.IsStatic && m.Parameters.All(p => p.IsOptional));
        BoundExpression recv = collection;
        if (getEnum == null)
        {
            string ifaceName = isAsync ? "System.Collections.Generic.IAsyncEnumerable`1" : "System.Collections.Generic.IEnumerable`1";
            NamedTypeSymbol? iface = ct.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.ToMetadataString() == ifaceName)
                        ?? (ct is NamedTypeSymbol n && n.OriginalDefinition.ToMetadataString() == ifaceName ? n : null);
            if (iface == null && !isAsync)
            {
                iface = (NamedTypeSymbol?)Special(SpecialType.IEnumerable);
            }

            getEnum = iface?.GetMembers(getEnumName).OfType<MethodSymbol>().FirstOrDefault();
            if (getEnum != null && !ct.Equals(iface))
            {
                recv = new BoundConversion(collection, ct.IsValueType ? ConversionKind.Boxing : ConversionKind.ImplicitReference, iface!) { Syntax = syntax };
            }
        }
        if (getEnum == null)
        {
            Report(Descriptors.NotIterable, syntax, ct.ToDisplayString());
            return new BoundNoOp { Syntax = syntax };
        }
        TypeSymbol enumType = getEnum.ReturnType;
        LocalSymbol enumLocal = new("<enum>", enumType, false, owner) { IsSynthesized = true };
        MethodSymbol? moveNext = LookupMembers(enumType, isAsync ? "MoveNextAsync" : "MoveNext").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0);
        PropertySymbol? current = LookupMembers(enumType, "Current").OfType<PropertySymbol>().FirstOrDefault();
        if (moveNext == null || current == null)
        {
            // interface-typed enumerators: IEnumerator<T> / IEnumerator
            List<NamedTypeSymbol> ie = [.. enumType.AllInterfaces];
            if (enumType is NamedTypeSymbol nt2)
            {
                ie.Add(nt2);
            }
            NamedTypeSymbol nonGeneric = Special(SpecialType.IEnumerator);
            moveNext ??= nonGeneric.GetMembers("MoveNext").OfType<MethodSymbol>().FirstOrDefault();
            current ??= ie.Select(i => i.GetMembers("Current").OfType<PropertySymbol>().FirstOrDefault()).FirstOrDefault(p => p != null);
        }
        if (moveNext == null || current == null)
        {
            Report(Descriptors.NotIterable, syntax, ct.ToDisplayString());
            return new BoundNoOp { Syntax = syntax };
        }
        BoundExpression moveCall = new BoundCall(new BoundLocal(enumLocal), moveNext, []) { Syntax = syntax };
        if (isAsync)
        {
            AwaitInfo? info = GetAwaitInfo(moveNext.ReturnType, syntax);
            if (info == null)
            {
                return new BoundNoOp { Syntax = syntax };
            }

            moveCall = new BoundAwait(moveCall, info, info.GetResult.ReturnType) { Syntax = syntax };
        }
        BoundExpression currentValue = new BoundPropertyAccess(new BoundLocal(enumLocal), current, []) { Syntax = syntax };
        if (!current.Type.Equals(elementType))
        {
            currentValue = ConvertImplicit(currentValue, elementType, syntax, report: false);
        }

        BoundBlock loopBody = new([cur], [new BoundLocalDeclaration(cur, currentValue) { Syntax = syntax }, makeBody(cur)]) { Syntax = syntax };
        BoundWhile whileLoop = new(moveCall, loopBody, breakLabel, continueLabel) { Syntax = syntax };

        // disposal
        BoundStatement? dispose = null;
        string disposableName = isAsync ? "System.IAsyncDisposable" : "System.IDisposable";
        NamedTypeSymbol? disposableIface = Compilation.GetWellKnownType(disposableName);
        string disposeName = isAsync ? "DisposeAsync" : "Dispose";
        MethodSymbol? disposeM = null;
        BoundExpression disposeRecv = new BoundLocal(enumLocal);
        MethodSymbol? directDispose = LookupMembers(enumType, disposeName).OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0 && !m.IsStatic);
        if (directDispose != null)
        {
            disposeM = directDispose;
        }
        else if (disposableIface != null && ((enumType is NamedTypeSymbol et && ImplementsInterface(et, disposableIface)) || enumType.Equals(disposableIface)))
        {
            disposeM = disposableIface.GetMembers(disposeName).OfType<MethodSymbol>().FirstOrDefault();
        }

        if (disposeM != null)
        {
            BoundExpression call = new BoundCall(disposeRecv, disposeM, []) { Syntax = syntax };
            if (isAsync)
            {
                AwaitInfo? info = GetAwaitInfo(disposeM.ReturnType, syntax);
                if (info != null)
                {
                    call = new BoundAwait(call, info, info.GetResult.ReturnType) { Syntax = syntax };
                }
            }
            BoundStatement st = new BoundExpressionStatement(call) { Syntax = syntax };
            if (!enumType.IsValueType)
            {
                st = new BoundIf(new BoundBinary(BinaryOperatorKind.NotEqual, new BoundLocal(enumLocal), new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance), BoolType, ObjectType), st, null) { Syntax = syntax };
            }

            dispose = st;
        }

        BoundStatement tried = whileLoop;
        if (dispose != null)
        {
            tried = new BoundTry(AsBlock(whileLoop), [], AsBlock(dispose)) { Syntax = syntax };
        }

        BoundLocalDeclaration enumInit = new(enumLocal, new BoundCall(recv, getEnum, [.. getEnum.Parameters.Select(p => DefaultArgument(p, syntax))]) { Syntax = syntax }) { Syntax = syntax };
        return new BoundBlock([enumLocal], [enumInit, tried]) { Syntax = syntax };
    }

    // ------------------------------------------------------------------ return / throw / break / continue

    private BoundStatement BindReturn(ReturnStatementSyntax rs)
    {
        if (_isIteratorContext)
        {
            if (rs.Expression != null)
            {
                Report(Descriptors.ReturnValueInIterator, rs.Expression);
                _ = BindValue(rs.Expression);
            }
            return new BoundYieldBreak { Syntax = rs };
        }
        if (rs.Expression == null)
        {
            _sawBareReturn = true;
            if (_declaredReturn != null && !_declaredReturn.IsVoid && !_declaredReturn.IsErrorType)
            {
                Report(Descriptors.MustReturnValue, rs, _declaredReturn.ToDisplayString());
            }
            return new BoundReturn(null) { Syntax = rs };
        }
        if (_declaredReturn != null && _declaredReturn.IsVoid)
        {
            Report(Descriptors.CannotReturnValue, rs.Expression);
            _ = BindValue(rs.Expression);
            return new BoundReturn(null) { Syntax = rs };
        }
        if (_declaredReturn != null)
        {
            BoundExpression v = BindToType(rs.Expression, _declaredReturn);
            return new BoundReturn(v) { Syntax = rs };
        }
        BoundExpression value = BindRValue(rs.Expression);
        _returnExprs?.Add(value);
        return new BoundReturn(value) { Syntax = rs };
    }

    private BoundThrow BindThrow(ThrowStatementSyntax ts)
    {
        if (ts.Expression == null)
        {
            if (!_inCatch)
            {
                Report(Descriptors.RethrowOutsideCatch, ts);
            }

            return new BoundThrow(null) { Syntax = ts };
        }
        NamedTypeSymbol excType = Compilation.GetSpecialType(SpecialType.Exception);
        BoundExpression e = BindToType(ts.Expression, excType);
        return new BoundThrow(e) { Syntax = ts };
    }

    private BoundStatement BindBreak(BreakStatementSyntax bs)
    {
        for (Scope? s = _scope; s != null; s = s.Parent)
        {
            if (s.Kind is ScopeKind.Lambda or ScopeKind.Method)
            {
                break;
            }

            if (bs.Label != null)
            {
                if (s.LabelName == bs.Label.Text && s.BreakLabel != null) { s.BreakUsed = true; return new BoundGoto(s.BreakLabel) { Syntax = bs }; }
            }
            else if (s.Kind is ScopeKind.Loop or ScopeKind.Switch && s.BreakLabel != null)
            {
                s.BreakUsed = true;
                return new BoundGoto(s.BreakLabel) { Syntax = bs };
            }
        }
        Report(bs.Label != null ? Descriptors.LabelNotFound : Descriptors.BreakOutsideLoop, bs, bs.Label?.Text);
        return new BoundNoOp { Syntax = bs };
    }

    private BoundStatement BindContinue(ContinueStatementSyntax cs)
    {
        for (Scope? s = _scope; s != null; s = s.Parent)
        {
            if (s.Kind is ScopeKind.Lambda or ScopeKind.Method)
            {
                break;
            }

            if (s.Kind == ScopeKind.Loop && s.ContinueLabel != null && (cs.Label == null || s.LabelName == cs.Label.Text))
            {
                return new BoundGoto(s.ContinueLabel) { Syntax = cs };
            }
        }
        Report(cs.Label != null ? Descriptors.LabelNotFound : Descriptors.ContinueOutsideLoop, cs, cs.Label?.Text);
        return new BoundNoOp { Syntax = cs };
    }

    private BoundStatement BindLabeled(LabeledStatementSyntax ls)
    {
        StatementSyntax inner = ls.Statement;
        if (inner is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForOfStatementSyntax)
        {
            _pendingLabel = ls.Label.Text;
            return BindStatement(inner);
        }
        // labeled block: `break label` leaves it
        Scope scope = PushScope(ScopeKind.Block);
        scope.BreakLabel = new LabelSymbol(ls.Label.Text);
        scope.LabelName = ls.Label.Text;
        BoundStatement st = BindStatement(inner);
        ImmutableArray<LocalSymbol> locals = [.. scope.Locals];
        PopScope();
        return new BoundBlock(locals, [st, new BoundLabel(scope.BreakLabel) { Syntax = ls }]) { Syntax = ls };
    }

    // ------------------------------------------------------------------ try

    private BoundTry BindTry(TryStatementSyntax tr)
    {
        ImmutableDictionary<NarrowPath, TypeSymbol> saved = _narrow;
        BoundBlock body = (BoundBlock)BindStatement(tr.Block);
        InvalidateAssignedIn(tr);
        _narrow = saved;
        InvalidateAssignedIn(tr);
        ImmutableArray<BoundCatch>.Builder catches = ImmutableArray.CreateBuilder<BoundCatch>();
        NamedTypeSymbol excType = Compilation.GetSpecialType(SpecialType.Exception);
        foreach (CatchClauseSyntax c in tr.Catches)
        {
            _ = PushScope(ScopeKind.Block);
            TypeSymbol ct = excType;
            if (c.TypeAnnotation != null)
            {
                ct = BindType(c.TypeAnnotation.Type);
                if (!ct.IsErrorType && !(ct.Equals(excType) || IsDerivedFrom(ct, excType)))
                {
                    Report(Descriptors.CatchMustBeException, c.TypeAnnotation.Type, ct.ToDisplayString());
                    ct = ErrorType;
                }
            }
            LocalSymbol? local = null;
            if (c.Identifier is { IsMissing: false } id)
            {
                local = new LocalSymbol(id.Text, ct, false, LocalOwner, c);
                DeclareSymbol(id.Text, local, id);
            }
            bool wasInCatch = _inCatch;
            _inCatch = true;
            BoundBlock cb = (BoundBlock)BindStatement(c.Block);
            _inCatch = wasInCatch;
            ImmutableArray<LocalSymbol> locals = [.. _scope.Locals];
            PopScope();
            catches.Add(new BoundCatch(local, ct, locals.IsEmpty ? cb : new BoundBlock(locals, [cb]) { Syntax = c.Block }));
            _narrow = saved;
            InvalidateAssignedIn(tr);
        }
        BoundBlock? fin = null;
        if (tr.Finally != null)
        {
            fin = (BoundBlock)BindStatement(tr.Finally.Block);
            _narrow = saved;
            InvalidateAssignedIn(tr);
        }
        BoundTry node = new(body, catches.ToImmutable(), fin) { Syntax = tr };
        bool completes = (CanCompleteNormally(body) || catches.Any(c => CanCompleteNormally(c.Body))) && (fin == null || CanCompleteNormally(fin));
        if (!completes)
        {
            _flow[node] = false;
        }

        return node;
    }

    // ------------------------------------------------------------------ flow

    internal bool CanCompleteNormally(BoundStatement s)
    {
        if (_flow.TryGetValue(s, out bool overridden))
        {
            return overridden;
        }

        switch (s)
        {
            case BoundReturn or BoundThrow or BoundYieldBreak or BoundGoto: return false;
            case BoundBlock b:
                foreach (BoundStatement st in b.Statements)
                {
                    if (!CanCompleteNormally(st))
                    {
                        return false;
                    }
                }

                return true;
            case BoundStatementList l:
                foreach (BoundStatement st in l.Statements)
                {
                    if (!CanCompleteNormally(st))
                    {
                        return false;
                    }
                }

                return true;
            case BoundIf i:
                return i.Alternative == null || CanCompleteNormally(i.Consequence) || CanCompleteNormally(i.Alternative);
            case BoundTry t:
                return (CanCompleteNormally(t.Body) || t.Catches.Any(c => CanCompleteNormally(c.Body))) && (t.Finally == null || CanCompleteNormally(t.Finally));
            case BoundExpressionStatement { Expression.Type: { } et } when et.IsNeverType: return false;
            case BoundSequencePoint sp: return CanCompleteNormally(sp.Statement);
        }
        return true;
    }
}

internal static class SyntaxListExtensions
{
    public static bool IsEmpty<T>(this SyntaxList<T> l) where T : SyntaxElement
    {
        return l.Count == 0;
    }
}
