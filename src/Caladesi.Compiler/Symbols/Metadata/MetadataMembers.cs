using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

namespace Caladesi.Compiler.Symbols.Metadata;

internal static class Nullability
{
    public static TypeSymbol Apply(TypeSymbol t, byte flag)
    {
        if (flag != 2) return t;
        if (t is NullableTypeSymbol || t.IsVoid || t.IsErrorType) return t;
        if (t.IsReferenceType || t is TypeParameterSymbol { HasValueTypeConstraint: false })
            return new NullableTypeSymbol(t);
        return t;
    }
}

public sealed class MetadataMethodSymbol : MethodSymbol
{
    private readonly MetadataNamedType _container;
    private readonly MethodDefinitionHandle _handle;
    private readonly MethodDefinition _def;
    private readonly string _name;
    private ImmutableArray<TypeParameterSymbol> _typeParams;
    private bool _sigDone;
    private TypeSymbol _returnType = null!;
    private ImmutableArray<ParameterSymbol> _params;
    private bool _unsupported;
    private bool _initOnly;
    private bool _returnsByRef;
    private bool? _isExtension;
    private ImmutableArray<AttributeData> _attributes;
    private Symbol? _associated;
    private MethodKind _kind;

    internal MetadataMethodSymbol(MetadataNamedType container, MethodDefinitionHandle handle)
    {
        _container = container;
        _handle = handle;
        _def = container.Reader.GetMethodDefinition(handle);
        _name = container.Reader.GetString(_def.Name);
        var special = (_def.Attributes & MethodAttributes.SpecialName) != 0;
        var rtSpecial = (_def.Attributes & MethodAttributes.RTSpecialName) != 0;
        if (_name == ".ctor") _kind = MethodKind.Constructor;
        else if (_name == ".cctor") _kind = MethodKind.StaticConstructor;
        else if (special && _name.StartsWith("get_", StringComparison.Ordinal)) _kind = MethodKind.PropertyGet;
        else if (special && _name.StartsWith("set_", StringComparison.Ordinal)) _kind = MethodKind.PropertySet;
        else if (special && (_name == "op_Implicit" || _name == "op_Explicit")) _kind = MethodKind.Conversion;
        else if (special && _name.StartsWith("op_", StringComparison.Ordinal)) _kind = MethodKind.UserDefinedOperator;
        else if (special && _name.StartsWith("add_", StringComparison.Ordinal)) _kind = MethodKind.EventAdd;
        else if (special && _name.StartsWith("remove_", StringComparison.Ordinal)) _kind = MethodKind.EventRemove;
        else if (_name == "Invoke" && container.TypeKind == TypeKind.Delegate) _kind = MethodKind.DelegateInvoke;
        else _kind = MethodKind.Ordinary;
        _ = rtSpecial;
    }

    internal bool IsUnsupported
    {
        get
        {
            EnsureSignature();
            return _unsupported;
        }
    }

    internal MethodDefinitionHandle Handle => _handle;
    public override string Name => _name;
    public override Symbol? ContainingSymbol => _container;
    public override MethodKind MethodKind => _kind;
    public override bool IsStatic => (_def.Attributes & MethodAttributes.Static) != 0;
    public override bool IsAbstract => (_def.Attributes & MethodAttributes.Abstract) != 0;
    public override bool IsVirtual => (_def.Attributes & MethodAttributes.Virtual) != 0 && (_def.Attributes & MethodAttributes.Final) == 0 && (_def.Attributes & MethodAttributes.Abstract) == 0;
    public override bool IsOverride => (_def.Attributes & MethodAttributes.Virtual) != 0 && (_def.Attributes & MethodAttributes.NewSlot) == 0;
    public override bool IsSealed => (_def.Attributes & MethodAttributes.Final) != 0;
    public override Symbol? AssociatedSymbol => _associated;
    internal void SetAssociated(Symbol s) => _associated = s;

    public override Accessibility DeclaredAccessibility => (_def.Attributes & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => Accessibility.Public,
        MethodAttributes.Family => Accessibility.Protected,
        MethodAttributes.FamORAssem => Accessibility.ProtectedOrInternal,
        MethodAttributes.FamANDAssem => Accessibility.ProtectedAndInternal,
        MethodAttributes.Assembly => Accessibility.Internal,
        _ => Accessibility.Private,
    };

    public override bool IsInitOnly { get { EnsureSignature(); return _initOnly; } }
    public override bool ReturnsByRef { get { EnsureSignature(); return _returnsByRef; } }

