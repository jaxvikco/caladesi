using System.Runtime.InteropServices;

namespace Caladesi.LanguageServer.Projects;

internal static class DotnetLocator
{
    /// <summary>The dotnet installation root: <c>DOTNET_ROOT</c>, then the <c>dotnet</c> found on <c>PATH</c>, then well-known locations.</summary>
    public static string? FindRoot()
    {
        string? env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return env;
        }

        string exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, exeName);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                string resolved = candidate;
                if (File.ResolveLinkTarget(candidate, returnFinalTarget: true) is { } target)
                {
                    resolved = target.FullName;
                }

                string? root = Path.GetDirectoryName(resolved);
                if ((root != null && Directory.Exists(Path.Combine(root, "packs"))) || (root != null && Directory.Exists(Path.Combine(root, "shared"))))
                {
                    return root;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }

        string[] wellKnown = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")]
            : ["/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet", "/usr/local/lib/dotnet", "/opt/dotnet"];
        foreach (string dir in wellKnown)
        {
            if (Directory.Exists(dir))
            {
                return dir;
            }
        }

        try
        {
            string runtime = RuntimeEnvironment.GetRuntimeDirectory();
            DirectoryInfo? up = Directory.GetParent(runtime.TrimEnd(Path.DirectorySeparatorChar))?.Parent?.Parent;
            if (up != null && Directory.Exists(Path.Combine(up.FullName, "shared")))
            {
                return up.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { }
        return null;
    }
}

/// <summary>Reference assemblies for files that do not belong to any project: the targeting pack plus <c>Caladesi.Runtime.dll</c>.</summary>
internal static class StandaloneReferences
{
    public static List<string> Find(ServerOptions options, out string? warning)
    {
        List<string> references = [];
        warning = null;

        string? pack = FindTargetingPack();
        if (pack != null)
        {
            references.AddRange(Directory.GetFiles(pack, "*.dll").OrderBy(p => p, StringComparer.Ordinal));
        }
        else
        {
            string runtime = RuntimeEnvironment.GetRuntimeDirectory();
            if (Directory.Exists(runtime) && File.Exists(Path.Combine(runtime, "System.Private.CoreLib.dll")))
            {
                references.AddRange(Directory.GetFiles(runtime, "System*.dll").Concat(Directory.GetFiles(runtime, "Microsoft*.dll")).Concat([Path.Combine(runtime, "System.Private.CoreLib.dll")]).Distinct());
            }
            else
            {
                warning = "Could not find the .NET targeting pack (Microsoft.NETCore.App.Ref); set DOTNET_ROOT. Standard library types are unavailable.";
            }
        }

        string? runtimeDll = FindCaladesiRuntime(options);
        if (runtimeDll != null)
        {
            references.Add(runtimeDll);
        }
        else
        {
            warning ??= "Could not find Caladesi.Runtime.dll next to the language server; set the runtimePath initialization option or CALADESI_RUNTIME_PATH.";
        }

        return references;
    }

    private static string? FindTargetingPack()
    {
        string? root = DotnetLocator.FindRoot();
        if (root == null)
        {
            return null;
        }

        string packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packs))
        {
            return null;
        }

        string? best = null;
        Version? bestVersion = null;
        foreach (string dir in Directory.GetDirectories(packs))
        {
            string refDir = Path.Combine(dir, "ref", "net10.0");
            if (!Directory.Exists(refDir))
            {
                continue;
            }

            string name = Path.GetFileName(dir).Split('-')[0];
            if (!Version.TryParse(name, out Version? v))
            {
                continue;
            }

            if (bestVersion == null || v > bestVersion)
            {
                best = refDir;
                bestVersion = v;
            }
        }
        return best;
    }

    private static string? FindCaladesiRuntime(ServerOptions options)
    {
        foreach (string? configured in new[] { options.RuntimePath, Environment.GetEnvironmentVariable("CALADESI_RUNTIME_PATH") })
        {
            if (string.IsNullOrEmpty(configured))
            {
                continue;
            }

            if (File.Exists(configured))
            {
                return configured;
            }

            string inDir = Path.Combine(configured, "Caladesi.Runtime.dll");
            if (File.Exists(inDir))
            {
                return inDir;
            }
        }
        string next = Path.Combine(AppContext.BaseDirectory, "Caladesi.Runtime.dll");
        return File.Exists(next) ? next : null;
    }
}
