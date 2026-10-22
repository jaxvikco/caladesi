using Caladesi.Compiler.Formatting;
using Caladesi.Compiler.Semantics;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Tests.Semantics;

[TestClass]
public class SemanticModelTests
{
    private static (SemanticModel Model, string Text) Model(string marked, out int position)
    {
        position = marked.IndexOf('|');
        var text = marked.Remove(position, 1);
        var comp = TestCompiler.CreateWithSemantics(text);
        return (comp.GetSemanticModel(comp.SyntaxTrees[0]), text);
    }

    [TestMethod]
    public void HoverOnFunctionCallShowsSignature()
    {
        var (m, _) = Model("function add(a: int, b: int): int { return a + b; }\nconst r = ad|d(1, 2);\nConsole.WriteLine(r);", out var pos);
        Assert.AreEqual("function add(a: int, b: int): int", m.GetHover(pos));
    }

    [TestMethod]
    public void HoverOnLocalAndParameter()
    {
        var (m, text) = Model("function f(count: int): void {\n  const total = count * 2;\n  Console.WriteLine(tot|al);\n}\nf(1);", out var pos);
        Assert.AreEqual("(constant) total: int", m.GetHover(pos));
        Assert.AreEqual("(parameter) count: int", m.GetHover(text.IndexOf("count * 2", StringComparison.Ordinal) + 2));
    }

    [TestMethod]
    public void DefinitionAndReferences()
    {
        var (m, text) = Model("function square(x: int): int { return x * x; }\nconst a = squ|are(2);\nconst b = square(3);\nConsole.WriteLine(a + b);", out var pos);
        var at = m.GetSymbolAt(pos)!;
        var def = m.GetDefinition(at.Symbol)!.Value;
        Assert.AreEqual(text.IndexOf("square", StringComparison.Ordinal), def.Span.Start);
        var refs = m.FindReferences(at.Symbol);
        Assert.HasCount(3, refs);
        Assert.IsTrue(refs.Any(r => r.IsDeclaration));
    }

    [TestMethod]
    public void MemberCompletionOnString()
    {
        var (m, _) = Model("const s = \"hi\";\nConsole.WriteLine(s.|);", out var pos);
        var labels = m.GetCompletions(pos).Select(c => c.Label).ToList();
        CollectionAssert.Contains(labels, "Length");
        CollectionAssert.Contains(labels, "ToUpper");
        CollectionAssert.DoesNotContain(labels, "let");
    }

    [TestMethod]
    public void MemberCompletionOnClassInstanceAndStatic()
    {
        var (m, _) = Model("class Dog { name = \"d\"; static count = 0; bark(): string { return \"woof\"; } }\nconst d = new Dog();\nd.|", out var pos);
        var labels = m.GetCompletions(pos).Select(c => c.Label).ToList();
        CollectionAssert.Contains(labels, "name");
        CollectionAssert.Contains(labels, "bark");
        CollectionAssert.DoesNotContain(labels, "count");
        var (m2, _) = Model("class Dog { name = \"d\"; static count = 0; }\nDog.|", out var pos2);
        var staticLabels = m2.GetCompletions(pos2).Select(c => c.Label).ToList();
        CollectionAssert.Contains(staticLabels, "count");
        CollectionAssert.DoesNotContain(staticLabels, "name");
    }

    [TestMethod]
    public void NamespaceCompletion()
    {
        var (m, _) = Model("import * as IO from \"System.IO\";\nIO.|", out var pos);
        var labels = m.GetCompletions(pos).Select(c => c.Label).ToList();
        CollectionAssert.Contains(labels, "Path");
        CollectionAssert.Contains(labels, "File");
    }

    [TestMethod]
    public void ExtensionMethodsAppearInCompletion()
    {
        var (m, _) = Model("const xs = [1, 2, 3];\nxs.|", out var pos);
        CollectionAssert.Contains(m.GetCompletions(pos).Select(c => c.Label).ToList(), "Select");
    }

    [TestMethod]
    public void ScopeCompletionSeesLocalsAndKeywords()
    {
        var (m, _) = Model("function f(alpha: int): void {\n  const beta = 1;\n  al|\n}", out var pos);
        var labels = m.GetCompletions(pos).Select(c => c.Label).ToList();
        CollectionAssert.Contains(labels, "alpha");
        CollectionAssert.Contains(labels, "beta");
        CollectionAssert.Contains(labels, "function");
        CollectionAssert.Contains(labels, "Console");
    }

    [TestMethod]
    public void SignatureHelpTracksActiveParameter()
    {
        var (m, _) = Model("function add(a: int, b: int): int { return a + b; }\nadd(1, |);", out var pos);
        var help = m.GetSignatureHelp(pos)!;
        Assert.AreEqual(1, help.ActiveParameter);
        Assert.AreEqual("add(a: int, b: int): int", help.Signatures[0].Label);
    }

    [TestMethod]
    public void DocumentSymbolsOutline()
    {
        var comp = TestCompiler.CreateWithSemantics("class A { x = 1; m(): void {} }\nfunction f() {}\nconst c = 1;\nenum E { P, Q }\ntype T = { a: int };");
        var symbols = comp.GetSemanticModel(comp.SyntaxTrees[0]).GetDocumentSymbols();
        CollectionAssert.AreEqual(new[] { "A", "f", "c", "E", "T" }, symbols.Select(s => s.Name).ToList());
        CollectionAssert.AreEqual(new[] { "x", "m" }, symbols[0].Children.Select(s => s.Name).ToList());
        CollectionAssert.AreEqual(new[] { "P", "Q" }, symbols[3].Children.Select(s => s.Name).ToList());
    }

    [TestMethod]
    public void SemanticTokensClassifyNames()
    {
        var comp = TestCompiler.CreateWithSemantics("class A { m(): void {} }\nfunction f(p: int) { const v = p; new A().m(); }\nf(1);");
        var tokens = comp.GetSemanticModel(comp.SyntaxTrees[0]).GetSemanticTokens();
        var kinds = tokens.Select(t => t.Type).Distinct().ToList();
        CollectionAssert.IsSubsetOf(new[] { SemanticTokenType.Class, SemanticTokenType.Method, SemanticTokenType.Function, SemanticTokenType.Parameter, SemanticTokenType.Variable }, kinds);
    }

    [TestMethod]
    public void FormatterReindentsAndIsIdempotent()
    {
        var source = "function f(a: int): int {\nif (a > 0) {\n      return a;   \n}\n\n\n\nreturn -a;\n}\nconst t = `x\n  y`;";
        var tree = Caladesi.Compiler.Syntax.SyntaxTree.ParseText(source);
        var once = Formatter.Format(tree);
        Assert.AreEqual("function f(a: int): int {\n  if (a > 0) {\n    return a;\n  }\n\n  return -a;\n}\nconst t = `x\n  y`;\n", once);
        Assert.AreEqual(once, Formatter.Format(Caladesi.Compiler.Syntax.SyntaxTree.ParseText(once)));
    }
}