    public override bool IsExtensionMethod =>
        _isExtension ??= IsStatic && _def.GetCustomAttributes().Count > 0 &&
                         MetadataHelpers.FindAttribute(_container.Reader, _def.GetCustomAttributes(), "System.Runtime.CompilerServices", "ExtensionAttribute") != null;

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_typeParams.IsDefault)
            {
                var gps = _def.GetGenericParameters();
                if (gps.Count == 0) { _typeParams = ImmutableArray<TypeParameterSymbol>.Empty; return _typeParams; }
                var r = _container.Reader;
                var b = ImmutableArray.CreateBuilder<TypeParameterSymbol>(gps.Count);
                foreach (var gh in gps)
                {
                    var gp = r.GetGenericParameter(gh);
                    var variance = VarianceKind.None;
                    var handle = gh;
                    b.Add(new TypeParameterSymbol(r.GetString(gp.Name), gp.Index, this, variance, tp =>
                        _container.DecodeConstraints(handle, new GenericContext(_container.TypeParameters, _typeParams))));
                }
                _typeParams = b.MoveToImmutable();
            }
            return _typeParams;
        }
    }

    public override TypeSymbol ReturnType { get { EnsureSignature(); return _returnType; } }
    public override ImmutableArray<ParameterSymbol> Parameters { get { EnsureSignature(); return _params; } }

    private byte NullableFlagOf(CustomAttributeHandleCollection attrs, byte fallback)
    {
        if (attrs.Count == 0) return fallback;
        var ca = MetadataHelpers.FindAttribute(_container.Reader, attrs, "System.Runtime.CompilerServices", "NullableAttribute");
        return ca == null ? fallback : MetadataHelpers.ReadNullableFlag(_container.Reader, ca.Value.Value);
    }

    private void EnsureSignature()
    {
        if (_sigDone) return;
        _sigDone = true;
        var r = _container.Reader;
        MetadataTypeProvider.SawUnsupported = false;
        MetadataTypeProvider.SawIsExternalInit = false;
        var ctx = new GenericContext(_container.TypeParameters, TypeParameters);
        MethodSignature<TypeSymbol> sig;
        try { sig = _def.DecodeSignature(_container.Assembly.Provider, ctx); }
        catch (BadImageFormatException)
        {
            _unsupported = true;
            _returnType = ErrorTypeSymbol.Instance;
            _params = ImmutableArray<ParameterSymbol>.Empty;
            return;
        }
        _initOnly = MetadataTypeProvider.SawIsExternalInit;
        _unsupported = MetadataTypeProvider.SawUnsupported;

        // nullable context
        byte ctxFlag = _container.NullableContext;
        var ma = MetadataHelpers.FindAttribute(r, _def.GetCustomAttributes(), "System.Runtime.CompilerServices", "NullableContextAttribute");
        if (ma != null) ctxFlag = MetadataHelpers.ReadByteArg(r, ma.Value.Value);

        // parameter rows
        var paramRows = new Dictionary<int, Parameter>();
        foreach (var ph in _def.GetParameters())
        {
            var p = r.GetParameter(ph);
            paramRows[p.SequenceNumber] = p;
        }

        var rt = sig.ReturnType;
        if (rt is ByRefTypeSymbol rb) { _returnsByRef = true; rt = rb.Element; }
        byte retFlag = ctxFlag;
        if (paramRows.TryGetValue(0, out var retRow)) retFlag = NullableFlagOf(retRow.GetCustomAttributes(), ctxFlag);
        _returnType = Nullability.Apply(rt, retFlag);

        var isExt = IsExtensionMethod;
        var b = ImmutableArray.CreateBuilder<ParameterSymbol>(sig.ParameterTypes.Length);
        for (var i = 0; i < sig.ParameterTypes.Length; i++)
        {
            var pt = sig.ParameterTypes[i];
            var refKind = RefKind.None;
            var name = "arg" + i;
            var isParams = false;
            ConstantValue? def = null;
            var optional = false;
            byte flag = ctxFlag;
            if (pt is ByRefTypeSymbol br) { refKind = RefKind.Ref; pt = br.Element; }
            if (paramRows.TryGetValue(i + 1, out var row))
            {
                name = r.GetString(row.Name);
                if ((row.Attributes & ParameterAttributes.Out) != 0 && refKind != RefKind.None) refKind = RefKind.Out;
                else if ((row.Attributes & ParameterAttributes.In) != 0 && refKind != RefKind.None) refKind = RefKind.In;
                optional = (row.Attributes & ParameterAttributes.Optional) != 0;
                if ((row.Attributes & ParameterAttributes.HasDefault) != 0)
                {
                    var dh = row.GetDefaultValue();
                    if (!dh.IsNil) def = ReadConstant(r, dh, pt);
                }
                var pa = row.GetCustomAttributes();
                if (pa.Count > 0)
                {
                    isParams = MetadataHelpers.FindAttribute(r, pa, "System", "ParamArrayAttribute") != null;
                    flag = NullableFlagOf(pa, ctxFlag);
                }
            }
            pt = Nullability.Apply(pt, flag);
            if (isParams) pt = pt.StripNullable();
            if (optional && def == null && !pt.IsValueType) def = ConstantValue.Null;
            b.Add(new ParameterSymbol(name, pt, i, this, refKind, isParams, def, optional, isThis: isExt && i == 0));
        }
        _params = b.MoveToImmutable();
    }

    internal static ConstantValue? ReadConstant(MetadataReader r, ConstantHandle dh, TypeSymbol type)
    {
        var c = r.GetConstant(dh);
        var br = r.GetBlobReader(c.Value);
        object? v = c.TypeCode switch
        {
            ConstantTypeCode.Boolean => br.ReadBoolean(),
            ConstantTypeCode.Char => br.ReadChar(),
            ConstantTypeCode.SByte => br.ReadSByte(),
            ConstantTypeCode.Byte => br.ReadByte(),
            ConstantTypeCode.Int16 => br.ReadInt16(),
            ConstantTypeCode.UInt16 => br.ReadUInt16(),
            ConstantTypeCode.Int32 => br.ReadInt32(),
            ConstantTypeCode.UInt32 => br.ReadUInt32(),
            ConstantTypeCode.Int64 => br.ReadInt64(),
            ConstantTypeCode.UInt64 => br.ReadUInt64(),
            ConstantTypeCode.Single => br.ReadSingle(),
            ConstantTypeCode.Double => br.ReadDouble(),
            ConstantTypeCode.String => br.ReadUTF16(br.RemainingBytes),
            ConstantTypeCode.NullReference => null,
            _ => null,
        };
        return new ConstantValue(v);
    }

    public override ImmutableArray<AttributeData> GetAttributes()
    {
        if (_attributes.IsDefault) _attributes = MetadataHelpers.DecodeAttributes(_container.Assembly, _def.GetCustomAttributes());
        return _attributes;
    }
}

