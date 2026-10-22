using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

namespace Caladesi.Compiler.Symbols.Metadata;

public sealed class MetadataNamedType : NamedTypeSymbol
{
    private static readonly Dictionary<string, SpecialType> s_special = BuildSpecial();

    private static Dictionary<string, SpecialType> BuildSpecial()
    {
        var d = new Dictionary<string, SpecialType>(StringComparer.Ordinal);
        foreach (var t in Enum.GetValues<SpecialType>())
        {
            var n = t.MetadataName();
            if (n != null) d[n] = t;
        }
        return d;
    }

    private readonly MetadataAssemblySymbol _assembly;
    private readonly TypeDefinitionHandle _handle;
    private readonly TypeDefinition _def;
    private readonly string _name;
    private readonly string _namespace;
    private readonly int _ownArity;
    private readonly SpecialType _special;
    private ImmutableArray<TypeParameterSymbol> _typeParams;
    private ImmutableArray<Symbol> _members;
    private Dictionary<string, ImmutableArray<Symbol>>? _byName;
    private NamedTypeSymbol? _base;
    private bool _baseDone;
    private ImmutableArray<NamedTypeSymbol> _interfaces;
    private TypeKind? _kind;
    private ImmutableArray<AttributeData> _attributes;
    private NamedTypeSymbol? _enumUnderlying;
    private bool _enumDone;
    private ImmutableArray<TypeSymbol> _unionCases;
    private byte? _nullableContext;

