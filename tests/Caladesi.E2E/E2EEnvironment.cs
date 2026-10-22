using System.Text;

namespace Caladesi.E2E;

/// <summary>
/// Shared, lazily initialised state for the whole test run: repository paths, the local NuGet feed holding the
/// freshly packed Caladesi packages, and an isolated global-packages folder so a re-packed Caladesi.Sdk is never shadowed
/// by a copy cached from an earlier run.
/// </summary>
internal static class E2EEnvironment
{
    private static readonly Lock Gate = new();
    private static Task<Info>? _init;

    public static Task<Info> EnsureAsync()
    {
        lock (Gate)
        {
            return _init ??= InitAsync();
        }
    }

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caladesi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Cannot locate the repository root (Caladesi.slnx) above " + AppContext.BaseDirectory);
    }

    private static async Task<Info> InitAsync()
    {
        string e2eDir = Path.Combine(RepoRoot, "artifacts", "e2e");
        string nugetCache = Path.Combine(e2eDir, "nuget-packages");
        _ = Directory.CreateDirectory(e2eDir);

        string? feed = Environment.GetEnvironmentVariable("CALADESI_E2E_FEED");
        if (string.IsNullOrEmpty(feed))
        {
            feed = Path.Combine(e2eDir, "packages");
            Dictionary<string, string?> env = new()
            {
                ["CALADESI_PACK_OUT"] = feed,
                ["CALADESI_PACK_WORK"] = Path.Combine(e2eDir, "pack-obj"),
            };
            ProcessResult result = await Dotnet.RunProcessAsync("bash", [Path.Combine(RepoRoot, "eng", "pack.sh")], RepoRoot, env, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("eng/pack.sh failed with exit code " + result.ExitCode + Environment.NewLine + result.Output);
            }
        }
        else if (!Directory.Exists(feed))
        {
            throw new InvalidOperationException("CALADESI_E2E_FEED does not exist: " + feed);
        }

        // The packages are re-created under the same id/version on every run; drop every cached copy of them.
        if (Directory.Exists(nugetCache))
        {
            foreach (string dir in Directory.GetDirectories(nugetCache, "caladesi.*"))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        _ = Directory.CreateDirectory(nugetCache);

        string workRoot = Path.Combine(Path.GetTempPath(), "caladesi-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        _ = Directory.CreateDirectory(workRoot);
        return new Info(feed, nugetCache, workRoot);
    }

    internal sealed record Info(string FeedDir, string NuGetPackagesDir, string WorkRoot)
    {
        /// <summary>Environment for every dotnet child process: isolated package cache, no node/server reuse.</summary>
        public Dictionary<string, string?> ChildEnvironment()
        {
            return new()
            {
                ["NUGET_PACKAGES"] = NuGetPackagesDir,
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["NUGET_XMLDOC_MODE"] = "skip",
            };
        }

        public string NuGetConfig(bool includeNuGetOrg)
        {
            StringBuilder sb = new();
            _ = sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            _ = sb.AppendLine("<configuration>");
            _ = sb.AppendLine("  <packageSources>");
            _ = sb.AppendLine("    <clear />");
            _ = sb.AppendLine("    <add key=\"caladesi-local\" value=\"" + System.Security.SecurityElement.Escape(FeedDir) + "\" />");
            if (includeNuGetOrg)
            {
                _ = sb.AppendLine("    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />");
            }

            _ = sb.AppendLine("  </packageSources>");
            _ = sb.AppendLine("</configuration>");
            return sb.ToString();
        }
    }
}
