[assembly: DoNotParallelize]

namespace Caladesi_E2E;

[TestClass]
public class Smoke
{
    [TestMethod]
    public void Runs() => Assert.AreEqual(2, 1 + int.Parse("1"));
}
