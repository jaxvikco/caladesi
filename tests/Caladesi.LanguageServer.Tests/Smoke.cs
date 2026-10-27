using Caladesi.Compiler;

[assembly: DoNotParallelize]

namespace Caladesi.LanguageServer.Tests;

[TestClass]
public class Smoke
{
    [TestMethod]
    public void Runs()
    {
        Assert.IsTrue(System.Version.TryParse(CompilerInfo.Version.Split('-')[0], out _), CompilerInfo.Version);
    }
}
