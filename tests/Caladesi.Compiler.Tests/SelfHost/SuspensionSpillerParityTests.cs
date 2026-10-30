extern alias SelfHost;

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;
using SelfBound = SelfHost::Caladesi.Compiler.Bound;
using SelfLowering = SelfHost::Caladesi.Compiler.Emit.Lowering;
using SelfSymbols = SelfHost::Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class SuspensionSpillerParityTests
{
    [TestMethod]
    public void Finder_CachesSharedSubtreesAndFindsCatchFinallyAndYield()
    {
        Fixture f = new();
        var finder = f.Finder();
        var empty = f.Block(f.Node("BoundNoOp"));
        var awaited = f.Await();
        var catchBody = f.Block(f.Statement(awaited));
        var @try = f.Node("BoundTry", empty, Fixture.Array("Bound.BoundCatch",
            f.Create("Bound.BoundCatch", null, f.Type, catchBody)), empty);
        Pair[] nodes = [f.Leaf, awaited, empty, @try,
            f.Node("BoundYieldReturn", f.Leaf), f.Node("BoundYieldBreak"),
            f.Node("BoundTry", empty, Fixture.Array("Bound.BoundCatch"), catchBody)];
        Assert.IsFalse((bool)Fixture.Invoke(finder, "Has", (object?)null).Actual);
        foreach (var node in nodes.Concat(nodes.Reverse()))
        {
            var result = Fixture.Invoke(finder, "Has", node);
            Assert.AreEqual(result.Expected, result.Actual);
            Assert.AreSame(node.Actual, Fixture.Invoke(finder, "Visit", node).Actual);
        }
        Assert.IsTrue((bool)Fixture.Invoke(finder, "Has", @try).Actual);
        Assert.IsFalse((bool)Fixture.Invoke(finder, "Has", empty).Actual);
        IDictionary expectedCache = Cache(finder.Expected), actualCache = Cache(finder.Actual);
        Assert.HasCount(expectedCache.Count, actualCache);
        var cachedCount = actualCache.Count;
        foreach (var node in nodes)
        {
            _ = Fixture.Invoke(finder, "Has", node);
        }

        Assert.HasCount(cachedCount, actualCache);

        static IDictionary Cache(object instance)
        {
            return (IDictionary)instance.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => typeof(IDictionary).IsAssignableFrom(field.FieldType)).GetValue(instance)!;
        }
    }

    [TestMethod]
    public void Spiller_NoSuspensionReturnsOriginalTreeAndExpression()
    {
        Fixture f = new();
        var body = f.Block(f.Statement(f.Leaf));
        var spilled = f.Spill(body);
        Assert.AreSame(body.Expected, spilled.Expected);
        Assert.AreSame(body.Actual, spilled.Actual);
        f.CheckExpression(f.Leaf);
    }

    [TestMethod]
    public void Finder_LambdaBodyTraversalMatchesOracle()
    {
        Fixture f = new();
        var lambda = f.Lambda();
        var result = Fixture.Invoke(f.Finder(), "Has", lambda);
        Assert.IsTrue((bool)result.Expected);
        Assert.AreEqual(result.Expected, result.Actual);
    }

    [TestMethod]
    public void Spiller_ExpressionEvaluationOrderAndMetadataMatchOracle()
    {
        Fixture f = new();
        foreach (var expression in f.Expressions())
        {
            f.CheckExpression(expression);
        }
    }

    [TestMethod]
    public void Spiller_StatementsLoopsGotosAndScopesMatchOracle()
    {
        Fixture f = new();
        foreach (var statement in f.Statements())
        {
            f.AssertEquivalent(f.Spill(f.Block(statement)));
        }
    }

    [TestMethod]
    public void Spiller_VoidAwaitRemainsStatementWithoutVoidTemporary()
    {
        Fixture f = new();
        var body = f.VoidAwaitBody();
        var spilled = f.Spill(body);
        f.AssertEquivalent(spilled);
        var statement = (SelfBound.BoundExpressionStatement)
            ((SelfBound.BoundBlock)spilled.Actual).Statements[^1];
        _ = Assert.IsInstanceOfType<SelfBound.BoundAwait>(statement.Expression);
    }

    [TestMethod]
    public void Spiller_ShortCircuitBranchesKeepAwaitsInsideTheirBranch()
    {
        Fixture f = new();
        foreach (var expression in new[]
        {
            f.Node("BoundLogicalBinary", true, f.Leaf, f.Await(), f.Type),
            f.Node("BoundLogicalBinary", false, f.Leaf, f.Await(), f.Type),
            f.Node("BoundConditional", f.Leaf, f.Await(), f.Await(), f.Type),
            f.Node("BoundCoalesce", f.SideEffect(), f.Await(), f.Type)
        })
        {
            var result = f.Spill(f.Block(f.Statement(expression)));
            f.AssertEquivalent(result);
            var actual = (SelfBound.BoundBlock)result.Actual;
            var branch = actual.Statements.OfType<SelfBound.BoundIf>().Single();
            Assert.IsFalse(actual.Statements.OfType<SelfBound.BoundExpressionStatement>()
                .Any(s => s.Expression is SelfBound.BoundAssignment { Value: SelfBound.BoundAwait }));
            Assert.IsTrue(((SelfBound.BoundBlock)branch.Consequence).Statements
                .OfType<SelfBound.BoundExpressionStatement>()
                .Any(s => s.Expression is SelfBound.BoundAssignment { Value: SelfBound.BoundAwait }));
        }
    }

    [TestMethod]
    public void Spiller_UnsupportedExpressionsReportSameMessageAndSyntax()
    {
        Fixture f = new();
        var placeholder = f.Node("BoundConditionalReceiver", f.Type);
        foreach (var expression in new[]
        {
            f.Node("BoundConditionalAccess", f.Leaf, placeholder, f.Await(), f.Type), f.Lambda()
        })
        {
            var body = f.Block(f.Statement(expression));
            var spiller = f.Spiller();
            var expected = Assert.ThrowsExactly<TargetInvocationException>(() =>
                Fixture.Call(spiller.Expected, "Spill", body.Expected)).InnerException!;
            var actual = Assert.ThrowsExactly<TargetInvocationException>(() =>
                Fixture.Call(spiller.Actual, "Spill", body.Actual)).InnerException!;
            Assert.AreEqual("LoweringNotSupportedException", expected.GetType().Name);
            Assert.AreEqual(expected.GetType().Name, actual.GetType().Name);
            Assert.AreEqual(expected.Message, actual.Message);
            Assert.AreSame(((BoundNode)expression.Expected).Syntax, expected.GetType().GetProperty("Syntax")!.GetValue(expected));
            Assert.AreSame(((SelfBound.BoundNode)expression.Actual).Syntax, actual.GetType().GetProperty("Syntax")!.GetValue(actual));
        }
    }

    [TestMethod]
    public void LoweringException_ConstructorsPreserveMessageInnerExceptionAndSyntax()
    {
        Assert.IsNull(new SelfLowering.LoweringNotSupportedException().Syntax);
        Assert.AreEqual("message", new SelfLowering.LoweringNotSupportedException("message").Message);
        InvalidOperationException inner = new("inner");
        SelfLowering.LoweringNotSupportedException wrapped = new("outer", inner);
        Assert.AreSame(inner, wrapped.InnerException);
        Assert.AreEqual("outer", wrapped.Message);
        var syntax = SelfHost::Caladesi.Compiler.Syntax.SyntaxTree.ParseText("1;").Root;
        Assert.AreSame(syntax, new SelfLowering.LoweringNotSupportedException("syntax", syntax).Syntax);
    }

    private sealed record Pair(object Expected, object Actual);

    private sealed class Fixture
    {
        private readonly Dictionary<object, object> pairs = new(ReferenceEqualityComparer.Instance);
        private readonly Pair syntax;
        private readonly Pair method;
        private readonly Pair property;
        private readonly Pair field;
        private readonly Pair local;
        private readonly Pair label;
        private readonly Pair continueLabel;
        private readonly Pair awaitInfo;
        public Pair Type { get; }
        public Pair Leaf { get; }

        public Fixture()
        {
            Type = Remember(ErrorTypeSymbol.Instance, SelfSymbols.ErrorTypeSymbol.Instance);
            syntax = Remember(SyntaxTree.ParseText("let x = 1;").Root,
                SelfHost::Caladesi.Compiler.Syntax.SyntaxTree.ParseText("let x = 1;").Root);
            method = Create("Bound.LambdaSymbol", Type, "sideEffect", false, false);
            property = Remember(new TestProperty(), new SelfTestProperty());
            field = Remember(new TestField(), new SelfTestField());
            local = Create("Symbols.LocalSymbol", "local", Type, false, null, null);
            label = Create("Symbols.LabelSymbol", "break");
            continueLabel = Create("Symbols.LabelSymbol", "continue");
            awaitInfo = Create("Bound.AwaitInfo", method, property, method, method, Type);
            Leaf = Node("BoundLiteral", Create("Symbols.ConstantValue", 1), Type);
        }

        public Pair Finder()
        {
            return Create("Emit.Lowering.SuspensionFinder");
        }

        public Pair Spiller()
        {
            int left = 0, right = 0;
            Pair factory = new((Func<TypeSymbol, LocalSymbol>)(type => new LocalSymbol("$" + left++, type, false, null)),
                (Func<SelfSymbols.TypeSymbol, SelfSymbols.LocalSymbol>)(type => new SelfSymbols.LocalSymbol("$" + right++, type, false, null)));
            return Create("Emit.Lowering.SuspensionSpiller", Finder(), factory, Type);
        }
        public Pair Spill(Pair block)
        {
            return Invoke(Spiller(), "Spill", block);
        }

        public Pair Await()
        {
            return Node("BoundAwait", SideEffect(), awaitInfo, Type);
        }

        public Pair Lambda()
        {
            return Node("BoundLambda", method, Block(Statement(Await())), Type);
        }

        public Pair VoidAwaitBody()
        {
            return Block(Statement(Node("BoundAwait", Await(), awaitInfo,
                    Remember(new VoidType(), new SelfVoidType()))));
        }

        public Pair SideEffect()
        {
            return Node("BoundCall", null, method, Array("Bound.BoundExpression"), Type);
        }

        public Pair Statement(Pair expression)
        {
            return Node("BoundExpressionStatement", expression);
        }

        public Pair Block(params Pair[] statements)
        {
            return Node("BoundBlock", Array("Symbols.LocalSymbol", local), Array("Bound.BoundStatement", statements));
        }

        public IEnumerable<Pair> Expressions()
        {
            var a = Await();
            var expressions = Array("Bound.BoundExpression", SideEffect(), a, SideEffect(), Await(), Leaf);
            yield return a;
            yield return Node("BoundAwait", a, awaitInfo, Type);
            yield return Set(Node("BoundCall", SideEffect(), method, expressions, Type), "IsBaseCall", true);
            yield return Set(Node("BoundCall", null, method, expressions, Type), "IsBaseCall", true);
            yield return Set(Node("BoundPropertyAccess", SideEffect(), property, expressions), "IsBaseAccess", true);
            yield return Node("BoundFieldAccess", a, field);
            yield return Node("BoundObjectCreation", method, Type, expressions, Array("Bound.BoundMemberInit",
                Create("Bound.BoundMemberInit", field, Await()), Create("Bound.BoundMemberInit", property, SideEffect())));
            yield return Node("BoundObjectCreation", method, Type, expressions, Array("Bound.BoundMemberInit"));
            yield return Node("BoundObjectCreation", method, Type, expressions,
                new Pair(default(ImmutableArray<BoundMemberInit>), default(ImmutableArray<SelfBound.BoundMemberInit>)));
            var arrayType = Create("Symbols.ArrayTypeSymbol", Type, null, 1);
            yield return Node("BoundArrayCreation", arrayType, SideEffect(), expressions);
            yield return Node("BoundArrayCreation", arrayType, null, expressions);
            yield return Node("BoundArrayAccess", SideEffect(), a, Type);
            var folded = Create("Symbols.ConstantValue", 17);
            yield return Set(Set(Node("BoundConversion", a, 11, Type, method), "CaseIndex", 4), "ConstantFolded", folded);
            yield return Set(Node("BoundUnary", 1, a, Type, method), "Folded", folded);
            yield return Set(Node("BoundBinary", 0, SideEffect(), a, Type, Type, method), "Folded", folded);
            foreach (var stable in new[]
            {
                Leaf, Node("BoundLocal", local), Node("BoundThis", Type),
                Node("BoundParameter", Create("Symbols.ParameterSymbol", "p", Type, 0, null)),
                Node("BoundDefaultValue", Type), Node("BoundTypeExpression", Type),
                Node("BoundBaseReference", Type),
                Node("BoundFieldAccess", null, Remember(new ConstField(), new SelfConstField()))
            })
            {
                yield return Node("BoundBinary", 0, stable, a, Type, Type, null);
            }

            yield return Node("BoundTypeTest", a, Type, Type, 1, 3);
            yield return Node("BoundDelegateCreation", a, method, Type);
            yield return Node("BoundThrowExpression", a, Type);
            yield return Node("BoundAddressOf", a);
            yield return Node("BoundIsNull", a, Type);
            foreach (var target in new[]
            {
                Node("BoundLocal", local), Node("BoundFieldAccess", SideEffect(), field),
                Node("BoundFieldAccess", Node("BoundLocal", local), field),
                Node("BoundArrayAccess", SideEffect(), SideEffect(), Type),
                Set(Node("BoundPropertyAccess", SideEffect(), property, Array("Bound.BoundExpression", SideEffect(), Leaf)), "IsBaseAccess", true),
                Node("BoundPropertyAccess", null, property, Array("Bound.BoundExpression", a))
            })
            {
                yield return Set(Node("BoundAssignment", target, a), "IsDiscarded", true);
            }

            yield return Node("BoundSequence", Array("Symbols.LocalSymbol", local), Array("Bound.BoundStatement", Statement(a)), a);
            yield return Node("BoundSequence", Array("Symbols.LocalSymbol"), Array("Bound.BoundStatement", Statement(a)), Leaf);
            foreach (var isAnd in new[] { true, false })
            {
                yield return Node("BoundLogicalBinary", isAnd, Leaf, a, Type);
                yield return Node("BoundLogicalBinary", isAnd, a, Leaf, Type);
                yield return Node("BoundLogicalBinary", isAnd, a, Await(), Type);
            }
            yield return Node("BoundConditional", a, Leaf, Leaf, Type);
            yield return Node("BoundConditional", a, a, Await(), Type);
            yield return Node("BoundCoalesce", a, Leaf, Type);
            yield return Node("BoundCoalesce", SideEffect(), a, Type);
            yield return Node("BoundCoalesce", Node("BoundLocal", local), a, Type);
            yield return Node("BoundConditionalAccess", a, Node("BoundConditionalReceiver", Type), Leaf, Type);
        }

        public IEnumerable<Pair> Statements()
        {
            Pair a = Await(), s = Statement(a), empty = Node("BoundNoOp");
            yield return s;
            yield return Node("BoundStatementList", Array("Bound.BoundStatement", empty, s, s));
            yield return Node("BoundSequencePoint", s, syntax);
            yield return Node("BoundSequencePoint", Node("BoundStatementList", Array("Bound.BoundStatement", s, s)), syntax);
            yield return Node("BoundLocalDeclaration", local, a);
            yield return Node("BoundIf", a, s, s);
            yield return Node("BoundIf", Leaf, s, null);
            yield return Node("BoundWhile", a, Node("BoundGoto", continueLabel), label, continueLabel);
            yield return Node("BoundWhile", Leaf, s, label, continueLabel);
            yield return Node("BoundDoWhile", a, s, label, continueLabel);
            yield return Node("BoundDoWhile", Leaf, s, label, continueLabel);
            yield return Node("BoundFor", Array("Symbols.LocalSymbol", local), s, a, s, s, label, continueLabel);
            yield return Node("BoundFor", Array("Symbols.LocalSymbol", local), null, null, s, empty, label, continueLabel);
            yield return Node("BoundFor", Array("Symbols.LocalSymbol", local), s, Leaf, empty, s, label, continueLabel);
            yield return Node("BoundFor", Array("Symbols.LocalSymbol", local), s, Leaf, empty, empty, label, continueLabel);
            yield return Node("BoundConditionalGoto", a, label, false);
            yield return Node("BoundReturn", a);
            yield return Node("BoundThrow", a);
            yield return Node("BoundYieldReturn", a);
            yield return Node("BoundYieldBreak");
            yield return Node("BoundTry", Block(s), Array("Bound.BoundCatch", Create("Bound.BoundCatch", local, Type, Block(s))), Block(s));
            yield return Node("BoundTry", Block(empty), Array("Bound.BoundCatch", Create("Bound.BoundCatch", null, Type, Block(s))), null);
        }

        public void CheckExpression(Pair expression)
        {
            Pair prefix = new(new List<BoundStatement>(), new List<SelfBound.BoundStatement>());
            var result = Invoke(Spiller(), "SpillExpr", expression, prefix);
            AssertEquivalent(prefix);
            AssertEquivalent(result);
        }

        public void AssertEquivalent(Pair pair)
        {
            AssertEquivalent(pair.Expected, pair.Actual);
        }

        private void AssertEquivalent(object? expected, object? actual)
        {
            if (expected == null) { Assert.IsNull(actual); return; }
            Assert.IsNotNull(actual);
            if (pairs.TryGetValue(expected, out var mapped)) { Assert.AreSame(mapped, actual); return; }
            if (expected is Enum) { Assert.AreEqual(Convert.ToInt32(expected), Convert.ToInt32(actual)); return; }
            if (expected is string || expected.GetType().IsPrimitive) { Assert.AreEqual(expected, actual); return; }
            Assert.AreEqual(expected.GetType().Name, actual.GetType().Name);
            if (expected is IEnumerable sequence)
            {
                if (expected.GetType().IsGenericType && expected.GetType().GetGenericTypeDefinition() == typeof(ImmutableArray<>) &&
                    (bool)expected.GetType().GetProperty("IsDefault")!.GetValue(expected)!)
                {
                    Assert.IsTrue((bool)actual.GetType().GetProperty("IsDefault")!.GetValue(actual)!);
                    return;
                }
                object[] left = [.. sequence.Cast<object>()], right = [.. ((IEnumerable)actual).Cast<object>()];
                Assert.HasCount(left.Length, right);
                for (var i = 0; i < left.Length; i++)
                {
                    AssertEquivalent(left[i], right[i]);
                }

                return;
            }
            if (expected is Symbol symbol)
            {
                Assert.AreEqual(symbol.Name, actual.GetType().GetProperty("Name")!.GetValue(actual));
                pairs.Add(expected, actual);
                if (expected is LocalSymbol localSymbol)
                {
                    AssertEquivalent(localSymbol.Type, actual.GetType().GetProperty("Type")!.GetValue(actual));
                }

                return;
            }
            pairs.Add(expected, actual);
            foreach (var member in expected.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var counterpart = actual.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Single(property => property.Name == member.Name && property.DeclaringType!.FullName == member.DeclaringType!.FullName);
                AssertEquivalent(member.GetValue(expected), counterpart.GetValue(actual));
            }
        }

        public Pair Node(string name, params object?[] arguments)
        {
            return Set(Create("Bound." + name, arguments), "Syntax", syntax);
        }

        private Pair Remember(object expected, object actual) { pairs.Add(expected, actual); return new(expected, actual); }
        public Pair Create(string name, params object?[] arguments)
        {
            var left = typeof(BoundNode).Assembly.GetType("Caladesi.Compiler." + name, true)!;
            var right = typeof(SelfBound.BoundNode).Assembly.GetType("Caladesi.Compiler." + name, true)!;
            return Remember(Construct(left, arguments, false), Construct(right, arguments, true));
        }
        private static object Construct(Type type, object?[] arguments, bool self)
        {
            var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
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
        public static Pair Invoke(Pair target, string name, params object?[] arguments)
        {
            return new(
            Call(target.Expected, name, [.. arguments.Select(a => a is Pair p ? p.Expected : a)]),
            Call(target.Actual, name, [.. arguments.Select(a => a is Pair p ? p.Actual : a)]));
        }

        public static object Call(object target, string name, params object?[] arguments)
        {
            return target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, arguments)!;
        }

        public static Pair Array(string elementName, params Pair[] items)
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

                var create = typeof(ImmutableArray).GetMethods().Single(m => m.Name == "Create" &&
                    m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsArray);
                return create.MakeGenericMethod(element).Invoke(null, [values])!;
            }
            return new(Build(false), Build(true));
        }
        public static Pair Set(Pair target, string name, object value)
        {
            target.Expected.GetType().GetProperty(name)!.SetValue(target.Expected, value is Pair p ? p.Expected : value);
            target.Actual.GetType().GetProperty(name)!.SetValue(target.Actual, value is Pair q ? q.Actual : value);
            return target;
        }
    }

    private class TestField : FieldSymbol
    {
        public override string Name => "field";
        public override Symbol? ContainingSymbol => null;
        public override TypeSymbol Type => ErrorTypeSymbol.Instance;
    }
    private sealed class VoidType : TypeSymbol
    {
        public override string Name => "void";
        public override SymbolKind Kind => SymbolKind.NamedType;
        public override Symbol? ContainingSymbol => null;
        public override TypeKind TypeKind => TypeKind.Void;
        public override SpecialType SpecialType => SpecialType.Void;
        public override bool IsReferenceType => false;
        public override bool IsValueType => false;
        public override bool Equals(TypeSymbol? other)
        {
            return ReferenceEquals(this, other);
        }
    }
    private sealed class SelfVoidType : SelfSymbols.TypeSymbol
    {
        public override string Name => "void";
        public override SelfSymbols.SymbolKind Kind => SelfSymbols.SymbolKind.NamedType;
        public override SelfSymbols.Symbol? ContainingSymbol => null;
        public override SelfSymbols.TypeKind TypeKind => SelfSymbols.TypeKind.Void;
        public override SelfSymbols.SpecialType SpecialType => SelfSymbols.SpecialType.Void;
        public override bool IsReferenceType => false;
        public override bool IsValueType => false;
        public override bool Equals(SelfSymbols.TypeSymbol? other)
        {
            return ReferenceEquals(this, other);
        }
    }
    private class SelfTestField : SelfSymbols.FieldSymbol
    {
        public override string Name => "field";
        public override SelfSymbols.Symbol? ContainingSymbol => null;
        public override SelfSymbols.TypeSymbol Type => SelfSymbols.ErrorTypeSymbol.Instance;
    }
    private sealed class ConstField : TestField
    {
        public override bool IsConst => true;
    }
    private sealed class SelfConstField : SelfTestField
    {
        public override bool IsConst => true;
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
}
