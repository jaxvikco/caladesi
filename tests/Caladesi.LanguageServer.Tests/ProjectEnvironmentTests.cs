namespace Caladesi.LanguageServer.Tests;

[TestClass]
public class ProjectEnvironmentTests
{
    private static string NewRoot()
    {
        string root = Path.Join(Path.GetTempPath(), "feed-stale-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Join(root, "feed"));
        _ = Directory.CreateDirectory(Path.Join(root, "repo", "src", "Caladesi.Compiler"));
        _ = Directory.CreateDirectory(Path.Join(root, "repo", "src", "Caladesi.Compiler", "obj"));
        return root;
    }

    private static void Write(string path, DateTime utc)
    {
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, utc);
    }

    [TestMethod]
    public void Item43_FeedPackedBeforeASourceChangeIsStale()
    {
        string root = NewRoot();
        try
        {
            DateTime packed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            string feed = Path.Join(root, "feed");
            string repo = Path.Join(root, "repo");
            Write(Path.Join(feed, "Caladesi.Sdk.1.0.0.nupkg"), packed);
            Write(Path.Join(feed, "Caladesi.Runtime.1.0.0.nupkg"), packed);
            Write(Path.Join(repo, "src", "Caladesi.Compiler", "Binder.cs"), packed.AddDays(-1));
            Write(Path.Join(repo, "src", "Caladesi.Compiler", "obj", "generated.cs"), packed.AddDays(5));
            Assert.IsFalse(ProjectEnvironment.IsStale(feed, repo), "older sources and newer build output do not make the feed stale");

            Write(Path.Join(repo, "src", "Caladesi.Compiler", "Binder.cs"), packed.AddDays(1));
            Assert.IsTrue(ProjectEnvironment.IsStale(feed, repo), "a source newer than the packages does");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Item43_MissingPackagesAreStale()
    {
        string root = NewRoot();
        try
        {
            Assert.IsTrue(ProjectEnvironment.IsStale(Path.Join(root, "feed"), Path.Join(root, "repo")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
