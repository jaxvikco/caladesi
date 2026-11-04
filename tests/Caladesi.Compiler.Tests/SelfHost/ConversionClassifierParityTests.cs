extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using System.Numerics;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Tests.Emit;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using Classifier = SelfHost::Caladesi.Compiler.Binding.ConversionClassifier;
using HConversion = SelfHost::Caladesi.Compiler.Conversion;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class ConversionClassifierParityTests
{
    private static readonly Lazy<HM.ReferenceSet> References = new(() => HM.ReferenceSet.Create(TestReferences.Paths));
    private static readonly Lazy<Binder> Oracle = new(() =>
    {
        var compilation = TestCompiler.Create("class Context {}", OutputKind.DynamicallyLinkedLibrary);
        return new Binder(compilation, compilation.GetFile(compilation.SyntaxTrees.Single()), null, null);
    });
    private static readonly MethodInfo Implicit = typeof(Binder).GetMethod("ClassifyImplicit", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Explicit = typeof(Binder).GetMethod("ClassifyExplicit", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [TestMethod]
    public void NoConstant_AllSpecialTypesAndNumericPairsMatchRealBinder()
    {
        Pair[] types = [.. Enum.GetValues<SpecialType>().Select(s => Named(s.ToString(),
            s is SpecialType.Object or SpecialType.String or SpecialType.Array ? TypeKind.Class : TypeKind.Struct, special: s))];
        Matrix(types);
        string[] numericNames = ["SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Char",
            "Single", "Double", "Decimal", "IntPtr", "UIntPtr"];
        Matrix(numericNames.Select(n => Metadata("System." + n)));
    }

    [TestMethod]
    public void IntegralConstants_AllBoxedTagsAndBoundariesPreservePayloadTypes()
    {
        string[] integral = ["SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64"];
        Pair[] targets = [.. integral.Concat(["Char", "IntPtr", "UIntPtr", "Single", "Double", "Decimal", "String", "Boolean"]).Select(n => Metadata("System." + n))];
        object?[] values = [
            null, (sbyte)-128, (sbyte)-1, (sbyte)127, (byte)0, byte.MaxValue,
            short.MinValue, (short)-1, short.MaxValue, (ushort)0, ushort.MaxValue,
            int.MinValue, -32769, -32768, -129, -128, -1, 0, 1, 127, 128, 255, 256, 32767, 32768, 65535, 65536, int.MaxValue,
            uint.MinValue, (uint)int.MaxValue + 1, uint.MaxValue,
            long.MinValue, (long)int.MinValue - 1, -1L, 0L, (long)uint.MaxValue + 1, long.MaxValue,
            0UL, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue,
            'a', '\0', true, false, 0f, 1d, 0m, decimal.MinValue, new BigInteger(1),
            "", "a", "ab", "\ud800", "\udc00", "\ud83d\ude00"
        ];
        Classifier classifier = new();
        foreach (var sourceName in integral)
        {
            foreach (var value in values)
            {
                foreach (var target in targets)
                {
                    Check(Metadata("System." + sourceName), target, value, hasConstant: true, classifier: classifier);
                }
            }
        }
    }

    [TestMethod]
    public void StringConstants_CharUsesExactlyOneUtf16CodeUnitBeforeEnumLookup()
    {
        Pair source = Metadata("System.String"), target = Metadata("System.Char");
        var calls = 0;
        Classifier classifier = new((_, _) => { calls++; return null; });
        foreach (var value in new[] { "", "a", "é", "\ud800", "\udc00", "\ud83d\ude00", "ab" })
        {
            Check(source, target, value, true, classifier);
            Check(source, Nullable(target), value, true, classifier);
        }
        Assert.IsGreaterThan(0, calls);
        calls = 0;
        _ = classifier.ClassifyImplicit(new HB.BoundLiteral(new H.ConstantValue("x"), source.Self), source.Self, target.Self);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void NullableErrorNeverVoidAndNull_OrderingAndNestedLayersMatch()
    {
        Pair integer = Metadata("System.Int32"), wide = Metadata("System.Int64"), text = Metadata("System.String"), obj = Metadata("System.Object");
        Pair[] core = [integer, wide, text, obj, Metadata("System.Void"), Named("E", TypeKind.Enum),
            new(ErrorTypeSymbol.Instance, H.ErrorTypeSymbol.Instance),
            new(NeverTypeSymbol.Instance, H.NeverTypeSymbol.Instance),
            new(NullTypeSymbol.Instance, H.NullTypeSymbol.Instance)];
        Pair[] types = [.. core, .. core.Select(Nullable), .. core.Select(t => Nullable(Nullable(t)))];
        Matrix(types);
        foreach (var source in new[] { integer, text })
        {
            foreach (var target in types)
            {
                foreach (var value in new object?[] { null, 0, -1L, "x" })
                {
                    Check(source, target, value, true);
                }
            }
        }
    }

    [TestMethod]
    public void MetadataArraysVarianceBoxingAndUnboxing_Match()
    {
        Pair obj = Metadata("System.Object"), text = Metadata("System.String"), number = Metadata("System.Int32");
        List<Pair> types = [obj, text, number, Metadata("System.Int64"), Metadata("System.Boolean"),
            Metadata("System.ValueType"), Metadata("System.Enum"), Metadata("System.Array"), Metadata("System.DayOfWeek"),
            Metadata("System.Exception"), Metadata("System.ArgumentException"), Metadata("System.IDisposable"),
            Metadata("System.IComparable"), Metadata("System.Collections.IEnumerable")];
        foreach (var name in new[] { "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.IList`1",
            "System.Collections.Generic.ICollection`1", "System.Collections.Generic.IReadOnlyList`1",
            "System.Collections.Generic.IReadOnlyCollection`1", "System.Collections.Generic.List`1", "System.IComparable`1",
            "System.Action`1", "System.Func`1" })
        {
            foreach (var argument in new[] { obj, text, number })
            {
                types.Add(Construct(Metadata(name), argument));
            }
        }

        foreach (var element in new[] { obj, text, number, Array(text) })
        {
            foreach (var rank in new[] { 1, 2, 3 })
            {
                types.Add(Array(element, rank));
            }
        }

        types.AddRange([Parameter("T"), Parameter("Ref", reference: true), Parameter("Value", value: true),
            Parameter("Constrained", constraints: [Metadata("System.IDisposable")])]);
        Matrix(types);
    }

    [TestMethod]
    public void SyntheticInterfacesSealedClassesEnumsAndTypeParameterChains_Match()
    {
        Pair obj = Metadata("System.Object"), iface = Named("IMarker", TypeKind.Interface);
        Pair root = Named("Root", TypeKind.Class, obj), derived = Named("Derived", TypeKind.Class, root);
        var sealedClass = Named("Sealed", TypeKind.Class, root, interfaces: [iface], isSealed: true);
        var unrelatedSealed = Named("OtherSealed", TypeKind.Class, obj, isSealed: true);
        Pair structure = Named("Struct", TypeKind.Struct, interfaces: [iface]), enumeration = Named("Enum", TypeKind.Enum, interfaces: [iface]);
        var parameter = Parameter("T", constraints: [derived, iface]);
        Matrix([obj, iface, root, derived, sealedClass, unrelatedSealed, structure, enumeration,
            Named("IOther", TypeKind.Interface), Parameter("Unconstrained"), parameter, Parameter("Chain", constraints: [parameter]),
            Array(root), Array(derived), Array(sealedClass), Array(iface), Array(structure)]);
    }

    [TestMethod]
    public void Unions_ExactFirstNumericPreferenceWideningEmptyCasesAndExtractionMatch()
    {
        Pair integer = Metadata("System.Int32"), wide = Metadata("System.Int64"), small = Metadata("System.Byte");
        Pair text = Metadata("System.String"), obj = Metadata("System.Object"), real = Metadata("System.Double");
        Pair narrowUnion = Union("Narrow", integer, text), widerUnion = Union("Wide", obj, wide);
        Pair numeric = Union("Numeric", real, wide, integer), numericReverse = Union("NumericReverse", integer, wide, real);
        Pair empty = Union("Empty"), impossible = Union("Impossible", small);
        Pair[] types = [integer, wide, small, text, obj, real, narrowUnion, widerUnion, numeric, numericReverse, empty, impossible,
            Union("Duplicates", integer, integer), Union("ReferenceFirst", obj, wide), Union("NumericFirst", wide, obj)];
        Matrix(types);
        foreach (var target in types)
        {
            foreach (var value in new object[] { 1, -1, 255, 256, "x" })
            {
                Check(integer, target, value, true);
            }
        }

        Assert.AreEqual(2, new Classifier().ClassifyImplicit(null, integer.Self, numeric.Self).CaseIndex);
        Assert.AreEqual(-3, new Classifier().ClassifyImplicit(null, narrowUnion.Self, widerUnion.Self).CaseIndex);
    }

    [TestMethod]
    public void Tuples_ElementwiseMarkerArityFailureAndNullableElementsMatch()
    {
        Pair integer = Metadata("System.Int32"), wide = Metadata("System.Int64"), text = Metadata("System.String"), obj = Metadata("System.Object");
        Pair tuple = Tuple("A", integer, text), other = Tuple("B", wide, obj);
        Pair[] types = [tuple, other, Tuple("EmptyA"), Tuple("EmptyB"), Tuple("One", integer),
            Tuple("Bad", text, integer), Tuple("Nullable", Nullable(wide), Nullable(obj)), Tuple("Nested", tuple, integer),
            Tuple("NestedWide", other, wide), integer, obj];
        Matrix(types);
        Assert.AreEqual(-2, new Classifier().ClassifyImplicit(null, tuple.Self, other.Self).CaseIndex);
        Check(Nullable(tuple), Nullable(other));
    }

    [TestMethod]
    public void SourceLiteralEnums_RealCompilationSyntaxAndTypedFieldLookupMatch()
    {
        var compilation = TestCompiler.Create("""type Direction = "up" | "down" | "x" | "a-b" | "a_b";""",
            OutputKind.DynamicallyLinkedLibrary);
        var file = compilation.GetFile(compilation.SyntaxTrees.Single());
        var original = file.Types.OfType<SourceLiteralEnumSymbol>().Single();
        Pair type = new(original, new SelfNamed("Direction", H.TypeKind.Enum));
        Pair text = Metadata("System.String"), character = Metadata("System.Char");
        Binder binder = new(compilation, file, null, null);
        List<(H.TypeSymbol Type, string Text)> calls = [];
        Dictionary<SourceFieldSymbol, H.FieldSymbol> fields = [];
        Classifier classifier = new((target, literal) =>
        {
            calls.Add((target, literal));
            if (!ReferenceEquals(target, type.Self))
            {
                return null;
            }

            var member = original.FindMember(literal);
            if (member == null)
            {
                return null;
            }

            if (!fields.TryGetValue(member, out var result))
            {
                fields.Add(member, result = new SelfField(member.Name, type.Self, () =>
                    member.ConstantValue is { } cv ? new H.ConstantValue(cv.Value) : null));
            }

            return result;
        });
        foreach (var literal in new[] { "up", "down", "x", "a-b", "a_b", "", "missing", "\ud800" })
        {
            foreach (var target in new[] { type, Nullable(type), Nullable(Nullable(type)), text, character })
            {
                Check(text, target, literal, true, classifier, binder);
            }
        }

        calls.Clear();
        Check(text, Nullable(type), "down", true, classifier, binder);
        Assert.AreSame(type.Self, calls[0].Type);
        calls.Clear();
        Check(text, Nullable(Nullable(type)), "down", true, classifier, binder);
        _ = Assert.IsInstanceOfType<H.NullableTypeSymbol>(calls[0].Type);
        Assert.IsTrue(calls.Any(c => ReferenceEquals(c.Type, type.Self)));
        calls.Clear();
        Check(text, text, "up", true, classifier, binder);
        Assert.IsEmpty(calls);
        Assert.IsFalse(new Classifier().ClassifyImplicit(new HB.BoundLiteral(new H.ConstantValue("up"), text.Self), text.Self, type.Self).Exists);
        var found = original.FindMember("up")!;
        typeof(SourceFieldSymbol).GetField("_constDone", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(found, true);
        foreach (var constant in new ConstantValue?[] { null, new(null) })
        {
            typeof(SourceFieldSymbol).GetField("_const", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(found, constant);
            Check(text, type, "up", true, classifier, binder);
            Check(text, Nullable(type), "up", true, classifier, binder);
        }
    }

    [TestMethod]
    public void LiteralEnumResolver_FoundFieldWithMissingOrNullConstantIsStillAConversion()
    {
        Pair text = Metadata("System.String"), type = Named("Literal", TypeKind.Enum);
        foreach (var value in new H.ConstantValue?[] { null, new(null), new(17) })
        {
            Classifier classifier = new((target, _) => ReferenceEquals(target, type.Self) ?
                new SelfField("member", type.Self, () => value) : null);
            foreach (var target in new[] { type, Nullable(type) })
            {
                var conversion = classifier.ClassifyImplicit(new HB.BoundLiteral(new H.ConstantValue("up"), text.Self), text.Self, target.Self);
                Assert.IsTrue(conversion.Exists);
                Assert.AreEqual(HB.ConversionKind.EnumNumeric, conversion.Kind);
                Assert.IsNotNull(conversion.Constant);
                Assert.AreEqual(value?.Value, conversion.Constant.Value);
                Assert.AreEqual(target.Self is H.NullableTypeSymbol, conversion.WrapNullable);
            }
        }
    }

    [TestMethod]
    public void UserDefinedOperators_ScoresOrderBasesAndEligibilityMatch()
    {
        Pair integer = Metadata("System.Int32"), wide = Metadata("System.Int64"), obj = Metadata("System.Object");
        Pair source = Named("Source", TypeKind.Class), target = Named("Target", TypeKind.Class);
        _ = AddOperator(source, "op_Implicit", obj, target);
        (_, var Self) = AddOperator(source, "op_Implicit", source, target);
        _ = AddOperator(target, "op_Implicit", source, target);
        Check(source, target);
        Assert.AreSame(Self, new Classifier().ClassifyImplicit(null, source.Self, target.Self).Method);
        Pair first = Named("First", TypeKind.Struct), second = Named("Second", TypeKind.Struct);
        var firstMethod = AddOperator(first, "op_Implicit", first, second);
        _ = AddOperator(first, "op_Implicit", first, second);
        _ = AddOperator(second, "op_Implicit", first, second);
        Assert.AreSame(firstMethod.Self, new Classifier().ClassifyImplicit(null, first.Self, second.Self).Method);
        Check(first, second);
        var explicitSource = Named("Explicit", TypeKind.Struct);
        _ = AddOperator(explicitSource, "op_Explicit", explicitSource, target);
        _ = AddOperator(explicitSource, "op_Implicit", target, explicitSource, isStatic: false);
        _ = AddOperator(explicitSource, "WrongName", explicitSource, target);
        _ = AddOperator(explicitSource, "op_Implicit", explicitSource, target, kind: MethodKind.Ordinary);
        _ = AddOperator(explicitSource, "op_Implicit", explicitSource, target, parameterCount: 0);
        _ = AddOperator(explicitSource, "op_Implicit", explicitSource, target, parameterCount: 2);
        Pair baseType = Named("Base", TypeKind.Class), derived = Named("Derived", TypeKind.Class, baseType);
        _ = AddOperator(baseType, "op_Implicit", baseType, target);
        var numeric = Named("Numeric", TypeKind.Struct);
        _ = AddOperator(numeric, "op_Implicit", numeric, integer);
        _ = AddOperator(numeric, "op_Implicit", numeric, wide);
        var invalidOutput = Named("InvalidOutput", TypeKind.Struct);
        _ = AddOperator(invalidOutput, "op_Implicit", invalidOutput, target);
        var constantTarget = Named("ConstantTarget", TypeKind.Struct);
        _ = AddOperator(constantTarget, "op_Implicit", Metadata("System.Byte"), constantTarget);
        var objectWithOperator = Named("ObjectWithOperator", TypeKind.Class, special: SpecialType.Object);
        var objectDerived = Named("ObjectDerived", TypeKind.Class, objectWithOperator);
        _ = AddOperator(objectWithOperator, "op_Implicit", objectDerived, target);
        Matrix([source, target, first, second, explicitSource, baseType, derived, numeric, invalidOutput, integer, wide, obj]);
        Check(Nullable(first), Nullable(second));
        Check(Nullable(explicitSource), target);
        Check(numeric, wide, 1, true);
        Check(integer, constantTarget);
        Check(integer, constantTarget, 1, true);
        Check(integer, constantTarget, 256, true);
        Check(objectDerived, target);
    }

    [TestMethod]
    public void UserDefinedDepth_DisablesNestedSearchAndRestoresAfterException()
    {
        Pair source = Named("Source", TypeKind.Struct), intermediate = Named("Intermediate", TypeKind.Struct),
            target = Named("Target", TypeKind.Struct);
        _ = AddOperator(source, "op_Implicit", source, intermediate);
        _ = AddOperator(intermediate, "op_Implicit", intermediate, target);
        Check(source, target);
        var wrapper = Union("UnionParameter", intermediate);
        _ = AddOperator(target, "op_Implicit", wrapper, target);
        Check(source, target);
        var throwing = Named("Throwing", TypeKind.Struct);
        ((OriginalNamed)throwing.Original).ThrowMembers = true;
        ((SelfNamed)throwing.Self).ThrowMembers = true;
        Classifier classifier = new();
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => classifier.ClassifyImplicit(null, throwing.Self, target.Self));
        _ = Assert.ThrowsExactly<TargetInvocationException>(() => Implicit.Invoke(Oracle.Value, [null, throwing.Original, target.Original, true]));
        (var Original, var Self) = AddOperator(source, "op_Implicit", source, target);
        Check(source, target, classifier: classifier);
        Assert.AreSame(Self, classifier.ClassifyImplicit(null, source.Self, target.Self).Method);
    }

    [TestMethod]
    public void WholeLibrary_VerifiesWithoutBootstrapDependency()
    {
        var library = typeof(Classifier).Assembly;
        Assert.IsFalse(library.GetReferencedAssemblies().Any(a => a.Name == "Caladesi.Compiler"));
        Assert.IsEmpty(ILVerifier.Verify(library.GetName().Name!, File.ReadAllBytes(library.Location)));
    }

    private static void Matrix(IEnumerable<Pair> pairs)
    {
        Pair[] types = [.. pairs];
        Classifier classifier = new();
        foreach (var source in types)
        {
            foreach (var target in types)
            {
                Check(source, target, classifier: classifier);
            }
        }
    }

    private static void Check(Pair source, Pair target, object? value = null, bool hasConstant = false,
        Classifier? classifier = null, Binder? binder = null)
    {
        classifier ??= new();
        binder ??= Oracle.Value;
        BoundExpression? expression = hasConstant ? new BoundLiteral(new ConstantValue(value), source.Original) : null;
        HB.BoundExpression? selfExpression = hasConstant ? new HB.BoundLiteral(new H.ConstantValue(value), source.Self) : null;
        var context = $"{source.Original.Name} -> {target.Original.Name}; {value?.GetType().Name}:{value}";
        foreach (var allow in new[] { false, true })
        {
            var expected = (Conversion)Implicit.Invoke(binder, [expression, source.Original, target.Original, allow])!;
            var actual = classifier.ClassifyImplicit(selfExpression, source.Self, target.Self, allow);
            AssertConversion(expected, actual, context + "; allow=" + allow);
            if (allow)
            {
                AssertConversion(expected, classifier.ClassifyImplicit(selfExpression, source.Self, target.Self), context);
            }
        }
        AssertConversion((Conversion)Explicit.Invoke(binder, [expression, source.Original, target.Original])!,
            classifier.ClassifyExplicit(selfExpression, source.Self, target.Self), "explicit " + context);
    }

    private static void AssertConversion(Conversion expected, HConversion actual, string context)
    {
        Assert.AreEqual(expected.Exists, actual.Exists, context);
        Assert.AreEqual((int)expected.Kind, (int)actual.Kind, context);
        Assert.AreEqual(expected.IsImplicit, actual.IsImplicit, context);
        Assert.AreEqual(expected.CaseIndex, actual.CaseIndex, context);
        Assert.AreEqual(expected.WrapNullable, actual.WrapNullable, context);
        Assert.AreEqual(expected.Constant.HasValue, actual.Constant != null, context);
        Assert.AreEqual(expected.Constant?.Value, actual.Constant?.Value, context);
        Assert.AreEqual(expected.Constant?.Value?.GetType(), actual.Constant?.Value?.GetType(), context);
        Assert.AreEqual(expected.Method?.Name, actual.Method?.Name, context);
        if (expected.Method is OriginalMethod original && actual.Method is SelfMethod self)
        {
            Assert.AreEqual(original.Id, self.Id, context);
        }
    }

    private sealed record Pair(TypeSymbol Original, H.TypeSymbol Self);
    private static Pair Metadata(string name)
    {
        return new(TestCompiler.References.FindType(name)!,
        References.Value.FindType(name) ?? throw new InvalidOperationException(name));
    }

    private static Pair Nullable(Pair type)
    {
        return new(new NullableTypeSymbol(type.Original), new H.NullableTypeSymbol(type.Self));
    }

    private static Pair Array(Pair element, int rank = 1)
    {
        return new(
            new ArrayTypeSymbol(element.Original, (NamedTypeSymbol)Metadata("System.Array").Original, rank),
            new H.ArrayTypeSymbol(element.Self, (H.NamedTypeSymbol)Metadata("System.Array").Self, rank));
    }

    private static Pair Construct(Pair definition, params Pair[] arguments)
    {
        return new(
            ((NamedTypeSymbol)definition.Original).Construct(arguments.Select(a => a.Original).ToArray()),
            ((H.NamedTypeSymbol)definition.Self).Construct(arguments.Select(a => a.Self).ToArray()));
    }

    private static Pair Parameter(string name, bool reference = false, bool value = false, Pair[]? constraints = null)
    {
        return new(
            new TypeParameterSymbol(name, 0, ErrorTypeSymbol.Instance, VarianceKind.None,
                _ => new TypeParameterConstraints(reference, value, false, false, [.. (constraints ?? []).Select(c => c.Original)])),
            new H.TypeParameterSymbol(name, 0, H.ErrorTypeSymbol.Instance, H.VarianceKind.None,
                _ => new H.TypeParameterConstraints(reference, value, false, false, [.. (constraints ?? []).Select(c => c.Self)])));
    }

    private static Pair Named(string name, TypeKind kind, Pair? baseType = null, Pair[]? interfaces = null,
            SpecialType special = SpecialType.None, bool isSealed = false)
    {
        return new(
            new OriginalNamed(name, kind, special, baseType?.Original as NamedTypeSymbol,
                [.. (interfaces ?? []).Select(i => (NamedTypeSymbol)i.Original)], isSealed),
            new SelfNamed(name, (H.TypeKind)(int)kind, (H.SpecialType)(int)special, baseType?.Self as H.NamedTypeSymbol,
                [.. (interfaces ?? []).Select(i => (H.NamedTypeSymbol)i.Self)], isSealed));
    }

    private static Pair Union(string name, params Pair[] cases)
    {
        var pair = Named(name, TypeKind.Struct);
        ((OriginalNamed)pair.Original).Cases = [.. cases.Select(c => c.Original)];
        ((SelfNamed)pair.Self).Cases = [.. cases.Select(c => c.Self)];
        return pair;
    }
    private static Pair Tuple(string name, params Pair[] elements)
    {
        var pair = Named(name, TypeKind.Struct);
        ((OriginalNamed)pair.Original).Elements = [.. elements.Select(c => c.Original)];
        ((SelfNamed)pair.Self).Elements = [.. elements.Select(c => c.Self)];
        return pair;
    }
    private static (OriginalMethod Original, SelfMethod Self) AddOperator(Pair owner, string name, Pair parameter,
        Pair result, bool isStatic = true, MethodKind kind = MethodKind.Conversion, int parameterCount = 1)
    {
        var originalOwner = (OriginalNamed)owner.Original;
        var selfOwner = (SelfNamed)owner.Self;
        var id = owner.Original.Name + ":" + originalOwner.Members.Length;
        OriginalMethod original = new(id, name, parameter.Original, result.Original, isStatic, kind, parameterCount);
        SelfMethod self = new(id, name, parameter.Self, result.Self, isStatic, (H.MethodKind)(int)kind, parameterCount);
        originalOwner.Members = originalOwner.Members.Add(original);
        selfOwner.Members = selfOwner.Members.Add(self);
        return (original, self);
    }

    private sealed class OriginalNamed(string name, TypeKind kind, SpecialType special = SpecialType.None,
        NamedTypeSymbol? baseType = null, ImmutableArray<NamedTypeSymbol> interfaces = default, bool isSealed = false) : NamedTypeSymbol
    {
        public ImmutableArray<Symbol> Members { get; set; } = [];
        public ImmutableArray<TypeSymbol> Cases { get; set; }
        public ImmutableArray<TypeSymbol> Elements { get; set; }
        public bool ThrowMembers { get; set; }
        public override string Name => name;
        public override TypeKind TypeKind => kind;
        public override SpecialType SpecialType => special;
        public override NamedTypeSymbol? BaseType => baseType;
        public override bool IsSealed => isSealed;
        public override ImmutableArray<NamedTypeSymbol> Interfaces => interfaces.IsDefault ? [] : interfaces;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<TypeSymbol> TypeArguments => Elements.IsDefault ? [] : Elements;
        public override bool IsTuple => !Elements.IsDefault;
        public override bool IsUnionType => !Cases.IsDefault;
        public override ImmutableArray<TypeSymbol> UnionCases => Cases.IsDefault ? [] : Cases;
        public override ImmutableArray<Symbol> GetMembers()
        {
            return ThrowMembers ? throw new InvalidOperationException("members") : Members;
        }
    }

    private sealed class SelfNamed(string name, H.TypeKind kind, H.SpecialType special = H.SpecialType.None,
        H.NamedTypeSymbol? baseType = null, ImmutableArray<H.NamedTypeSymbol> interfaces = default, bool isSealed = false) : H.NamedTypeSymbol
    {
        public ImmutableArray<H.Symbol> Members { get; set; } = [];
        public ImmutableArray<H.TypeSymbol> Cases { get; set; }
        public ImmutableArray<H.TypeSymbol> Elements { get; set; }
        public bool ThrowMembers { get; set; }
        public override string Name => name;
        public override H.TypeKind TypeKind => kind;
        public override H.SpecialType SpecialType => special;
        public override H.NamedTypeSymbol? BaseType => baseType;
        public override bool IsSealed => isSealed;
        public override ImmutableArray<H.NamedTypeSymbol> Interfaces => interfaces.IsDefault ? [] : interfaces;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<H.TypeSymbol> TypeArguments => Elements.IsDefault ? [] : Elements;
        public override bool IsTuple => !Elements.IsDefault;
        public override bool IsUnionType => !Cases.IsDefault;
        public override ImmutableArray<H.TypeSymbol> UnionCases => Cases.IsDefault ? [] : Cases;
        public override ImmutableArray<H.Symbol> GetMembers()
        {
            return ThrowMembers ? throw new InvalidOperationException("members") : Members;
        }
    }

    private sealed class OriginalMethod(string id, string name, TypeSymbol parameter, TypeSymbol result,
        bool isStatic, MethodKind kind, int parameterCount) : MethodSymbol
    {
        public string Id => id;
        public override string Name => name;
        public override bool IsStatic => isStatic;
        public override MethodKind MethodKind => kind;
        public override TypeSymbol ReturnType => result;
        public override ImmutableArray<ParameterSymbol> Parameters =>
            [.. Enumerable.Range(0, parameterCount).Select(i => new ParameterSymbol("p" + i, parameter, i, this))];
    }

    private sealed class SelfMethod(string id, string name, H.TypeSymbol parameter, H.TypeSymbol result,
        bool isStatic, H.MethodKind kind, int parameterCount) : H.MethodSymbol
    {
        public string Id => id;
        public override string Name => name;
        public override bool IsStatic => isStatic;
        public override H.MethodKind MethodKind => kind;
        public override H.TypeSymbol ReturnType => result;
        public override ImmutableArray<H.ParameterSymbol> Parameters =>
            [.. Enumerable.Range(0, parameterCount).Select(i => new H.ParameterSymbol("p" + i, parameter, i, this))];
    }

    private sealed class SelfField(string name, H.TypeSymbol type, Func<H.ConstantValue?> value) : H.FieldSymbol
    {
        public override string Name => name;
        public override H.TypeSymbol Type => type;
        public override H.ConstantValue? ConstantValue => value();
    }
}
