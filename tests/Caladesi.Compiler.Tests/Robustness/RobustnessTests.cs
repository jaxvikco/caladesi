using Caladesi.Compiler.Diagnostics;

namespace Caladesi.Compiler.Tests.Robustness;

[TestClass]
public class RobustnessTests
{
    private static int FuzzIterations => int.TryParse(Environment.GetEnvironmentVariable("CALADESI_FUZZ"), out var n) ? n : 25;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caladesi.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static IEnumerable<string> SampleSources() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "samples"), "Program.cala", SearchOption.AllDirectories)
            .Where(f => !f.Contains("errors-"))
            .Select(File.ReadAllText);

    private static readonly string[] s_fragments =
        ["(", ")", "{", "}", "[", "]", "<", ">", ";", ",", ".", "?", "=>", "=", "await ", "yield ", "class ", "function ", "let ", "import ", "@", "`${", "\"", "'", "/*", "|", "&", "!", "...", "0x", "1e", "\\"];

    [TestMethod]
    public void MutatedSourcesNeverCrashTheCompiler()
    {
        var rng = new Random(int.TryParse(Environment.GetEnvironmentVariable("CALADESI_FUZZ_SEED"), out var seed) ? seed : 12345);
        var failures = new List<string>();
        var count = 0;
        foreach (var source in SampleSources())
        {
            for (var i = 0; i < FuzzIterations; i++)
            {
                var text = Mutate(source, rng);
                count++;
                string? failure = null;
                var worker = new Thread(() =>
                {
                    try
                    {
                        var comp = TestCompiler.Create(text);
                        var diags = comp.GetDiagnostics();
                        if (!diags.Any(d => d.EffectiveSeverity == DiagnosticSeverity.Error))
                        {
                            using var pe = new MemoryStream();
                            comp.Emit(pe);
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex.GetType().Name + ": " + ex.Message + "\n" + string.Join("\n", (ex.StackTrace ?? "").Split('\n').Take(6));
                    }
                }, 64 * 1024 * 1024) { IsBackground = true };
                worker.Start();
                if (!worker.Join(TimeSpan.FromSeconds(20))) failure = "HANG (no result after 20s)";
                if (failure != null)
                {
                    failures.Add(failure + "\n--- source ---\n" + text);
                    if (failures.Count >= 3) break;
                }
            }
            if (failures.Count >= 3) break;
        }
        Assert.IsEmpty(failures, $"{failures.Count} crashes in {count} runs:\n" + string.Join("\n=====\n", failures));
    }

    private static string Mutate(string s, Random rng)
    {
        var sb = new System.Text.StringBuilder(s);
        var edits = rng.Next(1, 4);
        for (var e = 0; e < edits && sb.Length > 0; e++)
        {
            var pos = rng.Next(sb.Length);
            switch (rng.Next(4))
            {
                case 0: sb.Remove(pos, Math.Min(rng.Next(1, 6), sb.Length - pos)); break;
                case 1: sb.Insert(pos, s_fragments[rng.Next(s_fragments.Length)]); break;
                case 2:
                {
                    var len = Math.Min(rng.Next(1, 20), sb.Length - pos);
                    var piece = sb.ToString(pos, len);
                    sb.Insert(rng.Next(sb.Length), piece);
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
        foreach (var source in SampleSources().Take(6))
        {
            using var a = new MemoryStream();
            using var b = new MemoryStream();
            var ra = TestCompiler.Create(source).Emit(a);
            var rb = TestCompiler.Create(source).Emit(b);
            Assert.AreEqual(ra.Success, rb.Success);
            if (ra.Success) CollectionAssert.AreEqual(a.ToArray(), b.ToArray());
        }
    }
}
