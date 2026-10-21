using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    private void EnterFunction(SourceMethodSymbol m)
    {
        _function = m;
        _isStaticContext = m.IsStatic;
        _isAsyncContext = m.IsAsync;
        _isIteratorContext = m.IsIterator;
        _inConstructor = m.MethodKind == MethodKind.Constructor;
        _inStaticConstructor = m.MethodKind == MethodKind.StaticConstructor;
        _scope = new Scope(null, ScopeKind.Method);
        foreach (var p in m.Parameters) if (p.Name.Length > 0 && !p.Name.StartsWith('<')) DeclareSymbol(p.Name, p, p.DeclaringSyntax);
    }

    private ImmutableArray<BoundStatement> BindParameterPatterns(SourceMethodSymbol m)
    {
        var b = ImmutableArray.CreateBuilder<BoundStatement>();
        foreach (var p in m.Parameters)
            if (p.DeclaringSyntax is ParameterSyntax { Pattern: { } pat } ps)
                b.Add(BindDestructuringFrom(pat, new BoundParameter(p) { Syntax = ps }, p.Type, false, ps));
        return b.ToImmutable();
    }

    /// <summary>Binds the body of a source method (including constructors and static initializers).</summary>
    internal BoundBlock? BindMethodBody(SourceMethodSymbol m)
    {
        EnterFunction(m);
        switch (m.MethodKind)
        {
            case MethodKind.Constructor:
                return BindConstructorBody(m);
            case MethodKind.StaticConstructor:
                return BindStaticInitializer(m);
        }
        if (m.BodySyntax == null) return null;
        return BindRegularBody(m);
    }

    private BoundBlock BindRegularBody(SourceMethodSymbol m)
    {
        var body = m.BodySyntax!;
        var annotated = m.ReturnAnnotation != null || m.MethodKind == MethodKind.PropertyGet || m.MethodKind == MethodKind.PropertySet;
        TypeSymbol? declared = null;
        if (annotated)
        {
            var rt = m.ReturnType;
            if (m.IsIterator)
            {
                _yieldType = IteratorElementType(rt, m.IsAsync);
                declared = VoidType;
            }
            else declared = UnwrapTaskReturn(rt, m.IsAsync);
        }
        else
        {
            _returnExprs = new List<BoundExpression>();
            if (m.IsIterator) _yieldExprs = new List<BoundExpression>();
        }
        _declaredReturn = declared;
        var prefix = BindParameterPatterns(m);
        var stmts = BindStatementsInNewScope(body.Statements, out var locals);
        var block = new BoundBlock(locals, prefix.AddRange(stmts)) { Syntax = body };

        if (annotated)
        {
            if (declared != null && !declared.IsVoid && !declared.IsErrorType && !m.IsIterator && CanCompleteNormally(block))
                Report(Descriptors.NotAllPathsReturn, body.CloseBraceToken, m.Name);
        }
        else
        {
            TypeSymbol inner;
            if (m.IsIterator)
            {
                var elem = _yieldExprs!.Count > 0 ? BestCommonType(_yieldExprs, body) : null;
                var en = _c.GetWellKnownType(m.IsAsync ? "System.Collections.Generic.IAsyncEnumerable`1" : "System.Collections.Generic.IEnumerable`1");
                if (elem == null) { Report(Descriptors.CannotInferIteratorType, m.Locations.FirstOrDefault().Span); m.SetInferredReturnType(ErrorType); return block; }
                m.SetInferredReturnType(en!.Construct(elem));
                inner = elem;
                block = (BoundBlock)new YieldConverter(this, elem).Visit(block)!;
            }
            else
            {
                if (_returnExprs!.Count > 0)
                {
                    var common = BestCommonType(_returnExprs, body);
                    if (common == null) { Report(Descriptors.NoBestCommonTypeReturns, body); common = ErrorType; }
                    inner = common;
                    if (_sawBareReturn || CanCompleteNormally(block))
                    {
                        // some paths fall off the end without a value
                        Report(Descriptors.NotAllPathsReturn, body.CloseBraceToken, m.Name);
                    }
                    if (!common.IsErrorType) block = (BoundBlock)new ReturnConverter(this, common).Visit(block)!;
                }
                else inner = VoidType;
                m.SetInferredReturnType(m.IsAsync ? WrapInTask(inner) : inner);
            }
        }
        return block;
    }

    /// <summary>Converts yielded values once the element type of an inferred iterator is known.</summary>
    private sealed class YieldConverter(Binder binder, TypeSymbol target) : BoundTreeRewriter
    {
        protected override BoundNode VisitYieldReturn(BoundYieldReturn n)
        {
            if (n.Expression.Type == null || n.Expression.Type.Equals(target)) return n;
            return new BoundYieldReturn(binder.ConvertImplicit(n.Expression, target, n.Syntax, report: false)) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitLambda(BoundLambda n) => n;
    }

    // ------------------------------------------------------------------ constructors

    private BoundBlock BindConstructorBody(SourceMethodSymbol m)
    {
        var cls = (NamedTypeSymbol)m.ContainerType;
        var stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        _declaredReturn = VoidType;
        var bodySyntax = m.BodySyntax;
        var first = bodySyntax?.Statements.Count > 0 ? bodySyntax.Statements[0] : null;
        var explicitSuper = first is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: SuperExpressionSyntax } };
        var baseType = cls.TypeKind == TypeKind.Struct ? null : cls.BaseType;

        // 1. base constructor call
        BoundStatement? baseCall = null;
        if (m.InheritedBaseConstructor is { } inheritedCtor)
        {
            var at = (SyntaxNode?)(cls as SourceNamedType)?.DeclaringSyntax;
            var forwarded = m.Parameters.Select(p => (BoundExpression)new BoundParameter(p) { Syntax = at }).ToImmutableArray();
            baseCall = new BoundExpressionStatement(new BoundCall(ThisRef(cls, at!), inheritedCtor, forwarded, VoidType) { Syntax = at, IsBaseCall = true }) { Syntax = at };
        }
        else if (baseType != null)
        {
            if (explicitSuper)
            {
                var inv = (InvocationExpressionSyntax)((ExpressionStatementSyntax)first!).Expression;
                var args = BindArguments(inv.Arguments.Arguments);
                baseCall = BuildBaseConstructorCall(baseType, args, inv, explicitCall: true);
            }
            else
            {
                baseCall = BuildBaseConstructorCall(baseType, ImmutableArray<BoundExpression>.Empty, (SyntaxNode?)bodySyntax ?? (m.Declaration ?? (cls as SourceNamedType)?.DeclaringSyntax)!, explicitCall: false);
            }
        }
        else if (explicitSuper)
        {
            Report(Descriptors.SuperCallPosition, first!);
        }
        if (baseCall != null) stmts.Add(baseCall);

        // 2. parameter properties
        if (m.Declaration is ConstructorDeclarationSyntax cd)
        {
            foreach (var ps in cd.Parameters.Parameters)
            {
                var pm = new ModifierSet(ps.Modifiers);
                if (!(pm.IsPublic || pm.IsPrivate || pm.IsProtected || pm.IsInternal || pm.IsReadonly) || ps.Identifier == null) continue;
                var param = m.Parameters.FirstOrDefault(p => p.Name == ps.Identifier.Text);
                if (param == null) continue;
                var member = cls.GetMembers(param.Name).FirstOrDefault(x => x is SourcePropertySymbol or SourceFieldSymbol { IsBackingField: false });
                BoundExpression? target = member switch
                {
                    SourcePropertySymbol p when p.BackingField != null => new BoundFieldAccess(ThisRef(cls, ps), p.BackingField),
                    SourceFieldSymbol f => new BoundFieldAccess(ThisRef(cls, ps), f),
                    _ => null,
                };
                if (target == null) continue;
                stmts.Add(new BoundExpressionStatement(new BoundAssignment(target, new BoundParameter(param) { Syntax = ps }) { Syntax = ps, IsDiscarded = true }) { Syntax = ps });
            }
        }

        // 3. instance field / property initializers (bound outside the parameter scope)
        if (cls is SourceClassSymbol sc)
        {
            _ = sc.GetMembers();
            var initScope = _scope;
            _scope = new Scope(null, ScopeKind.Method);
            _inInitializer = true;
            foreach (var (member, initSyntax) in sc.InstanceInitializers)
                stmts.AddRange(BindInitializer(sc, member, initSyntax, isStatic: false));
            _inInitializer = false;
            _scope = initScope;
        }

        // 4. the explicit body
        if (bodySyntax != null)
        {
            var prefix = BindParameterPatterns(m);
            stmts.AddRange(prefix);
            var rest = new List<StatementSyntax>();
            for (var i = explicitSuper ? 1 : 0; i < bodySyntax.Statements.Count; i++) rest.Add(bodySyntax.Statements[i]);
            var restStmts = BindStatementsInNewScope(SyntaxListFrom(bodySyntax, explicitSuper ? 1 : 0), out var locals);
            stmts.Add(new BoundBlock(locals, restStmts) { Syntax = bodySyntax });
        }
        return new BoundBlock(ImmutableArray<LocalSymbol>.Empty, stmts.ToImmutable()) { Syntax = bodySyntax };
    }

    private static SyntaxList<StatementSyntax> SyntaxListFrom(BlockSyntax body, int skip)
    {
        if (skip == 0) return body.Statements;
        var items = new List<SyntaxElement>();
        for (var i = skip; i < body.Statements.Count; i++) items.Add(body.Statements[i]);
        // the statements keep their original parents; this list node is only used for enumeration
        return new SyntaxList<StatementSyntax>(new ListNode(SyntaxKind.List, items.Select(x => x).ToArray()));
    }

    private BoundExpression ThisRef(NamedTypeSymbol cls, SyntaxNode at)
    {
        _usesThis = true;
        var t = cls.IsGenericType ? cls.Construct(cls.TypeParameters.CastArray<TypeSymbol>()) : cls;
        return new BoundThis(t) { Syntax = at };
    }

    private BoundStatement? BuildBaseConstructorCall(NamedTypeSymbol baseType, ImmutableArray<BoundExpression> args, SyntaxNode at, bool explicitCall)
    {
        var ctors = baseType.InstanceConstructors.Where(c => IsAccessible(c)).ToImmutableArray();
        if (ctors.Length == 0)
        {
            if (baseType.SpecialType == SpecialType.Object) return null;
            Report(Descriptors.NoAccessibleConstructor, at, baseType.ToDisplayString());
            return null;
        }
        var res = ResolveOverloads(ctors, args, ImmutableArray<TypeSymbol>.Empty, at, baseType.Name, report: true);
        if (res == null)
        {
            if (!explicitCall) Report(Descriptors.SuperCallRequired, at, baseType.ToDisplayString());
            return null;
        }
        var call = new BoundCall(ThisRef(CurrentType!, at), res.Method, res.Arguments, VoidType) { Syntax = at, IsBaseCall = true };
        return new BoundExpressionStatement(call) { Syntax = at };
    }

    private IEnumerable<BoundStatement> BindInitializer(NamedTypeSymbol owner, Symbol member, ExpressionSyntax init, bool isStatic)
    {
        TypeSymbol type;
        BoundExpression target;
        switch (member)
        {
            case SourcePropertySymbol p when p.BackingField != null:
                type = p.Type;
                target = new BoundFieldAccess(isStatic ? null : ThisRef(owner, init), p.BackingField) { Syntax = init };
                break;
            case SourceFieldSymbol f:
                type = f.Type;
                target = new BoundFieldAccess(isStatic ? null : ThisRef(owner, init), f) { Syntax = init };
                break;
            default:
                yield break;
        }
        var value = BindToType(init, type);
        if (value.HasErrors) yield break;
        yield return new BoundExpressionStatement(new BoundAssignment(target, value) { Syntax = init, IsDiscarded = true }) { Syntax = init };
    }

    private BoundBlock BindStaticInitializer(SourceMethodSymbol m)
    {
        var owner = m.ContainerType;
        var stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        _declaredReturn = VoidType;
        _inInitializer = true;
        if (owner is SourceClassSymbol sc)
        {
            _ = sc.GetMembers();
            foreach (var (member, init) in sc.StaticInitializers)
                stmts.AddRange(BindInitializer(sc, member, init, isStatic: true));
        }
        else if (owner is SourceModuleClassSymbol mod)
        {
            foreach (var (field, d) in mod.VariableInitializers)
            {
                if (d.Initializer == null || field.IsConst) continue;
                stmts.AddRange(BindInitializer(mod, field, d.Initializer.Value, isStatic: true));
            }
        }
        _inInitializer = false;
        return new BoundBlock(ImmutableArray<LocalSymbol>.Empty, stmts.ToImmutable());
    }

    // ------------------------------------------------------------------ top-level statements

    internal BoundBlock BindTopLevelStatements(SourceMethodSymbol m, SourceModuleClassSymbol module)
    {
        EnterFunction(m);
        _declaredReturn = UnwrapTaskReturn(m.ReturnType, m.IsAsync) ?? VoidType;
        var stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        var statements = _file.Tree.Root.Statements;
        var members = module.GetMembers();
        var list = new List<StatementSyntax>();
        foreach (var s in statements)
        {
            switch (s)
            {
                case ImportDeclarationSyntax or TypeAliasDeclarationSyntax or ClassDeclarationSyntax or InterfaceDeclarationSyntax or EnumDeclarationSyntax:
                    continue;
                case FunctionDeclarationSyntax:
                    continue;
                case EmptyStatementSyntax:
                    continue;
            }
            list.Add(s);
        }
        _inInitializer = true;
        foreach (var s in list)
        {
            if (s is VariableStatementSyntax { Declaration.IsUsing: false } vs && vs.Declaration.Declarators.All(d => d.Identifier is { IsMissing: false }))
            {
                foreach (var d in vs.Declaration.Declarators)
                {
                    var field = members.OfType<SourceFieldSymbol>().FirstOrDefault(f => f.IsModuleVariable && f.Declarator == d);
                    if (field == null || d.Initializer == null) continue;
                    if (field.IsConst) { var _ = BindToType(d.Initializer.Value, field.Type); continue; }
                    var value = BindToType(d.Initializer.Value, field.Type);
                    if (value.HasErrors) continue;
                    stmts.Add(new BoundExpressionStatement(new BoundAssignment(new BoundFieldAccess(null, field) { Syntax = d }, value) { Syntax = d, IsDiscarded = true }) { Syntax = d });
                }
                continue;
            }
            if (s is VariableStatementSyntax { Declaration.IsUsing: true } us)
            {
                stmts.Add(BindUsing(us, list, list.IndexOf(s) + 1));
                break;
            }
            stmts.Add(BindStatement(s));
        }
        _inInitializer = false;
        var locals = _scope.Locals.ToImmutableArray();
        return new BoundBlock(locals, stmts.ToImmutable());
    }
}
