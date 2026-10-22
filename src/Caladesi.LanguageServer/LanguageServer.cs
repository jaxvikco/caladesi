using System.Collections.Concurrent;
using System.Text.Json;
using Caladesi.Compiler;
using Caladesi.LanguageServer.Protocol;

namespace Caladesi.LanguageServer;

/// <summary>
/// JSON-RPC 2.0 / LSP server. One reader thread parses incoming messages; a single worker thread handles them in arrival order,
/// so the (non thread safe) compiler is only ever used from one thread. Diagnostics are computed once the queue has been quiet
/// for <see cref="ServerOptions.DiagnosticsDelay"/>, which coalesces bursts of edits.
/// </summary>
public sealed partial class LanguageServer
{
    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;
    private const int ServerNotInitialized = -32002;
    private const int RequestCancelled = -32800;

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private sealed class Incoming
    {
        public JsonDocument? Document { get; init; }
        public string? Error { get; init; }
        public bool EndOfStream { get; init; }
    }

    private readonly MessageReader _reader;
    private readonly MessageWriter _writer;
    private readonly ServerOptions _options;
    private readonly BlockingCollection<Incoming> _queue = new();
    private readonly ConcurrentDictionary<string, byte> _cancelled = new();
    private readonly Workspace _workspace;
    private bool _initialized;
    private bool _shutdownRequested;
    private bool _exitRequested;
    private bool _supportsWatcherRegistration;
    private int _nextRequestId;

    public LanguageServer(Stream input, Stream output, ServerOptions? options = null)
    {
        _options = options ?? new ServerOptions();
        _reader = new MessageReader(input);
        _writer = new MessageWriter(output);
        _workspace = new Workspace(_options, message => Notify("window/showMessage", w =>
        {
            w.WriteStartObject();
            w.WriteNumber("type", 2);
            w.WriteString("message", message);
            w.WriteEndObject();
        }));
    }

