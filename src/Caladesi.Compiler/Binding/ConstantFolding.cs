using System.Globalization;
using System.Numerics;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler;

/// <summary>Compile-time evaluation of constant expressions.</summary>
internal static class ConstantFolding
{
    public static object? ConvertNumeric(object value, SpecialType to, bool unchecked_ = false)
    {
        try
        {
            if (value is char c)
            {
                value = (int)c;
            }

            return value is bool or string ? null : unchecked_ ? Unchecked(value, to) : Checked(value, to);
        }
        catch (OverflowException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    private static object? Checked(object v, SpecialType to)
    {
        // integral sources go through decimal to avoid precision loss; floating sources use double
        if (v is double or float)
        {
            double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            return to switch
            {
                SpecialType.Single => (float)d,
                SpecialType.Double => d,
                SpecialType.Decimal => (decimal)d,
                SpecialType.SByte => checked((sbyte)d),
                SpecialType.Byte => checked((byte)d),
                SpecialType.Int16 => checked((short)d),
                SpecialType.UInt16 => checked((ushort)d),
                SpecialType.Int32 => checked((int)d),
                SpecialType.UInt32 => checked((uint)d),
                SpecialType.Int64 => checked((long)d),
                SpecialType.UInt64 => checked((ulong)d),
                SpecialType.Char => checked((char)d),
                _ => null,
            };
        }
        decimal dec = v switch { decimal m => m, BigInteger => 0m, _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture) };
        return to switch
        {
            SpecialType.SByte => (sbyte)dec,
            SpecialType.Byte => (byte)dec,
            SpecialType.Int16 => (short)dec,
            SpecialType.UInt16 => (ushort)dec,
            SpecialType.Int32 => (int)dec,
            SpecialType.UInt32 => (uint)dec,
            SpecialType.Int64 => (long)dec,
            SpecialType.UInt64 => (ulong)dec,
            SpecialType.Single => (float)dec,
            SpecialType.Double => (double)dec,
            SpecialType.Decimal => dec,
            SpecialType.Char => (char)(ushort)dec,
            SpecialType.IntPtr => (long)dec,
            SpecialType.UIntPtr => (ulong)dec,
            _ => null,
        };
    }

    private static object? Unchecked(object v, SpecialType to)
    {
        unchecked
        {
            if (v is double or float or decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                return to switch
                {
                    SpecialType.Single => (float)d,
                    SpecialType.Double => d,
                    SpecialType.Decimal => (decimal)d,
                    SpecialType.SByte => (sbyte)(long)d,
                    SpecialType.Byte => (byte)(long)d,
                    SpecialType.Int16 => (short)(long)d,
                    SpecialType.UInt16 => (ushort)(long)d,
                    SpecialType.Int32 => (int)(long)d,
                    SpecialType.UInt32 => (uint)(long)d,
                    SpecialType.Int64 => (long)d,
                    SpecialType.UInt64 => (ulong)d,
                    SpecialType.Char => (char)(long)d,
                    _ => null,
                };
            }
            long l = v switch
            {
                int i => i,
                long x => x,
                uint u => u,
                short s => s,
                ushort us => us,
                byte b => b,
                sbyte sb => sb,
                ulong ul => (long)ul,
                _ => 0L,
            };
            bool isUnsigned64 = v is ulong;
            return to switch
            {
                SpecialType.SByte => (sbyte)l,
                SpecialType.Byte => (byte)l,
                SpecialType.Int16 => (short)l,
                SpecialType.UInt16 => (ushort)l,
                SpecialType.Int32 => (int)l,
                SpecialType.UInt32 => (uint)l,
                SpecialType.Int64 => l,
                SpecialType.UInt64 => (ulong)l,
                SpecialType.Single => isUnsigned64 ? (ulong)l : (float)l,
                SpecialType.Double => isUnsigned64 ? (ulong)l : (double)l,
                SpecialType.Decimal => isUnsigned64 ? (ulong)l : (decimal)l,
                SpecialType.Char => (char)l,
                _ => null,
            };
        }
    }

