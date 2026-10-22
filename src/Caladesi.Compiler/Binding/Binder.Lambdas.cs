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

    private Symbol? LocalOwner => (Symbol?)_function ?? ContainingSymbol;

    internal TypeSymbol? UnwrapTaskReturn(TypeSymbol? delegateReturn, bool isAsync)
    {
        if (delegateReturn == null || !isAsync)
        {
            return delegateReturn;
        }

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

    private BoundUnbound BindArrowFunction(ArrowFunctionSyntax af, TypeSymbol? _1)
    {
        bool isAsync = af.AsyncKeyword != null;
        ImmutableArray<ParameterSyntax> ps = af.Parameters != null ? [.. af.Parameters.Parameters] : [];
        ImmutableArray<string> names = af.Parameters != null ? [.. ps.Select(p => p.Identifier?.Text ?? "_")] : [af.SingleParameter!.Text];
        ImmutableArray<TypeSymbol?>.Builder explicitTypes = ImmutableArray.CreateBuilder<TypeSymbol?>();
        List<ExpressionSyntax?> defaults = [];
        if (af.Parameters != null)
        {
            foreach (ParameterSyntax p in ps)
            {
                explicitTypes.Add(p.TypeAnnotation != null ? BindType(p.TypeAnnotation.Type) : null);
                defaults.Add(p.Initializer?.Value);
            }
        }
        else
        {
            explicitTypes.Add(null);
        }

        TypeSymbol? explicitReturn = af.ReturnType != null ? BindType(af.ReturnType.Type, allowVoid: true) : null;
        return MakeUnboundLambda(af, names, af.Parameters, explicitTypes.ToImmutable(), explicitReturn, isAsync, false, af.Body);
    }

    private BoundUnbound BindFunctionExpression(FunctionExpressionSyntax fe, TypeSymbol? _1)
    {
        SeparatedSyntaxList<ParameterSyntax> ps = fe.Parameters.Parameters;
        ImmutableArray<TypeSymbol?>.Builder explicitTypes = ImmutableArray.CreateBuilder<TypeSymbol?>();
        foreach (ParameterSyntax p in ps)
        {
            explicitTypes.Add(p.TypeAnnotation != null ? BindType(p.TypeAnnotation.Type) : null);
        }

        ImmutableArray<string> names = [.. ps.Select(p => p.Identifier?.Text ?? "_")];
        TypeSymbol? explicitReturn = fe.ReturnType != null ? BindType(fe.ReturnType.Type, allowVoid: true) : null;
        return MakeUnboundLambda(fe, names, fe.Parameters, explicitTypes.ToImmutable(), explicitReturn, fe.AsyncKeyword != null, fe.AsteriskToken != null, fe.Body);
    }

    private BoundUnbound MakeUnboundLambda(SyntaxNode syntax, ImmutableArray<string> names, ParameterListSyntax? parameterList,
        ImmutableArray<TypeSymbol?> explicitTypes, TypeSymbol? explicitReturn, bool isAsync, bool isGenerator, SyntaxNode body)
    {
        Scope capturedScope = _scope;
        ImmutableDictionary<NarrowPath, TypeSymbol> capturedNarrow = _narrow;
        LambdaInfo info = new()
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
                    BoundLambda? l = BindLambdaBody(syntax, names, parameterList, paramTypes, explicitReturn, null, isAsync, isGenerator, body, inferOnly: true);
                    return l?.Symbol.ReturnTypeValue;
                })),
        };
        BoundExpression bindToType(TypeSymbol? target)
        {
            target ??= NaturalDelegateType(info);
            if (target == null)
            {
                Report(Descriptors.CannotInferType, syntax);
                return Bad(syntax);
            }
            return WithCapturedState(capturedScope, capturedNarrow, () => BindLambdaToDelegate(syntax, info, names, parameterList, target, isAsync, isGenerator, body));
        }
        return new BoundUnbound(syntax, bindToType) { IsLambda = true, Lambda = info };
    }

    /// <summary>True when a lambda can be bound without a target delegate type (all parameters annotated).</summary>
    internal static bool HasNaturalType(BoundUnbound u)
    {
        return !u.IsLambda || (u.Lambda is { IsGenerator: false } l && l.ExplicitParameterTypes.All(t => t != null));
    }

    /// <summary>The Func/Action type of a lambda whose parameters are all annotated (<c>(x: int) =&gt; x + 1</c> is <c>Func&lt;int, int&gt;</c>).</summary>
    private NamedTypeSymbol? NaturalDelegateType(LambdaInfo info)
    {
        if (info.IsGenerator || info.ExplicitParameterTypes.Any(t => t == null) || info.ParameterCount > 16)
        {
            return null;
        }

        ImmutableArray<TypeSymbol> ps = [.. info.ExplicitParameterTypes.Select(t => t!)];
        TypeSymbol? ret = info.ExplicitReturnType ?? info.InferReturnType(ps);
        if (ret == null || ret.IsErrorType)
        {
            return null;
        }

        if (info.IsAsync && info.ExplicitReturnType == null)
        {
            ret = WrapInTask(ret);
        }

        int n = ps.Length;
        if (ret.ReturnsNothing)
        {
            NamedTypeSymbol? action = Compilation.GetWellKnownType(n == 0 ? "System.Action" : "System.Action`" + n);
            return n == 0 ? action : action?.Construct(ps);
        }
        NamedTypeSymbol? func = Compilation.GetWellKnownType("System.Func`" + (n + 1));
        return func?.Construct(ps.Add(ret));
    }

    private T WithCapturedState<T>(Scope scope, ImmutableDictionary<NarrowPath, TypeSymbol> narrow, Func<T> f)
    {
        Scope savedScope = _scope;
        ImmutableDictionary<NarrowPath, TypeSymbol> savedNarrow = _narrow;
        _scope = scope;
        _narrow = narrow;
        try { return f(); }
        finally { _scope = savedScope; _narrow = savedNarrow; }
    }

    private BoundExpression BindLambdaToDelegate(SyntaxNode syntax, LambdaInfo info, ImmutableArray<string> names, ParameterListSyntax? parameterList,
        TypeSymbol target, bool isAsync, bool isGenerator, SyntaxNode body)
    {
        NamedTypeSymbol? d = target.StripNullable() as NamedTypeSymbol;
        if (d is not { TypeKind: TypeKind.Delegate } || d.DelegateInvokeMethod is not { } invoke)
        {
            if (!target.IsErrorType)
            {
                Report(Descriptors.LambdaNeedsDelegate, syntax, target.ToDisplayString());
            }

            return Bad(syntax);
        }
        if (invoke.Parameters.Length != names.Length)
        {
            Report(Descriptors.LambdaParameterCount, syntax, names.Length, invoke.Parameters.Length);
            return Bad(syntax);
        }
        ImmutableArray<TypeSymbol> paramTypes = [.. invoke.Parameters.Select(p => p.Type)];
        for (int i = 0; i < paramTypes.Length; i++)
        {
            TypeSymbol? ex = info.ExplicitParameterTypes[i];
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
        BoundLambda? lambda = BindLambdaBody(syntax, names, parameterList, paramTypes, info.ExplicitReturnType ?? invoke.ReturnType, d, isAsync, isGenerator, body);
        if (lambda == null)
        {
            return Bad(syntax);
        }

        BoundExpression result = lambda;
        if (target is NullableTypeSymbol)
        {
            result = new BoundConversion(result, ConversionKind.ToNullable, target) { Syntax = syntax };
        }

        return result;
    }

    /// <summary>Binds a lambda body. <paramref name="delegateReturn"/> is the delegate's return type, or null to infer.</summary>
    private BoundLambda? BindLambdaBody(SyntaxNode syntax, ImmutableArray<string> names, ParameterListSyntax? parameterList, ImmutableArray<TypeSymbol> paramTypes,
        TypeSymbol? delegateReturn, NamedTypeSymbol? delegateType, bool isAsync, bool isGenerator, SyntaxNode body, bool inferOnly = false)
    {
        // save function context
        Scope savedScope = _scope;
        MethodSymbol? savedFunction = _function;
        TypeSymbol? savedDeclared = _declaredReturn;
        List<BoundExpression>? savedReturns = _returnExprs;
        bool savedBare = _sawBareReturn;
        bool savedInCatch = _inCatch;
        bool savedAsync = _isAsyncContext;
        bool savedIter = _isIteratorContext;
        TypeSymbol? savedYield = _yieldType;
        List<BoundExpression>? savedYieldExprs = _yieldExprs;
        ImmutableDictionary<NarrowPath, TypeSymbol> savedNarrow = _narrow;
        (bool _inConstructor, bool _inStaticConstructor, bool _inInitializer) savedCtor = (_inConstructor, _inStaticConstructor, _inInitializer);
        try
        {
            LambdaSymbol sym = new(LocalOwner!, "<lambda>", isAsync, isGenerator);
            ImmutableArray<ParameterSymbol>.Builder ps = ImmutableArray.CreateBuilder<ParameterSymbol>(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                ParameterSyntax? declSyntax = parameterList != null && i < parameterList.Parameters.Count ? parameterList.Parameters[i] : null;
                ps.Add(new ParameterSymbol(names[i], paramTypes[i], i, sym) { DeclaringSyntax = declSyntax, IsFromSourceParam = true });
            }
            sym.SetParameters(ps.MoveToImmutable());

            _ = PushScope(ScopeKind.Lambda);
            foreach (ParameterSymbol p in sym.Parameters)
            {
                DeclareSymbol(p.Name, p, p.DeclaringSyntax);
            }

            DropMutableNarrowings();
            InvalidateAssignedIn(body);
            _inConstructor = false; _inStaticConstructor = false; _inInitializer = false;
            _inCatch = false;
            _function = sym;
            _isAsyncContext = isAsync;
            _isIteratorContext = isGenerator;
            _returnExprs = null;
            _sawBareReturn = false;

            bool inferring = delegateReturn == null;
            TypeSymbol? expectedInner = inferring ? null : UnwrapTaskReturn(delegateReturn, isAsync);
            if (isGenerator && !inferring)
            {
                _yieldType = IteratorElementType(delegateReturn!, isAsync);
                expectedInner = VoidType;
            }
            _declaredReturn = expectedInner;
            if (inferring) { _returnExprs = []; _yieldExprs = []; }
            else
            {
                _yieldExprs = null;
            }

            BoundBlock block;
            if (body is BlockSyntax bs)
            {
                ImmutableArray<BoundStatement> stmts = BindStatementsInNewScope(bs.Statements, out ImmutableArray<LocalSymbol> locals);
                block = new BoundBlock(locals, stmts) { Syntax = bs };
                if (!inferring && expectedInner != null && !expectedInner.IsVoid && !isGenerator && CanCompleteNormally(block))
                {
                    Report(Descriptors.NotAllPathsReturn, bs.CloseBraceToken, "lambda");
                }
            }
            else
            {
                ExpressionSyntax expr = (ExpressionSyntax)body;
                if (expectedInner != null && expectedInner.IsVoid)
                {
                    BoundExpression e = BindStatementExpression(expr);
                    block = new BoundBlock([], [new BoundExpressionStatement(e) { Syntax = expr }]) { Syntax = expr };
                }
                else if (expectedInner != null)
                {
                    BoundExpression e = BindToType(expr, expectedInner);
                    block = new BoundBlock([], [new BoundReturn(e) { Syntax = expr }]) { Syntax = expr };
                }
                else
                {
                    BoundExpression e = BindValue(expr);
                    if (e is BoundUnbound)
                    {
                        e = ConvertImplicit(e, e.Type ?? ObjectType, expr);
                    }

                    _returnExprs!.Add(e);
                    // a void call as expression body: no value
                    if (e.Type != null && e.Type.IsVoid)
                    {
                        _returnExprs.Clear();
                        block = new BoundBlock([], [new BoundExpressionStatement(e) { Syntax = expr }]) { Syntax = expr };
                    }
                    else
                    {
                        block = new BoundBlock([], [new BoundReturn(e) { Syntax = expr }]) { Syntax = expr };
                    }
                }
            }

            // return type
            TypeSymbol returnType;
            if (!inferring)
            {
                returnType = delegateReturn!;
            }
            else
            {
                TypeSymbol? inner = null;
                if (isGenerator)
                {
                    TypeSymbol? elem = _yieldExprs!.Count > 0 ? BestCommonType(_yieldExprs, syntax) : null;
                    NamedTypeSymbol? en = Compilation.GetWellKnownType(isAsync ? "System.Collections.Generic.IAsyncEnumerable`1" : "System.Collections.Generic.IEnumerable`1");
                    inner = elem != null && en != null ? en.Construct(elem) : null;
                    returnType = inner ?? ErrorType;
                }
                else
                {
                    inner = _returnExprs!.Count > 0 ? BestCommonType(_returnExprs, syntax) : VoidType;
                    if (inner == null)
                    {
                        inner = ErrorType; if (!inferOnly)
                        {
                            Report(Descriptors.NoBestCommonTypeReturns, syntax);
                        }
                    }
                    returnType = isAsync ? WrapInTask(inner) : inner;
                }
                if (inner != null && !isGenerator && !inferOnly)
                {
                    // convert collected returns to the inferred type
                    block = (BoundBlock)new ReturnConverter(this, inner).Visit(block)!;
                }
            }
            sym.ReturnTypeValue = returnType;
            TypeSymbol lambdaDelegate = (TypeSymbol?)delegateType ?? MakeDelegateType([.. sym.Parameters.Select(p => p.Type)], returnType, syntax);
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
            _inCatch = savedInCatch;
        }
    }

    private NamedTypeSymbol WrapInTask(TypeSymbol inner)
    {
        if (inner.IsVoid)
        {
            return Compilation.GetSpecialType(SpecialType.Task);
        }

        NamedTypeSymbol t = Compilation.GetSpecialType(SpecialType.TaskT);
        return t.IsErrorType ? t : t.Construct(inner);
    }

    internal TypeSymbol IteratorElementType(TypeSymbol returnType, bool _1)
    {
        return returnType is NamedTypeSymbol { IsGenericType: true } n && n.TypeArguments.Length == 1 ? n.TypeArguments[0] : ObjectType;
    }

    /// <summary>Rewrites `return e` statements after the return type of a lambda has been inferred.</summary>
    private sealed class ReturnConverter(Binder binder, TypeSymbol target) : BoundTreeRewriter
    {
        protected override BoundNode VisitReturn(BoundReturn n)
        {
            if (n.Expression == null || n.Expression.Type == null)
            {
                return n;
            }

            if (n.Expression.Type.Equals(target))
            {
                return n;
            }

            BoundExpression conv = binder.ConvertImplicit(n.Expression, target, n.Syntax, report: false);
            return new BoundReturn(conv) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitLambda(BoundLambda n)
        {
            return n; // nested lambdas keep their own returns
        }
    }
}