    /// <summary>Serves requests until <c>exit</c> or the end of the input stream. Returns the process exit code.</summary>
    public int Run()
    {
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "cala-ls reader" };
        reader.Start();
        var exitCode = 0;
        var worker = new Thread(() => exitCode = WorkLoop(), 128 * 1024 * 1024) { Name = "cala-ls worker" };
        worker.Start();
        worker.Join();
        return exitCode;
    }

    public Task<int> RunAsync() => Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);

    // ------------------------------------------------------------------ transport

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var body = _reader.ReadMessage();
                if (body == null) break;
                JsonDocument document;
                try { document = JsonDocument.Parse(body); }
                catch (JsonException ex)
                {
                    _queue.Add(new Incoming { Error = ex.Message });
                    continue;
                }
                if (IsCancelRequest(document.RootElement, out var key))
                {
                    _cancelled[key] = 0;
                    document.Dispose();
                    continue;
                }
                _queue.Add(new Incoming { Document = document });
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log.Write("input closed");
        }
        catch (Exception ex)
        {
            Log.Write("reader failed: " + ex);
        }
        finally
        {
            _queue.Add(new Incoming { EndOfStream = true });
        }
    }

    private static bool IsCancelRequest(JsonElement root, out string key)
    {
        key = "";
        if (root.Get("method").GetString() != "$/cancelRequest") return false;
        var id = root.Path("params", "id");
        if (id == null) return false;
        key = id.Value.GetRawText();
        return true;
    }

    private int WorkLoop()
    {
        while (!_exitRequested)
        {
            Incoming? message;
            try
            {
                if (_workspace.Dirty.Count > 0 || _workspace.HasRetired)
                {
                    if (!_queue.TryTake(out message, _options.DiagnosticsDelay))
                    {
                        RunDiagnostics();
                        continue;
                    }
                }
                else message = _queue.Take();
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (message.EndOfStream) break;
            try
            {
                if (message.Error != null) SendError(null, ParseError, "Parse error: " + message.Error);
                else Dispatch(message.Document!.RootElement);
            }
            catch (Exception ex)
            {
                Log.Write("unhandled error: " + ex);
            }
            finally
            {
                message.Document?.Dispose();
            }
        }
        return _shutdownRequested ? 0 : 1;
    }

    private void Send(Action<Utf8JsonWriter> body) => _writer.Write(Json.Build(body));

    private void SendResponse(JsonElement id, Action<Utf8JsonWriter>? result)
    {
        Send(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WritePropertyName("result");
            if (result == null) w.WriteNullValue();
            else result(w);
            w.WriteEndObject();
        });
    }

    private void SendError(JsonElement? id, int code, string message)
    {
        Send(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id == null) w.WriteNullValue();
            else id.Value.WriteTo(w);
            w.WritePropertyName("error");
            w.WriteStartObject();
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    private void Notify(string method, Action<Utf8JsonWriter> parameters)
    {
        Send(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", method);
            w.WritePropertyName("params");
            parameters(w);
            w.WriteEndObject();
        });
    }

    private void SendRequest(string method, Action<Utf8JsonWriter> parameters)
    {
        var id = ++_nextRequestId;
        Send(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            w.WritePropertyName("params");
            parameters(w);
            w.WriteEndObject();
        });
    }

    // ------------------------------------------------------------------ dispatch

    private void Dispatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            SendError(null, InvalidRequest, "Invalid request: expected a JSON object");
            return;
        }
        var id = root.Get("id");
        var method = root.Get("method").GetString();
        if (method == null)
        {
            if (id == null || root.Get("result") == null && root.Get("error") == null)
                SendError(id, InvalidRequest, "Invalid request: missing method");
            return;
        }
        var parameters = root.Get("params") ?? default;
        if (id == null) HandleNotification(method, parameters);
        else HandleRequest(id.Value, method, parameters);
    }

    private void HandleRequest(JsonElement id, string method, JsonElement p)
    {
        if (_cancelled.TryRemove(id.GetRawText(), out _))
        {
            SendError(id, RequestCancelled, "Request cancelled");
            return;
        }
        if (method == "initialize")
        {
            SendResponse(id, Initialize(p));
            return;
        }
        if (!_initialized)
        {
            SendError(id, ServerNotInitialized, "Server not initialized");
            return;
        }
        if (_shutdownRequested && method != "shutdown")
        {
            SendError(id, InvalidRequest, "Server is shutting down");
            return;
        }

        try
        {
            Action<Utf8JsonWriter>? result;
            switch (method)
            {
                case "shutdown": _shutdownRequested = true; result = null; break;
                case "textDocument/hover": result = Hover(p); break;
                case "textDocument/completion": result = Completion(p); break;
                case "textDocument/definition": result = Definition(p); break;
                case "textDocument/references": result = References(p); break;
                case "textDocument/prepareRename": result = PrepareRename(p); break;
                case "textDocument/rename": result = Rename(p); break;
                case "textDocument/documentSymbol": result = DocumentSymbols(p); break;
                case "textDocument/semanticTokens/full": result = SemanticTokensFull(p); break;
                case "textDocument/signatureHelp": result = SignatureHelp(p); break;
                case "textDocument/formatting": result = Formatting(p); break;
                default:
                    SendError(id, MethodNotFound, "Method not found: " + method);
                    return;
            }
            SendResponse(id, result);
        }
        catch (RpcException ex)
        {
            SendError(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write($"request {method} failed: {ex}");
            SendError(id, InternalError, $"Internal error handling {method}: {ex.Message}");
        }
    }

    private void HandleNotification(string method, JsonElement p)
    {
        try
        {
            switch (method)
            {
                case "initialized": OnInitialized(); break;
                case "exit": _exitRequested = true; break;
                case "textDocument/didOpen": DidOpen(p); break;
                case "textDocument/didChange": DidChange(p); break;
                case "textDocument/didClose": DidClose(p); break;
                case "textDocument/didSave": DidSave(p); break;
                case "workspace/didChangeWatchedFiles": DidChangeWatchedFiles(p); break;
                default:
                    if (!method.StartsWith("$/", StringComparison.Ordinal) && method != "workspace/didChangeConfiguration")
                        Log.Write("ignoring notification " + method);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"notification {method} failed: {ex}");
        }
    }

    // ------------------------------------------------------------------ lifecycle

    private Action<Utf8JsonWriter> Initialize(JsonElement p)
    {
        _initialized = true;
        foreach (var folder in p.Get("workspaceFolders").Items())
            if (folder.Get("uri").GetString() is { } uri) AddRoot(uri);
        if (_workspace.Roots.Count == 0)
        {
            if (p.Get("rootUri").GetString() is { } rootUri) AddRoot(rootUri);
            else if (p.Get("rootPath").GetString() is { } rootPath && Path.IsPathRooted(rootPath)) _workspace.Roots.Add(Path.GetFullPath(rootPath));
        }
        var options = p.Get("initializationOptions");
        if (options.Get("runtimePath").GetString() is { Length: > 0 } runtimePath) _options.RuntimePath = runtimePath;
        if (options.Get("dotnetPath").GetString() is { Length: > 0 } dotnetPath) _options.DotnetPath = dotnetPath;
        _supportsWatcherRegistration = p.Path("capabilities", "workspace", "didChangeWatchedFiles", "dynamicRegistration").GetBool();
        return WriteInitializeResult;
    }

    private void AddRoot(string uri)
    {
        var path = Workspace.UriToPath(uri);
        if (Path.IsPathRooted(path)) _workspace.Roots.Add(path.TrimEnd(Path.DirectorySeparatorChar));
    }

    private void OnInitialized()
    {
        if (!_supportsWatcherRegistration) return;
        SendRequest("client/registerCapability", w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("registrations");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("id", "caladesi-watch");
            w.WriteString("method", "workspace/didChangeWatchedFiles");
            w.WritePropertyName("registerOptions");
            w.WriteStartObject();
            w.WritePropertyName("watchers");
            w.WriteStartArray();
            foreach (var glob in new[] { "**/*.calaproj", "**/*.cala", "**/obj/project.assets.json" })
            {
                w.WriteStartObject();
                w.WriteString("globPattern", glob);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static readonly string[] TokenTypes =
        ["namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "function", "method", "decorator"];

    private static readonly string[] TokenModifiers = ["declaration", "readonly", "static"];

    private static void WriteInitializeResult(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WritePropertyName("capabilities");
        w.WriteStartObject();
        w.WritePropertyName("textDocumentSync");
        w.WriteStartObject();
        w.WriteBoolean("openClose", true);
        w.WriteNumber("change", 1);
        w.WritePropertyName("save");
        w.WriteStartObject();
        w.WriteBoolean("includeText", false);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteBoolean("hoverProvider", true);
        w.WritePropertyName("completionProvider");
        w.WriteStartObject();
        w.WritePropertyName("triggerCharacters");
        w.WriteStartArray();
        w.WriteStringValue(".");
        w.WriteEndArray();
        w.WriteBoolean("resolveProvider", false);
        w.WriteEndObject();
        w.WritePropertyName("signatureHelpProvider");
        w.WriteStartObject();
        w.WritePropertyName("triggerCharacters");
        w.WriteStartArray();
        w.WriteStringValue("(");
        w.WriteStringValue(",");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteBoolean("definitionProvider", true);
        w.WriteBoolean("referencesProvider", true);
        w.WritePropertyName("renameProvider");
        w.WriteStartObject();
        w.WriteBoolean("prepareProvider", true);
        w.WriteEndObject();
        w.WriteBoolean("documentSymbolProvider", true);
        w.WriteBoolean("documentFormattingProvider", true);
        w.WritePropertyName("semanticTokensProvider");
        w.WriteStartObject();
        w.WritePropertyName("legend");
        w.WriteStartObject();
        w.WritePropertyName("tokenTypes");
        w.WriteStartArray();
        foreach (var t in TokenTypes) w.WriteStringValue(t);
        w.WriteEndArray();
        w.WritePropertyName("tokenModifiers");
        w.WriteStartArray();
        foreach (var m in TokenModifiers) w.WriteStringValue(m);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteBoolean("full", true);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WritePropertyName("serverInfo");
        w.WriteStartObject();
        w.WriteString("name", "cala-ls");
        w.WriteString("version", CompilerInfo.Version);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    // ------------------------------------------------------------------ document sync

    private void DidOpen(JsonElement p)
    {
        var doc = p.Get("textDocument");
        var uri = doc.Get("uri").GetString();
        if (uri == null) return;
        _workspace.Open(uri, doc.Get("text").GetString() ?? "", doc.Get("version").GetInt() ?? 0);
    }

    private void DidChange(JsonElement p)
    {
        var td = p.Get("textDocument");
        var uri = td.Get("uri").GetString();
        if (uri == null || _workspace.GetDocument(uri) is not { } document) return;
        var text = document.Text;
        foreach (var change in p.Get("contentChanges").Items())
        {
            var newText = change.Get("text").GetString() ?? "";
            if (change.Get("range") is { } range)
            {
                var source = Caladesi.Compiler.Text.SourceText.From(text);
                var start = ReadPosition(range.Get("start"));
                var end = ReadPosition(range.Get("end"));
                var from = source.GetPosition(start);
                var to = Math.Max(from, source.GetPosition(end));
                text = string.Concat(text.AsSpan(0, from), newText, text.AsSpan(to));
            }
            else text = newText;
        }
        _workspace.Change(document, text, td.Get("version").GetInt() ?? document.Version);
    }

    private void DidClose(JsonElement p)
    {
        if (p.Path("textDocument", "uri").GetString() is { } uri) _workspace.Close(uri);
    }

    private void DidSave(JsonElement p)
    {
        if (p.Path("textDocument", "uri").GetString() is { } uri && _workspace.GetUnit(uri) is { } unit) _workspace.Dirty.Add(unit);
    }

    private void DidChangeWatchedFiles(JsonElement p)
    {
        var changes = new List<(string, int)>();
        foreach (var change in p.Get("changes").Items())
            if (change.Get("uri").GetString() is { } uri) changes.Add((Workspace.UriToPath(uri), change.Get("type").GetInt() ?? 2));
        _workspace.FilesChanged(changes);
    }
}
