using System.Collections.Immutable;
using Caladesi.Compiler;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests;

internal static class TestCompiler
{
    private static readonly Lazy<ReferenceSet> s_refs = new(() =>
    {
        var paths = TestReferences.Paths.ToList();
        paths.Add(typeof(Caladesi.Runtime.CaladesiUnionAttribute).Assembly.Location);
        return ReferenceSet.Create(paths);
    });

    public static ReferenceSet References => s_refs.Value;

    public static Compilation Create(string source, OutputKind kind = OutputKind.ConsoleApplication, string path = "/proj/main.cala", params (string path, string text)[] more)
    {
        var trees = new List<SyntaxTree> { SyntaxTree.ParseText(source, path) };
        foreach (var (p, t) in more) trees.Add(SyntaxTree.ParseText(t, p));
        var options = new CompilationOptions { OutputKind = kind, AssemblyName = "Test", RootNamespace = "Test", ProjectDirectory = "/proj" };
        return Compilation.Create(options, trees, References);
    }

    public static ImmutableArray<Diagnostic> Diagnose(string source, OutputKind kind = OutputKind.ConsoleApplication) =>
        Create(source, kind).GetDiagnostics();

    public static string Format(IEnumerable<Diagnostic> ds) => string.Join("\n", ds.Select(d => d.ToString()));
}
