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

    [TestMethod]
    public void Item02a_IteratorFinallyRunsOnBreakSync()
    {
        Check("""
        function* sf(): IEnumerable<int> { try { for (let i = 0; i < 3; i++) { yield i; } } finally { Console.WriteLine("fin-sf"); } }
        function* sw(): IEnumerable<int> { try { let i = 0; while (i < 3) { yield i; i++; } } finally { Console.WriteLine("fin-sw"); } }
        function* sfo(): IEnumerable<int> { try { for (const x of [1, 2, 3]) { yield x; } } finally { Console.WriteLine("fin-sfo"); } }
        for (const t of sf()) { break; } Console.WriteLine("sf done");
        for (const t of sw()) { break; } Console.WriteLine("sw done");
        for (const t of sfo()) { break; } Console.WriteLine("sfo done");
        const en = sf().GetEnumerator();
        en.MoveNext(); en.Dispose(); Console.WriteLine("manual done");
        """, "fin-sf\nsf done\nfin-sw\nsw done\nfin-sfo\nsfo done\nfin-sf\nmanual done\n");
    }

    [TestMethod]
    public void Item02b_IteratorFinallyRunsWhenYieldOperandIsHoisted()
    {
        Check("""
        class D implements IDisposable { constructor(public name: string) {} Dispose(): void { Console.WriteLine("disposed " + this.name); } }
        function* param(n: int): IEnumerable<int> { try { yield n; yield n; } finally { Console.WriteLine("fin-param"); } }
        function* hoisted(): IEnumerable<int> { try { let i = 5; yield i; i++; yield i; } finally { Console.WriteLine("fin-hoisted"); } }
        class H { v = 9; *m(): IEnumerable<int> { try { yield this.v; yield 1; } finally { Console.WriteLine("fin-this"); } } }
        function* usingIt(): IEnumerable<int> { using d = new D("iter"); yield 1; yield 2; }
        for (const t of param(1)) { break; } Console.WriteLine("param done");
        for (const t of hoisted()) { break; } Console.WriteLine("hoisted done");
        for (const t of new H().m()) { break; } Console.WriteLine("this done");
        for (const t of usingIt()) { break; } Console.WriteLine("usingIt done");
        """, "fin-param\nparam done\nfin-hoisted\nhoisted done\nfin-this\nthis done\ndisposed iter\nusingIt done\n");
    }

    [TestMethod]
    public void Item02c_AsyncIteratorFinallyRunsOnBreak()
    {
        Check("""
        async function* aparam(n: int): IAsyncEnumerable<int> { try { yield n; yield n; } finally { Console.WriteLine("fin-aparam"); } }
        async function* a(): IAsyncEnumerable<int> { try { await Task.Delay(1); yield 1; yield 2; } finally { Console.WriteLine("fin-a"); } }
        async function* d(): IAsyncEnumerable<int> { try { for (let i = 0; i < 3; i++) { yield i; await Task.Delay(1); } } finally { Console.WriteLine("fin-d"); } }
        for await (const t of aparam(1)) { break; } Console.WriteLine("aparam done");
        for await (const t of a()) { break; } Console.WriteLine("a done");
        for await (const t of d()) { break; } Console.WriteLine("d done");
        """, "fin-aparam\naparam done\nfin-a\na done\nfin-d\nd done\n");
    }

    [TestMethod]
    public void Item05_MethodSpecCacheSeparatesClassAndMethodTypeParameters()
    {
        Check("""
        function id<T>(x: T): T { return x; }
        function pick<T>(x: T): T { return id(x); }
        class Box<T> { constructor(public v: T) {} get(): T { return id(this.v); } }
        Console.WriteLine(pick(5));
        Console.WriteLine(new Box<string>("s").get());
        Console.WriteLine(new Box<int>(7).get());
        """, "5\ns\n7\n");
    }

    [TestMethod]
    public void Item06_RefReturnOfConstructedGenericIsDereferenced()
    {
        Check("""
        function first(): int { const sp: Span<int> = [1, 2, 3]; return sp[0]; }
        Console.WriteLine(first());
        """, "1\n");
    }

    [TestMethod]
    public void Item07_CompoundStructMemberWritesMutateFieldsAndArrayElements()
    {
        Check("""
        import { Vector2 } from "System.Numerics";
        class P { private ppos: Vector2 = new Vector2(0, 0); arr: Vector2[] = [new Vector2(0, 0)];
          m(): void { this.ppos.X += 1; this.ppos.X++; this.arr[0].X += 1; this.arr[0].Y = 5; this.arr[0].X++; }
          show(): string { return `${this.ppos.X} ${this.arr[0].X} ${this.arr[0].Y}`; } }
        const p = new P(); p.m(); Console.WriteLine(p.show());
        let local = new Vector2(0, 0);
        local.X = 3; local.Y += 4;
        Console.WriteLine(`${local.X} ${local.Y}`);
        """, "2 2 5\n3 4\n");
    }

    [TestMethod]
    public void Item08_IntConvertsToUnionDoubleCase()
    {
        Check("""
        type Num = double | string;
        function f(x: int): Num { return x; }
        Console.WriteLine(f(3));
        """, "3\n");
    }

    [TestMethod]
    public void Item09_DoubleToDecimalKeepsFraction()
    {
        Check("""
        function toDec(x: double): decimal { return x as decimal; }
        Console.WriteLine(toDec(3.75));
        """, "3.75\n");
    }

    [TestMethod]
    public void Item10_DoubleToUlongIsUnsigned()
    {
        Check("""
        function f(x: double): ulong { return x as ulong; }
        Console.WriteLine(f(1e19));
        """, "10000000000000000000\n");
    }
}
