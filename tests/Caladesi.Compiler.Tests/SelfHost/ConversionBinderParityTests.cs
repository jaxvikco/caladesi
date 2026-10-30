extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Tests.Emit;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HD = SelfHost::Caladesi.Compiler.Diagnostics;
using HX = SelfHost::Caladesi.Compiler.Syntax;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using HConversion = SelfHost::Caladesi.Compiler.Conversion;
using HConversions = SelfHost::Caladesi.Compiler.Conversions;
using Engine = SelfHost::Caladesi.Compiler.Binding.ConversionBinder;
using Context = SelfHost::Caladesi.Compiler.Binding.ConversionBindingContext;
using Classifier = SelfHost::Caladesi.Compiler.Binding.ConversionClassifier;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class ConversionBinderParityTests
{
    private static readonly Lazy<HM.ReferenceSet> References = new(() => HM.ReferenceSet.Create(TestReferences.Paths));
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void ImplicitAndExplicit_NumericConstantAndNullableMatrixMatchesRealBinder()
    {
        Fixture f = new();
        string[] names = ["SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
            "Single", "Double", "Decimal", "Char", "Boolean", "String", "Object", "Void"];
        Pair[] core = [.. names.Select(f.Metadata)];
        Pair[] types = [.. core, .. core.Where(p => p.Original.SpecialType != SpecialType.Void).Select(f.Nullable),
            f.Remember(ErrorTypeSymbol.Instance, H.ErrorTypeSymbol.Instance),
            f.Remember(NullTypeSymbol.Instance, H.NullTypeSymbol.Instance),
            f.Remember(NeverTypeSymbol.Instance, H.NeverTypeSymbol.Instance)];
        foreach (var source in types)
        {
            foreach (var target in types)
            {
                foreach (var explicitConversion in new[] { false, true })
                {
                    _ = f.Check(f.Value(source), target, explicitConversion);
                }
            }
        }

        object?[] values = [null, (sbyte)-1, (byte)255, (short)-32768, (ushort)65535, -1, 0, 256, int.MaxValue,
            uint.MaxValue, long.MinValue, ulong.MaxValue, 1f, 1.5d, 2m, 'x', "", "x", "xy", true];
        foreach (var source in core.Where(p => p.Original.SpecialType != SpecialType.Void))
        {
            foreach (var value in values)
            {
                foreach (var target in types)
                {
                    foreach (var explicitConversion in new[] { false, true })
                    {
                        _ = f.Check(f.Literal(source, value), target, explicitConversion, report: false, syntax: false);
                    }
                }
            }
        }
    }

    [TestMethod]
    public void IdentityErrorsAndBooleanFastPath_PreserveOriginalNodeAndSyntax()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), error = f.Remember(ErrorTypeSymbol.Instance, H.ErrorTypeSymbol.Instance);
        foreach (var explicitConversion in new[] { false, true })
        {
            foreach (var source in new[] { integer, error })
            {
                foreach (var target in new[] { integer, error })
                {
                    var expression = f.Value(source);
                    var result = f.Check(expression, target, explicitConversion);
                    Assert.AreSame(expression.Original, result.Original);
                    Assert.AreSame(expression.Self, result.Self);
                }
            }
        }

        foreach (var source in new[] { f.Metadata("Boolean"), integer, f.Nullable(f.Metadata("Boolean")), error })
        {
            var expression = f.Value(source);
            var result = f.CheckBool(expression);
            if (source.Original.SpecialType == SpecialType.Boolean)
            {
                Assert.AreSame(expression.Original, result.Original);
                Assert.AreSame(expression.Self, result.Self);
            }
        }
    }

    [TestMethod]
    public void Diagnostics_DescriptorIdentityArgumentsSpanAndSuppressionMatch()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), wide = f.Metadata("Int64"), text = f.Metadata("String");
        var nullType = f.Remember(NullTypeSymbol.Instance, H.NullTypeSymbol.Instance);
        foreach (var report in new[] { true, false })
        {
            foreach (var syntax in new[] { true, false })
            {
                foreach (var explicitConversion in new[] { false, true })
                {
                    _ = f.Check(f.Value(nullType), integer, explicitConversion, report, syntax);
                    _ = f.Check(f.Value(f.Nullable(integer)), wide, explicitConversion, report, syntax);
                    _ = f.Check(f.Value(wide), integer, explicitConversion, report, syntax);
                    _ = f.Check(f.Value(text), integer, explicitConversion, report, syntax);
                    _ = f.Check(new(new BoundExpressionStub(null) { Syntax = f.Syntax },
                        new SelfExpressionStub(null) { Syntax = f.SelfSyntax }), integer, explicitConversion, report, syntax);
                    _ = f.Check(new(new BoundNamespaceExpression(TestCompiler.References.GlobalNamespace) { Syntax = f.Syntax },
                        new HB.BoundNamespaceExpression(References.Value.GlobalNamespace) { Syntax = f.SelfSyntax }),
                        integer, explicitConversion, report, syntax);
                    // BoundTypeExpression carries a type in the oracle, so it reaches assignability, not NotAValue.
                    _ = f.Check(new(new BoundTypeExpression(text.Original), new HB.BoundTypeExpression(text.Self)),
                        integer, explicitConversion, report, syntax);
                }
            }
        }
    }

    [TestMethod]
    public void ModuleNonvalues_RealSourceFileAndDefaultFalseTypedMarkerMatchOriginalDiagnostics()
    {
        Fixture f = new();
        var integer = f.Metadata("Int32");
        var predicate = typeof(Context).GetMethod("IsModuleExpression")!;
        Assert.IsTrue(predicate.IsVirtual);
        Assert.IsFalse(predicate.IsAbstract);
        Assert.IsFalse(f.Context.IsModuleExpression(f.Value(integer).Self));
        foreach (var report in new[] { true, false })
        {
            foreach (var syntax in new[] { true, false })
            {
                foreach (var explicitConversion in new[] { false, true })
                {
                    BoundModuleExpression original = new(f.File) { Syntax = f.Syntax };
                    SelfModuleExpression self = new() { Syntax = f.SelfSyntax };
                    f.Context.ModuleExpression = self;
                    Assert.AreSame(f.File, original.File);
                    _ = f.Check(new(original, self), integer, explicitConversion, report, syntax);
                }
            }
        }
    }

    [TestMethod]
    public void LiteralEnums_RealSourceMarkerAndNullValuedFoldedConstantsMatch()
    {
        Fixture f = new("""type Direction = "up" | "down";""");
        var original = f.File.Types.OfType<SourceLiteralEnumSymbol>().Single();
        var enumeration = f.Remember(original, new SelfNamed("Direction"));
        f.Context.LiteralEnum = enumeration.Self;
        Classifier classifier = new((type, literal) =>
        {
            if (!ReferenceEquals(type, enumeration.Self))
            {
                return null;
            }

            var field = original.FindMember(literal);
            return field == null ? null : new SelfField(field.Name, enumeration.Self, field.ConstantValue?.Value);
        });
        f.Engine = new Engine(classifier, f.Context);
        var text = f.Metadata("String");
        foreach (var value in new[] { "up", "down", "missing", "\"quoted\"" })
        {
            foreach (var target in new[] { enumeration, f.Nullable(enumeration), f.Nullable(f.Nullable(enumeration)) })
            {
                foreach (var syntax in new[] { false, true })
                {
                    foreach (var report in new[] { false, true })
                    {
                        _ = f.Check(f.Literal(text, value), target, report: report, syntax: syntax);
                    }
                }
            }
        }

        var found = original.FindMember("up")!;
        typeof(SourceFieldSymbol).GetField("_constDone", PrivateInstance)!.SetValue(found, true);
        foreach (var constant in new ConstantValue?[] { null, new(null) })
        {
            typeof(SourceFieldSymbol).GetField("_const", PrivateInstance)!.SetValue(found, constant);
            _ = f.Check(f.Literal(text, "up"), enumeration);
            _ = f.Check(f.Literal(text, "up"), f.Nullable(enumeration));
        }
    }

    [TestMethod]
    public void CreateConversion_AllKindsFoldedNullConstantsMalformedAbsentAndWrapOrderingMatch()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), wide = f.Metadata("Int64");
        foreach (var kind in Enum.GetValues<ConversionKind>())
        {
            foreach (var exists in new[] { false, true })
            {
                foreach (var wrap in new[] { false, true })
                {
                    foreach (var hasConstant in new[] { false, true })
                    {
                        foreach (var value in new object?[] { null, -1, 300, "x" })
                        {
                            var c = exists ? new Conversion(kind, false, null, 17, hasConstant ? new ConstantValue(value) : null)
                            { WrapNullable = wrap } : default;
                            HConversion h = new(exists, exists ? (HB.ConversionKind)kind : HB.ConversionKind.Identity, false, null, exists ? 17 : 0,
                                exists && hasConstant ? new H.ConstantValue(value) : null, exists && wrap);
                            var target = wrap ? f.Nullable(wide) : wide;
                            f.CheckCreate(f.Literal(integer, value), c, h, target);
                        }
                    }
                }
            }
        }
        // WrapNullable on an identity is deliberately ignored, even for malformed non-nullable targets.
        f.CheckCreate(f.Value(integer), Conversion.Identity with { WrapNullable = true },
            HConversions.WithNullableWrap(HConversions.Identity), wide);
        f.CheckCreate(f.Value(integer), new Conversion(ConversionKind.Boxing, true) { WrapNullable = true },
            HConversions.WithNullableWrap(HConversions.Implicit(HB.ConversionKind.Boxing)), wide);
    }

    [TestMethod]
    public void NullableUnwrap_UserDefinedMethodChainAndNumericReferenceFollowupsMatch()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), wide = f.Metadata("Int64"), obj = f.Metadata("Object");
        Pair source = f.Named("Source"), target = f.Named("Target");
        (var Original, var Self) = f.AddMethod(source, "op_Explicit", target, [source], MethodKind.Conversion);
        foreach (var from in new[] { source, f.Nullable(source), f.Nullable(f.Nullable(source)) })
        {
            foreach (var to in new[] { target, f.Nullable(target) })
            {
                _ = f.Check(f.Value(from), to, explicitConversion: true);
            }
        }

        foreach (var from in new[] { f.Nullable(integer), f.Nullable(f.Metadata("String")) })
        {
            foreach (var to in new[] { integer, wide, obj, f.Nullable(wide), f.Metadata("String") })
            {
                _ = f.Check(f.Value(from), to, explicitConversion: true);
            }
        }

        f.CheckCreate(f.Value(f.Nullable(integer)), new Conversion(ConversionKind.FromNullable, false, Original, 23),
            HConversions.Create(HB.ConversionKind.FromNullable, false, Self, 23), target);
        // Failed followups still become an identity-kind bound conversion (Exists is not rechecked).
        f.CheckCreate(f.Value(f.Nullable(integer)), new Conversion(ConversionKind.FromNullable, false),
            HConversions.Explicit(HB.ConversionKind.FromNullable), f.Metadata("String"));
    }

    [TestMethod]
    public void Tuples_DirectConstructorAndGeneralSequenceEvaluateSourceOnceAndPreserveIdentities()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), wide = f.Metadata("Int64"), text = f.Metadata("String"), obj = f.Metadata("Object");
        Pair source = f.Tuple("SourceTuple", integer, text), target = f.Tuple("TargetTuple", wide, obj);
        foreach (var syntax in new[] { true, false })
        {
            _ = f.Check(f.Value(source), target, syntax: syntax);
            _ = f.Check(f.Creation(source, [f.Literal(integer, 7), f.Literal(text, "effect")]), target, syntax: syntax);
            _ = f.Check(f.Creation(source, [f.Value(integer), f.Value(text)], constructor: false), target, syntax: syntax);
            _ = f.Check(f.Creation(source, [f.Value(integer)], constructor: true), target, syntax: syntax);
            _ = f.Check(f.Creation(source, [f.Value(integer), f.Value(text)], initializer: true), target, syntax: syntax);
        }
        Pair empty = f.Tuple("EmptySource"), emptyTarget = f.Tuple("EmptyTarget");
        _ = f.Check(f.Value(empty), emptyTarget);
        _ = f.Check(f.Creation(empty, []), emptyTarget);
        var noConstructor = f.Tuple("NoConstructor", wide, obj);
        ((OriginalNamed)noConstructor.Original).Members = [.. ((OriginalNamed)noConstructor.Original).Members.OfType<FieldSymbol>().Cast<Symbol>()];
        ((SelfNamed)noConstructor.Self).Members = [.. ((SelfNamed)noConstructor.Self).Members.OfType<H.FieldSymbol>().Cast<H.Symbol>()];
        _ = f.Check(f.Value(source), noConstructor);
        Pair nested = f.Tuple("Nested", source), nestedTarget = f.Tuple("NestedTarget", target);
        _ = f.Check(f.Value(nested), nestedTarget);
        (var Original, var Self) = f.AddMethod(f.Named("Producer"), "Effect", source, [], MethodKind.Ordinary);
        _ = f.Check(new(new BoundCall(null, Original, []) { Syntax = f.Syntax },
            new HB.BoundCall(null, Self, []) { Syntax = f.SelfSyntax }), target);
        // The tuple marker is handled by CreateConversion, regardless of Exists.
        f.CheckCreate(f.Value(source), new Conversion(ConversionKind.TypeOnly, false, null, -2),
            HConversions.Explicit(HB.ConversionKind.TypeOnly, null, -2), target);
        var invalidTarget = f.Tuple("ForcedInvalid", integer, integer);
        f.CheckCreate(f.Value(source), new Conversion(ConversionKind.TypeOnly, false, null, -2),
            HConversions.Explicit(HB.ConversionKind.TypeOnly, null, -2), invalidTarget);
    }

    [TestMethod]
    public void Unbound_RebindingRecursionFailureAndExplicitRoutingUseImplicitRules()
    {
        Fixture f = new();
        Pair integer = f.Metadata("Int32"), small = f.Metadata("Byte");
        foreach (var explicitConversion in new[] { false, true })
        {
            foreach (var report in new[] { false, true })
            {
                foreach (var syntax in new[] { false, true })
                {
                    foreach (var variant in new[] { 0, 1, 2, 3 })
                    {
                        var bound = f.Value(integer);
                        int calls = 0, selfCalls = 0;
                        BoundUnbound? failed = null;
                        HB.BoundUnbound? selfFailed = null;
                        failed = new(f.Syntax, _ => failed!);
                        selfFailed = new(f.SelfSyntax, _ => selfFailed!);
                        BoundUnbound original = new(f.Syntax, target =>
                        {
                            calls++;
                            Assert.AreSame(small.Original, target);
                            return variant == 0 ? failed : variant == 1 ? new BoundUnbound(f.Syntax, _ => bound.Original) :
                                variant == 2 ? new BoundExpressionStub(null) : bound.Original;
                        });
                        HB.BoundUnbound self = new(f.SelfSyntax, target =>
                        {
                            selfCalls++;
                            Assert.AreSame(small.Self, target);
                            return variant == 0 ? selfFailed : variant == 1 ? new HB.BoundUnbound(f.SelfSyntax, _ => bound.Self) :
                                variant == 2 ? new SelfExpressionStub(null) : bound.Self;
                        });
                        // Returning a second unbound is failure, not another callback invocation.
                        _ = f.Check(new(original, self), small, explicitConversion, report, syntax);
                        Assert.AreEqual(1, calls);
                        Assert.AreEqual(calls, selfCalls);
                    }
                }
            }
        }
    }

    [TestMethod]
    public void MethodGroups_RealMetadataDelegateFixtureAndTypedBoundaryPreserveArgumentsAndReturnIdentity()
    {
        Fixture f = new();
        var integer = f.Metadata("Int32");
        var math = f.Metadata("Math");
        var original = ((NamedTypeSymbol)math.Original).GetMembers("Abs").OfType<MethodSymbol>()
            .Single(m => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.Int32);
        var self = ((H.NamedTypeSymbol)math.Self).GetMembers("Abs").OfType<H.MethodSymbol>()
            .Single(m => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == H.SpecialType.Int32);
        _ = f.RememberIdentity(original, self);
        var definition = f.Metadata("Func`2");
        var target = f.Remember(((NamedTypeSymbol)definition.Original).Construct(new TypeSymbol[] { integer.Original, integer.Original }),
            ((H.NamedTypeSymbol)definition.Self).Construct(new H.TypeSymbol[] { integer.Self, integer.Self }));
        foreach (var explicitConversion in new[] { false, true })
        {
            foreach (var report in new[] { false, true })
            {
                foreach (var syntax in new[] { false, true })
                {
                    BoundMethodGroup group = new(null, "Abs", [original], []) { Syntax = f.Syntax };
                    HB.BoundMethodGroup selfGroup = new(null, "Abs", [self], []) { Syntax = f.SelfSyntax };
                    var oracle = (BoundExpression)typeof(Binder).GetMethod("ConvertMethodGroup", PrivateInstance)!
                        .Invoke(f.Oracle, [group, target.Original, syntax ? f.Syntax : null, report])!;
                    var expected = (BoundDelegateCreation)oracle;
                    HB.BoundDelegateCreation boundaryResult = new(null, self, target.Self) { Syntax = f.SelfSyntax };
                    f.Context.MethodGroup = (g, t, s, r) =>
                    {
                        Assert.AreSame(selfGroup, g);
                        Assert.AreSame(target.Self, t);
                        Assert.AreSame(syntax ? f.SelfSyntax : null, s);
                        Assert.AreEqual(report, r);
                        return boundaryResult;
                    };
                    var result = f.Check(new(group, selfGroup), target, explicitConversion, report, syntax);
                    Assert.AreSame(boundaryResult, result.Self);
                    Assert.AreSame(original, expected.Method);
                }
            }
        }
    }

    [TestMethod]
    public void LibraryIsIndependentAndEntireAssemblyPassesILVerify()
    {
        var library = typeof(Engine).Assembly;
        Assert.IsFalse(library.GetReferencedAssemblies().Any(a => a.Name == "Caladesi.Compiler"));
        Assert.IsEmpty(ILVerifier.Verify(library.GetName().Name!, File.ReadAllBytes(library.Location)));
    }

    private sealed record Pair(TypeSymbol Original, H.TypeSymbol Self);
    private sealed record Expression(BoundExpression Original, HB.BoundExpression Self);

    private sealed class Fixture
    {
        public readonly Compilation Compilation;
        public readonly SourceFile File;
        public readonly Binder Oracle;
        public readonly DiagnosticBag Diagnostics = new();
        public readonly TestContext Context;
        public Engine Engine;
        public readonly SyntaxNode Syntax = SyntaxTree.ParseText("  let conversion = 1;", "conversion.cala").Root;
        public readonly HX.SyntaxNode SelfSyntax = HX.SyntaxTree.ParseText("  let conversion = 1;", "conversion.cala").Root;
        private readonly Dictionary<object, object> identities = new(ReferenceEqualityComparer.Instance);

        public Fixture(string source = "class Context {}")
        {
            Compilation = TestCompiler.Create(source, OutputKind.DynamicallyLinkedLibrary);
            File = Compilation.GetFile(Compilation.SyntaxTrees.Single());
            var container = Named("Container");
            Oracle = new(Compilation, File, container.Original, Diagnostics);
            Context = new TestContext(Metadata("Boolean").Self, container.Self);
            Engine = new(new Classifier(), Context);
            identities.Add(Syntax, SelfSyntax);
        }

        public TSelf RememberIdentity<TSelf>(object original, TSelf self) where TSelf : class
        {
            identities[original] = self;
            return self;
        }

        public Pair Remember(TypeSymbol original, H.TypeSymbol self)
        {
            identities[original] = self;
            return new(original, self);
        }

        public Pair Metadata(string name)
        {
            return Remember(TestCompiler.References.FindType("System." + name)!,
            References.Value.FindType("System." + name)!);
        }

        public Pair Nullable(Pair type)
        {
            return Remember(new NullableTypeSymbol(type.Original), new H.NullableTypeSymbol(type.Self));
        }

        public Pair Named(string name)
        {
            return Remember(new OriginalNamed(name), new SelfNamed(name));
        }

        public Pair Tuple(string name, params Pair[] elements)
        {
            var pair = Named(name);
            ((OriginalNamed)pair.Original).Elements = [.. elements.Select(p => p.Original)];
            ((SelfNamed)pair.Self).Elements = [.. elements.Select(p => p.Self)];
            for (var i = 0; i < elements.Length; i++)
            {
                OriginalField original = new("Item" + (i + 1), elements[i].Original);
                SelfField self = new("Item" + (i + 1), elements[i].Self);
                _ = RememberIdentity(original, self);
                ((OriginalNamed)pair.Original).Members = ((OriginalNamed)pair.Original).Members.Add(original);
                ((SelfNamed)pair.Self).Members = ((SelfNamed)pair.Self).Members.Add(self);
            }
            // An earlier wrong-arity constructor and duplicate correct-arity constructor test first-match selection.
            _ = AddMethod(pair, ".ctor", pair, [.. new Pair[elements.Length + 1].Select(_ => Metadata("Object"))], MethodKind.Constructor);
            _ = AddMethod(pair, ".ctor", pair, elements, MethodKind.Constructor);
            _ = AddMethod(pair, ".ctor", pair, elements, MethodKind.Constructor);
            return pair;
        }

        public (MethodSymbol Original, H.MethodSymbol Self) AddMethod(Pair owner, string name, Pair result,
            Pair[] parameters, MethodKind kind)
        {
            OriginalMethod original = new(name, result.Original, [.. parameters.Select(p => p.Original)], kind);
            SelfMethod self = new(name, result.Self, [.. parameters.Select(p => p.Self)], (H.MethodKind)kind);
            _ = RememberIdentity(original, self);
            ((OriginalNamed)owner.Original).Members = ((OriginalNamed)owner.Original).Members.Add(original);
            ((SelfNamed)owner.Self).Members = ((SelfNamed)owner.Self).Members.Add(self);
            return (original, self);
        }

        public Expression Value(Pair type)
        {
            return new(new BoundDefaultValue(type.Original) { Syntax = Syntax },
            new HB.BoundDefaultValue(type.Self) { Syntax = SelfSyntax });
        }

        public Expression Literal(Pair type, object? value)
        {
            return new(new BoundLiteral(new ConstantValue(value), type.Original) { Syntax = Syntax },
                    new HB.BoundLiteral(new H.ConstantValue(value), type.Self) { Syntax = SelfSyntax });
        }

        public Expression Creation(Pair type, Expression[] arguments, bool constructor = true, bool initializer = false)
        {
            var oc = constructor ? ((NamedTypeSymbol)type.Original).InstanceConstructors.First() : null;
            var hc = constructor ? ((H.NamedTypeSymbol)type.Self).InstanceConstructors.First() : null;
            return new(new BoundObjectCreation(oc, type.Original, [.. arguments.Select(a => a.Original)],
                    initializer ? [new BoundMemberInit(((NamedTypeSymbol)type.Original).GetMembers().OfType<FieldSymbol>().First(), arguments[0].Original)] : [])
            { Syntax = Syntax },
                new HB.BoundObjectCreation(hc, type.Self, [.. arguments.Select(a => a.Self)],
                    initializer ? [new HB.BoundMemberInit(((H.NamedTypeSymbol)type.Self).GetMembers().OfType<H.FieldSymbol>().First(), arguments[0].Self)] : [])
                { Syntax = SelfSyntax });
        }

        public Expression Check(Expression expression, Pair target, bool explicitConversion = false, bool report = true, bool syntax = true)
        {
            Diagnostics.TruncateTo(0);
            Context.Diagnostics.Clear();
            _ = RememberIdentity(expression.Original, expression.Self);
            var method = explicitConversion ? "ConvertExplicit" : "ConvertImplicit";
            var original = (BoundExpression)typeof(Binder).GetMethod(method, PrivateInstance)!
                .Invoke(Oracle, [expression.Original, target.Original, syntax ? Syntax : null, report])!;
            var self = explicitConversion
                ? (report ? Engine.ConvertExplicit(expression.Self, target.Self, syntax ? SelfSyntax : null) :
                    Engine.ConvertExplicit(expression.Self, target.Self, syntax ? SelfSyntax : null, false))
                : (report ? Engine.ConvertImplicit(expression.Self, target.Self, syntax ? SelfSyntax : null) :
                    Engine.ConvertImplicit(expression.Self, target.Self, syntax ? SelfSyntax : null, false));
            Compare(original, self);
            CompareDiagnostics();
            return new(original, self);
        }

        public Expression CheckBool(Expression expression)
        {
            Diagnostics.TruncateTo(0);
            Context.Diagnostics.Clear();
            _ = RememberIdentity(expression.Original, expression.Self);
            var original = (BoundExpression)typeof(Binder).GetMethod("ConvertToBool", PrivateInstance)!
                .Invoke(Oracle, [expression.Original, Syntax])!;
            var self = Engine.ConvertToBool(expression.Self, SelfSyntax);
            Compare(original, self);
            CompareDiagnostics();
            return new(original, self);
        }

        public void CheckCreate(Expression expression, Conversion conversion, HConversion selfConversion, Pair target)
        {
            Diagnostics.TruncateTo(0);
            Context.Diagnostics.Clear();
            _ = RememberIdentity(expression.Original, expression.Self);
            var original = (BoundExpression)typeof(Binder).GetMethod("CreateConversion", PrivateInstance)!
                .Invoke(Oracle, [expression.Original, conversion, target.Original, null])!;
            Compare(original, Engine.CreateConversion(expression.Self, selfConversion, target.Self, null));
            CompareDiagnostics();
        }

        private void CompareDiagnostics()
        {
            Assert.HasCount(Diagnostics.Count, Context.Diagnostics);
            for (var i = 0; i < Diagnostics.Count; i++)
            {
                var expected = Diagnostics.Items[i];
                var actual = Context.Diagnostics[i];
                Assert.AreEqual(expected.Id, actual.Descriptor.Id);
                Assert.AreEqual(expected.Descriptor.MessageFormat, actual.Descriptor.MessageFormat);
                Assert.AreEqual((int)expected.Descriptor.Severity, (int)actual.Descriptor.Severity);
                var descriptor = typeof(Descriptors).GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Single(d => ReferenceEquals(d.GetValue(null), expected.Descriptor));
                Assert.AreSame(typeof(HD.Descriptors).GetProperty(descriptor.Name)!.GetValue(null), actual.Descriptor);
                Assert.AreEqual(expected.Span.Start, actual.Syntax?.Span.Start ?? 0);
                Assert.AreEqual(expected.Span.Length, actual.Syntax?.Span.Length ?? 0);
                CollectionAssert.AreEqual(expected.Arguments, actual.Args);
            }
        }

        private void Compare(BoundNode expected, HB.BoundNode actual)
        {
            Assert.AreEqual(expected.GetType().Name.Replace("BoundExpressionStub", "ExpressionStub"),
                actual is SelfModuleExpression ? nameof(BoundModuleExpression) :
                    actual.GetType().Name.Replace("SelfExpressionStub", "ExpressionStub"));
            if (identities.TryGetValue(expected, out var known))
            {
                Assert.AreSame(known, actual);
            }
            else
            {
                identities.Add(expected, actual);
            }

            Assert.AreSame(expected.Syntax == null ? null : identities[expected.Syntax], actual.Syntax);
            if (expected is BoundExpression originalExpression && actual is HB.BoundExpression selfExpression)
            {
                CompareType(originalExpression.Type, selfExpression.Type);
                Assert.AreEqual(originalExpression.ConstantValue.HasValue, selfExpression.ConstantValue != null);
                Assert.AreEqual(originalExpression.ConstantValue?.Value, selfExpression.ConstantValue?.Value);
                Assert.AreEqual(originalExpression.ConstantValue?.Value?.GetType(), selfExpression.ConstantValue?.Value?.GetType());
            }
            switch (expected)
            {
                case BoundConversion e when actual is HB.BoundConversion a:
                    Assert.AreEqual((int)e.Kind, (int)a.Kind);
                    Assert.AreEqual(e.CaseIndex, a.CaseIndex);
                    CompareSymbol(e.Method, a.Method);
                    Compare(e.Operand, a.Operand);
                    break;
                case BoundBadExpression e when actual is HB.BoundBadExpression a:
                    Assert.HasCount(e.Children.Length, a.Children);
                    for (var i = 0; i < e.Children.Length; i++)
                    {
                        Compare(e.Children[i], a.Children[i]);
                    }

                    break;
                case BoundObjectCreation e when actual is HB.BoundObjectCreation a:
                    CompareSymbol(e.Constructor, a.Constructor);
                    Assert.HasCount(e.Arguments.Length, a.Arguments);
                    Assert.HasCount(e.Initializers.Length, a.Initializers);
                    for (var i = 0; i < e.Arguments.Length; i++)
                    {
                        Compare(e.Arguments[i], a.Arguments[i]);
                    }

                    break;
                case BoundSequence e when actual is HB.BoundSequence a:
                    Assert.HasCount(e.Locals.Length, a.Locals);
                    for (var i = 0; i < e.Locals.Length; i++)
                    {
                        var local = e.Locals[i];
                        var self = a.Locals[i];
                        CompareType(local.Type, self.Type);
                        CompareSymbol(local.ContainingSymbol, self.ContainingSymbol);
                        Assert.AreEqual(local.Name, self.Name);
                        Assert.AreEqual(local.IsConst, self.IsConst);
                        Assert.AreEqual(local.IsSynthesized, self.IsSynthesized);
                        Assert.IsNull(local.DeclaringSyntax);
                        Assert.IsNull(self.DeclaringSyntax);
                        identities[local] = self;
                    }
                    Assert.HasCount(e.SideEffects.Length, a.SideEffects);
                    for (var i = 0; i < e.SideEffects.Length; i++)
                    {
                        Compare(e.SideEffects[i], a.SideEffects[i]);
                    }

                    Compare(e.Value, a.Value);
                    break;
                case BoundLocalDeclaration e when actual is HB.BoundLocalDeclaration a:
                    CompareSymbol(e.Local, a.Local);
                    Compare(e.Initializer!, a.Initializer!);
                    break;
                case BoundFieldAccess e when actual is HB.BoundFieldAccess a:
                    CompareSymbol(e.Field, a.Field);
                    Compare(e.Receiver!, a.Receiver!);
                    break;
                case BoundLocal e when actual is HB.BoundLocal a: CompareSymbol(e.Local, a.Local); break;
                case BoundDelegateCreation e when actual is HB.BoundDelegateCreation a:
                    CompareSymbol(e.Method, a.Method);
                    Assert.IsNull(e.Receiver);
                    Assert.IsNull(a.Receiver);
                    break;
                case BoundCall e when actual is HB.BoundCall a:
                    CompareSymbol(e.Method, a.Method);
                    Assert.IsNull(e.Receiver);
                    Assert.IsNull(a.Receiver);
                    Assert.HasCount(e.Arguments.Length, a.Arguments);
                    break;
            }
        }

        private void CompareType(TypeSymbol? original, H.TypeSymbol? self)
        {
            if (original == null) { Assert.IsNull(self); return; }
            Assert.IsNotNull(self);
            if (identities.TryGetValue(original, out var expected))
            {
                Assert.AreSame(expected, self);
            }
            else if (original is NullableTypeSymbol n && self is H.NullableTypeSymbol h)
            {
                CompareType(n.Underlying, h.Underlying);
            }
            else
            {
                Assert.AreEqual(original.ToDisplayString(), self.ToDisplayString());
            }
        }

        private void CompareSymbol(Symbol? original, H.Symbol? self)
        {
            if (original is MethodSymbol method && !identities.ContainsKey(original) &&
                method.ContainingSymbol is NamedTypeSymbol owner)
            {
                var selfOwner = (H.NamedTypeSymbol)identities[owner];
                MethodSymbol[] methods = [.. owner.GetMembers(method.Name).OfType<MethodSymbol>()];
                var ordinal = Array.FindIndex(methods, m => ReferenceEquals(m, method));
                identities.Add(method, selfOwner.GetMembers(method.Name).OfType<H.MethodSymbol>().ElementAt(ordinal));
            }
            Assert.AreSame(original == null ? null : identities[original], self);
        }
    }

    private sealed class TestContext(H.TypeSymbol boolean, H.Symbol container) : Context
    {
        public readonly List<(HD.DiagnosticDescriptor Descriptor, HX.SyntaxNode? Syntax, object?[] Args)> Diagnostics = [];
        public H.TypeSymbol? LiteralEnum;
        public HB.BoundExpression? ModuleExpression;
        public Func<HB.BoundMethodGroup, H.TypeSymbol, HX.SyntaxNode?, bool, HB.BoundExpression>? MethodGroup;
        public override H.TypeSymbol BoolType => boolean;
        public override H.Symbol ContainingSymbol => container;
        public override bool IsLiteralEnum(H.TypeSymbol type)
        {
            return ReferenceEquals(type, LiteralEnum);
        }

        public override bool IsModuleExpression(HB.BoundExpression expression)
        {
            return ReferenceEquals(expression, ModuleExpression) || base.IsModuleExpression(expression);
        }

        public override void Report(HD.DiagnosticDescriptor descriptor, HX.SyntaxNode? syntax, object?[] args)
        {
            Diagnostics.Add((descriptor, syntax, args));
        }

        public override HB.BoundExpression ConvertMethodGroup(HB.BoundMethodGroup group, H.TypeSymbol target,
                    HX.SyntaxNode? syntax, bool report)
        {
            return MethodGroup!(group, target, syntax, report);
        }
    }

    private sealed class BoundExpressionStub(TypeSymbol? type) : BoundExpression(type);
    private sealed class SelfExpressionStub(H.TypeSymbol? type) : HB.BoundExpression(type);
    private sealed class SelfModuleExpression() : HB.BoundExpression(null);

    private sealed class OriginalNamed(string name) : NamedTypeSymbol
    {
        public ImmutableArray<Symbol> Members = [];
        public ImmutableArray<TypeSymbol> Elements;
        public override string Name => name;
        public override TypeKind TypeKind => TypeKind.Struct;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<TypeSymbol> TypeArguments => Elements.IsDefault ? [] : Elements;
        public override bool IsTuple => !Elements.IsDefault;
        public override ImmutableArray<Symbol> GetMembers()
        {
            return Members;
        }
    }

    private sealed class SelfNamed(string name) : H.NamedTypeSymbol
    {
        public ImmutableArray<H.Symbol> Members = [];
        public ImmutableArray<H.TypeSymbol> Elements;
        public override string Name => name;
        public override H.TypeKind TypeKind => H.TypeKind.Struct;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<H.TypeSymbol> TypeArguments => Elements.IsDefault ? [] : Elements;
        public override bool IsTuple => !Elements.IsDefault;
        public override ImmutableArray<H.Symbol> GetMembers()
        {
            return Members;
        }
    }

    private sealed class OriginalMethod(string name, TypeSymbol result, TypeSymbol[] parameters, MethodKind kind) : MethodSymbol
    {
        public override string Name => name;
        public override bool IsStatic => kind == MethodKind.Conversion;
        public override MethodKind MethodKind => kind;
        public override TypeSymbol ReturnType => result;
        public override ImmutableArray<ParameterSymbol> Parameters =>
            [.. parameters.Select((p, i) => new ParameterSymbol("p" + i, p, i, this))];
    }

    private sealed class SelfMethod(string name, H.TypeSymbol result, H.TypeSymbol[] parameters, H.MethodKind kind) : H.MethodSymbol
    {
        public override string Name => name;
        public override bool IsStatic => kind == H.MethodKind.Conversion;
        public override H.MethodKind MethodKind => kind;
        public override H.TypeSymbol ReturnType => result;
        public override ImmutableArray<H.ParameterSymbol> Parameters =>
            [.. parameters.Select((p, i) => new H.ParameterSymbol("p" + i, p, i, this))];
    }

    private sealed class OriginalField(string name, TypeSymbol type) : FieldSymbol
    {
        public override string Name => name;
        public override TypeSymbol Type => type;
    }

    private sealed class SelfField(string name, H.TypeSymbol type, object? constant = null) : H.FieldSymbol
    {
        public override string Name => name;
        public override H.TypeSymbol Type => type;
        public override H.ConstantValue ConstantValue => new(constant);
    }
}
