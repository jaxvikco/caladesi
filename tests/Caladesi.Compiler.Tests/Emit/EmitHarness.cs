using System.Reflection;
using System.Runtime.Loader;
using Caladesi.Compiler;

namespace Caladesi.Compiler.Tests.Emit;

internal static class EmitHarness
{
    private sealed class Alc : AssemblyLoadContext
    {
        private readonly Dictionary<string, string> _paths;
        public Alc(Dictionary<string, string> paths) : base(isCollectible: true) => _paths = paths;
        protected override Assembly? Load(AssemblyName name) => null;
    }

    private static readonly object s_lock = new();

    /// <summary>Compiles, loads into a collectible context, runs the entry point and returns captured stdout.</summary>
    public static (string Output, int ExitCode) Run(string source, params (string path, string text)[] more)
    {
        var comp = TestCompiler.Create(source, OutputKind.ConsoleApplication, "/proj/main.cala", more);
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = comp.Emit(pe, pdb, "Test.pdb");
        if (!result.Success)
            throw new InvalidOperationException("Compilation failed:\n" + TestCompiler.Format(result.Diagnostics.Where(d => d.EffectiveSeverity == Caladesi.Compiler.Diagnostics.DiagnosticSeverity.Error)));
        var image = pe.ToArray();
        var problems = ILVerifier.Verify("Test", image);
        if (problems.Count > 0)
            throw new InvalidOperationException("IL verification failed:\n" + string.Join("\n", problems));
        pe.Position = 0;
        var alc = new Alc([]);
        try
        {
            var asm = alc.LoadFromStream(pe);
            var entry = asm.EntryPoint ?? throw new InvalidOperationException("no entry point");
            lock (s_lock)
            {
                var original = Console.Out;
                var writer = new StringWriter { NewLine = "\n" };
                Console.SetOut(writer);
                try
                {
                    var args = entry.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() };
                    var r = entry.Invoke(null, args);
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
