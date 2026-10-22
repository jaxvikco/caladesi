using System.Diagnostics;
using System.Text;
using Caladesi.LanguageServer.Protocol;

namespace Caladesi.LanguageServer.Projects;

internal sealed record ProjectLoadResult(MsBuildProjectData? Data, string? Error);

/// <summary>Runs <c>dotnet msbuild</c> to read the compile items, properties and reference assemblies of an <c>.calaproj</c>.</summary>
internal sealed class ProjectLoader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly ServerOptions _options;

    public ProjectLoader(ServerOptions options) => _options = options;

    public ProjectLoadResult Load(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath) ?? ".";
        var first = Evaluate(projectPath, directory);
        if (first.Data is { References.Count: > 0 }) return first;

        if (!File.Exists(Path.Combine(directory, "obj", "project.assets.json")))
        {
            Log.Write("project is not restored, running dotnet restore: " + projectPath);
            var restore = Run(["restore", projectPath, "-nologo", "-v:q"], directory);
            if (restore.ExitCode != 0)
                return new ProjectLoadResult(null, "dotnet restore failed: " + Truncate(restore.Output));
            var second = Evaluate(projectPath, directory);
            if (second.Data is { References.Count: > 0 }) return second;
            return second.Data == null ? second : new ProjectLoadResult(null, "no reference assemblies were resolved for " + Path.GetFileName(projectPath));
        }
        return first.Data == null ? first : new ProjectLoadResult(null, "no reference assemblies were resolved for " + Path.GetFileName(projectPath));
    }

    private ProjectLoadResult Evaluate(string projectPath, string directory)
    {
        var result = Run(
        [
            "msbuild", projectPath, "-nologo", "-nodeReuse:false",
            "-getProperty:" + MsBuildProjectData.ProjectProperties,
            "-getItem:Compile,CaladesiGlobalImport,ReferencePathWithRefAssemblies,TestingPlatformBuilderHook",
            "-target:ResolveAssemblyReferences,FindReferenceAssembliesForReferences",
        ], directory);
        var data = MsBuildProjectData.Parse(result.Output, directory);
        if (data == null) return new ProjectLoadResult(null, "dotnet msbuild failed (exit code " + result.ExitCode + "): " + Truncate(result.Output));
        return new ProjectLoadResult(data, null);
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length <= 600 ? text : text[..600] + "...";
    }

    private (int ExitCode, string Output) Run(IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment["NUGET_XMLDOC_MODE"] = "skip";
        foreach (var (key, value) in _options.ChildEnvironment)
        {
            if (value == null) info.Environment.Remove(key);
            else info.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(info);
            if (process == null) return (-1, "could not start dotnet");
            process.StandardInput.Close();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var outTask = Task.Run(() => { string? line; while ((line = process.StandardOutput.ReadLine()) != null) lock (stdout) stdout.AppendLine(line); });
            var errTask = Task.Run(() => { string? line; while ((line = process.StandardError.ReadLine()) != null) lock (stderr) stderr.AppendLine(line); });
            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, "dotnet timed out");
            }
            process.WaitForExit();
            Task.WaitAll(outTask, errTask);
            return (process.ExitCode, stdout.ToString() + stderr.ToString());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, "could not run dotnet: " + ex.Message);
        }
    }

    private string DotnetHost()
    {
        if (_options.DotnetPath is { Length: > 0 } configured) return configured;
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host)) return host;
        var root = DotnetLocator.FindRoot();
        if (root != null)
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }
}
