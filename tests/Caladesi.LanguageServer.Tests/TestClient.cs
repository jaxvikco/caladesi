using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Caladesi.LanguageServer.Tests;

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
    private readonly Lock _writeGate = new();
    private int _nextId;

    public TestClient(ServerOptions? options = null)
    {
        _clientWrites = new AnonymousPipeServerStream(PipeDirection.Out);
        _serverInput = new AnonymousPipeClientStream(PipeDirection.In, _clientWrites.ClientSafePipeHandle);
        _serverWrites = new AnonymousPipeServerStream(PipeDirection.Out);
        _serverOutput = new AnonymousPipeClientStream(PipeDirection.In, _serverWrites.ClientSafePipeHandle);
        _toServer = _clientWrites;
        _fromServer = _serverOutput;
        LanguageServer server = new(_serverInput, _serverWrites, options ?? new ServerOptions { DiagnosticsDelay = TimeSpan.FromMilliseconds(20) });
        ServerExit = server.RunAsync();
        _ = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public Task<int> ServerExit { get; }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                int length = -1;
                while (true)
                {
                    string? line = ReadLine();
                    if (line == null)
                    {
                        return;
                    }

                    if (line.Length == 0)
                    {
                        break;
                    }

                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                byte[] body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = _fromServer.Read(body, read, length - read);
                    if (n <= 0)
                    {
                        return;
                    }

                    read += n;
                }

                JsonElement message = JsonDocument.Parse(body).RootElement;
                if (message.TryGetProperty("method", out _))
                {
                    if (message.TryGetProperty("id", out JsonElement requestId))
                    {
                        Send($$$"""{"jsonrpc":"2.0","id":{{{requestId.GetRawText()}}},"result":null}""");
                        continue;
                    }

                    AddNotification(message);
                }
                else if (message.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number)
                {
                    if (_pending.TryRemove(id.GetInt32(), out TaskCompletionSource<JsonElement>? tcs))
                    {
                        tcs.SetResult(message);
                    }
                    else
                    {
                        AddNotification(message);
                    }
                }
                else
                {
                    AddNotification(message);
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

    private void AddNotification(JsonElement message)
    {
        lock (_gate)
        {
            _notifications.Add(message);
            Monitor.PulseAll(_gate);
        }
    }

    private string? ReadLine()
    {
        StringBuilder sb = new();
        while (true)
        {
            int b = _fromServer.ReadByte();
            if (b < 0)
            {
                return null;
            }

            if (b == '\n')
            {
                return sb.ToString();
            }

            if (b != '\r')
            {
                _ = sb.Append((char)b);
            }
        }
    }

    public void Send(string json)
    {
        WriteRaw(Frame(json));
    }

    public static byte[] Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        return [.. Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n"), .. body];
    }

    public void WriteRaw(byte[] bytes)
    {
        lock (_writeGate)
        {
            _toServer.Write(bytes, 0, bytes.Length);
            _toServer.Flush();
        }
    }

    public void Notify(string method, string paramsJson = "{}")
    {
        Send($$$"""{"jsonrpc":"2.0","method":"{{{method}}}","params":{{{paramsJson}}}}""");
    }

    /// <summary>Sends a request and returns the whole response message (result or error).</summary>
    public async Task<JsonElement> RequestRaw(string method, string paramsJson = "{}")
    {
        return await RequestRawWithId(Interlocked.Increment(ref _nextId), method, paramsJson).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="RequestRaw"/> with an id chosen by the caller (to reuse or cancel it).</summary>
    public async Task<JsonElement> RequestRawWithId(int id, string method, string paramsJson = "{}")
    {
        TaskCompletionSource<JsonElement> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        Send($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{{{paramsJson}}}}""");
        return await tcs.Task.WaitAsync(Timeout).ConfigureAwait(false);
    }

    /// <summary>Sends a request and returns its <c>result</c> (fails the test on an error response).</summary>
    public async Task<JsonElement> Request(string method, string paramsJson = "{}")
    {
        JsonElement response = await RequestRaw(method, paramsJson).ConfigureAwait(false);
        if (response.TryGetProperty("error", out JsonElement error))
        {
            Assert.Fail($"{method} failed: {error}");
        }

        return response.GetProperty("result");
    }

    public async Task<JsonElement> Initialize(string? rootPath = null)
    {
        string root = rootPath == null ? "null" : $"\"{new Uri(rootPath).AbsoluteUri}\"";
        string folders = rootPath == null ? "null" : $$$"""[{"uri":{{{root}}},"name":"w"}]""";
        JsonElement result = await Request("initialize", "{\"processId\":null,\"rootUri\":" + root + ",\"workspaceFolders\":" + folders + ",\"capabilities\":{\"workspace\":{\"didChangeWatchedFiles\":{\"dynamicRegistration\":true}}}}").ConfigureAwait(false);
        Notify("initialized");
        return result;
    }

    public int Mark()
    {
        lock (_gate)
        {
            return _notifications.Count;
        }
    }

    public JsonElement WaitForNotification(string method, Func<JsonElement, bool> predicate, int from = 0)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        lock (_gate)
        {
            int index = from;
            while (true)
            {
                for (; index < _notifications.Count; index++)
                {
                    JsonElement n = _notifications[index];
                    if (n.TryGetProperty("method", out JsonElement m) && m.GetString() == method && n.TryGetProperty("params", out JsonElement p) && predicate(p))
                    {
                        return p;
                    }
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"No matching {method} notification");
                }

                _ = Monitor.Wait(_gate, remaining);
            }
        }
    }

    public JsonElement WaitForError(int code, int from = 0)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        lock (_gate)
        {
            int index = from;
            while (true)
            {
                for (; index < _notifications.Count; index++)
                {
                    JsonElement n = _notifications[index];
                    if (n.TryGetProperty("error", out JsonElement e) && e.GetProperty("code").GetInt32() == code)
                    {
                        return n;
                    }
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"No error response with code {code}");
                }

                _ = Monitor.Wait(_gate, remaining);
            }
        }
    }

    public int CountDiagnosticNotifications(string uri)
    {
        lock (_gate)
        {
            return _notifications.Count(n => n.TryGetProperty("method", out JsonElement m) && m.GetString() == "textDocument/publishDiagnostics" && n.GetProperty("params").GetProperty("uri").GetString() == uri);
        }
    }

    public JsonElement WaitForDiagnostics(string uri, Func<JsonElement, bool>? predicate = null, int from = 0)
    {
        return WaitForNotification("textDocument/publishDiagnostics", p => p.GetProperty("uri").GetString() == uri && (predicate?.Invoke(p.GetProperty("diagnostics")) ?? true), from);
    }

    public static string Uri(string path)
    {
        return new Uri(path).AbsoluteUri;
    }

    public void Open(string uri, string text, int version = 1)
    {
        Notify("textDocument/didOpen", $$$"""{"textDocument":{"uri":"{{{uri}}}","languageId":"caladesi","version":{{{version}}},"text":{{{JsonSerializer.Serialize(text)}}}}}""");
    }

    public void Change(string uri, string text, int version)
    {
        Notify("textDocument/didChange", $$$"""{"textDocument":{"uri":"{{{uri}}}","version":{{{version}}}},"contentChanges":[{"text":{{{JsonSerializer.Serialize(text)}}}}]}""");
    }

    public static string Position(string uri, int line, int character)
    {
        return $$$"""{"textDocument":{"uri":"{{{uri}}}"},"position":{"line":{{{line}}},"character":{{{character}}}}}""";
    }

    public async Task Shutdown()
    {
        _ = await Request("shutdown").ConfigureAwait(false);
        Notify("exit");
        Assert.AreEqual(0, await ServerExit.WaitAsync(Timeout).ConfigureAwait(false));
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
        int index = marked.IndexOf('|');
        Assert.IsGreaterThanOrEqualTo(0, index, "missing | marker");
        string text = marked.Remove(index, 1);
        int line = text.AsSpan(0, index).Count('\n');
        int lineStart = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        return new Marked(text, line, index - lineStart);
    }
}
