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
            int outer = ContainingType?.Arity ?? 0;
            return Math.Max(0, Arity - outer);
        }
    }

    public ImmutableArray<TypeSymbol> OwnTypeArguments
    {
        get
        {
            int skip = Arity - OwnArity;
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
    public virtual ImmutableArray<TypeSymbol> UnionCases => [];
    public virtual bool IsTuple => false;
    public virtual ImmutableArray<string?> TupleElementNames => default;
    public virtual NamedTypeSymbol? EnumUnderlyingType => null;
    public virtual bool IsStaticClass => false;
    public virtual bool IsReadOnlyStruct => false;
    public virtual MethodSymbol? DelegateInvokeMethod => null;

    public override bool IsReferenceType => TypeKind is TypeKind.Class or TypeKind.Interface or TypeKind.Delegate or TypeKind.Error;
    public override bool IsValueType => TypeKind is TypeKind.Struct or TypeKind.Enum;

    public ImmutableArray<NamedTypeSymbol> GetTypeMembers()
    {
        return [.. GetMembers().OfType<NamedTypeSymbol>()];
    }

    public ImmutableArray<MethodSymbol> InstanceConstructors =>
        [.. GetMembers().OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.Constructor)];

    public ImmutableArray<MethodSymbol> StaticConstructors =>
        [.. GetMembers().OfType<MethodSymbol>().Where(m => m.MethodKind == MethodKind.StaticConstructor)];

    public NamedTypeSymbol Construct(ImmutableArray<TypeSymbol> typeArguments)
    {
        if (typeArguments.Length != Arity)
        {
            throw new ArgumentException("wrong number of type arguments");
        }

        NamedTypeSymbol def = OriginalDefinition;
        // Constructing with the type's own parameters yields the definition itself.
        bool same = true;
        for (int i = 0; i < typeArguments.Length; i++)
        {
            if (!ReferenceEquals(typeArguments[i], def.TypeParameters[i])) { same = false; break; }
        }

        return same ? def : new ConstructedNamedType(def, typeArguments);
    }

    public NamedTypeSymbol Construct(params TypeSymbol[] typeArguments)
    {
        return Construct(typeArguments.ToImmutableArray());
    }

    public override string ToMetadataString()
    {
        StringBuilder sb = new();
        NamedTypeSymbol nt = OriginalDefinition;
        AppendMetadataName(sb, nt);
        return sb.ToString();
    }

    private static void AppendMetadataName(StringBuilder sb, NamedTypeSymbol nt)
    {
        if (nt.ContainingType != null)
        {
            AppendMetadataName(sb, nt.ContainingType.OriginalDefinition);
            _ = sb.Append('+');
        }
        else if (nt.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            _ = sb.Append(ns.ToDisplayString()).Append('.');
        }
        _ = sb.Append(nt.MetadataName);
    }

    public override string MetadataName
    {
        get
        {
            int own = OwnArity;
            return own > 0 ? Name + "`" + own : Name;
        }
    }

    /// <summary>Gets the fully-qualified namespace-and-type name without arity decoration, for diagnostics.</summary>
    public string QualifiedName
    {
        get
        {
            StringBuilder sb = new();
            if (ContainingType != null)
            {
                _ = sb.Append(ContainingType.QualifiedName).Append('.');
            }
            else if (ContainingNamespace is { IsGlobalNamespace: false } ns)
            {
                _ = sb.Append(ns.ToDisplayString()).Append('.');
            }

            _ = sb.Append(Name);
            return sb.ToString();
        }
    }

    public override bool Equals(TypeSymbol? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is not NamedTypeSymbol o)
        {
            return false;
        }

        if (IsDefinition && o.IsDefinition)
        {
            return false;
        }

        if (!ReferenceEquals(OriginalDefinition, o.OriginalDefinition))
        {
            return false;
        }

        ImmutableArray<TypeSymbol> a = TypeArguments;
        ImmutableArray<TypeSymbol> b = o.TypeArguments;
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        int h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(OriginalDefinition);
        foreach (TypeSymbol a in TypeArguments)
        {
            h = (h * 31) + a.GetHashCode();
        }

        return h;
    }
}

/// <summary>A generic type instantiated over type arguments; members are substituted lazily.</summary>
public sealed class ConstructedNamedType(NamedTypeSymbol definition, ImmutableArray<TypeSymbol> typeArguments, ImmutableArray<string?> tupleNames = default) : NamedTypeSymbol
{
    private readonly NamedTypeSymbol _definition = definition;
    private readonly ImmutableArray<TypeSymbol> _typeArguments = typeArguments;
    private ImmutableArray<Symbol> _members;
    private Dictionary<string, ImmutableArray<Symbol>>? _byName;
    private NamedTypeSymbol? _base;
    private bool _baseResolved;
    private ImmutableArray<NamedTypeSymbol> _interfaces;
    private readonly ImmutableArray<string?> _tupleNames = tupleNames;

    public TypeMap Map { get; } = new TypeMap(definition.TypeParameters, typeArguments);
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
    public override ImmutableArray<AttributeData> GetAttributes()
    {
        return _definition.GetAttributes();
    }

    public override ImmutableArray<SourceLocation> Locations => _definition.Locations;

    public override ImmutableArray<TypeSymbol> UnionCases
    {
        get
        {
            if (!_definition.IsUnionType)
            {
                return [];
            }

            ImmutableArray<TypeSymbol>.Builder b = ImmutableArray.CreateBuilder<TypeSymbol>();
            foreach (TypeSymbol c in _definition.UnionCases)
            {
                b.Add(Map.Substitute(c));
            }

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
                NamedTypeSymbol? b = _definition.BaseType;
                _base = b == null ? null : Map.Substitute(b) as NamedTypeSymbol;
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
            {
                _interfaces = [.. _definition.Interfaces.Select(i => (NamedTypeSymbol)Map.Substitute(i))];
            }

            return _interfaces;
        }
    }

    public override MethodSymbol? DelegateInvokeMethod => _definition.DelegateInvokeMethod == null ? null : GetMembers("Invoke").OfType<MethodSymbol>().FirstOrDefault();

    public override ImmutableArray<Symbol> GetMembers()
    {
        if (_members.IsDefault)
        {
            ImmutableArray<Symbol>.Builder b = ImmutableArray.CreateBuilder<Symbol>();
            foreach (Symbol m in _definition.GetMembers())
            {
                switch (m)
                {
                    case MethodSymbol ms: b.Add(new SubstitutedMethodSymbol(this, ms, Map)); break;
                    case FieldSymbol fs: b.Add(new SubstitutedFieldSymbol(this, fs, Map)); break;
                    case PropertySymbol ps: b.Add(new SubstitutedPropertySymbol(this, ps, Map)); break;
                    default: b.Add(m); break;
                }
            }
            _members = b.ToImmutable();
            // link accessors to substituted properties
            foreach (SubstitutedPropertySymbol p in _members.OfType<SubstitutedPropertySymbol>())
            {
                p.Link(_members);
            }
        }
        return _members;
    }

    public override ImmutableArray<Symbol> GetMembers(string name)
    {
        if (_byName == null)
        {
            Dictionary<string, ImmutableArray<Symbol>> d = [];
            foreach (IGrouping<string, Symbol> g in GetMembers().GroupBy(m => m.Name))
            {
                d[g.Key] = [.. g];
            }

            _byName = d;
        }
        return _byName.TryGetValue(name, out ImmutableArray<Symbol> r) ? r : [];
    }
}
