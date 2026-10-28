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
public sealed class ClosureLowererParityTests
{
    private static readonly string[] s_duplicateCaptureNames = ["same", "same_1"];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NoLambda_PreservesBodyIdentityAndProgramState(bool generic)
    {
        Fixture f = new(generic: generic);
        Pair body = f.Block(f.Return(f.Literal(42)));
        Pair result = f.Lower(body);
        Assert.AreSame(body.Expected, result.Expected);
        Assert.AreSame(body.Actual, result.Actual);
        Assert.IsEmpty(f.Program.Types);
        Assert.IsEmpty(f.Program.Bodies);
        Assert.IsEmpty(f.Program.ExtraMembers);
        f.Compare(result);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void NonCapturingLambda_IsStaticAndPreservesOriginFlagsParametersAndSource(bool async, bool iterator)
    {
        Fixture f = new();
        Pair symbol = f.LambdaSymbol(async, iterator);
        Pair parameter = f.Create("Symbols.ParameterSymbol", "arg", f.Int, 0, symbol);
        Fixture.Call(symbol, "SetParameters", Fixture.Array("Symbols.ParameterSymbol", parameter));
        Pair lambda = f.Lambda(f.Block(f.Return(f.Node("BoundParameter", parameter))), symbol);
        Pair result = f.Lower(f.Block(f.Statement(lambda)));
        f.Compare(result);
        HB.BoundDelegateCreation creation = (HB.BoundDelegateCreation)((HB.BoundExpressionStatement)
            ((HB.BoundBlock)result.Actual).Statements[0]).Expression;
        Assert.IsNull(creation.Receiver);
        Assert.AreSame(f.Container.Actual, creation.Method.ContainingSymbol);
        Assert.IsTrue(creation.Method.IsStatic);
        Assert.AreEqual(HS.Accessibility.Private, creation.Method.DeclaredAccessibility);
        Assert.AreSame(parameter.Actual, creation.Method.Parameters[0]);
        Assert.AreSame(symbol.Actual, ((HSS.SynthesizedMethodSymbol)creation.Method).LambdaOrigin);
        Assert.AreSame(f.Method.Actual, ((HSS.SynthesizedMethodSymbol)creation.Method).SourceMethod);
        Assert.AreEqual(async, creation.Method.IsAsync);
        Assert.AreEqual(iterator, creation.Method.IsIterator);
        Assert.AreSame(f.File, f.Program.MethodFiles[creation.Method]);
        Assert.IsEmpty(f.Program.Types);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ThisOnlyLambda_UsesContainingInstanceWithoutDisplayClass(bool baseReference)
    {
        Fixture f = new();
        Pair lambda = f.Lambda(f.Block(f.Return(f.Node(baseReference ? "BoundBaseReference" : "BoundThis", f.Container))));
        Pair result = f.Lower(f.Block(f.Statement(lambda)));
        f.Compare(result);
        HB.BoundDelegateCreation creation = Fixture.Delegates(result).Single();
        Assert.IsInstanceOfType<HB.BoundThis>(creation.Receiver);
        Assert.AreSame(f.Container.Actual, creation.Receiver.Type);
        Assert.IsFalse(creation.Method.IsStatic);
        Assert.AreSame(f.Container.Actual, creation.Method.ContainingSymbol);
        Assert.IsEmpty(f.Program.Types);
    }

    [TestMethod]
    public void LocalAndParameterCaptures_AllocateChainedEnvironmentsAndRewriteReadsWrites()
    {
        Fixture f = new();
        Pair parameter = f.AddParameter("value");
        Pair local = f.Local("value");
        Pair uninitialized = f.Local("empty");
        Pair untouched = f.Local("untouched");
        Pair lambda = f.Lambda(f.Block(
            f.Statement(f.Node("BoundAssignment", f.Node("BoundLocal", local), f.Node("BoundParameter", parameter))),
            f.Statement(f.Node("BoundThis", f.Container)),
            f.Statement(f.Node("BoundLocal", uninitialized)),
            f.Return(f.Node("BoundLocal", local))));
        Pair result = f.Lower(f.Block([local, uninitialized, untouched],
            f.Node("BoundLocalDeclaration", local, f.Literal(1)),
            f.Node("BoundLocalDeclaration", uninitialized, null),
            f.Node("BoundLocalDeclaration", untouched, f.Literal(2)),
            f.Statement(lambda),
            f.Return(f.Node("BoundParameter", parameter))));
        f.Compare(result);
        Assert.HasCount(2, f.Program.Types);
        Assert.HasCount(1, f.Program.Types.SelectMany(t => t.GetMembers()).OfType<HS.FieldSymbol>().Where(x => x.Name == "<>4__this"));
        Assert.HasCount(1, f.Program.Types.SelectMany(t => t.GetMembers()).OfType<HS.FieldSymbol>().Where(x => x.Name == "<>4__parent"));
        Assert.HasCount(1, f.Program.Bodies);
        HB.BoundBlock inner = (HB.BoundBlock)((HB.BoundBlock)result.Actual).Statements[^1];
        CollectionAssert.Contains(inner.Locals.ToArray(), untouched.Actual);
        CollectionAssert.DoesNotContain(inner.Locals.ToArray(), local.Actual);
        CollectionAssert.DoesNotContain(inner.Locals.ToArray(), uninitialized.Actual);
        Assert.IsInstanceOfType<HB.BoundNoOp>(inner.Statements[3]);
        Assert.IsTrue(Fixture.Nodes(result).OfType<HB.BoundAssignment>().All(a => a.IsDiscarded));
    }

    [TestMethod]
    public void NestedLambdas_ChainEnvironmentsAcrossLambdaAndLexicalScopes()
    {
        Fixture f = new();
        Pair outer = f.Local("outer");
        Pair inner = f.Local("inner");
        Pair nested = f.Lambda(f.Block(
            f.Statement(f.Node("BoundLocal", outer)),
            f.Statement(f.Node("BoundThis", f.Container)),
            f.Return(f.Node("BoundLocal", inner))));
        Pair lambda = f.Lambda(f.Block([inner],
            f.Node("BoundLocalDeclaration", inner, f.Literal(2)), f.Return(nested)));
        Pair result = f.Lower(f.Block([outer],
            f.Node("BoundLocalDeclaration", outer, f.Literal(1)), f.Statement(lambda)));
        f.Compare(result);
        Assert.HasCount(2, f.Program.Types);
        Assert.HasCount(2, f.Program.Bodies);
        HS.FieldSymbol parent = f.Program.Types.SelectMany(t => t.GetMembers()).OfType<HS.FieldSymbol>()
            .Single(field => field.Name == "<>4__parent");
        Assert.AreSame(f.Program.Types[0], parent.Type);
        Assert.AreSame(f.Program.Types[1], parent.ContainingSymbol);
        Assert.IsTrue(f.Program.Bodies.Values.SelectMany(Fixture.Walk).OfType<HB.BoundFieldAccess>()
            .Any(access => ReferenceEquals(access.Field, parent) && access.Receiver is HB.BoundThis));
    }

    [TestMethod]
    public void NestedBlocks_CaptureOrderAndDuplicateNamesRemainStable()
    {
        Fixture f = new();
        Pair first = f.Local("same");
        Pair second = f.Local("same");
        Pair leaf = f.Lambda(f.Block(f.Statement(f.Node("BoundLocal", second)), f.Return(f.Node("BoundLocal", first))));
        Pair result = f.Lower(f.Block([first, second],
            f.Node("BoundLocalDeclaration", first, f.Literal(1)),
            f.Node("BoundLocalDeclaration", second, f.Literal(2)),
            f.Block(f.Statement(leaf))));
        f.Compare(result);
        CollectionAssert.AreEqual(s_duplicateCaptureNames,
            f.Program.Types.Single().GetMembers().OfType<HS.FieldSymbol>().Select(field => field.Name).ToArray());
    }

    [TestMethod]
    public void NestedLexicalScopes_LinkInnermostHostToOuterCapturedLocal()
    {
        Fixture f = new();
        Pair outer = f.Local("outer");
        Pair inner = f.Local("inner");
        Pair lambda = f.Lambda(f.Block(f.Statement(f.Node("BoundLocal", outer)), f.Return(f.Node("BoundLocal", inner))));
        Pair result = f.Lower(f.Block([outer],
            f.Node("BoundLocalDeclaration", outer, f.Literal(1)),
            f.Block([inner], f.Node("BoundLocalDeclaration", inner, f.Literal(2)), f.Statement(lambda))));
        f.Compare(result);
        Assert.HasCount(2, f.Program.Types);
        HB.BoundDelegateCreation creation = Fixture.Delegates(result).Single();
        Assert.AreSame(f.Program.Types[1], creation.Method.ContainingSymbol);
        HB.BoundFieldAccess outerAccess = f.Program.Bodies.Values.SelectMany(Fixture.Walk)
            .OfType<HB.BoundFieldAccess>().Single(access => access.Field.Name == "outer");
        HB.BoundFieldAccess parentAccess = (HB.BoundFieldAccess)outerAccess.Receiver!;
        Assert.AreSame(f.Program.Types[1], parentAccess.Field.ContainingSymbol);
        Assert.AreSame(f.Program.Types[0], parentAccess.Type);
    }

    [TestMethod]
    public void ForAndSequenceCaptures_KeepLabelsAndAllocateAtDeclaringScope()
    {
        Fixture f = new();
        Pair loopLocal = f.Local("index");
        Pair sequenceLocal = f.Local("sequence");
        Pair br = f.Create("Symbols.LabelSymbol", "break");
        Pair cont = f.Create("Symbols.LabelSymbol", "continue");
        Pair loop = f.Node("BoundFor", Fixture.Array("Symbols.LocalSymbol", loopLocal),
            f.Node("BoundLocalDeclaration", loopLocal, f.Literal(0)), null, f.Node("BoundGoto", cont),
            f.Block(f.Statement(f.Lambda(f.Block(f.Return(f.Node("BoundLocal", loopLocal))))),
                f.Node("BoundGoto", br)), br, cont);
        Pair sequence = f.Node("BoundSequence", Fixture.Array("Symbols.LocalSymbol", sequenceLocal),
            Fixture.Array("Bound.BoundStatement", f.Node("BoundLocalDeclaration", sequenceLocal, f.Literal(3))),
            f.Lambda(f.Block(f.Return(f.Node("BoundLocal", sequenceLocal)))));
        Pair result = f.Lower(f.Block(loop, f.Statement(sequence)));
        f.Compare(result);
        Assert.HasCount(2, f.Program.Types);
        HB.BoundFor rewritten = Fixture.Nodes(result).OfType<HB.BoundFor>().Single();
        Assert.IsEmpty(rewritten.Locals);
        Assert.AreSame(br.Actual, rewritten.BreakLabel);
        Assert.AreSame(cont.Actual, rewritten.ContinueLabel);
        HB.BoundSequence rewrittenSequence = Fixture.Nodes(result).OfType<HB.BoundSequence>().Single();
        Assert.IsTrue(rewrittenSequence.Locals.Single().IsSynthesized);
        Assert.IsInstanceOfType<HB.BoundAssignment>(((HB.BoundExpressionStatement)rewrittenSequence.SideEffects[0]).Expression);
    }

    [TestMethod]
    public void CatchCapture_CopiesExceptionToFieldThroughFreshSynthesizedCatchLocal()
    {
        Fixture f = new();
        Pair caught = f.Local("caught", f.Exception);
        Pair handler = f.Create("Bound.BoundCatch", caught, f.Exception,
            f.Block(f.Statement(f.Lambda(f.Block(f.Return(f.Node("BoundLocal", caught)))))));
        Pair result = f.Lower(f.Block(f.Node("BoundTry", f.Block(f.Node("BoundNoOp")),
            Fixture.Array("Bound.BoundCatch", handler), f.Block(f.Node("BoundNoOp")))));
        f.Compare(result);
        HB.BoundCatch rewritten = Fixture.Nodes(result).OfType<HB.BoundTry>().Single().Catches.Single();
        Assert.AreNotSame(caught.Actual, rewritten.Local);
        Assert.IsTrue(rewritten.Local!.IsSynthesized);
        HB.BoundAssignment copy = (HB.BoundAssignment)((HB.BoundExpressionStatement)rewritten.Body.Statements[1]).Expression;
        Assert.AreSame(rewritten.Local, ((HB.BoundLocal)copy.Value).Local);
        Assert.AreSame(f.Program.Types.Single().GetMembers().OfType<HS.FieldSymbol>().Single(),
            ((HB.BoundFieldAccess)copy.Target).Field);
    }

    [TestMethod]
    public void RecursiveAndRepeatedLambda_ReuseOneSynthesizedMethodAndCapturedDelegateField()
    {
        Fixture f = new();
        Pair local = f.Local("recursive", f.Delegate);
        Pair lambda = f.Lambda(f.Block(f.Return(f.Node("BoundLocal", local))));
        Pair result = f.Lower(f.Block([local],
            f.Node("BoundLocalDeclaration", local, lambda), f.Statement(lambda)));
        f.Compare(result);
        Assert.HasCount(1, f.Program.Types);
        Assert.HasCount(1, f.Program.Bodies);
        HB.BoundDelegateCreation[] creations = Fixture.Delegates(result).ToArray();
        Assert.HasCount(2, creations);
        Assert.AreSame(creations[0].Method, creations[1].Method);
        Assert.AreSame(f.Delegate.Actual, f.Program.Types.Single().GetMembers().OfType<HS.FieldSymbol>().Single().Type);
    }

    [TestMethod]
    public void GenericCapture_MatchesOracleFailureAfterClosedFieldMembersAreCached()
    {
        Fixture f = new(generic: true);
        Pair local = f.Local("generic", f.MethodParameter!);
        Pair body = f.Block([local], f.Node("BoundLocalDeclaration", local, null),
            f.Statement(f.Lambda(f.Block(f.Return(f.Node("BoundLocal", local))))));
        // The oracle caches the closed fields before adding the lambda method, so method lookup fails.
        f.CompareFailure(body);
        Assert.HasCount(2, f.Program.Types);
        foreach (HSS.SynthesizedNamedType type in f.Program.Types.Cast<HSS.SynthesizedNamedType>())
            Assert.AreEqual(1, type.MethodTypeParameterMap![(HS.TypeParameterSymbol)f.MethodParameter!.Actual]);
    }

    [TestMethod]
    public void GenericMethod_MapsOriginalParametersOntoClosureAndClosesMemberReferences()
    {
        Fixture f = new(generic: true);
        Pair body = f.Block(f.Statement(f.Lambda(f.Block(f.Return(f.Literal(1))))));
        Pair result = f.Lower(body);
        f.Compare(result);
        Assert.HasCount(1, f.Program.Types);
        foreach (HSS.SynthesizedNamedType type in f.Program.Types.Cast<HSS.SynthesizedNamedType>())
        {
            Assert.AreEqual(2, type.Arity);
            Assert.AreEqual(1, type.OwnArity);
            Assert.AreEqual(1, type.MethodTypeParameterMap![(HS.TypeParameterSymbol)f.MethodParameter!.Actual]);
            Assert.AreSame(type, type.TypeParameters[0].ContainingSymbol);
            Assert.AreSame(type, type.TypeParameters[1].ContainingSymbol);
        }
        HB.BoundDelegateCreation creation = Fixture.Delegates(result).Single();
        Assert.IsFalse(creation.Method.IsStatic);
        Assert.AreNotSame(f.Program.Bodies.Keys.Single(), creation.Method);
        Assert.AreSame(f.MethodParameter!.Actual, creation.Method.ContainingType!.TypeArguments[1]);
        Assert.AreSame(f.File, f.Program.MethodFiles[f.Program.Bodies.Keys.Single()]);
        Assert.IsEmpty(f.Program.ClassTypeParameterMaps);
    }

    [TestMethod]
    public void MissingSourceAssociation_DoesNotInventMethodFileEntries()
    {
        Fixture f = new(source: false);
        Pair body = f.Block(f.Statement(f.Lambda(f.Block(f.Return(f.Literal(1))))));
        f.Compare(f.Lower(body));
        Assert.IsEmpty(f.Program.MethodFiles);
    }

    [TestMethod]
    public void SelfHostLibrary_HasNoBootstrapDependencyAndPassesWholeLibraryILVerification()
    {
        Assembly assembly = typeof(HE.LoweredProgram<>).Assembly;
        Assert.IsFalse(assembly.GetReferencedAssemblies().Any(a => a.Name == "Caladesi.Compiler"));
        Assert.IsEmpty(ILVerifier.Verify(assembly.GetName().Name!, File.ReadAllBytes(assembly.Location)));
    }

    private sealed record Pair(object Expected, object Actual);

    private sealed class Fixture
    {
        private readonly Compilation compilation = TestCompiler.Create("", OutputKind.DynamicallyLinkedLibrary);
        private static readonly Lazy<SelfHost::Caladesi.Compiler.Symbols.Metadata.ReferenceSet> s_references =
            new(() => SelfHost::Caladesi.Compiler.Symbols.Metadata.ReferenceSet.Create(TestReferences.Paths));
        private readonly Dictionary<object, object> identities = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, object> reverse = new(ReferenceEqualityComparer.Instance);
        private readonly object expectedProgram = Activator.CreateInstance(
            typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.LoweredProgram", true)!, nonPublic: true)!;
        private readonly Pair syntax = new(Caladesi.Compiler.Syntax.SyntaxTree.ParseText("const f = () => 1;").Root,
            SelfHost::Caladesi.Compiler.Syntax.SyntaxTree.ParseText("const f = () => 1;").Root);
        public HE.LoweredProgram<SourceFile> Program { get; } = new();
        public Pair Container { get; }
        public Pair Method { get; }
        public Pair Int { get; }
        public Pair Exception { get; }
        public Pair Delegate { get; }
        public Pair? MethodParameter { get; }
        public SourceFile File { get; }
        private readonly Pair objectType;
        private readonly Pair voidType;

        public Fixture(bool generic = false, bool source = true)
        {
            Remember(syntax);
            objectType = Type("System.Object");
            voidType = Type("System.Void");
            Int = Type("System.Int32");
            Exception = Type("System.Exception");
            Pair delegateDefinition = Type("System.Func`1");
            Delegate = Remember(new(
                ((NamedTypeSymbol)delegateDefinition.Expected).Construct(new TypeSymbol[] { (TypeSymbol)Int.Expected }),
                ((HS.NamedTypeSymbol)delegateDefinition.Actual).Construct(new HS.TypeSymbol[] { (HS.TypeSymbol)Int.Actual })));
            Container = Create("Symbols.Source.SynthesizedNamedType", "Container", null, 0);
            Method = Create("Bound.LambdaSymbol", Container, "Run", false, false);
            Set(Method, "ReturnTypeValue", Int);
            if (generic)
            {
                Pair classParameter = Create("Symbols.TypeParameterSymbol", "T", 0, Container, 0);
                Call(Container, "SetTypeParameters", Array("Symbols.TypeParameterSymbol", classParameter));
                Pair original = Create("Symbols.Source.SynthesizedMethodSymbol", Container, "Run", 0, 0, false, Int,
                    Array("Symbols.ParameterSymbol"));
                Method = original;
                MethodParameter = Create("Symbols.TypeParameterSymbol", "U", 0, Method, 0);
                Call(Method, "SetTypeParameters", Array("Symbols.TypeParameterSymbol", MethodParameter));
            }
            File = new SourceFile(compilation, Caladesi.Compiler.Syntax.SyntaxTree.ParseText("", "closure.cala"));
            if (source)
            {
                ((IDictionary)Get(expectedProgram, "MethodFiles")!).Add(Method.Expected, File);
                Program.MethodFiles.Add((HS.MethodSymbol)Method.Actual, File);
            }
        }

        private Pair Type(string name) => Remember(new(compilation.GetWellKnownType(name)!, s_references.Value.FindType(name)!));
        private Pair Remember(Pair pair)
        {
            identities.Add(pair.Expected, pair.Actual);
            reverse.Add(pair.Actual, pair.Expected);
            return pair;
        }
        public Pair Local(string name, Pair? type = null) => Create("Symbols.LocalSymbol", name, type ?? Int, false, Method, null);
        public Pair AddParameter(string name)
        {
            Pair parameter = Create("Symbols.ParameterSymbol", name, Int, 0, Method);
            Call(Method, "SetParameters", Array("Symbols.ParameterSymbol", parameter));
            return parameter;
        }
        public Pair Literal(int value) => Node("BoundLiteral", Create("Symbols.ConstantValue", value), Int);
        public Pair Return(Pair expression) => Node("BoundReturn", expression);
        public Pair Statement(Pair expression) => Node("BoundExpressionStatement", expression);
        public Pair Block(params Pair[] statements) => Block([], statements);
        public Pair Block(Pair[] locals, params Pair[] statements) =>
            Node("BoundBlock", Array("Symbols.LocalSymbol", locals), Array("Bound.BoundStatement", statements));
        public Pair LambdaSymbol(bool async = false, bool iterator = false)
        {
            Pair symbol = Create("Bound.LambdaSymbol", Method, "lambda", async, iterator);
            Set(symbol, "ReturnTypeValue", Int);
            return symbol;
        }
        public Pair Lambda(Pair body, Pair? symbol = null) => Node("BoundLambda", symbol ?? LambdaSymbol(), body, Delegate);
        public Pair Node(string name, params object?[] arguments) => Set(Create("Bound." + name, arguments), "Syntax", syntax);

        public Pair Lower(Pair body)
        {
            return new(LowerOracle(body), LowerActual(body));
        }

        private object LowerOracle(Pair body)
        {
            MethodInfo oracle = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.ClosureLowerer", true)!.GetMethod("Lower")!;
            return oracle.Invoke(null, [compilation, expectedProgram, Method.Expected, Container.Expected, 7, body.Expected])!;
        }

        private object LowerActual(Pair body)
        {
            MethodInfo actual = typeof(HE.LoweredProgram<>).Assembly.GetType("Caladesi.Compiler.Emit.ClosureLowerer`1", true)!
                .MakeGenericType(typeof(SourceFile)).GetMethod("Lower")!;
            return actual.Invoke(null, [objectType.Actual, voidType.Actual, Program,
                Method.Actual, Container.Actual, 7, body.Actual])!;
        }

        public void CompareFailure(Pair body)
        {
            Exception? expected = Assert.Throws<TargetInvocationException>(() => LowerOracle(body)).InnerException;
            Exception? actual = Assert.Throws<TargetInvocationException>(() => LowerActual(body)).InnerException;
            Assert.IsInstanceOfType<InvalidOperationException>(expected);
            Assert.IsInstanceOfType<InvalidOperationException>(actual);
            CompareProgram();
        }

        public void Compare(Pair result)
        {
            Equivalent(result.Expected, result.Actual);
            CompareProgram();
        }

        private void CompareProgram()
        {
            foreach (string name in new[] { "Types", "Bodies", "ExtraMembers", "ClassTypeParameterMaps", "MethodFiles", "EntryPoint" })
                Equivalent(Get(expectedProgram, name), Get(Program, name));
            foreach (HS.MethodSymbol method in Program.Bodies.Keys)
            {
                Assert.AreEqual(HS.MethodKind.LambdaMethod, method.MethodKind);
                Assert.IsTrue(Program.MembersOf(method.ContainingType!).Any(member => ReferenceEquals(member, method)));
            }
        }

        public static IEnumerable<HB.BoundNode> Nodes(Pair tree) => Walk((HB.BoundNode)tree.Actual);
        public static IEnumerable<HB.BoundDelegateCreation> Delegates(Pair tree) => Nodes(tree).OfType<HB.BoundDelegateCreation>();
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

        private void Equivalent(object? expected, object? actual)
        {
            if (expected == null) { Assert.IsNull(actual); return; }
            Assert.IsNotNull(actual);
            if (ReferenceEquals(expected, File)) { Assert.AreSame(File, actual); return; }
            if (identities.TryGetValue(expected, out object? known)) { Assert.AreSame(known, actual); return; }
            if (expected is Enum) { Assert.AreEqual(Convert.ToInt32(expected), Convert.ToInt32(actual)); return; }
            if (expected is string || expected.GetType().IsPrimitive) { Assert.AreEqual(expected, actual); return; }
            if (expected is IDictionary dictionary)
            {
                IDictionary other = (IDictionary)actual;
                Assert.HasCount(dictionary.Count, other);
                foreach (DictionaryEntry entry in dictionary)
                {
                    Assert.IsTrue(identities.TryGetValue(entry.Key, out object? key), "Unmapped dictionary key: " + entry.Key);
                    CollectionAssert.Contains(other.Keys, key);
                    Equivalent(entry.Value, other[key!]);
                }
                return;
            }
            if (expected is IEnumerable sequence)
            {
                object[] left = sequence.Cast<object>().ToArray(), right = ((IEnumerable)actual).Cast<object>().ToArray();
                Assert.HasCount(left.Length, right);
                for (int i = 0; i < left.Length; i++) Equivalent(left[i], right[i]);
                return;
            }
            Assert.AreEqual(expected.GetType().Name, actual.GetType().Name);
            Assert.IsFalse(reverse.ContainsKey(actual), "Distinct oracle objects must not collapse to one self-host object.");
            identities.Add(expected, actual);
            reverse.Add(actual, expected);
            if (expected is Symbol)
            {
                string[] properties = expected switch
                {
                    NamedTypeSymbol => ["Name", "MetadataName", "ContainingSymbol", "DeclaredAccessibility", "TypeKind",
                        "IsSealed", "IsStatic", "IsAbstract", "BaseType", "TypeParameters", "TypeArguments", "OwnArity", "OriginalDefinition"],
                    MethodSymbol => ["Name", "ContainingSymbol", "DeclaredAccessibility", "MethodKind", "IsStatic",
                        "IsAsync", "IsIterator", "IsVirtual", "IsAbstract", "IsOverride", "IsSealed", "ReturnType", "Parameters", "TypeParameters"],
                    FieldSymbol => ["Name", "ContainingSymbol", "DeclaredAccessibility", "IsStatic", "IsReadOnly", "Type"],
                    LocalSymbol => ["Name", "ContainingSymbol", "Type", "IsSynthesized", "IsConst"],
                    TypeParameterSymbol => ["Name", "ContainingSymbol", "Ordinal", "Variance", "HasReferenceTypeConstraint",
                        "HasValueTypeConstraint", "HasConstructorConstraint", "HasNotNullConstraint", "ConstraintTypes"],
                    _ => ["Name", "ContainingSymbol"]
                };
                foreach (string name in properties) Equivalent(Get(expected, name), Get(actual, name));
                if (expected is SynthesizedNamedType type)
                {
                    Equivalent(type.OwnArityValue, ((HSS.SynthesizedNamedType)actual).OwnArityValue);
                    Equivalent(type.IsCompilerGenerated, ((HSS.SynthesizedNamedType)actual).IsCompilerGenerated);
                    Equivalent(type.MethodTypeParameterMap, ((HSS.SynthesizedNamedType)actual).MethodTypeParameterMap);
                    Equivalent(type.GetMembers(), ((HS.NamedTypeSymbol)actual).GetMembers());
                }
                if (expected is SynthesizedMethodSymbol)
                    foreach (string name in new[] { "SourceMethod", "LambdaOrigin", "SynthesizedKind" })
                        Equivalent(Get(expected, name), Get(actual, name));
                return;
            }
            foreach (PropertyInfo property in expected.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                Equivalent(property.GetValue(expected), Get(actual, property.Name));
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
                    .Where(c => c.GetParameters().Length >= arguments.Length && c.GetParameters().Skip(arguments.Length).All(p => p.HasDefaultValue))
                    .OrderBy(c => c.GetParameters().Length).First();
                object?[] values = ctor.GetParameters().Select((p, i) =>
                {
                    object? value = i < arguments.Length ? arguments[i] : p.DefaultValue;
                    if (value is Pair pair) value = self ? pair.Actual : pair.Expected;
                    if (value != null && p.ParameterType.IsEnum) value = Enum.ToObject(p.ParameterType, value);
                    return value;
                }).ToArray();
                return ctor.Invoke(values);
            }
            return Remember(new(Build(false), Build(true)));
        }

        public static void Call(Pair target, string name, Pair argument)
        {
            target.Expected.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(target.Expected, [argument.Expected]);
            target.Actual.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(target.Actual, [argument.Actual]);
        }
        private static Pair Set(Pair target, string name, Pair value)
        {
            target.Expected.GetType().GetProperty(name)!.SetValue(target.Expected, value.Expected);
            target.Actual.GetType().GetProperty(name)!.SetValue(target.Actual, value.Actual);
            return target;
        }
        public static Pair Array(string name, params Pair[] items)
        {
            object Build(bool self)
            {
                Assembly assembly = self ? typeof(HB.BoundNode).Assembly : typeof(BoundNode).Assembly;
                Type element = assembly.GetType("Caladesi.Compiler." + name, true)!;
                System.Array values = System.Array.CreateInstance(element, items.Length);
                for (int i = 0; i < items.Length; i++) values.SetValue(self ? items[i].Actual : items[i].Expected, i);
                MethodInfo create = typeof(ImmutableArray).GetMethods().Single(m => m.Name == "Create" &&
                    m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsArray);
                return create.MakeGenericMethod(element).Invoke(null, [values])!;
            }
            return new(Build(false), Build(true));
        }
    }
}
