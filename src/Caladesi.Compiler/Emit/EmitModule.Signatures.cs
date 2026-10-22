using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>Emits a compilation as an assembly: owns the metadata builder and all handle caches.</summary>
internal sealed partial class EmitModule(Compilation compilation)
{
    private readonly Dictionary<AssemblySymbol, AssemblyReferenceHandle> _assemblyRefs = [];
    private readonly Dictionary<NamedTypeSymbol, EntityHandle> _typeHandles = [];
    private readonly Dictionary<(TypeSymbol, bool), TypeSpecificationHandle> _typeSpecs = [];
    private readonly Dictionary<(Symbol, bool), EntityHandle> _memberRefs = [];
    private readonly Dictionary<(EntityHandle, BlobHandle), MethodSpecificationHandle> _methodSpecs = [];

    public Compilation Compilation { get; } = compilation;
    public MetadataBuilder Metadata { get; } = new();

    /// <summary>Maps type parameters of an emitted generic method to class parameters when a method body is moved into a generic helper class.</summary>
    private Dictionary<TypeParameterSymbol, int>? _classTypeParameterMap;
    internal void SetClassTypeParameterMap(Dictionary<TypeParameterSymbol, int>? map)
    {
        _classTypeParameterMap = map;
    }

    // ------------------------------------------------------------------ assembly / type references

