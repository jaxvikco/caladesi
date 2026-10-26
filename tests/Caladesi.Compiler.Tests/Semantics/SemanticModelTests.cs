using Caladesi.Compiler.Formatting;
using Caladesi.Compiler.Semantics;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.Semantics;

[TestClass]
public class SemanticModelTests
{
    private static readonly string[] s_outlineTopLevel = ["A", "f", "c", "E", "T"];
    private static readonly string[] s_outlineClassMembers = ["x", "m"];
    private static readonly string[] s_outlineEnumMembers = ["P", "Q"];

    private static (SemanticModel Model, string Text) Model(string marked, out int position)
    {
        position = marked.IndexOf('|');
        string text = marked.Remove(position, 1);
        Compilation comp = TestCompiler.CreateWithSemantics(text);
        return (comp.GetSemanticModel(comp.SyntaxTrees[0]), text);
    }

    [TestMethod]
    public void HoverOnFunctionCallShowsSignature()
    {
        (SemanticModel? m, string _) = Model("function add(a: int, b: int): int { return a + b; }\nconst r = ad|d(1, 2);\nConsole.WriteLine(r);", out int pos);
        Assert.AreEqual("function add(a: int, b: int): int", m.GetHover(pos));
    }

    [TestMethod]
    public void HoverOnLocalAndParameter()
    {
        (SemanticModel? m, string? text) = Model("function f(count: int): void {\n  const total = count * 2;\n  Console.WriteLine(tot|al);\n}\nf(1);", out int pos);
        Assert.AreEqual("(constant) total: int", m.GetHover(pos));
        Assert.AreEqual("(parameter) count: int", m.GetHover(text.IndexOf("count * 2", StringComparison.Ordinal) + 2));
    }

    [TestMethod]
    public void DefinitionAndReferences()
    {
        (SemanticModel? m, string? text) = Model("function square(x: int): int { return x * x; }\nconst a = squ|are(2);\nconst b = square(3);\nConsole.WriteLine(a + b);", out int pos);
        SymbolAt at = m.GetSymbolAt(pos)!;
        SymbolReference def = m.GetDefinition(at.Symbol)!.Value;
        Assert.AreEqual(text.IndexOf("square", StringComparison.Ordinal), def.Span.Start);
        IReadOnlyList<SymbolReference> refs = m.FindReferences(at.Symbol);
        Assert.HasCount(3, refs);
        Assert.IsTrue(refs.Any(r => r.IsDeclaration));
    }

    [TestMethod]
    public void ReferencesIncludeImportSpecifiers()
    {
        Compilation comp = TestCompiler.CreateWithSemantics(
            "import { twice } from \"./lib\";\nConsole.WriteLine(twice(21));", "/proj/main.cala",
            ("/proj/lib.cala", "export function twice(x: int): int { return x * 2; }"));
        SemanticModel m = comp.GetSemanticModel(comp.SyntaxTrees[0]);
        string text = comp.SyntaxTrees[0].Root.ToFullString();
        SymbolAt at = m!.GetSymbolAt(text.LastIndexOf("twice", StringComparison.Ordinal) + 1)!;
        IReadOnlyList<SymbolReference> refs = m!.FindReferences(at.Symbol);
        Assert.HasCount(3, refs);
        Assert.IsTrue(refs.Any(r => r.Span.Start == text.IndexOf("twice", StringComparison.Ordinal) && !r.IsDeclaration));
    }

    [TestMethod]
    public void MemberCompletionOnString()
    {
        (SemanticModel? m, string _) = Model("const s = \"hi\";\nConsole.WriteLine(s.|);", out int pos);
        List<string> labels = [.. m.GetCompletions(pos).Select(c => c.Label)];
        CollectionAssert.Contains(labels, "Length");
        CollectionAssert.Contains(labels, "ToUpper");
        CollectionAssert.DoesNotContain(labels, "let");
    }

    [TestMethod]
    public void MemberCompletionOnClassInstanceAndStatic()
    {
        (SemanticModel? m, string _) = Model("class Dog { name = \"d\"; static count = 0; bark(): string { return \"woof\"; } }\nconst d = new Dog();\nd.|", out int pos);
        List<string> labels = [.. m.GetCompletions(pos).Select(c => c.Label)];
        CollectionAssert.Contains(labels, "name");
        CollectionAssert.Contains(labels, "bark");
        CollectionAssert.DoesNotContain(labels, "count");
        (SemanticModel? m2, string _) = Model("class Dog { name = \"d\"; static count = 0; }\nDog.|", out int pos2);
        List<string> staticLabels = [.. m2.GetCompletions(pos2).Select(c => c.Label)];
        CollectionAssert.Contains(staticLabels, "count");
        CollectionAssert.DoesNotContain(staticLabels, "name");
    }

    [TestMethod]
    public void NamespaceCompletion()
    {
        (SemanticModel? m, string _) = Model("import * as IO from \"System.IO\";\nIO.|", out int pos);
        List<string> labels = [.. m.GetCompletions(pos).Select(c => c.Label)];
        CollectionAssert.Contains(labels, "Path");
        CollectionAssert.Contains(labels, "File");
    }

