using System.Text;
using System.Text.Json;
using Caladesi.LanguageServer.Projects;
using Caladesi.LanguageServer.Protocol;

namespace Caladesi.LanguageServer.Tests;

[TestClass]
public class InteropTests
{
    [TestMethod]
    public void ProjectPropertyLookupPreservesValues()
    {
        MsBuildProjectData data = new();
        data.Properties.Add("AssemblyName", "PortedProject");
        Assert.AreEqual("PortedProject", data.Property("AssemblyName"));
        Assert.AreEqual("", data.Property("Missing"));
    }

    [TestMethod]
    public void TransportSkipsInvalidHeadersAndReadsUtf8Payload()
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"text":"🦎"}""");
        byte[] header = Encoding.ASCII.GetBytes("invalid\r\nContent-Length: nope\r\n\r\nContent-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n");
        using MemoryStream input = new();
        input.Write(header);
        input.Write(body);
        input.Position = 0;
        MessageReader reader = new(input);
        CollectionAssert.AreEqual(body, reader.ReadMessage());
        Assert.IsNull(reader.ReadMessage());
    }

    [TestMethod]
    public void WriterCountsUtf8BytesRatherThanCharacters()
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"text":"å"}""");
        using MemoryStream output = new();
        new MessageWriter(output).Write(body);
        output.Position = 0;
        CollectionAssert.AreEqual(body, new MessageReader(output).ReadMessage());
    }

    [TestMethod]
    public async Task StandaloneInlineUnionFindsRuntimeReference()
    {
        Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "Caladesi.Runtime.dll")));
        using TestClient client = new();
        await client.Initialize().ConfigureAwait(false);
        const string uri = "file:///virtual/selfhost-inline-union.cala";
        client.Open(uri, "const value: int | string = 42;\nlet invalid: any = value;\n");
        JsonElement diagnostics = client.WaitForDiagnostics(uri).GetProperty("diagnostics");
        Assert.AreEqual(1, diagnostics.GetArrayLength());
        Assert.AreEqual("CAL0201", diagnostics[0].GetProperty("code").GetString());
    }
}
