using System.Text.Json;
using Caladesi.Compiler;
using Caladesi.LanguageServer.Projects;

namespace Caladesi_LanguageServer_Tests;

[TestClass]
public class ProjectTests
{
    private const string MsBuildOutput = """
        warning NU1701: something printed before the JSON
        {
          "Properties": {
            "RootNamespace": "Acme.App",
            "AssemblyName": "acme",
            "OutputType": "Library",
            "TargetFramework": "net10.0",
            "TargetFrameworkMoniker": ".NETCoreApp,Version=v10.0",
            "Nullable": "disable",
            "TreatWarningsAsErrors": "true",
            "NoWarn": "CAL0001;CAL0002",
            "DefineConstants": "NET;DEBUG",
            "Version": "1.2.3-beta"
          },
          "Items": {
            "Compile": [
              { "Identity": "Program.cala", "FullPath": "/proj/Program.cala" },
              { "Identity": "sub/lib.cala", "FullPath": "/proj/sub/lib.cala" }
            ],
            "CaladesiGlobalImport": [ { "Identity": "System.Text" } ],
            "ReferencePathWithRefAssemblies": [
              { "Identity": "/packs/System.Runtime.dll", "FullPath": "/packs/System.Runtime.dll" },
              { "Identity": "relative/Caladesi.Runtime.dll" }
            ]
          }
        }
        """;

    [TestMethod]
    public void ParsesMsBuildJson()
    {
        var data = MsBuildProjectData.Parse(MsBuildOutput, "/proj");
        Assert.IsNotNull(data);
        CollectionAssert.AreEqual(new[] { "/proj/Program.cala", "/proj/sub/lib.cala" }, data.Compile);
        CollectionAssert.AreEqual(new[] { "System.Text" }, data.GlobalImports);
        Assert.HasCount(2, data.References);
        Assert.AreEqual("/packs/System.Runtime.dll", data.References[0]);
        Assert.AreEqual(Path.GetFullPath("/proj/relative/Caladesi.Runtime.dll"), data.References[1]);

        var options = data.ToCompilationOptions("/proj/acme.calaproj");
        Assert.AreEqual("acme", options.AssemblyName);
        Assert.AreEqual("Acme.App", options.RootNamespace);
        Assert.AreEqual(OutputKind.DynamicallyLinkedLibrary, options.OutputKind);
        Assert.IsFalse(options.NullableEnabled);
        Assert.IsTrue(options.WarningsAsErrors);
        CollectionAssert.AreEqual(new[] { "CAL0001", "CAL0002" }, options.NoWarn.ToArray());
        CollectionAssert.AreEqual(new[] { "NET", "DEBUG" }, options.Defines.ToArray());
        CollectionAssert.AreEqual(new[] { "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks", "System.Text" }, options.GlobalImports.ToArray());
        Assert.AreEqual(new Version(1, 2, 3, 0), options.AssemblyVersion);
        Assert.AreEqual("/proj", options.ProjectDirectory);
        Assert.IsTrue(options.RecordSemanticInfo);
    }

    [TestMethod]
    public void ParseRejectsGarbageAndFillsDefaults()
    {
        Assert.IsNull(MsBuildProjectData.Parse("MSBUILD : error MSB1009: Project file does not exist.", "/p"));
        Assert.IsNull(MsBuildProjectData.Parse("{ not json", "/p"));
        Assert.IsNull(MsBuildProjectData.Parse("[1,2]", "/p"));

        var data = MsBuildProjectData.Parse("{\"Properties\":{},\"Items\":{}}", "/p");
        Assert.IsNotNull(data);
        var options = data.ToCompilationOptions("/p/demo.calaproj");
        Assert.AreEqual("demo", options.AssemblyName);
        Assert.AreEqual("demo", options.RootNamespace);
        Assert.AreEqual(OutputKind.ConsoleApplication, options.OutputKind);
        Assert.IsTrue(options.NullableEnabled);
    }

