extern alias SelfHost;

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Tests.Syntax;
using Caladesi.Compiler.Text;
using SelfElement = SelfHost::Caladesi.Compiler.Syntax.SyntaxElement;
using SelfNode = SelfHost::Caladesi.Compiler.Syntax.SyntaxNode;
using SelfToken = SelfHost::Caladesi.Compiler.Syntax.SyntaxToken;
using SelfTree = SelfHost::Caladesi.Compiler.Syntax.SyntaxTree;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class ParserParityTests
{
    [TestMethod]
    public void Parser_AllValidSamplesAndEveryPrefixAndMutationMatchBootstrap()
    {
        foreach (string source in ParserTests.ValidSamples)
        {
            Assert.AreEqual(source, SelfTree.ParseText(source).Root.ToFullString());
            AssertParity(source);
            for (int length = 0; length <= source.Length; length += 3)
            {
                AssertParity(source[..length]);
            }

            for (int start = 0; start < source.Length; start += 7)
            {
                AssertParity(source.Remove(start, Math.Min(3, source.Length - start)));
            }
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("/* only trivia */\r\n// end")]
    [DataRow("export default function f() {} for (const k in o) {}")]
    [DataRow("class C { public public x: int; constructor(public x: int) {} get v(): int; set v(x: int); }")]
    [DataRow("class C { [x: string]: int; get [x](): int {} set [x](v: int) {} *f() { yield* xs; } }")]
    [DataRow("function f({a, b: c = 1, ...d}: O, [x,,...y]: A) { return; }\nlet [a,,...b] = xs;")]
    [DataRow("const x = a ? (x: int): int => x : async y => y; const y = a < b > (c);")]
    [DataRow("const x = async<T>(a: T): T => a; const y = <T>(a: T) => a; const z = async(a);")]
    [DataRow("const x = a?.<T>(x); const y = a?.(x); const z = a?.[0]!;")]
    [DataRow("const a = -2 ** 3; const b = await x ** y; const c = ++x ** y; a === b; a !== b;")]
    [DataRow("a >> b; a >>> b; a >>= b; a >>>= b; a > > b; a >/* gap */= b;")]
    [DataRow("const x = new int[2, 3]; const y = new int[] {1, 2}; const z = new List<List<int>>();")]
    [DataRow("const o = { get x() { return 1; }, f<T>(x: T) { return x; }, [a + b]: 1, ...rest };")]
    [DataRow("type T<in A extends class & new() = int, out B extends struct> = | readonly A?[] | [x: B, A];")]
    [DataRow("type U = <T>(a: T) => T & { readonly x?: T; (x: T): T; };")]
    [DataRow("import x from 'x'; import {type X, A as B,} from 'm'; import * as M from 'm';")]
    [DataRow("export {x}; export * from 'm'; namespace N { } const x = /abc/g; const y = class {};")]
    [DataRow("return\nx; throw\nx; break\nlabel; continue\nlabel; yield\nx; x\n++;")]
    [DataRow("if (a) b(); else if (c) d(); for await (let x of xs) {} for await (;;);")]
    [DataRow("try {} catch (e: E) {} catch {} finally {} switch (x) { case : ; default: ; }")]
    [DataRow("const x = `a${{x: `b${f()}`}.x}z`; const y = (a, b);")]
    [DataRow("function f<T( { let x: = ; return (1 + ); @ class C { get x( }")]
    [DataRow("const = ; let x = ; ; : ; ((( } ] > @ #")]
    public void Parser_RecoverySpeculationAndLanguageEdgesMatchBootstrap(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        AssertParity(source);
    }

    [TestMethod]
    public void Parser_UnsupportedTypeofTypeRecoveryMatchesBootstrap()
    {
        // The bootstrap parser consumes unsupported typeof in type position without preserving it.
        AssertParity("type T = typeof X;", roundTrip: false);
    }

    [TestMethod]
    public void Parser_AllRepositoryCaladesiFilesMatchBootstrap()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Caladesi.slnx")))
        {
            root = root.Parent;
        }

        Assert.IsNotNull(root);
        int count = 0;
        foreach (string folder in new[] { "samples", "src", "tests", "playground" })
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root.FullName, folder), "*.cala", SearchOption.AllDirectories))
            {
                if (path.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin"))
                {
                    continue;
                }

                AssertParity(File.ReadAllText(path), path);
                count++;
            }
        }

        Assert.IsGreaterThan(30, count);
    }

    [TestMethod]
    [SuppressMessage("Security", "CA5394", Justification = "Seeded deterministic parser fuzzing, not security-sensitive randomness.")]
    public void Parser_DeterministicMalformedInputsMatchBootstrap()
    {
        const string alphabet = "abcXYZ09_ $'\"`{}[]()<>/\\*+-.?=;:,&|!@\r\n\t\u2028α😀";
        Random random = new(0xCA1A);
        for (int sample = 0; sample < 2_500; sample++)
        {
            StringBuilder source = new();
            int length = random.Next(100);
            for (int i = 0; i < length; i++)
            {
                source.Append(alphabet[random.Next(alphabet.Length)]);
            }

            AssertParity(source.ToString());
        }
    }

    [TestMethod]
    public void Parser_NestingGuardAndUnwindingMatchBootstrap()
    {
        foreach (int depth in new[] { 120, 255, 256, 300, 600 })
        {
            AssertParity("let x = " + new string('(', depth) + "0" + new string(')', depth) + "; let tail = 1;");
            AssertParity(new string('{', depth) + ";" + new string('}', depth));
            AssertParity("type T = " + string.Concat(Enumerable.Repeat("A<", depth)) + "int" + new string('>', depth) + ";");
        }
    }

    [TestMethod]
    public void Parser_ExplicitTokenAndDiagnosticInputsMatchBootstrap()
    {
        const string source = "let x: = ; @";
        const string path = "supplied.cala";
        DiagnosticBag expectedDiagnostics = new();
        SelfHost::Caladesi.Compiler.Diagnostics.DiagnosticBag actualDiagnostics = new();
        Parser expected = new(path, new Lexer(SourceText.From(source), path, expectedDiagnostics).LexAll(), expectedDiagnostics);
        SelfHost::Caladesi.Compiler.Syntax.Parser actual = new(path,
            new SelfHost::Caladesi.Compiler.Syntax.Lexer(
                SelfHost::Caladesi.Compiler.Text.SourceText.From(source), path, actualDiagnostics).LexAll(), actualDiagnostics);

        AssertElements(expected.ParseCompilationUnit(), actual.ParseCompilationUnit(), source);
        Assert.AreEqual(expectedDiagnostics.Count, actual.Diagnostics.Count);
        Assert.IsTrue(ReferenceEquals(actualDiagnostics, actual.Diagnostics));
        for (int i = 0; i < expectedDiagnostics.Count; i++)
        {
            Assert.AreEqual(expectedDiagnostics.Items[i].Format(), actual.Diagnostics.Items[i].Format());
        }
    }

    private static void AssertParity(string source, string path = "parser.cala", bool roundTrip = true)
    {
        SyntaxTree expected = SyntaxTree.ParseText(source, path);
        SelfTree actual = SelfTree.ParseText(source, path);
        Assert.AreEqual(expected.Root.ToFullString(), actual.Root.ToFullString(), path);
        if (roundTrip)
        {
            Assert.AreEqual(source, actual.Root.ToFullString(), path);
        }
        AssertElements(expected.Root, actual.Root, path + ": " + source[..Math.Min(120, source.Length)]);
        Assert.HasCount(expected.Diagnostics.Length, actual.Diagnostics, source);
        for (int i = 0; i < expected.Diagnostics.Length; i++)
        {
            Diagnostic left = expected.Diagnostics[i];
            SelfHost::Caladesi.Compiler.Diagnostics.Diagnostic right = actual.Diagnostics[i];
            Assert.AreEqual(left.Id, right.Id, source);
            Assert.AreEqual(left.Message, right.Message, source);
            Assert.AreEqual(left.FilePath, right.FilePath, source);
            Assert.AreEqual(left.Span.Start, right.Span.Start, source);
            Assert.AreEqual(left.Span.Length, right.Span.Length, source);
            Assert.AreEqual((int)left.EffectiveSeverity, (int)right.EffectiveSeverity, source);
        }
    }

    private static void AssertElements(SyntaxElement expected, SelfElement actual, string context)
    {
        Stack<(SyntaxElement Left, SelfElement Right)> pending = new();
        pending.Push((expected, actual));
        while (pending.TryPop(out (SyntaxElement Left, SelfElement Right) pair))
        {
            (SyntaxElement left, SelfElement right) = pair;
            string location = $"{context}, {left.Kind} at {left.FullStart}";
            Assert.AreEqual(left.GetType().Name, right.GetType().Name, location);
            Assert.AreEqual((int)left.Kind, (int)right.Kind, location);
            Assert.AreEqual(left.FullStart, right.FullStart, location);
            Assert.AreEqual(left.FullWidth, right.FullWidth, location);
            Assert.AreEqual(left.FullEnd, right.FullEnd, location);
            Assert.AreEqual(left.Start, right.Start, location);
            Assert.AreEqual(left.Width, right.Width, location);
            Assert.AreEqual(left.End, right.End, location);
            Assert.AreEqual(left.Span.Start, right.Span.Start, location);
            Assert.AreEqual(left.Span.Length, right.Span.Length, location);
            Assert.AreEqual(left.IsMissing, right.IsMissing, location);
            if (left is SyntaxToken token)
            {
                SelfToken selfToken = (SelfToken)right;
                Assert.AreEqual(token.Text, selfToken.Text, location);
                Assert.AreEqual(token.Value, selfToken.Value, location);
                Assert.AreEqual(token.Value?.GetType(), selfToken.Value?.GetType(), location);
                Assert.AreEqual(token.ValueText, selfToken.ValueText, location);
                Assert.AreEqual(token.ToFullString(), selfToken.ToFullString(), location);
                Assert.HasCount(token.LeadingTrivia.Length, selfToken.LeadingTrivia, location);
                Assert.HasCount(token.TrailingTrivia.Length, selfToken.TrailingTrivia, location);
                for (int i = 0; i < token.LeadingTrivia.Length; i++)
                {
                    Assert.AreEqual((int)token.LeadingTrivia[i].Kind, (int)selfToken.LeadingTrivia[i].Kind, location);
                    Assert.AreEqual(token.LeadingTrivia[i].Text, selfToken.LeadingTrivia[i].Text, location);
                }

                for (int i = 0; i < token.TrailingTrivia.Length; i++)
                {
                    Assert.AreEqual((int)token.TrailingTrivia[i].Kind, (int)selfToken.TrailingTrivia[i].Kind, location);
                    Assert.AreEqual(token.TrailingTrivia[i].Text, selfToken.TrailingTrivia[i].Text, location);
                }
            }
            else
            {
                SyntaxNode node = (SyntaxNode)left;
                SelfNode selfNode = (SelfNode)right;
                Assert.AreEqual(node.SlotCount, selfNode.SlotCount, location);
                for (int i = 0; i < node.SlotCount; i++)
                {
                    SyntaxElement? child = node.Slot(i);
                    SelfElement? selfChild = selfNode.Slot(i);
                    Assert.AreEqual(child == null, selfChild == null, location);
                    if (child != null && selfChild != null)
                    {
                        Assert.AreSame(node, child.Parent, location);
                        Assert.IsTrue(ReferenceEquals(selfNode, selfChild.Parent), location);
                        pending.Push((child, selfChild));
                    }
                }
            }
        }
    }
}