    private AssemblyReferenceHandle GetAssemblyRef(AssemblySymbol asm)
    {
        if (_assemblyRefs.TryGetValue(asm, out AssemblyReferenceHandle h))
        {
            return h;
        }

        MetadataAssemblySymbol md = (MetadataAssemblySymbol)asm;
        MetadataReader reader = md.Reader;
        AssemblyDefinition def = reader.GetAssemblyDefinition();
        string name = reader.GetString(def.Name);
        string? culture = def.Culture.IsNil ? null : reader.GetString(def.Culture);
        byte[] keyBlob = def.PublicKey.IsNil ? [] : reader.GetBlobBytes(def.PublicKey);
        BlobHandle token = default;
        if (keyBlob.Length > 0)
        {
#pragma warning disable CA5350 // ECMA-335 defines the public key token as the low 8 bytes of the SHA-1 of the public key
            byte[] hash = SHA1.HashData(keyBlob);
#pragma warning restore CA5350
            byte[] tokenBytes = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                tokenBytes[i] = hash[hash.Length - 1 - i];
            }

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
        if (_typeHandles.TryGetValue(type, out EntityHandle h))
        {
            return h;
        }

        if (type is MetadataNamedType mt)
        {
            EntityHandle scope = type.ContainingSymbol is NamedTypeSymbol outer ? GetTypeDefOrRef(outer) : (EntityHandle)GetAssemblyRef(mt.Assembly);
            string raw = type.MetadataName;
            string? ns = type.ContainingSymbol is NamedTypeSymbol ? default : (type.ContainingSymbol as NamespaceSymbol)?.ToDisplayString() ?? mt.Namespace;
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

    internal void RegisterTypeDefinition(NamedTypeSymbol type, TypeDefinitionHandle handle)
    {
        _typeHandles[type] = handle;
    }

    // ------------------------------------------------------------------ CLR type mapping

    /// <summary>Maps an Caladesi type to the type used in IL: nullable wrappers become Nullable&lt;T&gt; (value types) or are erased (reference types).</summary>
    public TypeSymbol ClrType(TypeSymbol t)
    {
        if (t is NullableTypeSymbol n)
        {
            TypeSymbol u = ClrType(n.Underlying);
            if (u.IsValueType || u is TypeParameterSymbol { HasValueTypeConstraint: true })
            {
                NamedTypeSymbol nullable = Compilation.GetSpecialType(SpecialType.Nullable);
                return nullable.Construct(u);
            }
            return u;
        }
        return t;
    }

    public static bool IsNullableValue(TypeSymbol t)
    {
        return t is NullableTypeSymbol n && (n.Underlying.IsValueType || n.Underlying is TypeParameterSymbol { HasValueTypeConstraint: true });
    }

    public static bool IsValueTypeLike(TypeSymbol t)
    {
        return t.IsValueType || t is TypeParameterSymbol { HasValueTypeConstraint: true };
    }

    // ------------------------------------------------------------------ signatures

    private static PrimitiveTypeCode? Primitive(SpecialType t)
    {
        return t switch
        {
            SpecialType.Boolean => PrimitiveTypeCode.Boolean,
            SpecialType.Char => PrimitiveTypeCode.Char,
            SpecialType.SByte => PrimitiveTypeCode.SByte,
            SpecialType.Byte => PrimitiveTypeCode.Byte,
            SpecialType.Int16 => PrimitiveTypeCode.Int16,
            SpecialType.UInt16 => PrimitiveTypeCode.UInt16,
            SpecialType.Int32 => PrimitiveTypeCode.Int32,
            SpecialType.UInt32 => PrimitiveTypeCode.UInt32,
            SpecialType.Int64 => PrimitiveTypeCode.Int64,
            SpecialType.UInt64 => PrimitiveTypeCode.UInt64,
            SpecialType.Single => PrimitiveTypeCode.Single,
            SpecialType.Double => PrimitiveTypeCode.Double,
            SpecialType.String => PrimitiveTypeCode.String,
            SpecialType.Object => PrimitiveTypeCode.Object,
            SpecialType.IntPtr => PrimitiveTypeCode.IntPtr,
            SpecialType.UIntPtr => PrimitiveTypeCode.UIntPtr,
            _ => null,
        };
    }

    public void EncodeType(SignatureTypeEncoder enc, TypeSymbol t)
    {
        t = ClrType(t);
        switch (t)
        {
            case ArrayTypeSymbol a:
                if (a.Rank == 1)
                {
                    EncodeType(enc.SZArray(), a.ElementType);
                }
                else
                {
                    enc.Array(out SignatureTypeEncoder elem, out ArrayShapeEncoder shape);
                    EncodeType(elem, a.ElementType);
                    shape.Shape(a.Rank, [], []);
                }
                return;
            case TypeParameterSymbol tp:
                if (_classTypeParameterMap != null && _classTypeParameterMap.TryGetValue(tp, out int classOrdinal))
                {
                    enc.GenericTypeParameter(classOrdinal);
                }
                else if (tp.IsMethodTypeParameter)
                {
                    enc.GenericMethodTypeParameter(tp.Ordinal);
                }
                else
                {
                    enc.GenericTypeParameter(tp.Ordinal);
                }

                return;
            case NamedTypeSymbol nt:
                {
                    PrimitiveTypeCode? p = Primitive(nt.SpecialType);
                    if (p != null && nt.IsDefinition) { enc.PrimitiveType(p.Value); return; }
                    if ((nt.IsGenericType && !ReferenceEquals(nt, nt.OriginalDefinition)) || (nt.IsGenericType && nt is ConstructedNamedType))
                    {
                        NamedTypeSymbol def = nt.OriginalDefinition;
                        GenericTypeArgumentsEncoder gargs = enc.GenericInstantiation(GetTypeDefOrRef(def), nt.TypeArguments.Length, IsValueTypeLike(def));
                        foreach (TypeSymbol arg in nt.TypeArguments)
                        {
                            EncodeType(gargs.AddArgument(), arg);
                        }

                        return;
                    }
                    if (nt.IsGenericType)
                    {
                        // the generic definition referenced inside itself: instantiate over its own parameters
                        GenericTypeArgumentsEncoder gargs2 = enc.GenericInstantiation(GetTypeDefOrRef(nt), nt.TypeParameters.Length, IsValueTypeLike(nt));
                        foreach (TypeParameterSymbol tp2 in nt.TypeParameters)
                        {
                            EncodeType(gargs2.AddArgument(), tp2);
                        }

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
        BlobBuilder bb = new();
        EncodeType(new SignatureTypeEncoder(bb), t);
        return Metadata.GetOrAddBlob(bb);
    }

    /// <summary>An entity handle usable as the token operand of newobj / castclass / box / ...</summary>
    public EntityHandle GetTypeEntity(TypeSymbol t)
    {
        t = ClrType(t);
        if (t is NamedTypeSymbol nt && !nt.IsGenericType)
        {
            return GetTypeDefOrRef(nt);
        }

        if (_typeSpecs.TryGetValue((t, _classTypeParameterMap != null), out TypeSpecificationHandle h))
        {
            return h;
        }

        h = Metadata.AddTypeSpecification(EncodeTypeBlob(t));
        _typeSpecs[(t, _classTypeParameterMap != null)] = h;
        return h;
    }

    private void EncodeParameters(ParametersEncoder pe, ImmutableArray<ParameterSymbol> ps)
    {
        foreach (ParameterSymbol p in ps)
        {
            ParameterTypeEncoder e = pe.AddParameter();
            EncodeType(e.Type(p.RefKind != RefKind.None), p.Type);
        }
    }

    private BlobHandle EncodeMethodSignature(MethodSymbol def)
    {
        BlobBuilder bb = new();
        MethodSignatureEncoder enc = new BlobEncoder(bb).MethodSignature(SignatureCallingConvention.Default, def.Arity, isInstanceMethod: !def.IsStatic);
        enc.Parameters(def.Parameters.Length, out ReturnTypeEncoder ret, out ParametersEncoder ps);
        TypeSymbol rt = def.ReturnType;
        if (rt.ReturnsNothing)
        {
            ret.Void();
        }
        else
        {
            EncodeType(ret.Type(def.ReturnsByRef), rt);
        }

        EncodeParameters(ps, def.Parameters);
        return Metadata.GetOrAddBlob(bb);
    }

    private BlobHandle EncodeFieldSignature(TypeSymbol type)
    {
        BlobBuilder bb = new();
        EncodeType(new BlobEncoder(bb).FieldSignature(), type);
        return Metadata.GetOrAddBlob(bb);
    }

    private BlobHandle EncodePropertySignature(PropertySymbol p)
    {
        BlobBuilder bb = new();
        MethodSignatureEncoder enc = new BlobEncoder(bb).PropertySignature(isInstanceProperty: !p.IsStatic);
        enc.Parameters(p.Parameters.Length, out ReturnTypeEncoder ret, out ParametersEncoder ps);
        EncodeType(ret.Type(false), p.Type);
        EncodeParameters(ps, p.Parameters);
        return Metadata.GetOrAddBlob(bb);
    }

    // ------------------------------------------------------------------ member references

    private readonly Dictionary<MethodSymbol, MethodDefinitionHandle> _methodDefs = [];
    private readonly Dictionary<FieldSymbol, FieldDefinitionHandle> _fieldDefs = [];
    private readonly Dictionary<PropertySymbol, PropertyDefinitionHandle> _propertyDefs = [];

    internal void RegisterMethod(MethodSymbol m, MethodDefinitionHandle h)
    {
        _methodDefs[m] = h;
    }

    internal void RegisterField(FieldSymbol f, FieldDefinitionHandle h)
    {
        _fieldDefs[f] = h;
    }

    internal void RegisterProperty(PropertySymbol p, PropertyDefinitionHandle h)
    {
        _propertyDefs[p] = h;
    }

    /// <summary>The method token for a call: MethodDef, MemberRef or MethodSpec.</summary>
    public EntityHandle GetMethodEntity(MethodSymbol m)
    {
        if (m is ConstructedMethodSymbol cm)
        {
            EntityHandle baseHandle = GetMethodEntity(cm.ConstructedFrom);
            BlobBuilder bb = new();
            GenericTypeArgumentsEncoder enc = new BlobEncoder(bb).MethodSpecificationSignature(cm.TypeArguments.Length);
            foreach (TypeSymbol a in cm.TypeArguments)
            {
                EncodeType(enc.AddArgument(), a);
            }

            BlobHandle blob = Metadata.GetOrAddBlob(bb);
            (EntityHandle, BlobHandle) key = (baseHandle, blob); // the blob is the exact encoding, so it separates class and method type parameters that share a name
            if (!_methodSpecs.TryGetValue(key, out MethodSpecificationHandle spec))
            {
                spec = Metadata.AddMethodSpecification(baseHandle, blob);
                _methodSpecs[key] = spec;
            }
            return spec;
        }
        MethodSymbol def = m.OriginalDefinition;
        NamedTypeSymbol? container = m.ContainingSymbol as NamedTypeSymbol;
        // members of a generic type are always referenced through the type's own instantiation (Box<!0>::m), even inside the type
        bool isOnConstructedType = container is ConstructedNamedType or { IsGenericType: true };
        if (!isOnConstructedType && _methodDefs.TryGetValue(def, out MethodDefinitionHandle md))
        {
            return md;
        }

        if (_memberRefs.TryGetValue((m, _classTypeParameterMap != null), out EntityHandle existing))
        {
            return existing;
        }

        EntityHandle parent = container != null ? GetTypeEntity(container) : throw new InvalidOperationException("Method without container: " + m.Name);
        string name = def.MetadataName;
        Dictionary<TypeParameterSymbol, int>? savedMap = _classTypeParameterMap;
        _classTypeParameterMap = (def.ContainingSymbol as SynthesizedNamedType)?.MethodTypeParameterMap;
        BlobHandle signature = EncodeMethodSignature(def);
        _classTypeParameterMap = savedMap;
        MemberReferenceHandle handle = Metadata.AddMemberReference(parent, Metadata.GetOrAddString(name), signature);
        _memberRefs[(m, _classTypeParameterMap != null)] = handle;
        return handle;
    }

    public EntityHandle GetFieldEntity(FieldSymbol f)
    {
        FieldSymbol def = f.OriginalDefinition;
        NamedTypeSymbol? container = f.ContainingSymbol as NamedTypeSymbol;
        bool isOnConstructedType = container is ConstructedNamedType or { IsGenericType: true };
        if (!isOnConstructedType && _fieldDefs.TryGetValue(def, out FieldDefinitionHandle fd))
        {
            return fd;
        }

        if (_memberRefs.TryGetValue((f, _classTypeParameterMap != null), out EntityHandle existing))
        {
            return existing;
        }

        EntityHandle parent = GetTypeEntity(container!);
        Dictionary<TypeParameterSymbol, int>? savedMap = _classTypeParameterMap;
        _classTypeParameterMap = (def.ContainingSymbol as SynthesizedNamedType)?.MethodTypeParameterMap;
        BlobHandle signature = EncodeFieldSignature(def.Type);
        _classTypeParameterMap = savedMap;
        MemberReferenceHandle handle = Metadata.AddMemberReference(parent, Metadata.GetOrAddString(def.Name), signature);
        _memberRefs[(f, _classTypeParameterMap != null)] = handle;
        return handle;
    }
}
