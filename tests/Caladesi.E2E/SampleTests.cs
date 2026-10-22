using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Caladesi.E2E;

/// <summary>
/// Data-driven runner for <c>samples/&lt;name&gt;/</c>. See tests/Caladesi.E2E/README.md for the sample layout rules.
/// </summary>
[TestClass]
public sealed class SampleTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public static IEnumerable<object[]> Samples()
    {
        string samplesDir = Path.Combine(E2EEnvironment.RepoRoot, "samples");
        if (!Directory.Exists(samplesDir))
        {
            yield break;
        }

        foreach (string dir in Directory.GetDirectories(samplesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (Directory.EnumerateFiles(dir, "*.calaproj", SearchOption.AllDirectories).Any())
            {
                yield return [Path.GetFileName(dir)];
            }
        }
    }

    public static string SampleDisplayName(MethodInfo method, object?[]? data)
    {
        ArgumentNullException.ThrowIfNull(method);
        return data is { Length: > 0 } ? (string)data[0]! : method.Name;
    }

    [TestMethod]
    [DynamicData(nameof(Samples), DynamicDataDisplayName = nameof(SampleDisplayName))]
    public async Task Sample(string name)
    {
        E2EEnvironment.Info info = await E2EEnvironment.EnsureAsync().ConfigureAwait(false);
        string source = Path.Combine(E2EEnvironment.RepoRoot, "samples", name);

        string ignore = Path.Combine(source, "ignore.txt");
        if (File.Exists(ignore))
        {
            string reason = await File.ReadAllTextAsync(ignore).ConfigureAwait(false);
            Assert.Inconclusive("Sample ignored: " + reason.Trim());
        }

        string work = Path.Combine(info.WorkRoot, name);
        CopyDirectory(source, work);

        string expectedTest = Path.Combine(source, "expected.test");
        bool needsNuGetOrg = File.Exists(expectedTest) || File.Exists(Path.Combine(source, "needs-nuget-org.txt"));
        await File.WriteAllTextAsync(Path.Combine(work, "nuget.config"), info.NuGetConfig(needsNuGetOrg)).ConfigureAwait(false);

        string entry = ResolveEntryProject(source, work);
        Dictionary<string, string?> env = info.ChildEnvironment();
        StringBuilder log = new();

        bool succeeded = false;
        try
        {
            await RunSampleAsync(name, source, work, entry, env, log).ConfigureAwait(false);
            succeeded = true;
        }
        finally
        {
            if (succeeded && Environment.GetEnvironmentVariable("CALADESI_E2E_KEEP") != "1")
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            }
        }
    }

    private static async Task RunSampleAsync(string name, string source, string work, string entry, Dictionary<string, string?> env, StringBuilder log)
    {
        string diagnosticsFile = Path.Combine(source, "expected.diagnostics");
        List<string>? expectedDiagnostics = File.Exists(diagnosticsFile)
            ? [.. (await File.ReadAllLinesAsync(diagnosticsFile).ConfigureAwait(false)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))]
            : null;

        // ---- build
        ProcessResult build = await Dotnet.RunAsync(["build", entry, "-nodeReuse:false", "-v:minimal", "-nologo"], work, env, CommandTimeout).ConfigureAwait(false);
        _ = log.AppendLine("$ dotnet build " + Path.GetRelativePath(work, entry)).AppendLine(build.Output);

        if (expectedDiagnostics != null)
        {
            if (build.ExitCode == 0)
            {
                Fail(name, work, log, "dotnet build succeeded but expected.diagnostics lists errors");
            }

            foreach (string id in expectedDiagnostics)
            {
                if (!Regex.IsMatch(build.Output, @"\b" + Regex.Escape(id) + @"\b"))
                {
                    Fail(name, work, log, "build output does not contain diagnostic " + id);
                }
            }
            return;
        }

        if (build.TimedOut)
        {
            Fail(name, work, log, "dotnet build timed out");
        }

        if (build.ExitCode != 0)
        {
            Fail(name, work, log, "dotnet build failed with exit code " + build.ExitCode);
        }

        // ---- run / test
        string expectedTestFile = Path.Combine(source, "expected.test");
        if (File.Exists(expectedTestFile))
        {
            // Microsoft.Testing.Platform mode of `dotnet test` is selected through global.json.
            if (!File.Exists(Path.Combine(work, "global.json")))
            {
                await File.WriteAllTextAsync(Path.Combine(work, "global.json"), /*lang=json,strict*/ "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }\n").ConfigureAwait(false);
            }

            ProcessResult test = await Dotnet.RunAsync(["test", "--project", entry, "--no-build"], work, env, CommandTimeout).ConfigureAwait(false);
            _ = log.AppendLine("$ dotnet test --project " + Path.GetRelativePath(work, entry)).AppendLine(test.Output);
            if (test.TimedOut)
            {
                Fail(name, work, log, "dotnet test timed out");
            }

            if (test.ExitCode != 0)
            {
                Fail(name, work, log, "dotnet test failed with exit code " + test.ExitCode);
            }

            foreach (string line in (await File.ReadAllLinesAsync(expectedTestFile).ConfigureAwait(false)).Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                if (!test.Output.Contains(line, StringComparison.Ordinal))
                {
                    Fail(name, work, log, "test output does not contain expected text: " + line);
                }
            }
            return;
        }

        List<string> runArgs = ["run", "--no-build", "--project", entry];
        string argsFile = Path.Combine(source, "args.txt");
        if (File.Exists(argsFile))
        {
            runArgs.Add("--");
            runArgs.AddRange((await File.ReadAllLinesAsync(argsFile).ConfigureAwait(false)).Where(l => l.Length > 0));
        }
        ProcessResult run = await Dotnet.RunAsync(runArgs, work, env, CommandTimeout).ConfigureAwait(false);
        _ = log.AppendLine("$ dotnet " + string.Join(' ', runArgs.Select(a => a.Contains(' ') ? '"' + a + '"' : a)));
        _ = log.AppendLine("--- stdout").AppendLine(run.StdOut).AppendLine("--- stderr").AppendLine(run.StdErr);
        if (run.TimedOut)
        {
            Fail(name, work, log, "dotnet run timed out");
        }

        string exitFile = Path.Combine(source, "expected.exitcode");
        int expectedExit = File.Exists(exitFile) ? int.Parse((await File.ReadAllTextAsync(exitFile).ConfigureAwait(false)).Trim(), System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (run.ExitCode != expectedExit)
        {
            Fail(name, work, log, $"exit code {run.ExitCode}, expected {expectedExit}");
        }

        string stdoutFile = Path.Combine(source, "expected.stdout");
        if (File.Exists(stdoutFile))
        {
            string expected = Normalize(await File.ReadAllTextAsync(stdoutFile).ConfigureAwait(false));
            string actual = Normalize(run.StdOut);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                Fail(name, work, log, "stdout differs." + Environment.NewLine + "--- expected" + Environment.NewLine + expected + "--- actual" + Environment.NewLine + actual);
            }
        }
    }

    private static string Normalize(string s)
    {
        return s.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void Fail(string name, string work, StringBuilder log, string reason)
    {
        Assert.Fail($"Sample '{name}': {reason}{Environment.NewLine}Work directory (kept): {work}{Environment.NewLine}{Environment.NewLine}{log}");
    }

    /// <summary>
    /// Entry project rule: <c>entry.txt</c> (first non-blank line = project path relative to the sample root), else
    /// <c>app.calaproj</c> at the sample root, else the only <c>*.calaproj</c>/<c>*.csproj</c> at the sample root.
    /// </summary>
    private static string ResolveEntryProject(string source, string work)
    {
        string entryFile = Path.Combine(source, "entry.txt");
        if (File.Exists(entryFile))
        {
            string rel = File.ReadAllLines(entryFile).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)
                ?? throw new InvalidOperationException("entry.txt is empty in " + source);
            string path = Path.GetFullPath(Path.Combine(work, rel));
            return File.Exists(path) ? path : throw new InvalidOperationException($"entry.txt points to a missing project: {rel}");
        }

        string app = Path.Combine(work, "app.calaproj");
        if (File.Exists(app))
        {
            return app;
        }

        List<string> top = [.. Directory.GetFiles(work, "*.calaproj"), .. Directory.GetFiles(work, "*.csproj")];
        return top.Count == 1
            ? top[0]
            : throw new InvalidOperationException($"Cannot pick the entry project of '{source}': add entry.txt, name it app.calaproj, or keep exactly one project at the top level (found {top.Count}).");
    }

    private static void CopyDirectory(string source, string destination)
    {
        _ = Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            string leaf = Path.GetFileName(dir);
            if (leaf is "bin" or "obj")
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(destination, leaf));
        }
    }
}
