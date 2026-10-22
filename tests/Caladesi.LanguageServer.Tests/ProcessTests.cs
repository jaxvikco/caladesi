using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Caladesi_LanguageServer_Tests;

/// <summary>Starts the real <c>cala-ls</c> executable and speaks the protocol over its stdio.</summary>
[TestClass]
public class ProcessTests
{
    private static string ServerDll => Path.Combine(AppContext.BaseDirectory, "cala-ls.dll");

    private static void Write(Stream stream, string json)
    {
        var frame = TestClient.Frame(json);
        stream.Write(frame, 0, frame.Length);
        stream.Flush();
    }

    private static JsonElement Read(Stream stream)
    {
        var length = -1;
        while (true)
        {
            var line = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) >= 0 && b != '\n') if (b != '\r') line.Append((char)b);
            if (b < 0) throw new EndOfStreamException("server closed stdout");
            if (line.Length == 0) break;
            if (line.ToString().StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.ToString()["Content-Length:".Length..].Trim());
        }
        var body = new byte[length];
        stream.ReadExactly(body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement ReadUntil(Stream stream, Func<JsonElement, bool> predicate)
    {
        for (var i = 0; i < 100; i++)
        {
            var message = Read(stream);
            if (predicate(message)) return message;
        }
        throw new TimeoutException("expected message not received");
    }

    [TestMethod]
    public void DotnetServerDllSpeaksLspOverStdio()
    {
        Assert.IsTrue(File.Exists(ServerDll), "cala-ls.dll missing next to the tests");
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(ServerDll);
        using var process = Process.Start(info)!;
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
        process.BeginErrorReadLine();
        var input = process.StandardInput.BaseStream;
        var output = process.StandardOutput.BaseStream;
        try
        {
            Write(input, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"rootUri":null,"capabilities":{}}}""");
            var init = ReadUntil(output, m => m.TryGetProperty("id", out var id) && id.GetInt32() == 1);
            Assert.AreEqual("cala-ls", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            Write(input, """{"jsonrpc":"2.0","method":"initialized","params":{}}""");

            const string uri = "file:///virtual/process.cala";
            Write(input, $$$$"""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"{{{{uri}}}}","languageId":"caladesi","version":1,"text":"const a = 1;\nlet b: any = a;\n"}}}""");
            var diagnostics = ReadUntil(output, m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics");
            Assert.AreEqual("CAL0201", diagnostics.GetProperty("params").GetProperty("diagnostics")[0].GetProperty("code").GetString());

            Write(input, $$$$"""{"jsonrpc":"2.0","id":2,"method":"textDocument/hover","params":{"textDocument":{"uri":"{{{{uri}}}}"},"position":{"line":0,"character":6}}}""");
            var hover = ReadUntil(output, m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 2);
            StringAssert.Contains(hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString(), "a");

            Write(input, """{"jsonrpc":"2.0","id":3,"method":"shutdown"}""");
            ReadUntil(output, m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 3);
            Write(input, """{"jsonrpc":"2.0","method":"exit"}""");
            Assert.IsTrue(process.WaitForExit(30_000), "server did not exit");
            Assert.AreEqual(0, process.ExitCode, stderr.ToString());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [TestMethod]
    public void ServerExitsWhenStdinCloses()
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(ServerDll);
        using var process = Process.Start(info)!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        Assert.IsTrue(process.WaitForExit(30_000));
        Assert.AreEqual(1, process.ExitCode);
    }
}