    [TestMethod]
    public async Task ProjectModeSharesTheWholeProject()
    {
        var env = await ProjectEnvironment.EnsureAsync();
        var dir = env.CreateProject("shared", "Exe",
            ("Program.cala", "import { twice } from \"./lib\";\nConsole.WriteLine(twice(21));\n"),
            ("lib.cala", "export function twice(x: int): int { return x * 2; }\n"),
            ("bad.cala", "for (const k in [1]) {}\n"));
        var program = Path.Combine(dir, "Program.cala");
        var lib = Path.Combine(dir, "lib.cala");
        var bad = Path.Combine(dir, "bad.cala");
        var programUri = TestClient.Uri(program);
        var libUri = TestClient.Uri(lib);
        var badUri = TestClient.Uri(bad);

        using var client = new TestClient(env.ServerOptions());
        await client.Initialize(dir);
        client.Open(programUri, File.ReadAllText(program));

        var badDiagnostics = client.WaitForDiagnostics(badUri, d => d.GetArrayLength() > 0);
        Assert.AreEqual("CAL0021", badDiagnostics.GetProperty("diagnostics")[0].GetProperty("code").GetString());

        var call = Marked.Parse("import { twice } from \"./lib\";\nConsole.WriteLine(twi|ce(21));\n");
        var position = TestClient.Position(programUri, call.Line, call.Character);

        var hover = await client.Request("textDocument/hover", position);
        StringAssert.Contains(hover.GetProperty("contents").GetProperty("value").GetString(), "function twice(x: int): int");

        var definition = await client.Request("textDocument/definition", position);
        Assert.AreEqual(libUri, definition.GetProperty("uri").GetString());

        var references = await client.Request("textDocument/references", position.Replace("}}", "},\"context\":{\"includeDeclaration\":true}}"));
        CollectionAssert.AreEquivalent(
            new[] { libUri, programUri, programUri },
            references.EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToArray());

        var rename = await client.Request("textDocument/rename", position.Replace("}}", "},\"newName\":\"doubled\"}"));
        var changes = rename.GetProperty("changes");
        Assert.AreEqual(1, changes.GetProperty(libUri).GetArrayLength());
        Assert.AreEqual(2, changes.GetProperty(programUri).GetArrayLength());

        var mark = client.Mark();
        File.WriteAllText(bad, "export const fine = 1;\n");
        client.Notify("workspace/didChangeWatchedFiles", $$$"""{"changes":[{"uri":"{{{badUri}}}","type":2}]}""");
        client.WaitForDiagnostics(badUri, d => d.GetArrayLength() == 0, mark);

        mark = client.Mark();
        var extra = Path.Combine(dir, "extra.cala");
        File.WriteAllText(extra, "for (const k in [1]) {}\n");
        client.Notify("workspace/didChangeWatchedFiles", $$$"""{"changes":[{"uri":"{{{TestClient.Uri(extra)}}}","type":1}]}""");
        client.WaitForDiagnostics(TestClient.Uri(extra), d => d.GetArrayLength() > 0, mark);

        await client.Shutdown();
    }

    [TestMethod]
    public async Task EditedOpenDocumentOverridesDiskInProject()
    {
        var env = await ProjectEnvironment.EnsureAsync();
        var dir = env.CreateProject("overlay", "Exe",
            ("Program.cala", "import { twice } from \"./lib\";\nConsole.WriteLine(twice(2));\n"),
            ("lib.cala", "export function twice(x: int): int { return x * 2; }\n"));
        var program = Path.Combine(dir, "Program.cala");
        var lib = Path.Combine(dir, "lib.cala");
        var programUri = TestClient.Uri(program);
        var libUri = TestClient.Uri(lib);

        using var client = new TestClient(env.ServerOptions());
        await client.Initialize(dir);
        client.Open(programUri, File.ReadAllText(program));
        client.Open(libUri, File.ReadAllText(lib));

        var mark = client.Mark();
        client.Change(libUri, "export function twice(x: string): int { return 2; }\n", 2);
        var diagnostics = client.WaitForDiagnostics(programUri, d => d.GetArrayLength() > 0, mark);
        Assert.AreEqual(1, diagnostics.GetProperty("diagnostics")[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual("export function twice(x: int): int { return x * 2; }\n", File.ReadAllText(lib));

        mark = client.Mark();
        client.Change(libUri, "export function twice(x: int): int { return x * 2; }\n", 3);
        client.WaitForDiagnostics(programUri, d => d.GetArrayLength() == 0, mark);
        await client.Shutdown();
    }

    [TestMethod]
    public async Task BrokenProjectFallsBackToStandaloneMode()
    {
        var env = await ProjectEnvironment.EnsureAsync();
        var dir = Path.Combine(env.WorkRoot, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.calaproj"), "<Project><this is not xml");
        var file = Path.Combine(dir, "main.cala");
        File.WriteAllText(file, "const a = 1;\nif (a == 1) { }\n");
        var uri = TestClient.Uri(file);

        using var client = new TestClient(env.ServerOptions());
        await client.Initialize(dir);
        client.Open(uri, File.ReadAllText(file));

        var message = client.WaitForNotification("window/showMessage", p => p.GetProperty("message").GetString()!.Contains("standalone", StringComparison.Ordinal));
        StringAssert.Contains(message.GetProperty("message").GetString(), "app.calaproj");
        var diagnostics = client.WaitForDiagnostics(uri, d => d.GetArrayLength() > 0);
        Assert.AreEqual("CAL0023", diagnostics.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        var hover = await client.Request("textDocument/hover", TestClient.Position(uri, 1, 5));
        Assert.AreEqual(JsonValueKind.Object, hover.ValueKind);
        await client.Shutdown();
    }
}