    [TestMethod]
    public void ExtensionMethodsAppearInCompletion()
    {
        (SemanticModel? m, string _) = Model("const xs = [1, 2, 3];\nxs.|", out int pos);
        CollectionAssert.Contains(m.GetCompletions(pos).Select(c => c.Label).ToList(), "Select");
    }

    [TestMethod]
    public void ArrayCompletionOmitsExplicitInterfaceMembers()
    {
        (SemanticModel? m, string _) = Model("const xs = [1, 2, 3];\nxs.|", out int pos);
        List<string> labels = [.. m.GetCompletions(pos).Select(c => c.Label)];
        CollectionAssert.Contains(labels, "Length");
        CollectionAssert.Contains(labels, "Select");
        CollectionAssert.DoesNotContain(labels, "Add");
        CollectionAssert.DoesNotContain(labels, "Clear");
        CollectionAssert.DoesNotContain(labels, "Remove");
    }

    [TestMethod]
    public void InterfaceCompletionIncludesInheritedInterfaceMembers()
    {
        (SemanticModel? m, string _) = Model("""
            interface Base { baseMember(): void; }
            interface Derived extends Base { derivedMember(): void; }
            function use(value: Derived): void { value.| }
            """, out int pos);
        List<string> labels = [.. m.GetCompletions(pos).Select(c => c.Label)];
        CollectionAssert.Contains(labels, "baseMember");
        CollectionAssert.Contains(labels, "derivedMember");
    }

    [TestMethod]
    public void TypeParameterCompletionIncludesConstraintMembers()
    {
        (SemanticModel? m, string _) = Model("""
            interface HasValue { getValue(): int; }
            function use<T extends HasValue>(value: T): int { return value.| }
            """, out int pos);
        CollectionAssert.Contains(m.GetCompletions(pos).Select(c => c.Label).ToList(), "getValue");
    }

    [TestMethod]
    public void ListCompletionIncludesAdd()
    {
        (SemanticModel? m, string _) = Model("const xs: List<int> = [1, 2, 3];\nxs.|", out int pos);
        CollectionAssert.Contains(m.GetCompletions(pos).Select(c => c.Label).ToList(), "Add");
    }

    [TestMethod]
    public void ScopeCompletionSeesLocalsAndKeywords()
    {
        (SemanticModel? m, string _) = Model("function f(alpha: int): void {\n  const beta = 1;\n  al|\n}", out int pos);
        List<string> labels = [.. m.GetCompletions(pos).Select(c => c.Label)];
        CollectionAssert.Contains(labels, "alpha");
        CollectionAssert.Contains(labels, "beta");
        CollectionAssert.Contains(labels, "function");
        CollectionAssert.Contains(labels, "Console");
    }

    [TestMethod]
    public void SignatureHelpTracksActiveParameter()
    {
        (SemanticModel? m, string _) = Model("function add(a: int, b: int): int { return a + b; }\nadd(1, |);", out int pos);
        SignatureHelpInfo help = m.GetSignatureHelp(pos)!;
        Assert.AreEqual(1, help.ActiveParameter);
        Assert.AreEqual("add(a: int, b: int): int", help.Signatures[0].Label);
    }

    [TestMethod]
    public void DocumentSymbolsOutline()
    {
        Compilation comp = TestCompiler.CreateWithSemantics("class A { x = 1; m(): void {} }\nfunction f() {}\nconst c = 1;\nenum E { P, Q }\ntype T = { a: int };");
        IReadOnlyList<DocumentSymbolInfo> symbols = comp.GetSemanticModel(comp.SyntaxTrees[0]).GetDocumentSymbols();
        CollectionAssert.AreEqual(s_outlineTopLevel, symbols.Select(s => s.Name).ToList());
        CollectionAssert.AreEqual(s_outlineClassMembers, symbols[0].Children.Select(s => s.Name).ToList());
        CollectionAssert.AreEqual(s_outlineEnumMembers, symbols[3].Children.Select(s => s.Name).ToList());
    }

    [TestMethod]
    public void SemanticTokensClassifyNames()
    {
        Compilation comp = TestCompiler.CreateWithSemantics("class A { m(): void {} }\nfunction f(p: int) { const v = p; new A().m(); }\nf(1);");
        IReadOnlyList<SemanticTokenInfo> tokens = comp.GetSemanticModel(comp.SyntaxTrees[0]).GetSemanticTokens();
        List<SemanticTokenType> kinds = [.. tokens.Select(t => t.Type).Distinct()];
        CollectionAssert.IsSubsetOf(new[] { SemanticTokenType.Class, SemanticTokenType.Method, SemanticTokenType.Function, SemanticTokenType.Parameter, SemanticTokenType.Variable }, kinds);
    }

    [TestMethod]
    public void FormatterReindentsAndIsIdempotent()
    {
        string source = "function f(a: int): int {\nif (a > 0) {\n      return a;   \n}\n\n\n\nreturn -a;\n}\nconst t = `x\n  y`;";
        SyntaxTree tree = SyntaxTree.ParseText(source);
        string once = Formatter.Format(tree);
        Assert.AreEqual("function f(a: int): int {\n  if (a > 0) {\n    return a;\n  }\n\n  return -a;\n}\nconst t = `x\n  y`;\n", once);
        Assert.AreEqual(once, Formatter.Format(SyntaxTree.ParseText(once)));
    }
}
