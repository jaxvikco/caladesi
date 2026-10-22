using System.Collections.Immutable;

namespace Caladesi.Compiler.Symbols.Source;

public sealed class SynthesizedFieldSymbol(Symbol container, string name, TypeSymbol type, bool isStatic = false, Accessibility accessibility = Accessibility.Public, bool isReadOnly = false) : FieldSymbol
{
    private readonly Symbol _container = container;
    private TypeSymbol _type = type;

    private string Name_ { get; } = name;
    private bool IsStaticFlag { get; } = isStatic;
    private Accessibility Accessibility_ { get; } = accessibility;
    private bool IsReadOnlyFlag { get; } = isReadOnly;
    public override string Name => Name_;
    public override Symbol? ContainingSymbol => _container;
    public override TypeSymbol Type => _type;
    internal void SetType(TypeSymbol t)
    {
        _type = t;
    }

    public override bool IsStatic => IsStaticFlag;
    public override bool IsReadOnly => IsReadOnlyFlag;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    public override bool IsFromSource => true;
}

public sealed class SynthesizedMethodSymbol(Symbol container, string name, MethodKind kind, Accessibility accessibility, bool isStatic, TypeSymbol returnType,
    ImmutableArray<ParameterSymbol> parameters, bool isVirtual = false, bool isOverride = false, bool isFinal = false, bool isAbstract = false) : MethodSymbol
{
    private readonly Symbol _container = container;
    private ImmutableArray<ParameterSymbol> _params = parameters;
    private TypeSymbol _returnType = returnType;
    private ImmutableArray<TypeParameterSymbol> _typeParams = [];

    private string Name_ { get; } = name;
    private MethodKind Kind_ { get; } = kind;
    private Accessibility Accessibility_ { get; } = accessibility;
    private bool IsStaticFlag { get; } = isStatic;
    private bool IsVirtualFlag { get; } = isVirtual;
    private bool IsOverrideFlag { get; } = isOverride;
    private bool IsFinalFlag { get; } = isFinal;
    private bool IsAbstractFlag { get; } = isAbstract;
    /// <summary>The source method this was derived from (lambda bodies, state machines): used for debug info.</summary>
    public MethodSymbol? SourceMethod { get; init; }
    public SynthesizedBodyKind SynthesizedKind { get; init; }
    /// <summary>For a method generated from a lambda or local function: the lambda it came from.</summary>
    public Caladesi.Compiler.Bound.LambdaSymbol? LambdaOrigin { get; init; }
    public bool IsAsyncMethod { get; init; }
    public bool IsIteratorMethod { get; init; }
    public Symbol? Associated { get; init; }
    public ImmutableArray<MethodSymbol> Overrides { get; init; } = [];

    public override string Name => Name_;
    public override Symbol? ContainingSymbol => _container;
    public override MethodKind MethodKind => Kind_;
    public override TypeSymbol ReturnType => _returnType;
    public override ImmutableArray<ParameterSymbol> Parameters => _params;
    public override ImmutableArray<TypeParameterSymbol> TypeParameters => _typeParams;
    public override bool IsStatic => IsStaticFlag;
    public override bool IsVirtual => IsVirtualFlag;
    public override bool IsOverride => IsOverrideFlag;
    public override bool IsSealed => IsFinalFlag;
    public override bool IsAbstract => IsAbstractFlag;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    public override bool IsFromSource => true;
    public override ImmutableArray<MethodSymbol> ExplicitInterfaceImplementations => Overrides;
    public override bool IsAsync => IsAsyncMethod;
    public override bool IsIterator => IsIteratorMethod;
    public override Symbol? AssociatedSymbol => Associated;
    internal void SetReturnType(TypeSymbol t)
    {
        _returnType = t;
    }

    internal void SetParameters(ImmutableArray<ParameterSymbol> p)
    {
        _params = p;
    }

    internal void SetTypeParameters(ImmutableArray<TypeParameterSymbol> tps)
    {
        _typeParams = tps;
    }
}

/// <summary>A compiler-generated class or struct (closure environments, state machines, ...).</summary>
public sealed class SynthesizedNamedType(string name, Symbol? container, TypeKind kind, Accessibility accessibility = Accessibility.Private,
    NamedTypeSymbol? baseType = null, ImmutableArray<TypeParameterSymbol> typeParameters = default) : NamedTypeSymbol
{
    private readonly List<Symbol> _members = [];
    private readonly Symbol? _container = container;
    private NamedTypeSymbol? _baseType = baseType;
    private ImmutableArray<NamedTypeSymbol> _interfaces = [];

    /// <summary>Supplies the type parameters after construction (they need this type as their container).</summary>
    public void SetTypeParameters(ImmutableArray<TypeParameterSymbol> tps)
    {
        TypeParameters_ = tps;
    }

    /// <summary>Number of type parameters this type declares itself (the rest are inherited from its containing type).</summary>
    public int OwnArityValue { get; set; } = -1;
    /// <summary>For closure classes: maps the original method's type parameters to the ordinals of this class's own copies; applied when encoding signatures.</summary>
#pragma warning disable CA2227 // assigned by the closure lowerer after construction
    public Dictionary<TypeParameterSymbol, int>? MethodTypeParameterMap { get; set; }
#pragma warning restore CA2227

    private string Name_ { get; } = name;
    private TypeKind TypeKind_ { get; } = kind;
    private Accessibility Accessibility_ { get; } = accessibility;
    private ImmutableArray<TypeParameterSymbol> TypeParameters_ { get; set; } = typeParameters.IsDefault ? [] : typeParameters;
    public bool IsSealedFlag { get; init; } = true;
    public bool IsAbstractFlag { get; init; }
    public bool IsStaticFlag { get; init; }
#pragma warning disable CA1822 // marker flag read through instances by callers; making it static would break `symbol.Flag` access
    public bool IsCompilerGenerated => true;
#pragma warning restore CA1822
#pragma warning disable CA1721 // Members is the declared-member list; GetMembers is the symbol API
    public IReadOnlyList<Symbol> Members => _members;
#pragma warning restore CA1721

    public override string Name => Name_;
    public override Symbol? ContainingSymbol => _container;
    public override TypeKind TypeKind => TypeKind_;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    public override bool IsSealed => IsSealedFlag;
    public override bool IsAbstract => IsAbstractFlag;
    public override bool IsStatic => IsStaticFlag;
    public override bool IsStaticClass => IsStaticFlag;
    public override bool IsFromSource => true;
    public override NamedTypeSymbol? BaseType => _baseType;
    public override ImmutableArray<NamedTypeSymbol> Interfaces => _interfaces;
    public override ImmutableArray<TypeParameterSymbol> TypeParameters => TypeParameters_;
    public override ImmutableArray<TypeSymbol> TypeArguments => TypeParameters_.CastArray<TypeSymbol>();
    public override int OwnArity => OwnArityValue >= 0 ? OwnArityValue : TypeParameters_.Length;

    public void AddMember(Symbol m)
    {
        _members.Add(m);
    }

    public void AddInterface(NamedTypeSymbol i)
    {
        _interfaces = _interfaces.Add(i);
    }

    public void SetBase(NamedTypeSymbol b)
    {
        _baseType = b;
    }

    public override ImmutableArray<Symbol> GetMembers()
    {
        return [.. _members];
    }
}
