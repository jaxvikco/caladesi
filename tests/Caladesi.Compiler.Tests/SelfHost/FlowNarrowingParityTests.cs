extern alias SelfHost;

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Tests.Emit;
using H = SelfHost::Caladesi.Compiler.Binding;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using HX = SelfHost::Caladesi.Compiler.Syntax;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class FlowNarrowingParityTests
{
    [TestMethod]
    public void Paths_AllExpressionAndConversionKindsMatchOriginal()
    {
        Fixture f = new();
        foreach (Pair expression in f.Expressions())
        {
            f.Path(expression);
            foreach (ConversionKind kind in Enum.GetValues<ConversionKind>())
                f.Path(f.Convert(expression, kind, f.Object));
        }
        Pair generic = f.Type("System.Collections.Generic.List`1");
        NamedTypeSymbol original = ((NamedTypeSymbol)generic.Original).Construct(ImmutableArray.Create((TypeSymbol)f.String.Original));
        HS.NamedTypeSymbol actual = ((HS.NamedTypeSymbol)generic.Actual).Construct(ImmutableArray.Create((HS.TypeSymbol)f.String.Actual));
        Pair definition = new(((NamedTypeSymbol)generic.Original).GetMembers("Count").Single(),
            ((HS.NamedTypeSymbol)generic.Actual).GetMembers("Count").Single());
        Pair substituted = new(original.GetMembers("Count").Single(), actual.GetMembers("Count").Single());
        object left = f.NewPath((Symbol)definition.Original);
        object right = f.NewPath((Symbol)substituted.Original);
        H.NarrowPath selfLeft = new([(HS.Symbol)definition.Actual]);
        H.NarrowPath selfRight = new([(HS.Symbol)substituted.Actual]);
        Assert.AreEqual(left.Equals(right), selfLeft.Equals(selfRight));
        Assert.IsFalse(left.Equals(right)); // Typed definitions hide the Symbol slot in the oracle.
        Assert.AreEqual(unchecked(17 * 31 + RuntimeHelpers.GetHashCode(((HS.Symbol)definition.Actual).OriginalSymbolDefinition)),
            selfLeft.GetHashCode());
        Assert.AreEqual(unchecked(17 * 31 + RuntimeHelpers.GetHashCode(substituted.Actual)), selfRight.GetHashCode());
        Symbol originalRoot = new IdentitySymbol(null);
        HS.Symbol selfRoot = new SelfIdentitySymbol(null);
        object identityLeft = f.NewPath(new IdentitySymbol(originalRoot));
        object identityRight = f.NewPath(new IdentitySymbol(originalRoot));
        H.NarrowPath selfIdentityLeft = new([new SelfIdentitySymbol(selfRoot)]);
        H.NarrowPath selfIdentityRight = new([new SelfIdentitySymbol(selfRoot)]);
        Assert.AreEqual(identityLeft.Equals(identityRight), selfIdentityLeft.Equals(selfIdentityRight));
        Assert.IsTrue(selfIdentityLeft.Equals(selfIdentityRight));
        Assert.AreEqual(identityLeft.GetHashCode(), identityRight.GetHashCode());
        Assert.AreEqual(selfIdentityLeft.GetHashCode(), selfIdentityRight.GetHashCode());
        Assert.IsFalse(selfLeft.Equals(new object()));
        Assert.IsTrue(selfLeft.StartsWith(new([])));
        Assert.IsFalse(selfLeft.StartsWith(selfLeft.Append((HS.Symbol)definition.Actual)));
        Assert.IsFalse(selfLeft.Equals(selfLeft.Append((HS.Symbol)definition.Actual)));
        Pair[] values = [.. f.Expressions()];
        foreach (Pair value in values)
        foreach (Pair prefix in values)
        {
            object? originalValue = Fixture.Invoke(null, "PathOf", value.Original);
            object? originalPrefix = Fixture.Invoke(null, "PathOf", prefix.Original);
            H.NarrowPath? selfValue = H.FlowNarrowing.PathOf((HB.BoundExpression)value.Actual);
            H.NarrowPath? selfPrefix = H.FlowNarrowing.PathOf((HB.BoundExpression)prefix.Actual);
            if (originalValue != null && originalPrefix != null)
            {
                Assert.AreEqual(originalValue.Equals(originalPrefix), selfValue!.Equals(selfPrefix));
                Assert.AreEqual(originalValue.GetType().GetMethod("StartsWith")!.Invoke(originalValue, [originalPrefix]),
                    selfValue.StartsWith(selfPrefix!));
            }
        }
    }

    [TestMethod]
    public void BranchFacts_AllOperatorsWrappersNullSidesAndUnionQuirksMatchOriginal()
    {
        Fixture f = new();
        foreach (Pair expression in f.Expressions())
        {
            f.Facts(expression);
            Pair test = f.Test(expression, f.String);
            f.Facts(test);
            f.Facts(f.Unary(UnaryOperatorKind.Not, test));
            f.Facts(f.Unary(UnaryOperatorKind.Plus, test));
            foreach (ConversionKind kind in Enum.GetValues<ConversionKind>())
                f.Facts(f.Convert(test, kind, f.Bool));
            foreach (BinaryOperatorKind kind in Enum.GetValues<BinaryOperatorKind>())
            {
                Pair binary = f.Binary(kind, expression, f.Null);
                f.Facts(binary);
                f.Facts(f.Binary(kind, f.Null, expression));
                f.Facts(f.Logical(true, binary, test));
                f.Facts(f.Logical(false, f.Unary(UnaryOperatorKind.Not, binary), f.Unary(UnaryOperatorKind.Not, test)));
            }
        }
        foreach (int count in new[] { 0, 1, 2, 3 })
        {
            Pair union = new(new UnionType([.. new[] { f.String, f.Int, f.Object }.Take(count).Select(p => (TypeSymbol)p.Original)]),
                new SelfUnionType([.. new[] { f.String, f.Int, f.Object }.Take(count).Select(p => (HS.TypeSymbol)p.Actual)]));
            foreach (bool nullable in new[] { false, true })
            foreach (Pair target in new[] { f.String, f.Int, f.Object, f.Bool })
            {
                Pair operand = f.Local("union", nullable ? f.Nullable(union) : union);
                foreach (bool wasNullable in new[] { false, true })
                {
                    Pair test = f.Test(operand, target, wasNullable);
                    f.Facts(test);
                }
                f.Wrap(operand, target);
            }
        }
    }

    [TestMethod]
    public void WrappingAndImmutableState_PreserveSyntaxCaseIndexAndLastFactWins()
    {
        Fixture f = new();
        Pair[] declared = [f.Object, f.String, f.Int, f.Nullable(f.Object), f.Nullable(f.Int),
            f.Nullable(f.Nullable(f.Object)), f.Nullable(f.Nullable(f.Int))];
        foreach (Pair type in declared)
        foreach (Pair narrowed in new[] { f.String, f.Int, f.Object, f.Nullable(f.Int) })
            f.Wrap(f.Local("value", type), narrowed);
        Pair local = f.Local("value", f.Nullable(f.Object));
        f.Seed(local, f.Object);
        object originalSnapshot = f.OriginalState;
        var snapshot = f.Actual.SaveState();
        f.Seed(local, f.String);
        f.Apply(local);
        Assert.AreNotSame(snapshot, f.Actual.State);
        Assert.HasCount(1, snapshot);
        f.Restore(originalSnapshot, snapshot);
        f.Apply(local);
        f.Actual.Apply(H.Narrowings.Empty);
        Assert.AreSame(snapshot, f.Actual.State);
        Assert.AreSame(snapshot, H.FlowNarrowing.ApplyNarrowings(snapshot, new(default)));
        Assert.AreSame(H.Narrowings.Empty, H.Narrowings.Empty.Concat(H.Narrowings.Empty));
        Assert.AreSame(local.Actual, new H.FlowNarrowing().ApplyNarrowing((HB.BoundExpression)local.Actual));
    }

    [TestMethod]
    public void Invalidation_AssignmentNamesLambdasCallsAndMutableStateMatchOriginal()
    {
        foreach (string action in new[] { "InvalidateNarrowing", "InvalidateAssignedIn", "NoteLambdaAssignments",
                     "DropMutableNarrowings", "source", "external", "delegate", "null" })
        {
            Fixture f = new();
            Pair local = f.Local("value", f.Nullable(f.Object));
            Pair constant = f.Local("constant", f.Nullable(f.Object), true);
            Pair sameName = f.Local("value", f.Nullable(f.Object));
            Pair parameter = f.Parameter("parameter", f.Nullable(f.Object));
            Pair field = f.Field(local, "member");
            Pair staticField = f.Field(null, "member");
            Pair property = f.Property(parameter);
            Pair[] expressions = [local, constant, sameName, parameter, field, staticField, property, f.This];
            foreach (Pair expression in expressions) f.Seed(expression, f.String);
            var snapshot = f.Actual.SaveState();
            if (action == "InvalidateNarrowing") f.Invalidate(action, local);
            else if (action is "InvalidateAssignedIn" or "NoteLambdaAssignments")
                f.SyntaxAction(action, "function f() { ((value)) += 1; ++parameter; --this.member; constant++; (() => { value = null; }); }");
            else if (action == "DropMutableNarrowings") f.Invalidate(action);
            else
            {
                // Lambda names survive state restoration and re-seeding.
                object originalSnapshot = f.OriginalState;
                f.SyntaxAction("NoteLambdaAssignments", "function f() { value = null; }");
                f.Restore(originalSnapshot, snapshot);
                foreach (Pair expression in expressions) f.Seed(expression, f.String);
                Pair? callee = action switch
                {
                    "source" => new(new TestMethod(true, null), new SelfTestMethod(true, null)),
                    "delegate" => new(new TestMethod(false, new UnionType([], TypeKind.Delegate)),
                        new SelfTestMethod(false, new SelfUnionType([], HS.TypeKind.Delegate))),
                    "external" => new(new TestMethod(false, null), new SelfTestMethod(false, null)),
                    _ => null
                };
                f.Invalidate("InvalidateAfterCall", callee);
            }
            f.StateParity();
            Assert.HasCount(8, snapshot);
            foreach (Pair expression in expressions) f.Apply(expression);
        }
    }

    [TestMethod]
    public void AssignedNames_ExactOrdinalNamesAndSyntaxTraversalMatchOriginal()
    {
        Fixture f = new();
        foreach (string source in new[]
        {
            "", "function f() { value = 1; Value += 2; value++; --other; +untouched; }",
            "function f() { ((this.member)) = null; object[index]++; (() => { nested = 1; }); }",
            "function f() { function inner() { item = 2; } obj.property--; }"
        })
        {
            SyntaxNode original = SyntaxTree.ParseText(source).Root;
            HX.SyntaxNode actual = HX.SyntaxTree.ParseText(source).Root;
            string[] expected = [.. ((HashSet<string>)Fixture.Invoke(null, "AssignedNames", original)!).Order()];
            CollectionAssert.AreEqual(expected, H.FlowNarrowing.AssignedNames(actual).Order().ToArray());
            f.SyntaxAction("InvalidateAssignedIn", source);
            f.SyntaxAction("NoteLambdaAssignments", source);
        }
        f.Invalidate("InvalidateAfterCall", null);
    }

    [TestMethod]
    public void WholeLibrary_VerifiesWithoutBootstrapDependency()
    {
        Assembly library = typeof(H.FlowNarrowing).Assembly;
        Assert.IsFalse(library.GetReferencedAssemblies().Any(a => a.Name == "Caladesi.Compiler"));
        Assert.IsEmpty(ILVerifier.Verify(library.GetName().Name!, File.ReadAllBytes(library.Location)));
    }

    private sealed record Pair(object Original, object Actual);

#pragma warning disable CA1822 // Fluent paired-node fixture keeps oracle cases readable.
    private sealed class Fixture
    {
        private static readonly Lazy<HM.ReferenceSet> References = new(() => HM.ReferenceSet.Create(TestReferences.Paths));
        private static readonly Type PathType = typeof(Binder).Assembly.GetType("Caladesi.Compiler.NarrowPath", true)!;
        private static readonly FieldInfo StateField = typeof(Binder).GetField("_narrow", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly Compilation compilation = TestCompiler.Create("class Source {}", OutputKind.DynamicallyLinkedLibrary);
        private readonly Binder binder;
        public readonly H.FlowNarrowing Actual = new();
        public Pair Object => Type("System.Object");
        public Pair String => Type("System.String");
        public Pair Int => Type("System.Int32");
        public Pair Bool => Type("System.Boolean");
        public Pair Null => new(new BoundLiteral(ConstantValue.Null, (TypeSymbol)Object.Original),
            new HB.BoundLiteral(HS.ConstantValue.Null, (HS.TypeSymbol)Object.Actual));
        public Pair This => new(new BoundThis((TypeSymbol)Object.Original), new HB.BoundThis((HS.TypeSymbol)Object.Actual));
        public Fixture()
        {
            binder = new Binder(compilation, compilation.SourceFiles.First(), null, null);
        }
        public Pair Type(string name) => new(compilation.GetWellKnownType(name)!, References.Value.FindType(name)!);
        public Pair Nullable(Pair type) => new(new NullableTypeSymbol((TypeSymbol)type.Original), new HS.NullableTypeSymbol((HS.TypeSymbol)type.Actual));
        public Pair Local(string name, Pair type, bool constant = false) =>
            new(new BoundLocal(new LocalSymbol(name, (TypeSymbol)type.Original, constant, null)) { Syntax = SyntaxTree.ParseText("value").Root },
                new HB.BoundLocal(new HS.LocalSymbol(name, (HS.TypeSymbol)type.Actual, constant, null)) { Syntax = HX.SyntaxTree.ParseText("value").Root });
        public Pair Parameter(string name, Pair type) =>
            new(new BoundParameter(new ParameterSymbol(name, (TypeSymbol)type.Original, 0, null)),
                new HB.BoundParameter(new HS.ParameterSymbol(name, (HS.TypeSymbol)type.Actual, 0, null)));
        public Pair Field(Pair? receiver, string name) =>
            new(new BoundFieldAccess((BoundExpression?)receiver?.Original, new TestField(name, (TypeSymbol)Object.Original)),
                new HB.BoundFieldAccess((HB.BoundExpression?)receiver?.Actual, new SelfTestField(name, (HS.TypeSymbol)Object.Actual)));
        public Pair Property(Pair? receiver, bool indexed = false)
        {
            Pair property = new(((NamedTypeSymbol)Type("System.String").Original).GetMembers("Length").Single(),
                ((HS.NamedTypeSymbol)Type("System.String").Actual).GetMembers("Length").Single());
            return new(new BoundPropertyAccess((BoundExpression?)receiver?.Original, (PropertySymbol)property.Original,
                    indexed ? [(BoundExpression)Null.Original] : []),
                new HB.BoundPropertyAccess((HB.BoundExpression?)receiver?.Actual, (HS.PropertySymbol)property.Actual,
                    indexed ? [(HB.BoundExpression)Null.Actual] : []));
        }
        public IEnumerable<Pair> Expressions()
        {
            Pair local = Local("value", Nullable(Object));
            yield return local;
            yield return Parameter("parameter", Nullable(Int));
            yield return This;
            yield return Field(null, "static");
            yield return Field(local, "member");
            yield return Field(Null, "unrooted");
            yield return Property(null);
            yield return Property(local);
            yield return Property(Null);
            yield return Property(local, true);
            yield return Null;
            yield return new(new BoundBadExpression([], null), new HB.BoundBadExpression([], null));
            yield return new(new BoundBaseReference((TypeSymbol)Object.Original),
                new HB.BoundBaseReference((HS.TypeSymbol)Object.Actual));
            yield return new(new BoundMethodGroup(null, "unresolved", [], []),
                new HB.BoundMethodGroup(null, "unresolved", [], []));
        }
        public Pair Convert(Pair operand, ConversionKind kind, Pair type) =>
            new(new BoundConversion((BoundExpression)operand.Original, kind, (TypeSymbol)type.Original),
                new HB.BoundConversion((HB.BoundExpression)operand.Actual, (HB.ConversionKind)(int)kind, (HS.TypeSymbol)type.Actual));
        public Pair Test(Pair operand, Pair target, bool wasNullable = false) =>
            new(new BoundTypeTest((BoundExpression)operand.Original, (TypeSymbol)target.Original, (TypeSymbol)Bool.Original) { OperandWasNullable = wasNullable },
                new HB.BoundTypeTest((HB.BoundExpression)operand.Actual, (HS.TypeSymbol)target.Actual, (HS.TypeSymbol)Bool.Actual) { OperandWasNullable = wasNullable });
        public Pair Unary(UnaryOperatorKind kind, Pair operand) =>
            new(new BoundUnary(kind, (BoundExpression)operand.Original, (TypeSymbol)Bool.Original),
                new HB.BoundUnary((HB.UnaryOperatorKind)(int)kind, (HB.BoundExpression)operand.Actual, (HS.TypeSymbol)Bool.Actual));
        public Pair Binary(BinaryOperatorKind kind, Pair left, Pair right) =>
            new(new BoundBinary(kind, (BoundExpression)left.Original, (BoundExpression)right.Original, (TypeSymbol)Bool.Original, (TypeSymbol)Object.Original),
                new HB.BoundBinary((HB.BinaryOperatorKind)(int)kind, (HB.BoundExpression)left.Actual, (HB.BoundExpression)right.Actual, (HS.TypeSymbol)Bool.Actual, (HS.TypeSymbol)Object.Actual));
        public Pair Logical(bool and, Pair left, Pair right) =>
            new(new BoundLogicalBinary(and, (BoundExpression)left.Original, (BoundExpression)right.Original, (TypeSymbol)Bool.Original),
                new HB.BoundLogicalBinary(and, (HB.BoundExpression)left.Actual, (HB.BoundExpression)right.Actual, (HS.TypeSymbol)Bool.Actual));
        public static object? Invoke(object? instance, string name, params object?[] args) =>
            typeof(Binder).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!.Invoke(instance, args);
        public object NewPath(Symbol symbol) => Activator.CreateInstance(PathType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [ImmutableArray.Create(symbol)], null)!;
        private static string PathText(object? path)
        {
            if (path is null) return "-";
            return string.Join(".", ((IEnumerable)path.GetType().GetProperty("Parts")!.GetValue(path)!).Cast<object>()
                .Select(p => p.GetType().GetProperty("Name")!.GetValue(p)));
        }
        public void Path(Pair expression)
        {
            object? expected = Invoke(null, "PathOf", expression.Original);
            H.NarrowPath? actual = H.FlowNarrowing.PathOf((HB.BoundExpression)expression.Actual);
            Assert.AreEqual(PathText(expected), PathText(actual));
            if (actual != null)
            {
                Assert.IsTrue(actual.Equals(H.FlowNarrowing.PathOf((HB.BoundExpression)expression.Actual)));
                Assert.AreEqual(actual.GetHashCode(), H.FlowNarrowing.PathOf((HB.BoundExpression)expression.Actual)!.GetHashCode());
            }
        }
        private static string[] OriginalFacts(object facts) =>
            [.. ((IEnumerable)facts.GetType().GetProperty("Items")!.GetValue(facts)!).Cast<object>().Select(item =>
                PathText(item.GetType().GetField("Item1")!.GetValue(item)) + ":" + item.GetType().GetField("Item2")!.GetValue(item))];
        private static string[] SelfFacts(H.Narrowings facts) => [.. facts.Items.Select(i => PathText(i.Path) + ":" + i.Type)];
        public void Facts(Pair expression)
        {
            object result = Invoke(null, "GetNarrowing", expression.Original)!;
            H.NarrowingBranches actual = H.FlowNarrowing.GetNarrowing((HB.BoundExpression)expression.Actual);
            CollectionAssert.AreEqual(OriginalFacts(result.GetType().GetField("Item1")!.GetValue(result)!), SelfFacts(actual.WhenTrue));
            CollectionAssert.AreEqual(OriginalFacts(result.GetType().GetField("Item2")!.GetValue(result)!), SelfFacts(actual.WhenFalse));
        }
        public object OriginalState => StateField.GetValue(binder)!;
        public void Seed(Pair expression, Pair type)
        {
            Pair test = Test(expression, type);
            object branches = Invoke(null, "GetNarrowing", test.Original)!;
            StateField.SetValue(binder, Invoke(null, "ApplyNarrowings", OriginalState, branches.GetType().GetField("Item1")!.GetValue(branches)));
            Actual.Apply(H.FlowNarrowing.GetNarrowing((HB.BoundExpression)test.Actual).WhenTrue);
        }
        public void Restore(object original, ImmutableDictionary<H.NarrowPath, HS.TypeSymbol> actual)
        {
            StateField.SetValue(binder, original);
            Actual.RestoreState(actual);
        }
        private static string Shape(object expression)
        {
            if (expression is BoundConversion c) return $"{c.Kind}/{c.CaseIndex}/{c.Type}({Shape(c.Operand)})";
            if (expression is HB.BoundConversion h) return $"{h.Kind}/{h.CaseIndex}/{h.Type}({Shape(h.Operand)})";
            return expression.GetType().Name;
        }
        public void Wrap(Pair expression, Pair type)
        {
            object expected = Invoke(null, "NarrowExpression", expression.Original, type.Original)!;
            HB.BoundExpression actual = H.FlowNarrowing.NarrowExpression((HB.BoundExpression)expression.Actual, (HS.TypeSymbol)type.Actual);
            Assert.AreEqual(Shape(expected), Shape(actual));
            Assert.AreSame(type.Actual, actual.Type);
            for (HB.BoundExpression node = actual; node is HB.BoundConversion conversion; node = conversion.Operand)
                Assert.AreSame(((HB.BoundExpression)expression.Actual).Syntax, node.Syntax);
        }
#pragma warning restore CA1822
        public void Apply(Pair expression) =>
            Assert.AreEqual(Shape(Invoke(binder, "ApplyNarrowing", expression.Original)!), Shape(Actual.ApplyNarrowing((HB.BoundExpression)expression.Actual)));
        public void Invalidate(string action, Pair? argument = null)
        {
            if (action == "DropMutableNarrowings") { Invoke(binder, action); Actual.DropMutableNarrowings(); }
            else if (action == "InvalidateNarrowing")
            {
                Invoke(binder, action, argument!.Original);
                Actual.InvalidateNarrowing((HB.BoundExpression)argument.Actual);
            }
            else { Invoke(binder, action, argument?.Original); Actual.InvalidateAfterCall((HS.MethodSymbol?)argument?.Actual); }
        }
        public void SyntaxAction(string action, string source)
        {
            Invoke(binder, action, SyntaxTree.ParseText(source).Root);
            if (action == "NoteLambdaAssignments") Actual.NoteLambdaAssignments(HX.SyntaxTree.ParseText(source).Root);
            else Actual.InvalidateAssignedIn(HX.SyntaxTree.ParseText(source).Root);
        }
        public void StateParity()
        {
            string[] expected = [.. ((IEnumerable)OriginalState).Cast<object>().Select(entry =>
                PathText(entry.GetType().GetProperty("Key")!.GetValue(entry)) + ":" + entry.GetType().GetProperty("Value")!.GetValue(entry)).Order()];
            CollectionAssert.AreEqual(expected, Actual.State.Select(entry => PathText(entry.Key) + ":" + entry.Value).Order().ToArray());
        }
    }

    private sealed class TestField(string name, TypeSymbol type) : FieldSymbol
    {
        public override string Name => name;
        public override TypeSymbol Type => type;
    }
    private sealed class IdentitySymbol(Symbol? definition) : Symbol
    {
        public override SymbolKind Kind => SymbolKind.Parameter;
        public override string Name => "identity";
        public override Symbol OriginalDefinition => definition ?? this;
    }
    private sealed class SelfIdentitySymbol(HS.Symbol? definition) : HS.Symbol
    {
        public override HS.SymbolKind Kind => HS.SymbolKind.Parameter;
        public override string Name => "identity";
        public override HS.Symbol OriginalSymbolDefinition => definition ?? this;
    }
    private sealed class SelfTestField(string name, HS.TypeSymbol type) : HS.FieldSymbol
    {
        public override string Name => name;
        public override HS.TypeSymbol Type => type;
    }
    private sealed class TestMethod(bool source, NamedTypeSymbol? container) : MethodSymbol
    {
        public override string Name => "call";
        public override bool IsFromSource => source;
        public override Symbol? ContainingSymbol => container;
        public override MethodKind MethodKind => MethodKind.Ordinary;
        public override TypeSymbol ReturnType => ErrorTypeSymbol.Instance;
        public override ImmutableArray<ParameterSymbol> Parameters => [];
    }
    private sealed class SelfTestMethod(bool source, HS.NamedTypeSymbol? container) : HS.MethodSymbol
    {
        public override string Name => "call";
        public override bool IsFromSource => source;
        public override HS.Symbol? ContainingSymbol => container;
        public override HS.MethodKind MethodKind => HS.MethodKind.Ordinary;
        public override HS.TypeSymbol ReturnType => HS.ErrorTypeSymbol.Instance;
        public override ImmutableArray<HS.ParameterSymbol> Parameters => [];
    }
    private sealed class UnionType(ImmutableArray<TypeSymbol> cases, TypeKind kind = TypeKind.Class) : NamedTypeSymbol
    {
        public override string Name => "Union";
        public override TypeKind TypeKind => kind;
        public override bool IsUnionType => true;
        public override ImmutableArray<TypeSymbol> UnionCases => cases;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<TypeSymbol> TypeArguments => [];
    }
    private sealed class SelfUnionType(ImmutableArray<HS.TypeSymbol> cases, HS.TypeKind kind = HS.TypeKind.Class) : HS.NamedTypeSymbol
    {
        public override string Name => "Union";
        public override HS.TypeKind TypeKind => kind;
        public override bool IsUnionType => true;
        public override ImmutableArray<HS.TypeSymbol> UnionCases => cases;
        public override ImmutableArray<HS.TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<HS.TypeSymbol> TypeArguments => [];
    }
}
