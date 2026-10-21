using Caladesi.Compiler;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace Caladesi_LanguageServer_Tests;

[TestClass]
public class Smoke
{
    [TestMethod]
    public void Runs() => Assert.IsTrue(CompilerInfo.Version.StartsWith("0.1", StringComparison.Ordinal));
}
