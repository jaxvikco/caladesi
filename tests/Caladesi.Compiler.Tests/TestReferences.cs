namespace Caladesi.Compiler.Tests;

internal static class TestReferences
{
    /// <summary>Reference assemblies of the targeting pack (Microsoft.NETCore.App.Ref).</summary>
    public static string[] Paths { get; } = Find();

    private static string[] Find()
    {
        string coreLib = typeof(object).Assembly.Location;
        string dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(coreLib)!, "..", "..", ".."));
        string packs = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        string refDir = Directory.GetDirectories(packs)
            .Select(d => Path.Combine(d, "ref", "net10.0"))
            .Where(Directory.Exists)
            .OrderByDescending(d => d, StringComparer.Ordinal)
            .First();
        return Directory.GetFiles(refDir, "*.dll");
    }
}
