using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Caladesi.Compiler.Symbols.Metadata;

internal static class MetadataHelpers
{
    public static (string ns, string name)? GetAttributeTypeName(MetadataReader r, CustomAttribute ca)
    {
        switch (ca.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                {
                    MemberReference mr = r.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                    switch (mr.Parent.Kind)
                    {
                        case HandleKind.TypeReference:
                            {
                                TypeReference tr = r.GetTypeReference((TypeReferenceHandle)mr.Parent);
                                return (r.GetString(tr.Namespace), r.GetString(tr.Name));
                            }
                        case HandleKind.TypeDefinition:
                            {
                                TypeDefinition td = r.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
                                return (r.GetString(td.Namespace), r.GetString(td.Name));
                            }
                    }
                    return null;
                }
            case HandleKind.MethodDefinition:
                {
                    MethodDefinition md = r.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
                    TypeDefinition td = r.GetTypeDefinition(md.GetDeclaringType());
                    return (r.GetString(td.Namespace), r.GetString(td.Name));
                }
        }
        return null;
    }

    public static CustomAttribute? FindAttribute(MetadataReader r, CustomAttributeHandleCollection attrs, string ns, string name)
    {
        foreach (CustomAttributeHandle h in attrs)
        {
            CustomAttribute ca = r.GetCustomAttribute(h);
            (string ns, string name)? n = GetAttributeTypeName(r, ca);
            if (n != null && n.Value.name == name && n.Value.ns == ns)
            {
                return ca;
            }
        }
        return null;
    }

    /// <summary>Reads the single byte argument of attributes like NullableContext / Nullable(byte).</summary>
    public static byte ReadByteArg(MetadataReader r, BlobHandle value)
    {
        BlobReader br = r.GetBlobReader(value);
        return br.Length < 3 || br.ReadUInt16() != 1 ? (byte)0 : br.ReadByte();
    }

    /// <summary>Reads the first flag of Nullable(byte) or Nullable(byte[]).</summary>
    public static byte ReadNullableFlag(MetadataReader r, BlobHandle value)
    {
        BlobReader br = r.GetBlobReader(value);
        if (br.Length < 3 || br.ReadUInt16() != 1)
        {
            return 0;
        }

        if (br.Length == 5)
        {
            return br.ReadByte(); // single byte form: prolog(2) + byte + named count(2)
        }

        int count = br.ReadInt32();
        return count > 0 ? br.ReadByte() : (byte)0;
    }

    public static ImmutableArray<AttributeData> DecodeAttributes(MetadataAssemblySymbol asm, CustomAttributeHandleCollection handles)
    {
        if (handles.Count == 0)
        {
            return [];
        }

        ImmutableArray<AttributeData>.Builder b = ImmutableArray.CreateBuilder<AttributeData>();
        AttributeTypeProvider provider = new(asm);
        foreach (CustomAttributeHandle h in handles)
        {
            try
            {
                CustomAttribute ca = asm.Reader.GetCustomAttribute(h);
                CustomAttributeValue<TypeSymbol> value = ca.DecodeValue(provider);
                NamedTypeSymbol? cls = ResolveAttributeClass(asm, ca);
                ImmutableArray<object?> args = [.. value.FixedArguments.Select(a => ConvertArg(a.Value))];
                ImmutableArray<KeyValuePair<string, object?>> named = [.. value.NamedArguments.Select(a => new KeyValuePair<string, object?>(a.Name ?? "", ConvertArg(a.Value)))];
                b.Add(new AttributeData(cls, null, args, named));
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                // ignore undecodable attributes
            }
        }
        return b.ToImmutable();
    }

    private static object? ConvertArg(object? v)
    {
        return v switch
        {
            ImmutableArray<CustomAttributeTypedArgument<TypeSymbol>> arr => arr.Select(x => ConvertArg(x.Value)).ToImmutableArray(),
            _ => v,
        };
    }

    private static NamedTypeSymbol? ResolveAttributeClass(MetadataAssemblySymbol asm, CustomAttribute ca)
    {
        MetadataReader r = asm.Reader;
        switch (ca.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                {
                    MemberReference mr = r.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                    return mr.Parent.Kind switch
                    {
                        HandleKind.TypeReference => asm.ResolveTypeReference((TypeReferenceHandle)mr.Parent),
                        HandleKind.TypeDefinition => asm.GetTypeDefinition((TypeDefinitionHandle)mr.Parent),
                        _ => null,
                    };
                }
            case HandleKind.MethodDefinition:
                return asm.GetTypeDefinition(r.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType());
        }
        return null;
    }
}

internal sealed class AttributeTypeProvider(MetadataAssemblySymbol asm) : ICustomAttributeTypeProvider<TypeSymbol>
{
    public TypeSymbol GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        return asm.Provider.GetPrimitiveType(typeCode);
    }

    public TypeSymbol GetSystemType()
    {
        return (TypeSymbol?)asm.ReferenceSet.GetSpecialType(SpecialType.Type) ?? new ErrorTypeSymbol("Type");
    }

    public TypeSymbol GetSZArrayType(TypeSymbol elementType)
    {
        return asm.Provider.GetSZArrayType(elementType);
    }

    public TypeSymbol GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        return asm.GetTypeDefinition(handle);
    }

    public TypeSymbol GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        return asm.ResolveTypeReference(handle);
    }

    public TypeSymbol GetTypeFromSerializedName(string name)
    {
        int comma = name.IndexOf(',');
        string n = comma < 0 ? name : name[..comma];
        return (TypeSymbol?)asm.ReferenceSet.FindType(n) ?? new ErrorTypeSymbol(n);
    }
    public PrimitiveTypeCode GetUnderlyingEnumType(TypeSymbol type)
    {
        return (type as NamedTypeSymbol)?.EnumUnderlyingType?.SpecialType switch
        {
            SpecialType.Byte => PrimitiveTypeCode.Byte,
            SpecialType.SByte => PrimitiveTypeCode.SByte,
            SpecialType.Int16 => PrimitiveTypeCode.Int16,
            SpecialType.UInt16 => PrimitiveTypeCode.UInt16,
            SpecialType.UInt32 => PrimitiveTypeCode.UInt32,
            SpecialType.Int64 => PrimitiveTypeCode.Int64,
            SpecialType.UInt64 => PrimitiveTypeCode.UInt64,
            _ => PrimitiveTypeCode.Int32,
        };
    }

    public bool IsSystemType(TypeSymbol type)
    {
        return type is NamedTypeSymbol { SpecialType: SpecialType.Type };
    }
}
