using System.Diagnostics;
using System.Text;

namespace Caladesi.LanguageServer.Tests;

/// <summary>
/// A local NuGet feed holding the packed Caladesi.Sdk/Caladesi.Runtime (created once through <c>eng/pack.sh</c>, or taken from
/// <c>CALADESI_LS_FEED</c>) and an isolated global packages folder, so real <c>.calaproj</c> files can be restored offline.
/// </summary>
internal static class ProjectEnvironment
{
    private static readonly Lock Gate = new();
    private static Task<Info>? _init;

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caladesi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Cannot locate Caladesi.slnx above " + AppContext.BaseDirectory);
    }

    public static Task<Info> EnsureAsync()
    {
        lock (Gate)
        {
            return _init ??= InitAsync();
        }
    }

    private static async Task<Info> InitAsync()
    {
        string work = Path.Combine(RepoRoot, "artifacts", "ls-tests");
        _ = Directory.CreateDirectory(work);
        string? feed = Environment.GetEnvironmentVariable("CALADESI_LS_FEED");
        string cache = Path.Combine(work, "nuget-packages");
        if (string.IsNullOrEmpty(feed))
        {
            feed = Path.Combine(work, "packages");
            if (IsStale(feed, RepoRoot))
            {
                ProcessStartInfo info = new("bash") { WorkingDirectory = RepoRoot, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add(Path.Combine(RepoRoot, "eng", "pack.sh"));
                info.Environment["CALADESI_PACK_OUT"] = feed;
                info.Environment["CALADESI_PACK_WORK"] = Path.Combine(work, "pack-obj");
                using Process process = Process.Start(info)!;
                StringBuilder output = new();
                process.OutputDataReceived += (_, e) => output.AppendLine(e.Data);
                process.ErrorDataReceived += (_, e) => output.AppendLine(e.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("eng/pack.sh failed:\n" + output);
                }

                if (Directory.Exists(cache))
                {
                    foreach (string dir in Directory.GetDirectories(cache, "caladesi.*"))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
            }
        }
        _ = Directory.CreateDirectory(cache);
        return new Info(feed, cache, Path.Combine(Path.GetTempPath(), "cala-ls-tests-" + Guid.NewGuid().ToString("N")[..8]));
    }

    /// <summary>True when the packed feed is missing, or older than any compiler, SDK or runtime source it was packed from.</summary>
    internal static bool IsStale(string feed, string repoRoot)
    {
        string[] packages = [Path.Join(feed, "Caladesi.Sdk.1.0.0.nupkg"), Path.Join(feed, "Caladesi.Runtime.1.0.0.nupkg")];
        if (packages.Any(p => !File.Exists(p)))
        {
            return true;
        }

        DateTime packed = packages.Min(File.GetLastWriteTimeUtc);
        string src = Path.Join(repoRoot, "src");
        string[] roots = [Path.Join(repoRoot, "Directory.Build.props"), Path.Join(repoRoot, "Directory.Packages.props")];
        IEnumerable<string> sources = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Concat(roots.Where(File.Exists));
        return sources.Any(f => File.GetLastWriteTimeUtc(f) > packed);
    }

    internal sealed record Info(string Feed, string NuGetPackages, string WorkRoot)
    {
        public ServerOptions ServerOptions()
        {
            ServerOptions options = new() { DiagnosticsDelay = TimeSpan.FromMilliseconds(20) };
            options.ChildEnvironment["NUGET_PACKAGES"] = NuGetPackages;
            return options;
        }

        /// <summary>Creates a project directory with an <c>app.calaproj</c>, a nuget.config for the local feed and the given files.</summary>
        public string CreateProject(string name, string outputType, params (string Path, string Text)[] files)
        {
            string dir = Path.Combine(WorkRoot, name);
            _ = Directory.CreateDirectory(dir);
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
            foreach ((string path, string text) in files)
            {
                string full = Path.Combine(dir, path);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            return dir;
        }
    }
}
