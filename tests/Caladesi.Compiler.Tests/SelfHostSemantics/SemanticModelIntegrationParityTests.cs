extern alias SelfHost;

using Caladesi.Compiler.Semantics;
using Caladesi.Compiler.Syntax;
using Native = SelfHost::Caladesi.Compiler;
using NativeSemantics = SelfHost::Caladesi.Compiler.Semantics;
using NativeSyntax = SelfHost::Caladesi.Compiler.Syntax;
using NativeMetadata = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using NativeSymbols = SelfHost::Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Tests.SelfHostSemantics;

[TestClass]
public sealed class SemanticModelIntegrationParityTests
{
    private static readonly Lazy<NativeMetadata.ReferenceSet> References =
        new(() => NativeMetadata.ReferenceSet.Create(TestCompiler.References.Paths));

    [TestMethod]
    [DataRow("function square(x: int): int { return x * x; } const a = square(2); const b = square(3);")]
    [DataRow("class C<T> { value: T; get name(): string { return \"c\"; } id<U>(value: U): U { return value; } } const c = new C<int>(); const n = c.name; c.id<int>(1);")]
    [DataRow("interface I { run(): int; } class C implements I { run(): int { return 1; } } const c = new C(); const n = c.run();")]
    [DataRow("type NumberAlias = int; type Point = { x: int; y: int; }; enum Color { Red, Blue } const c = Color.Red;")]
    [DataRow("function f(count: int): void { const total = count * 2; { let hidden = total; } const result = total; }")]
    [DataRow("class C { value: int = 0; } const c = new C(); c.value = c.value + 1;")]
    public void RecordedQueriesReferencesAndSemanticTokensMatchBootstrap(string text)
    {
        var (expected, actual) = Models(text);
        for (int position = 0; position <= text.Length; position++)
        {
            Assert.AreEqual(expected.GetHover(position), actual.GetHover(position), $"Hover at {position}");
            var left = expected.GetSymbolAt(position);
            var right = actual.GetSymbolAt(position);
            Assert.AreEqual(left == null, right == null, $"Symbol presence at {position}");
            Assert.AreEqual(expected.GetExpressionTypeAt(position)?.ToDisplayString(),
                actual.GetExpressionTypeAt(position)?.ToDisplayString(), $"Expression type at {position}");
            if (left == null || right == null) continue;
            Assert.AreEqual(left.Symbol.Name, right.Symbol.Name);
            Assert.AreEqual(left.Symbol.Kind.ToString(), right.Symbol.Kind.ToString());
            Assert.AreEqual(left.IsDeclaration, right.IsDeclaration);
            Assert.AreEqual((left.Span.Start, left.Span.Length), (right.Span.Start, right.Span.Length));
            var leftDefinition = expected.GetDefinition(left.Symbol);
            var rightDefinition = actual.GetDefinition(right.Symbol);
            Assert.AreEqual(leftDefinition?.Tree.FilePath, rightDefinition?.Tree.FilePath);
            Assert.AreEqual(leftDefinition?.Span.Start, rightDefinition?.Span.Start);
            CollectionAssert.AreEqual(
                expected.FindReferences(left.Symbol).Select(r => (r.Tree.FilePath, r.Span.Start, r.Span.Length, r.IsDeclaration)).ToArray(),
                actual.FindReferences(right.Symbol).Select(r => (r.Tree.FilePath, r.Span.Start, r.Span.Length, r.IsDeclaration)).ToArray());
            CollectionAssert.AreEqual(
                expected.FindReferences(left.Symbol, false).Select(r => (r.Tree.FilePath, r.Span.Start, r.Span.Length, r.IsDeclaration)).ToArray(),
                actual.FindReferences(right.Symbol, false).Select(r => (r.Tree.FilePath, r.Span.Start, r.Span.Length, r.IsDeclaration)).ToArray());
        }
        CollectionAssert.AreEqual(
            expected.GetSemanticTokens().Select(t => (t.Span.Start, t.Span.Length, (int)t.Type, (int)t.Modifiers)).ToArray(),
            actual.GetSemanticTokens().Select(t => (t.Span.Start, t.Span.Length, (int)t.Type, (int)t.Modifiers)).ToArray());
    }

    [TestMethod]
    [DataRow("const s = \"hi\"; s.|")]
    [DataRow("const xs = [1, 2, 3]; xs.|")]
    [DataRow("class Dog { name = \"d\"; static count = 0; bark(): string { return \"woof\"; } } const d = new Dog(); d.|")]
    [DataRow("class Dog { name = \"d\"; static count = 0; } Dog.|")]
    [DataRow("interface Base { baseMember(): void; } interface Derived extends Base { derivedMember(): void; } function use(value: Derived): void { value.| }")]
    [DataRow("interface HasValue { getValue(): int; } function use<T extends HasValue>(value: T): int { return value.| }")]
    [DataRow("function use(value: int): void { const local = value; { const hidden = 1; } loc|al; }")]
    [DataRow("import * as IO from \"System.IO\"; IO.|")]
    [DataRow("enum Color { Red, Blue } Color.|")]
    public void LexicalMemberNamespaceAndExtensionCompletionsMatchBootstrap(string marked)
    {
        int position = marked.IndexOf('|');
        var (expected, actual) = Models(marked.Remove(position, 1));
        CollectionAssert.AreEqual(
            expected.GetCompletions(position).OrderBy(c => c.Label, StringComparer.Ordinal)
                .Select(c => (c.Label, (int)c.Kind, c.Detail, c.InsertText)).ToArray(),
            actual.GetCompletions(position).OrderBy(c => c.Label, StringComparer.Ordinal)
                .Select(c => (c.Label, (int)c.Kind, c.Detail, c.InsertText)).ToArray());
    }

