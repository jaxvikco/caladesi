using System.Diagnostics.CodeAnalysis;
using System.Text;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Emit;

namespace Caladesi.Compiler.Tests.Robustness;

[TestClass]
public class RobustnessTests
{
    private static int FuzzIterations => int.TryParse(Environment.GetEnvironmentVariable("CALADESI_FUZZ"), out int n) ? n : 25;

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caladesi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static IEnumerable<string> SampleSources()
    {
        return Directory.GetFiles(Path.Combine(RepoRoot(), "samples"), "Program.cala", SearchOption.AllDirectories)
            .Where(f => !f.Contains("errors-"))
            .Select(File.ReadAllText);
    }

    private static readonly string[] s_fragments =
        ["(", ")", "{", "}", "[", "]", "<", ">", ";", ",", ".", "?", "=>", "=", "await ", "yield ", "class ", "function ", "let ", "import ", "@", "`${", "\"", "'", "/*", "|", "&", "!", "...", "0x", "1e", "\\"];

    [TestMethod]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The test exists to detect any exception escaping the compiler.")]
    public void MutatedSourcesNeverCrashTheCompiler()
    {
        Random rng = new(int.TryParse(Environment.GetEnvironmentVariable("CALADESI_FUZZ_SEED"), out int seed) ? seed : 12345);
        List<string> failures = [];
        int count = 0;
        foreach (string source in SampleSources())
        {
            for (int i = 0; i < FuzzIterations; i++)
            {
                string text = Mutate(source, rng);
                count++;
                string? failure = null;
                Thread worker = new(() =>
                {
                    try
                    {
                        Compilation comp = TestCompiler.Create(text);
                        System.Collections.Immutable.ImmutableArray<Diagnostic> diags = comp.GetDiagnostics();
                        if (!diags.Any(d => d.EffectiveSeverity == DiagnosticSeverity.Error))
                        {
                            using MemoryStream pe = new();
                            _ = comp.Emit(pe);
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex.GetType().Name + ": " + ex.Message + "\n" + string.Join("\n", (ex.StackTrace ?? "").Split('\n').Take(6));
                    }
                }, 64 * 1024 * 1024)
                { IsBackground = true };
                worker.Start();
                if (!worker.Join(TimeSpan.FromSeconds(20)))
                {
                    failure = "HANG (no result after 20s)";
                }

                if (failure != null)
                {
                    failures.Add(failure + "\n--- source ---\n" + text);
                    if (failures.Count >= 3)
                    {
                        break;
                    }
                }
            }
            if (failures.Count >= 3)
            {
                break;
            }
        }
        Assert.IsEmpty(failures, $"{failures.Count} crashes in {count} runs:\n" + string.Join("\n=====\n", failures));
    }

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded Random keeps the fuzz mutations reproducible; this is not security-sensitive.")]
    private static string Mutate(string s, Random rng)
    {
        StringBuilder sb = new(s);
        int edits = rng.Next(1, 4);
        for (int e = 0; e < edits && sb.Length > 0; e++)
        {
            int pos = rng.Next(sb.Length);
            switch (rng.Next(4))
            {
                case 0: _ = sb.Remove(pos, Math.Min(rng.Next(1, 6), sb.Length - pos)); break;
                case 1: _ = sb.Insert(pos, s_fragments[rng.Next(s_fragments.Length)]); break;
                case 2:
                    {
                        int len = Math.Min(rng.Next(1, 20), sb.Length - pos);
                        string piece = sb.ToString(pos, len);
                        _ = sb.Insert(rng.Next(sb.Length), piece);
                        break;
                    }
                default: sb.Length = Math.Max(1, pos); break;
            }
        }
        return sb.ToString();
    }

    [TestMethod]
    public void EmitIsDeterministic()
    {
        // whole sample directories: a multi-file sample compiled from Program.cala alone fails, which would skip the byte comparison
        foreach (string sample in Emit.SampleProgramTests.RunnableSamples())
        {
            (string main, (string, string)[] more) = Emit.SampleProgramTests.Load(sample);
            using MemoryStream a = new();
            using MemoryStream b = new();
            EmitResult ra = TestCompiler.Create(main, OutputKind.ConsoleApplication, "/proj/main.cala", more).Emit(a);
            EmitResult rb = TestCompiler.Create(main, OutputKind.ConsoleApplication, "/proj/main.cala", more).Emit(b);
            Assert.IsTrue(ra.Success && rb.Success, sample + " did not compile: " + TestCompiler.Format(ra.Diagnostics));
            CollectionAssert.AreEqual(a.ToArray(), b.ToArray(), sample + " is not deterministic");
        }
    }
}
