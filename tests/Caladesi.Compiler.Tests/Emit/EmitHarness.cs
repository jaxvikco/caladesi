using System.Reflection;
using System.Runtime.Loader;
using Caladesi.Compiler.Emit;

namespace Caladesi.Compiler.Tests.Emit;

internal static class EmitHarness
{
    private sealed class Alc() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            return null;
        }
    }

    private static readonly Lock s_lock = new();

    /// <summary>Compiles, loads into a collectible context, runs the entry point and returns captured stdout.</summary>
    public static (string Output, int ExitCode) Run(string source, params (string path, string text)[] more)
    {
        return Execute(TestCompiler.Create(source, OutputKind.ConsoleApplication, "/proj/main.cala", more));
    }

    /// <summary>Like <see cref="Run"/> with extra reference assemblies (for example a C# fixture in the test assembly).</summary>
    public static (string Output, int ExitCode) RunWithReferences(string source, params string[] extraReferences)
    {
        List<string> paths = [.. TestReferences.Paths, typeof(Runtime.CaladesiUnionAttribute).Assembly.Location, .. extraReferences];
        CompilationOptions options = new() { OutputKind = OutputKind.ConsoleApplication, AssemblyName = "Test", RootNamespace = "Test", ProjectDirectory = "/proj" };
        return Execute(Compilation.Create(options, [Compiler.Syntax.SyntaxTree.ParseText(source, "/proj/main.cala")], Compiler.Symbols.Metadata.ReferenceSet.Create(paths)));
    }

    private static (string Output, int ExitCode) Execute(Compilation comp)
    {
        using MemoryStream pe = new();
        using MemoryStream pdb = new();
        EmitResult result = comp.Emit(pe, pdb, "Test.pdb");
        if (!result.Success)
        {
            throw new InvalidOperationException("Compilation failed:\n" + TestCompiler.Format(result.Diagnostics.Where(d => d.EffectiveSeverity == Diagnostics.DiagnosticSeverity.Error)));
        }

        byte[] image = pe.ToArray();
        List<string> problems = ILVerifier.Verify("Test", image);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("IL verification failed:\n" + string.Join("\n", problems));
        }

        pe.Position = 0;
        Alc alc = new();
        try
        {
            Assembly asm = alc.LoadFromStream(pe);
            MethodInfo entry = asm.EntryPoint ?? throw new InvalidOperationException("no entry point");
            lock (s_lock)
            {
                TextWriter original = Console.Out;
                using StringWriter writer = new() { NewLine = "\n" };
                Console.SetOut(writer);
                try
                {
                    object[]? args = entry.GetParameters().Length == 0 ? null : [Array.Empty<string>()];
                    object? r = entry.Invoke(null, args);
                    return (writer.ToString(), r is int i ? i : 0);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    throw new InvalidOperationException("Program threw: " + e.InnerException, e.InnerException);
                }
                finally { Console.SetOut(original); }
            }
        }
        finally { alc.Unload(); }
    }
}
