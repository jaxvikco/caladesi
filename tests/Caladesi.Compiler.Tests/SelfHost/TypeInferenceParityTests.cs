extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Tests.Emit;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using Inference = SelfHost::Caladesi.Compiler.Binding.TypeInference;
using Classifier = SelfHost::Caladesi.Compiler.Binding.ConversionClassifier;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class TypeInferenceParityTests
{
    private static readonly Lazy<HM.ReferenceSet> References = new(() => HM.ReferenceSet.Create(TestReferences.Paths));
    private static readonly MethodInfo Infer = typeof(Binder).GetMethod("InferTypeArguments", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Lazy<Binder> Oracle = new(() =>
    {
        Compilation compilation = TestCompiler.Create("class Context {}", OutputKind.DynamicallyLinkedLibrary);
        return new Binder(compilation, compilation.GetFile(compilation.SyntaxTrees.Single()), null, null);
    });

    [TestMethod]
    public void LowerBounds_NumericNullableNullDedupOrderAndNoSyntheticCommonType()
    {
        Pair t = Parameter("T");
        Pair[] types = [Metadata("System.Byte"), Metadata("System.Int32"), Metadata("System.Int64"),
            Metadata("System.String"), Metadata("System.Object"), Nullable(Metadata("System.Int32")),
            new(NullTypeSymbol.Instance, H.NullTypeSymbol.Instance), new(ErrorTypeSymbol.Instance, H.ErrorTypeSymbol.Instance),
            new(NeverTypeSymbol.Instance, H.NeverTypeSymbol.Instance)];
        foreach (Pair a in types)
        foreach (Pair b in types)
        {
            Check([t], [t, t, t], [Value(a), Value(b), Value(a)], t);
            Check([t], [t, t], [Value(b), Value(a)], t);
        }
        Pair root = Named("Root"), aClass = Named("A", root), bClass = Named("B", root);
        Assert.IsTrue(Check([t], [t, t], [Value(aClass), Value(bClass)], t).IsDefault);
        Assert.AreEqual("Root", Check([t], [t, t, t], [Value(aClass), Value(bClass), Value(root)], t)[0].Name);
    }

    [TestMethod]
    public void DefaultFailureIsDistinctFromSuccessfulEmptyAndExpectedIsLastResort()
    {
        Pair t = Parameter("T"), u = Parameter("U"), text = Metadata("System.String"), number = Metadata("System.Int32");
        Assert.IsTrue(Check([], [], [], text).IsEmpty);
        Assert.IsFalse(Check([], [], [], text).IsDefault);
        Assert.IsTrue(Check([t], [], [], t).IsDefault);
        Assert.AreEqual("String", Check([t], [], [], t, expected: text)[0].Name);
        Assert.AreEqual("Int32", Check([t], [t], [Value(number)], t, expected: text)[0].Name);
        Check([t, u], [t], [Value(number)], Construct(Metadata("System.Func`2"), t, u),
            expected: Construct(Metadata("System.Func`2"), number, text));
        Check([t, u], [u], [Value(number)], t, expected: text);
        Check([t], [], [], Array(t), expected: Array(text));
        Assert.IsTrue(Check([t], [], [], text, expected: text).IsDefault);
    }

    [TestMethod]
    public void Params_NormalExpandedEmptyExtraAndIndexerSetterEffectiveParameters()
    {
        Pair t = Parameter("T"), number = Metadata("System.Int32"), wide = Metadata("System.Int64");
        foreach (bool isParams in new[] { false, true })
        foreach (int count in new[] { 0, 1, 2, 4 })
        {
            Check([t], [Array(t)], Enumerable.Repeat(Value(number), count).ToArray(), t, paramsLast: isParams);
            Check([t], [Array(t)], [Value(Array(number))], t, paramsLast: isParams);
            Check([t], [number, Array(t)], [Value(number), Value(wide), Value(number)], t, paramsLast: isParams);
        }
        Check([t], [Array(t), number], [Value(number), Value(wide)], t, paramsLast: false,
            indexer: true, indexerCount: 1, kind: MethodKind.PropertySet);
        Assert.AreEqual("Int64", Check([t], [Array(t), number], [Value(number), Value(wide)], t,
            indexer: true, indexerCount: 1, kind: MethodKind.PropertySet, paramsIndex: 0)[0].Name);
        Check([t], [Array(t), t], [Value(Array(number)), Value(wide)], t,
            indexer: true, indexerCount: 1, kind: MethodKind.PropertySet);
        Check([t], [t, t], [Value(number), Value(wide)], t,
            indexer: true, indexerCount: 1, kind: MethodKind.Ordinary);
        Check([t], [t], [Value(number)], t, indexer: true, indexerCount: 4, kind: MethodKind.PropertySet);
        Check([t], [t], [Value(number)], t, indexer: true, indexerCount: -1, kind: MethodKind.PropertySet);
        Check([t], [t], [Value(number)], t, indexer: true, indexerCount: 0, kind: MethodKind.PropertySet);
    }

    [TestMethod]
    public void ArraysNullableAndAllFiveArrayInterfaceFallbackNames()
    {
        Pair t = Parameter("T"), number = Metadata("System.Int32"), text = Metadata("System.String");
        foreach (Pair element in new[] { number, text, Nullable(number), Nullable(text) })
        foreach (int sourceRank in new[] { 1, 2 })
        foreach (int targetRank in new[] { 1, 2 })
        {
            Check([t], [Array(t, targetRank)], [Value(Array(element, sourceRank))], t);
            Check([t], [Nullable(Array(t, targetRank))], [Value(Nullable(Array(element, sourceRank)))], t);
        }
        foreach (string name in new[] { "IEnumerable", "IList", "ICollection", "IReadOnlyList", "IReadOnlyCollection" })
        foreach (Pair element in new[] { number, text })
        foreach (int rank in new[] { 1, 2 })
            Check([t], [Construct(Metadata("System.Collections.Generic." + name + "`1"), t)], [Value(Array(element, rank))], t);
        Check([t], [Nullable(t)], [Value(number)], t);
        Check([t], [Nullable(t)], [Value(Nullable(number))], t);
    }

    [TestMethod]
    public void MixedVarianceInvariantAndNestedUpperExactBounds()
    {
        Pair t = Parameter("T"), text = Metadata("System.String"), obj = Metadata("System.Object"), number = Metadata("System.Int32");
        string[] definitions = ["System.Func`1", "System.Action`1", "System.Collections.Generic.List`1"];
        foreach (string left in definitions)
        foreach (string right in definitions)
        foreach (Pair a in new[] { text, obj, number })
        foreach (Pair b in new[] { text, obj, number })
            Check([t], [Construct(Metadata(left), t), Construct(Metadata(right), t)],
                [Value(Construct(Metadata(left), a)), Value(Construct(Metadata(right), b))], t);
        Pair action = Metadata("System.Action`1"), list = Metadata("System.Collections.Generic.List`1");
        Check([t], [Construct(action, Array(t, 2))], [Value(Construct(action, Array(text, 1)))], t);
        Check([t], [Construct(action, Construct(list, t))], [Value(Construct(action, Construct(list, text)))], t);
        Check([t], [Construct(list, Nullable(t))], [Value(Construct(list, Nullable(number)))], t);
        Check([t], [Construct(list, Array(t, 2))], [Value(Construct(list, Array(number, 1)))], t);
        Check([t], [Construct(list, Construct(list, t))], [Value(Construct(list, Construct(list, number)))], t);
    }

    [TestMethod]
    public void ConstructedBaseInterfacesDuplicatesAmbiguityAndDefinitionIdentity()
    {
        Pair t = Parameter("T"), text = Metadata("System.String"), number = Metadata("System.Int32");
        Pair enumerable = Metadata("System.Collections.Generic.IEnumerable`1");
        Pair list = Metadata("System.Collections.Generic.List`1");
        Check([t], [Construct(enumerable, t)], [Value(Construct(list, text))], t);
        Check([t], [Construct(list, t)], [Value(Named("Derived", Construct(list, number)))], t);
        Pair stringEnumerable = Construct(enumerable, text), numberEnumerable = Construct(enumerable, number);
        foreach (Pair[] interfaces in new[] { new[] { stringEnumerable }, new[] { stringEnumerable, stringEnumerable },
                     new[] { stringEnumerable, numberEnumerable }, new[] { numberEnumerable, stringEnumerable } })
            Check([t], [Construct(enumerable, t)], [Value(Named("Implements", interfaces: interfaces))], t);
        Pair fake = Generic("SameName", VarianceKind.Out), other = Generic("SameName", VarianceKind.Out);
        Assert.IsTrue(Check([t], [Construct(fake, t)], [Value(Construct(other, text))], t).IsDefault);
        Check([t], [Construct(fake, t)], [Value(Construct(fake, text))], t);
        Pair foreign = Parameter("T");
        Assert.IsTrue(Check([t], [foreign], [Value(text)], t).IsDefault);
    }

    [TestMethod]
    public void Collections_InferElementsWithOrWithoutNaturalTypeAndIgnoreMissingElement()
    {
        Pair t = Parameter("T"), text = Metadata("System.String"), number = Metadata("System.Int32");
        foreach (Pair target in new[] { Array(t), Nullable(Array(t)),
                     Construct(Metadata("System.Collections.Generic.List`1"), t),
                     Construct(Metadata("System.Collections.Generic.IEnumerable`1"), t), t })
        foreach (Pair? natural in new Pair?[] { null, Array(number) })
        foreach (Pair? element in new Pair?[] { null, text, number })
            Check([t], [target], [Collection(element, natural)], t);
    }

    [TestMethod]
    public void Lambdas_ExplicitInputsReturnsNullOutputArityMismatchAndIterativeDependency()
    {
        Pair t = Parameter("T"), u = Parameter("U"), v = Parameter("V"), text = Metadata("System.String"), number = Metadata("System.Int32");
        Pair func = Metadata("System.Func`2");
        int originalCalls = 0, selfCalls = 0;
        Expr first = Lambda([null], inputs => { originalCalls++; Assert.AreEqual("Int32", inputs[0].Name); return text.Original; },
            inputs => { selfCalls++; Assert.AreEqual("Int32", inputs[0].Name); return text.Self; });
        Expr second = Lambda([null], inputs => { Assert.AreEqual("String", inputs[0].Name); return number.Original; },
            inputs => { Assert.AreEqual("String", inputs[0].Name); return number.Self; });
        Check([v, u, t], [Construct(func, u, v), Construct(func, t, u), t], [second, first, Value(number)], v);
        Assert.AreEqual(1, originalCalls);
        Assert.AreEqual(originalCalls, selfCalls);
        Assert.IsTrue(Check([u, t], [u, Construct(func, t, u), t],
            [Value(number), Lambda([null], _ => text.Original, _ => text.Self), Value(number)], u).IsDefault);
        Check([t, u], [Construct(func, t, u)], [Lambda([number], _ => text.Original, _ => text.Self)], u);
        Check([t, u], [Construct(func, t, u)], [Lambda([number], _ => null, _ => null, text)], u);
        Check([t], [Construct(Metadata("System.Func`1"), t)], [Lambda([], _ => text.Original, _ => text.Self)], t);
        Check([t], [Construct(Metadata("System.Func`1"), t)], [Lambda([number], _ => text.Original, _ => text.Self)], t);
        Check([t, u], [Construct(func, t, u)], [Lambda([null], _ => text.Original, _ => text.Self)], u);
        Check([t], [t], [Lambda([], _ => text.Original, _ => text.Self)], t);
        Check([t], [Construct(Metadata("System.Func`1"), t)], [Lambda([], _ => NullTypeSymbol.Instance, _ => H.NullTypeSymbol.Instance)], t);
    }

    [TestMethod]
    public void ExplicitLambdaExactBoundsMustAgreeWithLowerBounds()
    {
        Pair t = Parameter("T"), number = Metadata("System.Int32"), wide = Metadata("System.Int64");
        Pair action = Metadata("System.Action`1");
        foreach (Pair explicitInput in new[] { number, wide })
        foreach (Pair ordinary in new[] { number, wide })
            Check([t], [Construct(action, t), t], [Lambda([explicitInput], _ => null, _ => null), Value(ordinary)], t);
        Check([t], [Construct(action, Array(t, 2))], [Lambda([Array(number, 1)], _ => null, _ => null)], t);
    }

    [TestMethod]
    public void MethodGroupResolutionIsDelegatedOnlyOnceInputsAreFixed()
    {
        Pair t = Parameter("T"), u = Parameter("U"), number = Metadata("System.Int32"), text = Metadata("System.String");
        OMethod chosen = new([], [number.Original], text.Original);
        SMethod selfChosen = new([], [number.Self], text.Self);
        Expr group = new(new BoundMethodGroup(null, "M", [chosen], []), new HB.BoundMethodGroup(null, "M", [selfChosen], [], false));
        int calls = 0;
        Inference inference = new(new Classifier(), (mg, inputs) =>
        {
            calls++;
            Assert.AreSame(group.Self, mg);
            Assert.HasCount(1, inputs);
            Assert.AreEqual("Int32", inputs[0].Name);
            return selfChosen;
        });
        Check([u, t], [Construct(Metadata("System.Func`2"), t, u), t], [group, Value(number)], u, inference: inference);
        Assert.AreEqual(1, calls);
        calls = 0;
        Check([u, t], [Construct(Metadata("System.Func`2"), t, u)], [group], u, inference: inference);
        Assert.AreEqual(0, calls);
        Assert.IsTrue(new Inference(new Classifier()).InferTypeArguments(
            new SMethod([(H.TypeParameterSymbol)u.Self], [Construct(Metadata("System.Func`2"), number, u).Self], u.Self),
            [group.Self], false, 0, null).IsDefault);
        Check([t], [t], [group], t, inference: inference);
        Assert.AreEqual(0, calls);
        foreach (bool returnsVoid in new[] { false, true })
        {
            Pair result = returnsVoid ? Metadata("System.Void") : text;
            OMethod originalCandidate = new([], [number.Original], result.Original);
            SMethod selfCandidate = new([], [number.Self], result.Self);
            Expr candidateGroup = new(new BoundMethodGroup(null, "M", [originalCandidate], []),
                new HB.BoundMethodGroup(null, "M", [selfCandidate], [], false));
            int resolutions = 0;
            Inference resolver = new(new Classifier(), (_, inputs) =>
            {
                resolutions++;
                Assert.AreEqual("Int32", inputs[0].Name);
                return selfCandidate;
            });
            Check([u], [Construct(Metadata("System.Func`2"), number, u)], [candidateGroup], u, inference: resolver);
            Assert.AreEqual(1, resolutions);
        }
        Expr noCandidate = new(new BoundMethodGroup(null, "M", [], []), new HB.BoundMethodGroup(null, "M", [], [], false));
        Check([u], [Construct(Metadata("System.Func`2"), number, u)], [noCandidate], u,
            inference: new Inference(new Classifier(), (_, _) => null));
    }

    private static ImmutableArray<H.TypeSymbol> Check(Pair[] tps, Pair[] parameters, Expr[] args, Pair result,
        Pair? expected = null, bool paramsLast = false, bool indexer = false, int indexerCount = 0,
        MethodKind kind = MethodKind.Ordinary, Inference? inference = null, int paramsIndex = -1)
    {
        OMethod method = new(tps.Select(t => (TypeParameterSymbol)t.Original).ToImmutableArray(),
            parameters.Select(p => p.Original).ToImmutableArray(), result.Original, paramsLast, kind, paramsIndex);
        SMethod self = new(tps.Select(t => (H.TypeParameterSymbol)t.Self).ToImmutableArray(),
            parameters.Select(p => p.Self).ToImmutableArray(), result.Self, paramsLast, (H.MethodKind)(int)kind, paramsIndex);
        ImmutableArray<TypeSymbol> original = (ImmutableArray<TypeSymbol>)Infer.Invoke(Oracle.Value,
            [method, args.Select(a => a.Original).ToImmutableArray(), indexer, indexerCount, expected?.Original])!;
        ImmutableArray<H.TypeSymbol> actual = (inference ?? new Inference(new Classifier())).InferTypeArguments(
            self, args.Select(a => a.Self).ToImmutableArray(), indexer, indexerCount, expected?.Self);
        string context = string.Join(",", parameters.Select(p => p.Original.ToDisplayString())) + " <- " +
            string.Join(",", args.Select(a => a.Original.Type?.ToDisplayString() ?? a.Original.GetType().Name));
        Assert.AreEqual(original.IsDefault, actual.IsDefault, context);
        if (!original.IsDefault)
        {
            Assert.HasCount(original.Length, actual, context);
            for (int i = 0; i < original.Length; i++)
                Assert.AreEqual(original[i].ToDisplayString(), actual[i].ToDisplayString(), context);
        }
        return actual;
    }

    private sealed record Pair(TypeSymbol Original, H.TypeSymbol Self);
    private sealed record Expr(BoundExpression Original, HB.BoundExpression Self);
    private static Pair Metadata(string name) => new(TestCompiler.References.FindType(name)!, References.Value.FindType(name)!);
    private static Pair Parameter(string name, VarianceKind variance = VarianceKind.None) => new(
        new TypeParameterSymbol(name, 0, ErrorTypeSymbol.Instance, variance, _ => TypeParameterConstraints.None),
        new H.TypeParameterSymbol(name, 0, H.ErrorTypeSymbol.Instance, (H.VarianceKind)(int)variance, _ => H.TypeParameterConstraints.None));
    private static Pair Nullable(Pair type) => new(new NullableTypeSymbol(type.Original), new H.NullableTypeSymbol(type.Self));
    private static Pair Array(Pair type, int rank = 1) => new(
        new ArrayTypeSymbol(type.Original, (NamedTypeSymbol)Metadata("System.Array").Original, rank),
        new H.ArrayTypeSymbol(type.Self, (H.NamedTypeSymbol)Metadata("System.Array").Self, rank));
    private static Pair Construct(Pair definition, params Pair[] args) => new(
        ((NamedTypeSymbol)definition.Original).Construct(args.Select(a => a.Original).ToArray()),
        ((H.NamedTypeSymbol)definition.Self).Construct(args.Select(a => a.Self).ToArray()));
    private static Pair Named(string name, Pair? baseType = null, Pair[]? interfaces = null) => new(
        new ONamed(name, [], baseType?.Original as NamedTypeSymbol, (interfaces ?? []).Select(i => (NamedTypeSymbol)i.Original).ToImmutableArray()),
        new SNamed(name, [], baseType?.Self as H.NamedTypeSymbol, (interfaces ?? []).Select(i => (H.NamedTypeSymbol)i.Self).ToImmutableArray()));
    private static Pair Generic(string name, VarianceKind variance)
    {
        Pair p = Parameter("A", variance);
        return new(new ONamed(name, [(TypeParameterSymbol)p.Original]), new SNamed(name, [(H.TypeParameterSymbol)p.Self]));
    }
    private static Expr Value(Pair type) => new(new BoundDefaultValue(type.Original), new HB.BoundDefaultValue(type.Self));
    private static Expr Collection(Pair? element, Pair? natural) => new(
        new BoundUnbound(null!, _ => throw new InvalidOperationException(), natural?.Original)
        { IsCollection = true, Collection = new CollectionInfo { ElementCount = 1, NaturalElementType = element?.Original } },
        new HB.BoundUnbound(null!, _ => throw new InvalidOperationException(), natural?.Self)
        { IsCollection = true, Collection = new HB.CollectionInfo(1) { NaturalElementType = element?.Self } });
    private static Expr Lambda(Pair?[] explicitTypes, Func<ImmutableArray<TypeSymbol>, TypeSymbol?> output,
        Func<ImmutableArray<H.TypeSymbol>, H.TypeSymbol?> selfOutput, Pair? explicitReturn = null) => new(
        new BoundUnbound(null!, _ => throw new InvalidOperationException()) { IsLambda = true, Lambda = new LambdaInfo {
            ParameterCount = explicitTypes.Length, ExplicitParameterTypes = explicitTypes.Select(t => t?.Original).ToImmutableArray(),
            ExplicitReturnType = explicitReturn?.Original, Bind = (_, _, _) => throw new InvalidOperationException(), InferReturnType = output } },
        new HB.BoundUnbound(null!, _ => throw new InvalidOperationException()) { IsLambda = true, Lambda = new HB.LambdaInfo(
            explicitTypes.Length, explicitTypes.Select(t => t?.Self).ToImmutableArray(), (_, _, _) => throw new InvalidOperationException(), selfOutput)
            { ExplicitReturnType = explicitReturn?.Self } });
    private sealed class OMethod(ImmutableArray<TypeParameterSymbol> tps, ImmutableArray<TypeSymbol> ps, TypeSymbol result,
        bool paramsLast = false, MethodKind kind = MethodKind.Ordinary, int paramsIndex = -1) : MethodSymbol
    {
        public override string Name => "M";
        public override MethodKind MethodKind => kind;
        public override TypeSymbol ReturnType => result;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => tps;
        public override ImmutableArray<ParameterSymbol> Parameters => ps.Select((p, i) => new ParameterSymbol("p" + i, p, i, this, isParams: (paramsLast && i == ps.Length - 1) || i == paramsIndex)).ToImmutableArray();
    }
    private sealed class SMethod(ImmutableArray<H.TypeParameterSymbol> tps, ImmutableArray<H.TypeSymbol> ps, H.TypeSymbol result,
        bool paramsLast = false, H.MethodKind kind = H.MethodKind.Ordinary, int paramsIndex = -1) : H.MethodSymbol
    {
        public override string Name => "M";
        public override H.MethodKind MethodKind => kind;
        public override H.TypeSymbol ReturnType => result;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => tps;
        public override ImmutableArray<H.ParameterSymbol> Parameters => ps.Select((p, i) => new H.ParameterSymbol("p" + i, p, i, this,
            H.RefKind.None, (paramsLast && i == ps.Length - 1) || i == paramsIndex)).ToImmutableArray();
    }
    private sealed class ONamed(string name, ImmutableArray<TypeParameterSymbol> tps, NamedTypeSymbol? baseType = null,
        ImmutableArray<NamedTypeSymbol> interfaces = default) : NamedTypeSymbol
    {
        public override string Name => name;
        public override TypeKind TypeKind => TypeKind.Class;
        public override NamedTypeSymbol? BaseType => baseType;
        public override ImmutableArray<NamedTypeSymbol> Interfaces => interfaces.IsDefault ? [] : interfaces;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => tps;
        public override ImmutableArray<TypeSymbol> TypeArguments => tps.Cast<TypeSymbol>().ToImmutableArray();
        public override ImmutableArray<Symbol> GetMembers() => [];
    }
    private sealed class SNamed(string name, ImmutableArray<H.TypeParameterSymbol> tps, H.NamedTypeSymbol? baseType = null,
        ImmutableArray<H.NamedTypeSymbol> interfaces = default) : H.NamedTypeSymbol
    {
        public override string Name => name;
        public override H.TypeKind TypeKind => H.TypeKind.Class;
        public override H.NamedTypeSymbol? BaseType => baseType;
        public override ImmutableArray<H.NamedTypeSymbol> Interfaces => interfaces.IsDefault ? [] : interfaces;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => tps;
        public override ImmutableArray<H.TypeSymbol> TypeArguments => tps.Cast<H.TypeSymbol>().ToImmutableArray();
        public override ImmutableArray<H.Symbol> GetMembers() => [];
    }
}
