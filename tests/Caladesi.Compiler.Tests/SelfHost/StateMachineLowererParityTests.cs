extern alias SelfHost;

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Tests.Emit;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using HE = SelfHost::Caladesi.Compiler.Emit;
using HSS = SelfHost::Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class StateMachineLowererParityTests
{
    [TestMethod]
    [DataRow("System.Threading.Tasks.Task", "AsyncTaskMethodBuilder", false)]
    [DataRow("System.Threading.Tasks.Task`1", "AsyncTaskMethodBuilder`1", true)]
    [DataRow("System.Threading.Tasks.ValueTask", "AsyncValueTaskMethodBuilder", false)]
    [DataRow("System.Threading.Tasks.ValueTask`1", "AsyncValueTaskMethodBuilder`1", true)]
    [DataRow("System.Void", "AsyncVoidMethodBuilder", false)]
    public void AsyncSignatures_SelectBuilderAndLowerAwaitDispatch(string returnType, string builder, bool payload)
    {
        ArgumentNullException.ThrowIfNull(returnType);
        Fixture f = new(returnType, async: true);
        f.Lower(f.Block(f.Statement(f.Await()), f.Node("BoundReturn", payload ? f.Literal(42) : null)));
        f.Compare();
        HS.NamedTypeSymbol machine = f.Machine;
        HS.NamedTypeSymbol builderType = (HS.NamedTypeSymbol)machine.GetMembers().OfType<HS.FieldSymbol>()
            .Single(field => field.Name == "<>t__builder").Type;
        Assert.AreEqual(builder, builderType.OriginalDefinition.MetadataName);
        Assert.AreEqual(payload, machine.GetMembers().Any(member => member.Name == "<>s__retval"));
        Assert.AreEqual("IAsyncStateMachine", machine.Interfaces.Single().Name);
        Assert.HasCount(1, machine.GetMembers().OfType<HS.FieldSymbol>().Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal)));
        Assert.IsTrue(f.MoveNextNodes.OfType<HB.BoundCall>().Any(call => call.Method.Name == "AwaitUnsafeOnCompleted"));
        Assert.IsTrue(f.MoveNextNodes.OfType<HB.BoundCall>().Any(call => call.Method.Name == "SetException"));
        Assert.IsTrue(f.MoveNextNodes.OfType<HB.BoundCall>().Any(call => call.Method.Name == "SetResult"));
        f.AssertDispatch(1);
    }

    [TestMethod]
    [DataRow("System.Collections.IEnumerator", false)]
    [DataRow("System.Collections.IEnumerable", true)]
    [DataRow("System.Collections.Generic.IEnumerator`1", false)]
    [DataRow("System.Collections.Generic.IEnumerable`1", true)]
    public void IteratorSignatures_PreserveInterfacesCurrentBoxingAndEnumerableParameterCopies(string returnType, bool enumerable)
    {
        ArgumentNullException.ThrowIfNull(returnType);
        Fixture f = new(returnType, iterator: true);
        Pair parameter = f.AddParameter("value");
        Pair first = f.Node("BoundParameter", parameter);
        Pair second = f.Literal(2);
        if (!returnType.Contains('`'))
        {
            Pair objectType = f.Type("System.Object");
            first = f.Node("BoundConversion", first, (int)ConversionKind.Boxing, objectType);
            second = f.Node("BoundConversion", second, (int)ConversionKind.Boxing, objectType);
        }
        f.Lower(f.Block(f.Node("BoundYieldReturn", first),
            f.Node("BoundYieldReturn", second), f.Node("BoundYieldBreak")));
        f.Compare();
        Assert.HasCount(enumerable ? 5 : 3, f.Machine.Interfaces);
        Assert.AreEqual(enumerable, f.Machine.GetMembers().Any(member => member.Name == "<>3__value"));
        Assert.AreEqual(enumerable, f.Machine.GetMembers().Any(member => member.Name == "GetEnumerator"));
        HS.MethodSymbol explicitCurrent = f.Machine.GetMembers().OfType<HS.MethodSymbol>()
            .Single(method => method.Name == "System.Collections.IEnumerator.get_Current");
        Assert.HasCount(1, explicitCurrent.ExplicitInterfaceImplementations);
        HB.BoundConversion current = (HB.BoundConversion)((HB.BoundReturn)f.Program.Bodies[explicitCurrent].Statements.Single()).Expression!;
        Assert.AreEqual(returnType.Contains('`') ? HB.ConversionKind.Boxing : HB.ConversionKind.ImplicitReference, current.Kind);
        Assert.IsTrue(f.Program.Bodies.Keys.Any(method => method.Name == "Reset"));
        Assert.IsTrue(f.Program.Bodies.Keys.Any(method => method.Name == "Dispose"));
        f.AssertDispatch(2);
    }

    [TestMethod]
    [DataRow("System.Collections.Generic.IAsyncEnumerable`1", true)]
    [DataRow("System.Collections.Generic.IAsyncEnumerator`1", false)]
    public void AsyncIterators_CombineAwaitAndYieldStatesWithAsyncDisposal(string returnType, bool enumerable)
    {
        ArgumentNullException.ThrowIfNull(returnType);
        Fixture f = new(returnType, async: true, iterator: true);
        Pair parameter = f.AddParameter("value");
        f.Lower(f.Block(f.Statement(f.Await()),
            f.Node("BoundYieldReturn", f.Node("BoundParameter", parameter)),
            f.Statement(f.Await()), f.Node("BoundYieldBreak")));
        f.Compare();
        Assert.HasCount(enumerable ? 3 : 2, f.Machine.Interfaces);
        Assert.AreEqual(enumerable, f.Machine.GetMembers().Any(member => member.Name == "GetAsyncEnumerator"));
        Assert.AreEqual(enumerable, f.Machine.GetMembers().Any(member => member.Name == "<>3__value"));
        HS.MethodSymbol move = f.Program.Bodies.Keys.Single(method => method.Name == "MoveNextAsync");
        HS.MethodSymbol dispose = f.Program.Bodies.Keys.Single(method => method.Name == "DisposeAsync");
        Assert.AreEqual("ValueTask`1", ((HS.NamedTypeSymbol)move.ReturnType).OriginalDefinition.MetadataName);
        Assert.AreEqual("ValueTask", dispose.ReturnType.Name);
        Assert.IsTrue(Fixture.Walk(f.Program.Bodies[dispose]).OfType<HB.BoundCall>().Any(call => call.Method.Name == "AsTask"));
        Assert.IsGreaterThanOrEqualTo(2, f.MoveNextNodes.OfType<HB.BoundCall>().Count(call => call.Method.Name == "SetResult"));
        f.AssertDispatch(3);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NestedAwaitExpressions_HoistLocalsParametersThisAndReuseAwaiterFields(bool valueTask)
    {
        Fixture f = new("System.Threading.Tasks.Task`1", async: true, isStatic: false);
        Pair parameter = f.AddParameter("value");
        Pair local = f.Local("saved");
        Pair sum = f.Node("BoundBinary", (int)BinaryOperatorKind.Add,
            f.Node("BoundParameter", parameter), f.Await(valueTask), f.Int, f.Int);
        Pair nested = f.Node("BoundBinary", (int)BinaryOperatorKind.Add, sum, f.Await(valueTask), f.Int, f.Int);
        f.Lower(f.Block([local],
            f.Node("BoundLocalDeclaration", local, nested),
            f.Statement(f.Node("BoundThis", f.Container)),
            f.Node("BoundReturn", f.Node("BoundLocal", local))));
        f.Compare();
        HS.FieldSymbol[] fields = f.Machine.GetMembers().OfType<HS.FieldSymbol>().ToArray();
        Assert.HasCount(1, fields.Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal)));
        Assert.IsTrue(fields.Any(field => field.Name == "value"));
        Assert.IsTrue(fields.Any(field => field.Name.StartsWith("<saved>5__", StringComparison.Ordinal)));
        Assert.AreSame(f.Container.Actual, fields.Single(field => field.Name == "<>4__this").Type);
        Assert.IsEmpty(f.MoveNextNodes.OfType<HB.BoundParameter>());
        Assert.IsFalse(f.MoveNextNodes.OfType<HB.BoundLocal>().Any(node => ReferenceEquals(node.Local, local.Actual)));
        Assert.IsTrue(f.MoveNextNodes.OfType<HB.BoundAssignment>().All(assignment => assignment.IsDiscarded));
        f.AssertDispatch(2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StaticGetAwaiter_SelectsSafeOrUnsafeContinuationFromImplementedInterfaces(bool critical)
    {
        Fixture f = new("System.Threading.Tasks.Task", async: true);
        f.Lower(f.Block(f.Statement(f.StaticAwait(critical)), f.Node("BoundReturn", (object?)null)));
        f.Compare();
        string continuation = critical ? "AwaitUnsafeOnCompleted" : "AwaitOnCompleted";
        HB.BoundCall registration = f.MoveNextNodes.OfType<HB.BoundCall>()
            .Single(call => call.Method.Name == continuation);
        Assert.HasCount(2, registration.Arguments);
        Assert.IsTrue(registration.Arguments.All(argument => argument is HB.BoundAddressOf));
        HB.BoundCall getAwaiter = f.MoveNextNodes.OfType<HB.BoundCall>()
            .Single(call => call.Method.Name == "GetAwaiter");
        Assert.IsNull(getAwaiter.Receiver);
        Assert.HasCount(1, getAwaiter.Arguments);
        f.AssertDispatch(1);
    }

    [TestMethod]
    public void MixedAndVoidAwaits_UseDistinctAwaiterFieldsAndPreserveConditionalEvaluation()
    {
        Fixture f = new("System.Threading.Tasks.Task`1", async: true);
        Pair condition = f.Node("BoundLiteral", f.Create("Symbols.ConstantValue", true), f.Type("System.Boolean"));
        Pair conditional = f.Node("BoundConditional", condition, f.Await(), f.Await(valueTask: true), f.Int);
        f.Lower(f.Block(f.Statement(f.Await(voidResult: true)), f.Node("BoundReturn", conditional)));
        f.Compare();
        Assert.HasCount(3, f.Machine.GetMembers().OfType<HS.FieldSymbol>()
            .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal)));
        Assert.IsTrue(f.MoveNextNodes.OfType<HB.BoundCall>().Any(call => call.Method.Name == "GetResult" && call.Method.ReturnType.IsVoid));
        f.AssertDispatch(3);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ProtectedSuspensions_ReplayFinallyCatchAndDisposalBranches(bool iterator, bool asyncIterator)
    {
        Fixture f = new(iterator ? asyncIterator ? "System.Collections.Generic.IAsyncEnumerable`1" :
            "System.Collections.Generic.IEnumerable`1" : "System.Threading.Tasks.Task`1",
            async: !iterator || asyncIterator, iterator: iterator);
        Pair caught = f.Local("caught", f.Type("System.Exception"));
        Pair suspension = iterator ? f.Node("BoundYieldReturn", f.Literal(1)) : f.Statement(f.Await());
        Pair handler = f.Create("Bound.BoundCatch", caught, f.Type("System.Exception"),
            f.Block(iterator ? f.Node("BoundYieldReturn", f.Literal(2)) : f.Statement(f.Await()),
                f.Node("BoundThrow", (object?)null)));
        Pair inner = f.Node("BoundTry", f.Block(suspension,
                iterator ? f.Node("BoundYieldBreak") : f.Node("BoundReturn", f.Literal(3))),
            Fixture.Array("Bound.BoundCatch", handler),
            f.Block(f.Statement(!iterator || asyncIterator ? f.Await() : f.Literal(9))));
        Pair outer = f.Node("BoundTry", f.Block(inner), Fixture.Array("Bound.BoundCatch"),
            f.Block(f.Statement(f.Literal(10))));
        f.Lower(f.Block(outer));
        f.Compare();
        Assert.IsTrue(f.MoveNextNodes.OfType<HB.BoundCall>().Any(call => call.Method.Name == "Capture"));
        Assert.IsTrue(f.MoveNextNodes.OfType<HB.BoundCall>().Any(call => call.Method.Name == "Throw"));
        Assert.IsGreaterThanOrEqualTo(2, f.MoveNextNodes.OfType<HB.BoundTry>().Count());
        if (iterator)
            Assert.IsTrue(f.MoveNextNodes.OfType<HB.BoundFieldAccess>().Any(access => access.Field.Name == "<>__disposing"));
        f.AssertDispatch(iterator && !asyncIterator ? 2 : 3);
    }

    [TestMethod]
    [DataRow("System.Threading.Tasks.Task`1", false)]
    [DataRow("System.Collections.Generic.IEnumerable`1", true)]
    [DataRow("System.Collections.Generic.IAsyncEnumerable`1", true)]
    public void GenericContainersAndMethods_PreserveClosedMachineAndTypeParameterMaps(string returnType, bool iterator)
    {
        ArgumentNullException.ThrowIfNull(returnType);
        Fixture f = new(returnType, async: !iterator || returnType.Contains("Async", StringComparison.Ordinal),
            iterator: iterator, generic: true, isStatic: false);
        Pair parameter = f.AddParameter("value", f.MethodParameter);
        Pair expression = f.Node("BoundParameter", parameter);
        f.Lower(f.Block(iterator ? f.Node("BoundYieldReturn", expression) : f.Node("BoundReturn", expression)));
        f.Compare();
        HSS.SynthesizedNamedType machine = (HSS.SynthesizedNamedType)f.Machine;
        Assert.AreEqual(2, machine.Arity);
        Assert.AreEqual(1, machine.OwnArity);
        Assert.AreEqual(1, machine.MethodTypeParameterMap![(HS.TypeParameterSymbol)f.MethodParameter!.Actual]);
        Assert.IsTrue(machine.TypeParameters.All(typeParameter => ReferenceEquals(typeParameter.ContainingSymbol, machine)));
        HB.BoundObjectCreation creation = Fixture.Walk(f.Program.Bodies[(HS.MethodSymbol)f.Method.Actual])
            .OfType<HB.BoundObjectCreation>().Single();
        HS.NamedTypeSymbol closedMachine = (HS.NamedTypeSymbol)creation.Type!;
        Assert.AreSame(machine, closedMachine.OriginalDefinition);
        Assert.AreSame(f.MethodParameter!.Actual, closedMachine.TypeArguments[1]);
        Assert.IsEmpty(f.Program.ClassTypeParameterMaps);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NoSuspension_StillGeneratesCompletionAndPreservesSourceAndEntryPoint(bool source)
    {
        Fixture f = new("System.Threading.Tasks.Task", async: true, source: source);
        f.Lower(f.Block(f.Node("BoundReturn", (object?)null)));
        f.Compare();
        Assert.IsEmpty(f.Machine.GetMembers().Where(member => member.Name.StartsWith("<>u__", StringComparison.Ordinal)));
        Assert.AreSame(f.Method.Actual, f.Program.EntryPoint);
        Assert.HasCount(source ? 1 : 0, f.Program.MethodFiles);
        if (source)
            Assert.AreSame(f.File, f.Program.MethodFiles[(HS.MethodSymbol)f.Method.Actual]);
        f.AssertDispatch(0);
    }

    [TestMethod]
    public void SelfHostLibrary_HasNoBootstrapDependencyAndPassesWholeLibraryILVerification()
    {
        Assembly assembly = typeof(HE.LoweredProgram<>).Assembly;
        Assert.IsFalse(assembly.GetReferencedAssemblies().Any(reference => reference.Name == "Caladesi.Compiler"));
        Assert.IsEmpty(ILVerifier.Verify(assembly.GetName().Name!, File.ReadAllBytes(assembly.Location)));
    }

    private sealed record Pair(object Expected, object Actual);

    private sealed class Fixture
    {
        private readonly Compilation compilation = TestCompiler.Create("", OutputKind.DynamicallyLinkedLibrary);
        private static readonly Lazy<SelfHost::Caladesi.Compiler.Symbols.Metadata.ReferenceSet> s_references =
            new(() => SelfHost::Caladesi.Compiler.Symbols.Metadata.ReferenceSet.Create(TestReferences.Paths));
        private readonly Dictionary<object, object> identities = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, object> generatedSymbols = new(ReferenceEqualityComparer.Instance);
        private readonly object expectedProgram = Activator.CreateInstance(
            typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.LoweredProgram", true)!, nonPublic: true)!;
        private readonly Pair syntax = new(Caladesi.Compiler.Syntax.SyntaxTree.ParseText("yield return 1;").Root,
            SelfHost::Caladesi.Compiler.Syntax.SyntaxTree.ParseText("yield return 1;").Root);
        public HE.LoweredProgram<SourceFile> Program { get; } = new();
        public Pair Container { get; }
        public Pair Method { get; }
        public Pair Int { get; }
        public Pair? MethodParameter { get; }
        public SourceFile File { get; }
        public HS.NamedTypeSymbol Machine => Program.Types.Single();
        public IEnumerable<HB.BoundNode> MoveNextNodes => Walk(Program.Bodies[
            Program.Bodies.Keys.Single(method => method.Name == "MoveNext")]);

        public Fixture(string returnType, bool async = false, bool iterator = false, bool isStatic = true,
            bool generic = false, bool source = true)
        {
            Remember(syntax);
            Int = Type("System.Int32");
            Container = Create("Symbols.Source.SynthesizedNamedType", "Container", null, 0);
            Method = Create("Symbols.Source.SynthesizedMethodSymbol", Container, "Run", 0, 0, isStatic,
                Int, Array("Symbols.ParameterSymbol"));
            Set(Method, "IsAsyncMethod", async);
            Set(Method, "IsIteratorMethod", iterator);
            if (generic)
            {
                Pair classParameter = Create("Symbols.TypeParameterSymbol", "T", 0, Container, 0);
                Call(Container, "SetTypeParameters", Array("Symbols.TypeParameterSymbol", classParameter));
                MethodParameter = Create("Symbols.TypeParameterSymbol", "U", 0, Method, 0);
                Call(Method, "SetTypeParameters", Array("Symbols.TypeParameterSymbol", MethodParameter));
            }
            Pair result = Type(returnType);
            if (returnType.Contains('`'))
                result = Construct(result, MethodParameter ?? Int);
            Call(Method, "SetReturnType", result);
            File = new SourceFile(compilation, Caladesi.Compiler.Syntax.SyntaxTree.ParseText("", "machine.cala"));
            if (source)
            {
                ((IDictionary)Get(expectedProgram, "MethodFiles")!).Add(Method.Expected, File);
                Program.MethodFiles.Add((HS.MethodSymbol)Method.Actual, File);
            }
            Set(new(expectedProgram, Program), "EntryPoint", Method);
        }

        public Pair Type(string name)
        {
            Pair pair = new(compilation.GetWellKnownType(name)!, s_references.Value.FindType(name)!);
            Assert.IsNotNull(pair.Expected, name);
            Assert.IsNotNull(pair.Actual, name);
            return Remember(pair);
        }

        private Pair Construct(Pair definition, Pair argument) => Remember(new(
            ((NamedTypeSymbol)definition.Expected).Construct(new TypeSymbol[] { (TypeSymbol)argument.Expected }),
            ((HS.NamedTypeSymbol)definition.Actual).Construct(new HS.TypeSymbol[] { (HS.TypeSymbol)argument.Actual })));

        private Pair Remember(Pair pair)
        {
            if (identities.TryGetValue(pair.Expected, out object? known))
                Assert.AreSame(known, pair.Actual);
            else
                identities.Add(pair.Expected, pair.Actual);
            return pair;
        }

        public Pair Local(string name, Pair? type = null) =>
            Create("Symbols.LocalSymbol", name, type ?? Int, false, Method, null);
        public Pair AddParameter(string name, Pair? type = null)
        {
            Pair parameter = Create("Symbols.ParameterSymbol", name, type ?? Int, 0, Method);
            Call(Method, "SetParameters", Array("Symbols.ParameterSymbol", parameter));
            return parameter;
        }
        public Pair Literal(int value) => Node("BoundLiteral", Create("Symbols.ConstantValue", value), Int);
        public Pair Statement(Pair expression) => Node("BoundExpressionStatement", expression);
        public Pair Block(params Pair[] statements) => Block([], statements);
        public Pair Block(Pair[] locals, params Pair[] statements) =>
            Node("BoundBlock", Array("Symbols.LocalSymbol", locals), Array("Bound.BoundStatement", statements));
        public Pair Node(string name, params object?[] arguments)
        {
            Pair node = Create("Bound." + name, arguments);
            Set(node, "Syntax", syntax);
            return node;
        }

        public Pair Await(bool valueTask = false, bool voidResult = false)
        {
            Pair task = Type(valueTask ? voidResult ? "System.Threading.Tasks.ValueTask" : "System.Threading.Tasks.ValueTask`1" :
                voidResult ? "System.Threading.Tasks.Task" : "System.Threading.Tasks.Task`1");
            if (!voidResult)
                task = Construct(task, Int);
            Pair getAwaiter = Members(task, "GetAwaiter");
            Pair awaiter = Remember(new(((MethodSymbol)getAwaiter.Expected).ReturnType, ((HS.MethodSymbol)getAwaiter.Actual).ReturnType));
            Pair info = Create("Bound.AwaitInfo", getAwaiter, null, Members(awaiter, "get_IsCompleted"),
                Members(awaiter, "GetResult"), awaiter);
            return Node("BoundAwait", Node("BoundDefaultValue", task), info, voidResult ? Type("System.Void") : Int);
        }

        public Pair StaticAwait(bool critical)
        {
            Pair awaiter = Create("Symbols.Source.SynthesizedNamedType", "Awaiter", Container, 1);
            Call(awaiter, "AddInterface", Type(critical ? "System.Runtime.CompilerServices.ICriticalNotifyCompletion" :
                "System.Runtime.CompilerServices.INotifyCompletion"));
            Pair getAwaiter = Create("Symbols.Source.SynthesizedMethodSymbol", Container, "GetAwaiter", 0, 0,
                true, awaiter, Array("Symbols.ParameterSymbol"));
            Pair parameter = Create("Symbols.ParameterSymbol", "operand", Int, 0, getAwaiter);
            Call(getAwaiter, "SetParameters", Array("Symbols.ParameterSymbol", parameter));
            Pair completed = Create("Symbols.Source.SynthesizedMethodSymbol", awaiter, "get_IsCompleted", 0, 0,
                false, Type("System.Boolean"), Array("Symbols.ParameterSymbol"));
            Pair result = Create("Symbols.Source.SynthesizedMethodSymbol", awaiter, "GetResult", 0, 0,
                false, Int, Array("Symbols.ParameterSymbol"));
            Pair info = Create("Bound.AwaitInfo", getAwaiter, null, completed, result, awaiter);
            return Node("BoundAwait", Literal(1), info, Int);
        }

        private Pair Members(Pair type, string name) => Remember(new(
            ((NamedTypeSymbol)type.Expected).GetMembers(name).OfType<MethodSymbol>().First(method => method.Parameters.IsEmpty),
            ((HS.NamedTypeSymbol)type.Actual).GetMembers(name).OfType<HS.MethodSymbol>().First(method => method.Parameters.IsEmpty)));

        public void Lower(Pair body)
        {
            MethodInfo oracle = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.StateMachineLowerer", true)!
                .GetMethod("Lower")!;
            oracle.Invoke(null, [compilation, expectedProgram, Method.Expected, Container.Expected, 7, body.Expected]);
            MethodInfo actual = typeof(HE.LoweredProgram<>).Assembly.GetType("Caladesi.Compiler.Emit.StateMachineLowerer`1", true)!
                .MakeGenericType(typeof(SourceFile)).GetMethod("Lower")!;
            Func<HS.SpecialType, HS.NamedTypeSymbol> getSpecial = special => s_references.Value.GetSpecialType(special)!;
            Func<string, HS.NamedTypeSymbol?> getWellKnown = s_references.Value.FindType;
            actual.Invoke(null, [getSpecial, getWellKnown, Program, Method.Actual, Container.Actual, 7, body.Actual]);
        }

        public void Compare()
        {
            foreach (string property in new[] { "Types", "Bodies", "ExtraMembers", "ClassTypeParameterMaps", "MethodFiles", "EntryPoint" })
                Equivalent(Get(expectedProgram, property), Get(Program, property), property);
            Assert.AreEqual("<Run>d__7", Machine.Name);
            Assert.AreSame(Container.Actual, Machine.ContainingSymbol);
            Assert.IsTrue(Machine.IsSealed);
            Assert.AreEqual(HS.Accessibility.Private, Machine.DeclaredAccessibility);
            Assert.IsFalse(MoveNextNodes.Any(node => node is HB.BoundAwait or HB.BoundYieldReturn or HB.BoundYieldBreak));
            foreach (HS.MethodSymbol method in Program.Bodies.Keys.Where(method => !ReferenceEquals(method, Method.Actual)))
                Assert.IsTrue(Program.MembersOf(Machine).Any(member => ReferenceEquals(member, method)));
        }

        public void AssertDispatch(int states)
        {
            HB.BoundNode[] nodes = MoveNextNodes.ToArray();
            HB.BoundConditionalGoto[] dispatch = nodes.OfType<HB.BoundConditionalGoto>().ToArray();
            Assert.IsGreaterThanOrEqualTo(states, dispatch.Length);
            Assert.HasCount(states, nodes.OfType<HB.BoundLabel>().Where(node => node.Label.Name == "<resume>"));
            HashSet<HS.LabelSymbol> labels = nodes.OfType<HB.BoundLabel>().Select(node => node.Label).ToHashSet();
            foreach (HB.BoundConditionalGoto branch in dispatch)
                CollectionAssert.Contains(labels.ToArray(), branch.Label, "Dispatch target must be defined in MoveNext.");
            int[] numbers = dispatch.Select(branch => branch.Condition).OfType<HB.BoundBinary>()
                .Select(binary => binary.Right).OfType<HB.BoundLiteral>()
                .Select(literal => literal.Value.Value).OfType<int>().Where(value => value >= 0).Distinct().Order().ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(0, states).ToArray(), numbers);
        }

        public static IEnumerable<HB.BoundNode> Walk(HB.BoundNode node)
        {
            yield return node;
            foreach (PropertyInfo property in node.GetType().GetProperties())
            {
                object? value = property.GetValue(node);
                if (value is HB.BoundNode child)
                    foreach (HB.BoundNode descendant in Walk(child)) yield return descendant;
                else if (value is IEnumerable sequence && value is not string)
                    foreach (object? item in sequence)
                    {
                        if (item is HB.BoundNode itemNode)
                            foreach (HB.BoundNode descendant in Walk(itemNode)) yield return descendant;
                        else if (item is HB.BoundCatch handler)
                            foreach (HB.BoundNode descendant in Walk(handler.Body)) yield return descendant;
                    }
            }
        }

        private void Equivalent(object? expected, object? actual, string path)
        {
            if (expected == null) { Assert.IsNull(actual, path); return; }
            Assert.IsNotNull(actual, path);
            if (ReferenceEquals(expected, File)) { Assert.AreSame(File, actual, path); return; }
            if (identities.TryGetValue(expected, out object? known)) { Assert.AreSame(known, actual, path); return; }
            if (expected is Enum) { Assert.AreEqual(Convert.ToInt32(expected), Convert.ToInt32(actual), path); return; }
            if (expected is string || expected.GetType().IsPrimitive) { Assert.AreEqual(expected, actual, path); return; }
            if (expected is IDictionary dictionary)
            {
                IDictionary other = (IDictionary)actual;
                Assert.HasCount(dictionary.Count, other, path);
                foreach (DictionaryEntry entry in dictionary)
                {
                    Assert.IsTrue(identities.TryGetValue(entry.Key, out object? key), path + ": unmapped key " + entry.Key);
                    CollectionAssert.Contains(other.Keys, key, path);
                    Equivalent(entry.Value, other[key!], path + "[" + entry.Key + "]");
                }
                return;
            }
            if (expected is IEnumerable sequence)
            {
                object[] left = sequence.Cast<object>().ToArray(), right = ((IEnumerable)actual).Cast<object>().ToArray();
                Assert.HasCount(left.Length, right, path);
                for (int i = 0; i < left.Length; i++) Equivalent(left[i], right[i], path + "[" + i + "]");
                return;
            }
            Assert.AreEqual(expected.GetType().Name, actual.GetType().Name, path);
            if (expected is SynthesizedNamedType or SynthesizedMethodSymbol or SynthesizedFieldSymbol or LocalSymbol or LabelSymbol or TypeParameterSymbol)
            {
                Assert.IsFalse(generatedSymbols.ContainsKey(actual), path + ": distinct oracle symbols must not collapse.");
                generatedSymbols.Add(actual, expected);
            }
            identities.Add(expected, actual);
            if (expected is NamedTypeSymbol named && expected is not SynthesizedNamedType)
            {
                Assert.AreEqual(named.ToMetadataString(), ((HS.NamedTypeSymbol)actual).ToMetadataString(), path);
                Equivalent(named.SpecialType, ((HS.NamedTypeSymbol)actual).SpecialType, path + ".SpecialType");
                if (!ReferenceEquals(named, named.OriginalDefinition))
                {
                    Equivalent(named.OriginalDefinition, ((HS.NamedTypeSymbol)actual).OriginalDefinition, path + ".OriginalDefinition");
                    Equivalent(named.TypeArguments, ((HS.NamedTypeSymbol)actual).TypeArguments, path + ".TypeArguments");
                }
                return;
            }
            if (expected is Symbol)
            {
                string[] properties = expected switch
                {
                    SynthesizedNamedType => ["Name", "MetadataName", "ContainingSymbol", "DeclaredAccessibility", "TypeKind",
                        "IsSealed", "IsStatic", "IsAbstract", "BaseType", "Interfaces", "TypeParameters", "TypeArguments",
                        "OwnArity", "OriginalDefinition", "IsCompilerGenerated", "MethodTypeParameterMap"],
                    MethodSymbol => ["Name", "ContainingSymbol", "DeclaredAccessibility", "MethodKind", "IsStatic",
                        "IsAsync", "IsIterator", "IsVirtual", "IsAbstract", "IsOverride", "IsSealed", "ReturnType",
                        "Parameters", "TypeParameters", "TypeArguments", "ExplicitInterfaceImplementations"],
                    FieldSymbol => ["Name", "ContainingSymbol", "DeclaredAccessibility", "IsStatic", "IsReadOnly", "Type"],
                    ParameterSymbol => ["Name", "ContainingSymbol", "Type", "Ordinal", "RefKind"],
                    LocalSymbol => ["Name", "ContainingSymbol", "Type", "IsSynthesized", "IsConst"],
                    TypeParameterSymbol => ["Name", "ContainingSymbol", "Ordinal", "Variance", "HasReferenceTypeConstraint",
                        "HasValueTypeConstraint", "HasConstructorConstraint", "HasNotNullConstraint", "ConstraintTypes"],
                    _ => ["Name", "ContainingSymbol"]
                };
                foreach (string property in properties) Equivalent(Get(expected, property), Get(actual, property), path + "." + property);
                if (expected is SynthesizedNamedType type)
                    Equivalent(type.GetMembers(), ((HS.NamedTypeSymbol)actual).GetMembers(), path + ".Members");
                if (expected is SynthesizedMethodSymbol)
                    foreach (string property in new[] { "SourceMethod", "LambdaOrigin", "SynthesizedKind" })
                        Equivalent(Get(expected, property), Get(actual, property), path + "." + property);
                return;
            }
            foreach (PropertyInfo property in expected.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                Equivalent(property.GetValue(expected), Get(actual, property.Name), path + "." + property.Name);
        }

        private static object? Get(object target, string name)
        {
            for (Type? type = target.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo? property = type.GetProperty(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public);
                if (property != null) return property.GetValue(target);
            }
            throw new InvalidOperationException("Missing property: " + name);
        }
        public Pair Create(string name, params object?[] arguments)
        {
            object Build(bool self)
            {
                Assembly assembly = self ? typeof(HB.BoundNode).Assembly : typeof(BoundNode).Assembly;
                Type type = assembly.GetType("Caladesi.Compiler." + name, true)!;
                ConstructorInfo ctor = type.GetConstructors()
                    .Where(candidate => candidate.GetParameters().Length >= arguments.Length &&
                        candidate.GetParameters().Skip(arguments.Length).All(parameter => parameter.HasDefaultValue))
                    .OrderBy(candidate => candidate.GetParameters().Length).First();
                object?[] values = ctor.GetParameters().Select((parameter, index) =>
                {
                    object? value = index < arguments.Length ? arguments[index] : parameter.DefaultValue;
                    if (value is Pair pair) value = self ? pair.Actual : pair.Expected;
                    if (value != null && parameter.ParameterType.IsEnum) value = Enum.ToObject(parameter.ParameterType, value);
                    return value;
                }).ToArray();
                return ctor.Invoke(values);
            }
            return Remember(new(Build(false), Build(true)));
        }

        private static void Call(Pair target, string name, Pair argument)
        {
            target.Expected.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(target.Expected, [argument.Expected]);
            target.Actual.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(target.Actual, [argument.Actual]);
        }
        private static void Set(Pair target, string name, object value)
        {
            target.Expected.GetType().GetProperty(name)!.SetValue(target.Expected, value is Pair pair ? pair.Expected : value);
            target.Actual.GetType().GetProperty(name)!.SetValue(target.Actual, value is Pair other ? other.Actual : value);
        }
        public static Pair Array(string name, params Pair[] items)
        {
            object Build(bool self)
            {
                Assembly assembly = self ? typeof(HB.BoundNode).Assembly : typeof(BoundNode).Assembly;
                Type element = assembly.GetType("Caladesi.Compiler." + name, true)!;
                System.Array values = System.Array.CreateInstance(element, items.Length);
                for (int i = 0; i < items.Length; i++) values.SetValue(self ? items[i].Actual : items[i].Expected, i);
                MethodInfo create = typeof(ImmutableArray).GetMethods().Single(method => method.Name == "Create" &&
                    method.IsGenericMethodDefinition && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.IsArray);
                return create.MakeGenericMethod(element).Invoke(null, [values])!;
            }
            return new(Build(false), Build(true));
        }
    }
}
