using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Caladesi.LanguageServer;

namespace Caladesi_LanguageServer_Tests;

/// <summary>A minimal LSP client that talks to an in-process <see cref="LanguageServer"/> over anonymous pipes.</summary>
internal sealed class TestClient : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly Stream _toServer;
    private readonly Stream _fromServer;
    private readonly AnonymousPipeServerStream _clientWrites;
    private readonly AnonymousPipeServerStream _serverWrites;
    private readonly AnonymousPipeClientStream _serverInput;
    private readonly AnonymousPipeClientStream _serverOutput;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly List<JsonElement> _notifications = [];
    private readonly object _gate = new();
    private int _nextId;
    private readonly Task<int> _serverTask;

    public TestClient(ServerOptions? options = null)
    {
        _clientWrites = new AnonymousPipeServerStream(PipeDirection.Out);
        _serverInput = new AnonymousPipeClientStream(PipeDirection.In, _clientWrites.ClientSafePipeHandle);
        _serverWrites = new AnonymousPipeServerStream(PipeDirection.Out);
        _serverOutput = new AnonymousPipeClientStream(PipeDirection.In, _serverWrites.ClientSafePipeHandle);
        _toServer = _clientWrites;
        _fromServer = _serverOutput;
        var server = new LanguageServer(_serverInput, _serverWrites, options ?? new ServerOptions { DiagnosticsDelay = TimeSpan.FromMilliseconds(20) });
        _serverTask = server.RunAsync();
        _ = Task.Factory.StartNew(ReadLoop, TaskCreationOptions.LongRunning);
    }

    public Task<int> ServerExit => _serverTask;

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var length = -1;
                while (true)
                {
                    var line = ReadLine();
                    if (line == null) return;
                    if (line.Length == 0) break;
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line["Content-Length:".Length..].Trim());
                }
                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = _fromServer.Read(body, read, length - read);
                    if (n <= 0) return;
                    read += n;
                }
                var message = JsonDocument.Parse(body).RootElement;
                if (message.TryGetProperty("method", out _))
                {
                    if (message.TryGetProperty("id", out var requestId))
                    {
                        Send($$$"""{"jsonrpc":"2.0","id":{{{requestId.GetRawText()}}},"result":null}""");
                        continue;
                    }
                    lock (_gate)
                    {
                        _notifications.Add(message);
                        Monitor.PulseAll(_gate);
                    }
                }
                else if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                {
                    if (_pending.TryRemove(id.GetInt32(), out var tcs)) tcs.SetResult(message);
                    else
                        lock (_gate)
                        {
                            _notifications.Add(message);
                            Monitor.PulseAll(_gate);
                        }
                }
                else
                {
                    lock (_gate)
                    {
                        _notifications.Add(message);
                        Monitor.PulseAll(_gate);
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string? ReadLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = _fromServer.ReadByte();
            if (b < 0) return null;
            if (b == '\n') return sb.ToString();
            if (b != '\r') sb.Append((char)b);
        }
    }

    public void Send(string json) => WriteRaw(Frame(json));

    public static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return [.. Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n"), .. body];
    }

    public void WriteRaw(byte[] bytes)
    {
        lock (_toServer)
        {
            _toServer.Write(bytes, 0, bytes.Length);
            _toServer.Flush();
        }
    }

    public void Notify(string method, string paramsJson = "{}") =>
        Send($$$"""{"jsonrpc":"2.0","method":"{{{method}}}","params":{{{paramsJson}}}}""");

    /// <summary>Sends a request and returns the whole response message (result or error).</summary>
    public async Task<JsonElement> RequestRaw(string method, string paramsJson = "{}")
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        Send($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{{{paramsJson}}}}""");
        return await tcs.Task.WaitAsync(Timeout);
    }

    /// <summary>Sends a request and returns its <c>result</c> (fails the test on an error response).</summary>
    public async Task<JsonElement> Request(string method, string paramsJson = "{}")
    {
        var response = await RequestRaw(method, paramsJson);
        if (response.TryGetProperty("error", out var error)) Assert.Fail($"{method} failed: {error}");
        return response.GetProperty("result");
    }

    public async Task<JsonElement> Initialize(string? rootPath = null)
    {
        var root = rootPath == null ? "null" : $"\"{new Uri(rootPath).AbsoluteUri}\"";
        var folders = rootPath == null ? "null" : $$$"""[{"uri":{{{root}}},"name":"w"}]""";
        var result = await Request("initialize", "{\"processId\":null,\"rootUri\":" + root + ",\"workspaceFolders\":" + folders + ",\"capabilities\":{\"workspace\":{\"didChangeWatchedFiles\":{\"dynamicRegistration\":true}}}}");
        Notify("initialized");
        return result;
    }

    public int Mark()
    {
        lock (_gate) return _notifications.Count;
    }

    public JsonElement WaitForNotification(string method, Func<JsonElement, bool> predicate, int from = 0)
    {
        var deadline = DateTime.UtcNow + Timeout;
        lock (_gate)
        {
            var index = from;
            while (true)
            {
                for (; index < _notifications.Count; index++)
                {
                    var n = _notifications[index];
                    if (n.TryGetProperty("method", out var m) && m.GetString() == method && n.TryGetProperty("params", out var p) && predicate(p)) return p;
                }
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"No matching {method} notification");
                Monitor.Wait(_gate, remaining);
            }
        }
    }

    public JsonElement WaitForError(int code, int from = 0)
    {
        var deadline = DateTime.UtcNow + Timeout;
        lock (_gate)
        {
            var index = from;
            while (true)
            {
                for (; index < _notifications.Count; index++)
                {
                    var n = _notifications[index];
                    if (n.TryGetProperty("error", out var e) && e.GetProperty("code").GetInt32() == code) return n;
                }
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"No error response with code {code}");
                Monitor.Wait(_gate, remaining);
            }
        }
    }

    public int CountDiagnosticNotifications(string uri)
    {
        lock (_gate)
            return _notifications.Count(n => n.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics" && n.GetProperty("params").GetProperty("uri").GetString() == uri);
    }

    public JsonElement WaitForDiagnostics(string uri, Func<JsonElement, bool>? predicate = null, int from = 0) =>
        WaitForNotification("textDocument/publishDiagnostics", p => p.GetProperty("uri").GetString() == uri && (predicate?.Invoke(p.GetProperty("diagnostics")) ?? true), from);

    public static string Uri(string path) => new Uri(path).AbsoluteUri;

    public void Open(string uri, string text, int version = 1) =>
        Notify("textDocument/didOpen", $$$"""{"textDocument":{"uri":"{{{uri}}}","languageId":"caladesi","version":{{{version}}},"text":{{{JsonSerializer.Serialize(text)}}}}}""");

    public void Change(string uri, string text, int version) =>
        Notify("textDocument/didChange", $$$"""{"textDocument":{"uri":"{{{uri}}}","version":{{{version}}}},"contentChanges":[{"text":{{{JsonSerializer.Serialize(text)}}}}]}""");

    public static string Position(string uri, int line, int character) =>
        $$$"""{"textDocument":{"uri":"{{{uri}}}"},"position":{"line":{{{line}}},"character":{{{character}}}}}""";

    public async Task Shutdown()
    {
        await Request("shutdown");
        Notify("exit");
        Assert.AreEqual(0, await _serverTask.WaitAsync(Timeout));
    }

    public void Dispose()
    {
        try { _clientWrites.Dispose(); } catch (IOException) { }
        try { _serverWrites.Dispose(); } catch (IOException) { }
        _serverInput.Dispose();
        _serverOutput.Dispose();
    }
}

/// <summary>Source text with a <c>|</c> cursor marker.</summary>
internal readonly record struct Marked(string Text, int Line, int Character)
{
    public static Marked Parse(string marked)
    {
        var index = marked.IndexOf('|');
        Assert.IsGreaterThanOrEqualTo(0, index, "missing | marker");
        var text = marked.Remove(index, 1);
        var line = text.AsSpan(0, index).Count('\n');
        var lineStart = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        return new Marked(text, line, index - lineStart);
    }
}
