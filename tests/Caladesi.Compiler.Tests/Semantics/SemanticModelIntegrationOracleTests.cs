using Caladesi.Compiler.Semantics;

namespace Caladesi.Compiler.Tests.Semantics;

[TestClass]
public sealed class SemanticModelIntegrationOracleTests
{
    [TestMethod]
    public void FunctionHoverDefinitionReferencesTokensAndSignatureShareRecordedSymbols()
    {
        const string text = "function square(x: int): int { return x * x; }\nconst a = square(2);\nconst b = square(3);";
        Compilation compilation = TestCompiler.CreateWithSemantics(text);
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        int position = text.IndexOf("square(2)", StringComparison.Ordinal) + 2;
        Assert.AreEqual("function square(x: int): int", model.GetHover(position));
        SymbolAt at = model.GetSymbolAt(position)!;
        Assert.IsFalse(at.IsDeclaration);
        SymbolReference definition = model.GetDefinition(at.Symbol)!.Value;
        Assert.AreEqual(text.IndexOf("square", StringComparison.Ordinal), definition.Span.Start);
        Assert.IsTrue(definition.IsDeclaration);
        Assert.HasCount(3, model.FindReferences(at.Symbol));
        Assert.IsTrue(model.FindReferences(at.Symbol)[0].IsDeclaration);
        Assert.HasCount(2, model.FindReferences(at.Symbol, false));
        Assert.IsNotNull(model.GetSymbolAt(text.IndexOf("square", StringComparison.Ordinal) + "square".Length));
        Assert.AreEqual(3, model.GetSemanticTokens().Count(t => t.Type == SemanticTokenType.Function));
        Assert.IsGreaterThanOrEqualTo(4, model.GetSemanticTokens().Count(t => (t.Modifiers & SemanticTokenModifiers.Declaration) != 0));
        SignatureHelpInfo help = model.GetSignatureHelp(text.IndexOf("square(2)", StringComparison.Ordinal) + "square(".Length)!;
        Assert.HasCount(1, help.Signatures);
        Assert.AreEqual(0, help.ActiveParameter);
        Assert.AreEqual("square(x: int): int", help.Signatures[0].Label);
        Assert.IsNull(model.GetSignatureHelp(text.IndexOf("square(2)", StringComparison.Ordinal) + "square(2)".Length));
    }

    [TestMethod]
    public void CompletionsRespectLexicalScopeAndStaticReceiver()
    {
        const string text = "class Dog { name: string = \"d\"; static count: int = 0; bark(): string { return \"woof\"; } }\n" +
            "function f(value: int): void { const local = value; { const hidden = 1; } local; }\nconst d = new Dog();\nd.;\nDog.";
        Compilation compilation = TestCompiler.CreateWithSemantics(text);
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        string[] instance = [.. model.GetCompletions(text.IndexOf("\nd.", StringComparison.Ordinal) + 3).Select(e => e.Label)];
        CollectionAssert.Contains(instance, "name");
        CollectionAssert.Contains(instance, "bark");
        CollectionAssert.DoesNotContain(instance, "count");
        CollectionAssert.DoesNotContain(instance, "let");
        string[] statics = [.. model.GetCompletions(text.Length).Select(e => e.Label)];
        CollectionAssert.Contains(statics, "count");
        CollectionAssert.DoesNotContain(statics, "name");
        string[] locals = [.. model.GetCompletions(text.IndexOf("local; }", StringComparison.Ordinal) + 2).Select(e => e.Label)];
        CollectionAssert.Contains(locals, "local");
        CollectionAssert.Contains(locals, "value");
        CollectionAssert.DoesNotContain(locals, "hidden");
        CollectionAssert.Contains(locals, "return");
        Assert.IsNotEmpty(model.GetCompletions(-10));
    }

    [TestMethod]
    public void SourceModuleReferencesIncludeImportsAndCompletionFiltersPrivateExports()
    {
        const string text = "import { twice } from \"./lib\";\nimport * as L from \"./lib\";\nconst result = twice(21);\nL.";
        Compilation compilation = TestCompiler.CreateWithSemantics(text, "/semantic/main.cala",
            ("/semantic/lib.cala", "export function twice(x: int): int { return x * 2; }\nfunction hidden(): void {}"));
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        SymbolAt at = model.GetSymbolAt(text.IndexOf("twice(21)", StringComparison.Ordinal) + 1)!;
        Assert.HasCount(3, model.FindReferences(at.Symbol));
        Assert.AreEqual("/semantic/lib.cala", model.GetDefinition(at.Symbol)!.Value.Tree.FilePath);
        string[] labels = [.. model.GetCompletions(text.Length).Select(e => e.Label)];
        CollectionAssert.Contains(labels, "twice");
        CollectionAssert.DoesNotContain(labels, "hidden");
        CollectionAssert.DoesNotContain(labels, "function");
    }

    [TestMethod]
    public void OverloadSignatureHelpOrdersByArityAndCountsSeparators()
    {
        const string text = "class C { run(x: int): int { return x; } run(x: int, y: int): int { return x + y; } }\nconst c = new C();\nc.run(1, 2);";
        Compilation compilation = TestCompiler.CreateWithSemantics(text);
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        SignatureHelpInfo help = model.GetSignatureHelp(text.LastIndexOf(", 2", StringComparison.Ordinal) + 2)!;
        Assert.HasCount(2, help.Signatures);
        Assert.AreEqual(1, help.ActiveParameter);
        Assert.AreEqual(1, help.ActiveSignature);
        Assert.HasCount(1, help.Signatures[0].Parameters);
        Assert.HasCount(2, help.Signatures[1].Parameters);
    }

    [TestMethod]
    public void ReadonlyPropertyGenericMethodAndAliasFormatting()
    {
        const string text = "type NumberAlias = int;\nclass C { get value(): int { return 1; } identity<T>(value: T): T { return value; } }\n" +
            "const c = new C();\nconst n = c.value;\nc.identity<int>(n);";
        Compilation compilation = TestCompiler.CreateWithSemantics(text);
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        Assert.AreEqual("type NumberAlias = int", model.GetHover(text.IndexOf("NumberAlias", StringComparison.Ordinal) + 1));
        Assert.AreEqual("(property) C.value: int (readonly)", model.GetHover(text.IndexOf("c.value", StringComparison.Ordinal) + 3));
        SymbolAt method = model.GetSymbolAt(text.IndexOf("c.identity", StringComparison.Ordinal) + 3)!;
        Assert.AreEqual("(method) C.identity<T>(value: T): T", SymbolDisplay.Describe(method.Symbol));
        Assert.HasCount(4, model.GetDocumentSymbols());
    }

    [TestMethod]
    public void SemanticModelRequiresRecordingOption()
    {
        Compilation compilation = TestCompiler.Create("const x = 1;");
        Assert.ThrowsExactly<ArgumentException>(() => compilation.GetSemanticModel(compilation.SyntaxTrees[0]));
    }
}
