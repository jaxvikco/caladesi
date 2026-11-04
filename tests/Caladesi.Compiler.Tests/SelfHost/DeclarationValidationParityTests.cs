extern alias SelfHost;

using System.Collections.Immutable;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;
using H = SelfHost::Caladesi.Compiler.Symbols;
using C = SelfHost::Caladesi.Compiler;
using HD = SelfHost::Caladesi.Compiler.Diagnostics;
using HS = SelfHost::Caladesi.Compiler.Symbols.Source;
using HX = SelfHost::Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class DeclarationValidationParityTests
{
    [TestMethod]
    public void Implementation_SearchAccessibilityOverridesCaseAndInheritanceMatchOracle()
    {
        foreach (var accessibility in Enum.GetValues<Accessibility>())
        {
            foreach (var isOverride in new[] { false, true })
            {
                foreach (var inherited in new[] { false, true })
                {
                    foreach (var requireOverride in new[] { false, true })
                    {
                        OriginalType value = new("Value");
                        NativeType selfValue = new("Value");
                        OriginalMethod target = new("Run", value);
                        NativeMethod selfTarget = new("Run", selfValue);
                        OriginalMethod candidate = new("run", value, accessibility, isOverride);
                        NativeMethod selfCandidate = new("run", selfValue, (H.Accessibility)(int)accessibility, isOverride);
                        OriginalType owner = new("Owner") { Members = [candidate] };
                        NativeType selfOwner = new("Owner") { Members = [selfCandidate] };
                        NamedTypeSymbol type = inherited ? new OriginalType("Derived") { Base = owner } : owner;
                        H.NamedTypeSymbol selfType = inherited ? new NativeType("Derived") { Base = selfOwner } : selfOwner;
                        var expected = DeclarationChecker.FindImplementation(type, target, requireOverride);
                        var actual = C.DeclarationChecker<string>.FindImplementation(selfType, selfTarget, requireOverride);
                        Assert.AreEqual(expected?.Name, actual?.Name, $"{accessibility}/{isOverride}/{inherited}/{requireOverride}");
                        Assert.AreEqual(expected is null, actual is null);
                        if (actual is not null)
                        {
                            Assert.AreSame(selfCandidate, actual);
                        }
                    }
                }
            }
        }
    }

    [TestMethod]
    public void Implementation_ReferenceNullabilityAndMethodParameterOrdinalsMatchOracle()
    {
        OriginalType value = new("Value");
        NativeType selfValue = new("Value");
        OriginalMethod method = new("Run", new NullableTypeSymbol(value))
        {
            Arguments = [new ParameterSymbol("value", new NullableTypeSymbol(value), 0, null)]
        };
        NativeMethod selfMethod = new("Run", new H.NullableTypeSymbol(selfValue))
        {
            Arguments = [new H.ParameterSymbol("value", new H.NullableTypeSymbol(selfValue), 0, null)]
        };
        OriginalType owner = new("Owner") { Members = [method] };
        NativeType selfOwner = new("Owner") { Members = [selfMethod] };
        OriginalMethod target = new("Run", value)
        {
            Arguments = [new ParameterSymbol("value", value, 0, null)]
        };
        NativeMethod selfTarget = new("Run", selfValue)
        {
            Arguments = [new H.ParameterSymbol("value", selfValue, 0, null)]
        };
        Assert.AreSame(method, DeclarationChecker.FindImplementation(owner, target, false));
        Assert.AreSame(selfMethod, C.DeclarationChecker<string>.FindImplementation(selfOwner, selfTarget, false));
        foreach (var ordinal in new[] { 0, 1 })
        {
            method.Arguments = [new ParameterSymbol("x", new TypeParameterSymbol("T", 0, method, VarianceKind.None), 0, method)];
            target.Arguments = [new ParameterSymbol("x", new TypeParameterSymbol("U", ordinal, target, VarianceKind.None), 0, target)];
            selfMethod.Arguments = [new H.ParameterSymbol("x", new H.TypeParameterSymbol("T", 0, selfMethod, H.VarianceKind.None), 0, selfMethod)];
            selfTarget.Arguments = [new H.ParameterSymbol("x", new H.TypeParameterSymbol("U", ordinal, selfTarget, H.VarianceKind.None), 0, selfTarget)];
            Assert.AreEqual(DeclarationChecker.FindImplementation(owner, target, false) is null,
                C.DeclarationChecker<string>.FindImplementation(selfOwner, selfTarget, false) is null);
        }
    }

    [TestMethod]
    public void Implementation_MethodKindsStaticAbstractAndGenericArityMatchOracle()
    {
        foreach (var kind in Enum.GetValues<MethodKind>())
        {
            foreach (var isStatic in new[] { false, true })
            {
                foreach (var isAbstract in new[] { false, true })
                {
                    foreach (var generic in new[] { false, true })
                    {
                        OriginalType value = new("Value");
                        NativeType selfValue = new("Value");
                        OriginalMethod candidate = new("Run", value)
                        {
                            Kind = kind,
                            StaticFlag = isStatic,
                            AbstractFlag = isAbstract
                        };
                        NativeMethod selfCandidate = new("Run", selfValue)
                        {
                            Kind = (H.MethodKind)(int)kind,
                            StaticFlag = isStatic,
                            AbstractFlag = isAbstract
                        };
                        if (generic)
                        {
                            candidate.GenericParameters = [new TypeParameterSymbol("T", 0, candidate, VarianceKind.None)];
                            selfCandidate.GenericParameters = [new H.TypeParameterSymbol("T", 0, selfCandidate, H.VarianceKind.None)];
                        }
                        OriginalMethod target = new("Run", value);
                        NativeMethod selfTarget = new("Run", selfValue);
                        OriginalType owner = new("Owner") { Members = [candidate] };
                        NativeType selfOwner = new("Owner") { Members = [selfCandidate] };
                        Assert.AreEqual(DeclarationChecker.FindImplementation(owner, target, false)?.Name,
                            C.DeclarationChecker<string>.FindImplementation(selfOwner, selfTarget, false)?.Name,
                            $"{kind}/{isStatic}/{isAbstract}/{generic}");
                        Assert.AreEqual(DeclarationChecker.FindBridgeTarget(owner, target)?.Name,
                            C.DeclarationChecker<string>.FindBridgeTarget(selfOwner, selfTarget)?.Name);
                    }
                }
            }
        }
    }

    [TestMethod]
    public void Bridges_ReferenceCovarianceInterfacesValueTypesAndParametersMatchOracle()
    {
        foreach (var valueType in new[] { false, true })
        {
            foreach (var throughInterface in new[] { false, true })
            {
                foreach (var parameterMatches in new[] { false, true })
                {
                    OriginalType result = new("Result");
                    NativeType selfResult = new("Result");
                    OriginalType derived = new("Derived", valueType ? TypeKind.Struct : TypeKind.Class);
                    NativeType selfDerived = new("Derived", valueType ? H.TypeKind.Struct : H.TypeKind.Class);
                    if (throughInterface) { derived.Implemented = [result]; selfDerived.Implemented = [selfResult]; }
                    else { derived.Base = result; selfDerived.Base = selfResult; }
                    OriginalType argument = new("Argument");
                    NativeType selfArgument = new("Argument");
                    OriginalMethod candidate = new("Get", derived)
                    {
                        Arguments = [new ParameterSymbol("x", parameterMatches ? result : argument, 0, null)]
                    };
                    NativeMethod selfCandidate = new("Get", selfDerived)
                    {
                        Arguments = [new H.ParameterSymbol("x", parameterMatches ? selfResult : selfArgument, 0, null)]
                    };
                    OriginalMethod target = new("Get", result) { Arguments = [new ParameterSymbol("x", result, 0, null)] };
                    NativeMethod selfTarget = new("Get", selfResult) { Arguments = [new H.ParameterSymbol("x", selfResult, 0, null)] };
                    OriginalType owner = new("Owner") { Members = [candidate] };
                    NativeType selfOwner = new("Owner") { Members = [selfCandidate] };
                    Assert.AreEqual(DeclarationChecker.FindBridgeTarget(owner, target)?.Name,
                        C.DeclarationChecker<string>.FindBridgeTarget(selfOwner, selfTarget)?.Name);
                    Assert.AreEqual(!valueType && parameterMatches,
                        C.DeclarationChecker<string>.FindBridgeTarget(selfOwner, selfTarget) is not null);
                }
            }
        }
    }

    [TestMethod]
    public void PropertyAccessors_ImplementationBridgesAndOverrideLookupMatchOracle()
    {
        foreach (var setter in new[] { false, true })
        {
            foreach (var covariance in new[] { false, true })
            {
                foreach (var requireOverride in new[] { false, true })
                {
                    OriginalType value = new("Value");
                    NativeType selfValue = new("Value");
                    OriginalType derived = new("Derived") { Base = value };
                    NativeType selfDerived = new("Derived") { Base = selfValue };
                    OriginalProperty property = new("value", covariance ? derived : value);
                    NativeProperty selfProperty = new("value", covariance ? selfDerived : selfValue);
                    OriginalMethod candidate = new(setter ? "set_value" : "get_value", value, Accessibility.Private, true)
                    {
                        Kind = setter ? MethodKind.PropertySet : MethodKind.PropertyGet,
                        Associated = property
                    };
                    NativeMethod selfCandidate = new(setter ? "set_value" : "get_value", selfValue, H.Accessibility.Private, true)
                    {
                        Kind = setter ? H.MethodKind.PropertySet : H.MethodKind.PropertyGet,
                        Associated = selfProperty
                    };
                    if (setter) { property.Setter = candidate; selfProperty.Setter = selfCandidate; }
                    else { property.Getter = candidate; selfProperty.Getter = selfCandidate; }
                    OriginalProperty targetProperty = new("Value", value);
                    NativeProperty selfTargetProperty = new("Value", selfValue);
                    OriginalMethod target = new("target", value)
                    {
                        Kind = candidate.Kind,
                        Associated = targetProperty
                    };
                    NativeMethod selfTarget = new("target", selfValue)
                    {
                        Kind = selfCandidate.Kind,
                        Associated = selfTargetProperty
                    };
                    OriginalType owner = new("Owner") { Members = [property] };
                    NativeType selfOwner = new("Owner") { Members = [selfProperty] };
                    Assert.AreEqual(DeclarationChecker.FindImplementation(owner, target, requireOverride)?.Name,
                        C.DeclarationChecker<string>.FindImplementation(selfOwner, selfTarget, requireOverride)?.Name);
                    Assert.AreEqual(DeclarationChecker.FindBridgeTarget(owner, target)?.Name,
                        C.DeclarationChecker<string>.FindBridgeTarget(selfOwner, selfTarget)?.Name);
                    OriginalType subclass = new("Subclass") { Base = owner };
                    NativeType selfSubclass = new("Subclass") { Base = selfOwner };
                    Assert.AreEqual(DeclarationChecker.FindOverriddenProperty(subclass, targetProperty)?.Name,
                        C.DeclarationChecker<string>.FindOverriddenProperty(selfSubclass, selfTargetProperty)?.Name);
                    Assert.AreSame(selfProperty,
                        C.DeclarationChecker<string>.FindOverriddenProperty(selfSubclass, selfTargetProperty));
                }
            }
        }
    }

    [TestMethod]
    public void NativeEmptyOwnership_DeclarationPhasesAndEntryPointOptions()
    {
        EmptyContext context = new();
        var declarations = new C.DeclarationChecker<string>(context);
        declarations.CheckAll();
        Assert.IsEmpty(declarations.Diagnostics);
        var entry = new C.EntryPointChecker<string>(context);
        Assert.IsNull(entry.FindEntryPoint());
        Assert.AreEqual(HD.Descriptors.NoEntryPoint.Id, entry.Check().Single().Id);
        context.Options.GenerateTestEntryPoint = true;
        Assert.IsEmpty(entry.Check());
        context.Options.GenerateTestEntryPoint = false;
        context.Options.OutputKind = C.OutputKind.DynamicallyLinkedLibrary;
        Assert.IsEmpty(entry.Check());
    }

    [TestMethod]
    public void BaseTypesAndSelf_EnumeratesNearestFirstWithoutDroppingSelf()
    {
        NativeType root = new("Root");
        NativeType middle = new("Middle") { Base = root };
        NativeType leaf = new("Leaf") { Base = middle };
        CollectionAssert.AreEqual(new H.NamedTypeSymbol[] { leaf, middle, root },
            C.TypeExtensions.BaseTypesAndSelf(leaf).ToArray());
    }

    [TestMethod]
    public void DecoratorHelper_NullAndUnrelatedSyntaxReturnEmpty()
    {
        Assert.IsEmpty(C.DecoratorSyntaxHelper.Of(null));
        Assert.IsEmpty(C.DecoratorSyntaxHelper.Of(HX.SyntaxTree.ParseText("const value = 1;", "decorators.cala").Root));
        const string source = """
            @Obsolete("class") class C {
              @Obsolete("constructor") constructor(@Obsolete("parameter") value: int) {}
              @Obsolete("method") run(): void {}
              @Obsolete("property") value: int;
              @Obsolete("getter") get label(): string { return ""; }
              @Obsolete("setter") set label(value: string) {}
            }
            @Obsolete("function") function run(): void {}
            """;
        string[] expected = [.. SyntaxTree.ParseText(source).Root.DescendantNodesAndSelf()
            .SelectMany(DecoratorSyntaxHelper.Of)
            .Select(d => $"{d.Span.Start}:{d.Span.Length}")];
        string[] actual = [.. HX.SyntaxTree.ParseText(source).Root.DescendantNodesAndSelf()
            .SelectMany(C.DecoratorSyntaxHelper.Of)
            .Select(d => $"{d.Span.Start}:{d.Span.Length}")];
        CollectionAssert.AreEqual(expected, actual);
        Assert.IsGreaterThan(0, actual.Length);
    }

    [TestMethod]
    public void FullCheckAll_RealNativeDeclarationsMatchOracleCrossMemberDiagnostics()
    {
        string[] sources =
        [
            "class C { method(x: string): void {} method(x: string | null): void {} value: int; value: int; }",
            "class Base { virtual run(): int { return 1; } } class Derived extends Base { run(): int { return 2; } }",
            "class Base { virtual run(): int { return 1; } } class Derived extends Base { override run(): string { return \"x\"; } }",
            "abstract class Base { abstract run(): void; } class Derived extends Base {}",
            "interface I { run(): void; } class C implements I {}",
            "class C { override run(): void {} }",
            "class C { abstract run(): void; }",
            "type Record = { value: int; }; type Alias = Record; class C {}",
            "class C { generic<T extends object>(value: T): T { return value; } }"
        ];
        foreach (var source in sources)
        {
            CompilationFixture fixture = new([source], OutputKind.DynamicallyLinkedLibrary);
            DeclarationChecker expected = new(fixture.Original);
            var actual = new C.DeclarationChecker<C.SourceFile>(fixture);
            expected.CheckAll();
            actual.CheckAll();
            CollectionAssert.AreEqual(expected.Diagnostics.Select(Format).ToArray(),
                actual.Diagnostics.Select(Format).ToArray(), source);
            CollectionAssert.AreEqual(
                fixture.Original.AllSourceTypes.SelectMany(t => t.GetMembers()).OfType<Compiler.Symbols.Source.SourceMethodSymbol>()
                    .Select(m => m.MetadataName).ToArray(),
                fixture.Native.AllSourceTypes.SelectMany(t => t.GetMembers()).OfType<HS.SourceMethodSymbol<C.SourceFile>>()
                    .Select(m => m.MetadataName).ToArray(), source);
        }
    }

    [TestMethod]
    public void FullCheckAll_UsesFreshOwnershipSnapshotsAndDelegatesEveryDecorator()
    {
        CompilationFixture fixture = new(
            ["""
             @Obsolete("class") class C {
               @Obsolete("method") run(): void {}
               @Obsolete("property") value: int;
             }
             @Obsolete("function") function run(): void {}
             """], OutputKind.DynamicallyLinkedLibrary);
        var checker = new C.DeclarationChecker<C.SourceFile>(fixture);
        checker.CheckAll();
        Assert.AreEqual(5, fixture.TypeEnumerations);
        Assert.IsGreaterThan(0, fixture.Attributes.Count);
        foreach (var request in fixture.Attributes)
        {
            Assert.AreSame(request.File, request.Owner.File);
            Assert.AreEqual("validation0.cala", request.File.FilePath);
        }
        string[] expected = [.. fixture.Native.AllSourceTypes.SelectMany(type =>
            type.DecoratorSyntaxes.Concat(type.GetMembers().SelectMany(member => C.DecoratorSyntaxHelper.Of(member switch
            {
                HS.SourceMethodSymbol<C.SourceFile> method => method.Declaration,
                HS.SourcePropertySymbol<C.SourceFile> property => property.Declaration,
                HS.SourceFieldSymbol<C.SourceFile> field => field.Declaration,
                _ => null
            })))).Select(decorator => $"{decorator.Span.Start}:{decorator.Span.Length}")];
        CollectionAssert.AreEqual(expected,
            fixture.Attributes.Select(request => $"{request.Decorator.Span.Start}:{request.Decorator.Span.Length}").ToArray());
    }

    [TestMethod]
    public void EntryPoints_RealNativeModulesSignaturesOrderingAndLibrarySpansMatchOracle()
    {
        (string[] Sources, OutputKind Kind)[] cases =
        [
            (["class C {}"], OutputKind.ConsoleApplication),
            (["function main(): void {}"], OutputKind.ConsoleApplication),
            (["function main(args: string[]): int { return 0; }"], OutputKind.ConsoleApplication),
            (["function main(args: int): string { return \"bad\"; }"], OutputKind.ConsoleApplication),
            (["function main(): Task<int> { return 0; }"], OutputKind.ConsoleApplication),
            (["function main(): void {}", "function main(): void {}"], OutputKind.ConsoleApplication),
            (["function main(): void {}", "const value: int = 1; value;"], OutputKind.ConsoleApplication),
            (["const value: int = 1; value;"], OutputKind.DynamicallyLinkedLibrary),
            (["function main(): void {}"], OutputKind.DynamicallyLinkedLibrary),
            (["1;", "2;"], OutputKind.ConsoleApplication)
        ];
        foreach ((var sources, var kind) in cases)
        {
            CompilationFixture fixture = new(sources, kind);
            EntryPointChecker expected = new(fixture.Original);
            var actual = new C.EntryPointChecker<C.SourceFile>(fixture);
            CollectionAssert.AreEqual(expected.Check().Select(Format).ToArray(),
                actual.Check().Select(Format).ToArray(), string.Join("\n---\n", sources));
            Assert.AreEqual(expected.FindEntryPoint()?.Name, actual.FindEntryPoint()?.Name);
            Assert.AreEqual(expected.FindEntryPoint()?.File.FilePath, actual.FindEntryPoint()?.File.FilePath);
        }
    }

    private static string Format(Diagnostics.Diagnostic diagnostic)
    {
        return $"{diagnostic.Id}|{diagnostic.FilePath}|{diagnostic.Span.Start}|{diagnostic.Span.Length}|{diagnostic.Message}";
    }

    private static string Format(HD.Diagnostic diagnostic)
    {
        return $"{diagnostic.Id}|{diagnostic.FilePath}|{diagnostic.Span.Start}|{diagnostic.Span.Length}|{diagnostic.Message}";
    }

    private sealed class CompilationFixture : C.DeclarationValidationContext<C.SourceFile>
    {
        public Compilation Original { get; }
        public C.Compilation Native { get; }
        public int TypeEnumerations { get; private set; }
        public List<(C.SourceFile File, HX.DecoratorSyntax Decorator, HS.SourceNamedType<C.SourceFile> Owner)> Attributes { get; } = [];

        public CompilationFixture(string[] sources, OutputKind kind)
        {
            CompilationOptions options = new() { OutputKind = kind };
            var nativeOptions = new C.CompilationOptions { OutputKind = (C.OutputKind)(int)kind };
            Original = Compilation.Create(options,
                sources.Select((source, i) => SyntaxTree.ParseText(source, $"validation{i}.cala")), TestCompiler.References);
            Native = C.Compilation.Create(nativeOptions,
                sources.Select((source, i) => HX.SyntaxTree.ParseText(source, $"validation{i}.cala")), TestReferences.Paths);
            _ = Original.AllSourceTypes.ToArray();
            _ = Native.AllSourceTypes.ToArray();
        }

        public override IEnumerable<HS.SourceNamedType<C.SourceFile>> AllSourceTypes
        {
            get
            {
                TypeEnumerations++;
                return Native.AllSourceTypes;
            }
        }
        public override IEnumerable<C.SourceFile> SourceFiles => Native.SourceFiles;
        public override C.CompilationOptions Options => Native.Options;
        public override IEnumerable<HS.TypeAliasSymbol<C.SourceFile>> GetAliases(C.SourceFile file)
        {
            return file.Aliases;
        }

        public override HX.SyntaxTree GetTree(C.SourceFile file)
        {
            return file.Tree;
        }

        public override string GetFilePath(C.SourceFile file)
        {
            return file.FilePath;
        }

        public override HS.SourceModuleClassSymbol<C.SourceFile>? GetModuleClass(C.SourceFile file)
        {
            return file.ModuleClass;
        }

        public override C.BoundAttribute? GetBoundAttribute(C.SourceFile file, HX.DecoratorSyntax decorator,
            HS.SourceNamedType<C.SourceFile> owner)
        {
            Attributes.Add((file, decorator, owner));
            return null;
        }
    }

    private sealed class OriginalType(string name, TypeKind kind = TypeKind.Class) : NamedTypeSymbol
    {
        public ImmutableArray<Symbol> Members { get; set; } = [];
        public NamedTypeSymbol? Base { get; set; }
        public ImmutableArray<NamedTypeSymbol> Implemented { get; set; } = [];
        public override string Name => name;
        public override TypeKind TypeKind => kind;
        public override NamedTypeSymbol? BaseType => Base;
        public override ImmutableArray<NamedTypeSymbol> Interfaces => Implemented;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<TypeSymbol> TypeArguments => [];
        public override ImmutableArray<Symbol> GetMembers()
        {
            return Members;
        }

        public override ImmutableArray<Symbol> GetMembers(string name)
        {
            return [.. Members.Where(m => m.Name == name)];
        }
    }

    private sealed class NativeType(string name, H.TypeKind kind = H.TypeKind.Class) : H.NamedTypeSymbol
    {
        public ImmutableArray<H.Symbol> Members { get; set; } = [];
        public H.NamedTypeSymbol? Base { get; set; }
        public ImmutableArray<H.NamedTypeSymbol> Implemented { get; set; } = [];
        public override string Name => name;
        public override H.TypeKind TypeKind => kind;
        public override H.NamedTypeSymbol? BaseType => Base;
        public override ImmutableArray<H.NamedTypeSymbol> Interfaces => Implemented;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<H.TypeSymbol> TypeArguments => [];
        public override ImmutableArray<H.Symbol> GetMembers()
        {
            return Members;
        }

        public override ImmutableArray<H.Symbol> GetMembers(string name)
        {
            return [.. Members.Where(m => m.Name == name)];
        }
    }

    private sealed class OriginalMethod(string name, TypeSymbol result,
        Accessibility accessibility = Accessibility.Public, bool isOverride = false) : MethodSymbol
    {
        public ImmutableArray<ParameterSymbol> Arguments { get; set; } = [];
        public new MethodKind Kind { get; set; } = MethodKind.Ordinary;
        public Symbol? Associated { get; set; }
        public bool StaticFlag { get; set; }
        public bool AbstractFlag { get; set; }
        public ImmutableArray<TypeParameterSymbol> GenericParameters { get; set; } = [];
        public override string Name => name;
        public override MethodKind MethodKind => Kind;
        public override Symbol? AssociatedSymbol => Associated;
        public override TypeSymbol ReturnType => result;
        public override ImmutableArray<ParameterSymbol> Parameters => Arguments;
        public override Accessibility DeclaredAccessibility => accessibility;
        public override bool IsOverride => isOverride;
        public override bool IsStatic => StaticFlag;
        public override bool IsAbstract => AbstractFlag;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => GenericParameters;
    }

    private sealed class NativeMethod(string name, H.TypeSymbol result,
        H.Accessibility accessibility = H.Accessibility.Public, bool isOverride = false) : H.MethodSymbol
    {
        public ImmutableArray<H.ParameterSymbol> Arguments { get; set; } = [];
        public new H.MethodKind Kind { get; set; } = H.MethodKind.Ordinary;
        public H.Symbol? Associated { get; set; }
        public bool StaticFlag { get; set; }
        public bool AbstractFlag { get; set; }
        public ImmutableArray<H.TypeParameterSymbol> GenericParameters { get; set; } = [];
        public override string Name => name;
        public override H.MethodKind MethodKind => Kind;
        public override H.Symbol? AssociatedSymbol => Associated;
        public override H.TypeSymbol ReturnType => result;
        public override ImmutableArray<H.ParameterSymbol> Parameters => Arguments;
        public override H.Accessibility DeclaredAccessibility => accessibility;
        public override bool IsOverride => isOverride;
        public override bool IsStatic => StaticFlag;
        public override bool IsAbstract => AbstractFlag;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => GenericParameters;
    }

    private sealed class OriginalProperty(string name, TypeSymbol type) : PropertySymbol
    {
        public MethodSymbol? Getter { get; set; }
        public MethodSymbol? Setter { get; set; }
        public override string Name => name;
        public override TypeSymbol Type => type;
        public override MethodSymbol? GetMethod => Getter;
        public override MethodSymbol? SetMethod => Setter;
        public override bool IsVirtual => true;
        public override Accessibility DeclaredAccessibility => Accessibility.Public;
    }

    private sealed class NativeProperty(string name, H.TypeSymbol type) : H.PropertySymbol
    {
        public H.MethodSymbol? Getter { get; set; }
        public H.MethodSymbol? Setter { get; set; }
        public override string Name => name;
        public override H.TypeSymbol Type => type;
        public override H.MethodSymbol? GetMethod => Getter;
        public override H.MethodSymbol? SetMethod => Setter;
        public override bool IsVirtual => true;
        public override H.Accessibility DeclaredAccessibility => H.Accessibility.Public;
    }

    private sealed class EmptyContext : C.DeclarationValidationContext<string>
    {
        public override IEnumerable<HS.SourceNamedType<string>> AllSourceTypes => [];
        public override IEnumerable<string> SourceFiles => [];
        public override C.CompilationOptions Options { get; } = new();
        public override IEnumerable<HS.TypeAliasSymbol<string>> GetAliases(string file)
        {
            return [];
        }

        public override HX.SyntaxTree GetTree(string file)
        {
            return HX.SyntaxTree.ParseText("", file);
        }

        public override string GetFilePath(string file)
        {
            return file;
        }

        public override HS.SourceModuleClassSymbol<string>? GetModuleClass(string file)
        {
            return null;
        }

        public override C.BoundAttribute? GetBoundAttribute(string file, HX.DecoratorSyntax decorator, HS.SourceNamedType<string> owner)
        {
            return null;
        }
    }
}
