using System.Collections.Immutable;
using System.Text;

namespace Caladesi.Compiler.Symbols;

public abstract class NamedTypeSymbol : TypeSymbol
{
    public override SymbolKind Kind => SymbolKind.NamedType;

    /// <summary>All type parameters including those of enclosing generic types (metadata layout).</summary>
    public abstract ImmutableArray<TypeParameterSymbol> TypeParameters { get; }
    public abstract ImmutableArray<TypeSymbol> TypeArguments { get; }

    public int Arity => TypeParameters.Length;
    public bool IsGenericType => Arity > 0;

    public virtual int OwnArity
    {
        get
        {
            var outer = ContainingType?.Arity ?? 0;
            return Math.Max(0, Arity - outer);
        }
    }

    public ImmutableArray<TypeSymbol> OwnTypeArguments
    {
        get
        {
            var skip = Arity - OwnArity;
            return skip == 0 ? TypeArguments : TypeArguments.RemoveRange(0, skip);
        }
    }

    public bool IsDefinition => ReferenceEquals(OriginalDefinition, this);
    public new virtual NamedTypeSymbol OriginalDefinition => this;
    public virtual NamedTypeSymbol ConstructedFrom => this;
    public override Symbol? ContainingSymbol => null;

    public virtual bool IsRecord => false;
    public virtual bool IsUnionType => false;
    public virtual bool IsAnonymousUnion => false;
    public virtual ImmutableArray<TypeSymbol> UnionCases => ImmutableArray<TypeSymbol>.Empty;
    public virtual bool IsTuple => false;
    public virtual ImmutableArray<string?> TupleElementNames => default;
    public virtual NamedTypeSymbol? EnumUnderlyingType => null;
    public virtual bool IsStaticClass => false;
    public virtual bool IsReadOnlyStruct => false;
    public virtual MethodSymbol? DelegateInvokeMethod => null;

    public override bool IsReferenceType => TypeKind is TypeKind.Class or TypeKind.Interface or TypeKind.Delegate or TypeKind.Error;
    public override bool IsValueType => TypeKind is TypeKind.Struct or TypeKind.Enum;

    public ImmutableArray<NamedTypeSymbol> GetTypeMembers() =>
        GetMembers().OfType<NamedTypeSymbol>().ToImmutableArray();

    public ImmutableArray<MethodSymbol> InstanceConstructors =>
        GetMembers().OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.Constructor).ToImmutableArray();

    public ImmutableArray<MethodSymbol> StaticConstructors =>
        GetMembers().OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.StaticConstructor).ToImmutableArray();

    public NamedTypeSymbol Construct(ImmutableArray<TypeSymbol> typeArguments)
    {
        if (typeArguments.Length != Arity) throw new ArgumentException("wrong number of type arguments");
        var def = OriginalDefinition;
        // Constructing with the type's own parameters yields the definition itself.
        var same = true;
        for (var i = 0; i < typeArguments.Length; i++)
            if (!ReferenceEquals(typeArguments[i], def.TypeParameters[i])) { same = false; break; }
        if (same) return def;
        return new ConstructedNamedType(def, typeArguments);
    }

    public NamedTypeSymbol Construct(params TypeSymbol[] typeArguments) => Construct(typeArguments.ToImmutableArray());

    public override string ToMetadataString()
    {
        var sb = new StringBuilder();
        var nt = OriginalDefinition;
        AppendMetadataName(sb, nt);
        return sb.ToString();
    }

    private static void AppendMetadataName(StringBuilder sb, NamedTypeSymbol nt)
    {
        if (nt.ContainingType != null)
        {
            AppendMetadataName(sb, nt.ContainingType.OriginalDefinition);
            sb.Append('+');
        }
        else if (nt.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            sb.Append(ns.ToDisplayString()).Append('.');
        }
        sb.Append(nt.MetadataName);
    }

    public override string MetadataName
    {
        get
        {
            var own = OwnArity;
            return own > 0 ? Name + "`" + own : Name;
        }
    }

    /// <summary>Gets the fully-qualified namespace-and-type name without arity decoration, for diagnostics.</summary>
    public string QualifiedName
    {
        get
        {
            var sb = new StringBuilder();
            if (ContainingType != null) sb.Append(ContainingType.QualifiedName).Append('.');
            else if (ContainingNamespace is { IsGlobalNamespace: false } ns) sb.Append(ns.ToDisplayString()).Append('.');
            sb.Append(Name);
            return sb.ToString();
        }
    }

    public override bool Equals(TypeSymbol? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is not NamedTypeSymbol o) return false;
        if (IsDefinition && o.IsDefinition) return false;
        if (!ReferenceEquals(OriginalDefinition, o.OriginalDefinition)) return false;
        var a = TypeArguments;
        var b = o.TypeArguments;
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(OriginalDefinition);
        foreach (var a in TypeArguments) h = h * 31 + a.GetHashCode();
        return h;
    }
}

