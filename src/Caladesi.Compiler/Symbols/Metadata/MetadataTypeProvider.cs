using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Caladesi.Compiler.Symbols.Metadata;

/// <summary>Type parameters in scope while decoding a signature.</summary>
internal sealed class GenericContext(ImmutableArray<TypeParameterSymbol> typeParameters, ImmutableArray<TypeParameterSymbol> methodParameters)
{
    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; } = typeParameters;
    public ImmutableArray<TypeParameterSymbol> MethodParameters { get; } = methodParameters;
    public static readonly GenericContext Empty = new(ImmutableArray<TypeParameterSymbol>.Empty, ImmutableArray<TypeParameterSymbol>.Empty);
}

/// <summary>Wraps a by-ref type during signature decoding; unwrapped into <see cref="RefKind"/> by callers.</summary>
internal sealed class ByRefTypeSymbol(TypeSymbol element) : TypeSymbol
{
    public TypeSymbol Element { get; } = element;
    public override SymbolKind Kind => SymbolKind.NamedType;
    public override string Name => Element.Name + "&";
    public override TypeKind TypeKind => TypeKind.Error;
    public override bool IsReferenceType => false;
    public override bool IsValueType => false;
    public override bool Equals(TypeSymbol? other) => other is ByRefTypeSymbol b && b.Element.Equals(Element);
    public override int GetHashCode() => Element.GetHashCode() + 1;
}

/// <summary>Pointers and function pointers; members mentioning these are unsupported and hidden.</summary>
public sealed class UnsupportedTypeSymbol(string name) : NamedTypeSymbol
{
    public override string Name => name;
    public override TypeKind TypeKind => TypeKind.Error;
    public override ImmutableArray<TypeParameterSymbol> TypeParameters => ImmutableArray<TypeParameterSymbol>.Empty;
    public override ImmutableArray<TypeSymbol> TypeArguments => ImmutableArray<TypeSymbol>.Empty;
    public override bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

internal sealed class MetadataTypeProvider(MetadataAssemblySymbol assembly) : ISignatureTypeProvider<TypeSymbol, GenericContext>
{
    [ThreadStatic] public static bool SawIsExternalInit;
    [ThreadStatic] public static bool SawUnsupported;

    private ReferenceSet Set => assembly.ReferenceSet;

    public TypeSymbol GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        var st = typeCode switch
        {
            PrimitiveTypeCode.Boolean => SpecialType.Boolean, PrimitiveTypeCode.Char => SpecialType.Char,
            PrimitiveTypeCode.SByte => SpecialType.SByte, PrimitiveTypeCode.Byte => SpecialType.Byte,
            PrimitiveTypeCode.Int16 => SpecialType.Int16, PrimitiveTypeCode.UInt16 => SpecialType.UInt16,
            PrimitiveTypeCode.Int32 => SpecialType.Int32, PrimitiveTypeCode.UInt32 => SpecialType.UInt32,
            PrimitiveTypeCode.Int64 => SpecialType.Int64, PrimitiveTypeCode.UInt64 => SpecialType.UInt64,
            PrimitiveTypeCode.Single => SpecialType.Single, PrimitiveTypeCode.Double => SpecialType.Double,
            PrimitiveTypeCode.String => SpecialType.String, PrimitiveTypeCode.Object => SpecialType.Object,
            PrimitiveTypeCode.Void => SpecialType.Void, PrimitiveTypeCode.IntPtr => SpecialType.IntPtr,
            PrimitiveTypeCode.UIntPtr => SpecialType.UIntPtr, PrimitiveTypeCode.TypedReference => SpecialType.None,
            _ => SpecialType.None,
        };
        if (st == SpecialType.None) { SawUnsupported = true; return new UnsupportedTypeSymbol("typedref"); }
        return (TypeSymbol?)Set.GetSpecialType(st) ?? new ErrorTypeSymbol(st.MetadataName() ?? "?");
    }

    public TypeSymbol GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        assembly.GetTypeDefinition(handle);

    public TypeSymbol GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        assembly.ResolveTypeReference(handle);

    public TypeSymbol GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public TypeSymbol GetSZArrayType(TypeSymbol elementType) => new ArrayTypeSymbol(elementType, Set.GetSpecialType(SpecialType.Array));

    public TypeSymbol GetArrayType(TypeSymbol elementType, ArrayShape shape) =>
        new ArrayTypeSymbol(elementType, Set.GetSpecialType(SpecialType.Array), shape.Rank);

    public TypeSymbol GetByReferenceType(TypeSymbol elementType) => new ByRefTypeSymbol(elementType);

    public TypeSymbol GetPointerType(TypeSymbol elementType)
    {
        SawUnsupported = true;
        return new UnsupportedTypeSymbol("pointer");
    }

    public TypeSymbol GetFunctionPointerType(MethodSignature<TypeSymbol> signature)
    {
        SawUnsupported = true;
        return new UnsupportedTypeSymbol("function pointer");
    }

    public TypeSymbol GetPinnedType(TypeSymbol elementType) => elementType;

    public TypeSymbol GetModifiedType(TypeSymbol modifier, TypeSymbol unmodifiedType, bool isRequired)
    {
        if (modifier is NamedTypeSymbol m && m.Name == "IsExternalInit") SawIsExternalInit = true;
        return unmodifiedType;
    }

    public TypeSymbol GetGenericInstantiation(TypeSymbol genericType, ImmutableArray<TypeSymbol> typeArguments)
    {
        if (genericType is not NamedTypeSymbol nt || nt is ErrorTypeSymbol) return genericType;
        if (nt.Arity != typeArguments.Length) return new ErrorTypeSymbol(nt.Name);
        if (nt.SpecialType == SpecialType.Nullable) return new NullableTypeSymbol(typeArguments[0]);
        return nt.Construct(typeArguments);
    }

    public TypeSymbol GetGenericMethodParameter(GenericContext genericContext, int index) =>
        index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : new ErrorTypeSymbol("!!" + index);

    public TypeSymbol GetGenericTypeParameter(GenericContext genericContext, int index) =>
        index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : new ErrorTypeSymbol("!" + index);
}
