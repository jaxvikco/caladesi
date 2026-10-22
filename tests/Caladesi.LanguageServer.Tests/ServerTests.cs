using System.Text.Json;

namespace Caladesi.LanguageServer.Tests;

[TestClass]
public class ServerTests
{
    private const string Uri = "file:///virtual/main.cala";

    private static readonly string[] s_signatureTriggers = ["(", ","];
    private static readonly string[] s_tokenTypes = ["namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "function", "method", "decorator"];
    private static readonly string[] s_tokenModifiers = ["declaration", "readonly", "static"];
    private static readonly int[] s_modifierFlags = [1, 2, 4];
    private static readonly int[] s_referenceLines = [0, 1, 2];
    private static readonly string[] s_dogMembers = ["name", "bark"];
    private static readonly string[] s_declarationReadonly = ["declaration", "readonly"];

    private static async Task<TestClient> Started(string? text = null, string uri = Uri)
    {
        TestClient client = new();
        _ = await client.Initialize().ConfigureAwait(false);
        if (text != null)
        {
            client.Open(uri, text);
        }

        return client;
    }

    private static List<string> Labels(JsonElement completion)
    {
        return [.. completion.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("label").GetString()!)];
    }

    private static string Slice(string text, JsonElement range)
    {
        string[] lines = text.Split('\n');
        JsonElement start = range.GetProperty("start");
        JsonElement end = range.GetProperty("end");
        Assert.AreEqual(start.GetProperty("line").GetInt32(), end.GetProperty("line").GetInt32(), "single-line range expected");
        return lines[start.GetProperty("line").GetInt32()][start.GetProperty("character").GetInt32()..end.GetProperty("character").GetInt32()];
    }

    // ------------------------------------------------------------------ lifecycle

