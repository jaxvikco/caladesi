using System.Collections.Immutable;

namespace Caladesi.Compiler.Symbols.Source;

public sealed class SynthesizedFieldSymbol : FieldSymbol
{
    private readonly Symbol _container;
    private TypeSymbol _type;

    public SynthesizedFieldSymbol(Symbol container, string name, TypeSymbol type, bool isStatic = false, Accessibility accessibility = Accessibility.Public, bool isReadOnly = false)
    {
        _container = container;
        Name_ = name;
        _type = type;
        IsStaticFlag = isStatic;
        Accessibility_ = accessibility;
        IsReadOnlyFlag = isReadOnly;
    }

    private string Name_ { get; }
    private bool IsStaticFlag { get; }
    private Accessibility Accessibility_ { get; }
    private bool IsReadOnlyFlag { get; }
    public override string Name => Name_;
    public override Symbol? ContainingSymbol => _container;
    public override TypeSymbol Type => _type;
    internal void SetType(TypeSymbol t) => _type = t;
    public override bool IsStatic => IsStaticFlag;
    public override bool IsReadOnly => IsReadOnlyFlag;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    public override bool IsFromSource => true;
}

public sealed class SynthesizedMethodSymbol : MethodSymbol
{
    private readonly Symbol _container;
    private ImmutableArray<ParameterSymbol> _params;
    private TypeSymbol _returnType;
    private ImmutableArray<TypeParameterSymbol> _typeParams;

    public SynthesizedMethodSymbol(Symbol container, string name, MethodKind kind, Accessibility accessibility, bool isStatic, TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters, bool isVirtual = false, bool isOverride = false, bool isFinal = false, bool isAbstract = false)
    {
        _container = container;
        Name_ = name;
        Kind_ = kind;
        Accessibility_ = accessibility;
        IsStaticFlag = isStatic;
        _returnType = returnType;
        _params = parameters;
        IsVirtualFlag = isVirtual;
        IsOverrideFlag = isOverride;
        IsFinalFlag = isFinal;
        IsAbstractFlag = isAbstract;
        _typeParams = ImmutableArray<TypeParameterSymbol>.Empty;
    }

    private string Name_ { get; }
    private MethodKind Kind_ { get; }
    private Accessibility Accessibility_ { get; }
    private bool IsStaticFlag { get; }
    private bool IsVirtualFlag { get; }
    private bool IsOverrideFlag { get; }
    private bool IsFinalFlag { get; }
    private bool IsAbstractFlag { get; }
    /// <summary>The source method this was derived from (lambda bodies, state machines): used for debug info.</summary>
    public MethodSymbol? SourceMethod { get; init; }
    public SynthesizedBodyKind SynthesizedKind { get; init; }
    public Symbol? Associated { get; init; }
    public ImmutableArray<MethodSymbol> Overrides { get; init; } = ImmutableArray<MethodSymbol>.Empty;

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
    public override Symbol? AssociatedSymbol => Associated;
    internal void SetReturnType(TypeSymbol t) => _returnType = t;
    internal void SetParameters(ImmutableArray<ParameterSymbol> p) => _params = p;
    internal void SetTypeParameters(ImmutableArray<TypeParameterSymbol> tps) => _typeParams = tps;
}

/// <summary>A compiler-generated class or struct (closure environments, state machines, ...).</summary>
public sealed class SynthesizedNamedType : NamedTypeSymbol
{
    private readonly List<Symbol> _members = new();
    private readonly Symbol? _container;
    private NamedTypeSymbol? _baseType;
    private ImmutableArray<NamedTypeSymbol> _interfaces = ImmutableArray<NamedTypeSymbol>.Empty;

    public SynthesizedNamedType(string name, Symbol? container, TypeKind kind, Accessibility accessibility = Accessibility.Private,
        NamedTypeSymbol? baseType = null, ImmutableArray<TypeParameterSymbol> typeParameters = default)
    {
        Name_ = name;
        _container = container;
        TypeKind_ = kind;
        Accessibility_ = accessibility;
        _baseType = baseType;
        TypeParameters_ = typeParameters.IsDefault ? ImmutableArray<TypeParameterSymbol>.Empty : typeParameters;
    }

    private string Name_ { get; }
    private TypeKind TypeKind_ { get; }
    private Accessibility Accessibility_ { get; }
    private ImmutableArray<TypeParameterSymbol> TypeParameters_ { get; }
    public bool IsSealedFlag { get; init; } = true;
    public bool IsAbstractFlag { get; init; }
    public bool IsStaticFlag { get; init; }
    public bool IsCompilerGenerated => true;
    public IReadOnlyList<Symbol> Members => _members;

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
    public override int OwnArity => TypeParameters_.Length;

    public void AddMember(Symbol m) => _members.Add(m);
    public void AddInterface(NamedTypeSymbol i) => _interfaces = _interfaces.Add(i);
    public void SetBase(NamedTypeSymbol b) => _baseType = b;
    public override ImmutableArray<Symbol> GetMembers() => _members.ToImmutableArray();
}