    public static object? FoldUnary(UnaryOperatorKind kind, object v, SpecialType _1)
    {
        try
        {
            switch (kind)
            {
                case UnaryOperatorKind.Not when v is bool b: return !b;
                case UnaryOperatorKind.Plus: return v;
                case UnaryOperatorKind.Minus:
                    return v switch
                    {
                        int i => unchecked(-i),
                        long l => unchecked(-l),
                        double d => -d,
                        float f => -f,
                        decimal m => -m,
                        _ => null,
                    };
                case UnaryOperatorKind.BitwiseNot:
                    return v switch { int i => ~i, long l => ~l, uint u => ~u, ulong ul => ~ul, _ => null };
            }
        }
        catch (OverflowException) { }
        return null;
    }

    public static object? FoldBinary(BinaryOperatorKind kind, object l, object r, SpecialType type)
    {
        try
        {
            switch (type)
            {
                case SpecialType.Int32: return FoldInt((int)l, r, kind);
                case SpecialType.Int64: return FoldLong((long)l, r, kind);
                case SpecialType.UInt32: return FoldUInt((uint)l, r, kind);
                case SpecialType.UInt64: return FoldULong((ulong)l, r, kind);
                case SpecialType.Double: return FoldDouble((double)l, (double)r, kind);
                case SpecialType.Single: return FoldFloat((float)l, (float)r, kind);
                case SpecialType.Decimal: return FoldDecimal((decimal)l, (decimal)r, kind);
                case SpecialType.Boolean:
                    return kind switch
                    {
                        BinaryOperatorKind.Equal => (bool)l == (bool)r,
                        BinaryOperatorKind.NotEqual => (bool)l != (bool)r,
                        BinaryOperatorKind.And => (bool)l & (bool)r,
                        BinaryOperatorKind.Or => (bool)l | (bool)r,
                        BinaryOperatorKind.Xor => (bool)l ^ (bool)r,
                        _ => null,
                    };
                case SpecialType.String:
                    return kind switch
                    {
                        BinaryOperatorKind.Add => (string)l + (string)r,
                        BinaryOperatorKind.Equal => (string)l == (string)r,
                        BinaryOperatorKind.NotEqual => (string)l != (string)r,
                        _ => null,
                    };
                case SpecialType.Char:
                    return FoldInt((char)l, (int)(char)r, kind) is { } v && kind is >= BinaryOperatorKind.Equal and <= BinaryOperatorKind.GreaterThanOrEqual ? v : null;
            }
        }
        catch (DivideByZeroException) { }
        catch (OverflowException) { }
        return null;
    }

    private static object? FoldInt(int a, object ro, BinaryOperatorKind k)
    {
        if (k is BinaryOperatorKind.ShiftLeft or BinaryOperatorKind.ShiftRight or BinaryOperatorKind.UnsignedShiftRight)
        {
            int sh = Convert.ToInt32(ro, CultureInfo.InvariantCulture) & 31;
            return k switch { BinaryOperatorKind.ShiftLeft => a << sh, BinaryOperatorKind.ShiftRight => a >> sh, _ => (int)((uint)a >> sh) };
        }
        int b = Convert.ToInt32(ro, CultureInfo.InvariantCulture);
        return unchecked(k switch
        {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => b == 0 ? null : (a == int.MinValue && b == -1 ? null : a / b),
            BinaryOperatorKind.Remainder => b == 0 ? null : (b == -1 ? 0 : a % b),
            BinaryOperatorKind.And => a & b,
            BinaryOperatorKind.Or => a | b,
            BinaryOperatorKind.Xor => a ^ b,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => null,
        });
    }