    [TestMethod]
    public async Task InitializeAdvertisesCapabilities()
    {
        using TestClient client = new();
        JsonElement result = await client.Initialize().ConfigureAwait(false);
        JsonElement caps = result.GetProperty("capabilities");
        Assert.AreEqual(1, caps.GetProperty("textDocumentSync").GetProperty("change").GetInt32());
        Assert.IsTrue(caps.GetProperty("textDocumentSync").GetProperty("openClose").GetBoolean());
        Assert.IsTrue(caps.GetProperty("hoverProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("definitionProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("referencesProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("documentSymbolProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("documentFormattingProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("renameProvider").GetProperty("prepareProvider").GetBoolean());
        Assert.AreEqual(".", caps.GetProperty("completionProvider").GetProperty("triggerCharacters")[0].GetString());
        CollectionAssert.AreEqual(s_signatureTriggers, caps.GetProperty("signatureHelpProvider").GetProperty("triggerCharacters").EnumerateArray().Select(e => e.GetString()).ToArray());
        JsonElement legend = caps.GetProperty("semanticTokensProvider").GetProperty("legend");
        CollectionAssert.AreEqual(
            s_tokenTypes,
            legend.GetProperty("tokenTypes").EnumerateArray().Select(e => e.GetString()).ToArray());
        CollectionAssert.AreEqual(s_tokenModifiers, legend.GetProperty("tokenModifiers").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.AreEqual("cala-ls", result.GetProperty("serverInfo").GetProperty("name").GetString());
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public void LegendMatchesCompilerEnums()
    {
        string[] names = Enum.GetNames<Caladesi.Compiler.Semantics.SemanticTokenType>();
        Assert.HasCount(14, names);
        Assert.AreEqual("Namespace,Type,Class,Enum,Interface,Struct,TypeParameter,Parameter,Variable,Property,EnumMember,Function,Method,Decorator", string.Join(',', names));
        int[] modifiers = [.. Enum.GetValues<Caladesi.Compiler.Semantics.SemanticTokenModifiers>().Where(m => m != Caladesi.Compiler.Semantics.SemanticTokenModifiers.None).Select(m => (int)m)];
        CollectionAssert.AreEqual(s_modifierFlags, modifiers);
    }

    [TestMethod]
    public async Task RequestBeforeInitializeIsRejected()
    {
        using TestClient client = new();
        JsonElement response = await client.RequestRaw("textDocument/hover", TestClient.Position(Uri, 0, 0)).ConfigureAwait(false);
        Assert.AreEqual(-32002, response.GetProperty("error").GetProperty("code").GetInt32());
        _ = await client.Initialize().ConfigureAwait(false);
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ExitWithoutShutdownReturnsOne()
    {
        using TestClient client = new();
        _ = await client.Initialize().ConfigureAwait(false);
        client.Notify("exit");
        Assert.AreEqual(1, await client.ServerExit.WaitAsync(TestClient.Timeout).ConfigureAwait(false));
    }

    // ------------------------------------------------------------------ diagnostics

    [TestMethod]
    public async Task DidOpenPublishesErrorAndFixClearsIt()
    {
        using TestClient client = await Started().ConfigureAwait(false);
        string text = "const a = 1;\nlet b: any = a;\n";
        client.Open(Uri, text);
        JsonElement published = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0);
        JsonElement diagnostic = published.GetProperty("diagnostics")[0];
        Assert.AreEqual("CAL0201", diagnostic.GetProperty("code").GetString());
        Assert.AreEqual(1, diagnostic.GetProperty("severity").GetInt32());
        Assert.AreEqual("caladesi", diagnostic.GetProperty("source").GetString());
        Assert.AreEqual(1, diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual("any", Slice(text, diagnostic.GetProperty("range")));

        int mark = client.Mark();
        client.Change(Uri, text.Replace("any", "int"), 2);
        JsonElement cleared = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() == 0, mark);
        Assert.AreEqual(2, cleared.GetProperty("version").GetInt32());
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task IncrementalChangeIsApplied()
    {
        using TestClient client = await Started().ConfigureAwait(false);
        client.Open(Uri, "const a = 1;\nlet b: int = a;\n");
        _ = await client.Request("textDocument/documentSymbol", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""").ConfigureAwait(false);
        int mark = client.Mark();
        client.Notify("textDocument/didChange", $$$"""{"textDocument":{"uri":"{{{Uri}}}","version":2},"contentChanges":[{"range":{"start":{"line":1,"character":7},"end":{"line":1,"character":10}},"text":"any"}]}""");
        JsonElement published = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0, mark);
        Assert.AreEqual("CAL0201", published.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RapidEditsAreCoalesced()
    {
        using TestClient client = new(new ServerOptions { DiagnosticsDelay = TimeSpan.FromMilliseconds(400) });
        _ = await client.Initialize().ConfigureAwait(false);
        client.Open(Uri, "const a = 1;\n");
        const int edits = 40;
        for (int i = 0; i < edits; i++)
        {
            client.Change(Uri, $"const a = {i} == 1;\n", i + 2);
        }

        client.Change(Uri, "for (const k in [1]) {}\n", edits + 5);
        JsonElement final = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0 && d[0].GetProperty("code").GetString() == "CAL0021");
        Assert.AreEqual(edits + 5, final.GetProperty("version").GetInt32());
        Assert.IsLessThan(10, client.CountDiagnosticNotifications(Uri), "expected stale diagnostics runs to be skipped");
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CloseClearsDiagnosticsOfLooseFile()
    {
        using TestClient client = await Started("const a = 1;\nlet b: any = a;\n").ConfigureAwait(false);
        _ = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0);
        int mark = client.Mark();
        client.Notify("textDocument/didClose", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""");
        _ = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() == 0, mark);
        await client.Shutdown().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ hover / navigation

    private const string AddSource = "function add(a: int, b: int): int { return a + b; }\nconst r = ad|d(1, 2);\nConsole.WriteLine(r);\n";

    [TestMethod]
    public async Task HoverOnFunctionCall()
    {
        Marked m = Marked.Parse(AddSource);
        using TestClient client = await Started(m.Text).ConfigureAwait(false);
        JsonElement hover = await client.Request("textDocument/hover", TestClient.Position(Uri, m.Line, m.Character)).ConfigureAwait(false);
        string value = hover.GetProperty("contents").GetProperty("value").GetString()!;
        StringAssert.Contains(value, "function add(a: int, b: int): int");
        Assert.AreEqual("markdown", hover.GetProperty("contents").GetProperty("kind").GetString());
        Assert.AreEqual("add", Slice(m.Text, hover.GetProperty("range")));
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task HoverOnNothingIsNull()
    {
        using TestClient client = await Started("const a = 1;\n").ConfigureAwait(false);
        JsonElement hover = await client.Request("textDocument/hover", TestClient.Position(Uri, 0, 12)).ConfigureAwait(false);
        Assert.AreEqual(JsonValueKind.Null, hover.ValueKind);
        JsonElement unknown = await client.Request("textDocument/hover", TestClient.Position("file:///virtual/unknown.cala", 0, 0)).ConfigureAwait(false);
        Assert.AreEqual(JsonValueKind.Null, unknown.ValueKind);
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DefinitionOfSourceSymbol()
    {
        Marked m = Marked.Parse(AddSource);
        using TestClient client = await Started(m.Text).ConfigureAwait(false);
        JsonElement location = await client.Request("textDocument/definition", TestClient.Position(Uri, m.Line, m.Character)).ConfigureAwait(false);
        Assert.AreEqual(Uri, location.GetProperty("uri").GetString());
        Assert.AreEqual(0, location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual("add", Slice(m.Text, location.GetProperty("range")));
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DefinitionOfMetadataSymbolIsNull()
    {
        Marked m = Marked.Parse("Console.Write|Line(\"x\");\n");
        using TestClient client = await Started(m.Text).ConfigureAwait(false);
        JsonElement location = await client.Request("textDocument/definition", TestClient.Position(Uri, m.Line, m.Character)).ConfigureAwait(false);
        Assert.AreEqual(JsonValueKind.Null, location.ValueKind);
        await client.Shutdown().ConfigureAwait(false);
    }

    private const string SquareSource = "function square(x: int): int { return x * x; }\nconst a = squ|are(2);\nconst b = square(3);\nConsole.WriteLine(a + b);\n";

    [TestMethod]
    public async Task ReferencesIncludeAndExcludeDeclaration()
    {
        Marked m = Marked.Parse(SquareSource);
        using TestClient client = await Started(m.Text).ConfigureAwait(false);
        string position = TestClient.Position(Uri, m.Line, m.Character);
        JsonElement all = await client.Request("textDocument/references", position.Replace("}}", "},\"context\":{\"includeDeclaration\":true}}")).ConfigureAwait(false);
        Assert.AreEqual(3, all.GetArrayLength());
        int[] lines = [.. all.EnumerateArray().Select(l => l.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()).Order()];
        CollectionAssert.AreEqual(s_referenceLines, lines);
        JsonElement usages = await client.Request("textDocument/references", position.Replace("}}", "},\"context\":{\"includeDeclaration\":false}}")).ConfigureAwait(false);
        Assert.AreEqual(2, usages.GetArrayLength());
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RenameRewritesEveryUse()
    {
        Marked m = Marked.Parse(SquareSource);
        using TestClient client = await Started(m.Text).ConfigureAwait(false);
        string position = TestClient.Position(Uri, m.Line, m.Character);

        JsonElement prepare = await client.Request("textDocument/prepareRename", position).ConfigureAwait(false);
        Assert.AreEqual("square", prepare.GetProperty("placeholder").GetString());

        JsonElement edit = await client.Request("textDocument/rename", position.Replace("}}", "},\"newName\":\"sq\"}")).ConfigureAwait(false);
        JsonElement edits = edit.GetProperty("changes").GetProperty(Uri);
        Assert.AreEqual(3, edits.GetArrayLength());
        Assert.IsTrue(edits.EnumerateArray().All(e => e.GetProperty("newText").GetString() == "sq"));
        Assert.IsTrue(edits.EnumerateArray().All(e => Slice(m.Text, e.GetProperty("range")) == "square"));
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RenameRefusesMetadataSymbolsAndBadNames()
    {
        Marked meta = Marked.Parse("Console.Write|Line(\"x\");\n");
        using TestClient client = await Started(meta.Text).ConfigureAwait(false);
        string position = TestClient.Position(Uri, meta.Line, meta.Character);
        JsonElement prepare = await client.Request("textDocument/prepareRename", position).ConfigureAwait(false);
        Assert.AreEqual(JsonValueKind.Null, prepare.ValueKind);
        JsonElement refused = await client.RequestRaw("textDocument/rename", position.Replace("}}", "},\"newName\":\"Foo\"}")).ConfigureAwait(false);
        Assert.AreEqual(-32602, refused.GetProperty("error").GetProperty("code").GetInt32());

        Marked source = Marked.Parse(SquareSource);
        string uri2 = "file:///virtual/other.cala";
        client.Open(uri2, source.Text);
        JsonElement bad = await client.RequestRaw("textDocument/rename", TestClient.Position(uri2, source.Line, source.Character).Replace("}}", "},\"newName\":\"class\"}")).ConfigureAwait(false);
        Assert.AreEqual(-32602, bad.GetProperty("error").GetProperty("code").GetInt32());
        await client.Shutdown().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ completion / signature help

    [TestMethod]
    public async Task CompletionAfterDotOnString()
    {
        Marked m = Marked.Parse("const s = \"hi\";\nConsole.WriteLine(s.|);\n");
        using TestClient client = await Started(m.Text).ConfigureAwait(false);
        JsonElement result = await client.Request("textDocument/completion", TestClient.Position(Uri, m.Line, m.Character)).ConfigureAwait(false);
        List<string> labels = Labels(result);
        CollectionAssert.Contains(labels, "Length");
        CollectionAssert.Contains(labels, "ToUpper");
        CollectionAssert.DoesNotContain(labels, "let");
        JsonElement length = result.GetProperty("items").EnumerateArray().First(i => i.GetProperty("label").GetString() == "Length");
        Assert.AreEqual(10, length.GetProperty("kind").GetInt32());
        JsonElement toUpper = result.GetProperty("items").EnumerateArray().First(i => i.GetProperty("label").GetString() == "ToUpper");
        Assert.AreEqual(2, toUpper.GetProperty("kind").GetInt32());
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CompletionAfterDotOnClassAndNamespace()
    {
        Marked cls = Marked.Parse("class Dog { name = \"d\"; static count = 0; bark(): string { return \"woof\"; } }\nconst d = new Dog();\nd.|");
        using TestClient client = await Started(cls.Text).ConfigureAwait(false);
        List<string> instance = Labels(await client.Request("textDocument/completion", TestClient.Position(Uri, cls.Line, cls.Character)).ConfigureAwait(false));
        CollectionAssert.Contains(instance, "name");
        CollectionAssert.Contains(instance, "bark");
        CollectionAssert.DoesNotContain(instance, "count");

        Marked statics = Marked.Parse("class Dog { name = \"d\"; static count = 0; }\nDog.|");
        string uri2 = "file:///virtual/statics.cala";
        client.Open(uri2, statics.Text);
        List<string> staticLabels = Labels(await client.Request("textDocument/completion", TestClient.Position(uri2, statics.Line, statics.Character)).ConfigureAwait(false));
        CollectionAssert.Contains(staticLabels, "count");
        CollectionAssert.DoesNotContain(staticLabels, "name");

        Marked ns = Marked.Parse("import * as IO from \"System.IO\";\nIO.|");
        string uri3 = "file:///virtual/ns.cala";
        client.Open(uri3, ns.Text);
        List<string> nsLabels = Labels(await client.Request("textDocument/completion", TestClient.Position(uri3, ns.Line, ns.Character)).ConfigureAwait(false));
        CollectionAssert.Contains(nsLabels, "Path");
        CollectionAssert.Contains(nsLabels, "File");
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CompletionSeesUnsavedEditsImmediately()
    {
        using TestClient client = await Started("const a = 1;\n").ConfigureAwait(false);
        client.Change(Uri, "const widget = \"x\";\nwidge", 2);
        JsonElement result = await client.Request("textDocument/completion", TestClient.Position(Uri, 1, 5)).ConfigureAwait(false);
        CollectionAssert.Contains(Labels(result), "widget");
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SignatureHelpReportsActiveParameter()
    {
        Marked m = Marked.Parse("function add(a: int, b: int): int { return a + b; }\nadd(1, |2);\n");
        using TestClient client = await Started(m.Text).ConfigureAwait(false);
        JsonElement help = await client.Request("textDocument/signatureHelp", TestClient.Position(Uri, m.Line, m.Character)).ConfigureAwait(false);
        Assert.AreEqual(1, help.GetProperty("activeParameter").GetInt32());
        JsonElement signature = help.GetProperty("signatures")[0];
        StringAssert.Contains(signature.GetProperty("label").GetString(), "add(a: int, b: int)");
        Assert.AreEqual(2, signature.GetProperty("parameters").GetArrayLength());
        Assert.AreEqual("b: int", signature.GetProperty("parameters")[1].GetProperty("label").GetString());

        Marked first = Marked.Parse("function add(a: int, b: int): int { return a + b; }\nadd(|1, 2);\n");
        client.Change(Uri, first.Text, 2);
        JsonElement firstHelp = await client.Request("textDocument/signatureHelp", TestClient.Position(Uri, first.Line, first.Character)).ConfigureAwait(false);
        Assert.AreEqual(0, firstHelp.GetProperty("activeParameter").GetInt32());

        client.Change(Uri, "const a = 1;\n", 3);
        JsonElement none = await client.Request("textDocument/signatureHelp", TestClient.Position(Uri, 0, 3)).ConfigureAwait(false);
        Assert.AreEqual(JsonValueKind.Null, none.ValueKind);
        await client.Shutdown().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ outline / tokens / formatting

    [TestMethod]
    public async Task DocumentSymbolsAreHierarchical()
    {
        const string text = "class Dog {\n  name: string = \"d\";\n  bark(): string { return \"woof\"; }\n}\nfunction run(): void {}\n";
        using TestClient client = await Started(text).ConfigureAwait(false);
        JsonElement symbols = await client.Request("textDocument/documentSymbol", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""").ConfigureAwait(false);
        Assert.AreEqual(2, symbols.GetArrayLength());
        JsonElement dog = symbols[0];
        Assert.AreEqual("Dog", dog.GetProperty("name").GetString());
        Assert.AreEqual(5, dog.GetProperty("kind").GetInt32());
        Assert.AreEqual(0, dog.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual(3, dog.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
        Assert.AreEqual("Dog", Slice(text, dog.GetProperty("selectionRange")));
        List<JsonElement> children = [.. dog.GetProperty("children").EnumerateArray()];
        CollectionAssert.AreEqual(s_dogMembers, children.Select(c => c.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(6, children[1].GetProperty("kind").GetInt32());
        JsonElement run = symbols[1];
        Assert.AreEqual("run", run.GetProperty("name").GetString());
        Assert.AreEqual(12, run.GetProperty("kind").GetInt32());
        Assert.IsFalse(run.TryGetProperty("children", out _));
        await client.Shutdown().ConfigureAwait(false);
    }

    private sealed record Token(int Line, int Character, int Length, string Type, string[] Modifiers);

    private static List<Token> Decode(JsonElement tokens, JsonElement legend)
    {
        string[] types = [.. legend.GetProperty("tokenTypes").EnumerateArray().Select(e => e.GetString()!)];
        string[] modifiers = [.. legend.GetProperty("tokenModifiers").EnumerateArray().Select(e => e.GetString()!)];
        int[] data = [.. tokens.GetProperty("data").EnumerateArray().Select(e => e.GetInt32())];
        Assert.AreEqual(0, data.Length % 5);
        List<Token> result = [];
        int line = 0;
        int character = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            string[] mods = [.. Enumerable.Range(0, modifiers.Length).Where(bit => (data[i + 4] & (1 << bit)) != 0).Select(bit => modifiers[bit])];
            result.Add(new Token(line, character, data[i + 2], types[data[i + 3]], mods));
        }
        return result;
    }

    [TestMethod]
    public async Task SemanticTokensAreDeltaEncoded()
    {
        const string text = "function add(a: int, b: int): int { return a + b; }\nconst r = add(1, 2);\nConsole.WriteLine(r);\nfunction g(): void { const q = 1; Console.WriteLine(q); }\n";
        using TestClient client = new();
        JsonElement init = await client.Initialize().ConfigureAwait(false);
        client.Open(Uri, text);
        JsonElement result = await client.Request("textDocument/semanticTokens/full", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""").ConfigureAwait(false);
        List<Token> tokens = Decode(result, init.GetProperty("capabilities").GetProperty("semanticTokensProvider").GetProperty("legend"));
        Assert.IsNotEmpty(tokens);

        string[] lines = text.Split('\n');
        foreach (Token t in tokens)
        {
            Assert.IsTrue(char.IsLetter(lines[t.Line][t.Character]), $"token at {t.Line}:{t.Character} does not start an identifier");
        }

        List<Token> ordered = [.. tokens.OrderBy(t => t.Line).ThenBy(t => t.Character)];
        CollectionAssert.AreEqual(ordered, tokens);

        Token addDeclaration = tokens.Single(t => t is { Line: 0, Character: 9 });
        Assert.AreEqual("function", addDeclaration.Type);
        Assert.AreEqual(3, addDeclaration.Length);
        CollectionAssert.Contains(addDeclaration.Modifiers, "declaration");
        Token a = tokens.Single(t => t is { Line: 0, Character: 13 });
        Assert.AreEqual("parameter", a.Type);
        Token r = tokens.Single(t => t is { Line: 1, Character: 6 });
        Assert.AreEqual("variable", r.Type);
        CollectionAssert.Contains(r.Modifiers, "declaration");
        Token q = tokens.Single(t => t is { Line: 3, Character: 27 });
        Assert.AreEqual("variable", q.Type);
        CollectionAssert.IsSubsetOf(s_declarationReadonly, q.Modifiers);
        Token call = tokens.Single(t => t is { Line: 1, Character: 10 });
        Assert.AreEqual("function", call.Type);
        CollectionAssert.DoesNotContain(call.Modifiers, "declaration");
        Token console = tokens.Single(t => t is { Line: 2, Character: 0 });
        Assert.AreEqual("class", console.Type);
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FormattingReturnsSingleWholeDocumentEdit()
    {
        const string text = "function f(): void {\nreturn;\n}\n";
        using TestClient client = await Started(text).ConfigureAwait(false);
        JsonElement edits = await client.Request("textDocument/formatting", $$$"""{"textDocument":{"uri":"{{{Uri}}}"},"options":{"tabSize":2,"insertSpaces":true}}""").ConfigureAwait(false);
        Assert.AreEqual(1, edits.GetArrayLength());
        JsonElement edit = edits[0];
        JsonElement range = edit.GetProperty("range");
        Assert.AreEqual(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual(3, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.AreEqual("function f(): void {\n  return;\n}\n", edit.GetProperty("newText").GetString());

        client.Change(Uri, edit.GetProperty("newText").GetString()!, 2);
        JsonElement again = await client.Request("textDocument/formatting", $$$"""{"textDocument":{"uri":"{{{Uri}}}"},"options":{"tabSize":2,"insertSpaces":true}}""").ConfigureAwait(false);
        Assert.AreEqual(0, again.GetArrayLength());
        await client.Shutdown().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ robustness

    [TestMethod]
    public async Task UnknownRequestGetsMethodNotFound()
    {
        using TestClient client = await Started().ConfigureAwait(false);
        JsonElement response = await client.RequestRaw("textDocument/madeUp").ConfigureAwait(false);
        Assert.AreEqual(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
        client.Notify("custom/notification", """{"a":1}""");
        client.Notify("$/setTrace", """{"value":"off"}""");
        client.Notify("$/cancelRequest", """{"id":12345}""");
        client.Notify("workspace/didChangeConfiguration", """{"settings":null}""");
        JsonElement hover = await client.Request("textDocument/hover", TestClient.Position(Uri, 0, 0)).ConfigureAwait(false);
        Assert.AreEqual(JsonValueKind.Null, hover.ValueKind);
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MalformedInputDoesNotKillTheServer()
    {
        using TestClient client = await Started("const a = 1;\n").ConfigureAwait(false);

        int mark = client.Mark();
        client.Send("{ this is not json");
        _ = client.WaitForError(-32700, mark);

        mark = client.Mark();
        client.Send("[1, 2, 3]");
        _ = client.WaitForError(-32600, mark);

        mark = client.Mark();
        client.Send("""{"jsonrpc":"2.0","id":99}""");
        _ = client.WaitForError(-32600, mark);

        client.WriteRaw("garbage line without colon\r\n\r\n"u8.ToArray());
        client.WriteRaw("Content-Length: banana\r\n\r\n"u8.ToArray());
        client.WriteRaw("Content-Type: application/vscode-jsonrpc\r\n\r\n"u8.ToArray());
        client.WriteRaw("content-length: 2\r\n\r\n{}"u8.ToArray());
        client.Notify("textDocument/didOpen", "{}");
        client.Notify("textDocument/didChange", """{"textDocument":{"uri":"file:///virtual/never-opened.cala"},"contentChanges":[{"text":"x"}]}""");
        client.Notify("textDocument/didOpen", """{"textDocument":{"uri":5}}""");
        client.Notify("workspace/didChangeWatchedFiles", """{"changes":[{"uri":null}]}""");

        JsonElement response = await client.RequestRaw("textDocument/hover", """{"textDocument":{}}""").ConfigureAwait(false);
        Assert.IsTrue(response.TryGetProperty("result", out _) || response.TryGetProperty("error", out _));
        JsonElement symbols = await client.Request("textDocument/documentSymbol", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""").ConfigureAwait(false);
        Assert.AreEqual(1, symbols.GetArrayLength());
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SourceWithSyntaxErrorsStillAnswersRequests()
    {
        Marked m = Marked.Parse("function broken(a: int { return a +;\nconst x = brok|en(;\n");
        using TestClient client = await Started(m.Text).ConfigureAwait(false);
        _ = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0);
        _ = await client.Request("textDocument/hover", TestClient.Position(Uri, m.Line, m.Character)).ConfigureAwait(false);
        _ = await client.Request("textDocument/completion", TestClient.Position(Uri, m.Line, m.Character)).ConfigureAwait(false);
        _ = await client.Request("textDocument/semanticTokens/full", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""").ConfigureAwait(false);
        _ = await client.Request("textDocument/documentSymbol", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""").ConfigureAwait(false);
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CancelledRequestIsAnsweredWithCancellation()
    {
        using TestClient client = await Started("const a = 1;\n").ConfigureAwait(false);
        client.Notify("$/cancelRequest", """{"id":777}""");
        int mark = client.Mark();
        client.Send($$$"""{"jsonrpc":"2.0","id":777,"method":"textDocument/hover","params":{{{TestClient.Position(Uri, 0, 0)}}}}""");
        _ = client.WaitForError(-32800, mark);
        await client.Shutdown().ConfigureAwait(false);
    }
}
