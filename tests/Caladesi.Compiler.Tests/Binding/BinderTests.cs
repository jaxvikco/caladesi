using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Emit;

namespace Caladesi.Compiler.Tests.Binding;

[TestClass]
public class BinderTests
{
    private static void AssertClean(string src, OutputKind kind = OutputKind.ConsoleApplication)
    {
        ImmutableArray<Diagnostic> d = TestCompiler.Diagnose(src, kind);
        Assert.IsEmpty(d, TestCompiler.Format(d));
    }

    private static void AssertError(string src, DiagnosticDescriptor descriptor, OutputKind kind = OutputKind.DynamicallyLinkedLibrary)
    {
        string code = descriptor.Id;
        ImmutableArray<Diagnostic> d = TestCompiler.Diagnose(src, kind);
        Assert.IsTrue(d.Any(x => x.Id == code), "expected " + code + " but got:\n" + TestCompiler.Format(d));
    }

    [TestMethod]
    public void LibraryFileWithOnlyAVariable_ReportsInitializerErrors()
    {
        AssertError("export const a: string = 5;", Descriptors.NotAssignable);
    }

    [TestMethod]
    public void RefStructTopLevelVariable_IsRejected()
    {
        AssertError("const s: Span<int> = new Span<int>([1, 2]);\nConsole.WriteLine(s.Length);", Descriptors.RefStructModuleVariable, OutputKind.ConsoleApplication);
    }

    [TestMethod]
    public void AwaitInsideConditionalAccess_IsReportedNotThrown()
    {
        Compilation comp = TestCompiler.Create("""
            class P { name = "x"; echo(s: string): string { return s + this.name; } }
            async function g(): Task<string> { return "a"; }
            async function main(): Task {
              const p: P | null = new P();
              Console.WriteLine(p?.echo(await g()));
            }
            """, OutputKind.ConsoleApplication, "/proj/main.cala");
        using MemoryStream pe = new();
        EmitResult result = comp.Emit(pe, null, null);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Diagnostics.Any(x => x.Id == Descriptors.UnsupportedConstruct.Id), TestCompiler.Format(result.Diagnostics));
    }

    [TestMethod]
    public void HelloWorld()
    {
        AssertClean("Console.WriteLine('hello');");
    }

    [TestMethod]
    public void Variables_Arithmetic_Functions()
    {
        AssertClean("""
        function add(a: int, b: int): int { return a + b; }
        const x = add(1, 2) * 3;
        let y: double = x / 2;
        let s = `x=${x} y=${y}`;
        Console.WriteLine(s);
        """);
    }

    [TestMethod]
    public void Linq_Lambdas_Infer()
    {
        AssertClean("""
        const xs = [1, 2, 3, 4];
        const ys = xs.Where(x => x % 2 == 0).Select(x => x * 10).ToList();
        const total = xs.Sum();
        Console.WriteLine(ys.Count + total);
        """);
    }

    [TestMethod]
    public void Collections_List_Dictionary()
    {
        AssertClean("""
        import { List, Dictionary } from "System.Collections.Generic";
        const l = new List<int>();
        l.Add(3);
        const d = new Dictionary<string, int>();
        d["a"] = 1;
        for (const v of l) Console.WriteLine(v);
        """);
    }

    [TestMethod]
    public void Undefined_Name_Error()
    {
        AssertError("function f() { Console.WriteLine(nope); }", Descriptors.NameNotFound);
    }

    [TestMethod]
    public void Type_Mismatch_Error()
    {
        AssertError("function f() { let x: int = 'a'; }", Descriptors.NotAssignable);
    }

    [TestMethod]
    public void NullStrictness()
    {
        AssertError("function f() { let s: string = null; }", Descriptors.NullNotAssignable);
    }

    [TestMethod]
    public void Narrowing_Allows_Deref()
    {
        AssertClean("""
        function len(s: string | null): int {
            if (s == null) return 0;
            return s.Length;
        }
        Console.WriteLine(len('abc'));
        """);
    }

    [TestMethod]
    public void Possibly_Null_Deref_Error()
    {
        AssertError("""
        function len(s: string | null): int { return s.Length; }
        """, Descriptors.PossiblyNull);
    }
}
