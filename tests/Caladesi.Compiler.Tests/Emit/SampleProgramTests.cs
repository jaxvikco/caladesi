using System.Diagnostics.CodeAnalysis;
using Caladesi.Compiler.Diagnostics;

namespace Caladesi.Compiler.Tests.Emit;

/// <summary>Runs the single-project samples in memory (fast) so every construct they exercise is also covered by unit tests.</summary>
[TestClass]
public class SampleProgramTests
{
    private static string SamplesRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caladesi.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "samples");
    }

    public static IEnumerable<string> RunnableSamples()
    {
        return Directory.GetDirectories(SamplesRoot())
            .Where(d => File.Exists(Path.Combine(d, "Program.cala")) && File.Exists(Path.Combine(d, "expected.stdout")) && !File.Exists(Path.Combine(d, "expected.test")) && !File.Exists(Path.Combine(d, "needs-nuget-org.txt")) && !File.Exists(Path.Combine(d, "args.txt")))
            .Select(Path.GetFileName)!;
    }

    public static IEnumerable<string> ErrorSamples()
    {
        return Directory.GetDirectories(SamplesRoot())
            .Where(d => File.Exists(Path.Combine(d, "Program.cala")) && File.Exists(Path.Combine(d, "expected.diagnostics")))
            .Select(Path.GetFileName)!;
    }

    internal static (string main, (string, string)[] more) Load(string sample)
    {
        string dir = Path.Combine(SamplesRoot(), sample);
        string main = File.ReadAllText(Path.Combine(dir, "Program.cala"));
        (string, string)[] more = [.. Directory.GetFiles(dir, "*.cala", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "Program.cala" || Path.GetDirectoryName(f) != dir)
            .Select(f => ("/proj/" + Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText(f)))];
        return (main, more);
    }

    [TestMethod]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every sample failure, whatever its type, is collected and reported together.")]
    public void AllRunnableSamplesProduceTheirExpectedOutput()
    {
        List<string> failures = [];
        foreach (string sample in RunnableSamples())
        {
            (string? main, (string, string)[]? more) = Load(sample);
            string expected = File.ReadAllText(Path.Combine(SamplesRoot(), sample, "expected.stdout")).Replace("\r\n", "\n");
            try
            {
                (string? output, int code) = EmitHarness.Run(main, more);
                if (output.TrimEnd('\n') != expected.TrimEnd('\n'))
                {
                    failures.Add($"{sample}: output differs\n--- expected\n{expected}\n--- actual\n{output}");
                }
            }
            catch (Exception e) { failures.Add($"{sample}: {e.Message}"); }
        }
        Assert.IsEmpty(failures, string.Join("\n\n", failures));
    }

    [TestMethod]
    public void ErrorSamplesReportTheirDiagnostics()
    {
        foreach (string sample in ErrorSamples())
        {
            (string? main, (string, string)[]? more) = Load(sample);
            List<string> expected = [.. File.ReadAllLines(Path.Combine(SamplesRoot(), sample, "expected.diagnostics")).SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))];
            System.Collections.Immutable.ImmutableArray<Diagnostic> diags = TestCompiler.Create(main, OutputKind.ConsoleApplication, "/proj/main.cala", more).GetDiagnostics();
            List<string> ids = [.. diags.Where(d => d.EffectiveSeverity == DiagnosticSeverity.Error).Select(d => d.Id).Distinct()];
            foreach (string id in expected)
            {
                CollectionAssert.Contains(ids, id, $"{sample}: expected {id}, got {string.Join(",", ids)}");
            }
        }
    }
}
