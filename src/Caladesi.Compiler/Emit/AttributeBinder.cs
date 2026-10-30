using System.Collections.Immutable;
using System.Reflection.Metadata;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Emit;

/// <summary>Binds a decorator to an attribute constructor and serializes its arguments into a custom attribute blob.</summary>
internal sealed class AttributeBinder(Compilation compilation, SourceFile file)
{
    public (MethodSymbol Constructor, BlobBuilder Blob)? Bind(DecoratorSyntax decorator, Symbol? context)
    {
        BoundAttribute? bound = compilation.GetBoundAttribute(file, decorator, context);
        if (bound == null)
        {
            return null;
        }

        BlobBuilder bb = new();
        bb.WriteUInt16(1);
        ImmutableArray<ParameterSymbol> ps = bound.Constructor.Parameters;
        for (int i = 0; i < ps.Length; i++)
        {
            WriteFixed(bb, ps[i].Type, bound.Arguments[i]);
        }

        bb.WriteUInt16((ushort)bound.Named.Length);
        foreach ((Symbol? member, BoundExpression? value) in bound.Named)
        {
            TypeSymbol type = member is PropertySymbol p ? p.Type : ((FieldSymbol)member).Type;
            bb.WriteByte(member is PropertySymbol ? (byte)0x54 : (byte)0x53);
            WriteTypeTag(bb, type);
            bb.WriteSerializedString(member.Name);
            WriteFixed(bb, type, value);
        }
        return (bound.Constructor, bb);
    }

    private static object? ConstantOf(BoundExpression e)
    {
        return e switch
        {
            _ when e.ConstantValue is { } cv => cv.Value,
            BoundConversion
            {
                Kind: ConversionKind.Boxing or ConversionKind.ImplicitReference or ConversionKind.Identity or
                ConversionKind.ToNullable or ConversionKind.TypeOnly
            } c => ConstantOf(c.Operand),
            _ => null,
        };
    }

    private void WriteTypeTag(BlobBuilder bb, TypeSymbol t)
    {
        t = t.StripNullable();
        if (t is ArrayTypeSymbol arr)
        {
            bb.WriteByte(0x1D);
            WriteTypeTag(bb, arr.ElementType);
            return;
        }
        if (t.TypeKind == TypeKind.Enum)
        {
            bb.WriteByte(0x55);
            bb.WriteSerializedString(QualifiedName(t));
            return;
        }
        bb.WriteByte(t.SpecialType switch
        {
            SpecialType.Boolean => 0x02,
            SpecialType.Char => 0x03,
            SpecialType.SByte => 0x04,
            SpecialType.Byte => 0x05,
            SpecialType.Int16 => 0x06,
            SpecialType.UInt16 => 0x07,
            SpecialType.Int32 => 0x08,
            SpecialType.UInt32 => 0x09,
            SpecialType.Int64 => 0x0A,
            SpecialType.UInt64 => 0x0B,
            SpecialType.Single => 0x0C,
            SpecialType.Double => 0x0D,
            SpecialType.String => 0x0E,
            SpecialType.Type => 0x50,
            _ => 0x51,
        });
    }

    private string QualifiedName(TypeSymbol t)
    {
        NamedTypeSymbol? def = t.StripNullable() as NamedTypeSymbol;
        if (def == null)
        {
            return t.ToDisplayString();
        }

        string? asm = compilation.GetAssemblyNameOf(def);
        return def.ToMetadataString() + (asm == null ? "" : ", " + asm);
    }

    private void WriteFixed(BlobBuilder bb, TypeSymbol type, BoundExpression arg)
    {
        type = type.StripNullable();
        if (type is ArrayTypeSymbol at)
        {
            if (arg is BoundArrayCreation ac)
            {
                bb.WriteInt32(ac.Initializers.Length);
                foreach (BoundExpression el in ac.Initializers)
                {
                    WriteFixed(bb, at.ElementType, el);
                }
            }
            else
            {
                bb.WriteInt32(-1);
            }

            return;
        }
        if (type.SpecialType == SpecialType.Object)
        {
            BoundExpression inner = arg is BoundConversion { Kind: ConversionKind.Boxing or ConversionKind.ImplicitReference } c ? c.Operand : arg;
            TypeSymbol itype = inner.Type ?? type;
            WriteTypeTag(bb, itype);
            WriteFixed(bb, itype, inner);
            return;
        }
        if (type.SpecialType == SpecialType.Type)
        {
            if (arg is BoundTypeOf to)
            {
                bb.WriteSerializedString(QualifiedName(to.Operand));
            }
            else
            {
                bb.WriteByte(0xFF);
            }

            return;
        }
        object? v = ConstantOf(arg);
        if (type.TypeKind == TypeKind.Enum)
        {
            NamedTypeSymbol under = (type as NamedTypeSymbol)?.EnumUnderlyingType ?? compilation.GetSpecialType(SpecialType.Int32);
            WritePrimitive(bb, under.SpecialType, v);
            return;
        }
        if (type.SpecialType == SpecialType.String)
        {
            if (v is string s)
            {
                bb.WriteSerializedString(s);
            }
            else
            {
                bb.WriteByte(0xFF);
            }

            return;
        }
        WritePrimitive(bb, type.SpecialType, v);
    }

    private static void WritePrimitive(BlobBuilder bb, SpecialType t, object? v)
    {
        switch (t)
        {
            case SpecialType.Boolean: bb.WriteBoolean(Convert.ToBoolean(v)); break;
            case SpecialType.Char: bb.WriteUInt16(v is char ch ? ch : v is string { Length: 1 } s ? s[0] : (char)Convert.ToInt32(v)); break;
            case SpecialType.SByte: bb.WriteSByte(Convert.ToSByte(v)); break;
            case SpecialType.Byte: bb.WriteByte(Convert.ToByte(v)); break;
            case SpecialType.Int16: bb.WriteInt16(Convert.ToInt16(v)); break;
            case SpecialType.UInt16: bb.WriteUInt16(Convert.ToUInt16(v)); break;
            case SpecialType.Int32: bb.WriteInt32(Convert.ToInt32(v)); break;
            case SpecialType.UInt32: bb.WriteUInt32(Convert.ToUInt32(v)); break;
            case SpecialType.Int64: bb.WriteInt64(Convert.ToInt64(v)); break;
            case SpecialType.UInt64: bb.WriteUInt64(Convert.ToUInt64(v)); break;
            case SpecialType.Single: bb.WriteSingle(Convert.ToSingle(v)); break;
            case SpecialType.Double: bb.WriteDouble(Convert.ToDouble(v)); break;
            default: bb.WriteInt32(Convert.ToInt32(v)); break;
        }
    }
}