/// <summary>A generic type instantiated over type arguments; members are substituted lazily.</summary>
public sealed class ConstructedNamedType : NamedTypeSymbol
{
    private readonly NamedTypeSymbol _definition;
    private readonly ImmutableArray<TypeSymbol> _typeArguments;
    private readonly TypeMap _map;
    private ImmutableArray<Symbol> _members;
    private Dictionary<string, ImmutableArray<Symbol>>? _byName;
    private NamedTypeSymbol? _base;
    private bool _baseResolved;
    private ImmutableArray<NamedTypeSymbol> _interfaces;
    private ImmutableArray<string?> _tupleNames;

    public ConstructedNamedType(NamedTypeSymbol definition, ImmutableArray<TypeSymbol> typeArguments, ImmutableArray<string?> tupleNames = default)
    {
        _definition = definition;
        _typeArguments = typeArguments;
        _map = new TypeMap(definition.TypeParameters, typeArguments);
        _tupleNames = tupleNames;
    }

    public TypeMap Map => _map;
    public override NamedTypeSymbol OriginalDefinition => _definition;
    public override Symbol? ContainingSymbol => _definition.ContainingSymbol;
    public override string Name => _definition.Name;
    public override string MetadataName => _definition.MetadataName;
    public override int OwnArity => _definition.OwnArity;
    public override ImmutableArray<TypeParameterSymbol> TypeParameters => _definition.TypeParameters;
    public override ImmutableArray<TypeSymbol> TypeArguments => _typeArguments;
    public override TypeKind TypeKind => _definition.TypeKind;
    public override SpecialType SpecialType => _definition.SpecialType;
    public override Accessibility DeclaredAccessibility => _definition.DeclaredAccessibility;
    public override bool IsAbstract => _definition.IsAbstract;
    public override bool IsSealed => _definition.IsSealed;
    public override bool IsStatic => _definition.IsStatic;
    public override bool IsRecord => _definition.IsRecord;
    public override bool IsReadOnlyStruct => _definition.IsReadOnlyStruct;
    public override bool IsUnionType => _definition.IsUnionType;
    public override bool IsAnonymousUnion => _definition.IsAnonymousUnion;
    public override bool IsFromSource => _definition.IsFromSource;
    public override bool IsTuple => !_tupleNames.IsDefault || _definition.SpecialType == SpecialType.ValueTuple || _definition.ToMetadataString().StartsWith("System.ValueTuple`", StringComparison.Ordinal);
    public override ImmutableArray<string?> TupleElementNames => _tupleNames;
    public override ImmutableArray<AttributeData> GetAttributes() => _definition.GetAttributes();
    public override ImmutableArray<SourceLocation> Locations => _definition.Locations;

    public override ImmutableArray<TypeSymbol> UnionCases
    {
        get
        {
            if (!_definition.IsUnionType) return ImmutableArray<TypeSymbol>.Empty;
            var b = ImmutableArray.CreateBuilder<TypeSymbol>();
            foreach (var c in _definition.UnionCases) b.Add(_map.Substitute(c));
            return b.ToImmutable();
        }
    }

    public override NamedTypeSymbol? EnumUnderlyingType => _definition.EnumUnderlyingType;

    public override NamedTypeSymbol? BaseType
    {
        get
        {
            if (!_baseResolved)
            {
                var b = _definition.BaseType;
                _base = b == null ? null : _map.Substitute(b) as NamedTypeSymbol;
                _baseResolved = true;
            }
            return _base;
        }
    }

    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            if (_interfaces.IsDefault)
                _interfaces = _definition.Interfaces.Select(i => (NamedTypeSymbol)_map.Substitute(i)).ToImmutableArray();
            return _interfaces;
        }
    }

    public override MethodSymbol? DelegateInvokeMethod
    {
        get
        {
            if (_definition.DelegateInvokeMethod == null) return null;
            return GetMembers("Invoke").OfType<MethodSymbol>().FirstOrDefault();
        }
    }

    public override ImmutableArray<Symbol> GetMembers()
    {
        if (_members.IsDefault)
        {
            var b = ImmutableArray.CreateBuilder<Symbol>();
            foreach (var m in _definition.GetMembers())
            {
                switch (m)
                {
                    case MethodSymbol ms: b.Add(new SubstitutedMethodSymbol(this, ms, _map)); break;
                    case FieldSymbol fs: b.Add(new SubstitutedFieldSymbol(this, fs, _map)); break;
                    case PropertySymbol ps: b.Add(new SubstitutedPropertySymbol(this, ps, _map)); break;
                    default: b.Add(m); break;
                }
            }
            _members = b.ToImmutable();
            // link accessors to substituted properties
            foreach (var p in _members.OfType<SubstitutedPropertySymbol>()) p.Link(_members);
        }
        return _members;
    }

    public override ImmutableArray<Symbol> GetMembers(string name)
    {
        if (_byName == null)
        {
            var d = new Dictionary<string, ImmutableArray<Symbol>>();
            foreach (var g in GetMembers().GroupBy(m => m.Name)) d[g.Key] = g.ToImmutableArray();
            _byName = d;
        }
        return _byName.TryGetValue(name, out var r) ? r : ImmutableArray<Symbol>.Empty;
    }
}
