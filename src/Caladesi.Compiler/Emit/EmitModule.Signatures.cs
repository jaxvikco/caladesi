using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>Emits a compilation as an assembly: owns the metadata builder and all handle caches.</summary>
internal sealed partial class EmitModule
{
    private readonly Dictionary<AssemblySymbol, AssemblyReferenceHandle> _assemblyRefs = new();
    private readonly Dictionary<NamedTypeSymbol, EntityHandle> _typeHandles = new();
    private readonly Dictionary<TypeSymbol, TypeSpecificationHandle> _typeSpecs = new();
    private readonly Dictionary<Symbol, EntityHandle> _memberRefs = new();
    private readonly Dictionary<(EntityHandle, string), MethodSpecificationHandle> _methodSpecs = new();

    public Compilation Compilation { get; }
    public MetadataBuilder Metadata { get; } = new();

    /// <summary>Maps type parameters of an emitted generic method to class parameters when a method body is moved into a generic helper class.</summary>
    private Dictionary<TypeParameterSymbol, int>? _classTypeParameterMap;
    internal void SetClassTypeParameterMap(Dictionary<TypeParameterSymbol, int>? map) => _classTypeParameterMap = map;

    public EmitModule(Compilation compilation) => Compilation = compilation;

    // ------------------------------------------------------------------ assembly / type references

    private AssemblyReferenceHandle GetAssemblyRef(AssemblySymbol asm)
    {
        if (_assemblyRefs.TryGetValue(asm, out var h)) return h;
        var md = (MetadataAssemblySymbol)asm;
        var reader = md.Reader;
        var def = reader.GetAssemblyDefinition();
        var name = reader.GetString(def.Name);
        var culture = def.Culture.IsNil ? null : reader.GetString(def.Culture);
        var keyBlob = def.PublicKey.IsNil ? Array.Empty<byte>() : reader.GetBlobBytes(def.PublicKey);
        BlobHandle token = default;
        if (keyBlob.Length > 0)
        {
            var hash = SHA1.HashData(keyBlob);
            var tokenBytes = new byte[8];
            for (var i = 0; i < 8; i++) tokenBytes[i] = hash[hash.Length - 1 - i];
            token = Metadata.GetOrAddBlob(tokenBytes);
        }
        h = Metadata.AddAssemblyReference(Metadata.GetOrAddString(name), def.Version, culture == null ? default : Metadata.GetOrAddString(culture), token, 0, default);
        _assemblyRefs[asm] = h;
        return h;
    }

    /// <summary>The TypeDef / TypeRef handle of a (non-constructed) named type.</summary>
    private EntityHandle GetTypeDefOrRef(NamedTypeSymbol type)
    {
        type = type.OriginalDefinition;
        if (_typeHandles.TryGetValue(type, out var h)) return h;
        if (type is MetadataNamedType mt)
        {
            EntityHandle scope;
            if (type.ContainingSymbol is NamedTypeSymbol outer) scope = GetTypeDefOrRef(outer);
            else scope = GetAssemblyRef(mt.Assembly);
            var raw = type.MetadataName;
            var ns = type.ContainingSymbol is NamedTypeSymbol ? default : (type.ContainingSymbol as NamespaceSymbol)?.ToDisplayString() ?? mt.Namespace;
            h = Metadata.AddTypeReference(scope, ns == default || string.IsNullOrEmpty(ns) ? default : Metadata.GetOrAddString(ns), Metadata.GetOrAddString(raw));
        }
        else if (type is ErrorTypeSymbol or UnsupportedTypeSymbol)
        {
            throw new InvalidOperationException("Cannot emit error type " + type.Name);
        }
        else
        {
            throw new InvalidOperationException("Type has no handle (not emitted): " + type.ToDisplayString());
        }
        _typeHandles[type] = h;
        return h;
    }

    internal void RegisterTypeDefinition(NamedTypeSymbol type, TypeDefinitionHandle handle) => _typeHandles[type] = handle;

    private bool IsEmittedStructLike(NamedTypeSymbol t) => t.TypeKind is TypeKind.Struct or TypeKind.Enum;

    // ------------------------------------------------------------------ CLR type mapping

    /// <summary>Maps an Caladesi type to the type used in IL: nullable wrappers become Nullable&lt;T&gt; (value types) or are erased (reference types).</summary>
    public TypeSymbol ClrType(TypeSymbol t)
    {
        if (t is NullableTypeSymbol n)
        {
            var u = ClrType(n.Underlying);
            if (u.IsValueType || u is TypeParameterSymbol { HasValueTypeConstraint: true })
            {
                var nullable = Compilation.GetSpecialType(SpecialType.Nullable);
                return nullable.Construct(u);
            }
            return u;
        }
        return t;
    }

