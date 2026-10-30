extern alias SelfHost;

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using B = Caladesi.Compiler.Bound;
using S = Caladesi.Compiler.Symbols;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using SyntaxNode = Caladesi.Compiler.Syntax.SyntaxNode;
using HSyntaxNode = SelfHost::Caladesi.Compiler.Syntax.SyntaxNode;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class BoundNodeParityTests
{
    [TestMethod]
    public void NodeConstructors_AllShapesPreserveTypesChildrenDefaultsAndAnnotations()
    {
        foreach (var type in new[] { typeof(B.BoundNode), typeof(B.BoundExpression), typeof(B.BoundStatement) })
        {
            Assert.IsTrue(HostType(type).IsAbstract, type.Name);
            Assert.AreEqual(type.BaseType!.FullName, HostType(type).BaseType!.FullName, type.Name);
        }
        // Module expressions depend on SourceFile and the not-yet-migrated compilation layer.
        Type[] originals = [.. typeof(B.BoundNode).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(B.BoundNode).Namespace && t.IsClass && t.IsSealed &&
                t != typeof(B.LambdaInfo) && t != typeof(B.ObjectLiteralInfo) && t != typeof(B.CollectionInfo) &&
                t != typeof(B.BoundModuleExpression))];
        Assert.IsGreaterThan(50, originals.Length);
        foreach (var variant in new[] { 0, 1, 2 })
        {
            Fixtures fixtures = new(variant);
            foreach (var original in originals)
            {
                var migrated = HostType(original);
                Assert.IsTrue(migrated.IsSealed, original.Name);
                Assert.AreEqual(original.BaseType!.FullName, migrated.BaseType!.FullName, original.Name);
                var constructor = original.GetConstructors().Single();
                var parameters = constructor.GetParameters();
                var hostConstructor = migrated.GetConstructors()
                    .Single(c => c.GetParameters().Length == parameters.Length);
                List<object?> left = [];
                List<object?> right = [];
                foreach (var parameter in parameters)
                {
                    var nullable = new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable;
                    (var Original, var Host) = fixtures.Value(parameter.ParameterType, nullable);
                    left.Add(Original);
                    right.Add(Host);
                }

                var expected = constructor.Invoke([.. left]);
                var actual = hostConstructor.Invoke([.. right]);
                fixtures.CompareProperties(expected, actual);
                foreach (var property in original.GetProperties().Where(p => p.SetMethod?.IsPublic == true))
                {
                    (var Original, var Host) = fixtures.Value(property.PropertyType, nullable: false);
                    property.SetValue(expected, Original);
                    migrated.GetProperty(property.Name)!.SetValue(actual, Host);
                }
                fixtures.CompareProperties(expected, actual);
            }
        }
    }

    [TestMethod]
    public void Operators_AllEnumValuesAndFoldedConstantsMatch()
    {
        foreach (var type in new[] { typeof(B.ConversionKind), typeof(B.UnaryOperatorKind),
                     typeof(B.BinaryOperatorKind), typeof(B.TypeTestKind) })
        {
            var migrated = HostType(type);
            CollectionAssert.AreEqual(Enum.GetNames(type), Enum.GetNames(migrated));
            CollectionAssert.AreEqual(Enum.GetValues(type).Cast<object>().Select(Convert.ToInt32).ToArray(),
                Enum.GetValues(migrated).Cast<object>().Select(Convert.ToInt32).ToArray());
        }

        B.BoundLiteral operand = new(new S.ConstantValue(11), S.NullTypeSymbol.Instance);
        HB.BoundLiteral hostOperand = new(new HS.ConstantValue(11), HS.NullTypeSymbol.Instance);
        foreach (var kind in Enum.GetValues<B.ConversionKind>())
        {
            B.BoundConversion expected = new(operand, kind, operand.Type!) { CaseIndex = 3, ConstantFolded = operand.Value };
            HB.BoundConversion actual = new(hostOperand, (HB.ConversionKind)kind, hostOperand.Type!)
            { CaseIndex = 3, ConstantFolded = hostOperand.Value };
            Assert.AreEqual((int)expected.Kind, (int)actual.Kind);
            Assert.AreEqual(expected.CaseIndex, actual.CaseIndex);
            Assert.AreEqual(expected.ConstantValue!.Value.Value, actual.ConstantValue!.Value);
            Assert.IsNull(new HB.BoundConversion(hostOperand, (HB.ConversionKind)kind, hostOperand.Type!).ConstantValue);
        }
        foreach (var kind in Enum.GetValues<B.UnaryOperatorKind>())
        {
            B.BoundUnary expected = new(kind, operand, operand.Type!) { Folded = operand.Value };
            HB.BoundUnary actual = new((HB.UnaryOperatorKind)kind, hostOperand, hostOperand.Type!) { Folded = hostOperand.Value };
            Assert.AreEqual((int)expected.Kind, (int)actual.Kind);
            Assert.AreEqual(expected.ConstantValue!.Value.Value, actual.ConstantValue!.Value);
        }
        foreach (var kind in Enum.GetValues<B.BinaryOperatorKind>())
        {
            B.BoundBinary expected = new(kind, operand, operand, operand.Type!, operand.Type!) { Folded = operand.Value };
            HB.BoundBinary actual = new((HB.BinaryOperatorKind)kind, hostOperand, hostOperand, hostOperand.Type!, hostOperand.Type!)
            { Folded = hostOperand.Value };
            Assert.AreEqual((int)expected.Kind, (int)actual.Kind);
            Assert.AreEqual(expected.ConstantValue!.Value.Value, actual.ConstantValue!.Value);
        }
        HB.BoundLogicalBinary logical = new(true, hostOperand, hostOperand, hostOperand.Type!);
        Assert.IsNull(logical.ConstantValue);
        logical.Folded = hostOperand.Value;
        Assert.AreSame(hostOperand.Value, logical.ConstantValue);
    }

    [TestMethod]
    public void Constants_LocalsFieldsBadExpressionsAndOptionalConstructorsMatch()
    {
        Fixtures fixtures = new(0);
        foreach (var isConst in new[] { false, true })
        {
            S.LocalSymbol local = new("local", S.NullTypeSymbol.Instance, isConst, null) { ConstantValue = new(17) };
            HS.LocalSymbol hostLocal = new("local", HS.NullTypeSymbol.Instance, isConst, null) { ConstantValue = new(17) };
            Assert.AreEqual(new B.BoundLocal(local).ConstantValue?.Value, new HB.BoundLocal(hostLocal).ConstantValue?.Value);
            Assert.AreEqual(new B.BoundFieldAccess(null, new FieldFixture(isConst)).ConstantValue?.Value,
                new HB.BoundFieldAccess(null, new HostFieldFixture(isConst)).ConstantValue?.Value);
        }
        Assert.IsTrue(new HB.BoundBadExpression([]).HasErrors);
        Assert.IsTrue(new HB.BoundBadExpression([], HS.NullTypeSymbol.Instance).HasErrors);
        Assert.IsTrue(new HB.BoundThis(HS.ErrorTypeSymbol.Instance).HasErrors);
        Assert.IsFalse(new HB.BoundThis(HS.NullTypeSymbol.Instance).HasErrors);
        Assert.IsNull(new HB.BoundDefaultValue(HS.NullTypeSymbol.Instance).ConstantValue);
        Assert.IsTrue(new HB.BoundLiteral(HS.ConstantValue.Null, HS.NullTypeSymbol.Instance).ConstantValue!.IsNull);
        B.BoundCall call = new(null, new MethodFixture(), []);
        HB.BoundCall hostCall = new(null, new HostMethodFixture(), []);
        Assert.AreEqual(call.Type!.Name, hostCall.Type!.Name);
        Assert.AreEqual(call.Type.Name, new HB.BoundCall(null, hostCall.Method, [], null).Type!.Name);
        B.BoundTypeTest test = new((B.BoundExpression)fixtures.Value(typeof(B.BoundExpression), false).Original!,
            S.NullTypeSymbol.Instance, S.NullTypeSymbol.Instance);
        HB.BoundTypeTest hostTest = new((HB.BoundExpression)fixtures.Value(typeof(B.BoundExpression), false).Host!,
            HS.NullTypeSymbol.Instance, HS.NullTypeSymbol.Instance);
        Assert.AreEqual((int)test.Kind, (int)hostTest.Kind);
        Assert.AreEqual(test.CaseIndex, hostTest.CaseIndex);
        Assert.AreEqual(new B.BoundMethodGroup(null, "m", [], []).ReceiverIsExtension,
            new HB.BoundMethodGroup(null, "m", [], []).ReceiverIsExtension);
        HS.ArrayTypeSymbol arrayType = new(HS.NullTypeSymbol.Instance, null);
        HB.BoundArrayCreation array = new(arrayType, null, []);
        Assert.AreSame(arrayType, array.Type);
        Assert.AreSame(arrayType, ((HB.BoundExpression)array).Type);
        Assert.IsNull(array.Length);
        Assert.IsTrue(array.Initializers.IsEmpty);
    }

    [TestMethod]
    public void UnboundMetadata_DelegatesDefaultsAndLambdaSymbolStateMatch()
    {
        S.NamespaceSymbol container = new("N", null);
        HS.NamespaceSymbol hostContainer = new("N", null);
        B.LambdaSymbol symbol = new(container, "lambda", true, true);
        HB.LambdaSymbol hostSymbol = new(hostContainer, "lambda", true, true);
        Assert.AreEqual(symbol.ReturnType.Name, hostSymbol.ReturnType.Name);
        Assert.AreEqual(symbol.Parameters.IsEmpty, hostSymbol.Parameters.IsEmpty);
        symbol.ReturnTypeValue = S.NullTypeSymbol.Instance;
        hostSymbol.ReturnTypeValue = HS.NullTypeSymbol.Instance;
        symbol.CapturesThis = true;
        hostSymbol.CapturesThis = true;
        S.ParameterSymbol parameter = new("x", S.NullTypeSymbol.Instance, 0, symbol);
        HS.ParameterSymbol hostParameter = new("x", HS.NullTypeSymbol.Instance, 0, hostSymbol);
        symbol.SetParameters([parameter]);
        hostSymbol.SetParameters([hostParameter]);
        Assert.AreSame(hostParameter, hostSymbol.Parameters.Single());
        Assert.AreSame(hostContainer, hostSymbol.ContainingSymbol);
        Assert.AreEqual(symbol.Name, hostSymbol.Name);
        Assert.AreEqual((int)symbol.MethodKind, (int)hostSymbol.MethodKind);
        Assert.AreEqual((int)symbol.DeclaredAccessibility, (int)hostSymbol.DeclaredAccessibility);
        Assert.AreEqual(symbol.IsAsync, hostSymbol.IsAsync);
        Assert.AreEqual(symbol.IsIterator, hostSymbol.IsIterator);
        Assert.AreEqual(symbol.CapturesThis, hostSymbol.CapturesThis);
        Assert.AreEqual(symbol.ReturnType.Name, hostSymbol.ReturnType.Name);
        hostSymbol.SetParameters([]);
        Assert.IsTrue(hostSymbol.Parameters.IsEmpty);

        var binds = 0;
        HB.BoundLambda lambda = new(hostSymbol, new([], []), HS.NullTypeSymbol.Instance);
        HB.LambdaInfo info = new(1, [null], (types, target, speculative) =>
        {
            Assert.AreSame(hostParameter.Type, types.Single());
            Assert.AreSame(HS.NullTypeSymbol.Instance, target);
            Assert.IsTrue(speculative);
            binds++;
            return lambda;
        }, types => types.Single());
        Assert.AreEqual(1, info.ParameterCount);
        Assert.IsNull(info.ExplicitParameterTypes.Single());
        Assert.IsNull(info.ExplicitReturnType);
        Assert.IsFalse(info.IsAsync);
        Assert.IsFalse(info.IsGenerator);
        Assert.IsFalse(info.HasExpressionBody);
        Assert.AreSame(lambda, info.Bind([hostParameter.Type], HS.NullTypeSymbol.Instance, true));
        Assert.AreSame(hostParameter.Type, info.InferReturnType([hostParameter.Type]));
        Assert.AreEqual(1, binds);
        HB.LambdaInfo failed = new(0, [], (_, _, _) => null, _ => null);
        Assert.IsNull(failed.Bind([], null, false));
        Assert.IsNull(failed.InferReturnType([]));
        HB.ObjectLiteralInfo literal = new(["a", "b"]);
        B.ObjectLiteralInfo expectedLiteral = new() { PropertyNames = ["a", "b"] };
        CollectionAssert.AreEqual(expectedLiteral.PropertyNames.ToArray(), literal.PropertyNames.ToArray());
        Assert.AreEqual(expectedLiteral.HasSpread, literal.HasSpread);
        literal.HasSpread = true;
        Assert.IsTrue(literal.HasSpread);
        HB.CollectionInfo collection = new(3);
        B.CollectionInfo expectedCollection = new() { ElementCount = 3 };
        Assert.AreEqual(expectedCollection.ElementCount, collection.ElementCount);
        Assert.AreEqual(expectedCollection.HasSpread, collection.HasSpread);
        Assert.IsNull(expectedCollection.NaturalElementType);
        Assert.IsNull(collection.NaturalElementType);
        collection.HasSpread = true;
        collection.NaturalElementType = HS.NullTypeSymbol.Instance;
        Assert.IsTrue(collection.HasSpread);
        Assert.AreSame(HS.NullTypeSymbol.Instance, collection.NaturalElementType);

        HSyntaxNode syntax = SelfHost::Caladesi.Compiler.Syntax.SyntaxTree.ParseText("const x = 1;").Root;
        HB.BoundUnbound unbound = new(syntax, target => new HB.BoundDefaultValue(target!), HS.NullTypeSymbol.Instance)
        { IsLambda = true, Lambda = info, IsObjectLiteral = true, ObjectLiteral = literal, IsCollection = true, Collection = collection };
        Assert.AreSame(syntax, unbound.SyntaxNode);
        Assert.AreSame(HS.NullTypeSymbol.Instance, unbound.BindToType(HS.NullTypeSymbol.Instance).Type);
        Assert.IsNull(new HB.BoundUnbound(syntax, _ => lambda).Type);
    }

    private static Type HostType(Type original)
    {
        return typeof(HB.BoundNode).Assembly.GetType(original.FullName!, throwOnError: true)!;
    }

    private sealed class Fixtures(int variant)
    {
        private readonly int _variant = variant;
        private readonly Dictionary<object, object> _identities = new(ReferenceEqualityComparer.Instance);
        private int _sequence;

        public (object? Original, object? Host) Value(Type type, bool nullable)
        {
            if (nullable && _variant == 1)
            {
                return (null, null);
            }

            if (Nullable.GetUnderlyingType(type) is Type underlying)
            {
                return Value(underlying, false);
            }

            if (type == typeof(bool))
            {
                return (true, true);
            }

            if (type == typeof(int))
            {
                return (7, 7);
            }

            if (type == typeof(string))
            {
                return ("name" + ++_sequence, "name" + _sequence);
            }

            if (type.IsEnum)
            {
                return (Enum.GetValues(type).GetValue(1), Enum.GetValues(HostType(type)).GetValue(1));
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            {
                var element = type.GenericTypeArguments[0];
                var hostElement = element.Assembly == typeof(B.BoundNode).Assembly ? HostType(element) : element;
                object? left = null;
                object? right = null;
                if (_variant == 0)
                {
                    (left, right) = Value(element, false);
                }

                return (MakeArray(element, left, _variant), MakeArray(hostElement, right, _variant));
            }
            object original;
            object host;
            if (type == typeof(S.TypeSymbol))
            {
                original = _variant == 0 ? new S.ErrorTypeSymbol("type" + ++_sequence) : S.NullTypeSymbol.Instance;
                host = _variant == 0 ? new HS.ErrorTypeSymbol("type" + _sequence) : HS.NullTypeSymbol.Instance;
            }
            else if (type == typeof(S.ArrayTypeSymbol))
            {
                original = new S.ArrayTypeSymbol(S.NullTypeSymbol.Instance, null);
                host = new HS.ArrayTypeSymbol(HS.NullTypeSymbol.Instance, null);
            }
            else if (type == typeof(S.ConstantValue)) { original = new S.ConstantValue(++_sequence); host = new HS.ConstantValue(_sequence); }
            else if (type == typeof(S.Symbol) || type == typeof(S.NamespaceSymbol))
            { original = new S.NamespaceSymbol("N", null); host = new HS.NamespaceSymbol("N", null); }
            else if (type == typeof(S.LocalSymbol))
            { original = new S.LocalSymbol("x", S.NullTypeSymbol.Instance, false, null); host = new HS.LocalSymbol("x", HS.NullTypeSymbol.Instance, false, null); }
            else if (type == typeof(S.ParameterSymbol))
            { original = new S.ParameterSymbol("p", S.NullTypeSymbol.Instance, 0, null); host = new HS.ParameterSymbol("p", HS.NullTypeSymbol.Instance, 0, null); }
            else if (type == typeof(S.LabelSymbol))
            { original = new S.LabelSymbol("label"); host = new HS.LabelSymbol("label"); }
            else if (type == typeof(S.MethodSymbol)) { original = new MethodFixture(); host = new HostMethodFixture(); }
            else if (type == typeof(S.FieldSymbol)) { original = new FieldFixture(true); host = new HostFieldFixture(true); }
            else if (type == typeof(S.PropertySymbol)) { original = new PropertyFixture(); host = new HostPropertyFixture(); }
            else if (type == typeof(SyntaxNode))
            {
                original = Compiler.Syntax.SyntaxTree.ParseText("const x = 1;").Root;
                host = SelfHost::Caladesi.Compiler.Syntax.SyntaxTree.ParseText("const x = 1;").Root;
            }
            else if (type == typeof(B.BoundExpression))
            { original = new B.BoundLiteral(new(++_sequence), S.NullTypeSymbol.Instance); host = new HB.BoundLiteral(new(_sequence), HS.NullTypeSymbol.Instance); }
            else if (type == typeof(B.BoundNode) || type == typeof(B.BoundStatement))
            { original = new B.BoundNoOp(); host = new HB.BoundNoOp(); }
            else if (type == typeof(B.BoundBlock))
            { original = new B.BoundBlock([], []); host = new HB.BoundBlock([], []); }
            else if (type == typeof(B.BoundConditionalReceiver))
            { original = new B.BoundConditionalReceiver(S.NullTypeSymbol.Instance); host = new HB.BoundConditionalReceiver(HS.NullTypeSymbol.Instance); }
            else if (type == typeof(B.LambdaSymbol))
            { original = new B.LambdaSymbol(new S.NamespaceSymbol("N", null), "lambda", false, false); host = new HB.LambdaSymbol(new HS.NamespaceSymbol("N", null), "lambda", false, false); }
            else if (type == typeof(B.LambdaInfo))
            {
                original = new B.LambdaInfo { ParameterCount = 0, ExplicitParameterTypes = [], Bind = (_, _, _) => null, InferReturnType = _ => null };
                host = new HB.LambdaInfo(0, [], (_, _, _) => null, _ => null);
            }
            else if (type == typeof(B.ObjectLiteralInfo))
            { original = new B.ObjectLiteralInfo { PropertyNames = ["p"] }; host = new HB.ObjectLiteralInfo(["p"]); }
            else if (type == typeof(B.CollectionInfo))
            { original = new B.CollectionInfo { ElementCount = 2 }; host = new HB.CollectionInfo(2); }
            else if (type == typeof(Func<S.TypeSymbol?, B.BoundExpression>))
            { original = new Func<S.TypeSymbol?, B.BoundExpression>(t => new B.BoundDefaultValue(t!)); host = new Func<HS.TypeSymbol?, HB.BoundExpression>(t => new HB.BoundDefaultValue(t!)); }
            else if (type.Namespace == typeof(B.BoundNode).Namespace)
            {
                var constructor = type.GetConstructors().Single();
                (object? Original, object? Host)[] args = [.. constructor.GetParameters().Select(p => Value(p.ParameterType, false))];
                original = constructor.Invoke([.. args.Select(a => a.Original)]);
                host = HostType(type).GetConstructors().Single(c => c.GetParameters().Length == args.Length)
                    .Invoke([.. args.Select(a => a.Host)]);
            }
            else
            {
                throw new AssertFailedException("Missing fixture: " + type);
            }

            _identities[original] = host;
            return (original, host);
        }

        public void CompareProperties(object original, object host)
        {
            _identities[original] = host;
            foreach (var property in original.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                PropertyInfo[] candidates = [.. host.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.Name == property.Name)];
                var migrated = candidates.Length == 1 ? candidates[0] :
                    candidates.Single(p => p.PropertyType.Name == property.PropertyType.Name);
                Assert.IsNotNull(migrated, original.GetType().Name + "." + property.Name);
                Compare(property.GetValue(original), migrated.GetValue(host), original.GetType().Name + "." + property.Name);
            }
        }

        private void Compare(object? expected, object? actual, string context)
        {
            if (expected is null) { Assert.IsNull(actual, context); return; }
            Assert.IsNotNull(actual, context);
            if (_identities.TryGetValue(expected, out var mapped)) { Assert.AreSame(mapped, actual, context); return; }
            if (expected is Enum) { Assert.AreEqual(Convert.ToInt32(expected), Convert.ToInt32(actual), context); return; }
            if (expected.GetType().IsGenericType && expected.GetType().GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            {
                var isDefault = (bool)expected.GetType().GetProperty("IsDefault")!.GetValue(expected)!;
                Assert.AreEqual(isDefault, actual.GetType().GetProperty("IsDefault")!.GetValue(actual), context);
                if (isDefault)
                {
                    return;
                }

                object?[] items = [.. ((IEnumerable)expected).Cast<object?>()];
                object?[] hostItems = [.. ((IEnumerable)actual).Cast<object?>()];
                Assert.HasCount(items.Length, hostItems, context);
                for (var i = 0; i < items.Length; i++)
                {
                    Compare(items[i], hostItems[i], context + "[" + i + "]");
                }

                return;
            }
            if (expected is S.TypeSymbol symbol) { Assert.AreEqual(symbol.Name, ((HS.TypeSymbol)actual).Name, context); return; }
            if (expected is S.ConstantValue value) { Assert.AreEqual(value.Value, ((HS.ConstantValue)actual).Value, context); return; }
            Assert.AreEqual(expected, actual, context);
        }

        private static object MakeArray(Type element, object? value, int variant)
        {
            return typeof(Fixtures).GetMethod(nameof(MakeArrayCore), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(element).Invoke(null, [value, variant])!;
        }

        private static ImmutableArray<T> MakeArrayCore<T>(object? value, int variant)
        {
            return variant == 2 ? default : variant == 1 ? [] : [(T)value!];
        }
    }

    private sealed class MethodFixture : S.MethodSymbol
    {
        public override string Name => "method";
        public override S.MethodKind MethodKind => S.MethodKind.Ordinary;
        public override S.TypeSymbol ReturnType => S.NullTypeSymbol.Instance;
        public override ImmutableArray<S.ParameterSymbol> Parameters => [];
    }
    private sealed class HostMethodFixture : HS.MethodSymbol
    {
        public override string Name => "method";
        public override HS.MethodKind MethodKind => HS.MethodKind.Ordinary;
        public override HS.TypeSymbol ReturnType => HS.NullTypeSymbol.Instance;
        public override ImmutableArray<HS.ParameterSymbol> Parameters => [];
    }
    private sealed class FieldFixture(bool isConst) : S.FieldSymbol
    {
        public override string Name => "field";
        public override S.TypeSymbol Type => S.NullTypeSymbol.Instance;
        public override bool IsConst => isConst;
        public override S.ConstantValue? ConstantValue => new S.ConstantValue(23);
    }
    private sealed class HostFieldFixture(bool isConst) : HS.FieldSymbol
    {
        public override string Name => "field";
        public override HS.TypeSymbol Type => HS.NullTypeSymbol.Instance;
        public override bool IsConst => isConst;
        public override HS.ConstantValue ConstantValue => new(23);
    }
    private sealed class PropertyFixture : S.PropertySymbol
    {
        public override string Name => "property";
        public override S.TypeSymbol Type => S.NullTypeSymbol.Instance;
        public override ImmutableArray<S.ParameterSymbol> Parameters => [];
        public override S.MethodSymbol? GetMethod => null;
        public override S.MethodSymbol? SetMethod => null;
    }
    private sealed class HostPropertyFixture : HS.PropertySymbol
    {
        public override string Name => "property";
        public override HS.TypeSymbol Type => HS.NullTypeSymbol.Instance;
        public override ImmutableArray<HS.ParameterSymbol> Parameters => [];
        public override HS.MethodSymbol? GetMethod => null;
        public override HS.MethodSymbol? SetMethod => null;
    }
}
