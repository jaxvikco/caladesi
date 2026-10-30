extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using C = SelfHost::Caladesi.Compiler;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HS = SelfHost::Caladesi.Compiler.Symbols.Source;
using HX = SelfHost::Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class FileScopeParityTests
{
    [TestMethod]
    public void Declarations_ExportsAliasesAndSourceOnlyModuleVariablesMatch()
    {
        Fixture f = new("""
            export class Public {}
            class Hidden {}
            export type PublicAlias = Public;
            type HiddenAlias = Public;
            export function visible(): int { return 1; }
            function hidden(): int { return 2; }
            export const value: int = 3;
            const secret: int = 4;
            """);
        var file = f.Main;
        var scope = f.GetScope(file);
        Assert.AreSame(scope.Declared, scope.Declared);
        CollectionAssert.AreEqual(file.Scope.Declared.Keys.ToArray(), scope.Declared.Keys.ToArray());
        foreach (var name in file.Scope.Declared.Keys)
        {
            f.Check(file.Scope.Lookup(name), scope.Lookup(name));
            f.Check(file.Scope.GetExports(name), scope.GetExports(name));
        }
        f.Check(file.Scope.GetExports("absent"), scope.GetExports("absent"));
        f.Check(file.Scope.AllExports(), scope.AllExports());
        Assert.IsTrue(scope.GetExports("PublicAlias").Any());
        Assert.IsFalse(scope.GetExports("HiddenAlias").Any());
        Assert.AreEqual(0, f.ImportsRead);
        Assert.IsFalse(ImportsResolved(file.Scope));
        Assert.AreEqual(1, f.TypesRead);
        f.CheckVisible(file);
    }

    [TestMethod]
    public void NamedAliasWildcardAndGlobalPrecedence_OrderingDuplicatesAndIdentityMatch()
    {
        Fixture f = new("""
            import "Root.B";
            import "Root.A";
            import { C as Choice, C as Choice } from "Root.A";
            import * as Choice from "Root.B";
            import * as B from "Root.B";
            import { C as Own } from "Root.A";
            export class Own {}
            """, ["/scope/a/a.cala", "/scope/b/b.cala", "/scope/global.cala"],
            ["export class C {} export class OnlyA {}", "export class C {} export class OnlyB {}", "export class Global {}"],
            globalImports: ["Root.A", "Missing", "Root.A"]);
        var scope = f.GetScope(f.Main);
        foreach (var name in new[] { "Own", "Choice", "C", "OnlyA", "OnlyB", "B", "Root", "Unknown" })
        {
            f.Check(f.Main.Scope.Lookup(name), scope.Lookup(name));
        }

        Assert.HasCount(2, scope.Lookup("Choice"));
        Assert.AreSame(scope.Lookup("Choice")[0], scope.Lookup("Choice")[1]);
        Assert.HasCount(2, scope.Lookup("C"));
        f.Check(f.Main.Scope.WildcardImports, scope.WildcardImports);
        Assert.HasCount(4, scope.WildcardImports);
        Assert.AreSame(scope.WildcardImports, scope.WildcardImports);
        Assert.AreSame(scope.WildcardImports[0], scope.WildcardImports[1]);
        f.Check(f.Main.Scope.NamedImportSymbols, scope.NamedImportSymbols);
        f.CheckVisible(f.Main);
        Assert.IsGreaterThan(scope.Lookup("C").Length, scope.VisibleNames().Count(p => p.Item1 == "C"));
        Assert.AreEqual(1, f.ImportsRead);
    }

    [TestMethod]
    public void RelativeFiles_ExportAccessibilityAliasesIndexPathsAndWildcardVisibilityMatch()
    {
        Fixture f = new("""
            import "./library";
            import "./library";
            import * as Library from "./library";
            import { Public, PublicAlias as Renamed, value, run, Hidden, HiddenAlias, secret } from "./library";
            import { Indexed } from "./folder";
            import { Up } from "../up";
            """, ["/scope/library.cala", "/scope/folder/index.cala", "/up.cala"],
            ["""
             export class Public {}
             class Hidden {}
             export type PublicAlias = Public;
             type HiddenAlias = Public;
             export const value: int = 1;
             const secret: int = 2;
             export function run(): int { return 1; }
             """, "export class Indexed {}", "export class Up {}"], record: true);
        var scope = f.GetScope(f.Main);
        foreach (var name in new[] { "Public", "Renamed", "PublicAlias", "value", "run", "Hidden", "HiddenAlias", "secret", "Library", "Indexed", "Up" })
        {
            f.Check(f.Main.Scope.Lookup(name), scope.Lookup(name));
        }

        f.CheckDiagnostics(f.Main);
        f.CheckVisible(f.Main);
        f.CheckRecorded();
        var module = (C.FileModuleSymbol<SourceFile>)scope.Lookup("Library").Single();
        Assert.AreEqual("Library", module.Name);
        Assert.AreSame(f.Files.Single(file => file.FilePath == "/scope/library.cala"), module.File);
        Assert.HasCount(2, scope.WildcardImports);
        Assert.AreNotSame(scope.WildcardImports[0], scope.WildcardImports[1]);
        Assert.IsFalse(scope.VisibleNames().Any(p => p.Item1 == "PublicAlias"));
        Assert.HasCount(1, scope.Lookup("PublicAlias"));
        Assert.HasCount(3, scope.GetImportDiagnostics());
    }

    [TestMethod]
    public void NamespaceAttributesAndStaticTypeImports_PreserveFallbackAndAccessibilityOddities()
    {
        Fixture f = new("""
            import { Tag, Both, Missing } from "Root.Lib";
            import "Root.Lib.Container";
            import { PublicStatic, PrivateStatic, Instance, Tag } from "Root.Lib.Container";
            import "Container";
            import * as Alias from "Root.Lib";
            """, ["/scope/lib/types.cala", "/scope/lib/both/child.cala"],
            ["""
             export class TagAttribute {}
             export class BothAttribute {}
             class Both {}
             export class Container {
               public static PublicStatic(): int { return 1; }
               private static PrivateStatic(): int { return 2; }
               public Instance(): int { return 3; }
               public static TagAttribute(): int { return 4; }
             }
             """, "export class Child {}"]);
        var scope = f.GetScope(f.Main);
        foreach (var name in new[] { "Tag", "Both", "PublicStatic", "PrivateStatic", "Instance", "TagAttribute", "Alias" })
        {
            f.Check(f.Main.Scope.Lookup(name), scope.Lookup(name));
        }

        Assert.HasCount(2, scope.Lookup("Both"));
        Assert.AreEqual("Both", scope.Lookup("Both")[0].Name);
        _ = Assert.IsInstanceOfType<H.NamespaceSymbol>(scope.Lookup("Both")[1]);
        Assert.AreEqual("TagAttribute", scope.Lookup("Tag").Single().Name);
        Assert.IsTrue(scope.Lookup("PrivateStatic").Any());
        Assert.IsFalse(scope.VisibleNames().Any(p => p.Item1 == "PrivateStatic" && p.Item2.Name == "PrivateStatic" &&
            !scope.NamedImportSymbols.Contains(p.Item2)));
        f.CheckDiagnostics(f.Main);
        f.CheckVisible(f.Main);
        Assert.HasCount(4, scope.GetImportDiagnostics());
    }

    private static void AddNested(Fixture f)
    {
        // Namespace lookup also exercises a synthetic static container with an instance nested type.
        var ns = f.Compilation.GlobalNamespace.GetOrAddNamespace("Root.Static");
        SynthesizedNamedType container = new("Holder", ns, TypeKind.Class, Accessibility.Public);
        SynthesizedNamedType nested = new("Nested", container, TypeKind.Class, Accessibility.Private);
        container.AddMember(nested);
        container.AddMember(new SynthesizedMethodSymbol(container, "privateMember", MethodKind.Ordinary,
            Accessibility.Private, true, ErrorTypeSymbol.Instance, []));
        ns.AddType(container);
        f.AddNamespace(ns);
    }

    [TestMethod]
    public void StaticWildcardLookup_DoesNotFilterPrivateMembersOrNonstaticNestedTypes()
    {
        Fixture f = new("""import "Root.Static.Holder"; import { Nested as N } from "Root.Static.Holder";""");
        AddNested(f);
        var scope = f.GetScope(f.Main);
        foreach (var name in new[] { "Nested", "N", "privateMember" })
        {
            f.Check(f.Main.Scope.Lookup(name), scope.Lookup(name));
        }

        Assert.HasCount(1, scope.Lookup("Nested"));
        Assert.HasCount(1, scope.Lookup("privateMember"));
        Assert.IsFalse(scope.VisibleNames().Any(p => p.Item1 is "Nested" or "privateMember"));
        f.CheckVisible(f.Main);
    }

    [TestMethod]
    public void LazyIteratorsAndCaches_ResolveOnlyAfterOwnPrefixAndNeverRepeat()
    {
        Fixture f = new("""export class Own {} import { Foreign } from "Root.Lib"; import "Missing";""",
            ["/scope/lib/lib.cala"], ["export class Foreign {}"], record: true);
        var scope = f.GetScope(f.Main);
        var expected = f.Main.Scope.VisibleNames();
        var actual = scope.VisibleNames();
        Assert.IsFalse(ImportsResolved(f.Main.Scope));
        Assert.AreEqual(0, f.ImportsRead);
        using var e = expected.GetEnumerator();
        using var a = actual.GetEnumerator();
        Assert.IsTrue(e.MoveNext());
        Assert.IsTrue(a.MoveNext());
        Assert.AreEqual(e.Current.Name, a.Current.Item1);
        f.Check([e.Current.Symbol], [a.Current.Item2]);
        Assert.IsFalse(ImportsResolved(f.Main.Scope));
        Assert.AreEqual(0, f.ImportsRead);
        f.Check(f.Main.Scope.Lookup("Own"), scope.Lookup("Own"));
        Assert.AreEqual(0, f.ImportsRead);
        Assert.IsTrue(e.MoveNext());
        Assert.IsTrue(a.MoveNext());
        Assert.IsTrue(ImportsResolved(f.Main.Scope));
        Assert.AreEqual(1, f.ImportsRead);
        f.CheckRecorded();
        f.CheckDiagnostics(f.Main);
        Assert.AreNotSame(scope.GetImportDiagnostics()[0], scope.GetImportDiagnostics()[0]);
        Assert.AreSame(scope.GetImportDiagnostics()[0].Descriptor, scope.GetImportDiagnostics()[0].Descriptor);
        var originalOwn = f.Main.Scope.Declared["Own"];
        var selfOwn = scope.Declared["Own"];
        originalOwn.Clear();
        selfOwn.Clear();
        Assert.IsEmpty(f.Main.Scope.Lookup("Own"));
        Assert.IsEmpty(scope.Lookup("Own"));
        f.Options.GlobalImports = ["Does.Not.Exist"];
        _ = scope.NamedImportSymbols.ToArray();
        _ = scope.WildcardImports;
        _ = scope.GetImportDiagnostics();
        Assert.AreEqual(1, f.ImportsRead);
        Assert.AreEqual(1, f.TypesRead);
    }

    [TestMethod]
    public void SemanticRecording_ReentrySeesGuardAndOnlyPreviouslyInsertedNamedImports()
    {
        Fixture f = new("""import { A, B as Renamed, Missing } from "Root.Lib";""",
            ["/scope/lib/lib.cala"], ["export class A {} export class B {}"], record: true);
        var scope = f.GetScope(f.Main);
        List<int> prefixes = [];
        f.OnRecord = () =>
        {
            Assert.IsEmpty(scope.GetImportDiagnostics());
            prefixes.Add(scope.NamedImportSymbols.Count());
        };
        f.CheckDiagnostics(f.Main);
        Assert.HasCount(2, prefixes);
        Assert.AreEqual(0, prefixes[0]);
        Assert.AreEqual(1, prefixes[1]);
        f.CheckRecorded();
        Assert.AreEqual(1, f.ImportsRead);
        Fixture disabled = new("""import { A } from "Root.Lib";""",
            ["/scope/lib/lib.cala"], ["export class A {}"]);
        disabled.CheckDiagnostics(disabled.Main);
        Assert.IsEmpty(disabled.Recorded);
        disabled.CheckRecorded();
    }

    [TestMethod]
    public void MalformedImports_MissingNamesAliasesAndSpecifiersMatch()
    {
        Fixture f = new("""import { } from "Missing"; import { Missing } from "Root"; import * as from "Root"; import { A } from ;""");
        f.CheckDiagnostics(f.Main);
        f.CheckVisible(f.Main);
        f.Check(f.Main.Scope.NamedImportSymbols, f.GetScope(f.Main).NamedImportSymbols);
    }

    [TestMethod]
    public void SourceFileNaming_RootRelativeOutsideInvalidAndPascalPathsMatch()
    {
        foreach (var project in new[] { "/scope", "/scope/", "", null, "relative-project", "bad\0project" })
        {
            foreach (var root in new[] { "", "Root.AlreadyDotted" })
            {
                foreach (var path in new[] { "", "/scope/main.cala", "/scope/data-access/my_file.cala",
            "/scope/42 things/--/foo bar.cala", "/other/main.cala", "/scope/..hidden/a.cala", "relative.cala" })
                {
                    CompilationOptions options = new() { ProjectDirectory = project, RootNamespace = root, GlobalImports = [] };
                    var compilation = Compilation.Create(options, [], []);
                    SourceFile original = new(compilation, SyntaxTree.ParseText("", path));
                    C.CompilationOptions actualOptions = new() { ProjectDirectory = project, RootNamespace = root };
                    Assert.AreEqual(original.NamespaceName, C.SourceFileNames.DeriveNamespace(actualOptions, path), $"{project}: {path}");
                    Assert.AreEqual(original.ModuleClassBaseName, C.SourceFileNames.GetModuleClassBaseName(actualOptions, path), path);
                }
            }
        }

        C.CompilationOptions defaults = new();
        Assert.AreEqual("Program", C.SourceFileNames.GetModuleClassBaseName(defaults, "/scope/--.cala"));
        Assert.AreEqual("Root", C.SourceFileNames.DeriveNamespace(
            new C.CompilationOptions { RootNamespace = "Root", ProjectDirectory = "/scope" }, "bad\0name"));
    }

    private static bool ImportsResolved(FileScope scope)
    {
        return (bool)typeof(FileScope).GetField("_importsResolved", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scope)!;
    }

    private sealed class Fixture : C.FileScopeContext<SourceFile>
    {
        private readonly Dictionary<Symbol, H.Symbol> symbols = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<H.Symbol, Symbol> originals = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SourceFile, C.FileScope<SourceFile>> scopes = [];
        private readonly Dictionary<SourceFile, ImmutableArray<HX.ImportDeclarationSyntax>> imports = [];
        public readonly Compilation Compilation;
        public readonly SourceFile[] Files;
        public SourceFile Main => Files[0];
        public override C.CompilationOptions Options { get; }
        public override H.NamespaceSymbol GlobalNamespace { get; }
        public readonly List<(HX.ImportSpecifierSyntax Syntax, H.Symbol Symbol)> Recorded = [];
        public Action? OnRecord;
        public int ImportsRead, TypesRead;

        public Fixture(string source, string[]? paths = null, string[]? sources = null,
            string[]? globalImports = null, bool record = false)
        {
            CompilationOptions options = new()
            {
                RootNamespace = "Root",
                ProjectDirectory = "/scope",
                OutputKind = OutputKind.DynamicallyLinkedLibrary,
                GlobalImports = globalImports?.ToImmutableArray() ?? [],
                RecordSemanticInfo = record
            };
            List<SyntaxTree> trees = [SyntaxTree.ParseText(source, "/scope/main.cala")];
            for (var i = 0; i < (paths?.Length ?? 0); i++)
            {
                trees.Add(SyntaxTree.ParseText(sources![i], paths![i]));
            }

            Compilation = Compilation.Create(options, trees, []);
            Files = [.. trees.Select(Compilation.GetFile)];
            Options = new C.CompilationOptions
            {
                RootNamespace = options.RootNamespace,
                ProjectDirectory = options.ProjectDirectory,
                GlobalImports = options.GlobalImports,
                RecordSemanticInfo = record
            };
            GlobalNamespace = (H.NamespaceSymbol)Map(Compilation.GlobalNamespace);
            AddNamespace(Compilation.GlobalNamespace);
            foreach (var file in Files)
            {
                foreach (NamedTypeSymbol type in file.Types)
                {
                    _ = Map(type);
                }

                foreach (Symbol alias in file.Aliases)
                {
                    _ = Map(alias);
                }

                if (file.ModuleClass is { } module)
                {
                    _ = Map(module);
                }

                var tree = HX.SyntaxTree.ParseText(file.Tree.Text.ToString(), file.FilePath);
                var fileImports = ImmutableArray.CreateBuilder<HX.ImportDeclarationSyntax>();
                foreach (var statement in tree.Root.Statements)
                {
                    if (statement is HX.ImportDeclarationSyntax import)
                    {
                        fileImports.Add(import);
                    }
                }

                imports[file] = fileImports.ToImmutable();
            }
        }

        public void AddNamespace(NamespaceSymbol ns)
        {
            var actual = (H.NamespaceSymbol)Map(ns);
            foreach (var type in ns.GetTypeMembers())
            {
                actual.AddType((H.NamedTypeSymbol)Map(type));
            }

            foreach (var child in ns.GetNamespaceMembers())
            {
                AddNamespace(child);
            }
        }

        private H.Symbol Map(Symbol original)
        {
            if (symbols.TryGetValue(original, out var cached))
            {
                return cached;
            }

            H.Symbol result;
            switch (original)
            {
                case NamespaceSymbol ns:
                    result = ns.IsGlobalNamespace ? new H.NamespaceSymbol("", null) :
                        GlobalNamespace.GetOrAddNamespace(ns.ToDisplayString());
                    Register(original, result);
                    return result;
                case NamedTypeSymbol type:
                    HS.SynthesizedNamedType named = new(type.Name,
                        type.ContainingSymbol is null ? null : Map(type.ContainingSymbol),
                        (H.TypeKind)(int)type.TypeKind, (H.Accessibility)(int)type.DeclaredAccessibility)
                    { OwnArityValue = type.OwnArity, IsStaticFlag = type.IsStatic };
                    Register(original, named);
                    foreach (var member in type.GetMembers())
                    {
                        named.AddMember(Map(member));
                    }

                    return named;
                case MethodSymbol method:
                    result = new HS.SynthesizedMethodSymbol(Map(method.ContainingSymbol!), method.Name,
                        (H.MethodKind)(int)method.MethodKind, (H.Accessibility)(int)method.DeclaredAccessibility,
                        method.IsStatic, H.ErrorTypeSymbol.Instance, []);
                    break;
                case FieldSymbol field:
                    result = new HS.SynthesizedFieldSymbol(Map(field.ContainingSymbol!), field.Name,
                        H.ErrorTypeSymbol.Instance, field.IsStatic, (H.Accessibility)(int)field.DeclaredAccessibility);
                    break;
                default:
                    result = new DeclarationSymbol(original);
                    break;
            }
            Register(original, result);
            return result;
        }

        private void Register(Symbol original, H.Symbol actual)
        {
            symbols.Add(original, actual);
            originals.Add(actual, original);
        }

        public override string GetFilePath(SourceFile file)
        {
            return file.FilePath;
        }

        public override string GetModuleClassBaseName(SourceFile file)
        {
            return file.ModuleClassBaseName;
        }

        public override ImmutableArray<H.NamedTypeSymbol> GetTypes(SourceFile file)
        {
            TypesRead++;
            return [.. file.Types.Select(t => (H.NamedTypeSymbol)Map(t))];
        }
        public override ImmutableArray<H.Symbol> GetAliases(SourceFile file)
        {
            return [.. file.Aliases.Select(Map)];
        }

        public override H.NamedTypeSymbol? GetModuleClass(SourceFile file)
        {
            return file.ModuleClass is null ? null : (H.NamedTypeSymbol)Map(file.ModuleClass);
        }

        public override ImmutableArray<HX.ImportDeclarationSyntax> GetImports(SourceFile file)
        {
            ImportsRead++;
            return imports[file];
        }
        public override bool? IsExportedAlias(H.Symbol symbol)
        {
            return originals.TryGetValue(symbol, out var original) && original is TypeAliasSymbol alias ? alias.IsExported : null;
        }

        public override bool IsModuleVariable(H.FieldSymbol field)
        {
            return originals[field] is SourceFieldSymbol { IsModuleVariable: true };
        }
#pragma warning disable CS8764 // The Cala TFile? boundary does not yet emit unconstrained generic return nullability.
        public override SourceFile? FindSourceFile(SourceFile from, string spec)
        {
            var combined = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(from.FilePath))!, spec));
            string[] candidates = [combined, combined + ".cala", Path.Combine(combined, "index.cala")];
            foreach (var candidate in candidates)
            {
                if (Files.FirstOrDefault(file => Path.GetFullPath(file.FilePath) == candidate) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
#pragma warning restore CS8764
        public override C.FileScope<SourceFile> GetScope(SourceFile file)
        {
            if (!scopes.TryGetValue(file, out var scope))
            {
                scopes.Add(file, scope = new C.FileScope<SourceFile>(this, file));
            }

            return scope;
        }
        public override void RecordImport(HX.ImportSpecifierSyntax specifier, H.Symbol symbol)
        {
            Assert.IsTrue(Options.RecordSemanticInfo);
            Recorded.Add((specifier, symbol));
            OnRecord?.Invoke();
        }

        public void Check(IEnumerable<Symbol> expected, IEnumerable<H.Symbol> actual)
        {
            Symbol[] left = [.. expected];
            H.Symbol[] right = [.. actual];
            Assert.HasCount(left.Length, right);
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] is FileModuleSymbol module)
                {
                    _ = Assert.IsInstanceOfType<C.FileModuleSymbol<SourceFile>>(right[i]);
                    var actualModule = (C.FileModuleSymbol<SourceFile>)right[i];
                    Assert.AreSame(module.File, actualModule.File);
                    Assert.AreEqual(module.Name, actualModule.Name);
                    Assert.AreEqual((int)module.Kind, (int)actualModule.Kind);
                }
                else
                {
                    Assert.AreSame(Map(left[i]), right[i], left[i].Name);
                }
            }
        }

        public void CheckVisible(SourceFile file)
        {
            (string Name, Symbol Symbol)[] expected = [.. file.Scope.VisibleNames()];
            var actual = GetScope(file).VisibleNames().ToArray();
            CollectionAssert.AreEqual(expected.Select(p => p.Name).ToArray(), actual.Select(p => p.Item1).ToArray());
            Check(expected.Select(p => p.Symbol), actual.Select(p => p.Item2));
        }

        public void CheckDiagnostics(SourceFile file)
        {
            var expected = file.Scope.GetImportDiagnostics();
            var actual = GetScope(file).GetImportDiagnostics();
            Assert.HasCount(expected.Length, actual);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].Id, actual[i].Id);
                Assert.AreEqual(expected[i].Message, actual[i].Message);
                Assert.AreEqual(expected[i].FilePath, actual[i].FilePath);
                Assert.AreEqual(expected[i].Span.Start, actual[i].Span.Start);
                Assert.AreEqual(expected[i].Span.Length, actual[i].Span.Length);
                CollectionAssert.AreEqual(expected[i].Arguments, actual[i].Arguments);
            }
        }

        public void CheckRecorded()
        {
            var recorded = (Dictionary<SyntaxNode, Symbol>?)typeof(Compilation)
                .GetProperty("RecordedSymbols", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Compilation);
            KeyValuePair<SyntaxNode, Symbol>[] expected = [.. (recorded ?? []).Where(p => p.Key is ImportSpecifierSyntax).OrderBy(p => p.Key.Span.Start)];
            var actual = Recorded.OrderBy(p => p.Syntax.Span.Start).ToArray();
            Assert.HasCount(expected.Length, actual);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].Key.Span.Start, actual[i].Syntax.Span.Start);
                Check([expected[i].Value], [actual[i].Symbol]);
            }
        }
    }

    private sealed class DeclarationSymbol(Symbol original) : H.Symbol
    {
        public override H.SymbolKind Kind => (H.SymbolKind)(int)original.Kind;
        public override string Name => original.Name;
        // Alias export decisions must come from the context flag, not symbol accessibility.
        public override H.Accessibility DeclaredAccessibility => original is TypeAliasSymbol ?
            H.Accessibility.NotApplicable : (H.Accessibility)(int)original.DeclaredAccessibility;
        public override bool IsStatic => original.IsStatic;
    }
}
