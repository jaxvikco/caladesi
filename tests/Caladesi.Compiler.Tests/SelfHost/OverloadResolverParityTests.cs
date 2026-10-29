extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Tests.Emit;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HD = SelfHost::Caladesi.Compiler.Diagnostics;
using HX = SelfHost::Caladesi.Compiler.Syntax;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using Engine = SelfHost::Caladesi.Compiler.Binding.OverloadResolver;
using Context = SelfHost::Caladesi.Compiler.Binding.OverloadResolutionContext;
using ConversionContext = SelfHost::Caladesi.Compiler.Binding.ConversionBindingContext;
using Classifier = SelfHost::Caladesi.Compiler.Binding.ConversionClassifier;
using ConversionBinder = SelfHost::Caladesi.Compiler.Binding.ConversionBinder;
using Call = SelfHost::Caladesi.Compiler.Binding.ResolvedCall;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class OverloadResolverParityTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Lazy<HM.ReferenceSet> References = new(() => HM.ReferenceSet.Create(TestReferences.Paths));

    [TestMethod]
    public void MetadataOverloadSets_NumericNullabilityAndDiagnosticsMatchRealBinder()
    {
        Fixture f = new();
        foreach (string name in new[] { "Abs", "Max", "Min", "Sign", "Round" })
        {
            Method[] methods = f.MetadataMethods("System.Math", name);
            foreach (string type in new[] { "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64",
                "UInt64", "Single", "Double", "Decimal", "String", "Object" })
            foreach (int count in new[] { 0, 1, 2, 3 })
            foreach (bool report in new[] { false, true })
            {
                Pair p = f.Metadata(type);
                f.Check(methods, Enumerable.Range(0, count).Select(_ => f.Value(p)).ToArray(), report: report);
                f.Check(methods, Enumerable.Range(0, count).Select(_ => f.Literal(p, 1)).ToArray(), report: report);
            }
        }
        f.Check([], [f.Value(f.Metadata("Int32"))]);
        f.Check([], [new(new BoundBadExpression([]), new HB.BoundBadExpression([]))]);
    }

    [TestMethod]
    public void NormalExpandedParamsOptionalOmittedOutAndIndexerFormsMatch()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), wide = f.Metadata("Int64");
        Pair array = f.Array(integer);
        Method plain = f.Method(integer, [integer]);
        Method optional = f.Method(integer, [integer, integer], optional: 1, defaultValue: 17);
        Method expanded = f.Method(integer, [integer, array], paramsIndex: 1);
        Method outs = f.Method(integer, [integer, wide, integer], outStart: 1);
        Method byRef = f.Method(integer, [integer], refKind: RefKind.Ref);
        Method setter = f.Method(integer, [integer, wide], kind: MethodKind.PropertySet);
        Method[][] sets = [[plain], [optional], [expanded], [outs], [byRef], [plain, optional, expanded, outs]];
        foreach (Method[] methods in sets)
        foreach (int count in new[] { 0, 1, 2, 3, 5 })
        foreach (bool report in new[] { false, true })
            f.Check(methods, Enumerable.Range(0, count).Select(_ => f.Value(integer)).ToArray(), report: report);
        f.Check([expanded], [f.Value(integer), f.Value(array)]);
        f.Check([byRef], [f.Value(wide)]);
        f.Check([setter], [f.Value(integer)], isIndexer: true, indexerCount: 1);
        f.Check([setter], [f.Value(integer)], isIndexer: false, indexerCount: 1);
        Call? omitted = f.Check([outs], [f.Value(integer)]);
        Assert.IsNotNull(omitted);
        Assert.HasCount(2, omitted.OutLocals);
        Assert.AreSame(omitted.OutLocals[0], ((HB.BoundLocal)((HB.BoundAddressOf)omitted.Arguments[1]).Operand).Local);
        Assert.AreSame(f.Context.LocalOwner, omitted.OutLocals[0].ContainingSymbol);
    }

    [TestMethod]
    public void AmbiguityUsesFirstTwoDistinctCandidatesAndSpecificityTieBreaksMatch()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), wide = f.Metadata("Int64"), obj = f.Metadata("Object");
        Method first = f.Method(integer, [integer]), second = f.Method(wide, [integer]), third = f.Method(obj, [integer]);
        foreach (Method[] methods in new[] { new[] { first, second, third }, [third, first, second], [first, first, second] })
        {
            Call? result = f.Check(methods, [f.Value(integer)]);
            Assert.IsNotNull(result?.Ambiguity);
            Assert.AreSame(methods[0].Self, result.Ambiguity.First);
            Assert.AreSame(methods.First(m => !ReferenceEquals(m, methods[0])).Self, result.Ambiguity.Second);
        }
        foreach (Pair source in new[] { integer, wide, f.Metadata("String"), f.Nullable(f.Metadata("String")) })
            f.Check([f.Method(integer, [wide]), f.Method(integer, [obj])], [f.Value(source)]);
        f.Check([f.Method(integer, [integer]), f.Method(integer, [f.Metadata("UInt32")])], [f.Literal(integer, 0)]);
        Method hidden = f.Method(integer, [integer], accessibility: Accessibility.Private, foreignOwner: true);
        f.Check([hidden], [f.Value(integer)]);
        f.Check([hidden, first], [f.Value(integer)]);
    }

    [TestMethod]
    public void GenericInferenceExplicitArityExpectedTypeAndConstraintChecksMatch()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), text = f.Metadata("String");
        foreach (TypeParameterConstraints constraint in new[] {
            TypeParameterConstraints.None, new(false, true, false, false, []),
            new(true, false, false, false, []), new(false, false, true, false, []),
            new(false, false, false, true, []), new(false, false, false, false, [f.Metadata("Object").Original]) })
        {
            Method generic = f.Generic(constraint);
            foreach (Pair type in new[] { integer, text, f.Nullable(integer), f.Nullable(text) })
            {
                f.Check([generic], [f.Value(type)]);
                f.Check([generic], [f.Value(type)], typeArgs: [type]);
                f.Check([generic], [f.Value(type)], typeArgs: [type, type]);
                f.Check([generic], [], expected: type);
                var tp = generic.Original.TypeParameters[0];
                var ht = generic.Self.TypeParameters[0];
                bool expected = (bool)Invoke(f.Oracle, "SatisfiesConstraint", tp, type.Original,
                    new TypeMap([tp], [type.Original]))!;
                Assert.AreEqual(expected, f.Engine.SatisfiesConstraint(ht, type.Self, new H.TypeMap([ht], [type.Self])));
            }
            f.Check([generic], []);
            f.Check([generic, f.Method(integer, [integer])], [f.Value(integer)]);
        }
    }

    [TestMethod]
    public void DefaultsPreserveBoxedConstantQuirksNullableEnumAndDateTimeTicks()
    {
        Fixture f = new();
        Pair custom = f.Named("Custom", TypeKind.Struct), enumeration = f.Named("Enumeration", TypeKind.Enum);
        Pair[] types = [f.Metadata("Object"), f.Metadata("Int32"), f.Metadata("DateTime"), f.Nullable(f.Metadata("DateTime")),
            f.Nullable(f.Metadata("Int32")), f.Metadata("String"), custom, enumeration];
        object?[] values = [null, (sbyte)-1, (byte)255, (short)-2, (ushort)3, -4, 5L, 6U, 7UL,
            1.5d, 2.5f, 3m, true, 'x', "hello", new DateTime(638000000000000000L)];
        foreach (Pair type in types)
        foreach (object? value in values)
        {
            Method method = f.Method(type, [type], optional: 0, defaultValue: value);
            var p = method.Original.Parameters[0];
            BoundExpression original = (BoundExpression)Invoke(f.Oracle, "DefaultArgument", p, f.Syntax)!;
            f.Compare(original, f.Engine.DefaultArgument(method.Self.Parameters[0], f.SelfSyntax));
            f.Check([method], []);
        }
        Method absent = f.Method(custom, [custom], optional: 0, absentDefault: true);
        f.Check([absent], []);
    }

    [TestMethod]
    public void LambdaApplicabilityCacheIsByInfoIdentityAndDelegateDisplayAndRestoresSpeculation()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), wide = f.Metadata("Int64"), voidType = f.Metadata("Void");
        Pair d1 = f.Delegate("D", integer, integer), sameName = f.Delegate("D", wide, integer);
        Pair differentName = f.Delegate("Other", integer, integer), action = f.Delegate("Action", voidType, integer);
        int originalBinds = 0, selfBinds = 0;
        LambdaInfo original = new()
        {
            ParameterCount = 1, ExplicitParameterTypes = [null],
            Bind = (ps, rt, speculative) =>
            {
                Assert.IsTrue(speculative);
                Assert.IsTrue((bool)typeof(Binder).GetProperty("IsSpeculative", Hidden)!.GetValue(f.Oracle)!);
                originalBinds++;
                return f.OriginalLambda(d1);
            },
            InferReturnType = _ => integer.Original
        };
        HB.LambdaInfo self = new(1, [null], (ps, rt, speculative) =>
        {
            Assert.IsTrue(speculative);
            Assert.AreEqual(1, f.Context.Depth);
            Assert.IsNull(f.Context.SemanticMap);
            selfBinds++;
            f.Context.Report(HD.Descriptors.NotCallable, f.SelfSyntax, []);
            return f.SelfLambda(d1);
        }, _ => integer.Self);
        foreach (Pair target in new[] { d1, d1, sameName, differentName, action, f.Nullable(d1), integer })
            Assert.AreEqual((bool)Invoke(f.Oracle, "LambdaConvertible", original, target.Original)!,
                f.Engine.LambdaConvertible(self, target.Self));
        Assert.AreEqual(originalBinds, selfBinds);
        Assert.AreEqual(3, selfBinds);
        Assert.AreEqual(0, f.Context.Depth);
        Assert.IsNotNull(f.Context.SemanticMap);
        Assert.IsEmpty(f.Context.Diagnostics);
        HB.LambdaInfo failure = new(1, [integer.Self], (_, _, _) => throw new InvalidOperationException("speculation"), _ => null);
        Assert.ThrowsExactly<InvalidOperationException>(() => f.Engine.LambdaConvertible(failure, d1.Self));
        Assert.AreEqual(0, f.Context.Depth);
        Assert.IsNotNull(f.Context.SemanticMap);
        LambdaInfo mismatch = new() { ParameterCount = 1, ExplicitParameterTypes = [wide.Original],
            Bind = (_, _, _) => throw new AssertFailedException("Explicit mismatch must not bind"), InferReturnType = _ => null };
        HB.LambdaInfo selfMismatch = new(1, [wide.Self],
            (_, _, _) => throw new AssertFailedException("Explicit mismatch must not bind"), _ => null);
        Assert.AreEqual((bool)Invoke(f.Oracle, "LambdaConvertible", mismatch, d1.Original)!,
            f.Engine.LambdaConvertible(selfMismatch, d1.Self));
        Expression lambda = new(new BoundUnbound(f.Syntax, _ => f.OriginalLambda(d1)) { IsLambda = true, Lambda = original },
            new HB.BoundUnbound(f.SelfSyntax, _ => f.SelfLambda(d1)) { IsLambda = true, Lambda = self });
        f.Check([f.Method(integer, [d1]), f.Method(integer, [action])], [lambda]);
    }

    [TestMethod]
    public void MethodGroupsUseRealResolverRespectReturnCompatibilityAndDoNotDiscardResults()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), wide = f.Metadata("Int64"), voidType = f.Metadata("Void");
        Pair func = f.Delegate("Func", wide, integer), action = f.Delegate("Action", voidType, integer);
        foreach (Pair result in new[] { integer, wide, voidType, f.Remember(NeverTypeSymbol.Instance, H.NeverTypeSymbol.Instance) })
        {
            Method method = f.Method(result, [integer]);
            Expression group = f.Group([method]);
            foreach (Pair target in new[] { func, action, f.Nullable(func), integer })
            {
                Assert.AreEqual((bool)Invoke(f.Oracle, "MethodGroupConvertible", group.Original, target.Original)!,
                    f.Engine.MethodGroupConvertible((HB.BoundMethodGroup)group.Self, target.Self));
                f.Check([f.Method(integer, [target])], [group]);
            }
        }
        Method generic = f.Generic(TypeParameterConstraints.None);
        Expression inferredGroup = f.Group([generic]);
        f.Check([f.Method(integer, [func])], [inferredGroup]);
        Assert.AreSame(generic.Self, f.Engine.ResolveMethodGroupByTypes((HB.BoundMethodGroup)f.Group([generic]).Self,
            [integer.Self])?.ConstructedFrom);
        Assert.AreEqual(0, f.Context.Depth);
        Assert.IsNotNull(f.Context.SemanticMap);
    }

    [TestMethod]
    public void ExtensionReceiverFilteringAndUnboundSpeculationMatchOracle()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), text = f.Metadata("String");
        Method extension = f.Method(integer, [text, integer], extension: true);
        Method normal = f.Method(integer, [text, integer]);
        foreach (Method[] methods in new[] { new[] { normal }, [extension], [normal, extension] })
            f.Check(methods, [f.Value(integer)], receiver: f.Value(text));
        Expression unbound = new(new BoundUnbound(f.Syntax, target => new BoundDefaultValue(target!)),
            new HB.BoundUnbound(f.SelfSyntax, target =>
            {
                Assert.IsGreaterThan(0, f.Context.Depth);
                Assert.IsNull(f.Context.SemanticMap);
                return new HB.BoundDefaultValue(target!);
            }));
        foreach (Pair target in new[] { integer, text })
            Assert.AreEqual((bool)Invoke(f.Oracle, "ArgumentConvertible", unbound.Original, target.Original, RefKind.None)!,
                f.Engine.ArgumentConvertible(unbound.Self, target.Self, H.RefKind.None));
        Assert.AreEqual(0, f.Context.Depth);
        Assert.IsNotNull(f.Context.SemanticMap);
    }

    [TestMethod]
    public void ReentrantSpeculationRestoresOuterSuppressionAndOwnerMapsAfterInnerException()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32");
        Dictionary<SyntaxNode, Symbol> symbols = new() { [f.Syntax] = integer.Original };
        Dictionary<SyntaxNode, TypeSymbol> types = new() { [f.Syntax] = integer.Original };
        Dictionary<SyntaxNode, BoundNode> bounds = new() { [f.Syntax] = new BoundDefaultValue(integer.Original) };
        Dictionary<(SyntaxTree, int), Symbol> declarations = [];
        f.Oracle.SymbolMap = symbols;
        f.Oracle.TypeMap = types;
        f.Oracle.BoundMap = bounds;
        PropertyInfo declarationMap = typeof(Binder).GetProperty("DeclarationMap", Hidden)!;
        declarationMap.SetValue(f.Oracle, declarations);
        f.Context.SemanticMap![f.SelfSyntax] = integer.Self;
        var selfMap = f.Context.SemanticMap;
        FieldInfo diagnostics = typeof(Binder).GetField("_diag", Hidden)!;
        void CheckOracleSuppressed()
        {
            Assert.IsNull(diagnostics.GetValue(f.Oracle));
            Assert.IsNull(f.Oracle.SymbolMap);
            Assert.IsNull(f.Oracle.TypeMap);
            Assert.IsNull(f.Oracle.BoundMap);
            Assert.IsNull(declarationMap.GetValue(f.Oracle));
        }
        foreach (bool catchInsideOuter in new[] { true, false })
        {
            BoundUnbound originalInner = new(f.Syntax, _ =>
            {
                CheckOracleSuppressed();
                throw new InvalidOperationException("inner");
            });
            BoundUnbound originalOuter = new(f.Syntax, target =>
            {
                CheckOracleSuppressed();
                try { Invoke(f.Oracle, "ArgumentConvertible", originalInner, target, RefKind.None); }
                catch (TargetInvocationException error) when (catchInsideOuter)
                {
                    Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
                    CheckOracleSuppressed();
                }
                return new BoundDefaultValue(target!);
            });
            List<int> depths = [];
            HB.BoundUnbound selfInner = new(f.SelfSyntax, _ =>
            {
                depths.Add(f.Context.Depth);
                Assert.AreEqual(2, f.Context.Depth);
                Assert.IsNull(f.Context.SemanticMap);
                throw new InvalidOperationException("inner");
            });
            HB.BoundUnbound selfOuter = new(f.SelfSyntax, target =>
            {
                depths.Add(f.Context.Depth);
                Assert.AreEqual(1, f.Context.Depth);
                Assert.IsNull(f.Context.SemanticMap);
                try { f.Engine.ArgumentConvertible(selfInner, target!, H.RefKind.None); }
                catch (InvalidOperationException) when (catchInsideOuter)
                {
                    depths.Add(f.Context.Depth);
                    Assert.AreEqual(1, f.Context.Depth);
                    Assert.IsNull(f.Context.SemanticMap);
                }
                return new HB.BoundDefaultValue(target!);
            });
            if (catchInsideOuter)
                Assert.AreEqual((bool)Invoke(f.Oracle, "ArgumentConvertible", originalOuter, integer.Original, RefKind.None)!,
                    f.Engine.ArgumentConvertible(selfOuter, integer.Self, H.RefKind.None));
            else
            {
                Assert.ThrowsExactly<TargetInvocationException>(() =>
                    Invoke(f.Oracle, "ArgumentConvertible", originalOuter, integer.Original, RefKind.None));
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    f.Engine.ArgumentConvertible(selfOuter, integer.Self, H.RefKind.None));
            }
            Assert.HasCount(catchInsideOuter ? 3 : 2, depths);
            Assert.AreEqual(1, depths[0]);
            Assert.AreEqual(2, depths[1]);
            if (catchInsideOuter) Assert.AreEqual(1, depths[2]);
            Assert.AreSame(f.Diagnostics, diagnostics.GetValue(f.Oracle));
            Assert.AreSame(symbols, f.Oracle.SymbolMap);
            Assert.AreSame(types, f.Oracle.TypeMap);
            Assert.AreSame(bounds, f.Oracle.BoundMap);
            Assert.AreSame(declarations, declarationMap.GetValue(f.Oracle));
            Assert.AreSame(selfMap, f.Context.SemanticMap);
            Assert.AreSame(integer.Self, f.Context.SemanticMap![f.SelfSyntax]);
            Assert.AreEqual(0, f.Context.Depth);
        }
    }

    [TestMethod]
    public void CandidatesDifferingOnlyByRefKindRemainAmbiguousInEitherDeclarationOrder()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32");
        foreach ((RefKind firstKind, RefKind secondKind) in new[] {
            (RefKind.None, RefKind.Ref), (RefKind.Ref, RefKind.Out), (RefKind.None, RefKind.In) })
        {
            Method first = f.Method(integer, [integer], refKind: firstKind);
            Method second = f.Method(integer, [integer], refKind: secondKind);
            foreach (Method[] candidates in new[] { new[] { first, second }, [second, first] })
            {
                Call? result = f.Check(candidates, [f.Value(integer)]);
                Assert.IsNotNull(result?.Ambiguity);
                Assert.AreSame(candidates[0].Self, result.Method);
                Assert.AreSame(candidates[0].Self, result.Ambiguity.First);
                Assert.AreSame(candidates[1].Self, result.Ambiguity.Second);
                Assert.AreEqual(HD.Descriptors.AmbiguousCall.Id, f.Context.Diagnostics.Single().Descriptor.Id);
            }
        }
    }

    private static object? Invoke(object owner, string method, params object?[] args) =>
        owner.GetType().GetMethod(method, Hidden)!.Invoke(owner, args);

    private sealed record Pair(TypeSymbol Original, H.TypeSymbol Self);
    private sealed record Method(MethodSymbol Original, H.MethodSymbol Self);
    private sealed record Expression(BoundExpression Original, HB.BoundExpression Self);

    private sealed class Fixture
    {
        public readonly Binder Oracle;
        public readonly DiagnosticBag Diagnostics = new();
        public readonly TestContext Context;
        public readonly Engine Engine;
        public readonly SyntaxNode Syntax = SyntaxTree.ParseText("  const call = 1;", "overloads.cala").Root;
        public readonly HX.SyntaxNode SelfSyntax = HX.SyntaxTree.ParseText("  const call = 1;", "overloads.cala").Root;
        public readonly Dictionary<object, object> identities = new(ReferenceEqualityComparer.Instance);
        private readonly Pair owner;

        public Fixture()
        {
            var compilation = TestCompiler.Create("class Context {}", OutputKind.DynamicallyLinkedLibrary);
            owner = Named("Owner", TypeKind.Class);
            Oracle = new Binder(compilation, compilation.GetFile(compilation.SyntaxTrees.Single()), owner.Original, Diagnostics);
            Context = new TestContext(this, owner.Self);
            Classifier classifier = new();
            ConversionServices services = new(this);
            Engine = new(classifier, new ConversionBinder(classifier, services), Context);
            identities[Syntax] = SelfSyntax;
        }

        public Pair Remember(TypeSymbol original, H.TypeSymbol self)
        {
            identities[original] = self;
            return new(original, self);
        }
        public Pair Metadata(string name) => Remember(TestCompiler.References.FindType("System." + name)!,
            References.Value.FindType("System." + name)!);
        public Pair Nullable(Pair type) => Remember(new NullableTypeSymbol(type.Original), new H.NullableTypeSymbol(type.Self));
        public Pair Array(Pair type) => Remember(new ArrayTypeSymbol(type.Original, (NamedTypeSymbol)Metadata("Array").Original),
            new H.ArrayTypeSymbol(type.Self, (H.NamedTypeSymbol)Metadata("Array").Self));
        public Pair Named(string name, TypeKind kind) => Remember(new OriginalNamed(name, kind), new SelfNamed(name, (H.TypeKind)kind));
        public Pair Delegate(string name, Pair result, params Pair[] parameters)
        {
            Pair type = Named(name, TypeKind.Delegate);
            Method invoke = Method(result, parameters, kind: MethodKind.DelegateInvoke);
            ((OriginalNamed)type.Original).Invoke = invoke.Original;
            ((SelfNamed)type.Self).Invoke = invoke.Self;
            return type;
        }
        public Method[] MetadataMethods(string ownerName, string name)
        {
            var original = TestCompiler.References.FindType(ownerName)!.GetMembers(name).OfType<MethodSymbol>().ToArray();
            var self = References.Value.FindType(ownerName)!.GetMembers(name).OfType<H.MethodSymbol>().ToArray();
            Assert.HasCount(original.Length, self);
            return original.Select((m, i) => { identities[m] = self[i]; return new Method(m, self[i]); }).ToArray();
        }
        public Method Method(Pair result, Pair[] parameters, int optional = -1, object? defaultValue = null,
            int paramsIndex = -1, int outStart = int.MaxValue, RefKind refKind = RefKind.None,
            MethodKind kind = MethodKind.Ordinary, bool extension = false,
            Accessibility accessibility = Accessibility.Public, bool foreignOwner = false, bool absentDefault = false)
        {
            Pair container = foreignOwner ? Named("Foreign", TypeKind.Class) : owner;
            OriginalMethod original = new(container.Original, result.Original, kind, extension, accessibility);
            SelfMethod self = new(container.Self, result.Self, (H.MethodKind)kind, extension, (H.Accessibility)accessibility);
            original.Params = parameters.Select((p, i) => new ParameterSymbol("p" + i, p.Original, i, original,
                i >= outStart ? RefKind.Out : refKind, i == paramsIndex,
                i == optional && !absentDefault ? new ConstantValue(defaultValue) : null, i == optional)).ToImmutableArray();
            self.Params = parameters.Select((p, i) => new H.ParameterSymbol("p" + i, p.Self, i, self,
                i >= outStart ? H.RefKind.Out : (H.RefKind)refKind, i == paramsIndex,
                i == optional && !absentDefault ? new H.ConstantValue(defaultValue) : null, i == optional, false)).ToImmutableArray();
            identities[original] = self;
            return new(original, self);
        }
        public Method Generic(TypeParameterConstraints constraints)
        {
            Pair integer = Metadata("Int32");
            Method pair = Method(integer, []);
            OriginalMethod original = (OriginalMethod)pair.Original;
            SelfMethod self = (SelfMethod)pair.Self;
            TypeParameterSymbol tp = new("T", 0, original, VarianceKind.None, _ => constraints);
            H.TypeParameterSymbol ht = new("T", 0, self, H.VarianceKind.None, _ => new H.TypeParameterConstraints(
                constraints.HasClass, constraints.HasStruct, constraints.HasNew, constraints.HasNotNull,
                constraints.Types.Select(t => (H.TypeSymbol)identities[t]).ToImmutableArray()));
            Remember(tp, ht);
            original.Tps = [tp]; self.Tps = [ht];
            original.Result = tp; self.Result = ht;
            original.Params = [new ParameterSymbol("value", tp, 0, original)];
            self.Params = [new H.ParameterSymbol("value", ht, 0, self)];
            return pair;
        }
        public Expression Value(Pair type) => new(new BoundDefaultValue(type.Original) { Syntax = Syntax },
            new HB.BoundDefaultValue(type.Self) { Syntax = SelfSyntax });
        public Expression Literal(Pair type, object? value) => new(new BoundLiteral(new(value), type.Original) { Syntax = Syntax },
            new HB.BoundLiteral(new(value), type.Self) { Syntax = SelfSyntax });
        public Expression Group(Method[] methods) => new(new BoundMethodGroup(null, "Call", methods.Select(m => m.Original).ToImmutableArray(), []) { Syntax = Syntax },
            new HB.BoundMethodGroup(null, "Call", methods.Select(m => m.Self).ToImmutableArray(), []) { Syntax = SelfSyntax });
        public BoundLambda OriginalLambda(Pair target) => new(new LambdaSymbol(owner.Original, "<lambda>", false, false),
            new BoundBlock([], []), target.Original);
        public HB.BoundLambda SelfLambda(Pair target) => new(new HB.LambdaSymbol(owner.Self, "<lambda>", false, false),
            new HB.BoundBlock([], []), target.Self);

        public Call? Check(Method[] methods, Expression[] arguments, Pair[]? typeArgs = null, bool report = true,
            bool isIndexer = false, int indexerCount = 0, Expression? receiver = null, Pair? expected = null)
        {
            Diagnostics.TruncateTo(0);
            Context.Diagnostics.Clear();
            foreach (Expression argument in arguments) identities[argument.Original] = argument.Self;
            if (receiver != null) identities[receiver.Original] = receiver.Self;
            object? original = Invoke(Oracle, "ResolveOverloads", methods.Select(m => m.Original),
                arguments.Select(a => a.Original).ToImmutableArray(),
                (typeArgs ?? []).Select(t => t.Original).ToImmutableArray(), Syntax, "Call", report,
                isIndexer, indexerCount, receiver?.Original, expected?.Original);
            Call? self = Engine.ResolveOverloads(methods.Select(m => m.Self),
                arguments.Select(a => a.Self).ToImmutableArray(),
                (typeArgs ?? []).Select(t => t.Self).ToImmutableArray(), SelfSyntax, "Call", report,
                isIndexer, indexerCount, receiver?.Self, expected?.Self);
            Assert.AreEqual(original == null, self == null);
            if (original != null && self != null)
            {
                object? Get(string name) => original.GetType().GetProperty(name)!.GetValue(original);
                CompareMethod((MethodSymbol)Get("Method")!, self.Method);
                Assert.AreEqual(Get("Expanded"), self.Expanded);
                var outs = (ImmutableArray<LocalSymbol>)Get("OutLocals")!;
                Assert.HasCount(outs.Length, self.OutLocals);
                for (int i = 0; i < outs.Length; i++)
                {
                    identities[outs[i]] = self.OutLocals[i];
                    Assert.AreEqual(outs[i].Name, self.OutLocals[i].Name);
                    Assert.AreEqual(outs[i].IsSynthesized, self.OutLocals[i].IsSynthesized);
                    Assert.AreSame(identities[outs[i].ContainingSymbol!], self.OutLocals[i].ContainingSymbol);
                    CompareType(outs[i].Type, self.OutLocals[i].Type);
                }
                var args = (ImmutableArray<BoundExpression>)Get("Arguments")!;
                Assert.HasCount(args.Length, self.Arguments);
                for (int i = 0; i < args.Length; i++) Compare(args[i], self.Arguments[i]);
                var ambiguity = ((MethodSymbol, MethodSymbol)?)Get("Ambiguity");
                Assert.AreEqual(ambiguity.HasValue, self.Ambiguity != null);
                if (ambiguity.HasValue)
                {
                    CompareMethod(ambiguity.Value.Item1, self.Ambiguity!.First);
                    CompareMethod(ambiguity.Value.Item2, self.Ambiguity.Second);
                }
            }
            Assert.HasCount(Diagnostics.Count, Context.Diagnostics);
            for (int i = 0; i < Diagnostics.Count; i++)
            {
                var actual = Context.Diagnostics[i];
                var diagnostic = Diagnostics.Items[i];
                Assert.AreEqual(diagnostic.Id, actual.Descriptor.Id);
                Assert.AreEqual(diagnostic.Descriptor.MessageFormat, actual.Descriptor.MessageFormat);
                Assert.AreEqual(diagnostic.Span.Start, actual.Syntax?.Span.Start ?? 0);
                Assert.AreEqual(diagnostic.Span.Length, actual.Syntax?.Span.Length ?? 0);
                CollectionAssert.AreEqual(diagnostic.Arguments, actual.Args);
                FieldInfo descriptor = typeof(Descriptors).GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Single(d => ReferenceEquals(d.GetValue(null), diagnostic.Descriptor));
                Assert.AreSame(typeof(HD.Descriptors).GetProperty(descriptor.Name)!.GetValue(null), actual.Descriptor);
            }
            return self;
        }

        public void Compare(BoundExpression original, HB.BoundExpression self)
        {
            Assert.AreEqual(original.GetType().Name, self.GetType().Name);
            CompareType(original.Type, self.Type);
            Assert.AreEqual(original.Syntax == null, self.Syntax == null);
            if (original.Syntax != null) Assert.AreSame(identities[original.Syntax], self.Syntax);
            Assert.AreEqual(original.ConstantValue?.Value, self.ConstantValue?.Value);
            Assert.AreEqual(original.ConstantValue?.Value?.GetType(), self.ConstantValue?.Value?.GetType());
            switch (original)
            {
                case BoundConversion c:
                    var hc = (HB.BoundConversion)self;
                    Assert.AreEqual((int)c.Kind, (int)hc.Kind);
                    Compare(c.Operand, hc.Operand);
                    break;
                case BoundArrayCreation a:
                    var ha = (HB.BoundArrayCreation)self;
                    Assert.HasCount(a.Initializers.Length, ha.Initializers);
                    if (a.Length != null) Compare(a.Length, ha.Length!);
                    for (int i = 0; i < a.Initializers.Length; i++) Compare(a.Initializers[i], ha.Initializers[i]);
                    break;
                case BoundObjectCreation o:
                    var ho = (HB.BoundObjectCreation)self;
                    Assert.HasCount(o.Arguments.Length, ho.Arguments);
                    CompareMethod(o.Constructor!, ho.Constructor!);
                    for (int i = 0; i < o.Arguments.Length; i++) Compare(o.Arguments[i], ho.Arguments[i]);
                    break;
                case BoundAddressOf address: Compare(address.Operand, ((HB.BoundAddressOf)self).Operand); break;
                case BoundLocal local: Assert.AreSame(identities[local.Local], ((HB.BoundLocal)self).Local); break;
                case BoundDelegateCreation creation: CompareMethod(creation.Method, ((HB.BoundDelegateCreation)self).Method); break;
            }
        }
        private void CompareType(TypeSymbol? original, H.TypeSymbol? self)
        {
            Assert.AreEqual(original?.ToDisplayString(), self?.ToDisplayString());
            Assert.AreEqual(original == null ? null : (int?)original.SpecialType, self == null ? null : (int?)self.SpecialType);
            if (original != null && identities.TryGetValue(original, out object? known)) Assert.AreSame(known, self);
        }
        private void CompareMethod(MethodSymbol original, H.MethodSymbol self)
        {
            if (identities.TryGetValue(original, out object? known)) { Assert.AreSame(known, self); return; }
            if (identities.TryGetValue(original.ConstructedFrom, out known))
                Assert.AreSame(known, self.ConstructedFrom);
            Assert.AreEqual(original.Name, self.Name);
            CompareType(original.ReturnType, self.ReturnType);
            Assert.HasCount(original.Parameters.Length, self.Parameters);
            for (int i = 0; i < original.Parameters.Length; i++) CompareType(original.Parameters[i].Type, self.Parameters[i].Type);
        }
    }

    private sealed class TestContext(Fixture fixture, H.Symbol owner) : Context
    {
        public List<(HD.DiagnosticDescriptor Descriptor, HX.SyntaxNode? Syntax, object?[] Args)> Diagnostics = [];
        public Dictionary<HX.SyntaxNode, H.Symbol>? SemanticMap = [];
        public int Depth;
        public override H.Symbol LocalOwner => owner;
        public override bool IsAccessible(H.Symbol symbol)
        {
            object original = fixture.identities.Single(p => ReferenceEquals(p.Value, symbol)).Key;
            return (bool)Invoke(fixture.Oracle, "IsAccessible", original, null)!;
        }
        public override IEnumerable<H.MethodSymbol> LookupExtensionMethods(string name) =>
            ((IEnumerable<MethodSymbol>)Invoke(fixture.Oracle, "LookupExtensionMethods", name)!)
            .Select(m => (H.MethodSymbol)fixture.identities[m]);
        public override H.NamedTypeSymbol GetSpecialType(H.SpecialType type) =>
            References.Value.FindType("System." + type)!;
        public override void Report(HD.DiagnosticDescriptor descriptor, HX.SyntaxNode? syntax, object?[] args)
        {
            if (Depth == 0) Diagnostics.Add((descriptor, syntax, args));
        }
        public override IDisposable BeginSpeculation()
        {
            var saved = SemanticMap;
            SemanticMap = null;
            Depth++;
            return new Guard(() => { Depth--; SemanticMap = saved; });
        }
    }
    private sealed class Guard(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class ConversionServices(Fixture fixture) : ConversionContext
    {
        public override H.TypeSymbol BoolType => fixture.Metadata("Boolean").Self;
        public override H.Symbol ContainingSymbol => fixture.Context.LocalOwner!;
        public override bool IsLiteralEnum(H.TypeSymbol type) => false;
        public override void Report(HD.DiagnosticDescriptor descriptor, HX.SyntaxNode? syntax, object?[] args) =>
            fixture.Context.Report(descriptor, syntax, args);
        public override HB.BoundExpression ConvertMethodGroup(HB.BoundMethodGroup group, H.TypeSymbol target,
            HX.SyntaxNode? syntax, bool report)
        {
            H.MethodSymbol? method = target.StripNullable() is H.NamedTypeSymbol d
                ? fixture.Engine.ResolveMethodGroupForDelegate(group, d) : null;
            Assert.IsNotNull(method);
            HB.BoundDelegateCreation created = new(group.Receiver, method, target.StripNullable()) { Syntax = syntax ?? group.Syntax };
            return target is H.NullableTypeSymbol
                ? new HB.BoundConversion(created, HB.ConversionKind.ToNullable, target) { Syntax = syntax }
                : created;
        }
    }
    private sealed class OriginalNamed(string name, TypeKind kind) : NamedTypeSymbol
    {
        public MethodSymbol? Invoke;
        public override string Name => name;
        public override TypeKind TypeKind => kind;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<TypeSymbol> TypeArguments => [];
        public override Accessibility DeclaredAccessibility => Accessibility.Public;
        public override MethodSymbol? DelegateInvokeMethod => Invoke;
        public override ImmutableArray<Symbol> GetMembers() => [];
    }
    private sealed class SelfNamed(string name, H.TypeKind kind) : H.NamedTypeSymbol
    {
        public H.MethodSymbol? Invoke;
        public override string Name => name;
        public override H.TypeKind TypeKind => kind;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<H.TypeSymbol> TypeArguments => [];
        public override H.Accessibility DeclaredAccessibility => H.Accessibility.Public;
        public override H.MethodSymbol? DelegateInvokeMethod => Invoke;
        public override ImmutableArray<H.Symbol> GetMembers() => [];
    }
    private sealed class OriginalMethod(TypeSymbol owner, TypeSymbol result, MethodKind kind, bool extension,
        Accessibility accessibility) : MethodSymbol
    {
        public ImmutableArray<ParameterSymbol> Params = [];
        public ImmutableArray<TypeParameterSymbol> Tps = [];
        public TypeSymbol Result = result;
        public override string Name => "Call";
        public override Symbol ContainingSymbol => owner;
        public override TypeSymbol ReturnType => Result;
        public override MethodKind MethodKind => kind;
        public override ImmutableArray<ParameterSymbol> Parameters => Params;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => Tps;
        public override bool IsExtensionMethod => extension;
        public override bool IsStatic => true;
        public override Accessibility DeclaredAccessibility => accessibility;
    }
    private sealed class SelfMethod(H.TypeSymbol owner, H.TypeSymbol result, H.MethodKind kind, bool extension,
        H.Accessibility accessibility) : H.MethodSymbol
    {
        public ImmutableArray<H.ParameterSymbol> Params = [];
        public ImmutableArray<H.TypeParameterSymbol> Tps = [];
        public H.TypeSymbol Result = result;
        public override string Name => "Call";
        public override H.Symbol ContainingSymbol => owner;
        public override H.TypeSymbol ReturnType => Result;
        public override H.MethodKind MethodKind => kind;
        public override ImmutableArray<H.ParameterSymbol> Parameters => Params;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => Tps;
        public override bool IsExtensionMethod => extension;
        public override bool IsStatic => true;
        public override H.Accessibility DeclaredAccessibility => accessibility;
    }
}
