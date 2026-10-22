namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public class EmitIteratorTests
{
    private static void Check(string source, string expected)
    {
        var (output, _) = EmitHarness.Run(source);
        Assert.AreEqual(expected, output);
    }

    [TestMethod]
    public void SimpleGenerator() => Check("""
        function* count(n: int): IEnumerable<int> {
          for (let i = 0; i < n; i++) yield i * i;
        }
        for (const x of count(4)) Console.WriteLine(x);
        Console.WriteLine(count(5).Sum());
        """, "0\n1\n4\n9\n30\n");

    [TestMethod]
    public void GeneratorWithFinallyAndEarlyExit() => Check("""
        function* gen(): IEnumerable<string> {
          try {
            yield "a";
            yield "b";
            yield "c";
          } finally {
            Console.WriteLine("cleanup");
          }
        }
        for (const s of gen()) { Console.WriteLine(s); if (s == "b") break; }
        """, "a\nb\ncleanup\n");

    [TestMethod]
    public void GeneratorMethodCapturesThis() => Check("""
        class Bag {
          items = ["x", "y"];
          *all(): IEnumerable<string> { for (const i of this.items) yield i; yield "end"; }
        }
        Console.WriteLine(string.Join(",", new Bag().all()));
        """, "x,y,end\n");

    [TestMethod]
    public void EnumerableIsReenumerable() => Check("""
        function* two(): IEnumerable<int> { yield 1; yield 2; }
        const e = two();
        Console.WriteLine(e.Count() + e.Count());
        """, "4\n");
}