    public bool IsNullableValue(TypeSymbol t) =>
        t is NullableTypeSymbol n && (n.Underlying.IsValueType || n.Underlying is TypeParameterSymbol { HasValueTypeConstraint: true });

    public static bool IsValueTypeLike(TypeSymbol t) =>
        t.IsValueType || t is TypeParameterSymbol { HasValueTypeConstraint: true };

    // ------------------------------------------------------------------ signatures

    private static PrimitiveTypeCode? Primitive(SpecialType t) => t switch
    {
        SpecialType.Boolean => PrimitiveTypeCode.Boolean, SpecialType.Char => PrimitiveTypeCode.Char, SpecialType.SByte => PrimitiveTypeCode.SByte,
        SpecialType.Byte => PrimitiveTypeCode.Byte, SpecialType.Int16 => PrimitiveTypeCode.Int16, SpecialType.UInt16 => PrimitiveTypeCode.UInt16,
        SpecialType.Int32 => PrimitiveTypeCode.Int32, SpecialType.UInt32 => PrimitiveTypeCode.UInt32, SpecialType.Int64 => PrimitiveTypeCode.Int64,
        SpecialType.UInt64 => PrimitiveTypeCode.UInt64, SpecialType.Single => PrimitiveTypeCode.Single, SpecialType.Double => PrimitiveTypeCode.Double,
        SpecialType.String => PrimitiveTypeCode.String, SpecialType.Object => PrimitiveTypeCode.Object, SpecialType.IntPtr => PrimitiveTypeCode.IntPtr,
        SpecialType.UIntPtr => PrimitiveTypeCode.UIntPtr, _ => null,
    };

    public void EncodeType(SignatureTypeEncoder enc, TypeSymbol t)
    {
        t = ClrType(t);
        switch (t)
        {
            case ArrayTypeSymbol a:
                if (a.Rank == 1) EncodeType(enc.SZArray(), a.ElementType);
                else
                {
                    enc.Array(out var elem, out var shape);
                    EncodeType(elem, a.ElementType);
                    shape.Shape(a.Rank, ImmutableArray<int>.Empty, ImmutableArray<int>.Empty);
                }
                return;
            case TypeParameterSymbol tp:
                if (_classTypeParameterMap != null && _classTypeParameterMap.TryGetValue(tp, out var classOrdinal)) enc.GenericTypeParameter(classOrdinal);
                else if (tp.IsMethodTypeParameter) enc.GenericMethodTypeParameter(tp.Ordinal);
                else enc.GenericTypeParameter(tp.Ordinal);
                return;
            case NamedTypeSymbol nt:
            {
                var p = Primitive(nt.SpecialType);
                if (p != null && nt.IsDefinition) { enc.PrimitiveType(p.Value); return; }
                if (nt.IsGenericType && !ReferenceEquals(nt, nt.OriginalDefinition) || (nt.IsGenericType && nt is ConstructedNamedType))
                {
                    var def = nt.OriginalDefinition;
                    var gargs = enc.GenericInstantiation(GetTypeDefOrRef(def), nt.TypeArguments.Length, IsValueTypeLike(def));
                    foreach (var arg in nt.TypeArguments) EncodeType(gargs.AddArgument(), arg);
                    return;
                }
                if (nt.IsGenericType)
                {
                    // the generic definition referenced inside itself: instantiate over its own parameters
                    var gargs2 = enc.GenericInstantiation(GetTypeDefOrRef(nt), nt.TypeParameters.Length, IsValueTypeLike(nt));
                    foreach (var tp2 in nt.TypeParameters) EncodeType(gargs2.AddArgument(), tp2);
                    return;
                }
                enc.Type(GetTypeDefOrRef(nt), IsValueTypeLike(nt));
                return;
            }
        }
        throw new InvalidOperationException("Cannot encode type " + t.ToDisplayString());
    }

    private BlobHandle EncodeTypeBlob(TypeSymbol t)
    {
        var bb = new BlobBuilder();
        EncodeType(new SignatureTypeEncoder(bb), t);
        return Metadata.GetOrAddBlob(bb);
    }

    /// <summary>An entity handle usable as the token operand of newobj / castclass / box / ...</summary>
    public EntityHandle GetTypeEntity(TypeSymbol t)
    {
        t = ClrType(t);
        if (t is NamedTypeSymbol nt && !nt.IsGenericType) return GetTypeDefOrRef(nt);
        if (_typeSpecs.TryGetValue(t, out var h)) return h;
        h = Metadata.AddTypeSpecification(EncodeTypeBlob(t));
        _typeSpecs[t] = h;
        return h;
    }

