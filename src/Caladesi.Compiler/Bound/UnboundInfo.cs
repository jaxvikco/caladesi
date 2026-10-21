using System.Collections.Immutable;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Bound;

/// <summary>Everything overload resolution needs to know about a lambda argument before its target type is known.</summary>
public sealed class LambdaInfo
{
    public required int ParameterCount { get; init; }
    /// <summary>Explicit parameter types (null entries are inferred from the target delegate).</summary>
    public required ImmutableArray<TypeSymbol?> ExplicitParameterTypes { get; init; }
    public TypeSymbol? ExplicitReturnType { get; init; }
    public bool IsAsync { get; init; }
    public bool IsGenerator { get; init; }
    public bool HasExpressionBody { get; init; }
    /// <summary>Binds the lambda with the given parameter types (diagnostics suppressed when <c>speculative</c>). Returns null on failure.</summary>
    public required Func<ImmutableArray<TypeSymbol>, TypeSymbol?, bool, BoundLambda?> Bind { get; init; }
    /// <summary>Infers the lambda's (unwrapped) return type given parameter types; null if it cannot be determined.</summary>
    public required Func<ImmutableArray<TypeSymbol>, TypeSymbol?> InferReturnType { get; init; }
}

public sealed class ObjectLiteralInfo
{
    public required ImmutableArray<string> PropertyNames { get; init; }
    public bool HasSpread { get; init; }
}

public sealed class CollectionInfo
{
    public required int ElementCount { get; init; }
    public bool HasSpread { get; init; }
    /// <summary>The natural element type (best common type), or null if none.</summary>
    public TypeSymbol? NaturalElementType { get; init; }
}

public sealed class LambdaSymbol : MethodSymbol
{
    private ImmutableArray<ParameterSymbol> _parameters = ImmutableArray<ParameterSymbol>.Empty;

    public LambdaSymbol(Symbol container, string name, bool isAsync, bool isIterator)
    {
        Container = container;
        Name_ = name;
        IsAsyncLambda = isAsync;
        IsIteratorLambda = isIterator;
    }

    public Symbol Container { get; }
    private string Name_ { get; }
    public bool IsAsyncLambda { get; }
    public bool IsIteratorLambda { get; }
    public TypeSymbol? ReturnTypeValue { get; set; }
    public override string Name => Name_;
    public override Symbol? ContainingSymbol => Container;
    public override MethodKind MethodKind => MethodKind.LambdaMethod;
    public override TypeSymbol ReturnType => ReturnTypeValue ?? ErrorTypeSymbol.Instance;
    public override ImmutableArray<ParameterSymbol> Parameters => _parameters;
    public override bool IsAsync => IsAsyncLambda;
    public override bool IsIterator => IsIteratorLambda;
    public override Accessibility DeclaredAccessibility => Accessibility.Private;
    public void SetParameters(ImmutableArray<ParameterSymbol> p) => _parameters = p;
    public bool CapturesThis { get; set; }
}
