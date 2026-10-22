using System.Diagnostics;
using System.Text;
using Caladesi.LanguageServer;

namespace Caladesi_LanguageServer_Tests;

/// <summary>
/// A local NuGet feed holding the packed Caladesi.Sdk/Caladesi.Runtime (created once through <c>eng/pack.sh</c>, or taken from
/// <c>CALADESI_LS_FEED</c>) and an isolated global packages folder, so real <c>.calaproj</c> files can be restored offline.
/// </summary>
internal static class ProjectEnvironment
{
    private static readonly object Gate = new();
    private static Task<Info>? _init;

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caladesi.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot locate Caladesi.slnx above " + AppContext.BaseDirectory);
    }

    public static Task<Info> EnsureAsync()
    {
        lock (Gate) return _init ??= InitAsync();
    }

    private static async Task<Info> InitAsync()
    {
        var work = Path.Combine(RepoRoot, "artifacts", "ls-tests");
        Directory.CreateDirectory(work);
        var feed = Environment.GetEnvironmentVariable("CALADESI_LS_FEED");
        var cache = Path.Combine(work, "nuget-packages");
        if (string.IsNullOrEmpty(feed))
        {
            feed = Path.Combine(work, "packages");
            if (!File.Exists(Path.Combine(feed, "Caladesi.Sdk.1.0.0.nupkg")) || !File.Exists(Path.Combine(feed, "Caladesi.Runtime.1.0.0.nupkg")))
            {
                var info = new ProcessStartInfo("bash") { WorkingDirectory = RepoRoot, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add(Path.Combine(RepoRoot, "eng", "pack.sh"));
                info.Environment["CALADESI_PACK_OUT"] = feed;
                info.Environment["CALADESI_PACK_WORK"] = Path.Combine(work, "pack-obj");
                using var process = Process.Start(info)!;
                var output = new StringBuilder();
                process.OutputDataReceived += (_, e) => output.AppendLine(e.Data);
                process.ErrorDataReceived += (_, e) => output.AppendLine(e.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
                if (process.ExitCode != 0) throw new InvalidOperationException("eng/pack.sh failed:\n" + output);
                if (Directory.Exists(cache)) foreach (var dir in Directory.GetDirectories(cache, "caladesi.*")) Directory.Delete(dir, recursive: true);
            }
        }
        Directory.CreateDirectory(cache);
        return new Info(feed, cache, Path.Combine(Path.GetTempPath(), "cala-ls-tests-" + Guid.NewGuid().ToString("N")[..8]));
    }

    internal sealed record Info(string Feed, string NuGetPackages, string WorkRoot)
    {
        public ServerOptions ServerOptions()
        {
            var options = new ServerOptions { DiagnosticsDelay = TimeSpan.FromMilliseconds(20) };
            options.ChildEnvironment["NUGET_PACKAGES"] = NuGetPackages;
            return options;
        }

        /// <summary>Creates a project directory with an <c>app.calaproj</c>, a nuget.config for the local feed and the given files.</summary>
        public string CreateProject(string name, string outputType, params (string Path, string Text)[] files)
        {
            var dir = Path.Combine(WorkRoot, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "app.calaproj"), $$"""
                <Project Sdk="Caladesi.Sdk/1.0.0">
                  <PropertyGroup>
                    <OutputType>{{outputType}}</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <RootNamespace>Demo</RootNamespace>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, "nuget.config"), $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="caladesi-local" value="{{System.Security.SecurityElement.Escape(Feed)}}" />
                  </packageSources>
                </configuration>
                """);
            foreach (var (path, text) in files)
            {
                var full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            return dir;
        }
    }
}
