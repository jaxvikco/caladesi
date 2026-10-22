using System.Text.Json;

namespace Caladesi_LanguageServer_Tests;

[TestClass]
public class ServerTests
{
    private const string Uri = "file:///virtual/main.cala";

    private static async Task<TestClient> Started(string? text = null, string uri = Uri)
    {
        var client = new TestClient();
        await client.Initialize();
        if (text != null) client.Open(uri, text);
        return client;
    }

    private static List<string> Labels(JsonElement completion) =>
        completion.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("label").GetString()!).ToList();

    private static string Slice(string text, JsonElement range)
    {
        var lines = text.Split('\n');
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");
        Assert.AreEqual(start.GetProperty("line").GetInt32(), end.GetProperty("line").GetInt32(), "single-line range expected");
        return lines[start.GetProperty("line").GetInt32()][start.GetProperty("character").GetInt32()..end.GetProperty("character").GetInt32()];
    }

    // ------------------------------------------------------------------ lifecycle

    [TestMethod]
    public async Task InitializeAdvertisesCapabilities()
    {
        using var client = new TestClient();
        var result = await client.Initialize();
        var caps = result.GetProperty("capabilities");
        Assert.AreEqual(1, caps.GetProperty("textDocumentSync").GetProperty("change").GetInt32());
        Assert.IsTrue(caps.GetProperty("textDocumentSync").GetProperty("openClose").GetBoolean());
        Assert.IsTrue(caps.GetProperty("hoverProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("definitionProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("referencesProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("documentSymbolProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("documentFormattingProvider").GetBoolean());
        Assert.IsTrue(caps.GetProperty("renameProvider").GetProperty("prepareProvider").GetBoolean());
        Assert.AreEqual(".", caps.GetProperty("completionProvider").GetProperty("triggerCharacters")[0].GetString());
        CollectionAssert.AreEqual(new[] { "(", "," }, caps.GetProperty("signatureHelpProvider").GetProperty("triggerCharacters").EnumerateArray().Select(e => e.GetString()).ToArray());
        var legend = caps.GetProperty("semanticTokensProvider").GetProperty("legend");
        CollectionAssert.AreEqual(
            new[] { "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "function", "method", "decorator" },
            legend.GetProperty("tokenTypes").EnumerateArray().Select(e => e.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "declaration", "readonly", "static" }, legend.GetProperty("tokenModifiers").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.AreEqual("cala-ls", result.GetProperty("serverInfo").GetProperty("name").GetString());
        await client.Shutdown();
    }

    [TestMethod]
    public void LegendMatchesCompilerEnums()
    {
        var names = Enum.GetNames<Caladesi.Compiler.Semantics.SemanticTokenType>();
        Assert.HasCount(14, names);
        Assert.AreEqual("Namespace,Type,Class,Enum,Interface,Struct,TypeParameter,Parameter,Variable,Property,EnumMember,Function,Method,Decorator", string.Join(',', names));
        var modifiers = Enum.GetValues<Caladesi.Compiler.Semantics.SemanticTokenModifiers>().Where(m => m != Caladesi.Compiler.Semantics.SemanticTokenModifiers.None).Select(m => (int)m).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 4 }, modifiers);
    }

    [TestMethod]
    public async Task RequestBeforeInitializeIsRejected()
    {
        using var client = new TestClient();
        var response = await client.RequestRaw("textDocument/hover", TestClient.Position(Uri, 0, 0));
        Assert.AreEqual(-32002, response.GetProperty("error").GetProperty("code").GetInt32());
        await client.Initialize();
        await client.Shutdown();
    }

    [TestMethod]
    public async Task ExitWithoutShutdownReturnsOne()
    {
        using var client = new TestClient();
        await client.Initialize();
        client.Notify("exit");
        Assert.AreEqual(1, await client.ServerExit.WaitAsync(TestClient.Timeout));
    }

    // ------------------------------------------------------------------ diagnostics

    [TestMethod]
    public async Task DidOpenPublishesErrorAndFixClearsIt()
    {
        using var client = await Started();
        var text = "const a = 1;\nif (a == 1) { Console.WriteLine(\"x\"); }\n";
        client.Open(Uri, text);
        var published = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0);
        var diagnostic = published.GetProperty("diagnostics")[0];
        Assert.AreEqual("CAL0023", diagnostic.GetProperty("code").GetString());
        Assert.AreEqual(1, diagnostic.GetProperty("severity").GetInt32());
        Assert.AreEqual("caladesi", diagnostic.GetProperty("source").GetString());
        Assert.AreEqual(1, diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual("==", Slice(text, diagnostic.GetProperty("range")));

        var mark = client.Mark();
        client.Change(Uri, text.Replace("==", "==="), 2);
        var cleared = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() == 0, mark);
        Assert.AreEqual(2, cleared.GetProperty("version").GetInt32());
        await client.Shutdown();
    }

    [TestMethod]
    public async Task IncrementalChangeIsApplied()
    {
        using var client = await Started();
        client.Open(Uri, "const a = 1;\nif (a === 1) { }\n");
        await client.Request("textDocument/documentSymbol", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""");
        var mark = client.Mark();
        client.Notify("textDocument/didChange", $$$"""{"textDocument":{"uri":"{{{Uri}}}","version":2},"contentChanges":[{"range":{"start":{"line":1,"character":6},"end":{"line":1,"character":9}},"text":"=="}]}""");
        var published = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0, mark);
        Assert.AreEqual("CAL0023", published.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        await client.Shutdown();
    }

    [TestMethod]
    public async Task RapidEditsAreCoalesced()
    {
        using var client = new TestClient(new Caladesi.LanguageServer.ServerOptions { DiagnosticsDelay = TimeSpan.FromMilliseconds(400) });
        await client.Initialize();
        client.Open(Uri, "const a = 1;\n");
        const int edits = 40;
        for (var i = 0; i < edits; i++) client.Change(Uri, $"const a = {i} == 1;\n", i + 2);
        client.Change(Uri, "for (const k in [1]) {}\n", edits + 5);
        var final = client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0 && d[0].GetProperty("code").GetString() == "CAL0021");
        Assert.AreEqual(edits + 5, final.GetProperty("version").GetInt32());
        Assert.IsLessThan(10, client.CountDiagnosticNotifications(Uri), "expected stale diagnostics runs to be skipped");
        await client.Shutdown();
    }

    [TestMethod]
    public async Task CloseClearsDiagnosticsOfLooseFile()
    {
        using var client = await Started("const a = 1;\nif (a == 1) { }\n");
        client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0);
        var mark = client.Mark();
        client.Notify("textDocument/didClose", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""");
        client.WaitForDiagnostics(Uri, d => d.GetArrayLength() == 0, mark);
        await client.Shutdown();
    }

    // ------------------------------------------------------------------ hover / navigation

    private const string AddSource = "function add(a: int, b: int): int { return a + b; }\nconst r = ad|d(1, 2);\nConsole.WriteLine(r);\n";

    [TestMethod]
    public async Task HoverOnFunctionCall()
    {
        var m = Marked.Parse(AddSource);
        using var client = await Started(m.Text);
        var hover = await client.Request("textDocument/hover", TestClient.Position(Uri, m.Line, m.Character));
        var value = hover.GetProperty("contents").GetProperty("value").GetString()!;
        StringAssert.Contains(value, "function add(a: int, b: int): int");
        Assert.AreEqual("markdown", hover.GetProperty("contents").GetProperty("kind").GetString());
        Assert.AreEqual("add", Slice(m.Text, hover.GetProperty("range")));
        await client.Shutdown();
    }

    [TestMethod]
    public async Task HoverOnNothingIsNull()
    {
        using var client = await Started("const a = 1;\n");
        var hover = await client.Request("textDocument/hover", TestClient.Position(Uri, 0, 12));
        Assert.AreEqual(JsonValueKind.Null, hover.ValueKind);
        var unknown = await client.Request("textDocument/hover", TestClient.Position("file:///virtual/unknown.cala", 0, 0));
        Assert.AreEqual(JsonValueKind.Null, unknown.ValueKind);
        await client.Shutdown();
    }

    [TestMethod]
    public async Task DefinitionOfSourceSymbol()
    {
        var m = Marked.Parse(AddSource);
        using var client = await Started(m.Text);
        var location = await client.Request("textDocument/definition", TestClient.Position(Uri, m.Line, m.Character));
        Assert.AreEqual(Uri, location.GetProperty("uri").GetString());
        Assert.AreEqual(0, location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual("add", Slice(m.Text, location.GetProperty("range")));
        await client.Shutdown();
    }

    [TestMethod]
    public async Task DefinitionOfMetadataSymbolIsNull()
    {
        var m = Marked.Parse("Console.Write|Line(\"x\");\n");
        using var client = await Started(m.Text);
        var location = await client.Request("textDocument/definition", TestClient.Position(Uri, m.Line, m.Character));
        Assert.AreEqual(JsonValueKind.Null, location.ValueKind);
        await client.Shutdown();
    }

    private const string SquareSource = "function square(x: int): int { return x * x; }\nconst a = squ|are(2);\nconst b = square(3);\nConsole.WriteLine(a + b);\n";

    [TestMethod]
    public async Task ReferencesIncludeAndExcludeDeclaration()
    {
        var m = Marked.Parse(SquareSource);
        using var client = await Started(m.Text);
        var position = TestClient.Position(Uri, m.Line, m.Character);
        var all = await client.Request("textDocument/references", position.Replace("}}", "},\"context\":{\"includeDeclaration\":true}}"));
        Assert.AreEqual(3, all.GetArrayLength());
        var lines = all.EnumerateArray().Select(l => l.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()).Order().ToArray();
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, lines);
        var usages = await client.Request("textDocument/references", position.Replace("}}", "},\"context\":{\"includeDeclaration\":false}}"));
        Assert.AreEqual(2, usages.GetArrayLength());
        await client.Shutdown();
    }

    [TestMethod]
    public async Task RenameRewritesEveryUse()
    {
        var m = Marked.Parse(SquareSource);
        using var client = await Started(m.Text);
        var position = TestClient.Position(Uri, m.Line, m.Character);

        var prepare = await client.Request("textDocument/prepareRename", position);
        Assert.AreEqual("square", prepare.GetProperty("placeholder").GetString());

        var edit = await client.Request("textDocument/rename", position.Replace("}}", "},\"newName\":\"sq\"}"));
        var edits = edit.GetProperty("changes").GetProperty(Uri);
        Assert.AreEqual(3, edits.GetArrayLength());
        Assert.IsTrue(edits.EnumerateArray().All(e => e.GetProperty("newText").GetString() == "sq"));
        Assert.IsTrue(edits.EnumerateArray().All(e => Slice(m.Text, e.GetProperty("range")) == "square"));
        await client.Shutdown();
    }

    [TestMethod]
    public async Task RenameRefusesMetadataSymbolsAndBadNames()
    {
        var meta = Marked.Parse("Console.Write|Line(\"x\");\n");
        using var client = await Started(meta.Text);
        var position = TestClient.Position(Uri, meta.Line, meta.Character);
        var prepare = await client.Request("textDocument/prepareRename", position);
        Assert.AreEqual(JsonValueKind.Null, prepare.ValueKind);
        var refused = await client.RequestRaw("textDocument/rename", position.Replace("}}", "},\"newName\":\"Foo\"}"));
        Assert.AreEqual(-32602, refused.GetProperty("error").GetProperty("code").GetInt32());

        var source = Marked.Parse(SquareSource);
        var uri2 = "file:///virtual/other.cala";
        client.Open(uri2, source.Text);
        var bad = await client.RequestRaw("textDocument/rename", TestClient.Position(uri2, source.Line, source.Character).Replace("}}", "},\"newName\":\"class\"}"));
        Assert.AreEqual(-32602, bad.GetProperty("error").GetProperty("code").GetInt32());
        await client.Shutdown();
    }

    // ------------------------------------------------------------------ completion / signature help

    [TestMethod]
    public async Task CompletionAfterDotOnString()
    {
        var m = Marked.Parse("const s = \"hi\";\nConsole.WriteLine(s.|);\n");
        using var client = await Started(m.Text);
        var result = await client.Request("textDocument/completion", TestClient.Position(Uri, m.Line, m.Character));
        var labels = Labels(result);
        CollectionAssert.Contains(labels, "Length");
        CollectionAssert.Contains(labels, "ToUpper");
        CollectionAssert.DoesNotContain(labels, "let");
        var length = result.GetProperty("items").EnumerateArray().First(i => i.GetProperty("label").GetString() == "Length");
        Assert.AreEqual(10, length.GetProperty("kind").GetInt32());
        var toUpper = result.GetProperty("items").EnumerateArray().First(i => i.GetProperty("label").GetString() == "ToUpper");
        Assert.AreEqual(2, toUpper.GetProperty("kind").GetInt32());
        await client.Shutdown();
    }

    [TestMethod]
    public async Task CompletionAfterDotOnClassAndNamespace()
    {
        var cls = Marked.Parse("class Dog { name = \"d\"; static count = 0; bark(): string { return \"woof\"; } }\nconst d = new Dog();\nd.|");
        using var client = await Started(cls.Text);
        var instance = Labels(await client.Request("textDocument/completion", TestClient.Position(Uri, cls.Line, cls.Character)));
        CollectionAssert.Contains(instance, "name");
        CollectionAssert.Contains(instance, "bark");
        CollectionAssert.DoesNotContain(instance, "count");

        var statics = Marked.Parse("class Dog { name = \"d\"; static count = 0; }\nDog.|");
        var uri2 = "file:///virtual/statics.cala";
        client.Open(uri2, statics.Text);
        var staticLabels = Labels(await client.Request("textDocument/completion", TestClient.Position(uri2, statics.Line, statics.Character)));
        CollectionAssert.Contains(staticLabels, "count");
        CollectionAssert.DoesNotContain(staticLabels, "name");

        var ns = Marked.Parse("import * as IO from \"System.IO\";\nIO.|");
        var uri3 = "file:///virtual/ns.cala";
        client.Open(uri3, ns.Text);
        var nsLabels = Labels(await client.Request("textDocument/completion", TestClient.Position(uri3, ns.Line, ns.Character)));
        CollectionAssert.Contains(nsLabels, "Path");
        CollectionAssert.Contains(nsLabels, "File");
        await client.Shutdown();
    }

    [TestMethod]
    public async Task CompletionSeesUnsavedEditsImmediately()
    {
        using var client = await Started("const a = 1;\n");
        client.Change(Uri, "const widget = \"x\";\nwidge", 2);
        var result = await client.Request("textDocument/completion", TestClient.Position(Uri, 1, 5));
        CollectionAssert.Contains(Labels(result), "widget");
        await client.Shutdown();
    }

    [TestMethod]
    public async Task SignatureHelpReportsActiveParameter()
    {
        var m = Marked.Parse("function add(a: int, b: int): int { return a + b; }\nadd(1, |2);\n");
        using var client = await Started(m.Text);
        var help = await client.Request("textDocument/signatureHelp", TestClient.Position(Uri, m.Line, m.Character));
        Assert.AreEqual(1, help.GetProperty("activeParameter").GetInt32());
        var signature = help.GetProperty("signatures")[0];
        StringAssert.Contains(signature.GetProperty("label").GetString(), "add(a: int, b: int)");
        Assert.AreEqual(2, signature.GetProperty("parameters").GetArrayLength());
        Assert.AreEqual("b: int", signature.GetProperty("parameters")[1].GetProperty("label").GetString());

        var first = Marked.Parse("function add(a: int, b: int): int { return a + b; }\nadd(|1, 2);\n");
        client.Change(Uri, first.Text, 2);
        var firstHelp = await client.Request("textDocument/signatureHelp", TestClient.Position(Uri, first.Line, first.Character));
        Assert.AreEqual(0, firstHelp.GetProperty("activeParameter").GetInt32());

        client.Change(Uri, "const a = 1;\n", 3);
        var none = await client.Request("textDocument/signatureHelp", TestClient.Position(Uri, 0, 3));
        Assert.AreEqual(JsonValueKind.Null, none.ValueKind);
        await client.Shutdown();
    }

    // ------------------------------------------------------------------ outline / tokens / formatting

    [TestMethod]
    public async Task DocumentSymbolsAreHierarchical()
    {
        const string text = "class Dog {\n  name: string = \"d\";\n  bark(): string { return \"woof\"; }\n}\nfunction run(): void {}\n";
        using var client = await Started(text);
        var symbols = await client.Request("textDocument/documentSymbol", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""");
        Assert.AreEqual(2, symbols.GetArrayLength());
        var dog = symbols[0];
        Assert.AreEqual("Dog", dog.GetProperty("name").GetString());
        Assert.AreEqual(5, dog.GetProperty("kind").GetInt32());
        Assert.AreEqual(0, dog.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual(3, dog.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
        Assert.AreEqual("Dog", Slice(text, dog.GetProperty("selectionRange")));
        var children = dog.GetProperty("children").EnumerateArray().ToList();
        CollectionAssert.AreEqual(new[] { "name", "bark" }, children.Select(c => c.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(6, children[1].GetProperty("kind").GetInt32());
        var run = symbols[1];
        Assert.AreEqual("run", run.GetProperty("name").GetString());
        Assert.AreEqual(12, run.GetProperty("kind").GetInt32());
        Assert.IsFalse(run.TryGetProperty("children", out _));
        await client.Shutdown();
    }

    private sealed record Token(int Line, int Character, int Length, string Type, string[] Modifiers);

    private static List<Token> Decode(JsonElement tokens, JsonElement legend)
    {
        var types = legend.GetProperty("tokenTypes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var modifiers = legend.GetProperty("tokenModifiers").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var data = tokens.GetProperty("data").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.AreEqual(0, data.Length % 5);
        var result = new List<Token>();
        int line = 0, character = 0;
        for (var i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            var mods = Enumerable.Range(0, modifiers.Length).Where(bit => (data[i + 4] & (1 << bit)) != 0).Select(bit => modifiers[bit]).ToArray();
            result.Add(new Token(line, character, data[i + 2], types[data[i + 3]], mods));
        }
        return result;
    }

    [TestMethod]
    public async Task SemanticTokensAreDeltaEncoded()
    {
        const string text = "function add(a: int, b: int): int { return a + b; }\nconst r = add(1, 2);\nConsole.WriteLine(r);\nfunction g(): void { const q = 1; Console.WriteLine(q); }\n";
        using var client = new TestClient();
        var init = await client.Initialize();
        client.Open(Uri, text);
        var result = await client.Request("textDocument/semanticTokens/full", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""");
        var tokens = Decode(result, init.GetProperty("capabilities").GetProperty("semanticTokensProvider").GetProperty("legend"));
        Assert.IsNotEmpty(tokens);

        var lines = text.Split('\n');
        foreach (var t in tokens)
            Assert.IsTrue(char.IsLetter(lines[t.Line][t.Character]), $"token at {t.Line}:{t.Character} does not start an identifier");
        var ordered = tokens.OrderBy(t => t.Line).ThenBy(t => t.Character).ToList();
        CollectionAssert.AreEqual(ordered, tokens);

        var addDeclaration = tokens.Single(t => t is { Line: 0, Character: 9 });
        Assert.AreEqual("function", addDeclaration.Type);
        Assert.AreEqual(3, addDeclaration.Length);
        CollectionAssert.Contains(addDeclaration.Modifiers, "declaration");
        var a = tokens.Single(t => t is { Line: 0, Character: 13 });
        Assert.AreEqual("parameter", a.Type);
        var r = tokens.Single(t => t is { Line: 1, Character: 6 });
        Assert.AreEqual("variable", r.Type);
        CollectionAssert.Contains(r.Modifiers, "declaration");
        var q = tokens.Single(t => t is { Line: 3, Character: 27 });
        Assert.AreEqual("variable", q.Type);
        CollectionAssert.IsSubsetOf(new[] { "declaration", "readonly" }, q.Modifiers);
        var call = tokens.Single(t => t is { Line: 1, Character: 10 });
        Assert.AreEqual("function", call.Type);
        CollectionAssert.DoesNotContain(call.Modifiers, "declaration");
        var console = tokens.Single(t => t is { Line: 2, Character: 0 });
        Assert.AreEqual("class", console.Type);
        await client.Shutdown();
    }

    [TestMethod]
    public async Task FormattingReturnsSingleWholeDocumentEdit()
    {
        const string text = "function f(): void {\nreturn;\n}\n";
        using var client = await Started(text);
        var edits = await client.Request("textDocument/formatting", $$$"""{"textDocument":{"uri":"{{{Uri}}}"},"options":{"tabSize":2,"insertSpaces":true}}""");
        Assert.AreEqual(1, edits.GetArrayLength());
        var edit = edits[0];
        var range = edit.GetProperty("range");
        Assert.AreEqual(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual(3, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.AreEqual("function f(): void {\n  return;\n}\n", edit.GetProperty("newText").GetString());

        client.Change(Uri, edit.GetProperty("newText").GetString()!, 2);
        var again = await client.Request("textDocument/formatting", $$$"""{"textDocument":{"uri":"{{{Uri}}}"},"options":{"tabSize":2,"insertSpaces":true}}""");
        Assert.AreEqual(0, again.GetArrayLength());
        await client.Shutdown();
    }

    // ------------------------------------------------------------------ robustness

    [TestMethod]
    public async Task UnknownRequestGetsMethodNotFound()
    {
        using var client = await Started();
        var response = await client.RequestRaw("textDocument/madeUp");
        Assert.AreEqual(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
        client.Notify("custom/notification", """{"a":1}""");
        client.Notify("$/setTrace", """{"value":"off"}""");
        client.Notify("$/cancelRequest", """{"id":12345}""");
        client.Notify("workspace/didChangeConfiguration", """{"settings":null}""");
        var hover = await client.Request("textDocument/hover", TestClient.Position(Uri, 0, 0));
        Assert.AreEqual(JsonValueKind.Null, hover.ValueKind);
        await client.Shutdown();
    }

    [TestMethod]
    public async Task MalformedInputDoesNotKillTheServer()
    {
        using var client = await Started("const a = 1;\n");

        var mark = client.Mark();
        client.Send("{ this is not json");
        client.WaitForError(-32700, mark);

        mark = client.Mark();
        client.Send("[1, 2, 3]");
        client.WaitForError(-32600, mark);

        mark = client.Mark();
        client.Send("""{"jsonrpc":"2.0","id":99}""");
        client.WaitForError(-32600, mark);

        client.WriteRaw("garbage line without colon\r\n\r\n"u8.ToArray());
        client.WriteRaw("Content-Length: banana\r\n\r\n"u8.ToArray());
        client.WriteRaw("Content-Type: application/vscode-jsonrpc\r\n\r\n"u8.ToArray());
        client.WriteRaw("content-length: 2\r\n\r\n{}"u8.ToArray());
        client.Notify("textDocument/didOpen", "{}");
        client.Notify("textDocument/didChange", """{"textDocument":{"uri":"file:///virtual/never-opened.cala"},"contentChanges":[{"text":"x"}]}""");
        client.Notify("textDocument/didOpen", """{"textDocument":{"uri":5}}""");
        client.Notify("workspace/didChangeWatchedFiles", """{"changes":[{"uri":null}]}""");

        var response = await client.RequestRaw("textDocument/hover", """{"textDocument":{}}""");
        Assert.IsTrue(response.TryGetProperty("result", out _) || response.TryGetProperty("error", out _));
        var symbols = await client.Request("textDocument/documentSymbol", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""");
        Assert.AreEqual(1, symbols.GetArrayLength());
        await client.Shutdown();
    }

    [TestMethod]
    public async Task SourceWithSyntaxErrorsStillAnswersRequests()
    {
        var m = Marked.Parse("function broken(a: int { return a +;\nconst x = brok|en(;\n");
        using var client = await Started(m.Text);
        client.WaitForDiagnostics(Uri, d => d.GetArrayLength() > 0);
        await client.Request("textDocument/hover", TestClient.Position(Uri, m.Line, m.Character));
        await client.Request("textDocument/completion", TestClient.Position(Uri, m.Line, m.Character));
        await client.Request("textDocument/semanticTokens/full", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""");
        await client.Request("textDocument/documentSymbol", $$$"""{"textDocument":{"uri":"{{{Uri}}}"}}""");
        await client.Shutdown();
    }

    [TestMethod]
    public async Task CancelledRequestIsAnsweredWithCancellation()
    {
        using var client = await Started("const a = 1;\n");
        client.Notify("$/cancelRequest", """{"id":777}""");
        var mark = client.Mark();
        client.Send($$$"""{"jsonrpc":"2.0","id":777,"method":"textDocument/hover","params":{{{TestClient.Position(Uri, 0, 0)}}}}""");
        client.WaitForError(-32800, mark);
        await client.Shutdown();
    }
}
