extern alias SelfHost;

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using SelfBound = SelfHost::Caladesi.Compiler.Bound;
using SelfSymbols = SelfHost::Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class BoundTreeRewriterParityTests
{
    [TestMethod]
    public void Rewriter_AllDispatchCasesReuseUnchangedNodes()
    {
        Fixture fixture = new();
        BootstrapRewriter expected = new();
        SelfRewriter actual = new();
        Assert.IsNull(actual.Visit(null));
        Assert.IsNull(actual.VisitExprOpt(null));
        Assert.IsNull(actual.VisitStmtOpt(null));
        foreach (var node in fixture.Nodes())
        {
            Assert.AreSame(node.Expected, expected.Visit((BoundNode)node.Expected), node.Expected.GetType().Name);
            Assert.AreSame(node.Actual, actual.Visit((SelfBound.BoundNode)node.Actual), node.Actual.GetType().Name);
        }
    }

    [TestMethod]
    public void Rewriter_ReplacementPropagationAndMetadataMatchBootstrap()
    {
        Fixture fixture = new();
        BootstrapRewriter expected = new((BoundLiteral)fixture.Leaf.Expected, (BoundLiteral)fixture.Replacement.Expected);
        SelfRewriter actual = new((SelfBound.BoundLiteral)fixture.Leaf.Actual, (SelfBound.BoundLiteral)fixture.Replacement.Actual);
        foreach (var node in fixture.Nodes())
        {
            var left = expected.Visit((BoundNode)node.Expected)!;
            var right = actual.Visit((SelfBound.BoundNode)node.Actual)!;
            Assert.AreEqual(ReferenceEquals(node.Expected, left), ReferenceEquals(node.Actual, right), node.Expected.GetType().Name);
            fixture.AssertEquivalent(left, right);
        }
    }

    [TestMethod]
    public void Rewriter_ExpressionAndStatementArraysCopyOnlyAfterFirstChange()
    {
        BoundLiteral leaf = new(new ConstantValue(1), ErrorTypeSymbol.Instance);
        BoundLiteral replacement = new(new ConstantValue(2), ErrorTypeSymbol.Instance);
        SelfBound.BoundLiteral selfLeaf = new(new SelfSymbols.ConstantValue(1), SelfSymbols.ErrorTypeSymbol.Instance);
        SelfBound.BoundLiteral selfReplacement = new(new SelfSymbols.ConstantValue(2), SelfSymbols.ErrorTypeSymbol.Instance);
        BootstrapRewriter expected = new(leaf, replacement);
        SelfRewriter actual = new(selfLeaf, selfReplacement);
        ImmutableArray<BoundExpression> defaultExpressions = default;
        ImmutableArray<SelfBound.BoundExpression> selfDefaultExpressions = default;
        Assert.IsTrue(expected.Expressions(defaultExpressions).IsDefault);
        Assert.IsTrue(actual.Expressions(selfDefaultExpressions).IsDefault);
        Assert.IsTrue(actual.Expressions([]).IsEmpty);
        Assert.IsTrue(actual.Statements(default).IsDefault);
        Assert.IsTrue(actual.Statements([]).IsEmpty);
        BoundThis unchanged = new(ErrorTypeSymbol.Instance);
        SelfBound.BoundThis selfUnchanged = new(SelfSymbols.ErrorTypeSymbol.Instance);
        ImmutableArray<SelfBound.BoundExpression> unchangedExpressions = [selfUnchanged, selfUnchanged];
        Assert.AreEqual(unchangedExpressions, actual.Expressions(unchangedExpressions));
        SelfBound.BoundNoOp noOp = new();
        ImmutableArray<SelfBound.BoundStatement> unchangedStatements = [noOp, noOp];
        Assert.AreEqual(unchangedStatements, actual.Statements(unchangedStatements));
        for (var changedIndex = 0; changedIndex < 3; changedIndex++)
        {
            ImmutableArray<BoundExpression> expressions = [.. Enumerable.Range(0, 3).Select(i => i == changedIndex ? (BoundExpression)leaf : unchanged)];
            ImmutableArray<SelfBound.BoundExpression> selfExpressions = [.. Enumerable.Range(0, 3).Select(i => i == changedIndex ? (SelfBound.BoundExpression)selfLeaf : selfUnchanged)];
            var left = expected.Expressions(expressions);
            var visitsBefore = actual.LiteralVisits;
            var right = actual.Expressions(selfExpressions);
            Assert.AreEqual(visitsBefore + 1, actual.LiteralVisits);
            Assert.AreNotEqual(selfExpressions, right);
            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(ReferenceEquals(expressions[i], left[i]), ReferenceEquals(selfExpressions[i], right[i]));
                Assert.AreSame(i == changedIndex ? selfReplacement : selfUnchanged, right[i]);
            }

            ImmutableArray<SelfBound.BoundStatement> statements = [.. selfExpressions.Select(e => (SelfBound.BoundStatement)new SelfBound.BoundExpressionStatement(e))];
            var rewritten = actual.Statements(statements);
            Assert.AreNotEqual(statements, rewritten);
            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(i != changedIndex, ReferenceEquals(statements[i], rewritten[i]));
            }
        }
    }

    [TestMethod]
    public void Rewriter_UnknownNodesAndVirtualLeafHooksMatchBootstrap()
    {
        // BoundModuleExpression is deferred until SourceFile/Compilation are migrated.
        Assert.IsNull(typeof(SelfBound.BoundNode).Assembly.GetType("Caladesi.Compiler.Bound.BoundModuleExpression"));
        var left = Assert.ThrowsExactly<InvalidOperationException>(() => new BootstrapRewriter().Visit(new UnknownNode()));
        var right = Assert.ThrowsExactly<InvalidOperationException>(() => new SelfRewriter().Visit(new UnknownSelfNode()));
        Assert.AreEqual("Unhandled bound node UnknownNode", left.Message);
        Assert.AreEqual("Unhandled bound node UnknownSelfNode", right.Message);
        SelfBound.BoundYieldBreak yieldBreak = new();
        SelfBound.BoundNoOp replacement = new();
        Assert.AreSame(replacement, new YieldBreakRewriter(replacement).Visit(yieldBreak));
    }

    private sealed class BootstrapRewriter(BoundLiteral? from = null, BoundLiteral? to = null) : BoundTreeRewriter
    {
        protected override BoundNode VisitLiteral(BoundLiteral n)
        {
            return ReferenceEquals(n, from) ? to! : n;
        }

        public ImmutableArray<BoundExpression> Expressions(ImmutableArray<BoundExpression> expressions)
        {
            return VisitExprs(expressions);
        }
    }

    private sealed class SelfRewriter(SelfBound.BoundLiteral? from = null, SelfBound.BoundLiteral? to = null) : SelfBound.BoundTreeRewriter
    {
        public int LiteralVisits { get; private set; }
        protected override SelfBound.BoundNode VisitLiteral(SelfBound.BoundLiteral n)
        {
            LiteralVisits++;
            return ReferenceEquals(n, from) ? to! : n;
        }
        public ImmutableArray<SelfBound.BoundExpression> Expressions(ImmutableArray<SelfBound.BoundExpression> expressions)
        {
            return VisitExprs(expressions);
        }

        public ImmutableArray<SelfBound.BoundStatement> Statements(ImmutableArray<SelfBound.BoundStatement> statements)
        {
            return VisitStmts(statements);
        }
    }

    private sealed class YieldBreakRewriter(SelfBound.BoundNode replacement) : SelfBound.BoundTreeRewriter
    {
        protected override SelfBound.BoundNode VisitYieldBreak(SelfBound.BoundYieldBreak n)
        {
            return replacement;
        }
    }

    private sealed class UnknownNode : BoundNode;
    private sealed class UnknownSelfNode : SelfBound.BoundNode;

    private sealed class TestField : FieldSymbol
    {
        public override string Name => "field";
        public override Symbol? ContainingSymbol => null;
        public override TypeSymbol Type => ErrorTypeSymbol.Instance;
    }

    private sealed class SelfTestField : SelfSymbols.FieldSymbol
    {
        public override string Name => "field";
        public override SelfSymbols.Symbol? ContainingSymbol => null;
        public override SelfSymbols.TypeSymbol Type => SelfSymbols.ErrorTypeSymbol.Instance;
    }

    private sealed class TestProperty : PropertySymbol
    {
        public override string Name => "property";
        public override Symbol? ContainingSymbol => null;
        public override TypeSymbol Type => ErrorTypeSymbol.Instance;
        public override MethodSymbol? GetMethod => null;
        public override MethodSymbol? SetMethod => null;
    }

    private sealed class SelfTestProperty : SelfSymbols.PropertySymbol
    {
        public override string Name => "property";
        public override SelfSymbols.Symbol? ContainingSymbol => null;
        public override SelfSymbols.TypeSymbol Type => SelfSymbols.ErrorTypeSymbol.Instance;
        public override SelfSymbols.MethodSymbol? GetMethod => null;
        public override SelfSymbols.MethodSymbol? SetMethod => null;
    }

    private sealed record Pair(object Expected, object Actual);

    private sealed class Fixture
    {
        private readonly Dictionary<object, object> originals = new(ReferenceEqualityComparer.Instance);
        private readonly Pair type;
        private readonly Pair syntax;
        private readonly Pair label;
        private readonly Pair local;
        private readonly Pair method;
        private readonly Pair field;
        private readonly Pair property;

        public Fixture()
        {
            type = Remember(ErrorTypeSymbol.Instance, SelfSymbols.ErrorTypeSymbol.Instance);
            syntax = Remember(Compiler.Syntax.SyntaxTree.ParseText("let x = 1;").Root,
                SelfHost::Caladesi.Compiler.Syntax.SyntaxTree.ParseText("let x = 1;").Root);
            label = Create("Symbols.LabelSymbol", "exit");
            local = Create("Symbols.LocalSymbol", "local", type, false, null, null);
            method = Create("Bound.LambdaSymbol", type, "lambda", false, false);
            field = Remember(new TestField(), new SelfTestField());
            property = Remember(new TestProperty(), new SelfTestProperty());
            Leaf = Node("BoundLiteral", Create("Symbols.ConstantValue", 1), type);
            Replacement = Node("BoundLiteral", Create("Symbols.ConstantValue", 2), type);
        }

        public Pair Leaf { get; }
        public Pair Replacement { get; }

        public IEnumerable<Pair> Nodes()
        {
            var empty = Node("BoundNoOp");
            var statement = Node("BoundExpressionStatement", Leaf);
            var block = Node("BoundBlock", Array("Symbols.LocalSymbol", local), Array("Bound.BoundStatement", empty, statement, empty));
            var placeholder = Node("BoundConditionalReceiver", type);
            var arrayType = Create("Symbols.ArrayTypeSymbol", type, null, 1);
            var expressions = Array("Bound.BoundExpression", Node("BoundThis", type), Leaf, Leaf);
            yield return Leaf;
            yield return Node("BoundBadExpression", Array("Bound.BoundNode", Leaf), null);
            yield return Node("BoundLocal", local);
            yield return Node("BoundParameter", Create("Symbols.ParameterSymbol", "p", type, 0, null));
            yield return Node("BoundThis", type);
            yield return Node("BoundBaseReference", type);
            yield return Node("BoundDefaultValue", type);
            yield return Node("BoundTypeOf", type, type);
            yield return Node("BoundTypeExpression", type);
            yield return Node("BoundNamespaceExpression", (object?)null);
            var binder = Remember((Func<TypeSymbol?, BoundExpression>)(_ => (BoundExpression)Leaf.Expected),
                (Func<SelfSymbols.TypeSymbol?, SelfBound.BoundExpression>)(_ => (SelfBound.BoundExpression)Leaf.Actual));
            yield return Set(Node("BoundUnbound", syntax, binder, type), "IsLambda", true);
            yield return Set(Node("BoundMethodGroup", Leaf, "group", Array("Symbols.MethodSymbol", method), Array("Symbols.TypeSymbol", type), true), "IsBaseAccess", true);
            yield return Node("BoundFieldAccess", Leaf, field);
            yield return Set(Node("BoundPropertyAccess", Leaf, property, expressions), "IsBaseAccess", true);
            yield return Set(Node("BoundCall", Leaf, method, expressions, type), "IsBaseCall", true);
            yield return Node("BoundObjectCreation", method, type, expressions,
                Array("Bound.BoundMemberInit", Create("Bound.BoundMemberInit", field, Leaf), Create("Bound.BoundMemberInit", property, Node("BoundThis", type))));
            yield return Node("BoundObjectCreation", null, type, Array("Bound.BoundExpression"), Array("Bound.BoundMemberInit"));
            yield return Node("BoundArrayCreation", arrayType, Leaf, expressions);
            yield return Node("BoundArrayCreation", arrayType, null, Array("Bound.BoundExpression"));
            yield return Node("BoundArrayAccess", Leaf, Leaf, type);
            var folded = Create("Symbols.ConstantValue", 17);
            yield return Set(Set(Node("BoundConversion", Leaf, 11, type, method), "CaseIndex", 4), "ConstantFolded", folded);
            yield return Set(Node("BoundUnary", 1, Leaf, type, method), "Folded", folded);
            yield return Set(Node("BoundBinary", 0, Leaf, Leaf, type, type, method), "Folded", folded);
            yield return Set(Node("BoundLogicalBinary", true, Leaf, Leaf, type), "Folded", folded);
            yield return Set(Node("BoundAssignment", Leaf, Leaf), "IsDiscarded", true);
            yield return Node("BoundConditional", Leaf, Leaf, Leaf, type);
            yield return Node("BoundCoalesce", Leaf, Leaf, type);
            yield return placeholder;
            yield return Node("BoundConditionalAccess", Leaf, placeholder, Leaf, type);
            yield return Node("BoundTypeTest", Leaf, type, type, 1, 3);
            yield return Node("BoundLambda", method, block, type);
            yield return Node("BoundDelegateCreation", Leaf, method, type);
            yield return Node("BoundDelegateCreation", null, method, type);
            yield return Node("BoundSequence", Array("Symbols.LocalSymbol", local), Array("Bound.BoundStatement", empty, statement), Leaf);
            yield return Node("BoundAddressOf", Leaf);
            yield return Node("BoundIsNull", Leaf, type);
            yield return Node("BoundAwait", Leaf, Create("Bound.AwaitInfo", method, property, method, method, type), type);
            yield return Node("BoundThrowExpression", Leaf, type);
            yield return block;
            yield return Node("BoundStatementList", Array("Bound.BoundStatement", empty, statement, empty));
            yield return statement;
            yield return Node("BoundLocalDeclaration", local, Leaf);
            yield return Node("BoundLocalDeclaration", local, null);
            yield return Node("BoundIf", Leaf, statement, null);
            yield return Node("BoundIf", Leaf, empty, statement);
            yield return Node("BoundWhile", Leaf, statement, label, label);
            yield return Node("BoundDoWhile", Leaf, statement, label, label);
            yield return Node("BoundFor", Array("Symbols.LocalSymbol", local), statement, Leaf, statement, block, label, label);
            yield return Node("BoundFor", Array("Symbols.LocalSymbol"), null, null, null, empty, label, label);
            yield return Node("BoundGoto", label);
            yield return Node("BoundLabel", label);
            yield return Node("BoundConditionalGoto", Leaf, label, false);
            yield return Node("BoundReturn", Leaf);
            yield return Node("BoundReturn", (object?)null);
            yield return Node("BoundThrow", Leaf);
            yield return Node("BoundThrow", (object?)null);
            var unchangedBlock = Node("BoundBlock", Array("Symbols.LocalSymbol"), Array("Bound.BoundStatement", empty));
            var unchangedCatch = Create("Bound.BoundCatch", null, type, unchangedBlock);
            var changedCatch = Create("Bound.BoundCatch", local, type, block);
            yield return Node("BoundTry", block, Array("Bound.BoundCatch", unchangedCatch, changedCatch), block);
            yield return Node("BoundTry", unchangedBlock, Array("Bound.BoundCatch", changedCatch), null);
            yield return Node("BoundTry", unchangedBlock, Array("Bound.BoundCatch", unchangedCatch), block);
            yield return Node("BoundTry", unchangedBlock, Array("Bound.BoundCatch"), null);
            yield return Set(Node("BoundYieldReturn", Leaf), "DisposeTarget", label);
            yield return Node("BoundYieldBreak");
            yield return empty;
            yield return Node("BoundSequencePoint", statement, syntax);
        }

        public void AssertEquivalent(object? expected, object? actual)
        {
            if (expected == null) { Assert.IsNull(actual); return; }
            Assert.IsNotNull(actual);
            if (originals.TryGetValue(expected, out var original))
            {
                Assert.AreSame(original, actual, expected.GetType().Name);
                return;
            }
            if (expected is Enum) { Assert.AreEqual(Convert.ToInt32(expected), Convert.ToInt32(actual)); return; }
            if (expected is string || expected.GetType().IsPrimitive) { Assert.AreEqual(expected, actual); return; }
            Assert.AreEqual(expected.GetType().Name, actual.GetType().Name);
            if (expected is IEnumerable sequence)
            {
                object[] left = [.. sequence.Cast<object>()];
                object[] right = [.. ((IEnumerable)actual).Cast<object>()];
                Assert.HasCount(left.Length, right);
                for (var i = 0; i < left.Length; i++)
                {
                    AssertEquivalent(left[i], right[i]);
                }

                return;
            }
            foreach (var member in expected.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var counterpart = actual.GetType().GetProperties()
                    .First(p => p.Name == member.Name);
                var left = member.GetValue(expected);
                var right = counterpart.GetValue(actual);
                if (left != null && member.PropertyType.IsGenericType &&
                    member.PropertyType.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
                {
                    if ((bool)member.PropertyType.GetProperty("IsDefault")!.GetValue(left)!)
                    {
                        Assert.IsTrue((bool)right!.GetType().GetProperty("IsDefault")!.GetValue(right)!);
                        continue;
                    }
                }
                AssertEquivalent(left, right);
            }
        }

        private Pair Node(string name, params object?[] arguments)
        {
            return Set(Create("Bound." + name, arguments), "Syntax", syntax);
        }

        private Pair Remember(object expected, object actual)
        {
            originals.Add(expected, actual);
            return new Pair(expected, actual);
        }

        private Pair Create(string name, params object?[] arguments)
        {
            var left = typeof(BoundNode).Assembly.GetType("Caladesi.Compiler." + name, true)!;
            var right = typeof(SelfBound.BoundNode).Assembly.GetType("Caladesi.Compiler." + name, true)!;
            return Remember(Construct(left, arguments, false), Construct(right, arguments, true));
        }

        private static object Construct(Type type, object?[] arguments, bool self)
        {
            var constructor = type.GetConstructors()
                .Where(c => c.GetParameters().Length >= arguments.Length && c.GetParameters().Skip(arguments.Length).All(p => p.HasDefaultValue))
                .OrderBy(c => c.GetParameters().Length).First();
            var parameters = constructor.GetParameters();
            var values = new object?[parameters.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var value = i < arguments.Length ? arguments[i] : parameters[i].DefaultValue;
                if (value is Pair pair)
                {
                    value = self ? pair.Actual : pair.Expected;
                }

                if (value != null && parameters[i].ParameterType.IsEnum)
                {
                    value = Enum.ToObject(parameters[i].ParameterType, value);
                }

                values[i] = value;
            }
            return constructor.Invoke(values);
        }

        private static Pair Array(string elementName, params Pair[] items)
        {
            object Build(bool self)
            {
                var assembly = self ? typeof(SelfBound.BoundNode).Assembly : typeof(BoundNode).Assembly;
                var element = assembly.GetType("Caladesi.Compiler." + elementName, true)!;
                var values = System.Array.CreateInstance(element, items.Length);
                for (var i = 0; i < items.Length; i++)
                {
                    values.SetValue(self ? items[i].Actual : items[i].Expected, i);
                }

                var create = typeof(ImmutableArray).GetMethods()
                    .Single(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsArray);
                return create.MakeGenericMethod(element).Invoke(null, [values])!;
            }
            return new Pair(Build(false), Build(true));
        }

        private static Pair Set(Pair target, string propertyName, object value)
        {
            target.Expected.GetType().GetProperty(propertyName)!.SetValue(target.Expected, value is Pair pair ? pair.Expected : value);
            target.Actual.GetType().GetProperty(propertyName)!.SetValue(target.Actual, value is Pair other ? other.Actual : value);
            return target;
        }
    }
}
