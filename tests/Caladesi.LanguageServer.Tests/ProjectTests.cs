using System.Text.Json;
using Caladesi.Compiler;
using Caladesi.LanguageServer.Projects;

namespace Caladesi.LanguageServer.Tests;

[TestClass]
public class ProjectTests
{
    private static readonly string[] s_expectedCompile = ["/proj/Program.cala", "/proj/sub/lib.cala"];
    private static readonly string[] s_expectedGlobalImports = ["System.Text"];
    private static readonly string[] s_expectedNoWarn = ["CAL0001", "CAL0002"];
    private static readonly string[] s_expectedDefines = ["NET", "DEBUG"];
    private static readonly string[] s_expectedAllImports = ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks", "System.Text"];

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
        MsBuildProjectData? data = MsBuildProjectData.Parse(MsBuildOutput, "/proj");
        Assert.IsNotNull(data);
        CollectionAssert.AreEqual(s_expectedCompile, data.Compile);
        CollectionAssert.AreEqual(s_expectedGlobalImports, data.GlobalImports);
        Assert.HasCount(2, data.References);
        Assert.AreEqual("/packs/System.Runtime.dll", data.References[0]);
        Assert.AreEqual(Path.GetFullPath("/proj/relative/Caladesi.Runtime.dll"), data.References[1]);

        CompilationOptions options = data.ToCompilationOptions("/proj/acme.calaproj");
        Assert.AreEqual("acme", options.AssemblyName);
        Assert.AreEqual("Acme.App", options.RootNamespace);
        Assert.AreEqual(OutputKind.DynamicallyLinkedLibrary, options.OutputKind);
        Assert.IsFalse(options.NullableEnabled);
        Assert.IsTrue(options.WarningsAsErrors);
        CollectionAssert.AreEqual(s_expectedNoWarn, options.NoWarn.ToArray());
        CollectionAssert.AreEqual(s_expectedDefines, options.Defines.ToArray());
        CollectionAssert.AreEqual(s_expectedAllImports, options.GlobalImports.ToArray());
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

