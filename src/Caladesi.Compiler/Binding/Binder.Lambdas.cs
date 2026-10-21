using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    // function context
    private MethodSymbol? _function;
    private TypeSymbol? _declaredReturn;          // value type expected from `return` (null while inferring)
    private List<BoundExpression>? _returnExprs;  // return expressions collected while inferring
    private bool _sawBareReturn;
    private bool _isIteratorContext;
    private TypeSymbol? _yieldType;
    private List<BoundExpression>? _yieldExprs;

    private Symbol? LocalOwner => (Symbol?)_function ?? _containing;

    internal TypeSymbol? UnwrapTaskReturn(TypeSymbol? delegateReturn, bool isAsync)
    {
        if (delegateReturn == null || !isAsync) return delegateReturn;
        if (delegateReturn is NamedTypeSymbol n)
        {
            switch (n.OriginalDefinition.SpecialType)
            {
                case SpecialType.Task or SpecialType.ValueTask: return VoidType;
                case SpecialType.TaskT or SpecialType.ValueTaskT: return n.TypeArguments[0];
            }
        }
        return delegateReturn;
    }

    private BoundExpression BindArrowFunction(ArrowFunctionSyntax af, TypeSymbol? target)
    {
        var isAsync = af.AsyncKeyword != null;
        ImmutableArray<ParameterSyntax> ps;
        if (af.Parameters != null) ps = af.Parameters.Parameters.ToImmutableArray();
        else ps = ImmutableArray<ParameterSyntax>.Empty;
        var names = af.Parameters != null ? ps.Select(p => p.Identifier?.Text ?? "_").ToImmutableArray() : [af.SingleParameter!.Text];
        var explicitTypes = ImmutableArray.CreateBuilder<TypeSymbol?>();
        var defaults = new List<ExpressionSyntax?>();
        if (af.Parameters != null)
        {
            foreach (var p in ps)
            {
                explicitTypes.Add(p.TypeAnnotation != null ? BindType(p.TypeAnnotation.Type) : null);
                defaults.Add(p.Initializer?.Value);
            }
        }
        else explicitTypes.Add(null);
        TypeSymbol? explicitReturn = af.ReturnType != null ? BindType(af.ReturnType.Type, allowVoid: true) : null;
        return MakeUnboundLambda(af, names, af.Parameters, explicitTypes.ToImmutable(), explicitReturn, isAsync, false, af.Body);
    }

    private BoundExpression BindFunctionExpression(FunctionExpressionSyntax fe, TypeSymbol? target)
    {
        var ps = fe.Parameters.Parameters;
        var explicitTypes = ImmutableArray.CreateBuilder<TypeSymbol?>();
        foreach (var p in ps) explicitTypes.Add(p.TypeAnnotation != null ? BindType(p.TypeAnnotation.Type) : null);
        var names = ps.Select(p => p.Identifier?.Text ?? "_").ToImmutableArray();
        TypeSymbol? explicitReturn = fe.ReturnType != null ? BindType(fe.ReturnType.Type, allowVoid: true) : null;
        return MakeUnboundLambda(fe, names, fe.Parameters, explicitTypes.ToImmutable(), explicitReturn, fe.AsyncKeyword != null, fe.AsteriskToken != null, fe.Body);
    }

    private BoundExpression MakeUnboundLambda(SyntaxNode syntax, ImmutableArray<string> names, ParameterListSyntax? parameterList,
        ImmutableArray<TypeSymbol?> explicitTypes, TypeSymbol? explicitReturn, bool isAsync, bool isGenerator, SyntaxNode body)
    {
        var capturedScope = _scope;
        var capturedNarrow = _narrow;
        var info = new LambdaInfo
        {
            ParameterCount = names.Length,
            ExplicitParameterTypes = explicitTypes,
            ExplicitReturnType = explicitReturn,
            IsAsync = isAsync,
            IsGenerator = isGenerator,
            HasExpressionBody = body is not BlockSyntax,
            Bind = (paramTypes, delegateReturn, speculative) =>
                WithCapturedState(capturedScope, capturedNarrow, () =>
                    speculative
                        ? Speculatively(() => BindLambdaBody(syntax, names, parameterList, paramTypes, explicitReturn ?? delegateReturn, null, isAsync, isGenerator, body))
                        : BindLambdaBody(syntax, names, parameterList, paramTypes, explicitReturn ?? delegateReturn, null, isAsync, isGenerator, body)),
            InferReturnType = paramTypes =>
                WithCapturedState(capturedScope, capturedNarrow, () => Speculatively(() =>
                {
                    var l = BindLambdaBody(syntax, names, parameterList, paramTypes, explicitReturn, null, isAsync, isGenerator, body, inferOnly: true);
                    return l?.Symbol.ReturnTypeValue;
                })),
        };
        Func<TypeSymbol?, BoundExpression> bindToType = target =>
        {
            if (target == null) return Bad(syntax);
            return WithCapturedState(capturedScope, capturedNarrow, () => BindLambdaToDelegate(syntax, info, names, parameterList, target, isAsync, isGenerator, body));
        };
        return new BoundUnbound(syntax, bindToType) { IsLambda = true, Lambda = info };
    }

    private T WithCapturedState<T>(Scope scope, ImmutableDictionary<NarrowPath, TypeSymbol> narrow, Func<T> f)
    {
        var savedScope = _scope;
        var savedNarrow = _narrow;
        _scope = scope;
        _narrow = narrow;
        try { return f(); }
        finally { _scope = savedScope; _narrow = savedNarrow; }
    }

    private BoundExpression BindLambdaToDelegate(SyntaxNode syntax, LambdaInfo info, ImmutableArray<string> names, ParameterListSyntax? parameterList,
        TypeSymbol target, bool isAsync, bool isGenerator, SyntaxNode body)
    {
        var d = target.StripNullable() as NamedTypeSymbol;
        if (d is not { TypeKind: TypeKind.Delegate } || d.DelegateInvokeMethod is not { } invoke)
        {
            if (!target.IsErrorType) Report(Descriptors.LambdaNeedsDelegate, syntax, target.ToDisplayString());
            return Bad(syntax);
        }
        if (invoke.Parameters.Length != names.Length)
        {
            Report(Descriptors.LambdaParameterCount, syntax, names.Length, invoke.Parameters.Length);
            return Bad(syntax);
        }
        var paramTypes = invoke.Parameters.Select(p => p.Type).ToImmutableArray();
        for (var i = 0; i < paramTypes.Length; i++)
        {
            var ex = info.ExplicitParameterTypes[i];
            if (ex != null && !ex.Equals(paramTypes[i]) && !paramTypes[i].IsErrorType && !ex.IsErrorType)
            {
                // a parameter annotation must be compatible (contravariantly)
                if (!ClassifyImplicit(null, paramTypes[i], ex).Exists)
                {
                    Report(Descriptors.LambdaParameterType, syntax, names[i], ex.ToDisplayString(), paramTypes[i].ToDisplayString());
                    return Bad(syntax);
                }
            }
        }
        var lambda = BindLambdaBody(syntax, names, parameterList, paramTypes, info.ExplicitReturnType ?? invoke.ReturnType, d, isAsync, isGenerator, body);
        if (lambda == null) return Bad(syntax);
        var result = (BoundExpression)lambda;
        if (target is NullableTypeSymbol) result = new BoundConversion(result, ConversionKind.ToNullable, target) { Syntax = syntax };
        return result;
    }

    /// <summary>Binds a lambda body. <paramref name="delegateReturn"/> is the delegate's return type, or null to infer.</summary>
    private BoundLambda? BindLambdaBody(SyntaxNode syntax, ImmutableArray<string> names, ParameterListSyntax? parameterList, ImmutableArray<TypeSymbol> paramTypes,
        TypeSymbol? delegateReturn, NamedTypeSymbol? delegateType, bool isAsync, bool isGenerator, SyntaxNode body, bool inferOnly = false)
    {
        // save function context
        var savedScope = _scope;
        var savedFunction = _function;
        var savedDeclared = _declaredReturn;
        var savedReturns = _returnExprs;
        var savedBare = _sawBareReturn;
        var savedAsync = _isAsyncContext;
        var savedIter = _isIteratorContext;
        var savedYield = _yieldType;
        var savedYieldExprs = _yieldExprs;
        var savedNarrow = _narrow;
        var savedCtor = (_inConstructor, _inStaticConstructor, _inInitializer);
        try
        {
            var sym = new LambdaSymbol(LocalOwner!, "<lambda>", isAsync, isGenerator);
            var ps = ImmutableArray.CreateBuilder<ParameterSymbol>(names.Length);
            for (var i = 0; i < names.Length; i++)
            {
                var declSyntax = parameterList != null && i < parameterList.Parameters.Count ? parameterList.Parameters[i] : null;
                ps.Add(new ParameterSymbol(names[i], paramTypes[i], i, sym) { DeclaringSyntax = declSyntax, IsFromSourceParam = true });
            }
            sym.SetParameters(ps.MoveToImmutable());

            PushScope(Binder.ScopeKind.Lambda);
            foreach (var p in sym.Parameters) DeclareSymbol(p.Name, p, p.DeclaringSyntax);
            DropMutableNarrowings();
            InvalidateAssignedIn(body);
            _inConstructor = false; _inStaticConstructor = false; _inInitializer = false;
            _function = sym;
            _isAsyncContext = isAsync;
            _isIteratorContext = isGenerator;
            _returnExprs = null;
            _sawBareReturn = false;

            var inferring = delegateReturn == null;
            TypeSymbol? expectedInner = inferring ? null : UnwrapTaskReturn(delegateReturn, isAsync);
            if (isGenerator && !inferring)
            {
                _yieldType = IteratorElementType(delegateReturn!, isAsync);
                expectedInner = VoidType;
            }
            _declaredReturn = expectedInner;
            if (inferring) { _returnExprs = new List<BoundExpression>(); _yieldExprs = new List<BoundExpression>(); }
            else _yieldExprs = null;

            BoundBlock block;
            if (body is BlockSyntax bs)
            {
                var stmts = BindStatementsInNewScope(bs.Statements, out var locals);
                block = new BoundBlock(locals, stmts) { Syntax = bs };
                if (!inferring && expectedInner != null && !expectedInner.IsVoid && !isGenerator && CanCompleteNormally(block))
                    Report(Descriptors.NotAllPathsReturn, bs.CloseBraceToken, "lambda");
            }
            else
            {
                var expr = (ExpressionSyntax)body;
                if (expectedInner != null && expectedInner.IsVoid)
                {
                    var e = BindStatementExpression(expr);
                    block = new BoundBlock(ImmutableArray<LocalSymbol>.Empty, [new BoundExpressionStatement(e) { Syntax = expr }]) { Syntax = expr };
                }
                else if (expectedInner != null)
                {
                    var e = BindToType(expr, expectedInner);
                    block = new BoundBlock(ImmutableArray<LocalSymbol>.Empty, [new BoundReturn(e) { Syntax = expr }]) { Syntax = expr };
                }
                else
                {
                    var e = BindValue(expr);
                    if (e is BoundUnbound) e = ConvertImplicit(e, e.Type ?? ObjectType, expr);
                    _returnExprs!.Add(e);
                    // a void call as expression body: no value
                    if (e.Type != null && e.Type.IsVoid)
                    {
                        _returnExprs.Clear();
                        block = new BoundBlock(ImmutableArray<LocalSymbol>.Empty, [new BoundExpressionStatement(e) { Syntax = expr }]) { Syntax = expr };
                    }
                    else
                        block = new BoundBlock(ImmutableArray<LocalSymbol>.Empty, [new BoundReturn(e) { Syntax = expr }]) { Syntax = expr };
                }
            }

            // return type
            TypeSymbol returnType;
            if (!inferring) returnType = delegateReturn!;
            else
            {
                TypeSymbol? inner = null;
                if (isGenerator)
                {
                    var elem = _yieldExprs!.Count > 0 ? BestCommonType(_yieldExprs, syntax) : null;
                    var en = _c.GetWellKnownType(isAsync ? "System.Collections.Generic.IAsyncEnumerable`1" : "System.Collections.Generic.IEnumerable`1");
                    inner = elem != null && en != null ? en.Construct(elem) : null;
                    returnType = inner ?? ErrorType;
                }
                else
                {
                    if (_returnExprs!.Count > 0) inner = BestCommonType(_returnExprs, syntax);
                    else inner = VoidType;
                    if (inner == null) { inner = ErrorType; if (!inferOnly) Report(Descriptors.NoBestCommonTypeReturns, syntax); }
                    returnType = isAsync ? WrapInTask(inner) : inner;
                }
                if (inner != null && !isGenerator && !inferOnly)
                {
                    // convert collected returns to the inferred type
                    block = (BoundBlock)new ReturnConverter(this, inner).Visit(block)!;
                }
            }
            sym.ReturnTypeValue = returnType;
            var lambdaDelegate = (TypeSymbol?)delegateType;
            if (lambdaDelegate == null)
            {
                lambdaDelegate = MakeDelegateType(sym.Parameters.Select(p => p.Type).ToImmutableArray(), returnType, syntax);
            }
            return new BoundLambda(sym, block, lambdaDelegate) { Syntax = syntax };
        }
        finally
        {
            _scope = savedScope;
            _function = savedFunction;
            _declaredReturn = savedDeclared;
            _returnExprs = savedReturns;
            _sawBareReturn = savedBare;
            _isAsyncContext = savedAsync;
            _isIteratorContext = savedIter;
            _yieldType = savedYield;
            _yieldExprs = savedYieldExprs;
            _narrow = savedNarrow;
            (_inConstructor, _inStaticConstructor, _inInitializer) = savedCtor;
        }
    }

    private TypeSymbol WrapInTask(TypeSymbol inner)
    {
        if (inner.IsVoid) return _c.GetSpecialType(SpecialType.Task);
        var t = _c.GetSpecialType(SpecialType.TaskT);
        return t.IsErrorType ? t : t.Construct(inner);
    }

    internal TypeSymbol IteratorElementType(TypeSymbol returnType, bool isAsync)
    {
        if (returnType is NamedTypeSymbol { IsGenericType: true } n && n.TypeArguments.Length == 1) return n.TypeArguments[0];
        return ObjectType;
    }

    /// <summary>Rewrites `return e` statements after the return type of a lambda has been inferred.</summary>
    private sealed class ReturnConverter(Binder binder, TypeSymbol target) : BoundTreeRewriter
    {
        protected override BoundNode VisitReturn(BoundReturn n)
        {
            if (n.Expression == null || n.Expression.Type == null) return n;
            if (n.Expression.Type.Equals(target)) return n;
            var conv = binder.ConvertImplicit(n.Expression, target, n.Syntax, report: false);
            return new BoundReturn(conv) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitLambda(BoundLambda n) => n; // nested lambdas keep their own returns
    }
}
