using System.Diagnostics.CodeAnalysis;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.Syntax;

[TestClass]
public class ParserTests
{
    private static readonly string[] s_forInExportDiagnostics = ["CAL0026", "CAL0021"];

    internal static readonly string[] ValidSamples =
    [
        "import { List, Dictionary } from \"System.Collections.Generic\";\nimport * as IO from \"System.IO\";\nimport \"System.Linq\";\nimport type { Foo } from './foo';",
        "const x = 1;\nlet y: double = 2.5, z = 10L;\nconst s = `a ${x + 1} b ${ {a: 1}.a } c`;",
        "export function add(a: int, b: int = 2, ...rest: int[]): int { return a + b; }",
        "export async function* gen<T extends IComparable<T> & new()>(xs: T[]): IAsyncEnumerable<T> { for await (const x of xs) yield x; }",
        "type Point = { x: double; y: double; z?: int; readonly w: string };\nstruct type P2 = { x: int, y: int }\ntype Id = string;\ntype Dir = 'up' | 'down';\ntype Shape = Circle | Square | null;",
        "export class Foo<in T, out U> extends Bar<T> implements IBaz, IQux { private readonly n: int = 0x1F_FF; static count = 0; constructor(public a: int, private b: string) { super(a); } override toString(): string { return this.n as string; } get area(): double { return 1; } set area(v: double) { } abstract go(): void; async run(): Task { await Task.Delay(1); } }",
        "@Obsolete(\"x\") export interface I<T> extends J { name: string; run(x: int): void; get value(): int; }",
        "enum Color { Red, Green = 2, Blue }",
        "const f = (a: int, b) => a + b; const g = async x => await x; const h = <T>(x: T): T => x; const k = () => { return 1; };",
        "if (a < b && c > d) { x = a >> 2; y = a >>> 3; z >>= 1; w >>>= 2; v = a >= b; } else if (!c) throw new Error('x'); else x++;",
        "for (let i = 0; i < 10; i++) { continue; }\nfor (const x of xs) break;\nwhile (true) { }\ndo { } while (false);\nouter: for (;;) { break outer; }",
        "switch (x) { case 1: case 2: foo(); break; case Circle: { break; } default: bar(); }",
        "try { a(); } catch (e: IOException) { throw; } catch (e) { } finally { b(); }",
        "using f = new File(); await using g = open(); const { a, b: c = 2, ...rest } = obj; const [p, , q, ...r] = arr;",
        "const l = new List<int>(); const m = new Dictionary<string, List<int>>(); const a = new int[5]; const t: [int, string] = [1, 'a']; const u = xs.Select<int, int>(x => x * 2);",
        "const o = { a: 1, 'b': 2, c, ...d }; const arr = [1, 2, ...rest]; const r = a ?? b; a ??= 1; const n = a?.b?.[0]!.c; const e = 2 ** 3 ** 2;",
        "const v = x satisfies Foo; const w = y instanceof Bar; const q = cond ? 1 : 2; function f(this s: string): int { return 1; }",
        "// comment\n/* block */ let a = 1; // trailing\n/// doc\nlet b = 2;",
        "type F = (a: int) => string; type G = <T>(x: T) => T; let ro: readonly int[] = []; let ro2: IReadOnlyList<int>; let nn: string | null; let nn2: string?; let nn3: int?[];",
    ];

    [TestMethod]
    public void ValidSamples_RoundTripWithoutDiagnostics()
    {
        foreach (string src in ValidSamples)
        {
            SyntaxTree tree = SyntaxTree.ParseText(src, "t.cala");
            Assert.AreEqual(src, tree.Root.ToFullString());
            Assert.IsEmpty(tree.Diagnostics, src + "\n" + string.Join("\n", tree.Diagnostics));
        }
    }

    [TestMethod]
    public void EveryPrefixAndMutation_RoundTripsAndNeverThrows()
    {
        foreach (string src in ValidSamples)
        {
            for (int n = 0; n <= src.Length; n += 3)
            {
                string prefix = src[..n];
                SyntaxTree tree = SyntaxTree.ParseText(prefix, "t.cala");
                Assert.AreEqual(prefix, tree.Root.ToFullString(), "input: [" + prefix + "]");
            }
            for (int n = 0; n < src.Length; n += 7)
            {
                string cut = src.Remove(n, Math.Min(3, src.Length - n));
                SyntaxTree tree = SyntaxTree.ParseText(cut, "t.cala");
                Assert.AreEqual(cut, tree.Root.ToFullString(), "input: [" + cut + "]");
            }
        }
    }

    [TestMethod]
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded Random keeps the garbage inputs reproducible; this is not security-sensitive.")]
    public void Garbage_DoesNotCrash()
    {
        Random rnd = new(42);
        const string alphabet = "abc (){}[]<>;:,.=+-*/&|!?`'\"\n \t0123@#~^%";
        for (int i = 0; i < 300; i++)
        {
            char[] chars = new char[rnd.Next(1, 80)];
            for (int j = 0; j < chars.Length; j++)
            {
                chars[j] = alphabet[rnd.Next(alphabet.Length)];
            }

            string s = new(chars);
            SyntaxTree tree = SyntaxTree.ParseText(s, "t.cala");
            Assert.AreEqual(s, tree.Root.ToFullString(), "input: [" + s + "]");
        }
    }

    [TestMethod]
    public void BinaryPrecedence_And_GreaterThanComposition()
    {
        SyntaxTree tree = SyntaxTree.ParseText("const x = 1 + 2 * 3 >> 1;", "t.cala");
        BinaryExpressionSyntax bin = tree.Root.DescendantNodes().OfType<BinaryExpressionSyntax>().First();
        Assert.AreEqual(SyntaxKind.GreaterThanGreaterThanToken, bin.OperatorKind);
    }

    [TestMethod]
    public void NestedGenerics_DoNotConsumeShift()
    {
        SyntaxTree tree = SyntaxTree.ParseText("let x: List<List<int>> = new List<List<int>>();", "t.cala");
        Assert.IsEmpty(tree.Diagnostics);
        Assert.AreEqual(2, tree.Root.DescendantNodes().OfType<GenericNameSyntax>().Count(g => g.Name == "List" && g.TypeArguments.Arguments.Count == 1 && g.TypeArguments.Arguments[0] is GenericNameSyntax));
    }

    [TestMethod]
    public void ForIn_And_ExportDefault_ReportErrors()
    {
        SyntaxTree tree = SyntaxTree.ParseText("export default function f() { } for (const k in o) { }", "t.cala");
        CollectionAssert.IsSubsetOf(s_forInExportDiagnostics, tree.Diagnostics.Select(d => d.Id).ToArray());
    }

    [TestMethod]
    public void Spans_AreAccurate()
    {
        string src = "  let abc = 12 ;";
        SyntaxTree tree = SyntaxTree.ParseText(src, "t.cala");
        VariableDeclaratorSyntax decl = tree.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        Assert.AreEqual("abc = 12", src.Substring(decl.Span.Start, decl.Span.Length));
        SyntaxToken tok = tree.Root.FindToken(src.IndexOf("12", StringComparison.Ordinal))!;
        Assert.AreEqual("12", tok.Text);
    }
}

