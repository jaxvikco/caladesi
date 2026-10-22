using Caladesi.Compiler;

[assembly: DoNotParallelize]

namespace Caladesi.LanguageServer.Tests;

[TestClass]
public class Smoke
{
    [TestMethod]
    public void Runs()
    {
        Assert.IsTrue(CompilerInfo.Version.StartsWith("1.0", StringComparison.Ordinal));
    }
}
