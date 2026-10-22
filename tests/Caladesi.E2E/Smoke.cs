[assembly: DoNotParallelize]

namespace Caladesi.E2E;

[TestClass]
public sealed class Smoke
{
    [TestMethod]
    public void Runs()
    {
        Assert.AreEqual(2, 1 + int.Parse("1"));
    }
}
