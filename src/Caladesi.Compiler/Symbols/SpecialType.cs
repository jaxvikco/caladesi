namespace Caladesi.Compiler.Symbols;

#pragma warning disable CA1720 // member names intentionally mirror the CLR type names they represent
public enum SpecialType
{
    None = 0,
    Object, Void, Boolean, Char, SByte, Byte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Single, Double, Decimal,
    String, IntPtr, UIntPtr, Array, Enum, ValueType, Delegate, MulticastDelegate, Nullable, IEnumerable, IEnumerator,
    IEnumerableT, IEnumeratorT, IDisposable, IAsyncDisposable, Task, TaskT, ValueTask, ValueTaskT, Exception, Type,
    BigInteger, ValueTuple, DateTime, Attribute,
    // synthesized/internal markers (never produced from metadata)
    Null, Never,
}
#pragma warning restore CA1720

public static class SpecialTypes
{
    public static bool IsNumeric(this SpecialType t)
    {
        return t is SpecialType.SByte or SpecialType.Byte or SpecialType.Int16 or SpecialType.UInt16
        or SpecialType.Int32 or SpecialType.UInt32 or SpecialType.Int64 or SpecialType.UInt64 or SpecialType.Single
        or SpecialType.Double or SpecialType.Decimal or SpecialType.Char or SpecialType.IntPtr or SpecialType.UIntPtr;
    }

    public static bool IsIntegral(this SpecialType t)
    {
        return t is SpecialType.SByte or SpecialType.Byte or SpecialType.Int16 or SpecialType.UInt16
        or SpecialType.Int32 or SpecialType.UInt32 or SpecialType.Int64 or SpecialType.UInt64 or SpecialType.Char
        or SpecialType.IntPtr or SpecialType.UIntPtr;
    }

    public static bool IsSignedIntegral(this SpecialType t)
    {
        return t is SpecialType.SByte or SpecialType.Int16 or SpecialType.Int32 or SpecialType.Int64 or SpecialType.IntPtr;
    }

    public static bool IsUnsignedIntegral(this SpecialType t)
    {
        return t is SpecialType.Byte or SpecialType.UInt16 or SpecialType.UInt32 or SpecialType.UInt64 or SpecialType.UIntPtr or SpecialType.Char;
    }

    public static bool IsFloating(this SpecialType t)
    {
        return t is SpecialType.Single or SpecialType.Double;
    }

    /// <summary>Metadata full name of a primitive special type (null for synthesized ones).</summary>
    public static string? MetadataName(this SpecialType t)
    {
        return t switch
        {
            SpecialType.Object => "System.Object",
            SpecialType.Void => "System.Void",
            SpecialType.Boolean => "System.Boolean",
            SpecialType.Char => "System.Char",
            SpecialType.SByte => "System.SByte",
            SpecialType.Byte => "System.Byte",
            SpecialType.Int16 => "System.Int16",
            SpecialType.UInt16 => "System.UInt16",
            SpecialType.Int32 => "System.Int32",
            SpecialType.UInt32 => "System.UInt32",
            SpecialType.Int64 => "System.Int64",
            SpecialType.UInt64 => "System.UInt64",
            SpecialType.Single => "System.Single",
            SpecialType.Double => "System.Double",
            SpecialType.Decimal => "System.Decimal",
            SpecialType.String => "System.String",
            SpecialType.IntPtr => "System.IntPtr",
            SpecialType.UIntPtr => "System.UIntPtr",
            SpecialType.Array => "System.Array",
            SpecialType.Enum => "System.Enum",
            SpecialType.ValueType => "System.ValueType",
            SpecialType.Delegate => "System.Delegate",
            SpecialType.MulticastDelegate => "System.MulticastDelegate",
            SpecialType.Nullable => "System.Nullable`1",
            SpecialType.IEnumerable => "System.Collections.IEnumerable",
            SpecialType.IEnumerator => "System.Collections.IEnumerator",
            SpecialType.IEnumerableT => "System.Collections.Generic.IEnumerable`1",
            SpecialType.IEnumeratorT => "System.Collections.Generic.IEnumerator`1",
            SpecialType.IDisposable => "System.IDisposable",
            SpecialType.IAsyncDisposable => "System.IAsyncDisposable",
            SpecialType.Task => "System.Threading.Tasks.Task",
            SpecialType.TaskT => "System.Threading.Tasks.Task`1",
            SpecialType.ValueTask => "System.Threading.Tasks.ValueTask",
            SpecialType.ValueTaskT => "System.Threading.Tasks.ValueTask`1",
            SpecialType.Exception => "System.Exception",
            SpecialType.Type => "System.Type",
            SpecialType.BigInteger => "System.Numerics.BigInteger",
            SpecialType.ValueTuple => "System.ValueTuple",
            SpecialType.DateTime => "System.DateTime",
            SpecialType.Attribute => "System.Attribute",
            _ => null,
        };
    }

    public static string? Keyword(this SpecialType t)
    {
        return t switch
        {
            SpecialType.Object => "object",
            SpecialType.Void => "void",
            SpecialType.Boolean => "bool",
            SpecialType.Char => "char",
            SpecialType.SByte => "sbyte",
            SpecialType.Byte => "byte",
            SpecialType.Int16 => "short",
            SpecialType.UInt16 => "ushort",
            SpecialType.Int32 => "int",
            SpecialType.UInt32 => "uint",
            SpecialType.Int64 => "long",
            SpecialType.UInt64 => "ulong",
            SpecialType.Single => "float",
            SpecialType.Double => "double",
            SpecialType.Decimal => "decimal",
            SpecialType.String => "string",
            SpecialType.IntPtr => "nint",
            SpecialType.UIntPtr => "nuint",
            SpecialType.BigInteger => "bigint",
            _ => null,
        };
    }
}