    internal MetadataNamedType(MetadataAssemblySymbol assembly, TypeDefinitionHandle handle)
    {
        _assembly = assembly;
        _handle = handle;
        var r = assembly.Reader;
        _def = r.GetTypeDefinition(handle);
        var raw = r.GetString(_def.Name);
        _namespace = r.GetString(_def.Namespace);
        var tick = raw.IndexOf('`');
        if (tick >= 0)
        {
            _name = raw[..tick];
            _ownArity = int.Parse(raw.AsSpan(tick + 1), System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            _name = raw;
            _ownArity = 0;
        }
        var declaring = _def.GetDeclaringType();
        var full = _namespace.Length == 0 ? raw : _namespace + "." + raw;
        if (declaring.IsNil && s_special.TryGetValue(full, out var st)) _special = st;
    }

    internal MetadataAssemblySymbol Assembly => _assembly;
    internal TypeDefinitionHandle Handle => _handle;
    internal MetadataReader Reader => _assembly.Reader;

    internal bool IsByRefLike => TypeKind == TypeKind.Struct
        && MetadataHelpers.FindAttribute(Reader, _def.GetCustomAttributes(), "System.Runtime.CompilerServices", "IsByRefLikeAttribute") != null;

    public override string Name => _name;
    public override int OwnArity => _ownArity;
    public string Namespace => _namespace;
    public override AssemblySymbol? ContainingAssembly => _assembly;
    public override SpecialType SpecialType => _special;

    public override Symbol? ContainingSymbol
    {
        get
        {
            var d = _def.GetDeclaringType();
            if (!d.IsNil) return _assembly.GetTypeDefinition(d);
            return _assembly.ReferenceSet.GlobalNamespace.FindNamespace(_namespace);
        }
    }

    public override Accessibility DeclaredAccessibility => (_def.Attributes & TypeAttributes.VisibilityMask) switch
    {
        TypeAttributes.Public or TypeAttributes.NestedPublic => Accessibility.Public,
        TypeAttributes.NestedFamily => Accessibility.Protected,
        TypeAttributes.NestedFamORAssem => Accessibility.ProtectedOrInternal,
        TypeAttributes.NestedFamANDAssem => Accessibility.ProtectedAndInternal,
        TypeAttributes.NestedAssembly or TypeAttributes.NotPublic => Accessibility.Internal,
        _ => Accessibility.Private,
    };

    public override bool IsAbstract => (_def.Attributes & TypeAttributes.Abstract) != 0 && (_def.Attributes & TypeAttributes.Sealed) == 0;
    public override bool IsSealed => (_def.Attributes & TypeAttributes.Sealed) != 0;
    public override bool IsStatic => (_def.Attributes & TypeAttributes.Abstract) != 0 && (_def.Attributes & TypeAttributes.Sealed) != 0;
    public override bool IsStaticClass => IsStatic;

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_typeParams.IsDefault)
            {
                var gps = _def.GetGenericParameters();
                if (gps.Count == 0) { _typeParams = ImmutableArray<TypeParameterSymbol>.Empty; return _typeParams; }
                var b = ImmutableArray.CreateBuilder<TypeParameterSymbol>(gps.Count);
                foreach (var gh in gps)
                {
                    var gp = Reader.GetGenericParameter(gh);
                    var attrs = gp.Attributes;
                    var variance = (attrs & GenericParameterAttributes.VarianceMask) switch
                    {
                        GenericParameterAttributes.Covariant => VarianceKind.Out,
                        GenericParameterAttributes.Contravariant => VarianceKind.In,
                        _ => VarianceKind.None,
                    };
                    var handle = gh;
                    b.Add(new TypeParameterSymbol(Reader.GetString(gp.Name), gp.Index, this, variance, tp => DecodeConstraints(handle, new GenericContext(_typeParams, ImmutableArray<TypeParameterSymbol>.Empty))));
                }
                _typeParams = b.MoveToImmutable();
            }
            return _typeParams;
        }
    }

    internal TypeParameterConstraints DecodeConstraints(GenericParameterHandle handle, GenericContext ctx)
    {
        var gp = Reader.GetGenericParameter(handle);
        var attrs = gp.Attributes;
        var types = ImmutableArray.CreateBuilder<TypeSymbol>();
        foreach (var ch in gp.GetConstraints())
        {
            var c = Reader.GetGenericParameterConstraint(ch);
            var t = DecodeTypeHandle(c.Type, ctx);
            if (t.SpecialType == SpecialType.ValueType) continue; // implied by the struct constraint
            types.Add(t);
        }
        return new TypeParameterConstraints(
            (attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0,
            (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0,
            (attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0,
            false,
            types.ToImmutable());
    }

    internal TypeSymbol DecodeTypeHandle(EntityHandle h, GenericContext ctx) => h.Kind switch
    {
        HandleKind.TypeDefinition => _assembly.GetTypeDefinition((TypeDefinitionHandle)h),
        HandleKind.TypeReference => _assembly.ResolveTypeReference((TypeReferenceHandle)h),
        HandleKind.TypeSpecification => Reader.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(_assembly.Provider, ctx),
        _ => new ErrorTypeSymbol("?"),
    };

    internal GenericContext Context => new(TypeParameters, ImmutableArray<TypeParameterSymbol>.Empty);

    public override ImmutableArray<TypeSymbol> TypeArguments => TypeParameters.CastArray<TypeSymbol>();

    public override TypeKind TypeKind
    {
        get
        {
            if (_kind.HasValue) return _kind.Value;
            TypeKind k;
            if ((_def.Attributes & TypeAttributes.ClassSemanticsMask) == TypeAttributes.Interface) k = TypeKind.Interface;
            else
            {
                var b = BaseTypeNoCycle();
                k = b?.SpecialType switch
                {
                    SpecialType.Enum => TypeKind.Enum,
                    SpecialType.ValueType => TypeKind.Struct,
                    SpecialType.MulticastDelegate => TypeKind.Delegate,
                    _ => TypeKind.Class,
                };
                if (_special is SpecialType.Enum or SpecialType.ValueType) k = TypeKind.Class;
                if (_special == SpecialType.Void) k = TypeKind.Struct;
            }
            _kind = k;
            return k;
        }
    }

    private NamedTypeSymbol? BaseTypeNoCycle() => BaseType;

    public override NamedTypeSymbol? BaseType
    {
        get
        {
            if (!_baseDone)
            {
                _baseDone = true;
                var bh = _def.BaseType;
                if (!bh.IsNil && (_def.Attributes & TypeAttributes.ClassSemanticsMask) != TypeAttributes.Interface)
                    _base = DecodeTypeHandle(bh, Context) as NamedTypeSymbol;
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
                var b = ImmutableArray.CreateBuilder<NamedTypeSymbol>();
                foreach (var ih in _def.GetInterfaceImplementations())
                {
                    var ii = Reader.GetInterfaceImplementation(ih);
                    if (DecodeTypeHandle(ii.Interface, Context) is NamedTypeSymbol n && n is not ErrorTypeSymbol) b.Add(n);
                }
                _interfaces = b.ToImmutable();
            }
            return _interfaces;
        }
    }

    public override NamedTypeSymbol? EnumUnderlyingType
    {
        get
        {
            if (TypeKind != TypeKind.Enum) return null;
            if (!_enumDone)
            {
                _enumDone = true;
                foreach (var fh in _def.GetFields())
                {
                    var f = Reader.GetFieldDefinition(fh);
                    if ((f.Attributes & FieldAttributes.Static) == 0)
                    {
                        _enumUnderlying = f.DecodeSignature(_assembly.Provider, Context) as NamedTypeSymbol;
                        break;
                    }
                }
            }
            return _enumUnderlying;
        }
    }

    public override MethodSymbol? DelegateInvokeMethod =>
        TypeKind == TypeKind.Delegate ? GetMembers("Invoke").OfType<MethodSymbol>().FirstOrDefault() : null;

    public override bool IsReadOnlyStruct => TypeKind == TypeKind.Struct && HasAttributeFast("System.Runtime.CompilerServices", "IsReadOnlyAttribute");
    public override bool IsUnionType => HasAttributeFast("Caladesi.Runtime", "CaladesiUnionAttribute");
    public override bool IsAnonymousUnion => IsUnionType && _namespace == "Caladesi.Runtime" && _name == "Union";
    public override bool IsRecord => TypeKind is TypeKind.Class or TypeKind.Struct && GetMembers("<Clone>$").Length > 0;

    public override ImmutableArray<TypeSymbol> UnionCases
    {
        get
        {
            if (!IsUnionType) return ImmutableArray<TypeSymbol>.Empty;
            if (_unionCases.IsDefault)
            {
                var b = ImmutableArray.CreateBuilder<TypeSymbol>();
                foreach (var c in InstanceConstructors)
                    if (c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 1) b.Add(c.Parameters[0].Type);
                _unionCases = b.ToImmutable();
            }
            return _unionCases;
        }
    }

    internal bool HasAttributeFast(string ns, string name) =>
        MetadataHelpers.FindAttribute(Reader, _def.GetCustomAttributes(), ns, name) != null;

    internal byte NullableContext
    {
        get
        {
            if (_nullableContext.HasValue) return _nullableContext.Value;
            byte v = 0;
            var ca = MetadataHelpers.FindAttribute(Reader, _def.GetCustomAttributes(), "System.Runtime.CompilerServices", "NullableContextAttribute");
            if (ca != null) v = MetadataHelpers.ReadByteArg(Reader, ca.Value.Value);
            else if (_def.GetDeclaringType() is { IsNil: false } d) v = _assembly.GetTypeDefinition(d).NullableContext;
            _nullableContext = v;
            return v;
        }
    }

    public override ImmutableArray<AttributeData> GetAttributes()
    {
        if (_attributes.IsDefault) _attributes = MetadataHelpers.DecodeAttributes(_assembly, _def.GetCustomAttributes());
        return _attributes;
    }

    public override ImmutableArray<Symbol> GetMembers()
    {
        if (!_members.IsDefault) return _members;
        var b = ImmutableArray.CreateBuilder<Symbol>();
        var r = Reader;

        foreach (var nh in _def.GetNestedTypes())
        {
            var n = _assembly.GetTypeDefinition(nh);
            if (n.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
                b.Add(n);
        }
        foreach (var fh in _def.GetFields())
        {
            var f = r.GetFieldDefinition(fh);
            if (!IsVisible(f.Attributes & FieldAttributes.FieldAccessMask)) continue;
            if ((f.Attributes & FieldAttributes.RTSpecialName) != 0) continue;
            var fs = new MetadataFieldSymbol(this, fh);
            if (!fs.IsUnsupported) b.Add(fs);
        }
        var methodMap = new Dictionary<MethodDefinitionHandle, MetadataMethodSymbol>();
        foreach (var mh in _def.GetMethods())
        {
            var m = r.GetMethodDefinition(mh);
            var acc = m.Attributes & MethodAttributes.MemberAccessMask;
            if (!IsVisibleMethod(acc)) continue;
            var ms = new MetadataMethodSymbol(this, mh);
            if (ms.IsUnsupported) continue;
            methodMap[mh] = ms;
            b.Add(ms);
        }
        foreach (var ph in _def.GetProperties())
        {
            var ps = new MetadataPropertySymbol(this, ph, methodMap);
            if (ps.GetMethod == null && ps.SetMethod == null) continue;
            if (ps.IsUnsupported) continue;
            b.Add(ps);
        }
        _members = b.ToImmutable();
        return _members;
    }

    private bool IsVisible(FieldAttributes a) => a is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem;
    private bool IsVisibleMethod(MethodAttributes a) => a is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

    public override ImmutableArray<Symbol> GetMembers(string name)
    {
        if (_byName == null)
        {
            var d = new Dictionary<string, ImmutableArray<Symbol>>(StringComparer.Ordinal);
            foreach (var g in GetMembers().GroupBy(m => m.Name)) d[g.Key] = g.ToImmutableArray();
            _byName = d;
        }
        return _byName.TryGetValue(name, out var r) ? r : ImmutableArray<Symbol>.Empty;
    }
}