        MsBuildProjectData? data = MsBuildProjectData.Parse(/*lang=json,strict*/ "{\"Properties\":{},\"Items\":{}}", "/p");
        Assert.IsNotNull(data);
        CompilationOptions options = data.ToCompilationOptions("/p/demo.calaproj");
        Assert.AreEqual("demo", options.AssemblyName);
        Assert.AreEqual("demo", options.RootNamespace);
        Assert.AreEqual(OutputKind.ConsoleApplication, options.OutputKind);
        Assert.IsTrue(options.NullableEnabled);
    }

    [TestMethod]
    public async Task ProjectModeSharesTheWholeProject()
    {
        ProjectEnvironment.Info env = await ProjectEnvironment.EnsureAsync().ConfigureAwait(false);
        string dir = env.CreateProject("shared", "Exe",
            ("Program.cala", "import { twice } from \"./lib\";\nConsole.WriteLine(twice(21));\n"),
            ("lib.cala", "export function twice(x: int): int { return x * 2; }\n"),
            ("bad.cala", "for (const k in [1]) {}\n"));
        string program = Path.Combine(dir, "Program.cala");
        string lib = Path.Combine(dir, "lib.cala");
        string bad = Path.Combine(dir, "bad.cala");
        string programUri = TestClient.Uri(program);
        string libUri = TestClient.Uri(lib);
        string badUri = TestClient.Uri(bad);

        using TestClient client = new(env.ServerOptions());
        _ = await client.Initialize(dir).ConfigureAwait(false);
        client.Open(programUri, await File.ReadAllTextAsync(program).ConfigureAwait(false));

        JsonElement badDiagnostics = client.WaitForDiagnostics(badUri, d => d.GetArrayLength() > 0);
        Assert.AreEqual("CAL0021", badDiagnostics.GetProperty("diagnostics")[0].GetProperty("code").GetString());

        Marked call = Marked.Parse("import { twice } from \"./lib\";\nConsole.WriteLine(twi|ce(21));\n");
        string position = TestClient.Position(programUri, call.Line, call.Character);

        JsonElement hover = await client.Request("textDocument/hover", position).ConfigureAwait(false);
        StringAssert.Contains(hover.GetProperty("contents").GetProperty("value").GetString(), "function twice(x: int): int");

        JsonElement definition = await client.Request("textDocument/definition", position).ConfigureAwait(false);
        Assert.AreEqual(libUri, definition.GetProperty("uri").GetString());

        JsonElement references = await client.Request("textDocument/references", position.Replace("}}", "},\"context\":{\"includeDeclaration\":true}}")).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(
            new[] { libUri, programUri, programUri },
            references.EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToArray());

        JsonElement rename = await client.Request("textDocument/rename", position.Replace("}}", "},\"newName\":\"doubled\"}")).ConfigureAwait(false);
        JsonElement changes = rename.GetProperty("changes");
        Assert.AreEqual(1, changes.GetProperty(libUri).GetArrayLength());
        Assert.AreEqual(2, changes.GetProperty(programUri).GetArrayLength());

        int mark = client.Mark();
        await File.WriteAllTextAsync(bad, "export const fine = 1;\n").ConfigureAwait(false);
        client.Notify("workspace/didChangeWatchedFiles", $$$"""{"changes":[{"uri":"{{{badUri}}}","type":2}]}""");
        _ = client.WaitForDiagnostics(badUri, d => d.GetArrayLength() == 0, mark);

        mark = client.Mark();
        string extra = Path.Combine(dir, "extra.cala");
        await File.WriteAllTextAsync(extra, "for (const k in [1]) {}\n").ConfigureAwait(false);
        client.Notify("workspace/didChangeWatchedFiles", $$$"""{"changes":[{"uri":"{{{TestClient.Uri(extra)}}}","type":1}]}""");
        _ = client.WaitForDiagnostics(TestClient.Uri(extra), d => d.GetArrayLength() > 0, mark);

        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task EditedOpenDocumentOverridesDiskInProject()
    {
        ProjectEnvironment.Info env = await ProjectEnvironment.EnsureAsync().ConfigureAwait(false);
        string dir = env.CreateProject("overlay", "Exe",
            ("Program.cala", "import { twice } from \"./lib\";\nConsole.WriteLine(twice(2));\n"),
            ("lib.cala", "export function twice(x: int): int { return x * 2; }\n"));
        string program = Path.Combine(dir, "Program.cala");
        string lib = Path.Combine(dir, "lib.cala");
        string programUri = TestClient.Uri(program);
        string libUri = TestClient.Uri(lib);

        using TestClient client = new(env.ServerOptions());
        _ = await client.Initialize(dir).ConfigureAwait(false);
        client.Open(programUri, await File.ReadAllTextAsync(program).ConfigureAwait(false));
        client.Open(libUri, await File.ReadAllTextAsync(lib).ConfigureAwait(false));

        int mark = client.Mark();
        client.Change(libUri, "export function twice(x: string): int { return 2; }\n", 2);
        JsonElement diagnostics = client.WaitForDiagnostics(programUri, d => d.GetArrayLength() > 0, mark);
        Assert.AreEqual(1, diagnostics.GetProperty("diagnostics")[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.AreEqual("export function twice(x: int): int { return x * 2; }\n", await File.ReadAllTextAsync(lib).ConfigureAwait(false));

        mark = client.Mark();
        client.Change(libUri, "export function twice(x: int): int { return x * 2; }\n", 3);
        _ = client.WaitForDiagnostics(programUri, d => d.GetArrayLength() == 0, mark);
        await client.Shutdown().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task BrokenProjectFallsBackToStandaloneMode()
    {
        ProjectEnvironment.Info env = await ProjectEnvironment.EnsureAsync().ConfigureAwait(false);
        string dir = Path.Combine(env.WorkRoot, "broken");
        _ = Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "app.calaproj"), "<Project><this is not xml").ConfigureAwait(false);
        string file = Path.Combine(dir, "main.cala");
        await File.WriteAllTextAsync(file, "const a = 1;\nlet b: any = a;\n").ConfigureAwait(false);
        string uri = TestClient.Uri(file);

        using TestClient client = new(env.ServerOptions());
        _ = await client.Initialize(dir).ConfigureAwait(false);
        client.Open(uri, await File.ReadAllTextAsync(file).ConfigureAwait(false));

        JsonElement message = client.WaitForNotification("window/showMessage", p => p.GetProperty("message").GetString()!.Contains("standalone", StringComparison.Ordinal));
        StringAssert.Contains(message.GetProperty("message").GetString(), "app.calaproj");
        JsonElement diagnostics = client.WaitForDiagnostics(uri, d => d.GetArrayLength() > 0);
        Assert.AreEqual("CAL0201", diagnostics.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        JsonElement hover = await client.Request("textDocument/hover", TestClient.Position(uri, 1, 13)).ConfigureAwait(false);
        Assert.AreEqual(JsonValueKind.Object, hover.ValueKind);
        await client.Shutdown().ConfigureAwait(false);
    }
}
