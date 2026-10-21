namespace Caladesi.Compiler.Tests;

internal static class TestReferences
{
    private static string[]? s_paths;

    /// <summary>Reference assemblies of the targeting pack (Microsoft.NETCore.App.Ref).</summary>
    public static string[] Paths => s_paths ??= Find();

    private static string[] Find()
    {
        var coreLib = typeof(object).Assembly.Location;
        var dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(coreLib)!, "..", "..", ".."));
        var packs = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        var dir = Directory.GetDirectories(packs).OrderByDescending(d => d, StringComparer.Ordinal).First();
        var refDir = Path.Combine(dir, "ref", "net10.0");
        return Directory.GetFiles(refDir, "*.dll");
    }
}
