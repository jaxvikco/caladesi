namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public class EmitCoreTests
{
    private static void Check(string source, string expected)
    {
        (string? output, int _) = EmitHarness.Run(source);
        Assert.AreEqual(expected, output);
    }

    [TestMethod]
    public void HelloWorld()
    {
        Check("Console.WriteLine('hello');", "hello\n");
    }

    [TestMethod]
    public void ArithmeticAndFunctions()
    {
        Check("""
        function add(a: int, b: int): int { return a + b; }
        const x = add(2, 3) * 4;
        Console.WriteLine(x);
        Console.WriteLine(7 / 2);
        Console.WriteLine(7.0 / 2);
        Console.WriteLine(2 ** 10);
        """, "20\n3\n3.5\n1024\n");
    }

    [TestMethod]
    public void ControlFlow()
    {
        Check("""
        let sum = 0;
        for (let i = 0; i < 5; i++) { if (i == 3) continue; sum += i; }
        Console.WriteLine(sum);
        let n = 0;
        while (n < 3) n++;
        do { n--; } while (n > 0);
        Console.WriteLine(n);
        for (const s of ["a", "b"]) Console.WriteLine(s);
        """, "7\n0\na\nb\n");
    }

    [TestMethod]
    public void StringsAndTemplates()
    {
        Check("""
        const name = "world";
        Console.WriteLine(`hello ${name} ${1 + 2}`);
        Console.WriteLine("abc".Length);
        """, "hello world 3\n3\n");
    }

    [TestMethod]
    public void TryCatchFinally()
    {
        Check("""
        try { throw new Exception("boom"); }
        catch (e: Exception) { Console.WriteLine(e.Message); }
        finally { Console.WriteLine("done"); }
        """, "boom\ndone\n");
    }

    [TestMethod]
    public void Switch()
    {
        Check("""
        function f(n: int): string {
          switch (n) { case 1: return "one"; case 2: return "two"; default: return "many"; }
        }
        Console.WriteLine(f(1)); Console.WriteLine(f(2)); Console.WriteLine(f(9));
        """, "one\ntwo\nmany\n");
    }

    [TestMethod]
    public void Arrays()
    {
        Check("""
        const a = [1, 2, 3];
        a[1] = 10;
        let t = 0;
        for (const v of a) t += v;
        Console.WriteLine(t);
        Console.WriteLine(a.Length);
        """, "14\n3\n");
    }

    [TestMethod]
    public void NullableOps()
    {
        Check("""
        function f(s: string | null): int { return s?.Length ?? -1; }
        Console.WriteLine(f(null));
        Console.WriteLine(f("abcd"));
        """, "-1\n4\n");
    }
}
