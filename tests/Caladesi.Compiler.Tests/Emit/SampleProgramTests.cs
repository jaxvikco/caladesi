using Caladesi.Compiler.Diagnostics;

namespace Caladesi.Compiler.Tests.Emit;

/// <summary>Runs the single-project samples in memory (fast) so every construct they exercise is also covered by unit tests.</summary>
[TestClass]
public class SampleProgramTests
{
    private static string SamplesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caladesi.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples");
    }

    public static IEnumerable<string> RunnableSamples() =>
        Directory.GetDirectories(SamplesRoot())
            .Where(d => File.Exists(Path.Combine(d, "Program.cala")) && File.Exists(Path.Combine(d, "expected.stdout")) && !File.Exists(Path.Combine(d, "expected.test")) && !File.Exists(Path.Combine(d, "needs-nuget-org.txt")) && !File.Exists(Path.Combine(d, "args.txt")))
            .Select(Path.GetFileName)!;

    public static IEnumerable<string> ErrorSamples() =>
        Directory.GetDirectories(SamplesRoot())
            .Where(d => File.Exists(Path.Combine(d, "Program.cala")) && File.Exists(Path.Combine(d, "expected.diagnostics")))
            .Select(Path.GetFileName)!;

    private static (string main, (string, string)[] more) Load(string sample)
    {
        var dir = Path.Combine(SamplesRoot(), sample);
        var main = File.ReadAllText(Path.Combine(dir, "Program.cala"));
        var more = Directory.GetFiles(dir, "*.cala", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "Program.cala" || Path.GetDirectoryName(f) != dir)
            .Select(f => ("/proj/" + Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToArray();
        return (main, more);
    }

    [TestMethod]
    public void AllRunnableSamplesProduceTheirExpectedOutput()
    {
        var failures = new List<string>();
        foreach (var sample in RunnableSamples())
        {
            var (main, more) = Load(sample);
            var expected = File.ReadAllText(Path.Combine(SamplesRoot(), sample, "expected.stdout")).Replace("\r\n", "\n");
            try
            {
                var (output, code) = EmitHarness.Run(main, more);
                if (output.TrimEnd('\n') != expected.TrimEnd('\n')) failures.Add($"{sample}: output differs\n--- expected\n{expected}\n--- actual\n{output}");
            }
            catch (Exception e) { failures.Add($"{sample}: {e.Message}"); }
        }
        Assert.IsEmpty(failures, string.Join("\n\n", failures));
    }

    [TestMethod]
    public void ErrorSamplesReportTheirDiagnostics()
    {
        foreach (var sample in ErrorSamples())
        {
            var (main, more) = Load(sample);
            var expected = File.ReadAllLines(Path.Combine(SamplesRoot(), sample, "expected.diagnostics")).SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
            var diags = TestCompiler.Create(main, OutputKind.ConsoleApplication, "/proj/main.cala", more).GetDiagnostics();
            var ids = diags.Where(d => d.EffectiveSeverity == DiagnosticSeverity.Error).Select(d => d.Id).Distinct().ToList();
            foreach (var id in expected) CollectionAssert.Contains(ids, id, $"{sample}: expected {id}, got {string.Join(",", ids)}");
        }
    }
}