    [TestMethod]
    [DataRow("function f(x: int, y: string): void {} f(1, |\"x\");")]
    [DataRow("class C { constructor(x: int) {} } const c = new C(|1);")]
    [DataRow("class C { run(x: int): int { return x; } run(x: int, y: int): int { return x + y; } } const c = new C(); c.run(1, |2);")]
    [DataRow("function f(...values: int[]): void {} f(1, 2, |3);")]
    [DataRow("function f(value: int = 1): void {} f(|);")]
    [DataRow("function f(value: int): int { return value; } f(f(|1));")]
    [DataRow("function f(value: int): int { return value; } f(1)|;")]
    public void SignatureHelpOverloadsConstructorsOptionalAndRestParametersMatchBootstrap(string marked)
    {
        int position = marked.IndexOf('|');
        var (expected, actual) = Models(marked.Remove(position, 1));
        var left = expected.GetSignatureHelp(position);
        var right = actual.GetSignatureHelp(position);
        Assert.AreEqual(left == null, right == null);
        if (left == null || right == null) return;
        Assert.AreEqual(left.ActiveParameter, right.ActiveParameter);
        Assert.AreEqual(left.ActiveSignature, right.ActiveSignature);
        CollectionAssert.AreEqual(left.Signatures.Select(s => s.Label).ToArray(), right.Signatures.Select(s => s.Label).ToArray());
        for (int i = 0; i < left.Signatures.Count; i++)
            CollectionAssert.AreEqual(left.Signatures[i].Parameters.ToArray(), right.Signatures[i].Parameters.ToArray());
    }

    [TestMethod]
    public void AccessorReferencesCanonicalizeToTheirProperty()
    {
        const string text = "class C { get value(): int { return 1; } } const c = new C(); const n = c.value;";
        var (expected, actual) = Models(text);
        int position = text.LastIndexOf("value", StringComparison.Ordinal);
        var left = (Caladesi.Compiler.Symbols.PropertySymbol)expected.GetSymbolAt(position)!.Symbol;
        var right = (NativeSymbols.PropertySymbol)actual.GetSymbolAt(position)!.Symbol;
        CollectionAssert.AreEqual(
            expected.FindReferences(left.GetMethod!).Select(r => (r.Tree.FilePath, r.Span.Start, r.IsDeclaration)).ToArray(),
            actual.FindReferences(right.GetMethod!).Select(r => (r.Tree.FilePath, r.Span.Start, r.IsDeclaration)).ToArray());
        Assert.HasCount(expected.FindReferences(left).Count, actual.FindReferences(right.GetMethod!));
    }

    [TestMethod]
    public void FileModuleExportsImportsAndCrossFileReferenceOrderMatchBootstrap()
    {
        const string text = "import { twice } from \"./lib\"; import * as L from \"./lib\"; const value = twice(21); L.";
        const string library = "export function twice(x: int): int { return x * 2; } function hidden(): void {}";
        var (expected, actual) = Models(text, library);
        int position = text.IndexOf("twice(21)", StringComparison.Ordinal);
        var left = expected.GetSymbolAt(position)!.Symbol;
        var right = actual.GetSymbolAt(position)!.Symbol;
        CollectionAssert.AreEqual(
            expected.FindReferences(left).Select(r => (r.Tree.FilePath, r.Span.Start, r.Span.Length, r.IsDeclaration)).ToArray(),
            actual.FindReferences(right).Select(r => (r.Tree.FilePath, r.Span.Start, r.Span.Length, r.IsDeclaration)).ToArray());
        CollectionAssert.AreEqual(
            expected.GetCompletions(text.Length).Select(c => (c.Label, (int)c.Kind, c.Detail)).ToArray(),
            actual.GetCompletions(text.Length).Select(c => (c.Label, (int)c.Kind, c.Detail)).ToArray());
    }

    private static (SemanticModel Expected, NativeSemantics.SemanticModel<Native.SourceFile> Actual) Models(
        string text, string? library = null)
    {
        var originalTrees = new List<SyntaxTree> { SyntaxTree.ParseText(text, "/semantic/main.cala") };
        var nativeTrees = new List<NativeSyntax.SyntaxTree> { NativeSyntax.SyntaxTree.ParseText(text, "/semantic/main.cala") };
        if (library != null)
        {
            originalTrees.Add(SyntaxTree.ParseText(library, "/semantic/lib.cala"));
            nativeTrees.Add(NativeSyntax.SyntaxTree.ParseText(library, "/semantic/lib.cala"));
        }
        var original = Compilation.Create(new CompilationOptions
        {
            RecordSemanticInfo = true, OutputKind = OutputKind.DynamicallyLinkedLibrary,
            AssemblyName = "SemanticIntegration", ProjectDirectory = "/semantic",
        }, originalTrees, TestCompiler.References);
        var native = Native.Compilation.Create(new Native.CompilationOptions
        {
            RecordSemanticInfo = true, OutputKind = Native.OutputKind.DynamicallyLinkedLibrary,
            AssemblyName = "SemanticIntegration", ProjectDirectory = "/semantic",
        }, nativeTrees, References.Value);
        return (original.GetSemanticModel(originalTrees[0]), native.GetSemanticModel(nativeTrees[0]));
    }
}
