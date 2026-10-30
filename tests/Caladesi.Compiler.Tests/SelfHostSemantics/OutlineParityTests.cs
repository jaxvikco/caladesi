extern alias SelfHost;

using System.Diagnostics.CodeAnalysis;
using Caladesi.Compiler.Semantics;
using Caladesi.Compiler.Syntax;
using SelfOutline = SelfHost::Caladesi.Compiler.Semantics.DocumentOutline;
using SelfSymbol = SelfHost::Caladesi.Compiler.Semantics.DocumentSymbolInfo;
using SelfTree = SelfHost::Caladesi.Compiler.Syntax.SyntaxTree;

namespace Caladesi.Compiler.Tests.SelfHostSemantics;

[TestClass]
public sealed class OutlineParityTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("function f(x: int, y: string): bool { return true; } const x = 1, y = 2; let z: string;")]
    [DataRow("export class C { constructor(x: int) {} value: string; get Name(): string { return \"\"; } set Name(value: string) {} M(x: int): bool { return true; } }")]
    [DataRow("interface I { Value: string; M(x: int): void; } enum E { A, B = 2 } type Alias = string; type Shape = { x: int; M(): string; };")]
    [DataRow("function () {} class {} enum { A } const ; type = ;")]
    [DataRow("const {x, y} = value; const [a, b] = pair;")]
    public void Outline_DeclarationsAndRecoveryMatchBootstrap(string source)
    {
        AssertSymbols(DocumentOutline.Compute(SyntaxTree.ParseText(source)), SelfOutline.Compute(SelfTree.ParseText(source)));
    }

    [TestMethod]
    public void Outline_RepositorySourcesMatchBootstrap()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Caladesi.slnx")))
        {
            root = root.Parent;
        }

        Assert.IsNotNull(root);
        foreach (var folder in new[] { "samples", "src", "tests", "playground" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root.FullName, folder), "*.cala", SearchOption.AllDirectories))
            {
                if (!path.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin"))
                {
                    var source = File.ReadAllText(path);
                    var expected = SyntaxTree.ParseText(source);
                    var actual = SelfTree.ParseText(source);
                    AssertSymbols(DocumentOutline.Compute(expected), SelfOutline.Compute(actual));
                }
            }
        }
    }

    [TestMethod]
    public void Outline_NullTreeRejected()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => SelfOutline.Compute(null!));
    }

    [TestMethod]
    public void SemanticData_OptionalCompletionAndValueTokensPreserveContract()
    {
        var first = new SelfHost::Caladesi.Compiler.Semantics.CompletionEntry("name", SelfHost::Caladesi.Compiler.Semantics.CompletionKind.Variable, null);
        var second = new SelfHost::Caladesi.Compiler.Semantics.CompletionEntry("name", SelfHost::Caladesi.Compiler.Semantics.CompletionKind.Variable, null);
        Assert.IsNull(first.InsertText);
        Assert.AreEqual(first, second);
        var span = new SelfHost::Caladesi.Compiler.Text.TextSpan(2, 4);
        var modifiers = SelfHost::Caladesi.Compiler.Semantics.SemanticTokenModifiers.Declaration | SelfHost::Caladesi.Compiler.Semantics.SemanticTokenModifiers.Static;
        var token = new SelfHost::Caladesi.Compiler.Semantics.SemanticTokenInfo(span, SelfHost::Caladesi.Compiler.Semantics.SemanticTokenType.Function, modifiers);
        Assert.AreEqual(token, new SelfHost::Caladesi.Compiler.Semantics.SemanticTokenInfo(span, SelfHost::Caladesi.Compiler.Semantics.SemanticTokenType.Function, modifiers));
        Assert.AreEqual("Declaration, Static", modifiers.ToString());
    }

    [TestMethod]
    [SuppressMessage("Security", "CA5394", Justification = "Deterministic malformed source generation, not security-sensitive randomness.")]
    public void Outline_MalformedFuzzMatchesBootstrap()
    {
        const string alphabet = "ab09 \t\r\n{}[]()'\"`/$\\*?;:";
        Random random = new(0x5EAA);
        for (var sample = 0; sample < 500; sample++)
        {
            string source = new([.. Enumerable.Range(0, random.Next(100)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            var expected = SyntaxTree.ParseText(source);
            var actual = SelfTree.ParseText(source);
            AssertSymbols(DocumentOutline.Compute(expected), SelfOutline.Compute(actual));
        }
    }

    private static void AssertSymbols(IReadOnlyList<DocumentSymbolInfo> expected, IReadOnlyList<SelfSymbol> actual)
    {
        Assert.HasCount(expected.Count, actual);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].Name, actual[i].Name);
            Assert.AreEqual(expected[i].Detail, actual[i].Detail);
            Assert.AreEqual((int)expected[i].Kind, (int)actual[i].Kind);
            Assert.AreEqual(expected[i].Span.Start, actual[i].Span.Start);
            Assert.AreEqual(expected[i].Span.Length, actual[i].Span.Length);
            Assert.AreEqual(expected[i].SelectionSpan.Start, actual[i].SelectionSpan.Start);
            Assert.AreEqual(expected[i].SelectionSpan.Length, actual[i].SelectionSpan.Length);
            AssertSymbols(expected[i].Children, actual[i].Children);
        }
    }
}