public sealed class MetadataFieldSymbol : FieldSymbol
{
    private readonly MetadataNamedType _container;
    private readonly FieldDefinition _def;
    private readonly string _name;
    private TypeSymbol? _type;
    private bool _unsupported;
    private ConstantValue? _const;
    private bool _constDone;
    private ImmutableArray<AttributeData> _attributes;

    internal MetadataFieldSymbol(MetadataNamedType container, FieldDefinitionHandle handle)
    {
        _container = container;
        _def = container.Reader.GetFieldDefinition(handle);
        _name = container.Reader.GetString(_def.Name);
        _ = TypeCore;
    }

    internal bool IsUnsupported => _unsupported;

    private TypeSymbol TypeCore
    {
        get
        {
            if (_type != null) return _type;
            MetadataTypeProvider.SawUnsupported = false;
            try { _type = _def.DecodeSignature(_container.Assembly.Provider, _container.Context); }
            catch (BadImageFormatException) { _type = ErrorTypeSymbol.Instance; _unsupported = true; }
            if (MetadataTypeProvider.SawUnsupported) _unsupported = true;
            byte flag = _container.NullableContext;
            var attrs = _def.GetCustomAttributes();
            if (attrs.Count > 0)
            {
                var ca = MetadataHelpers.FindAttribute(_container.Reader, attrs, "System.Runtime.CompilerServices", "NullableAttribute");
                if (ca != null) flag = MetadataHelpers.ReadNullableFlag(_container.Reader, ca.Value.Value);
            }
            _type = Nullability.Apply(_type, flag);
            return _type;
        }
    }

