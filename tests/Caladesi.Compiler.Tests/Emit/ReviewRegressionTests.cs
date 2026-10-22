namespace Caladesi.Compiler.Tests.Emit;

/// <summary>Regression tests for the 2026-10-22 full-tree review; test names carry the finding number.</summary>
[TestClass]
public class ReviewRegressionTests
{
    private static void Check(string source, string expected)
    {
        (string? output, int _) = EmitHarness.Run(source);
        Assert.AreEqual(expected, output);
    }

    [TestMethod]
    public void Item01a_GenericTEqualsBoxes()
    {
        Check("""
        function eqs<T>(a: T, b: T): bool { return a.Equals(b); }
        Console.WriteLine(eqs(1, 1));
        """, "True\n");
    }

    [TestMethod]
    public void Item01b_GenericTToWriteLineBoxes()
    {
        Check("""
        function w<T>(a: T): void { Console.WriteLine(a); }
        w(1); w("s");
        """, "1\ns\n");
    }

    [TestMethod]
    public void Item01c_GenericTNonNullAssertionToObjectBoxes()
    {
        Check("""
        function toObj<T>(a: T): object { const o: object = a!; return o; }
        Console.WriteLine(toObj(1));
        Console.WriteLine(toObj("s"));
        """, "1\ns\n");
    }

    [TestMethod]
    public void Item01d_GenericTNullableToObjectBoxes()
    {
        Check("""
        function f<T>(x: T | null): object | null { return x; }
        Console.WriteLine(f(5));
        """, "5\n");
    }
}
