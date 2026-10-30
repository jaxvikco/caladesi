extern alias SelfHost;

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Tests.Emit;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using HL = SelfHost::Caladesi.Compiler.Emit.Lowering;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class TryRewriterParityTests
{
    [TestMethod]
    public void Finally_NoSuspensionPreservesIdentityAndSyntax()
    {
        Fixture f = new();
        var tree = f.Try(f.Block(f.Node("BoundReturn", f.Literal(42))), f.Block(f.Node("BoundNoOp")));
        var result = f.Rewrite(tree);
        Assert.AreSame(tree.Expected, result.Expected);
        Assert.AreSame(tree.Actual, result.Actual);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Finally_ReplaysExternalBranchesReturnsAndYieldBreakAfterDisposal(bool disposing)
    {
        Fixture f = new();
        var inside = f.Label("inside");
        var outside = f.Label("outside");
        var tree = f.Try(f.Block(
            f.Node("BoundYieldReturn", f.Literal(1)),
            f.Node("BoundLabel", inside),
            f.Node("BoundGoto", inside),
            f.Node("BoundConditionalGoto", f.Boolean(), outside, false),
            f.Node("BoundConditionalGoto", f.Boolean(), outside, true),
            f.Node("BoundGoto", outside),
            f.Node("BoundReturn", f.Literal(42)),
            f.Node("BoundReturn", (object?)null),
            f.Node("BoundYieldBreak")), f.Block(f.Node("BoundNoOp")));
        var result = f.Rewrite(tree, disposing);
        f.Compare(result);
        var block = (HB.BoundBlock)result.Actual;
        Assert.HasCount(disposing ? 13 : 12, block.Statements);
        var handler = (HB.BoundTry)block.Statements[2];
        var yield = (HB.BoundYieldReturn)handler.Body.Statements[0];
        Assert.AreSame(((HB.BoundLabel)block.Statements[3]).Label, yield.DisposeTarget);
        _ = Assert.IsInstanceOfType<HB.BoundGoto>(handler.Body.Statements[2]);
        Assert.AreEqual("inside", ((HB.BoundGoto)handler.Body.Statements[2]).Label.Name);
        Assert.IsTrue(((HB.BoundAssignment)((HB.BoundExpressionStatement)block.Statements[0]).Expression).IsDiscarded);
        Assert.AreEqual("Throw", ((HB.BoundCall)((HB.BoundExpressionStatement)
            ((HB.BoundBlock)((HB.BoundIf)block.Statements[5]).Consequence).Statements[0]).Expression).Method.Name);
        if (disposing)
        {
            Assert.AreSame(f.End.Actual, ((HB.BoundGoto)((HB.BoundBlock)((HB.BoundIf)block.Statements[6]).Consequence).Statements[0]).Label);
        }
    }

    [TestMethod]
    public void Finally_NestedLoopsKeepInternalTargetsAndChainDisposalThroughOuterFinally()
    {
        Fixture f = new();
        var br = f.Label("break");
        var cont = f.Label("continue");
        var loopBody = f.Block(f.Node("BoundYieldReturn", f.Literal(7)), f.Node("BoundGoto", cont), f.Node("BoundGoto", br));
        var inner = f.Try(f.Block(
            f.Node("BoundWhile", f.Boolean(), loopBody, br, cont),
            f.Node("BoundDoWhile", f.Boolean(), loopBody, br, cont),
            f.Node("BoundFor", Fixture.Array("Symbols.LocalSymbol"), null, null, null, loopBody, br, cont),
            f.Node("BoundReturn", f.Literal(9))), f.Block(f.Node("BoundYieldReturn", f.Literal(8))));
        var result = f.Rewrite(f.Try(f.Block(inner), f.Block(f.Node("BoundNoOp"))), true);
        f.Compare(result);
        var outer = (HB.BoundBlock)result.Actual;
        var nested = (HB.BoundBlock)((HB.BoundTry)outer.Statements[2]).Body.Statements[0];
        var nestedHandler = (HB.BoundTry)nested.Statements[2];
        var loop = (HB.BoundWhile)nestedHandler.Body.Statements[0];
        Assert.AreSame(br.Actual, ((HB.BoundGoto)((HB.BoundBlock)loop.Body).Statements[2]).Label);
        Assert.AreSame(((HB.BoundLabel)nested.Statements[3]).Label,
            ((HB.BoundYieldReturn)((HB.BoundBlock)loop.Body).Statements[0]).DisposeTarget);
        Assert.AreSame(((HB.BoundLabel)outer.Statements[3]).Label,
            ((HB.BoundYieldReturn)((HB.BoundBlock)nested.Statements[4]).Statements[0]).DisposeTarget);
    }

    [TestMethod]
    public void Catches_SuspendedHandlersPreserveOrderingConversionsLocalsAndNestedRethrows()
    {
        Fixture f = new();
        var local = f.Local("caught", f.DerivedException);
        var nested = f.Try(f.Block(f.Node("BoundThrow", (object?)null)),
            f.Block(f.Node("BoundThrow", (object?)null)),
            f.Catch(null, f.Exception, f.Block(f.Node("BoundThrow", (object?)null))));
        var tree = f.Try(f.Block(f.Node("BoundNoOp")), null,
            f.Catch(local, f.DerivedException, f.Block(f.Node("BoundYieldReturn", f.Literal(1)),
                f.Node("BoundThrow", (object?)null), nested)),
            f.Catch(null, f.Exception, f.Block(f.Node("BoundThrow", f.Node("BoundLocal", local)))));
        var result = f.Rewrite(tree);
        f.Compare(result);
        var block = (HB.BoundBlock)result.Actual;
        Assert.HasCount(3, block.Statements);
        Assert.HasCount(2, ((HB.BoundTry)block.Statements[1]).Catches);
        var first = (HB.BoundBlock)((HB.BoundIf)block.Statements[2]).Consequence;
        Assert.AreSame(local.Actual, ((HB.BoundLocalDeclaration)first.Statements[0]).Local);
        Assert.AreEqual(HB.ConversionKind.ExplicitReference,
            ((HB.BoundConversion)((HB.BoundLocalDeclaration)first.Statements[0]).Initializer!).Kind);
        var replay = (HB.BoundBlock)first.Statements[1];
        _ = Assert.IsInstanceOfType<HB.BoundExpressionStatement>(replay.Statements[1]);
        var preserved = (HB.BoundTry)replay.Statements[2];
        _ = Assert.IsInstanceOfType<HB.BoundThrow>(preserved.Catches[0].Body.Statements[0]);
        _ = Assert.IsInstanceOfType<HB.BoundExpressionStatement>(preserved.Body.Statements[0]);
        _ = Assert.IsInstanceOfType<HB.BoundExpressionStatement>(preserved.Finally!.Statements[0]);
    }

    [TestMethod]
    public void CombinedCatchFinallyAndBodyOnlySuspensionMatchWithVoidOrAbsentReturnType()
    {
        foreach (var voidReturn in new[] { false, true })
        {
            Fixture f = new();
            var tree = f.Try(f.Block(f.Node("BoundYieldReturn", f.Literal(1)), f.Node("BoundReturn", (object?)null)),
                f.Block(f.Node("BoundNoOp")),
                f.Catch(null, f.Exception, f.Block(f.Node("BoundNoOp"))));
            f.Compare(f.Rewrite(tree, true, voidReturn ? f.Void : null, false));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Await_InProtectedBodyOrHandlerTriggersTheSameRewrite(bool inHandler)
    {
        Fixture f = new();
        var awaitStatement = f.Node("BoundExpressionStatement", f.Await());
        var tree = inHandler
            ? f.Try(f.Block(f.Node("BoundNoOp")), null,
                f.Catch(null, f.Exception, f.Block(awaitStatement, f.Node("BoundThrow", (object?)null))))
            : f.Try(f.Block(awaitStatement), f.Block(f.Node("BoundNoOp")));
        var result = f.Rewrite(tree);
        _ = Assert.IsInstanceOfType<HB.BoundBlock>(result.Actual);
        f.Compare(result);
    }

    [TestMethod]
    public void SelfHostLibrary_HasNoBootstrapDependencyAndPassesWholeLibraryILVerification()
    {
        var assembly = typeof(HL.TryRewriter).Assembly;
        Assert.IsFalse(assembly.GetReferencedAssemblies().Any(a => a.Name == "Caladesi.Compiler"));
        Assert.IsEmpty(ILVerifier.Verify(assembly.GetName().Name!, File.ReadAllBytes(assembly.Location)));
    }

    private sealed record Pair(object Expected, object Actual);

    private sealed class Fixture
    {
        private readonly Compilation compilation = TestCompiler.Create("", OutputKind.DynamicallyLinkedLibrary);
        private static readonly Lazy<HS.Metadata.ReferenceSet> s_references =
            new(() => HS.Metadata.ReferenceSet.Create(TestReferences.Paths));
        private readonly Dictionary<object, object> identities = new(ReferenceEqualityComparer.Instance);
        private readonly Pair syntax = new(Compiler.Syntax.SyntaxTree.ParseText("yield return 1;").Root,
            SelfHost::Caladesi.Compiler.Syntax.SyntaxTree.ParseText("yield return 1;").Root);
        public readonly Pair Int;
        public readonly Pair Bool;
        public readonly Pair Void;
        public readonly Pair Exception;
        public readonly Pair DerivedException;
        public readonly Pair End;

        public Fixture()
        {
            Int = Type("System.Int32");
            Bool = Type("System.Boolean");
            Void = Type("System.Void");
            Exception = Type("System.Exception");
            DerivedException = Type("System.InvalidOperationException");
            End = Label("end");
            identities.Add(syntax.Expected, syntax.Actual);
        }

        private Pair Type(string name)
        {
            return Remember(new(compilation.GetWellKnownType(name)!, s_references.Value.FindType(name)!));
        }

        private Pair Remember(Pair pair) { identities.Add(pair.Expected, pair.Actual); return pair; }
        public Pair Label(string name)
        {
            return Create("Symbols.LabelSymbol", name);
        }

        public Pair Local(string name, Pair type)
        {
            return Create("Symbols.LocalSymbol", name, type, false, null, null);
        }

        public Pair Literal(int value)
        {
            return Node("BoundLiteral", Create("Symbols.ConstantValue", value), Int);
        }

        public Pair Boolean()
        {
            return Node("BoundLiteral", Create("Symbols.ConstantValue", true), Bool);
        }

        public Pair Await()
        {
            var method = Create("Bound.LambdaSymbol", Int, "awaiter", false, false);
            var info = Create("Bound.AwaitInfo", method, null, method, method, Int);
            return Node("BoundAwait", Literal(1), info, Int);
        }
        public Pair Block(params Pair[] statements)
        {
            return Node("BoundBlock", Array("Symbols.LocalSymbol"), Array("Bound.BoundStatement", statements));
        }

        public Pair Catch(Pair? local, Pair type, Pair body)
        {
            return Create("Bound.BoundCatch", local, type, body);
        }

        public Pair Try(Pair body, Pair? finallyBody, params Pair[] catches)
        {
            return Node("BoundTry", body, Array("Bound.BoundCatch", catches), finallyBody);
        }

        public Pair Node(string name, params object?[] arguments)
        {
            var pair = Create("Bound." + name, arguments);
            pair.Expected.GetType().GetProperty("Syntax")!.SetValue(pair.Expected, syntax.Expected);
            pair.Actual.GetType().GetProperty("Syntax")!.SetValue(pair.Actual, syntax.Actual);
            return pair;
        }

        public Pair Rewrite(Pair tree, bool disposing = false, Pair? returnType = null, bool defaultReturn = true)
        {
            int leftCount = 0, rightCount = 0;
            List<LocalSymbol> leftTemps = [];
            List<HS.LocalSymbol> rightTemps = [];
            Func<TypeSymbol, LocalSymbol> leftTemp = type =>
            {
                LocalSymbol local = new("temp" + leftCount++, type, false, null);
                leftTemps.Add(local);
                return local;
            };
            HS.LocalSymbol rightTemp(HS.TypeSymbol type)
            {
                HS.LocalSymbol local = new("temp" + rightCount++, type, false, null, null);
                rightTemps.Add(local);
                return local;
            }
            var rt = returnType ?? (defaultReturn ? Int : null);
            var finderType = typeof(BoundNode).Assembly.GetType("Caladesi.Compiler.Emit.Lowering.SuspensionFinder", true)!;
            var rewriterType = typeof(BoundNode).Assembly.GetType("Caladesi.Compiler.Emit.Lowering.TryRewriter", true)!;
            var expected = (BoundTreeRewriter)Activator.CreateInstance(rewriterType,
                compilation, Activator.CreateInstance(finderType), leftTemp, rt?.Expected,
                disposing ? (Func<BoundExpression>)(() => new BoundLiteral(new ConstantValue(true), (TypeSymbol)Bool.Expected)) : null, End.Expected)!;
            HL.TryRewriter actual = new((HS.TypeSymbol)Bool.Actual, (HS.TypeSymbol)Int.Actual,
                (HS.NamedTypeSymbol)Exception.Actual, s_references.Value.FindType("System.Runtime.ExceptionServices.ExceptionDispatchInfo")!,
                new HL.SuspensionFinder(), rightTemp, (HS.TypeSymbol?)rt?.Actual,
                disposing ? () => new HB.BoundLiteral(new HS.ConstantValue(true), (HS.TypeSymbol)Bool.Actual) : null,
                (HS.LabelSymbol)End.Actual);
            Pair result = new(expected.Visit((BoundNode)tree.Expected)!, actual.Visit((HB.BoundNode)tree.Actual)!);
            Assert.AreEqual(leftCount, rightCount);
            for (var i = 0; i < leftTemps.Count; i++)
            {
                Assert.AreEqual(leftTemps[i].Type.ToDisplayString(), rightTemps[i].Type.ToDisplayString());
                identities.Add(leftTemps[i], rightTemps[i]);
            }
            return result;
        }

        public void Compare(Pair pair)
        {
            Compare(pair.Expected, pair.Actual);
        }

        private void Compare(object? expected, object? actual)
        {
            if (expected == null) { Assert.IsNull(actual); return; }
            Assert.IsNotNull(actual);
            if (identities.TryGetValue(expected, out var known)) { Assert.AreSame(known, actual); return; }
            Assert.AreEqual(expected.GetType().Name, actual.GetType().Name);
            if (expected is Symbol symbol)
            {
                var other = (HS.Symbol)actual;
                Assert.AreEqual(symbol.Name, other.Name);
                if (symbol is MethodSymbol method)
                {
                    Assert.HasCount(method.Parameters.Length, ((HS.MethodSymbol)other).Parameters);
                    Assert.AreEqual(method.ContainingType!.ToMetadataString(), other.ContainingType!.ToMetadataString());
                }
                identities.Add(expected, actual);
                return;
            }
            if (expected is Enum) { Assert.AreEqual(Convert.ToInt32(expected), Convert.ToInt32(actual)); return; }
            if (expected is string || expected.GetType().IsPrimitive) { Assert.AreEqual(expected, actual); return; }
            if (expected is IEnumerable sequence)
            {
                object[] left = [.. sequence.Cast<object>()];
                object[] right = [.. ((IEnumerable)actual).Cast<object>()];
                Assert.HasCount(left.Length, right);
                for (var i = 0; i < left.Length; i++)
                {
                    Compare(left[i], right[i]);
                }

                return;
            }
            foreach (var property in expected.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Compare(property.GetValue(expected), actual.GetType().GetProperty(property.Name)!.GetValue(actual));
            }
        }

        private Pair Create(string name, params object?[] arguments)
        {
            object Build(bool self)
            {
                var assembly = self ? typeof(HB.BoundNode).Assembly : typeof(BoundNode).Assembly;
                var type = assembly.GetType("Caladesi.Compiler." + name, true)!;
                var ctor = type.GetConstructors()
                    .Where(c => c.GetParameters().Length >= arguments.Length && c.GetParameters().Skip(arguments.Length).All(p => p.HasDefaultValue))
                    .OrderBy(c => c.GetParameters().Length).First();
                object?[] values = [.. ctor.GetParameters().Select((p, i) =>
                {
                    var value = i < arguments.Length ? arguments[i] : p.DefaultValue;
                    return value is Pair pair ? self ? pair.Actual : pair.Expected : value;
                })];
                return ctor.Invoke(values);
            }
            return Remember(new(Build(false), Build(true)));
        }

        public static Pair Array(string name, params Pair[] items)
        {
            object Build(bool self)
            {
                var assembly = self ? typeof(HB.BoundNode).Assembly : typeof(BoundNode).Assembly;
                var element = assembly.GetType("Caladesi.Compiler." + name, true)!;
                var values = System.Array.CreateInstance(element, items.Length);
                for (var i = 0; i < items.Length; i++)
                {
                    values.SetValue(self ? items[i].Actual : items[i].Expected, i);
                }

                var create = typeof(ImmutableArray).GetMethods().Single(m => m.Name == "Create" &&
                    m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsArray);
                return create.MakeGenericMethod(element).Invoke(null, [values])!;
            }
            return new(Build(false), Build(true));
        }
    }
}
