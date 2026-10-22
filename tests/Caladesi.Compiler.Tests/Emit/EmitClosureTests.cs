namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public class EmitClosureTests
{
    private static void Check(string source, string expected)
    {
        (string? output, int _) = EmitHarness.Run(source);
        Assert.AreEqual(expected, output);
    }

    [TestMethod]
    public void NonCapturingLambdaWithLinq()
    {
        Check("""
        const xs = [1, 2, 3, 4];
        Console.WriteLine(xs.Where(x => x % 2 == 0).Select(x => x * 10).Sum());
        """, "60\n");
    }

    [TestMethod]
    public void CapturesLocal()
    {
        Check("""
        function make(): () => int {
          let n = 0;
          return () => { n++; return n; };
        }
        const c = make();
        c(); c();
        Console.WriteLine(c());
        """, "3\n");
    }

    [TestMethod]
    public void CapturesParameter()
    {
        Check("""
        function adder(k: int): (x: int) => int { return x => x + k; }
        Console.WriteLine(adder(5)(10));
        """, "15\n");
    }

    [TestMethod]
    public void NestedClosuresShareVariables()
    {
        Check("""
        function f(): int {
          let total = 0;
          const add = (n: int) => { const inc = () => { total += n; }; inc(); };
          add(2); add(3);
          return total;
        }
        Console.WriteLine(f());
        """, "5\n");
    }

    [TestMethod]
    public void CapturesThis()
    {
        Check("""
        class Counter {
          count = 0;
          make(): () => int { return () => { this.count++; return this.count; }; }
          makeWith(step: int): () => int { return () => { this.count += step; return this.count; }; }
        }
        const c = new Counter();
        const a = c.make();
        a();
        Console.WriteLine(a());
        Console.WriteLine(c.makeWith(10)());
        """, "2\n12\n");
    }

    [TestMethod]
    public void LoopVariableCapture()
    {
        Check("""
        const fs = new List<() => int>();
        for (let i = 0; i < 3; i++) {
          const j = i * 10;
          fs.Add(() => j);
        }
        let sum = 0;
        for (const f of fs) sum += f();
        Console.WriteLine(sum);
        """, "30\n");
    }

    [TestMethod]
    public void LocalFunctionRecursion()
    {
        Check("""
        function fact(n: int): int {
          function go(k: int): int { return k <= 1 ? 1 : k * go(k - 1); }
          return go(n);
        }
        Console.WriteLine(fact(5));
        """, "120\n");
    }

    [TestMethod]
    public void GenericMethodClosure()
    {
        Check("""
        function mapAll<T, U>(xs: T[], f: (x: T) => U): U[] {
          const res = new List<U>();
          for (const x of xs) res.Add(f(x));
          return res.ToArray();
        }
        function twice<T>(x: T, join: (a: T, b: T) => string): string { return join(x, x); }
        Console.WriteLine(mapAll([1, 2, 3], x => x * 2).Length);
        Console.WriteLine(twice<string>("ab", (a, b) => a + b));
        """, "3\nabab\n");
    }
}
