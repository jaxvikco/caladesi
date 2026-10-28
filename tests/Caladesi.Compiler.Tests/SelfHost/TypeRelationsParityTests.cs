extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Symbols;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using Relations = SelfHost::Caladesi.Compiler.Binding.TypeRelations;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class TypeRelationsParityTests
{
    private static readonly Lazy<HM.ReferenceSet> SelfReferences = new(() => HM.ReferenceSet.Create(TestReferences.Paths));
    private static readonly Lazy<Binder> Oracle = new(() =>
    {
        Compilation compilation = TestCompiler.Create("class Context {}", OutputKind.DynamicallyLinkedLibrary);
        return new Binder(compilation, compilation.GetFile(compilation.SyntaxTrees.Single()), null, null);
    });

    [TestMethod]
    public void NumericTable_AllSpecialTypesAndUndefinedValuesMatchBinder()
    {
        SpecialType[] types = [.. Enum.GetValues<SpecialType>(), (SpecialType)(-1), (SpecialType)int.MinValue, (SpecialType)int.MaxValue];
        foreach (SpecialType from in types)
        {
            Pair source = Named(from.ToString(), TypeKind.Struct, special: from);
            Assert.AreEqual(Original("IsNumericType", source.Original), Relations.IsNumericType(source.Self), from.ToString());
            foreach (SpecialType to in types)
                Assert.AreEqual(Original("HasImplicitNumeric", from, to),
                    Relations.HasImplicitNumeric((H.SpecialType)(int)from, (H.SpecialType)(int)to), $"{from} -> {to}");
        }
        Assert.IsFalse(Relations.HasImplicitNumeric(H.SpecialType.Int32, H.SpecialType.Int32));
        Assert.IsFalse(Relations.HasImplicitNumeric(H.SpecialType.UInt32, H.SpecialType.IntPtr));
        Assert.IsTrue(Relations.HasImplicitNumeric(H.SpecialType.Char, H.SpecialType.UIntPtr));
    }

    [TestMethod]
    public void RealMetadata_ReferenceBoxingInheritanceAndVariantInterfacesMatchBinder()
    {
        List<Pair> types =
        [
            Metadata("System.Object"), Metadata("System.String"), Metadata("System.Int32"), Metadata("System.Int64"),
            Metadata("System.Boolean"), Metadata("System.Decimal"), Metadata("System.Array"), Metadata("System.Enum"),
            Metadata("System.ValueType"), Metadata("System.Delegate"), Metadata("System.MulticastDelegate"),
            Metadata("System.DayOfWeek"), Metadata("System.Exception"), Metadata("System.ArgumentException"),
            Metadata("System.IComparable"), Metadata("System.IDisposable"), Metadata("System.Collections.IEnumerable"),
            Metadata("System.Collections.ICollection"), Metadata("System.Collections.IList")
        ];
        Pair obj = types[0], text = types[1], number = types[2];
        foreach (string name in new[]
        {
            "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.IEnumerator`1",
            "System.Collections.Generic.IList`1", "System.Collections.Generic.ICollection`1",
            "System.Collections.Generic.IReadOnlyList`1", "System.Collections.Generic.IReadOnlyCollection`1",
            "System.Collections.Generic.List`1", "System.IComparable`1", "System.Action`1", "System.Func`1"
        })
        {
            Pair definition = Metadata(name);
            foreach (Pair argument in new[] { obj, text, number })
                types.Add(Construct(definition, argument));
        }
        Pair function = Metadata("System.Func`2");
        types.Add(Construct(function, obj, text));
        types.Add(Construct(function, text, obj));
        foreach (Pair element in new[] { obj, text, number, Array(text) })
        foreach (int rank in new[] { 1, 2, 3 })
            types.Add(Array(element, rank));
        types.AddRange(new[] { Nullable(obj), Nullable(text), Nullable(number), Nullable(Nullable(text)) });
        types.Add(new Pair(NullTypeSymbol.Instance, H.NullTypeSymbol.Instance));
        types.Add(new Pair(NeverTypeSymbol.Instance, H.NeverTypeSymbol.Instance));
        types.Add(new Pair(ErrorTypeSymbol.Instance, H.ErrorTypeSymbol.Instance));
        AssertMatrix(types);
    }

    [TestMethod]
    public void SyntheticGraphs_AllVariancePositionsNestedArgumentsAndDefinitionIdentityMatchBinder()
    {
        Pair obj = Metadata("System.Object"), text = Metadata("System.String"), number = Metadata("System.Int32");
        Pair root = Named("Root", TypeKind.Class, obj);
        Pair derived = Named("Derived", TypeKind.Class, root);
        Pair marker = Named("IMarker", TypeKind.Interface);
        Pair parent = Named("IParent", TypeKind.Interface, interfaces: [marker]);
        Pair implementation = Named("Implementation", TypeKind.Class, derived, interfaces: [parent]);
        Pair structure = Named("Structure", TypeKind.Struct, interfaces: [parent]);
        Pair enumeration = Named("Enumeration", TypeKind.Enum, interfaces: [marker]);
        List<Pair> types = [obj, text, number, root, derived, marker, parent, implementation, structure, enumeration];
        foreach (TypeKind kind in new[] { TypeKind.Interface, TypeKind.Delegate, TypeKind.Class })
        foreach (VarianceKind variance in Enum.GetValues<VarianceKind>())
        {
            Pair definition = Generic("G" + kind + variance, kind, variance);
            Pair sameNameDifferentDefinition = Generic("G" + kind + variance, kind, variance);
            Pair narrow = Construct(definition, derived), wide = Construct(definition, root);
            types.AddRange([definition, narrow, wide, Construct(definition, number), Construct(definition, obj),
                Construct(definition, Nullable(derived)), Construct(definition, Nullable(root)),
                Construct(sameNameDifferentDefinition, derived)]);
            if (kind == TypeKind.Interface)
            {
                types.Add(Named("Class" + variance, TypeKind.Class, interfaces: [narrow]));
                types.Add(Named("Struct" + variance, TypeKind.Struct, interfaces: [narrow]));
                types.Add(Named("Child" + variance, TypeKind.Interface, interfaces: [narrow]));
            }
        }
        Pair mixed = Generic("Mixed", TypeKind.Interface, VarianceKind.In, VarianceKind.Out, VarianceKind.None);
        types.AddRange([
            Construct(mixed, root, derived, text), Construct(mixed, derived, root, text),
            Construct(mixed, derived, root, obj), Construct(mixed, root, root, text),
            Construct(mixed, number, derived, text)
        ]);
        Pair producer = Generic("Producer", TypeKind.Interface, VarianceKind.Out);
        types.Add(Construct(producer, Construct(producer, derived)));
        types.Add(Construct(producer, Construct(producer, root)));
        types.Add(Metadata("System.Delegate"));
        types.Add(Metadata("System.MulticastDelegate"));
        AssertMatrix(types);
    }

    [TestMethod]
    public void TypeParameters_ConstraintFlagsChainsAndVariantConstraintsMatchBinder()
    {
        Pair obj = Metadata("System.Object"), text = Metadata("System.String"), number = Metadata("System.Int32");
        Pair iface = Generic("IProducer", TypeKind.Interface, VarianceKind.Out);
        Pair narrow = Construct(iface, text), wide = Construct(iface, obj);
        Pair implementation = Named("Implementation", TypeKind.Class, obj, interfaces: [narrow]);
        Pair structure = Named("Structure", TypeKind.Struct, interfaces: [narrow]);
        List<Pair> types = [obj, text, number, narrow, wide, implementation, structure,
            Metadata("System.ValueType"), Metadata("System.Enum"), Nullable(obj), Nullable(number)];
        foreach (bool hasClass in new[] { false, true })
        foreach (bool hasStruct in new[] { false, true })
        foreach (Pair[] constraints in new Pair[][] { [], [narrow], [implementation], [number], [narrow, implementation] })
            types.Add(Parameter("T" + types.Count, hasClass, hasStruct, constraints));
        Pair unconstrained = types[11];
        types.Add(Parameter("Chained", false, false, [unconstrained]));
        Pair constrained = Parameter("Constrained", false, false, [implementation]);
        types.Add(constrained);
        types.Add(Parameter("Transitive", false, false, [constrained]));
        types.Add(Parameter("Annotated", false, false, [Nullable(implementation)]));
        types.Add(Construct(iface, constrained));
        types.Add(Construct(iface, unconstrained));
        AssertMatrix(types);
        Assert.IsTrue(Relations.ImplicitReferenceConversionExists(unconstrained.Self, obj.Self));
        Assert.IsTrue(Relations.BoxingConversionExists(unconstrained.Self, obj.Self));
        Assert.IsTrue(Relations.BoxingConversionExists(unconstrained.Self, Metadata("System.ValueType").Self));
        Assert.IsFalse(Relations.BoxingConversionExists(unconstrained.Self, Nullable(obj).Self));
    }

    [TestMethod]
    public void Arrays_AllFiveGenericTargetsRanksAndElementEqualityMatchBinder()
    {
        Pair obj = Metadata("System.Object"), text = Metadata("System.String"), number = Metadata("System.Int32");
        List<Pair> types = [obj, text, number, Metadata("System.Array"), Metadata("System.Collections.IEnumerable")];
        foreach (Pair element in new[] { text, obj, number, Nullable(text), Array(text), Nullable(number) })
        foreach (int rank in new[] { -1, 0, 1, 2, 3 })
            types.Add(Array(element, rank));
        foreach (string name in new[]
        {
            "System.Collections.Generic.IList`1", "System.Collections.Generic.ICollection`1",
            "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.IReadOnlyList`1",
            "System.Collections.Generic.IReadOnlyCollection`1", "System.Collections.Generic.IEnumerator`1"
        })
        {
            foreach (Pair argument in new[] { obj, text, number, Nullable(text) })
                types.Add(Construct(Metadata(name), argument));
            string simpleName = name[..name.IndexOf('`')];
            Pair wrongArity = Construct(Generic(simpleName, TypeKind.Interface, VarianceKind.Out, VarianceKind.Out), obj, obj);
            Pair lookalike = Construct(Generic(simpleName.Replace("System.", "Lookalike.", StringComparison.Ordinal),
                TypeKind.Interface, VarianceKind.Out), obj);
            Pair nongeneric = Named(simpleName, TypeKind.Interface);
            types.AddRange([wrongArity, lookalike, nongeneric]);
            foreach (Pair rejected in new[] { wrongArity, lookalike, nongeneric })
            {
                Assert.IsFalse(Original("ImplicitReferenceConversionExists", Array(text).Original, rejected.Original));
                Assert.IsFalse(Relations.ImplicitReferenceConversionExists(Array(text).Self, rejected.Self));
            }
        }
        AssertMatrix(types);
        Assert.IsTrue(Relations.ImplicitReferenceConversionExists(Array(number).Self,
            Construct(Metadata("System.Collections.Generic.IList`1"), number).Self));
        Assert.IsFalse(Relations.ImplicitReferenceConversionExists(Array(text, 2).Self,
            Construct(Metadata("System.Collections.Generic.IEnumerable`1"), obj).Self));
        Assert.IsFalse(Relations.ImplicitReferenceConversionExists(Array(text).Self,
            Metadata("System.Collections.IEnumerable").Self));
    }

    [TestMethod]
    public void OrderingQuirks_IdentityNullablePeelingSelfInterfacesAndDelegatesArePreserved()
    {
        Pair obj = Metadata("System.Object"), number = Metadata("System.Int32"), text = Metadata("System.String");
        Assert.IsTrue(Relations.ImplicitReferenceConversionExists(number.Self, number.Self));
        Assert.IsTrue(Relations.ImplicitReferenceConversionExists(Nullable(number).Self, number.Self));
        Assert.IsTrue(Relations.ImplicitReferenceConversionExists(number.Self, Nullable(number).Self));
        Assert.IsFalse(Relations.ImplicitReferenceConversionExists(number.Self, obj.Self));
        Assert.IsFalse(Relations.BoxingConversionExists(Nullable(number).Self, obj.Self));
        Assert.IsFalse(Relations.IsDerivedFrom(obj.Self, obj.Self));
        Pair marker = Named("IMarker", TypeKind.Interface);
        Assert.IsFalse(Relations.ImplementsInterface(marker.Self, (H.NamedTypeSymbol)marker.Self));
        Assert.IsFalse(Relations.ImplementsInterfaceVariant(marker.Self, (H.NamedTypeSymbol)marker.Self));
        Assert.IsTrue(Relations.ImplicitReferenceConversionExists(marker.Self, marker.Self));
        Pair producer = Generic("Producer", TypeKind.Interface, VarianceKind.Out);
        Pair narrow = Construct(producer, text), wide = Construct(producer, obj);
        Assert.IsFalse(Relations.ImplementsInterface(narrow.Self, (H.NamedTypeSymbol)wide.Self));
        Assert.IsTrue(Relations.ImplementsInterfaceVariant(narrow.Self, (H.NamedTypeSymbol)wide.Self));
        Pair delegateType = Named("BareDelegate", TypeKind.Delegate);
        Assert.IsTrue(Relations.ImplicitReferenceConversionExists(delegateType.Self, Metadata("System.Delegate").Self));
        Assert.IsTrue(Relations.ImplicitReferenceConversionExists(delegateType.Self, Metadata("System.MulticastDelegate").Self));
        Assert.IsFalse(Relations.IsDelegateVarianceConvertible(delegateType.Self, (H.NamedTypeSymbol)delegateType.Self));
        AssertMatrix([obj, number, text, marker, narrow, wide, delegateType, Nullable(number),
            Nullable(Nullable(number)), Nullable(text), Nullable(Nullable(text))]);
    }

    private static bool Original(string name, params object[] arguments)
    {
        MethodInfo method = typeof(Binder).GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(method.IsStatic ? null : Oracle.Value, arguments)!;
    }

    private static void AssertMatrix(IEnumerable<Pair> pairs)
    {
        Pair[] types = pairs.ToArray();
        foreach (Pair source in types)
        foreach (Pair target in types)
        {
            string context = $"{Describe(source.Original)} -> {Describe(target.Original)}";
            Assert.AreEqual(Original("IsDerivedFrom", source.Original, target.Original),
                Relations.IsDerivedFrom(source.Self, target.Self), "derivation: " + context);
            Assert.AreEqual(Original("ImplicitReferenceConversionExists", source.Original, target.Original),
                Relations.ImplicitReferenceConversionExists(source.Self, target.Self), "reference: " + context);
            Assert.AreEqual(Original("BoxingConversionExists", source.Original, target.Original),
                Relations.BoxingConversionExists(source.Self, target.Self), "boxing: " + context);
            if (target.Original is NamedTypeSymbol originalTarget && target.Self is H.NamedTypeSymbol selfTarget)
            {
                Assert.AreEqual(Original("ImplementsInterface", source.Original, originalTarget),
                    Relations.ImplementsInterface(source.Self, selfTarget), "interface: " + context);
                Assert.AreEqual(Original("ImplementsInterfaceVariant", source.Original, originalTarget),
                    Relations.ImplementsInterfaceVariant(source.Self, selfTarget), "variant interface: " + context);
                Assert.AreEqual(Original("IsDelegateVarianceConvertible", source.Original, originalTarget),
                    Relations.IsDelegateVarianceConvertible(source.Self, selfTarget), "delegate: " + context);
                if (source.Original is NamedTypeSymbol originalSource && source.Self is H.NamedTypeSymbol selfSource)
                    Assert.AreEqual(Original("IsVarianceConvertible", originalSource, originalTarget),
                        Relations.IsVarianceConvertible(selfSource, selfTarget), "variance: " + context);
            }
        }
    }

    private static string Describe(TypeSymbol type) => type switch
    {
        ArrayTypeSymbol array => $"{Describe(array.ElementType)}[rank {array.Rank}]",
        NullableTypeSymbol nullable => Describe(nullable.Underlying) + "?",
        NamedTypeSymbol { IsGenericType: true } named => $"{named.Name}<{string.Join(",", named.TypeArguments.Select(Describe))}>",
        _ => type.Name,
    };

    private sealed record Pair(TypeSymbol Original, H.TypeSymbol Self);

    private static Pair Metadata(string name) =>
        new(TestCompiler.References.FindType(name) ?? throw new InvalidOperationException(name),
            SelfReferences.Value.FindType(name) ?? throw new InvalidOperationException(name));

    private static Pair Nullable(Pair type) => new(new NullableTypeSymbol(type.Original), new H.NullableTypeSymbol(type.Self));

    private static Pair Array(Pair element, int rank = 1) =>
        new(new ArrayTypeSymbol(element.Original, (NamedTypeSymbol)Metadata("System.Array").Original, rank),
            new H.ArrayTypeSymbol(element.Self, (H.NamedTypeSymbol)Metadata("System.Array").Self, rank));

    private static Pair Construct(Pair definition, params Pair[] arguments) =>
        new(((NamedTypeSymbol)definition.Original).Construct(arguments.Select(a => a.Original).ToArray()),
            ((H.NamedTypeSymbol)definition.Self).Construct(arguments.Select(a => a.Self).ToArray()));

    private static Pair Named(string name, TypeKind kind, Pair? baseType = null, Pair[]? interfaces = null, SpecialType special = SpecialType.None) =>
        new(new OriginalNamed(name, kind, special, baseType?.Original as NamedTypeSymbol,
                (interfaces ?? []).Select(i => (NamedTypeSymbol)i.Original).ToImmutableArray(), []),
            new SelfNamed(name, (H.TypeKind)(int)kind, (H.SpecialType)(int)special, baseType?.Self as H.NamedTypeSymbol,
                (interfaces ?? []).Select(i => (H.NamedTypeSymbol)i.Self).ToImmutableArray(), []));

    private static Pair Generic(string name, TypeKind kind, params VarianceKind[] variance) =>
        new(new OriginalNamed(name, kind, SpecialType.None, null, [], variance),
            new SelfNamed(name, (H.TypeKind)(int)kind, H.SpecialType.None, null, [], variance));

    private static Pair Parameter(string name, bool hasClass, bool hasStruct, Pair[] constraints)
    {
        return new(new TypeParameterSymbol(name, 0, ErrorTypeSymbol.Instance, VarianceKind.None,
                _ => new TypeParameterConstraints(hasClass, hasStruct, false, false, constraints.Select(c => c.Original).ToImmutableArray())),
            new H.TypeParameterSymbol(name, 0, H.ErrorTypeSymbol.Instance, H.VarianceKind.None,
                _ => new H.TypeParameterConstraints(hasClass, hasStruct, false, false, constraints.Select(c => c.Self).ToImmutableArray())));
    }

    private sealed class OriginalNamed : NamedTypeSymbol
    {
        private readonly string name;
        private readonly TypeKind kind;
        private readonly SpecialType special;
        private readonly NamedTypeSymbol? baseType;
        private readonly ImmutableArray<NamedTypeSymbol> interfaces;
        private readonly ImmutableArray<TypeParameterSymbol> parameters;

        public OriginalNamed(string name, TypeKind kind, SpecialType special, NamedTypeSymbol? baseType,
            ImmutableArray<NamedTypeSymbol> interfaces, VarianceKind[] variance)
        {
            this.name = name;
            this.kind = kind;
            this.special = special;
            this.baseType = baseType;
            this.interfaces = interfaces;
            parameters = variance.Select((v, i) => new TypeParameterSymbol("T" + i, i, this, v)).ToImmutableArray();
        }

        public override string Name => name;
        public override TypeKind TypeKind => kind;
        public override SpecialType SpecialType => special;
        public override NamedTypeSymbol? BaseType => baseType;
        public override ImmutableArray<NamedTypeSymbol> Interfaces => interfaces;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => parameters;
        public override ImmutableArray<TypeSymbol> TypeArguments => [.. parameters];
    }

    private sealed class SelfNamed : H.NamedTypeSymbol
    {
        private readonly string name;
        private readonly H.TypeKind kind;
        private readonly H.SpecialType special;
        private readonly H.NamedTypeSymbol? baseType;
        private readonly ImmutableArray<H.NamedTypeSymbol> interfaces;
        private readonly ImmutableArray<H.TypeParameterSymbol> parameters;

        public SelfNamed(string name, H.TypeKind kind, H.SpecialType special, H.NamedTypeSymbol? baseType,
            ImmutableArray<H.NamedTypeSymbol> interfaces, VarianceKind[] variance)
        {
            this.name = name;
            this.kind = kind;
            this.special = special;
            this.baseType = baseType;
            this.interfaces = interfaces;
            parameters = variance.Select((v, i) => new H.TypeParameterSymbol("T" + i, i, this, (H.VarianceKind)(int)v)).ToImmutableArray();
        }

        public override string Name => name;
        public override H.TypeKind TypeKind => kind;
        public override H.SpecialType SpecialType => special;
        public override H.NamedTypeSymbol? BaseType => baseType;
        public override ImmutableArray<H.NamedTypeSymbol> Interfaces => interfaces;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => parameters;
        public override ImmutableArray<H.TypeSymbol> TypeArguments => [.. parameters];
    }
}
