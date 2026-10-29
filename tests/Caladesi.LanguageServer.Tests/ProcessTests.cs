using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Caladesi.LanguageServer.Tests;

/// <summary>Starts the real <c>cala-ls</c> executable and speaks the protocol over its stdio.</summary>
[TestClass]
public class ProcessTests
{
    private static string ServerDll => typeof(Caladesi.LanguageServer.LanguageServer).Assembly.Location;

    private static void Write(Stream stream, string json)
    {
        byte[] frame = TestClient.Frame(json);
        stream.Write(frame, 0, frame.Length);
        stream.Flush();
    }

    private static JsonElement Read(Stream stream)
    {
        int length = -1;
        while (true)
        {
            StringBuilder line = new();
            int b;
            while ((b = stream.ReadByte()) >= 0 && b != '\n')
            {
                if (b != '\r')
                {
                    _ = line.Append((char)b);
                }
            }

            if (b < 0)
            {
                throw new EndOfStreamException("server closed stdout");
            }

            if (line.Length == 0)
            {
                break;
            }

            if (line.ToString().StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line.ToString()["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        byte[] body = new byte[length];
        stream.ReadExactly(body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement ReadUntil(Stream stream, Func<JsonElement, bool> predicate)
    {
        for (int i = 0; i < 100; i++)
        {
            JsonElement message = Read(stream);
            if (predicate(message))
            {
                return message;
            }
        }

        throw new TimeoutException("expected message not received");
    }

    [TestMethod]
    public void DotnetServerDllSpeaksLspOverStdio()
    {
        Assert.IsTrue(File.Exists(ServerDll), "language server assembly missing next to the tests");
        ProcessStartInfo info = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(ServerDll);
        using Process process = Process.Start(info)!;
        StringBuilder stderr = new();
        process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
        process.BeginErrorReadLine();
        Stream input = process.StandardInput.BaseStream;
        Stream output = process.StandardOutput.BaseStream;
        try
        {
            Write(input, /*lang=json,strict*/ """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"rootUri":null,"capabilities":{}}}""");
            JsonElement init = ReadUntil(output, m => m.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 1);
            Assert.AreEqual("cala-ls", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            Write(input, /*lang=json,strict*/ """{"jsonrpc":"2.0","method":"initialized","params":{}}""");

            const string uri = "file:///virtual/process.cala";
            Write(input, $$$$"""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"{{{{uri}}}}","languageId":"caladesi","version":1,"text":"const a = 1;\nlet b: any = a;\n"}}}""");
            JsonElement diagnostics = ReadUntil(output, m => m.TryGetProperty("method", out JsonElement method) && method.GetString() == "textDocument/publishDiagnostics");
            Assert.AreEqual("CAL0201", diagnostics.GetProperty("params").GetProperty("diagnostics")[0].GetProperty("code").GetString());

            Write(input, $$$$"""{"jsonrpc":"2.0","id":2,"method":"textDocument/hover","params":{"textDocument":{"uri":"{{{{uri}}}}"},"position":{"line":0,"character":6}}}""");
            JsonElement hover = ReadUntil(output, m => m.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 2);
            StringAssert.Contains(hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString(), "a");

            Write(input, /*lang=json,strict*/ """{"jsonrpc":"2.0","id":3,"method":"shutdown"}""");
            _ = ReadUntil(output, m => m.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 3);
            Write(input, /*lang=json,strict*/ """{"jsonrpc":"2.0","method":"exit"}""");
            Assert.IsTrue(process.WaitForExit(30_000), "server did not exit");
            Assert.AreEqual(0, process.ExitCode, stderr.ToString());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [TestMethod]
    public void ServerExitsWhenStdinCloses()
    {
        ProcessStartInfo info = new("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(ServerDll);
        using Process process = Process.Start(info)!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        Assert.IsTrue(process.WaitForExit(30_000));
        Assert.AreEqual(1, process.ExitCode);
    }
}