    private void EncodeParameters(ParametersEncoder pe, ImmutableArray<ParameterSymbol> ps)
    {
        foreach (var p in ps)
        {
            var e = pe.AddParameter();
            EncodeType(e.Type(p.RefKind != RefKind.None), p.Type);
        }
    }

    private BlobHandle EncodeMethodSignature(MethodSymbol def)
    {
        var bb = new BlobBuilder();
        var enc = new BlobEncoder(bb).MethodSignature(SignatureCallingConvention.Default, def.Arity, isInstanceMethod: !def.IsStatic);
        enc.Parameters(def.Parameters.Length, out var ret, out var ps);
        var rt = def.ReturnType;
        if (rt.IsVoid) ret.Void();
        else EncodeType(ret.Type(def.ReturnsByRef), rt);
        EncodeParameters(ps, def.Parameters);
        return Metadata.GetOrAddBlob(bb);
    }

    private BlobHandle EncodeFieldSignature(TypeSymbol type)
    {
        var bb = new BlobBuilder();
        EncodeType(new BlobEncoder(bb).FieldSignature(), type);
        return Metadata.GetOrAddBlob(bb);
    }

    private BlobHandle EncodePropertySignature(PropertySymbol p)
    {
        var bb = new BlobBuilder();
        var enc = new BlobEncoder(bb).PropertySignature(isInstanceProperty: !p.IsStatic);
        enc.Parameters(p.Parameters.Length, out var ret, out var ps);
        EncodeType(ret.Type(false), p.Type);
        EncodeParameters(ps, p.Parameters);
        return Metadata.GetOrAddBlob(bb);
    }

    // ------------------------------------------------------------------ member references

    private readonly Dictionary<MethodSymbol, MethodDefinitionHandle> _methodDefs = new();
    private readonly Dictionary<FieldSymbol, FieldDefinitionHandle> _fieldDefs = new();
    private readonly Dictionary<PropertySymbol, PropertyDefinitionHandle> _propertyDefs = new();

    internal void RegisterMethod(MethodSymbol m, MethodDefinitionHandle h) => _methodDefs[m] = h;
    internal void RegisterField(FieldSymbol f, FieldDefinitionHandle h) => _fieldDefs[f] = h;
    internal void RegisterProperty(PropertySymbol p, PropertyDefinitionHandle h) => _propertyDefs[p] = h;

    /// <summary>The method token for a call: MethodDef, MemberRef or MethodSpec.</summary>
    public EntityHandle GetMethodEntity(MethodSymbol m)
    {
        if (m is ConstructedMethodSymbol cm)
        {
            var baseHandle = GetMethodEntity(cm.ConstructedFrom);
            var bb = new BlobBuilder();
            var enc = new BlobEncoder(bb).MethodSpecificationSignature(cm.TypeArguments.Length);
            foreach (var a in cm.TypeArguments) EncodeType(enc.AddArgument(), a);
            var blob = Metadata.GetOrAddBlob(bb);
            var key = (baseHandle, string.Join(",", cm.TypeArguments.Select(a => a.ToDisplayString())));
            if (!_methodSpecs.TryGetValue(key, out var spec))
            {
                spec = Metadata.AddMethodSpecification(baseHandle, blob);
                _methodSpecs[key] = spec;
            }
            return spec;
        }
        var def = m.OriginalDefinition;
        var container = m.ContainingSymbol as NamedTypeSymbol;
        // members of a generic type are always referenced through the type's own instantiation (Box<!0>::m), even inside the type
        var isOnConstructedType = container is ConstructedNamedType || container is { IsGenericType: true };
        if (!isOnConstructedType && _methodDefs.TryGetValue(def, out var md)) return md;
        if (_memberRefs.TryGetValue(m, out var existing)) return existing;
        var parent = container != null ? GetTypeEntity(container) : throw new InvalidOperationException("Method without container: " + m.Name);
        var name = def.MetadataName;
        var handle = Metadata.AddMemberReference(parent, Metadata.GetOrAddString(name), EncodeMethodSignature(def));
        _memberRefs[m] = handle;
        return handle;
    }

    public EntityHandle GetFieldEntity(FieldSymbol f)
    {
        var def = f.OriginalDefinition;
        var container = f.ContainingSymbol as NamedTypeSymbol;
        var isOnConstructedType = container is ConstructedNamedType || container is { IsGenericType: true };
        if (!isOnConstructedType && _fieldDefs.TryGetValue(def, out var fd)) return fd;
        if (_memberRefs.TryGetValue(f, out var existing)) return existing;
        var parent = GetTypeEntity(container!);
        var handle = Metadata.AddMemberReference(parent, Metadata.GetOrAddString(def.Name), EncodeFieldSignature(def.Type));
        _memberRefs[f] = handle;
        return handle;
    }
}
