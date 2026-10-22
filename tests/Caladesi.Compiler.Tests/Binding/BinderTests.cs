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

    [TestMethod]
    public void Item03a_ReturnInFinally_IsReported()
    {
        AssertError("function f(): int { try { return 1; } finally { return 2; } }\nConsole.WriteLine(f());", Descriptors.JumpOutOfFinally, OutputKind.ConsoleApplication);
    }

    [TestMethod]
    public void Item03b_BreakAndContinueInFinally_AreReported()
    {
        AssertError("function f(): int { let n = 0; while (true) { try { n++; } finally { break; } } return n; }\nConsole.WriteLine(f());", Descriptors.JumpOutOfFinally, OutputKind.ConsoleApplication);
        AssertError("function f(): int { let n = 0; while (n < 3) { try { n++; } finally { continue; } } return n; }\nConsole.WriteLine(f());", Descriptors.JumpOutOfFinally, OutputKind.ConsoleApplication);
    }

    [TestMethod]
    public void Item03c_JumpsInsideFinally_AreAllowed()
    {
        AssertClean("function f(): int { let n = 0; try { n++; } finally { for (let i = 0; i < 3; i++) { if (i == 1) continue; if (i == 2) break; n++; } const g = (): int => { return 1; }; n += g(); } return n; }\nConsole.WriteLine(f());");
    }

    [TestMethod]
    public void Item04_RethrowInLambdaInsideCatch_IsReported()
    {
        AssertError("function f(): void { throw new Exception(\"x\"); }\ntry { f(); } catch (e) { const g = () => { throw; }; g(); }", Descriptors.RethrowOutsideCatch, OutputKind.ConsoleApplication);
    }

    [TestMethod]
    public void Item07_StructMemberWriteThroughProperty_IsReported()
    {
        AssertError("""
            import { Vector2 } from "System.Numerics";
            class P { pos: Vector2 = new Vector2(0, 0); m(): void { this.pos.X = 1; } }
            """, Descriptors.ModifyValueTypeTemporary);
        AssertError("""
            import { Vector2 } from "System.Numerics";
            class P { pos: Vector2 = new Vector2(0, 0); m(): void { this.pos.X += 1; } }
            """, Descriptors.ModifyValueTypeTemporary);
    }

    [TestMethod]
    public void Item13_OverrideWithDifferentReturnType_IsReported()
    {
        AssertError("""
            class B { f(): int { return 1; } }
            class D extends B { override f(): string { return "x"; } }
            """, Descriptors.OverrideTypeMismatch);
    }

    [TestMethod]
    public void Item13_OverridePropertyWithDifferentType_IsReported()
    {
        AssertError("""
            class B { get v(): int { return 1; } }
            class D extends B { override get v(): string { return "x"; } }
            """, Descriptors.OverrideTypeMismatch);
    }

    [TestMethod]
    public void Item14_InterfacePropertyWithWrongType_IsNotAnImplementation()
    {
        AssertError("""
            interface I { x: string }
            class C implements I { x: int = 1; }
            """, Descriptors.InterfaceMemberNotImplemented);
    }

    [TestMethod]
    public void Item15_ExtendingBclCollection_IsClean()
    {
        AssertClean("class MyList extends List<int> {}", OutputKind.DynamicallyLinkedLibrary);
    }

    [TestMethod]
    public void Item16_CamelCasePropertyOverrideOfPascalCaseAbstractProperty_IsClean()
    {
        AssertClean("""
            import { Stream, SeekOrigin } from "System.IO";
            class S extends Stream {
              override get canRead(): bool { return true; }
              override get canSeek(): bool { return false; }
              override get canWrite(): bool { return false; }
              override get length(): long { return 0; }
              override get position(): long { return 0; }
              override set position(v: long) {}
              override flush(): void {}
              override read(buffer: byte[], offset: int, count: int): int { return 0; }
              override seek(offset: long, origin: SeekOrigin): long { return 0; }
              override setLength(value: long): void {}
              override write(buffer: byte[], offset: int, count: int): void {}
            }
            """, OutputKind.DynamicallyLinkedLibrary);
    }

    [TestMethod]
    public void Item17a_NarrowingDoesNotSurviveBreakOutOfDoWhile()
    {
        AssertError("function f(s: string | null): int { do { if (s == null) break; } while (false); return s.Length; }", Descriptors.PossiblyNull);
    }

    [TestMethod]
    public void Item17b_ForIncrementorDoesNotSeeNarrowingFromContinuedBody()
    {
        AssertError("function f(s: string | null): int { let i = 0; for (; i < 3; i += s.Length) { if (s == null) continue; } return i; }", Descriptors.PossiblyNull);
    }

    [TestMethod]
    public void Item17c_SwitchFallthroughDoesNotApplyNextCaseNarrowing()
    {
        AssertError("""
            type Circle = { r: double };
            type Square = { side: double };
            type Shape = Circle | Square;
            function f(x: Shape): double { switch (x) { case Circle: Console.WriteLine("c"); case Square: return x.side; } }
            """, Descriptors.MemberNotFound);
    }

    [TestMethod]
    public void Item17d_InstanceofFalseBranchKeepsNullOfNullableUnion()
    {
        AssertError("""
            type Circle = { r: double };
            type Square = { side: double };
            function f(x: Circle | Square | null): double { if (x instanceof Circle) return 1; return x.side; }
            """, Descriptors.PossiblyNull);
    }

    [TestMethod]
    public void Item17_ValidNarrowingStillWorks()
    {
        AssertClean("""
            type Circle = { r: double };
            type Square = { side: double };
            type Shape = Circle | Square;
            function a(s: string | null): int { do { if (s == null) break; } while (false); return s == null ? 0 : s.Length; }
            function b(s: string | null): int { if (s == null) return 0; let i = 0; for (; i < 3; i += s.Length) { if (i == 1) continue; } return i; }
            function c(x: Shape): double { switch (x) { case Circle: return x.r; case Square: return x.side; } }
            function d(x: Shape): double { if (x instanceof Circle) return x.r; return x.side; }
            function e(x: Circle | Square | null): double { if (x == null) return 0; if (x instanceof Circle) return x.r; return x.side; }
            """, OutputKind.DynamicallyLinkedLibrary);
    }

    [TestMethod]
    public void Item18_LabeledBlockExitedWithBreakCompletesNormally()
    {
        AssertError("function f(x: bool): int { outer: { if (x) break outer; return 1; } }", Descriptors.NotAllPathsReturn);
        AssertClean("function f(x: bool): int { outer: { if (x) break outer; return 1; } return 2; }", OutputKind.DynamicallyLinkedLibrary);
    }

    [TestMethod]
    public void Item19c_AwaitUsingInNonAsyncFunction_IsReported()
    {
        AssertError("""
            class AsyncRes implements IAsyncDisposable { DisposeAsync(): ValueTask { return ValueTask.CompletedTask; } }
            function f(): void { await using x = new AsyncRes(); }
            """, Descriptors.AwaitOutsideAsync);
    }

    [TestMethod]
    public void Item19g_DuplicateObjectLiteralKeysAreReportedNotThrown()
    {
        AssertError("""
            type Point = { x: int; y: int };
            const p: Point = { x: 1, x: 2, y: 3 };
            """, Descriptors.DuplicateObjectLiteralKey, OutputKind.ConsoleApplication);
        AssertError("const o = { x: 1, x: 2 };\nConsole.WriteLine(o.x);", Descriptors.DuplicateObjectLiteralKey, OutputKind.ConsoleApplication);
    }

    [TestMethod]
    public void Item19j_SelfReferentialStaticPropertyIsReportedNotOverflowed()
    {
        AssertError("class C { static a = C.a; }", Descriptors.RecursiveInference);
    }

    [TestMethod]
    public void Item19k_DeeplyNestedParenthesesAreReportedNotOverflowed()
    {
        string source = "const x = " + new string('(', 20000) + "1" + new string(')', 20000) + ";";
        AssertError(source, Descriptors.NestingTooDeep, OutputKind.ConsoleApplication);
        Assert.AreEqual(source, Compiler.Syntax.SyntaxTree.ParseText(source, "/proj/main.cala").Root.ToFullString());
    }

    [TestMethod]
    public void Item23_AmbiguousOverloadIsReported()
    {
        AssertError("""
            function f(a: int, b: long): string { return "A"; }
            function f(a: long, b: int): string { return "B"; }
            Console.WriteLine(f(1, 2));
            """, Descriptors.AmbiguousCall, OutputKind.ConsoleApplication);
    }

    [TestMethod]
    public void Item23_UnambiguousOverloadsStillResolve()
    {
        AssertClean("""
            function f(a: int, b: long): string { return "A"; }
            function f(a: long, b: int): string { return "B"; }
            Console.WriteLine(f(1, 2L));
            """);
    }

    [TestMethod]
    public void Item24_OverloadsDifferingOnlyByNullabilityAreDuplicates()
    {
        AssertError("""
            function f(x: string): string { return "a"; }
            function f(x: string | null): string { return "b"; }
            """, Descriptors.DuplicateMember);
    }

    [TestMethod]
    public void Item25_TargetTypedNewOfOpenGenericAgainstObjectIsReported()
    {
        AssertError("const o: object = new List();", Descriptors.TypeRequiresArguments, OutputKind.ConsoleApplication);
    }

    [TestMethod]
    public void Item26_ImpossibleInstanceofIsAWarning()
    {
        ImmutableArray<Diagnostic> d = TestCompiler.Diagnose("const x: int = 1;\nConsole.WriteLine(x instanceof string);");
        Diagnostic hit = d.Single(x => x.Id == Descriptors.InstanceOfImpossible.Id);
        Assert.AreEqual(DiagnosticSeverity.Warning, hit.EffectiveSeverity);
        Assert.IsFalse(d.Any(x => x.EffectiveSeverity == DiagnosticSeverity.Error), TestCompiler.Format(d));
    }

    [TestMethod]
    public void Item26_CallsIntoSourceCodeResetFieldNarrowingButLibraryCallsDoNot()
    {
        AssertError("""
            class K {
              name: string | null = "a";
              clear(): void { this.name = null; }
              m(): int { if (this.name != null) { this.clear(); return this.name.Length; } return 0; }
            }
            """, Descriptors.PossiblyNull);
        AssertClean("""
            class K {
              name: string | null = "a";
              m(): int { if (this.name != null) { Console.WriteLine("x"); return this.name.Length; } return 0; }
            }
            """, OutputKind.DynamicallyLinkedLibrary);
    }

    [TestMethod]
    public void Item28b_ConcatenatingNullableNonStringStillReportsNull()
    {
        AssertError("function j(a: string, b: int | null): string { return a + b; }", Descriptors.PossiblyNull);
    }

    [TestMethod]
    public void Item32_NonVoidMethodGroupDoesNotConvertToVoidDelegate()
    {
        AssertError("const a: Action = Console.ReadLine;", Descriptors.MethodGroupNotConvertible, OutputKind.ConsoleApplication);
        AssertClean("const a: Action = Console.WriteLine;\nconst b: Func<string | null> = Console.ReadLine;", OutputKind.ConsoleApplication);
    }
}
