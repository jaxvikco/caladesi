extern alias SelfHost;

using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Tests.Emit;
using H = SelfHost::Caladesi.Compiler.Emit;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using HT = SelfHost::Caladesi.Compiler.Text;
using HX = SelfHost::Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class MethodBodyEmitterParityTests
{
    [TestMethod]
    public void WholeSelfHostLibrary_PassesILVerification()
    {
        var library = typeof(H.MethodBodyEmitter).Assembly;
        Assert.IsEmpty(ILVerifier.Verify(library.GetName().Name!, File.ReadAllBytes(library.Location)));
    }

    [TestMethod]
    public void ConstantsAndDefaults_PreserveUncheckedBitsAndUnusedElision()
    {
        Fixture f = new();
        foreach ((var type, var value) in new (SpecialType, object)[] {
            (SpecialType.Boolean, true), (SpecialType.Char, '\uffff'), (SpecialType.SByte, (sbyte)-128),
            (SpecialType.Byte, byte.MaxValue), (SpecialType.Int16, short.MinValue), (SpecialType.UInt16, ushort.MaxValue),
            (SpecialType.Int32, int.MinValue), (SpecialType.UInt32, uint.MaxValue), (SpecialType.Int64, long.MinValue),
            (SpecialType.UInt64, ulong.MaxValue), (SpecialType.Single, -0.0f), (SpecialType.Single, float.NaN),
            (SpecialType.Double, double.NegativeInfinity), (SpecialType.Double, -0.0),
            (SpecialType.IntPtr, -1), (SpecialType.UIntPtr, uint.MaxValue), (SpecialType.String, "real token \u2603"),
            (SpecialType.Decimal, -123.456m), (SpecialType.Decimal, decimal.MinValue),
            (SpecialType.BigInteger, BigInteger.One << 127), (SpecialType.Object, "boxed string") })
        {
            var target = f.Special(type);
            f.Expression(f.Literal(value, target));
            f.Expression(f.Literal(value, target), used: false);
        }
        foreach (var type in new[] { SpecialType.Boolean, SpecialType.Char, SpecialType.SByte, SpecialType.Byte,
            SpecialType.Int16, SpecialType.UInt16, SpecialType.Int32, SpecialType.UInt32, SpecialType.Int64,
            SpecialType.UInt64, SpecialType.Single, SpecialType.Double, SpecialType.IntPtr, SpecialType.UIntPtr,
            SpecialType.String, SpecialType.Object, SpecialType.Decimal, SpecialType.BigInteger })
        {
            f.Expression(f.Node("BoundDefaultValue", f.Special(type)));
        }

        f.Expression(f.Literal(null, f.Nullable(f.Special(SpecialType.Int32))));
        f.Expression(f.Literal(9, f.Nullable(f.Special(SpecialType.Int32))));
        f.Expression(f.Literal(null, f.Nullable(f.Special(SpecialType.String))));
        f.Expression(f.Literal(2, f.Type(typeof(DayOfWeek))));
        f.Compare();
    }

    [TestMethod]
    public void NumericConversions_AllSignedUnsignedFloatAndNativePairsMatch()
    {
        foreach (var from in NumericTypes)
        {
            Fixture f = new();
            var parameter = f.Parameter("source", f.Special(from));
            foreach (var to in NumericTypes)
            {
                var conversion = f.Node("BoundConversion", f.Node("BoundParameter", parameter),
                    (int)ConversionKind.ExplicitNumeric, f.Special(to));
                f.Expression(conversion);
                f.Expression(conversion, used: false);
            }
            var enumeration = f.Type(typeof(DayOfWeek));
            f.Expression(f.Node("BoundConversion", f.Node("BoundParameter", parameter), (int)ConversionKind.EnumNumeric, enumeration));
            f.Compare();
        }
        Fixture enums = new();
        enums.Expression(enums.Node("BoundConversion", enums.Literal(2, enums.Type(typeof(DayOfWeek))),
            (int)ConversionKind.EnumNumeric, enums.Special(SpecialType.Int64)));
        enums.Compare();
    }

    [TestMethod]
    public void DecimalConversions_PreserveDirectOperatorsAndInt32IntermediatePaths()
    {
        Fixture f = new();
        var decimalType = f.Special(SpecialType.Decimal);
        var decimalValue = f.Node("BoundParameter", f.Parameter("decimalValue", decimalType));
        foreach (var special in NumericTypes.Where(type => type is not SpecialType.IntPtr and not SpecialType.UIntPtr))
        {
            var target = f.Special(special);
            var value = f.Node("BoundParameter", f.Parameter("value", target));
            f.Expression(f.Node("BoundConversion", value, (int)ConversionKind.ExplicitNumeric, decimalType));
            f.Expression(f.Node("BoundConversion", decimalValue, (int)ConversionKind.ExplicitNumeric, target), used: false);
        }
        f.Compare();
    }

    [TestMethod]
    public void Operators_ShiftsNullComparisonsAndBranchPredicatesMatch()
    {
        foreach (var special in new[] { SpecialType.Int32, SpecialType.UInt32, SpecialType.Int64, SpecialType.UInt64,
            SpecialType.Single, SpecialType.Double, SpecialType.Char, SpecialType.Boolean })
        {
            Fixture f = new();
            var type = f.Special(special);
            var left = f.Node("BoundParameter", f.Parameter("left", type));
            var right = f.Node("BoundParameter", f.Parameter("right", type));
            foreach (var kind in Enum.GetValues<BinaryOperatorKind>().Where(kind => kind != BinaryOperatorKind.UserDefined))
            {
                var binary = f.Binary(kind, left, right, type);
                f.Expression(binary);
                f.Expression(binary, used: false);
                if (kind >= BinaryOperatorKind.Equal)
                {
                    f.Statement(f.Node("BoundIf", binary, f.Node("BoundNoOp"), f.Node("BoundNoOp")));
                }
            }
            foreach (var count in new[] { -129, -1, 0, 31, 32, 63, 64, 127 })
            {
                f.Expression(f.Binary(BinaryOperatorKind.ShiftLeft, left, f.Integer(count), type));
            }

            foreach (var kind in new[] { UnaryOperatorKind.Plus, UnaryOperatorKind.Minus, UnaryOperatorKind.Not, UnaryOperatorKind.BitwiseNot })
            {
                f.Expression(f.Node("BoundUnary", (int)kind, left, type), used: false);
            }

            f.Compare();
        }
        Fixture nulls = new();
        foreach (var type in new[] { nulls.Special(SpecialType.String), nulls.Nullable(nulls.Special(SpecialType.Int32)), nulls.TypeParameter() })
        {
            var subject = nulls.Node("BoundParameter", nulls.Parameter("nullable", type));
            foreach (var kind in new[] { BinaryOperatorKind.Equal, BinaryOperatorKind.NotEqual })
            {
                nulls.Expression(nulls.Binary(kind, subject, nulls.Literal(null, type), type));
                nulls.Expression(nulls.Binary(kind, nulls.Literal(null, type), subject, type), used: false);
            }
            nulls.Expression(nulls.Node("BoundIsNull", subject, nulls.Special(SpecialType.Boolean)));
        }
        nulls.Expression(nulls.Node("BoundIsNull", nulls.Integer(1), nulls.Special(SpecialType.Boolean)), used: false);
        nulls.Compare();
    }

    [TestMethod]
    public void ConditionalLogicalCoalesceAndNullableConversionsMatch()
    {
        Fixture f = new();
        var integer = f.Special(SpecialType.Int32);
        var nullable = f.Nullable(integer);
        var boolean = f.Special(SpecialType.Boolean);
        var condition = f.Node("BoundParameter", f.Parameter("condition", boolean));
        var second = f.Node("BoundParameter", f.Parameter("second", boolean));
        var optional = f.Node("BoundParameter", f.Parameter("optional", nullable));
        foreach (var used in new[] { false, true })
        {
            f.Expression(f.Node("BoundConditional", condition, f.Integer(10), f.Integer(20), integer), used);
            foreach (var and in new[] { false, true })
            {
                var logical = f.Node("BoundLogicalBinary", and, condition, second, boolean);
                f.Expression(logical, used);
                f.Statement(f.Node("BoundIf", logical, f.Node("BoundNoOp"), f.Node("BoundNoOp")));
                f.Statement(f.Node("BoundIf", f.Node("BoundUnary", (int)UnaryOperatorKind.Not, logical, boolean),
                    f.Node("BoundNoOp"), null));
            }
            foreach (var constant in new[] { false, true })
            {
                f.Statement(f.Node("BoundIf", f.Literal(constant, boolean), f.Node("BoundNoOp"), f.Node("BoundNoOp")));
            }

            f.Expression(f.Node("BoundCoalesce", optional, f.Integer(42), integer), used);
            f.Expression(f.Node("BoundCoalesce", optional, f.Node("BoundDefaultValue", nullable), nullable), used);
            var text = f.Special(SpecialType.String);
            f.Expression(f.Node("BoundCoalesce", f.Literal(null, f.Nullable(text)), f.Literal("fallback", text), text), used);
            var generic = f.TypeParameter();
            f.Expression(f.Node("BoundCoalesce", f.Node("BoundDefaultValue", generic), f.Node("BoundDefaultValue", generic), generic), used);
            f.Expression(f.Node("BoundConversion", f.Integer(7), (int)ConversionKind.ToNullable, nullable), used);
            f.Expression(f.Node("BoundConversion", optional, (int)ConversionKind.FromNullable, integer), used);
            f.Expression(f.Node("BoundConversion", optional, (int)ConversionKind.LiftedNumeric,
                f.Nullable(f.Special(SpecialType.Double))), used);
            foreach (var receiver in new[] { optional, f.Literal(null, f.Nullable(text)) })
            {
                var placeholder = f.Node("BoundConditionalReceiver", ((BoundExpression)receiver.Expected).Type is NullableTypeSymbol n && n.Underlying.IsValueType
                    ? integer : text);
                f.Expression(f.Node("BoundConditionalAccess", receiver, placeholder, placeholder,
                    receiver == optional ? nullable : f.Nullable(text)), used);
                f.Expression(f.Node("BoundConditionalAccess", receiver, placeholder,
                    f.Call(f.Member(f.Type(typeof(GC)), "KeepAlive"), null, placeholder), null), used);
            }
        }
        foreach (var kind in new[] { ConversionKind.Identity, ConversionKind.TypeOnly,
            ConversionKind.ImplicitReference, ConversionKind.TypeParameter, ConversionKind.NullLiteral })
        {
            f.Expression(f.Node("BoundConversion", f.Integer(1), (int)kind, integer), used: false);
        }

        f.Compare();
    }

    [TestMethod]
    public void CallsFieldsPropertiesAddressesAndAssignmentsUseMatchingRuntimeMetadata()
    {
        Fixture f = new();
        var owner = f.Type(typeof(RuntimeMethods));
        var receiver = f.Node("BoundParameter", f.Parameter("receiver", owner));
        var integer = f.Special(SpecialType.Int32);
        var local = f.Local("value", integer);
        var argument = f.Parameter("argument", integer);
        var arrayType = f.ArrayType(integer);
        var array = f.Node("BoundParameter", f.Parameter("array", arrayType));
        var field = f.Node("BoundFieldAccess", receiver, f.Member(owner, nameof(RuntimeMethods.Field)));
        var staticField = f.Node("BoundFieldAccess", null, f.Member(owner, nameof(RuntimeMethods.StaticField)));
        var property = f.Node("BoundPropertyAccess", receiver, f.Member(owner, nameof(RuntimeMethods.Property)), f.Expressions());
        var indexer = f.Node("BoundPropertyAccess", receiver, f.Member(owner, "Item"), f.Expressions(f.Integer(0)));
        foreach (var target in new[] { f.Node("BoundLocal", local), f.Node("BoundParameter", argument), field, staticField,
            property, indexer, f.Node("BoundArrayAccess", array, f.Integer(0), integer) })
        {
            f.Expression(target);
            f.Expression(target, used: false);
            f.Expression(f.Node("BoundAddressOf", target));
            f.Expression(f.Node("BoundAddressOf", target), used: false);
            f.Expression(f.Node("BoundAssignment", target, f.Integer(5)));
            f.Expression(f.Node("BoundAssignment", target, f.Integer(6)), used: false);
            f.Expression(f.With(f.Node("BoundAssignment", target, f.Integer(7)), "IsDiscarded", true));
        }
        foreach (var name in new[] { nameof(RuntimeMethods.Static), nameof(RuntimeMethods.Instance),
            nameof(RuntimeMethods.Virtual), nameof(RuntimeMethods.RefReturn), nameof(RuntimeMethods.Never) })
        {
            var method = f.Member(owner, name);
            Pair[] args = name == nameof(RuntimeMethods.RefReturn) ? [f.Node("BoundAddressOf", f.Node("BoundLocal", local))] : [];
            var call = f.Call(method, ((MethodSymbol)method.Expected).IsStatic ? null : receiver, args);
            f.Expression(call);
            f.Expression(call, used: false);
            if (!((MethodSymbol)method.Expected).IsStatic)
            {
                f.Expression(f.With(f.Call(method, receiver, args), "IsBaseCall", true));
            }
        }
        f.Expression(f.Call(f.Member(owner, nameof(RuntimeMethods.Instance)), null));
        f.Expression(f.Call(f.Member(f.Type(typeof(int)), "ToString", 0), f.Integer(4)));
        f.Expression(f.Call(f.Member(f.Type(typeof(object)), "ToString", 0), f.Integer(4)));
        var generic = f.TypeParameter();
        f.Expression(f.Call(f.Member(f.Type(typeof(object)), "ToString", 0), f.Node("BoundDefaultValue", generic)));
        f.Expression(f.Node("BoundTypeTest", f.Integer(1), owner, f.Special(SpecialType.Boolean)), used: false);
        f.Expression(f.Node("BoundTypeOf", arrayType, f.Special(SpecialType.Type)), used: false);
        f.Compare();
    }

    [TestMethod]
    public void StructThisConstructorsInitializersAndFunctionPointersMatch()
    {
        foreach (var valueType in new[] { false, true })
        {
            Fixture f = new(isStatic: false, valueType: valueType);
            var owner = f.Type(valueType ? typeof(RuntimeValue) : typeof(RuntimeMethods));
            f.Expression(f.Node("BoundThis", f.Owner));
            f.Expression(f.Node("BoundThis", f.Owner), used: false);
            f.Expression(f.Node("BoundAddressOf", f.Node("BoundThis", f.Owner)));
            f.Expression(f.Node("BoundBaseReference", f.Owner));
            var constructor = f.Constructor(owner, 1);
            var initializers = f.Array("Bound.BoundMemberInit",
                f.Node("BoundMemberInit", f.Member(owner, "Field"), f.Integer(8)),
                f.Node("BoundMemberInit", f.Member(owner, "Property"), f.Integer(9)));
            foreach (var used in new[] { false, true })
            {
                f.Expression(f.Node("BoundObjectCreation", constructor, owner, f.Expressions(f.Integer(3)), initializers), used);
                f.Expression(f.Node("BoundObjectCreation", constructor, owner, f.Expressions(f.Integer(3)), f.Array("Bound.BoundMemberInit")), used);
                if (valueType)
                {
                    f.Expression(f.Node("BoundObjectCreation", null, owner, f.Expressions(), initializers), used);
                    f.Expression(f.Node("BoundObjectCreation", null, owner, f.Expressions(), f.Array("Bound.BoundMemberInit")), used);
                }
            }
            f.Compare();
        }
        Fixture delegates = new();
        var runtime = delegates.Type(typeof(RuntimeMethods));
        var receiver = delegates.Node("BoundParameter", delegates.Parameter("target", runtime));
        var delegateType = delegates.Construct(delegates.Type(typeof(Func<>)), delegates.Special(SpecialType.Int32));
        foreach (var name in new[] { nameof(RuntimeMethods.Static), nameof(RuntimeMethods.Instance), nameof(RuntimeMethods.Virtual), nameof(RuntimeMethods.Sealed) })
        {
            var method = delegates.Member(runtime, name);
            foreach (var used in new[] { false, true })
            {
                delegates.Expression(delegates.Node("BoundDelegateCreation",
                    name == nameof(RuntimeMethods.Static) ? null : receiver, method, delegateType), used);
            }
        }
        var generic = delegates.TypeParameter();
        delegates.Expression(delegates.Node("BoundObjectCreation", null, generic, delegates.Expressions(), delegates.Array("Bound.BoundMemberInit")));
        delegates.Compare();
    }

    [TestMethod]
    public void Arrays_AllElementOpcodesAndTemporaryAddressSpillsMatch()
    {
        Fixture f = new();
        foreach (var element in NumericTypes.Select(f.Special).Concat([
            f.Special(SpecialType.Boolean), f.Special(SpecialType.String), f.Special(SpecialType.Decimal),
            f.Type(typeof(DayOfWeek)), f.TypeParameter()]))
        {
            var array = f.Node("BoundArrayCreation", f.ArrayType(element), null,
                f.Expressions(f.Node("BoundDefaultValue", element), f.Node("BoundDefaultValue", element)));
            f.Expression(array);
            f.Expression(array, used: false);
            f.Expression(f.Node("BoundArrayCreation", f.ArrayType(element), f.Integer(4), f.Expressions()), used: false);
            var access = f.Node("BoundArrayAccess", array, f.Integer(0), element);
            f.Expression(access);
            f.Expression(f.Node("BoundAddressOf", access));
            f.Expression(f.Node("BoundAssignment", access, f.Node("BoundDefaultValue", element)));
            f.Expression(f.Node("BoundAssignment", access, f.Node("BoundDefaultValue", element)), used: false);
        }
        f.Compare();
    }

    [TestMethod]
    public void BoxingReferenceUserDefinedAndUnionConversionsMatch()
    {
        Fixture f = new();
        var integer = f.Special(SpecialType.Int32);
        var obj = f.Special(SpecialType.Object);
        var generic = f.TypeParameter();
        foreach (var type in new[] { integer, f.Special(SpecialType.String), generic, f.Nullable(integer) })
        {
            var subject = f.Node("BoundDefaultValue", type);
            f.Expression(f.Node("BoundConversion", subject, (int)ConversionKind.Boxing, obj));
            f.Expression(f.Node("BoundConversion", subject, (int)ConversionKind.ExplicitReference, obj));
            f.Expression(f.Node("BoundConversion", f.Literal(null, obj), (int)ConversionKind.ExplicitReference, type), used: false);
            f.Expression(f.Node("BoundConversion", f.Literal(null, obj), (int)ConversionKind.Unboxing, type));
        }
        var conversion = f.Member(f.Type(typeof(RuntimeMethods)), "op_Implicit");
        f.Expression(f.Node("BoundConversion", f.Integer(1), (int)ConversionKind.UserDefined,
            f.Type(typeof(RuntimeMethods)), conversion), used: false);
        var union = f.Union(integer, f.Special(SpecialType.String));
        var wider = f.Union(f.Special(SpecialType.Double), f.Special(SpecialType.String), integer);
        var wrap = f.With(f.Node("BoundConversion", f.Integer(2), (int)ConversionKind.UnionWrap, union), "CaseIndex", 0);
        f.Expression(wrap);
        f.Expression(f.With(f.Node("BoundConversion", f.Integer(2), (int)ConversionKind.UnionWrap, wider), "CaseIndex", 0));
        f.Expression(f.With(f.Node("BoundConversion", wrap, (int)ConversionKind.UnionWrap, wider), "CaseIndex", -3), used: false);
        f.Expression(f.Node("BoundConversion", wrap, (int)ConversionKind.UnionWrap, wider));
        f.Expression(f.Node("BoundConversion", wrap, (int)ConversionKind.UnionExtract, integer), used: false);
        f.Expression(f.Node("BoundConversion", wrap, (int)ConversionKind.UnionExtract, f.Special(SpecialType.String)));
        f.Expression(f.Node("BoundTypeTest", wrap, integer, f.Special(SpecialType.Boolean), (int)TypeTestKind.UnionCase, 0));
        f.Compare();
    }

    [TestMethod]
    public void NestedConditionalReceiversAndUnionReferenceWideningPreserveOriginalRouting()
    {
        Fixture f = new();
        var text = f.Special(SpecialType.String);
        var optional = f.Nullable(text);
        var outer = f.Node("BoundConditionalReceiver", text);
        var inner = f.Node("BoundConditionalReceiver", text);
        var nested = f.Node("BoundConditionalAccess", outer, inner, inner, optional);
        var receiver = f.Node("BoundParameter", f.Parameter("outer", optional));
        f.Expression(f.Node("BoundConditionalAccess", receiver, outer, nested, optional));
        // The original retains placeholder mappings after nested emission.
        f.Expression(outer);
        f.Expression(inner, used: false);
        var small = f.Union(text, f.Special(SpecialType.Int32));
        var large = f.Union(f.Special(SpecialType.Object), f.Special(SpecialType.Int64));
        var wrap = f.With(f.Node("BoundConversion", f.Literal("case", text), (int)ConversionKind.UnionWrap, small), "CaseIndex", 0);
        f.Expression(f.Node("BoundConversion", wrap, (int)ConversionKind.UnionWrap, large));
        f.Expression(f.With(f.Node("BoundConversion", f.Node("BoundDefaultValue", small),
            (int)ConversionKind.UnionWrap, large), "CaseIndex", -3), used: false);
        f.Compare();
    }

    [TestMethod]
    public void StatementsLoopsProtectedReturnsAndSequencePointsMatchIncludingExceptionRegions()
    {
        foreach (var returnsValue in new[] { false, true })
        {
            Fixture f = new(returnsValue: returnsValue, source: true);
            var integer = f.Special(SpecialType.Int32);
            var local = f.Local("named", integer);
            var hidden = f.Local("hidden", integer, synthesized: true);
            var outer = f.Label("outer");
            var inner = f.Label("inner");
            var nested = f.Label("nested");
            var condition = f.Binary(BinaryOperatorKind.LessThan,
                f.Node("BoundLocal", local), f.Integer(10), integer);
            var returnNode = f.Node("BoundReturn", returnsValue ? f.Node("BoundLocal", local) : null);
            var catchLocal = f.Local("error", f.Special(SpecialType.Exception));
            var handlers = f.Array("Bound.BoundCatch",
                f.Node("BoundCatch", catchLocal, f.Special(SpecialType.Exception), f.Block(f.Node("BoundThrow", (object?)null))),
                f.Node("BoundCatch", null, f.Special(SpecialType.Exception), f.Block(f.Node("BoundGoto", outer))));
            var protectedBody = f.Block(
                f.Node("BoundGoto", nested),
                f.Node("BoundTry", f.Block(f.Node("BoundLabel", nested),
                    f.Node("BoundConditionalGoto", condition, inner, true),
                    f.Node("BoundGoto", outer), f.Node("BoundNoOp")),
                    f.Array("Bound.BoundCatch"), null),
                f.Node("BoundLabel", inner),
                f.Node("BoundConditionalGoto", condition, inner, true),
                f.Node("BoundConditionalGoto", condition, outer, false),
                returnNode);
            var region = f.Node("BoundTry", protectedBody, handlers, f.Block(f.Node("BoundNoOp")));
            var assignment = f.StatementNode(f.Node("BoundAssignment", f.Node("BoundLocal", local), f.Integer(1)));
            Pair[] loops = [
                f.Node("BoundWhile", condition, assignment, f.Label("break"), f.Label("continue")),
                f.Node("BoundDoWhile", condition, assignment, f.Label("break"), f.Label("continue")),
                f.Node("BoundFor", f.Array("Symbols.LocalSymbol", hidden), assignment, condition, assignment, assignment, f.Label("break"), f.Label("continue")),
                f.Node("BoundFor", f.Array("Symbols.LocalSymbol"), null, null, null, f.Node("BoundNoOp"), f.Label("break"), f.Label("continue"))];
            var sequence = f.Node("BoundSequence", f.Array("Symbols.LocalSymbol", hidden),
                f.Array("Bound.BoundStatement", assignment), f.Node("BoundLocal", local));
            var body = f.Block([local],
                f.With(f.Node("BoundLocalDeclaration", local, f.Integer(0)), "Syntax", f.Syntax!),
                f.Node("BoundLocalDeclaration", hidden, null),
                f.Node("BoundStatementList", f.Array("Bound.BoundStatement", loops)),
                f.With(f.StatementNode(sequence), "Syntax", f.Syntax!),
                f.Node("BoundSequencePoint", region, f.Syntax!),
                f.Node("BoundLabel", outer),
                f.Node("BoundTry", f.Block(f.Node("BoundNoOp")), f.Array("Bound.BoundCatch",
                    f.Node("BoundCatch", null, f.Special(SpecialType.Exception), f.Block(f.Node("BoundNoOp")))), null),
                returnNode);
            f.Body(body);
            f.Compare();
            Assert.IsGreaterThan(0, f.Actual.SequencePoints.Count);
            Assert.IsGreaterThan(0, f.Actual.Locals.Count);
        }
    }

    [TestMethod]
    public void UnloweredAndInvalidNodes_PreserveDiagnosticExceptions()
    {
        foreach (var node in new[] { "BoundYieldReturn", "BoundYieldBreak" })
        {
            Fixture f = new();
            f.Failure("EmitBody", f.Block(node == "BoundYieldReturn" ? f.Node(node, f.Integer(1)) : f.Node(node)));
        }
        foreach (var node in new[] { "BoundBadExpression", "BoundTypeExpression", "BoundNamespaceExpression" })
        {
            Fixture f = new();
            var invalid = node switch
            {
                "BoundBadExpression" => f.Node(node, f.Array("Bound.BoundNode")),
                "BoundTypeExpression" => f.Node(node, f.Special(SpecialType.Int32)),
                _ => f.Node(node, new Pair(new NamespaceSymbol("", null), new HS.NamespaceSymbol("", null)))
            };
            f.Failure("EmitExpression", invalid, true);
        }
        Fixture binary = new();
        binary.Failure("EmitExpression", binary.Binary(BinaryOperatorKind.UserDefined, binary.Integer(1), binary.Integer(2),
            binary.Special(SpecialType.Int32)), true);
    }

    private static readonly SpecialType[] NumericTypes = [
        SpecialType.SByte, SpecialType.Byte, SpecialType.Int16, SpecialType.UInt16, SpecialType.Char,
        SpecialType.Int32, SpecialType.UInt32, SpecialType.Int64, SpecialType.UInt64,
        SpecialType.Single, SpecialType.Double, SpecialType.IntPtr, SpecialType.UIntPtr];
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private sealed record Pair(object Expected, object Actual);

    private static object? Invoke(object target, string name, params object?[] args)
    {
        return target.GetType().GetMethod(name, Instance)!.Invoke(target, args);
    }

    private static T Get<T>(object target, string name)
    {
        return (T)target.GetType().GetProperty(name, Instance)!.GetValue(target)!;
    }

#pragma warning disable CA1822 // Fluent paired-node factory keeps test trees readable.
    private sealed class Fixture
    {
        private static readonly string[] Paths = [.. TestReferences.Paths, typeof(RuntimeMethods).Assembly.Location, typeof(Runtime.Union<,>).Assembly.Location];
        private static readonly Lazy<ReferenceSet> OriginalReferences = new(() => ReferenceSet.Create(Paths));
        private static readonly Lazy<HM.ReferenceSet> SelfReferences = new(() => HM.ReferenceSet.Create(Paths));
        private static readonly Type ModuleType = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.EmitModule", true)!;
        private static readonly Type EmitterType = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.MethodBodyEmitter", true)!;
        private readonly Compilation compilation = Compilation.Create(new CompilationOptions(), [], OriginalReferences.Value);
        private readonly object module;
        private readonly object emitter;
        private readonly object il = Activator.CreateInstance(typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.ILBuilder", true)!)!;
        private readonly H.MetadataEmitter metadata;
        private readonly Pair method;
        private int parameterOrdinal;
        private readonly int typeId = 1;
        public H.ILBuilder Actual { get; } = new();
        public Pair Owner { get; }
        public Pair? Syntax { get; }
        private H.MethodBodyEmitter Emitter { get; }
        private MetadataBuilder OriginalMetadata => Get<MetadataBuilder>(module, "Metadata");

        public Fixture(bool isStatic = true, bool valueType = false, bool returnsValue = true, bool source = false)
        {
            module = Activator.CreateInstance(ModuleType, compilation)!;
            metadata = new(special => SelfReferences.Value.GetSpecialType(special)!, name => SelfReferences.Value.FindType(name));
            Owner = Create("Symbols.Source.SynthesizedNamedType", "Methods", null, (int)(valueType ? TypeKind.Struct : TypeKind.Class));
            method = Create("Symbols.Source.SynthesizedMethodSymbol", Owner, "M", (int)MethodKind.Ordinary,
                (int)Accessibility.Public, isStatic, Special(returnsValue ? SpecialType.Int32 : SpecialType.Void), Array("Symbols.ParameterSymbol"));
            SourceFile? file = null;
            HT.SourceText? text = null;
            if (source)
            {
                const string code = "\r\nlet value = 42;\nvalue;\n";
                var tree = SyntaxTree.ParseText(code, "method.cala");
                var other = HX.SyntaxTree.ParseText(code, "method.cala");
                file = new SourceFile(compilation, tree);
                text = other.Text;
                Syntax = new(tree.Root.Statements[0], other.Root.Statements.Get(0));
            }
            emitter = Activator.CreateInstance(EmitterType, Instance, null, [module, method.Expected, il, file], null)!;
            Emitter = new(metadata, (HS.MethodSymbol)method.Actual, Actual,
                special => SelfReferences.Value.GetSpecialType(special)!, name => SelfReferences.Value.FindType(name), text);
            foreach (var builder in new[] { OriginalMetadata, metadata.Metadata })
            {
                _ = builder.AddModule(0, builder.GetOrAddString("MethodBodyParity"),
                    builder.GetOrAddGuid(new Guid("2f2c5e5e-2de4-4f4b-847f-e4163666e733")), default, default);
            }

            Register("RegisterTypeDefinition", Owner, MetadataTokens.TypeDefinitionHandle(typeId++));
        }

        public Pair Special(SpecialType special)
        {
            return new(compilation.GetSpecialType(special),
            SelfReferences.Value.GetSpecialType((HS.SpecialType)(int)special)!);
        }

        public Pair Type(Type type)
        {
            return new(OriginalReferences.Value.FindType(type.FullName!)!, SelfReferences.Value.FindType(type.FullName!)!);
        }

        public Pair Nullable(Pair type)
        {
            return Create("Symbols.NullableTypeSymbol", type);
        }

        public Pair ArrayType(Pair type)
        {
            return Create("Symbols.ArrayTypeSymbol", type, null, 1);
        }

        public Pair Construct(Pair definition, params Pair[] args)
        {
            return new(
                    ((NamedTypeSymbol)definition.Expected).Construct(args.Select(arg => (TypeSymbol)arg.Expected).ToImmutableArray()),
                    ((HS.NamedTypeSymbol)definition.Actual).Construct(args.Select(arg => (HS.TypeSymbol)arg.Actual).ToImmutableArray()));
        }

        public Pair Member(Pair owner, string name, int? count = null)
        {
            return new(
                    ((NamedTypeSymbol)owner.Expected).GetMembers(name).Single(member => count == null || (member is MethodSymbol m && m.Parameters.Length == count)),
                    ((HS.NamedTypeSymbol)owner.Actual).GetMembers(name).Single(member => count == null || (member is HS.MethodSymbol m && m.Parameters.Length == count)));
        }

        public Pair Constructor(Pair owner, int count)
        {
            return new(
                    ((NamedTypeSymbol)owner.Expected).InstanceConstructors.Single(ctor => ctor.Parameters.Length == count),
                    ((HS.NamedTypeSymbol)owner.Actual).InstanceConstructors.Single(ctor => ctor.Parameters.Length == count));
        }

        public Pair TypeParameter()
        {
            return Create("Symbols.TypeParameterSymbol", "T", 0, method, (int)VarianceKind.None);
        }

        public Pair Local(string name, Pair type, bool synthesized = false)
        {
            var result = Create("Symbols.LocalSymbol", name, type, false, method);
            return With(result, "IsSynthesized", synthesized);
        }
        public Pair Label(string name)
        {
            return Create("Symbols.LabelSymbol", name);
        }

        public Pair Parameter(string name, Pair type)
        {
            var result = Create("Symbols.ParameterSymbol", name, type, parameterOrdinal++, method);
            var expected = (IDictionary)emitter.GetType().GetField("_paramIndex", Instance)!.GetValue(emitter)!;
            var actual = (IDictionary)Emitter.GetType().GetField("paramIndex", Instance)!.GetValue(Emitter)!;
            var offset = ((MethodSymbol)method.Expected).IsStatic ? 0 : 1;
            expected.Add(result.Expected, parameterOrdinal - 1 + offset);
            actual.Add(result.Actual, parameterOrdinal - 1 + offset);
            return result;
        }
        public Pair Literal(object? value, Pair type)
        {
            return Node("BoundLiteral", Create("Symbols.ConstantValue", value), type);
        }

        public Pair Integer(int value)
        {
            return Literal(value, Special(SpecialType.Int32));
        }

        public Pair Binary(BinaryOperatorKind kind, Pair left, Pair right, Pair type)
        {
            return Node("BoundBinary", (int)kind, left, right, kind >= BinaryOperatorKind.Equal ? Special(SpecialType.Boolean) : type, type);
        }

        public Pair Expressions(params Pair[] items)
        {
            return Array("Bound.BoundExpression", items);
        }

        public Pair Node(string name, params object?[] args)
        {
            return Create("Bound." + name, args);
        }

        public Pair StatementNode(Pair expression)
        {
            return Node("BoundExpressionStatement", expression);
        }

        public Pair Call(Pair methodSymbol, Pair? receiver, params Pair[] args)
        {
            return Node("BoundCall", receiver, methodSymbol, Expressions(args));
        }

        public Pair Block(params Pair[] statements)
        {
            return Block([], statements);
        }

        public Pair Block(Pair[] locals, params Pair[] statements)
        {
            return Node("BoundBlock", Array("Symbols.LocalSymbol", locals), Array("Bound.BoundStatement", statements));
        }

        public Pair With(Pair pair, string name, object value)
        {
            pair.Expected.GetType().GetProperty(name)!.SetValue(pair.Expected, value is Pair p ? p.Expected : value);
            pair.Actual.GetType().GetProperty(name)!.SetValue(pair.Actual, value is Pair q ? q.Actual : value);
            return pair;
        }
        public Pair Union(params Pair[] cases)
        {
            return Construct(Type(cases.Length == 2 ? typeof(Runtime.Union<,>) : typeof(Runtime.Union<,,>)), cases);
        }
        private void Register(string name, Pair symbol, object handle)
        {
            _ = Invoke(module, name, symbol.Expected, handle); _ = Invoke(metadata, name, symbol.Actual, handle);
        }
        public void Expression(Pair expression, bool used = true)
        {
            _ = Invoke(emitter, "EmitExpression", expression.Expected, used);
            Emitter.EmitExpression((HB.BoundExpression)expression.Actual, used);
        }
        public void Statement(Pair statement)
        {
            _ = Invoke(emitter, "EmitStatement", statement.Expected);
            _ = Invoke(Emitter, "EmitStatement", statement.Actual);
        }
        public void Body(Pair body)
        {
            _ = Invoke(emitter, "EmitBody", body.Expected);
            Emitter.EmitBody((HB.BoundBlock)body.Actual);
        }
        public void Failure(string name, Pair node, params object[] rest)
        {
            var expected = Assert.ThrowsExactly<TargetInvocationException>(() => Invoke(emitter, name, [node.Expected, .. rest]));
            var actual = Assert.ThrowsExactly<TargetInvocationException>(() => Invoke(Emitter, name, [node.Actual, .. rest]));
            Assert.AreEqual(expected.InnerException!.GetType(), actual.InnerException!.GetType());
            Assert.AreEqual(expected.InnerException.Message, actual.InnerException.Message);
        }
        public void Compare()
        {
            CollectionAssert.AreEqual(Get<BlobBuilder>(il, "Code").ToArray(), Actual.Code.ToArray(), "Raw IL and metadata tokens");
            Assert.AreEqual(Get<int>(il, "MaxStack"), Actual.MaxStack, "Max stack");
            Assert.AreEqual(Get<int>(il, "CurrentDepth"), Actual.CurrentDepth, "Current stack");
            var locals = Get<IList>(il, "Locals");
            Assert.HasCount(locals.Count, Actual.Locals);
            for (var i = 0; i < locals.Count; i++)
            {
                Assert.AreEqual(Get<int>(locals[i]!, "Index"), Actual.Locals[i].Index);
                Assert.AreEqual(Get<string?>(locals[i]!, "Name"), Actual.Locals[i].Name);
                var expected = Get<TypeSymbol>(locals[i]!, "Type");
                Assert.AreEqual(expected.ToDisplayString(), Actual.Locals[i].Type.ToDisplayString());
                BlobBuilder left = new(), right = new();
                _ = Invoke(module, "EncodeType", new SignatureTypeEncoder(left), expected);
                metadata.EncodeType(new SignatureTypeEncoder(right), Actual.Locals[i].Type);
                CollectionAssert.AreEqual(left.ToArray(), right.ToArray(), "Local type signature");
            }
            var points = Get<IList>(il, "SequencePoints");
            Assert.HasCount(points.Count, Actual.SequencePoints);
            for (var i = 0; i < points.Count; i++)
            {
                foreach (var property in new[] { "Offset", "StartLine", "StartColumn", "EndLine", "EndColumn", "IsHidden" })
                {
                    Assert.AreEqual(Get<object>(points[i]!, property), Get<object>(Actual.SequencePoints[i], property), property);
                }
            }

            BlobBuilder expectedBody = new(), actualBody = new();
            _ = new MethodBodyStreamEncoder(expectedBody).AddMethodBody(Get<InstructionEncoder>(il, "Encoder"), Get<int>(il, "MaxStack"));
            _ = new MethodBodyStreamEncoder(actualBody).AddMethodBody(Actual.Encoder, Actual.MaxStack);
            CollectionAssert.AreEqual(expectedBody.ToArray(), actualBody.ToArray(), "Fixed branches, method header, IL, and exception regions");
            CollectionAssert.AreEqual(MetadataBytes(OriginalMetadata), MetadataBytes(metadata.Metadata), "Real metadata scopes, strings, member refs and signatures");
        }
        private static byte[] MetadataBytes(MetadataBuilder builder)
        {
            BlobBuilder bytes = new();
            new MetadataRootBuilder(builder).Serialize(bytes, 0, 0);
            return bytes.ToArray();
        }
        private static Pair Create(string name, params object?[] args)
        {
            object Build(bool self)
            {
                var assembly = self ? typeof(HB.BoundNode).Assembly : typeof(BoundNode).Assembly;
                var type = assembly.GetType("Caladesi.Compiler." + name, true)!;
                var ctor = type.GetConstructors()
                    .Where(candidate => candidate.GetParameters().Length >= args.Length
                        && candidate.GetParameters().Skip(args.Length).All(parameter => parameter.HasDefaultValue))
                    .OrderBy(candidate => candidate.GetParameters().Length).First();
                object?[] values = [.. ctor.GetParameters().Select((parameter, index) =>
                {
                    var value = index < args.Length ? args[index] : parameter.DefaultValue;
                    if (value is Pair pair)
                    {
                        value = self ? pair.Actual : pair.Expected;
                    }

                    if (value != null && parameter.ParameterType.IsEnum)
                    {
                        value = Enum.ToObject(parameter.ParameterType, value);
                    }

                    return value;
                })];
                return ctor.Invoke(values);
            }
            return new(Build(false), Build(true));
        }
        public Pair Array(string name, params Pair[] items)
        {
            object Build(bool self)
            {
                var assembly = self ? typeof(HB.BoundNode).Assembly : typeof(BoundNode).Assembly;
                var type = assembly.GetType("Caladesi.Compiler." + name, true)!;
                var values = System.Array.CreateInstance(type, items.Length);
                for (var i = 0; i < items.Length; i++)
                {
                    values.SetValue(self ? items[i].Actual : items[i].Expected, i);
                }

                var create = typeof(ImmutableArray).GetMethods().Single(candidate => candidate.Name == "Create"
                    && candidate.IsGenericMethodDefinition && candidate.GetParameters().Length == 1 && candidate.GetParameters()[0].ParameterType.IsArray);
                return create.MakeGenericMethod(type).Invoke(null, [values])!;
            }
            return new(Build(false), Build(true));
        }
    }
#pragma warning restore CA1822

#pragma warning disable CA1034, CA1051, CA1815, CA1822, CA2211, CA2225 // Deliberately varied runtime metadata for emission oracles.
    public class RuntimeMethods(int value)
    {
        public int Field = value;
        public static int StaticField = 1;
        public int Property { get; set; }
        public int this[int index] { get => index; set => Field = value; }

        public static int Static()
        {
            return 1;
        }

        public int Instance()
        {
            return 2;
        }

        public virtual int Virtual()
        {
            return 3;
        }

        public int Sealed()
        {
            return 4;
        }

        public ref int RefReturn(ref int value) { Field = value; return ref Field; }
        public static void Never()
        {
            throw new InvalidOperationException();
        }

        public static implicit operator RuntimeMethods(int value)
        {
            return new(value);
        }
    }
    public struct RuntimeValue(int value)
    {
        public int Field = value;
        public int Property { get; set; } = value;
    }
#pragma warning restore CA1034, CA1051, CA1815, CA1822, CA2211, CA2225
}