    private static object? FoldLong(long a, object ro, BinaryOperatorKind k)
    {
        if (k is BinaryOperatorKind.ShiftLeft or BinaryOperatorKind.ShiftRight or BinaryOperatorKind.UnsignedShiftRight)
        {
            int sh = Convert.ToInt32(ro, CultureInfo.InvariantCulture) & 63;
            return k switch { BinaryOperatorKind.ShiftLeft => a << sh, BinaryOperatorKind.ShiftRight => a >> sh, _ => (long)((ulong)a >> sh) };
        }
        long b = Convert.ToInt64(ro, CultureInfo.InvariantCulture);
        return unchecked(k switch
        {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => b == 0 ? null : (a == long.MinValue && b == -1 ? null : a / b),
            BinaryOperatorKind.Remainder => b == 0 ? null : (b == -1 ? 0L : a % b),
            BinaryOperatorKind.And => a & b,
            BinaryOperatorKind.Or => a | b,
            BinaryOperatorKind.Xor => a ^ b,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => null,
        });
    }

    private static object? FoldUInt(uint a, object ro, BinaryOperatorKind k)
    {
        if (k is BinaryOperatorKind.ShiftLeft or BinaryOperatorKind.ShiftRight or BinaryOperatorKind.UnsignedShiftRight)
        {
            int sh = Convert.ToInt32(ro, CultureInfo.InvariantCulture) & 31;
            return k == BinaryOperatorKind.ShiftLeft ? a << sh : a >> sh;
        }
        uint b = Convert.ToUInt32(ro, CultureInfo.InvariantCulture);
        return unchecked(k switch
        {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => b == 0 ? null : a / b,
            BinaryOperatorKind.Remainder => b == 0 ? null : a % b,
            BinaryOperatorKind.And => a & b,
            BinaryOperatorKind.Or => a | b,
            BinaryOperatorKind.Xor => a ^ b,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => null,
        });
    }

    private static object? FoldULong(ulong a, object ro, BinaryOperatorKind k)
    {
        if (k is BinaryOperatorKind.ShiftLeft or BinaryOperatorKind.ShiftRight or BinaryOperatorKind.UnsignedShiftRight)
        {
            int sh = Convert.ToInt32(ro, CultureInfo.InvariantCulture) & 63;
            return k == BinaryOperatorKind.ShiftLeft ? a << sh : a >> sh;
        }
        ulong b = Convert.ToUInt64(ro, CultureInfo.InvariantCulture);
        return unchecked(k switch
        {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => b == 0 ? null : a / b,
            BinaryOperatorKind.Remainder => b == 0 ? null : a % b,
            BinaryOperatorKind.And => a & b,
            BinaryOperatorKind.Or => a | b,
            BinaryOperatorKind.Xor => a ^ b,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => null,
        });
    }

    private static object? FoldDouble(double a, double b, BinaryOperatorKind k)
    {
        return k switch
        {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => a / b,
            BinaryOperatorKind.Remainder => Math.IEEERemainder(a, b) is var _ ? a % b : null,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => null,
        };
    }

    private static object? FoldFloat(float a, float b, BinaryOperatorKind k)
    {
        return k switch
        {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => a / b,
            BinaryOperatorKind.Remainder => a % b,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => null,
        };
    }

    private static object? FoldDecimal(decimal a, decimal b, BinaryOperatorKind k)
    {
        return k switch
        {
            BinaryOperatorKind.Add => a + b,
            BinaryOperatorKind.Subtract => a - b,
            BinaryOperatorKind.Multiply => a * b,
            BinaryOperatorKind.Divide => b == 0 ? null : a / b,
            BinaryOperatorKind.Remainder => b == 0 ? null : a % b,
            BinaryOperatorKind.Equal => a == b,
            BinaryOperatorKind.NotEqual => a != b,
            BinaryOperatorKind.LessThan => a < b,
            BinaryOperatorKind.LessThanOrEqual => a <= b,
            BinaryOperatorKind.GreaterThan => a > b,
            BinaryOperatorKind.GreaterThanOrEqual => a >= b,
            _ => null,
        };
    }
}
