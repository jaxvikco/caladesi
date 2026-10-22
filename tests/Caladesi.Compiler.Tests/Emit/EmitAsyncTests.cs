namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public class EmitAsyncTests
{
    private static void Check(string source, string expected)
    {
        var (output, _) = EmitHarness.Run(source);
        Assert.AreEqual(expected, output);
    }

    [TestMethod]
    public void AsyncReturnsValue() => Check("""
        async function add(a: int, b: int): Task<int> {
          await Task.Delay(1);
          return a + b;
        }
        Console.WriteLine(add(2, 3).Result);
        """, "5\n");

    [TestMethod]
    public void AwaitInExpression() => Check("""
        async function one(): Task<int> { await Task.Yield(); return 1; }
        async function run(): Task<int> {
          const x = 10 + await one() + (await one()) * 3;
          return x;
        }
        Console.WriteLine(run().Result);
        """, "14\n");

    [TestMethod]
    public void AwaitInLoopAndTry() => Check("""
        async function work(n: int): Task<int> {
          let sum = 0;
          for (let i = 0; i < n; i++) {
            try {
              await Task.Delay(1);
              if (i == 2) throw new Exception("two");
              sum += i;
            } catch (e: Exception) {
              await Task.Delay(1);
              sum += 100;
            } finally {
              await Task.Yield();
              sum += 1000;
            }
          }
          return sum;
        }
        Console.WriteLine(work(4).Result);
        """, "4104\n");

    [TestMethod]
    public void VoidTaskAsync() => Check("""
        async function hello(): Task {
          await Task.Delay(1);
          Console.WriteLine("hi");
        }
        hello().Wait();
        """, "hi\n");

    [TestMethod]
    public void AsyncLambdaAndMethod() => Check("""
        class Svc {
          name = "svc";
          async greet(who: string): Task<string> { await Task.Yield(); return `${this.name} hello ${who}`; }
        }
        const f = async (x: int): Task<int> => { await Task.Yield(); return x * 2; };
        Console.WriteLine(new Svc().greet("bob").Result);
        Console.WriteLine(f(21).Result);
        """, "svc hello bob\n42\n");

    [TestMethod]
    public void ExceptionPropagatesThroughTask() => Check("""
        async function boom(): Task<int> { await Task.Yield(); throw new InvalidOperationException("bad"); }
        try { boom().Wait(); } catch (e: AggregateException) { Console.WriteLine(e.InnerException!.Message); }
        """, "bad\n");

    [TestMethod]
    public void TopLevelAwait() => Check("""
        async function twice(x: int): Task<int> { await Task.Yield(); return x * 2; }
        const v = await twice(21);
        Console.WriteLine(v);
        """, "42\n");

    [TestMethod]
    public void AsyncMainWithExitCode()
    {
        var (output, code) = EmitHarness.Run("""
            async function main(args: string[]): Task<int> {
              await Task.Delay(1);
              Console.WriteLine("main");
              return 7;
            }
            """);
        Assert.AreEqual("main\n", output);
        Assert.AreEqual(7, code);
    }

    [TestMethod]
    public void AsyncGeneratorAndForAwait() => Check("""
        async function* numbers(n: int): AsyncIterable<int> {
          for (let i = 0; i < n; i++) {
            await Task.Delay(1);
            yield i * 10;
          }
        }
        let total = 0;
        for await (const x of numbers(4)) total += x;
        Console.WriteLine(total);
        """, "60\n");

    [TestMethod]
    public void AsyncGeneratorFinallyRunsOnEarlyExit() => Check("""
        async function* gen(): AsyncIterable<string> {
          try {
            yield "a";
            yield "b";
          } finally {
            await Task.Yield();
            Console.WriteLine("cleanup");
          }
        }
        for await (const s of gen()) { Console.WriteLine(s); break; }
        """, "a\ncleanup\n");

    [TestMethod]
    public void AwaitUsing() => Check("""
        class Res implements IAsyncDisposable {
          async DisposeAsync(): ValueTask { await Task.Yield(); Console.WriteLine("disposed"); }
        }
        async function run(): Task {
          await using r = new Res();
          Console.WriteLine("body");
        }
        await run();
        """, "body\ndisposed\n");
}
