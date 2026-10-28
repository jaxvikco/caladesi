extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Tests.Emit;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HE = SelfHost::Caladesi.Compiler.Emit;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using H = SelfHost::Caladesi.Compiler;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class AttributeBinderParityTests
{
    [TestMethod]
    public void FixedPrimitiveScalars_BytesAndFailuresMatchOriginal()
    {
        Fixture f = new();
        (string Name, object?[] Values)[] cases =
        [
            ("Boolean", [false, true, null, 0, 1, "true", "invalid"]),
            ("Char", ['\0', '\uffff', "a", "Ω", "", "ab", 65535, -1, 65536, null]),
            ("SByte", [sbyte.MinValue, sbyte.MaxValue, null, 128, "invalid"]),
            ("Byte", [byte.MinValue, byte.MaxValue, null, -1]),
            ("Int16", [short.MinValue, short.MaxValue, null, 32768]),
            ("UInt16", [ushort.MinValue, ushort.MaxValue, null, -1]),
            ("Int32", [int.MinValue, int.MaxValue, null, long.MaxValue, "invalid"]),
            ("UInt32", [uint.MinValue, uint.MaxValue, null, -1]),
            ("Int64", [long.MinValue, long.MaxValue, null, ulong.MaxValue]),
            ("UInt64", [ulong.MinValue, ulong.MaxValue, null, -1]),
            ("Single", [-0.0f, float.Epsilon, float.NaN, float.PositiveInfinity, null]),
            ("Double", [-0.0, double.Epsilon, double.NaN, double.NegativeInfinity, null]),
            ("Decimal", [12, null]), // The original's default branch writes Int32.
        ];
        foreach ((string name, object?[] values) in cases)
        {
            TypePair type = f.Type("System." + name);
            f.Tag(type);
            foreach (object? value in values)
            {
                f.Fixed(type, Literal(type, value));
                f.Fixed(new(new NullableTypeSymbol(type.Expected), new HS.NullableTypeSymbol(type.Actual)),
                    Literal(type, value));
            }
        }
    }

    [TestMethod]
    public void Strings_UseSerializedUtf8AndPreserveNullSentinel()
    {
        Fixture f = new();
        TypePair type = f.Type("System.String");
        foreach (object? value in new object?[] { null, "", "ascii", "λ🌴", "a\0b", "\ud800", new string('é', 200), 42 })
            f.Fixed(type, Literal(type, value));
        CollectionAssert.AreEqual(new byte[] { 0xFF }, f.Fixed(type, Literal(type, null)));
    }

    [TestMethod]
    public void Arrays_NullEmptyNestedAndWrappedArraysMatchOriginal()
    {
        Fixture f = new();
        foreach (string name in new[] { "System.Int32", "System.String", "System.DayOfWeek" })
        {
            TypePair element = f.Type(name);
            TypePair array = ArrayType(element);
            f.Tag(array);
            f.Fixed(array, Literal(array, null));
            f.Fixed(array, Array(array));
            ExpressionPair populated = Array(array, Literal(element, name == "System.String" ? "λ" : 42),
                Literal(element, null));
            f.Fixed(array, populated);
            // WriteFixed intentionally does not unwrap conversions around arrays.
            f.Fixed(array, Convert(populated, ConversionKind.Identity, array));
            TypePair nested = ArrayType(array);
            f.Tag(nested);
            f.Fixed(nested, Array(nested, populated, Literal(array, null)));
        }
    }

    [TestMethod]
    public void TypeNames_IncludeAssemblyAndPreserveNonNamedDisplayFallback()
    {
        Fixture f = new();
        TypePair systemType = f.Type("System.Type");
        foreach (TypePair operand in new[]
        {
            f.Type("System.String"), f.Type("System.Environment+SpecialFolder"),
            ArrayType(f.Type("System.Int32")),
            new(new NullableTypeSymbol(f.Type("System.Int32").Expected),
                new HS.NullableTypeSymbol(f.Type("System.Int32").Actual)),
        })
        {
            ExpressionPair expression = new(new BoundTypeOf(operand.Expected, systemType.Expected),
                new HB.BoundTypeOf(operand.Actual, systemType.Actual));
            f.Fixed(systemType, expression);
            f.Fixed(systemType, Convert(expression, ConversionKind.Identity, systemType));
        }
        f.Fixed(systemType, Literal(systemType, null));
        f.Tag(systemType);

        HE.AttributeBinder noAssembly = new((_, _) => null, _ => null, f.Special);
        BlobBuilder bytes = new();
        Invoke(noAssembly, "WriteFixed", bytes, systemType.Actual,
            new HB.BoundTypeOf(f.Type("System.String").Actual, systemType.Actual));
        BlobBuilder expected = new();
        expected.WriteSerializedString("System.String");
        CollectionAssert.AreEqual(expected.ToArray(), bytes.ToArray());
    }

    [TestMethod]
    public void EnumsBoxedValuesAndConversions_PreserveOriginalUnwrappingRules()
    {
        Fixture f = new();
        TypePair obj = f.Type("System.Object");
        foreach (string name in new[] { "System.Int32", "System.String", "System.Char",
                     "System.DayOfWeek", "System.AttributeTargets", "System.Reflection.BindingFlags",
                     "System.Reflection.Metadata.SignatureTypeCode" })
        {
            TypePair type = f.Type(name);
            f.Tag(type);
            object value = name == "System.String" ? "λ" : name == "System.Char" ? "x" : 7;
            ExpressionPair literal = Literal(type, value);
            f.Fixed(type, literal);
            f.Fixed(obj, literal);
            f.Fixed(obj, Convert(literal, ConversionKind.Boxing, obj));
            f.Fixed(obj, Convert(literal, ConversionKind.ImplicitReference, obj));
            foreach (ConversionKind kind in Enum.GetValues<ConversionKind>())
            {
                ExpressionPair converted = Convert(literal, kind, type);
                f.Fixed(type, converted);
                HB.BoundConversion folded = new(literal.Actual, (HB.ConversionKind)(int)kind, type.Actual);
                folded.ConstantFolded = new HS.ConstantValue(3);
                f.Fixed(type, new(new BoundConversion(literal.Expected, kind, type.Expected)
                    { ConstantFolded = new ConstantValue(3) }, folded));
            }
            f.Fixed(type, Convert(Convert(literal, ConversionKind.Identity, type), ConversionKind.Boxing, type));
        }
        TypePair intArray = ArrayType(f.Type("System.Int32"));
        f.Fixed(obj, Convert(Array(intArray, Literal(f.Type("System.Int32"), 1)), ConversionKind.ImplicitReference, obj));
        TypePair systemType = f.Type("System.Type");
        f.Fixed(obj, Convert(new(new BoundTypeOf(obj.Expected, systemType.Expected),
            new HB.BoundTypeOf(obj.Actual, systemType.Actual)), ConversionKind.ImplicitReference, obj));
        // Object-typed null / identity-to-object recurse indefinitely in the original.
        // They are deliberately neither "fixed" nor executed in-process here.
    }

    [TestMethod]
    public void Bind_UsesRealMetadataConstructorsAndNamedFieldPropertyTags()
    {
        Fixture f = new();
        f.Bind("System.ObsoleteAttribute", "System.String", Literal(f.Type("System.String"), "λ"),
            ("DiagnosticId", Literal(f.Type("System.String"), "CA0001")),
            ("IsError", Literal(f.Type("System.Boolean"), true)));
        f.Bind("System.Runtime.InteropServices.StructLayoutAttribute", "System.Runtime.InteropServices.LayoutKind",
            Literal(f.Type("System.Runtime.InteropServices.LayoutKind"), 2),
            ("CharSet", Literal(f.Type("System.Runtime.InteropServices.CharSet"), 3)),
            ("Pack", Literal(f.Type("System.Int32"), 8)));
        int calls = 0;
        HE.AttributeBinder absent = new((decorator, context) =>
        {
            calls++;
            Assert.IsNull(decorator);
            Assert.AreSame(f.Type("System.String").Actual, context);
            return null;
        }, _ => throw new AssertFailedException("No assembly lookup for unbound attribute"),
            _ => throw new AssertFailedException("No type lookup for unbound attribute"));
        Assert.IsNull(absent.Bind(null!, f.Type("System.String").Actual));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void Dto_PreservesRootNamespaceAndDoesNotReferenceBootstrap()
    {
        Assert.AreEqual("Caladesi.Compiler.BoundAttribute", typeof(H.BoundAttribute).FullName);
        Type original = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.BoundAttribute", true)!;
        Assert.IsTrue(original.IsSealed && typeof(H.BoundAttribute).IsSealed);
        foreach (string name in new[] { "AttributeType", "Constructor", "Arguments", "Named" })
        {
            Assert.IsNotNull(original.GetProperty(name));
            Assert.IsNotNull(typeof(H.BoundAttribute).GetProperty(name));
            Assert.IsFalse(typeof(H.BoundAttribute).GetProperty(name)!.CanWrite);
        }
        Assert.IsFalse(typeof(HE.AttributeBinder).Assembly.GetReferencedAssemblies()
            .Any(reference => reference.Name == "Caladesi.Compiler"));
    }

    private sealed record TypePair(TypeSymbol Expected, HS.TypeSymbol Actual);
    private sealed record ExpressionPair(BoundExpression Expected, HB.BoundExpression Actual);

    private static ExpressionPair Literal(TypePair type, object? value) =>
        new(new BoundLiteral(new ConstantValue(value), type.Expected),
            new HB.BoundLiteral(new HS.ConstantValue(value), type.Actual));

    private static ExpressionPair Convert(ExpressionPair value, ConversionKind kind, TypePair type) =>
        new(new BoundConversion(value.Expected, kind, type.Expected),
            new HB.BoundConversion(value.Actual, (HB.ConversionKind)(int)kind, type.Actual));

    private static TypePair ArrayType(TypePair element) =>
        new(new ArrayTypeSymbol(element.Expected, null), new HS.ArrayTypeSymbol(element.Actual, null));

    private static ExpressionPair Array(TypePair type, params ExpressionPair[] elements) =>
        new(new BoundArrayCreation((ArrayTypeSymbol)type.Expected, null,
                elements.Select(element => element.Expected).ToImmutableArray()),
            new HB.BoundArrayCreation((HS.ArrayTypeSymbol)type.Actual, null,
                elements.Select(element => element.Actual).ToImmutableArray()));

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(target, args);

    private sealed class Fixture
    {
        private static readonly Lazy<SelfHost::Caladesi.Compiler.Symbols.Metadata.ReferenceSet> s_references =
            new(() => SelfHost::Caladesi.Compiler.Symbols.Metadata.ReferenceSet.Create(TestReferences.Paths));
        private readonly Compilation compilation = TestCompiler.Create("", OutputKind.DynamicallyLinkedLibrary);
        private readonly object oracle;
        private readonly HE.AttributeBinder actual;

        public Fixture()
        {
            oracle = Activator.CreateInstance(
                typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.AttributeBinder", true)!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [compilation, null], null)!;
            actual = new((_, _) => null, type => type.ContainingAssembly?.Name, Special);
        }

        public HS.NamedTypeSymbol Special(HS.SpecialType type) => s_references.Value.GetSpecialType(type)!;
        public TypePair Type(string name) =>
            new(compilation.GetWellKnownType(name)!, s_references.Value.FindType(name)!);

        public void Tag(TypePair type) => Compare("WriteTypeTag", [type.Expected], [type.Actual]);
        public byte[] Fixed(TypePair type, ExpressionPair value) =>
            Compare("WriteFixed", [type.Expected, value.Expected], [type.Actual, value.Actual]);

        private byte[] Compare(string method, object?[] expectedArgs, object?[] actualArgs)
        {
            BlobBuilder expected = new();
            BlobBuilder result = new();
            Exception? left = Capture(() => Invoke(oracle, method, [expected, .. expectedArgs]));
            Exception? right = Capture(() => Invoke(actual, method, [result, .. actualArgs]));
            Assert.AreEqual(left?.GetType(), right?.GetType(), method);
            CollectionAssert.AreEqual(expected.ToArray(), result.ToArray(), method);
            return result.ToArray();
        }

        private static Exception? Capture(Action action)
        {
            try { action(); return null; }
            catch (TargetInvocationException error) { return error.InnerException; }
        }

        public void Bind(string name, string parameterType, ExpressionPair argument,
            params (string Name, ExpressionPair Value)[] named)
        {
            TypePair type = Type(name);
            NamedTypeSymbol originalType = (NamedTypeSymbol)type.Expected;
            HS.NamedTypeSymbol selfType = (HS.NamedTypeSymbol)type.Actual;
            TypePair parameter = Type(parameterType);
            MethodSymbol constructor = originalType.InstanceConstructors.Single(method =>
                method.Parameters.Length == 1 && method.Parameters[0].Type.StripNullable().Equals(parameter.Expected));
            HS.MethodSymbol selfConstructor = selfType.InstanceConstructors.Single(method =>
                method.Parameters.Length == 1 && method.Parameters[0].Type.StripNullable().Equals(parameter.Actual));
            BlobBuilder expected = new();
            expected.WriteUInt16(1);
            Invoke(oracle, "WriteFixed", expected, constructor.Parameters[0].Type, argument.Expected);
            expected.WriteUInt16((ushort)named.Length);
            ImmutableArray<(HS.Symbol, HB.BoundExpression)>.Builder members =
                ImmutableArray.CreateBuilder<(HS.Symbol, HB.BoundExpression)>();
            foreach ((string memberName, ExpressionPair value) in named)
            {
                Symbol member = originalType.GetMembers(memberName).Single(member => member is FieldSymbol or PropertySymbol);
                HS.Symbol selfMember = selfType.GetMembers(memberName).Single(member => member is HS.FieldSymbol or HS.PropertySymbol);
                TypeSymbol memberType = member is PropertySymbol property ? property.Type : ((FieldSymbol)member).Type;
                expected.WriteByte(member is PropertySymbol ? (byte)0x54 : (byte)0x53);
                Invoke(oracle, "WriteTypeTag", expected, memberType);
                expected.WriteSerializedString(member.Name);
                Invoke(oracle, "WriteFixed", expected, memberType, value.Expected);
                members.Add((selfMember, value.Actual));
            }
            H.BoundAttribute bound = new(selfType, selfConstructor, [argument.Actual], members.ToImmutable());
            HE.AttributeBinder binder = new((_, context) =>
            {
                Assert.AreSame(selfType, context);
                return bound;
            }, symbol => symbol.ContainingAssembly?.Name, Special);
            var result = binder.Bind(null!, selfType);
            Assert.IsTrue(result.HasValue);
            Assert.AreSame(selfConstructor, result.Value.Item1);
            CollectionAssert.AreEqual(expected.ToArray(), result.Value.Item2.ToArray());
            Assert.AreSame(selfType, bound.AttributeType);
            Assert.AreSame(selfConstructor, bound.Constructor);
            Assert.AreSame(argument.Actual, bound.Arguments[0]);
        }
    }
}