    public override string Name => _name;
    public override Symbol? ContainingSymbol => _container;
    public override TypeSymbol Type => TypeCore;
    public override bool IsStatic => (_def.Attributes & FieldAttributes.Static) != 0;
    public override bool IsConst => (_def.Attributes & FieldAttributes.Literal) != 0;
    public override bool IsReadOnly => (_def.Attributes & FieldAttributes.InitOnly) != 0;
    public override Accessibility DeclaredAccessibility => (_def.Attributes & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => Accessibility.Public,
        FieldAttributes.Family => Accessibility.Protected,
        FieldAttributes.FamORAssem => Accessibility.ProtectedOrInternal,
        FieldAttributes.Assembly => Accessibility.Internal,
        _ => Accessibility.Private,
    };

    public override ConstantValue? ConstantValue
    {
        get
        {
            if (!IsConst) return null;
            if (!_constDone)
            {
                _constDone = true;
                var dh = _def.GetDefaultValue();
                if (!dh.IsNil) _const = MetadataMethodSymbol.ReadConstant(_container.Reader, dh, Type);
            }
            return _const;
        }
    }

    public override ImmutableArray<AttributeData> GetAttributes()
    {
        if (_attributes.IsDefault) _attributes = MetadataHelpers.DecodeAttributes(_container.Assembly, _def.GetCustomAttributes());
        return _attributes;
    }
}

public sealed class MetadataPropertySymbol : PropertySymbol
{
    private readonly MetadataNamedType _container;
    private readonly PropertyDefinition _def;
    private readonly string _name;
    private readonly MetadataMethodSymbol? _get;
    private readonly MetadataMethodSymbol? _set;
    private ImmutableArray<ParameterSymbol> _params;
    private ImmutableArray<AttributeData> _attributes;

    internal MetadataPropertySymbol(MetadataNamedType container, PropertyDefinitionHandle handle,
        Dictionary<MethodDefinitionHandle, MetadataMethodSymbol> methods)
    {
        _container = container;
        _def = container.Reader.GetPropertyDefinition(handle);
        _name = container.Reader.GetString(_def.Name);
        var acc = _def.GetAccessors();
        if (!acc.Getter.IsNil && methods.TryGetValue(acc.Getter, out var g)) { _get = g; g.SetAssociated(this); }
        if (!acc.Setter.IsNil && methods.TryGetValue(acc.Setter, out var s)) { _set = s; s.SetAssociated(this); }
    }

    internal bool IsUnsupported => false;
    public override string Name => _name;
    public override Symbol? ContainingSymbol => _container;
    public override MethodSymbol? GetMethod => _get;
    public override MethodSymbol? SetMethod => _set;
    public override bool IsStatic => (_get ?? _set)!.IsStatic;
    public override bool IsAbstract => (_get ?? _set)!.IsAbstract;
    public override bool IsVirtual => (_get ?? _set)!.IsVirtual;
    public override bool IsOverride => (_get ?? _set)!.IsOverride;
    public override bool IsSealed => (_get ?? _set)!.IsSealed;

    public override Accessibility DeclaredAccessibility
    {
        get
        {
            var a = _get?.DeclaredAccessibility ?? Accessibility.Private;
            var b = _set?.DeclaredAccessibility ?? Accessibility.Private;
            return MoreAccessible(a, b);
        }
    }

    private static Accessibility MoreAccessible(Accessibility a, Accessibility b)
    {
        static int Rank(Accessibility x) => x switch
        {
            Accessibility.Public => 5, Accessibility.ProtectedOrInternal => 4, Accessibility.Protected => 3,
            Accessibility.Internal => 2, Accessibility.ProtectedAndInternal => 1, _ => 0,
        };
        return Rank(a) >= Rank(b) ? a : b;
    }

    public override TypeSymbol Type => _get != null ? _get.ReturnType
        : _set != null && _set.Parameters.Length > 0 ? _set.Parameters[^1].Type : ErrorTypeSymbol.Instance;

    public override ImmutableArray<ParameterSymbol> Parameters
    {
        get
        {
            if (_params.IsDefault)
            {
                if (_get != null) _params = _get.Parameters.Select((p, i) => p.WithType(p.Type, this)).ToImmutableArray();
                else if (_set != null) _params = _set.Parameters.Take(_set.Parameters.Length - 1).Select(p => p.WithType(p.Type, this)).ToImmutableArray();
                else _params = ImmutableArray<ParameterSymbol>.Empty;
            }
            return _params;
        }
    }

    public override ImmutableArray<AttributeData> GetAttributes()
    {
        if (_attributes.IsDefault) _attributes = MetadataHelpers.DecodeAttributes(_container.Assembly, _def.GetCustomAttributes());
        return _attributes;
    }
}
