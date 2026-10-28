extern alias SelfHost;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using SelfBinary = SelfHost::Caladesi.Compiler.Bound.BinaryOperatorKind;
using SelfFolding = SelfHost::Caladesi.Compiler.ConstantFolding;
using SelfType = SelfHost::Caladesi.Compiler.Symbols.SpecialType;
using SelfUnary = SelfHost::Caladesi.Compiler.Bound.UnaryOperatorKind;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class ConstantFoldingParityTests
{
    private static readonly Func<object, SpecialType, bool, object?> s_convert = BootstrapMethod<Func<object, SpecialType, bool, object?>>("ConvertNumeric");
    private static readonly Func<UnaryOperatorKind, object, SpecialType, object?> s_unary = BootstrapMethod<Func<UnaryOperatorKind, object, SpecialType, object?>>("FoldUnary");
    private static readonly Func<BinaryOperatorKind, object, object, SpecialType, object?> s_binary = BootstrapMethod<Func<BinaryOperatorKind, object, object, SpecialType, object?>>("FoldBinary");
    private static readonly SpecialType[] s_types =
        [.. Enum.GetValues<SpecialType>(), (SpecialType)(-1), (SpecialType)int.MinValue, (SpecialType)int.MaxValue];
    private static readonly UnaryOperatorKind[] s_unaryKinds =
        [.. Enum.GetValues<UnaryOperatorKind>(), (UnaryOperatorKind)(-1), (UnaryOperatorKind)int.MinValue, (UnaryOperatorKind)int.MaxValue];
    private static readonly BinaryOperatorKind[] s_binaryKinds =
        [.. Enum.GetValues<BinaryOperatorKind>(), (BinaryOperatorKind)(-1), (BinaryOperatorKind)int.MinValue, (BinaryOperatorKind)int.MaxValue];

    [TestMethod]
    public void FoldUnary_AllKindsTypesAndBoxedOperandsMatchBootstrap()
    {
        object?[] values =
        [
            null, false, true, (sbyte)-1, byte.MaxValue, short.MinValue, ushort.MaxValue,
            int.MinValue, -1, 0, 1, int.MaxValue, uint.MaxValue, long.MinValue, -1L, 0L,
            long.MaxValue, ulong.MaxValue, '\0', '\uffff', -0.0f, 1.5f, float.NaN,
            -0.0, 1.5, double.NaN, decimal.MinValue, decimal.MaxValue, -1.5m,
            BigInteger.One << 100, "", "value", new object(),
        ];
        foreach (UnaryOperatorKind kind in s_unaryKinds)
        {
            foreach (SpecialType type in s_types)
            {
                foreach (object? value in values)
                {
                    AssertParity(() => s_unary(kind, value!, type),
                        () => SelfFolding.FoldUnary((SelfUnary)(int)kind, value!, (SelfType)(int)type),
                        $"unary {kind}, {type}, {Describe(value)}");
                }
            }
        }
    }

    [TestMethod]
    public void FoldBinary_AllKindsAndSpecialTypesMatchBootstrap()
    {
        foreach (SpecialType type in s_types)
        {
            (object Left, object Right) operands = type switch
            {
                SpecialType.Int32 => (7, 3),
                SpecialType.Int64 => (7L, 3L),
                SpecialType.UInt32 => (7U, 3U),
                SpecialType.UInt64 => (7UL, 3UL),
                SpecialType.Single => (7.5f, 3.0f),
                SpecialType.Double => (7.5, 3.0),
                SpecialType.Decimal => (7.5m, 3.0m),
                SpecialType.Boolean => (true, false),
                SpecialType.String => ("left", "right"),
                SpecialType.Char => ('a', 'z'),
                _ => (7, 3),
            };
            AssertBinaryKinds(operands.Left, operands.Right, type);
            AssertBinaryKinds(null, null, type);
            AssertBinaryKinds(new object(), new object(), type);
        }
    }

    [TestMethod]
    public void FoldBinary_IntegralBoundariesAndShiftMasksMatchBootstrap()
    {
        (SpecialType Type, object[] Values)[] cases =
        [
            (SpecialType.Int32, [int.MinValue, -1, 0, 1, int.MaxValue]),
            (SpecialType.Int64, [long.MinValue, -1L, 0L, 1L, long.MaxValue]),
            (SpecialType.UInt32, [0U, 1U, 0x80000000U, uint.MaxValue]),
            (SpecialType.UInt64, [0UL, 1UL, 0x8000000000000000UL, ulong.MaxValue]),
            (SpecialType.Char, ['\0', '\u0001', '\u7fff', '\u8000', '\uffff']),
            (SpecialType.Decimal, [decimal.MinValue, -1.5m, -1m, 0m, 0.1m, 1m, 3m, decimal.MaxValue]),
        ];
        foreach ((SpecialType type, object[] values) in cases)
        {
            foreach (object left in values)
            {
                foreach (object right in values)
                {
                    AssertBinaryKinds(left, right, type);
                }
                if (type is SpecialType.Int32 or SpecialType.Int64 or SpecialType.UInt32 or SpecialType.UInt64)
                {
                    foreach (object count in new object[] { int.MinValue, -65, -64, -33, -32, -1, 0, 1, 31, 32, 63, 64, 65, int.MaxValue, 33L, 1.5, "65", null! })
                    {
                        foreach (BinaryOperatorKind kind in new[] { BinaryOperatorKind.ShiftLeft, BinaryOperatorKind.ShiftRight, BinaryOperatorKind.UnsignedShiftRight })
                        {
                            AssertBinary(kind, left, count, type);
                        }
                    }
                }
            }
        }
    }

    [TestMethod]
    public void FoldBinary_FloatingBitsAndBooleanStringCasesMatchBootstrap()
    {
        object[] singles =
        [
            0.0f, -0.0f, 1.0f, -1.0f, 5.5f, float.Epsilon, float.MaxValue,
            float.PositiveInfinity, float.NegativeInfinity, float.NaN,
            BitConverter.Int32BitsToSingle(0x7fc01234), BitConverter.Int32BitsToSingle(unchecked((int)0xffc05678)),
        ];
        object[] doubles =
        [
            0.0, -0.0, 1.0, -1.0, 5.5, double.Epsilon, double.MaxValue,
            double.PositiveInfinity, double.NegativeInfinity, double.NaN,
            BitConverter.Int64BitsToDouble(0x7ff8000000001234L),
            BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000005678UL)),
        ];
        foreach ((SpecialType type, object[] values) in new[]
        {
            (SpecialType.Single, singles), (SpecialType.Double, doubles),
            (SpecialType.Boolean, new object[] { false, true }),
            (SpecialType.String, new object[] { null!, "", "a", "A", "a\0b", "\ud800" }),
        })
        {
            foreach (object left in values)
            {
                foreach (object right in values)
                {
                    AssertBinaryKinds(left, right, type);
                }
                foreach (UnaryOperatorKind kind in s_unaryKinds)
                {
                    AssertParity(() => s_unary(kind, left, type),
                        () => SelfFolding.FoldUnary((SelfUnary)(int)kind, left, (SelfType)(int)type),
                        $"unary floating {kind}, {type}, {Describe(left)}");
                }
            }
        }
    }

    [TestMethod]
    public void FoldBinary_NullInvalidAndConvertibleOperandsMatchExceptionTypes()
    {
        object?[] invalid =
            [null, new object(), "bad", "2", true, (byte)2, -2.5, -1.5, 1.5, 2.5, 3.5, 1.5m, 2.5m, BigInteger.One, ulong.MaxValue];
        foreach (SpecialType type in new[]
        {
            SpecialType.Int32, SpecialType.Int64, SpecialType.UInt32, SpecialType.UInt64,
            SpecialType.Single, SpecialType.Double, SpecialType.Decimal, SpecialType.Boolean,
            SpecialType.String, SpecialType.Char,
        })
        {
            object valid = type switch
            {
                SpecialType.Int32 => 1,
                SpecialType.Int64 => 1L,
                SpecialType.UInt32 => 1U,
                SpecialType.UInt64 => 1UL,
                SpecialType.Single => 1f,
                SpecialType.Double => 1d,
                SpecialType.Decimal => 1m,
                SpecialType.Boolean => true,
                SpecialType.String => "a",
                _ => 'a',
            };
            foreach (object? value in invalid)
            {
                AssertBinaryKinds(valid, value, type);
                AssertBinaryKinds(value, valid, type);
            }
        }
    }

    [TestMethod]
    public void ConvertNumeric_AllTargetsSourcesAndCheckedModesMatchBootstrap()
    {
        object?[] sources =
        [
            null, (sbyte)-1, sbyte.MinValue, sbyte.MaxValue, (byte)0, byte.MaxValue,
            short.MinValue, short.MaxValue, (ushort)0, ushort.MaxValue, int.MinValue, -1, 0, 1,
            int.MaxValue, 0U, uint.MaxValue, long.MinValue, -1L, 0L, long.MaxValue,
            0UL, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue,
            '\0', 'a', '\uffff', false, true, "", "42", new object(), DateTime.UnixEpoch,
            (nint)1, (nuint)1, SpecialType.Int32, DBNull.Value,
            BigInteger.Zero, BigInteger.One, -BigInteger.One, BigInteger.One << 200,
            0f, -0f, 1.5f, -1.5f, float.Epsilon, float.MinValue, float.MaxValue,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity,
            0d, -0d, 1.5d, -1.5d, double.Epsilon, double.MinValue, double.MaxValue,
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            0m, -1.5m, 1.5m, decimal.MinValue, decimal.MaxValue,
        ];
        AssertConversions(sources);
    }

    [TestMethod]
    public void ConvertNumeric_TruncationOverflowAndNaNPayloadsMatchBootstrap()
    {
        List<object?> sources =
        [
            -2.9, -2.5, -1.9, -0.9, 0.9, 1.9, 2.5, 2.9,
            -2.9f, -2.5f, -0.9f, 0.9f, 2.5f, 2.9f,
            -2.9m, -2.5m, -0.9m, 0.9m, 2.5m, 2.9m,
            9007199254740993m, 9007199254740993L, 9007199254740993UL,
            BitConverter.Int32BitsToSingle(0x7fc01234),
            BitConverter.Int32BitsToSingle(unchecked((int)0xffc05678)),
            BitConverter.Int64BitsToDouble(0x7ff8000000001234L),
            BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000005678UL)),
        ];
        foreach (decimal boundary in new decimal[]
        {
            sbyte.MinValue, sbyte.MaxValue, byte.MaxValue, short.MinValue, short.MaxValue,
            ushort.MaxValue, int.MinValue, int.MaxValue, uint.MaxValue, long.MinValue,
            long.MaxValue, ulong.MaxValue,
        })
        {
            foreach (decimal offset in new[] { -1m, -0.9m, 0m, 0.9m, 1m })
            {
                decimal value = boundary + offset;
                sources.Add(value);
                sources.Add((double)value);
                sources.Add((float)value);
            }
        }
        AssertConversions(sources);
    }

    private static void AssertConversions(IEnumerable<object?> sources)
    {
        foreach (object? source in sources)
        {
            foreach (SpecialType target in s_types)
            {
                foreach (bool unchecked_ in new[] { false, true })
                {
                    AssertParity(() => s_convert(source!, target, unchecked_),
                        () => SelfFolding.ConvertNumeric(source!, (SelfType)(int)target, unchecked_),
                        $"convert {Describe(source)} to {target}, unchecked={unchecked_}");
                }
            }
        }
    }

    private static void AssertBinaryKinds(object? left, object? right, SpecialType type)
    {
        foreach (BinaryOperatorKind kind in s_binaryKinds)
        {
            AssertBinary(kind, left, right, type);
        }
    }

    private static void AssertBinary(BinaryOperatorKind kind, object? left, object? right, SpecialType type) =>
        AssertParity(() => s_binary(kind, left!, right!, type),
            () => SelfFolding.FoldBinary((SelfBinary)(int)kind, left!, right!, (SelfType)(int)type),
            $"binary {kind}, {type}, {Describe(left)}, {Describe(right)}");

    private static T BootstrapMethod<T>(string name) where T : Delegate =>
        typeof(Compilation).Assembly.GetType("Caladesi.Compiler.ConstantFolding", throwOnError: true)!
            .GetMethod(name)!.CreateDelegate<T>();

    private static void AssertParity(Func<object?> original, Func<object?> migrated, string context)
    {
        (object? expected, Type? expectedException) = Evaluate(original);
        (object? actual, Type? actualException) = Evaluate(migrated);
        Assert.AreEqual(expectedException, actualException, context);
        if (expectedException is not null)
        {
            return;
        }
        Assert.AreEqual(expected?.GetType(), actual?.GetType(), context);
        switch (expected)
        {
            case float value:
                Assert.AreEqual(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits((float)actual!), context);
                break;
            case double value:
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits((double)actual!), context);
                break;
            default:
                Assert.AreEqual(expected, actual, context);
                break;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Parity includes the exact type of every exception escaping either implementation.")]
    private static (object? Value, Type? Exception) Evaluate(Func<object?> action)
    {
        try
        {
            return (action(), null);
        }
        catch (Exception exception)
        {
            return (null, exception.GetType());
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        float single => $"Single:{single.ToString("R", CultureInfo.InvariantCulture)}(0x{BitConverter.SingleToInt32Bits(single):x8})",
        double number => $"Double:{number.ToString("R", CultureInfo.InvariantCulture)}(0x{BitConverter.DoubleToInt64Bits(number):x16})",
        _ => $"{value.GetType().Name}:{Convert.ToString(value, CultureInfo.InvariantCulture)}",
    };
}
