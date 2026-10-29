extern alias SelfHost;

using Caladesi.Compiler.Tests.Emit;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using C = SelfHost::Caladesi.Compiler;
using D = SelfHost::Caladesi.Compiler.Diagnostics;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using HS = SelfHost::Caladesi.Compiler.Symbols.Source;
using HX = SelfHost::Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class CompilationIntegrationTests
{
    private static readonly Lazy<HM.ReferenceSet> References = new(() => HM.ReferenceSet.Create(TestReferences.Paths));

    [TestMethod]
    public void FilesDeclarationsImportsAndModuleCollisionUseRealCompilationOwnership()
    {
        string[] paths = ["/project/models/person.cala", "/project/main.cala", "/project/main-file.cala"];
        string[] sources =
        [
            "export class Person {} export interface IItem {} export enum Color { Red, Blue }",
            "import { Person } from './models/person'; export class Main {} export function run(): int { return 1; }",
            "export function other(): int { return 2; }",
        ];
        Compilation original = CreateOriginal(paths, sources);
        C.Compilation port = CreatePort(paths, sources);
        Assert.AreEqual(original.SourceFiles.Count(), port.SourceFiles.Count());
        foreach (var pair in original.SourceFiles.Zip(port.SourceFiles))
        {
            Assert.AreSame(port, pair.Second.Compilation);
            Assert.AreEqual(pair.First.FilePath, pair.Second.FilePath);
            SourceFile file = original.GetFile(pair.First.Tree);
            C.SourceFile actual = port.GetFile(pair.Second.Tree);
            Assert.AreSame(actual, port.GetFile(pair.Second.Tree));
            Assert.AreEqual(file.NamespaceName, actual.NamespaceName);
            Assert.AreEqual(file.Namespace.ToDisplayString(), actual.Namespace.ToDisplayString());
            Assert.AreEqual(file.ModuleClass?.Name, actual.ModuleClass?.Name);
            CollectionAssert.AreEqual(file.Types.Select(t => t.MetadataName).ToArray(),
                actual.Types.Select(t => t.MetadataName).ToArray());
            CollectionAssert.AreEqual(file.Scope.Declared.Keys.ToArray(), actual.Scope.Declared.Keys.ToArray());
        }
        C.SourceFile main = port.GetFile(port.SyntaxTrees[1]);
        Assert.AreEqual("MainModule", main.ModuleClass!.Name);
        Assert.AreSame(port.GetFile(port.SyntaxTrees[0]).Types[0], main.Scope.Lookup("Person").Single());
        Assert.AreSame(port.GlobalNamespace, port.SourceAssembly.GlobalNamespace);
        Assert.AreEqual(original.SourceAssembly.Name, port.SourceAssembly.Name);
        Assert.AreEqual(original.SourceAssembly.Version, port.SourceAssembly.Version);
    }

    [TestMethod]
    public void DuplicateGenericDeclarationsAndPathBasedModulesMatchBootstrap()
    {
        string[] paths = ["/project/a/item.cala", "/project/a/other.cala", "/project/a/a-file.cala", "/project/a/a_file.cala"];
        string[] sources =
        [
            "export class Item {} export class Item<T> {}",
            "export class Item {} export class Item<T> {} export class Item<T, U> {}",
            "export function first(): void {}",
            "export function second(): void {}",
        ];
        Compilation original = CreateOriginal(paths, sources);
        C.Compilation port = CreatePort(paths, sources);
        foreach (var pair in original.SyntaxTrees.Zip(port.SyntaxTrees))
        {
            SourceFile file = original.GetFile(pair.First);
            C.SourceFile actual = port.GetFile(pair.Second);
            CollectionAssert.AreEqual(file.Types.Select(t => t.MetadataName).ToArray(),
                actual.Types.Select(t => t.MetadataName).ToArray());
            Assert.AreEqual(file.ModuleClass?.Name, actual.ModuleClass?.Name);
        }
        var expected = original.DeclarationDiagnostics.Items.Select(d => (d.Id, d.FilePath, d.Span.Start, d.Message)).ToArray();
        var actualDiagnostics = port.DeclarationDiagnostics.Items.Select(d => (d.Id, d.FilePath, d.Span.Start, d.Message)).ToArray();
        CollectionAssert.AreEqual(expected, actualDiagnostics);
        Assert.HasCount(2, actualDiagnostics);
    }

    [TestMethod]
    public void MetadataCachesAndImmutableCompilationUpdatesPreserveReferenceOwnership()
    {
        C.Compilation port = CreatePort(["/project/main.cala"], ["export class Local {}"]);
        H.NamedTypeSymbol integer = port.GetSpecialType(H.SpecialType.Int32);
        Assert.AreSame(integer, port.GetSpecialType(H.SpecialType.Int32));
        Assert.AreSame(port.References.FindType("System.Int32"), integer);
        Assert.AreSame(integer, port.GetWellKnownType("System.Int32"));
        Assert.IsNull(port.GetWellKnownType("Missing.DoesNotExist"));
        Assert.IsNull(port.GetWellKnownType("Missing.DoesNotExist"));
        H.NamedTypeSymbol source = port.GetFile(port.SyntaxTrees[0]).Types[0];
        Assert.AreSame(source, port.GetWellKnownType("Root.Local"));
        C.Compilation changed = port.WithSyntaxTrees([HX.SyntaxTree.ParseText("export class Other {}", "/project/other.cala")]);
        Assert.AreSame(port.References, changed.References);
        Assert.AreNotSame(port.SourceAssembly, changed.SourceAssembly);
        Assert.AreSame(port.Options, changed.Options);
        Assert.AreEqual("Other", changed.GetFile(changed.SyntaxTrees[0]).Types.Single().Name);
        Assert.AreEqual("Local", port.GetFile(port.SyntaxTrees[0]).Types.Single().Name);
        C.Compilation renamed = port.WithOptions(new C.CompilationOptions { AssemblyName = "Changed" });
        Assert.AreEqual("Changed", renamed.SourceAssembly.Name);
        Assert.AreSame(port.References, renamed.References);
        Assert.AreSame(port.SyntaxTrees[0], renamed.SyntaxTrees[0]);
    }

    [TestMethod]
    public void WellKnownSourceLookupPrecedesMetadataButSpecialTypesStayMetadataOwned()
    {
        C.Compilation port = C.Compilation.Create(new C.CompilationOptions
        {
            OutputKind = C.OutputKind.DynamicallyLinkedLibrary,
            RootNamespace = "System",
        }, [HX.SyntaxTree.ParseText("export class Object {}", "types.cala")], References.Value);
        H.NamedTypeSymbol source = port.GetFile(port.SyntaxTrees[0]).Types.Single();
        Assert.AreSame(source, port.GetWellKnownType("System.Object"));
        Assert.AreSame(source, port.GetWellKnownType("System.Object"));
        Assert.AreSame(port.References.GetSpecialType(H.SpecialType.Object), port.GetSpecialType(H.SpecialType.Object));
        Assert.AreNotSame(source, port.GetSpecialType(H.SpecialType.Object));
        Assert.ThrowsExactly<IndexOutOfRangeException>(() => port.GetSpecialType((H.SpecialType)(-1)));
    }

    [TestMethod]
    public void RelativeLookupUsesExactExtensionThenDirectoryIndexAndRejectsAbsentFiles()
    {
        string[] paths = ["/project/main.cala", "/project/lib", "/project/lib.cala", "/project/folder/index.cala"];
        C.Compilation port = CreatePort(paths, Enumerable.Repeat("", paths.Length).ToArray());
        C.SourceFile from = port.GetFile(port.SyntaxTrees[0]);
        Assert.AreSame(port.GetFile(port.SyntaxTrees[1]), port.FindSourceFile(from, "./lib"));
        Assert.AreSame(port.GetFile(port.SyntaxTrees[2]), port.FindSourceFile(from, "./lib.cala"));
        Assert.AreSame(port.GetFile(port.SyntaxTrees[3]), port.FindSourceFile(from, "./folder"));
        Assert.IsNull(port.FindSourceFile(from, "./missing"));
        Assert.IsNull(port.FindTypeByFullName("NoNamespace"));
    }

    [TestMethod]
    [DataRow("export function add(a: int, b: int): int { return a + b; }")]
    [DataRow("export class Box<T> { value: T; constructor(value: T) { this.value = value; } }")]
    [DataRow("export enum Number { First = 4, Second = First + 3, Third }")]
    [DataRow("export function bad(): int { return missingName; }")]
    [DataRow("export class Item {} export class Item {}")]
    [DataRow("import { Missing } from './absent'; export function f(): void {}")]
    [DataRow("export function broken(value: Missing): int { return value; }")]
    [DataRow("export class MissingImplementation implements IDisposable {}")]
    public void FullDiagnosticPipelineMatchesBootstrapAndCachesItsResult(string source)
    {
        Compilation original = CreateOriginal(["/project/main.cala"], [source]);
        C.Compilation port = CreatePort(["/project/main.cala"], [source]);
        var expected = original.GetDiagnostics().Select(d =>
            (d.Id, d.FilePath, d.Span.Start, d.Span.Length, d.Message, (int)d.EffectiveSeverity)).ToArray();
        var diagnostics = port.GetDiagnostics();
        var actual = diagnostics.Select(d =>
            (d.Id, d.FilePath, d.Span.Start, d.Span.Length, d.Message, (int)d.EffectiveSeverity)).ToArray();
        CollectionAssert.AreEqual(expected, actual);
        Assert.AreEqual(diagnostics, port.GetDiagnostics());
        Assert.AreEqual(original.HasErrors, port.HasErrors);
    }

    [TestMethod]
    public void ParametersConstraintsBodiesAndEnumConstantsAreBoundByTheOwnedCompiler()
    {
        C.Compilation port = CreatePort(["/project/main.cala"], ["""
            export enum Number { First = 4, Second = First + 3, Third }
            export function identity<T extends class>(value: T): T { return value; }
            export function sum(value: int): int { let result = 0; for (let i = 0; i < value; i++) result += i; return result; }
            """]);
        AssertNoErrors(port);
        C.SourceFile file = port.GetFile(port.SyntaxTrees[0]);
        H.NamedTypeSymbol numbers = file.Types.Single(t => t.Name == "Number");
        CollectionAssert.AreEqual(new object[] { 4, 7, 8 }, numbers.GetMembers().OfType<H.FieldSymbol>()
            .Select(f => f.ConstantValue!.Value!).ToArray());
        HS.SourceMethodSymbol<C.SourceFile> identity = file.ModuleClass!.GetMembers()
            .OfType<HS.SourceMethodSymbol<C.SourceFile>>().Single(m => m.Name == "identity");
        Assert.AreSame(identity.TypeParameters[0], identity.Parameters[0].Type);
        Assert.AreSame(identity.TypeParameters[0], identity.ReturnType);
        Assert.IsTrue(identity.TypeParameters[0].HasReferenceTypeConstraint);
        Assert.IsNotNull(identity.GetBoundBody());
        Assert.AreSame(identity.GetBoundBody(), identity.GetBoundBody());
        Assert.AreEqual(10, ExecuteLibrary(port, "sum", 5));
    }

    [TestMethod]
    public void EmitExecutesImportedGenericFunctionsClassesArraysAndControlFlow()
    {
        C.Compilation port = CreatePort(["/project/math.cala", "/project/main.cala"], [
            "export function identity<T>(value: T): T { return value; }",
            """
            import { identity } from './math';
            export class Counter {
              value: int;
              constructor(value: int) { this.value = value; }
              add(value: int): int { this.value += value; return this.value; }
            }
            export function evaluate(input: int): int {
              const counter = new Counter(identity<int>(input));
              const values = [1, 2, 3];
              for (const value of values) counter.add(value);
              try { return counter.value; } finally { counter.add(1); }
            }
            """
        ]);
        AssertNoErrors(port);
        Assert.AreEqual(16, ExecuteLibrary(port, "evaluate", 10));
        Assert.AreEqual(3, ExecuteLibrary(port, "evaluate", -3));
    }

    [TestMethod]
    public void EmitProducesPortablePdbAndDeterministicImages()
    {
        C.Compilation port = CreatePort(["/project/main.cala"],
            ["export function evaluate(value: int): int { return value * 2 + 1; }"]);
        using MemoryStream first = new();
        using MemoryStream second = new();
        using MemoryStream pdb = new();
        Assert.IsTrue(port.Emit(first, pdb, "Integration.pdb").Success);
        Assert.IsTrue(port.Emit(second).Success);
        Assert.IsTrue(first.Length > 0);
        Assert.IsTrue(pdb.Length > 0);
        pdb.Position = 0;
        using MetadataReaderProvider reader = MetadataReaderProvider.FromPortablePdbStream(pdb);
        Assert.AreEqual("/project/main.cala", reader.GetMetadataReader().GetString(
            reader.GetMetadataReader().GetDocument(reader.GetMetadataReader().Documents.Single()).Name));
        using PEReader pe = new(new MemoryStream(second.ToArray()));
        Assert.IsTrue(pe.HasMetadata);
        Assert.AreEqual("Caladesi", pe.GetMetadataReader().GetString(pe.GetMetadataReader().GetAssemblyDefinition().Name));
        using MemoryStream third = new();
        Assert.IsTrue(port.Emit(third).Success);
        CollectionAssert.AreEqual(second.ToArray(), third.ToArray());
    }

    [TestMethod]
    public void InvalidProgramsDoNotWriteEitherOutputStream()
    {
        C.Compilation port = CreatePort(["/project/main.cala"],
            ["export function evaluate(): int { return unknown; }"]);
        using MemoryStream pe = new();
        using MemoryStream pdb = new();
        var result = port.Emit(pe, pdb);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(0L, pe.Length);
        Assert.AreEqual(0L, pdb.Length);
        Assert.IsTrue(result.Diagnostics.Any(d => d.EffectiveSeverity == D.DiagnosticSeverity.Error));
    }

    [TestMethod]
    public void NullEqualityPreservesEvaluationOfNonNullableOperands()
    {
        C.Compilation port = CreatePort(["/project/main.cala"], ["""
            let calls: int = 0;
            function touch(): int { calls++; return 1; }
            export function evaluate(input: int): int {
              calls = 0;
              if (touch() == null) calls += 100;
              if (null != touch()) calls += 10;
              return calls;
            }
            """]);
        AssertNoErrors(port);
        Assert.AreEqual(12, ExecuteLibrary(port, "evaluate", 0));
    }

    [TestMethod]
    public void ConsoleEntryPointIsSelectedValidatedEmittedAndExecuted()
    {
        C.Compilation port = C.Compilation.Create(new C.CompilationOptions
        {
            OutputKind = C.OutputKind.ConsoleApplication,
            RootNamespace = "Root",
            ProjectDirectory = "/project",
        }, [HX.SyntaxTree.ParseText("function main(): int { return 42; }", "/project/main.cala")], References.Value);
        using MemoryStream image = new();
        var result = port.Emit(image);
        Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        image.Position = 0;
        AssemblyLoadContext context = new("SelfHostEntry_" + Guid.NewGuid(), isCollectible: true);
        try
        {
            Assembly assembly = context.LoadFromStream(image);
            Assert.IsNotNull(assembly.EntryPoint);
            Assert.AreEqual(42, assembly.EntryPoint.Invoke(null, null));
        }
        finally { context.Unload(); }
    }

    [TestMethod]
    public void SemanticModelRecordsDeclarationParameterLocalAndReferenceIdentities()
    {
        const string source = "export function sum(value: int): int { const result = value + 1; return result; }";
        C.Compilation port = CreatePort(["/project/main.cala"], [source]);
        C.CompilationOptions options = new()
        {
            OutputKind = C.OutputKind.DynamicallyLinkedLibrary,
            RootNamespace = "Root",
            ProjectDirectory = "/project",
            RecordSemanticInfo = true,
        };
        port = port.WithOptions(options);
        var model = port.GetSemanticModel(port.SyntaxTrees[0]);
        var function = model.GetSymbolAt(source.IndexOf("sum", StringComparison.Ordinal));
        var parameter = model.GetSymbolAt(source.IndexOf("value", StringComparison.Ordinal));
        var parameterUse = model.GetSymbolAt(source.LastIndexOf("value", StringComparison.Ordinal));
        var local = model.GetSymbolAt(source.IndexOf("result", StringComparison.Ordinal));
        var localUse = model.GetSymbolAt(source.LastIndexOf("result", StringComparison.Ordinal));
        Assert.IsNotNull(function);
        Assert.IsNotNull(parameter);
        Assert.IsNotNull(parameterUse);
        Assert.IsNotNull(local);
        Assert.IsNotNull(localUse);
        Assert.AreSame(parameter.Symbol, parameterUse.Symbol);
        Assert.AreSame(local.Symbol, localUse.Symbol);
        Assert.AreEqual("sum", function.Symbol.Name);
        Assert.AreEqual(H.SpecialType.Int32, model.GetExpressionTypeAt(
            source.LastIndexOf("value", StringComparison.Ordinal))!.SpecialType);
        Assert.ThrowsExactly<ArgumentException>(() =>
            CreatePort(["/project/main.cala"], [source]).GetSemanticModel(HX.SyntaxTree.ParseText(source)));
    }

    [TestMethod]
    public void SemanticImportsAreRecordedBeforeAnyBodyBinderIsCreated()
    {
        const string imported = "export class Item {}";
        const string source = "import { Item } from './item'; export function identity(value: Item): Item { return value; }";
        C.Compilation port = C.Compilation.Create(new C.CompilationOptions
        {
            OutputKind = C.OutputKind.DynamicallyLinkedLibrary,
            RootNamespace = "Root",
            ProjectDirectory = "/project",
            RecordSemanticInfo = true,
        }, [HX.SyntaxTree.ParseText(imported, "/project/item.cala"), HX.SyntaxTree.ParseText(source, "/project/main.cala")],
            References.Value);
        var model = port.GetSemanticModel(port.SyntaxTrees[1]);
        var declaration = port.GetSemanticModel(port.SyntaxTrees[0]).GetSymbolAt(imported.IndexOf("Item", StringComparison.Ordinal));
        var import = model.GetSymbolAt(source.IndexOf("Item", StringComparison.Ordinal));
        var reference = model.GetSymbolAt(source.LastIndexOf("Item", StringComparison.Ordinal));
        Assert.IsNotNull(declaration);
        Assert.IsNotNull(import);
        Assert.IsNotNull(reference);
        Assert.AreSame(declaration.Symbol, import.Symbol);
        Assert.AreSame(declaration.Symbol, reference.Symbol);
    }

    [TestMethod]
    public void DiagnosticOptionsDeduplicateSortSuppressAndPromoteWithoutMutatingDiagnostics()
    {
        C.Compilation port = CreatePort(["/project/main.cala"], [""]);
        D.Diagnostic warning = D.Diagnostic.Create(D.Descriptors.InstanceOfImpossible, "/project/z.cala",
            new SelfHost::Caladesi.Compiler.Text.TextSpan(10, 2), "int", "string");
        D.Diagnostic error = D.Diagnostic.Create(D.Descriptors.NameNotFound, "/project/a.cala",
            new SelfHost::Caladesi.Compiler.Text.TextSpan(1, 2), "missing");
        port.Options.WarningsAsErrors = true;
        var promoted = port.ApplyDiagnosticOptions([warning, error, warning]);
        Assert.HasCount(2, promoted);
        Assert.AreSame(error, promoted[0]);
        Assert.AreEqual(D.DiagnosticSeverity.Error, promoted[1].EffectiveSeverity);
        Assert.AreEqual(D.DiagnosticSeverity.Warning, warning.EffectiveSeverity);
        port.Options.NoWarn = [warning.Id.ToLowerInvariant()];
        var suppressed = port.ApplyDiagnosticOptions([warning, error]);
        Assert.HasCount(1, suppressed);
        Assert.AreSame(error, suppressed[0]);
    }

    [TestMethod]
    public void RealBinderWarningsHonorSuppressionAndWarningsAsErrorsDuringEmit()
    {
        const string source = "export function impossible(value: int): bool { return value instanceof string; }";
        C.Compilation port = CreatePort(["/project/main.cala"], [source]);
        var warning = port.GetDiagnostics().Single(d => d.Id == D.Descriptors.InstanceOfImpossible.Id);
        Assert.AreEqual(D.DiagnosticSeverity.Warning, warning.EffectiveSeverity);
        C.Compilation promoted = port.WithOptions(new C.CompilationOptions
        {
            OutputKind = C.OutputKind.DynamicallyLinkedLibrary,
            WarningsAsErrors = true,
        });
        Assert.IsTrue(promoted.HasErrors);
        using MemoryStream rejected = new();
        Assert.IsFalse(promoted.Emit(rejected).Success);
        Assert.AreEqual(0L, rejected.Length);
        C.Compilation suppressed = port.WithOptions(new C.CompilationOptions
        {
            OutputKind = C.OutputKind.DynamicallyLinkedLibrary,
            WarningsAsErrors = true,
            NoWarn = [warning.Id.ToLowerInvariant()],
        });
        Assert.IsFalse(suppressed.HasErrors);
        Assert.IsFalse(suppressed.GetDiagnostics().Any(d => d.Id == warning.Id));
        using MemoryStream accepted = new();
        Assert.IsTrue(suppressed.Emit(accepted).Success);
        Assert.IsTrue(accepted.Length > 0);
    }

    [TestMethod]
    public void AnonymousRecordShapesAreCachedAndObjectLiteralsExecute()
    {
        C.Compilation port = CreatePort(["/project/main.cala"], ["""
            export function evaluate(value: int): int {
              const record = { value: value, extra: 2 };
              return record.value + record.extra;
            }
            """]);
        AssertNoErrors(port);
        H.NamedTypeSymbol integerShape = port.GetAnonymousRecord(["value", "extra"],
            [port.GetSpecialType(H.SpecialType.Int32), port.GetSpecialType(H.SpecialType.Int32)]);
        H.NamedTypeSymbol stringShape = port.GetAnonymousRecord(["value", "extra"],
            [port.GetSpecialType(H.SpecialType.String), port.GetSpecialType(H.SpecialType.Int32)]);
        Assert.AreSame(integerShape.OriginalDefinition, stringShape.OriginalDefinition);
        Assert.AreNotSame(integerShape, stringShape);
        Assert.AreEqual(H.SpecialType.String, stringShape.GetMembers("value").OfType<H.PropertySymbol>().Single().Type.SpecialType);
        Assert.AreEqual(12, ExecuteLibrary(port, "evaluate", 10));
    }

    [TestMethod]
    public void DecoratorsAreBoundOnceAndWrittenAsRealCustomAttributes()
    {
        C.Compilation port = CreatePort(["/project/main.cala"], ["@Obsolete('old') export class Item {}"]);
        AssertNoErrors(port);
        C.SourceFile file = port.GetFile(port.SyntaxTrees[0]);
        var syntax = (HX.ClassDeclarationSyntax)file.Tree.Root.Statements.Single();
        var decorator = syntax.Decorators.Single();
        var bound = port.GetBoundAttribute(file, decorator, file.Types[0]);
        Assert.IsNotNull(bound);
        Assert.AreSame(bound, port.GetBoundAttribute(file, decorator, file.Types[0]));
        Assert.AreEqual("ObsoleteAttribute", bound.AttributeType.Name);
        using MemoryStream image = new();
        Assert.IsTrue(port.Emit(image).Success);
        image.Position = 0;
        using PEReader pe = new(image);
        MetadataReader reader = pe.GetMetadataReader();
        TypeDefinition item = reader.TypeDefinitions.Select(reader.GetTypeDefinition)
            .Single(t => reader.GetString(t.Name) == "Item");
        Assert.HasCount(1, item.GetCustomAttributes());
    }

    [TestMethod]
    [DataRow("function main(): string { return 'bad'; }")]
    [DataRow("function main(value: int): int { return value; }")]
    [DataRow("export function helper(): void {}")]
    public void ConsoleEntryPointDiagnosticsMatchBootstrap(string source)
    {
        Compilation original = Compilation.Create(new CompilationOptions { OutputKind = OutputKind.ConsoleApplication },
            [Syntax.SyntaxTree.ParseText(source, "main.cala")], TestReferences.Paths);
        C.Compilation port = C.Compilation.Create(new C.CompilationOptions { OutputKind = C.OutputKind.ConsoleApplication },
            [HX.SyntaxTree.ParseText(source, "main.cala")], References.Value);
        CollectionAssert.AreEqual(original.GetDiagnostics().Select(d => (d.Id, d.Message)).ToArray(),
            port.GetDiagnostics().Select(d => (d.Id, d.Message)).ToArray());
        Assert.IsTrue(port.HasErrors);
    }

    [TestMethod]
    public void FullCompilerSourceCorpusBindsAndEmitsThroughBothCompilers()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Caladesi.slnx"))) root = root.Parent;
        Assert.IsNotNull(root, "Repository root not found.");
        string directory = Path.Combine(root.FullName, "src", "Caladesi.Compiler.SelfHost");
        string[] paths = Directory.EnumerateFiles(directory, "*.cala", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.IsTrue(paths.Length > 100, "The complete compiler source corpus must be included.");
        string[] sources = paths.Select(File.ReadAllText).ToArray();
        List<string> references = [.. TestReferences.Paths];
        string runtime = Path.Combine(AppContext.BaseDirectory, "Caladesi.Runtime.SelfHost.dll");
        if (File.Exists(runtime)) references.Add(runtime);
        Compilation original = Compilation.Create(new CompilationOptions
        {
            OutputKind = OutputKind.DynamicallyLinkedLibrary,
            AssemblyName = "CompilerCorpus",
            RootNamespace = "Caladesi.Compiler",
            ProjectDirectory = directory,
        }, sources.Select((s, i) => Syntax.SyntaxTree.ParseText(s, paths[i])), references);
        C.Compilation port = C.Compilation.Create(new C.CompilationOptions
        {
            OutputKind = C.OutputKind.DynamicallyLinkedLibrary,
            AssemblyName = "CompilerCorpus",
            RootNamespace = "Caladesi.Compiler",
            ProjectDirectory = directory,
        }, sources.Select((s, i) => HX.SyntaxTree.ParseText(s, paths[i])), references);
        var expected = original.GetDiagnostics().Select(d =>
            (d.Id, d.FilePath, d.Span.Start, d.Span.Length, d.Message, (int)d.EffectiveSeverity)).ToArray();
        var actual = port.GetDiagnostics().Select(d =>
            (d.Id, d.FilePath, d.Span.Start, d.Span.Length, d.Message, (int)d.EffectiveSeverity)).ToArray();
        CollectionAssert.AreEqual(expected, actual, "Full-corpus binding diagnostics diverged.");
        Assert.IsFalse(original.HasErrors, string.Join("\n", expected.Select(d => $"{d.FilePath}:{d.Start}: {d.Id}: {d.Message}")));
        AssertNoErrors(port);
        using MemoryStream bootstrapImage = new();
        using MemoryStream nativeImage = new();
        Assert.IsTrue(original.Emit(bootstrapImage).Success);
        var emitted = port.Emit(nativeImage);
        Assert.IsTrue(emitted.Success, string.Join("\n", emitted.Diagnostics.Select(d => $"{d.Id}: {d.Message}")));
        using PEReader bootstrapReader = new(new MemoryStream(bootstrapImage.ToArray()));
        using PEReader nativeReader = new(new MemoryStream(nativeImage.ToArray()));
        MetadataReader expectedMetadata = bootstrapReader.GetMetadataReader();
        MetadataReader actualMetadata = nativeReader.GetMetadataReader();
        CollectionAssert.AreEqual(expectedMetadata.TypeDefinitions.Select(t =>
            expectedMetadata.GetString(expectedMetadata.GetTypeDefinition(t).Name)).Order(StringComparer.Ordinal).ToArray(),
            actualMetadata.TypeDefinitions.Select(t =>
                actualMetadata.GetString(actualMetadata.GetTypeDefinition(t).Name)).Order(StringComparer.Ordinal).ToArray());
        Assert.IsEmpty(ILVerifier.Verify("CompilerCorpus", nativeImage.ToArray()));
    }

    private static void AssertNoErrors(C.Compilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.EffectiveSeverity == D.DiagnosticSeverity.Error).ToArray();
        Assert.IsEmpty(errors, string.Join("\n", errors.Select(d => $"{d.Id}: {d.Message}")));
    }

    private static object? ExecuteLibrary(C.Compilation compilation, string methodName, params object?[] arguments)
    {
        using MemoryStream image = new();
        var result = compilation.Emit(image);
        Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.Message}")));
        List<string> problems = ILVerifier.Verify(compilation.Options.AssemblyName, image.ToArray());
        Assert.IsEmpty(problems, string.Join("\n", problems));
        image.Position = 0;
        AssemblyLoadContext context = new("SelfHostCompilation_" + Guid.NewGuid(), isCollectible: true);
        try
        {
            Assembly assembly = context.LoadFromStream(image);
            MethodInfo method = assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Single(m => m.Name == methodName);
            return method.Invoke(null, arguments);
        }
        finally { context.Unload(); }
    }

    private static Compilation CreateOriginal(string[] paths, string[] sources)
    {
        return Compilation.Create(new CompilationOptions
        {
            OutputKind = OutputKind.DynamicallyLinkedLibrary,
            RootNamespace = "Root",
            ProjectDirectory = "/project",
        }, sources.Select((source, i) => Syntax.SyntaxTree.ParseText(source, paths[i])), TestReferences.Paths);
    }

    private static C.Compilation CreatePort(string[] paths, string[] sources)
    {
        return C.Compilation.Create(new C.CompilationOptions
        {
            OutputKind = C.OutputKind.DynamicallyLinkedLibrary,
            RootNamespace = "Root",
            ProjectDirectory = "/project",
        }, sources.Select((source, i) => HX.SyntaxTree.ParseText(source, paths[i])), References.Value);
    }
}
