using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests;

internal static class TestCompiler
{
    private static readonly Lazy<ReferenceSet> s_refs = new(() =>
    {
        List<string> paths = [.. TestReferences.Paths];
        paths.Add(typeof(Runtime.CaladesiUnionAttribute).Assembly.Location);
        return ReferenceSet.Create(paths);
    });

    public static ReferenceSet References => s_refs.Value;

    public static Compilation Create(string source, OutputKind kind = OutputKind.ConsoleApplication, string path = "/proj/main.cala", params (string path, string text)[] more)
    {
        List<SyntaxTree> trees = [SyntaxTree.ParseText(source, path)];
        foreach ((string? p, string? t) in more)
        {
            trees.Add(SyntaxTree.ParseText(t, p));
        }

        CompilationOptions options = new() { OutputKind = kind, AssemblyName = "Test", RootNamespace = "Test", ProjectDirectory = "/proj" };
        return Compilation.Create(options, trees, References);
    }

    public static Compilation CreateWithSemantics(string source, string path = "/proj/main.cala", params (string path, string text)[] more)
    {
        List<SyntaxTree> trees = [SyntaxTree.ParseText(source, path)];
        foreach ((string? p, string? t) in more)
        {
            trees.Add(SyntaxTree.ParseText(t, p));
        }

        CompilationOptions options = new() { AssemblyName = "Test", RootNamespace = "Test", ProjectDirectory = "/proj", RecordSemanticInfo = true };
        return Compilation.Create(options, trees, References);
    }

    public static ImmutableArray<Diagnostic> Diagnose(string source, OutputKind kind = OutputKind.ConsoleApplication)
    {
        return Create(source, kind).GetDiagnostics();
    }

    public static string Format(IEnumerable<Diagnostic> ds)
    {
        return string.Join("\n", ds.Select(d => d.ToString()));
    }
}
