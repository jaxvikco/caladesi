using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Caladesi.Compiler.Tests.SelfHostCli;

[TestClass]
public sealed class CliParityTests
{
    private sealed record Result(int ExitCode, string Stdout, string Stderr);

    private static string FindCli(bool selfHost)
    {
        string project = selfHost ? "Caladesi.Cli.SelfHost" : "Caladesi.Cli";
        string file = selfHost ? "calac-selfhost.dll" : "calac.dll";
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string normal = Path.Join(directory.FullName, "src", project, "bin", configuration, "net10.0", file);
            string artifacts = Path.Join(directory.FullName, project, configuration.ToLowerInvariant(), file);
            if (File.Exists(artifacts))
            {
                return artifacts;
            }

            if (File.Exists(normal))
            {
                return normal;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Build {project} before running CLI parity tests.");
    }

    private static Result Run(bool selfHost, IEnumerable<string> arguments)
        => RunCompiler(FindCli(selfHost), arguments);

    private static Result RunCompiler(string cli, IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(cli);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("CLI did not exit within one minute.");
        }

        return new Result(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static Result AssertParity(IEnumerable<string> arguments, int exitCode)
    {
        string[] args = arguments.ToArray();
        Result bootstrap = Run(selfHost: false, args);
        Result port = Run(selfHost: true, args);
        Assert.AreEqual(exitCode, bootstrap.ExitCode, bootstrap.Stderr);
        Assert.AreEqual(bootstrap, port);
        return port;
    }

    private static void InWorkspace(Action<string> test)
    {
        // Keep test artifacts beside the test binary, not in the system temporary directory.
        string directory = Path.Join(AppContext.BaseDirectory, "cli-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string[] CompileArguments(string directory, string source, params string[] options)
    {
        string path = Path.Join(directory, "input.cala");
        File.WriteAllText(path, source);
        return [
            "-out:" + Path.Join(directory, "app.dll"),
            "-projectdir:" + directory,
            .. TestReferences.Paths.Select(path => "-ref:" + path),
            "-ref:" + typeof(Runtime.CaladesiUnionAttribute).Assembly.Location,
            .. options,
            path,
        ];
    }

    [TestMethod]
    public void NativeTooling_DoesNotReferenceBootstrapCompilerOrRuntime()
    {
        string cli = FindCli(selfHost: true);
        string directory = Path.GetDirectoryName(cli)!;
        foreach (string assembly in new[] { cli, Path.Join(directory, "Caladesi.Compiler.SelfHost.dll"), Path.Join(directory, "Caladesi.Runtime.SelfHost.dll") })
        {
            using FileStream stream = File.OpenRead(assembly);
            using PEReader reader = new(stream);
            MetadataReader metadata = reader.GetMetadataReader();
            string[] references = metadata.AssemblyReferences
                .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
            CollectionAssert.DoesNotContain(references, "Caladesi.Compiler", assembly);
            CollectionAssert.DoesNotContain(references, "Caladesi.Runtime", assembly);
            if (assembly == cli)
            {
                CollectionAssert.Contains(references, "Caladesi.Compiler.SelfHost");
                Assert.IsTrue(metadata.TypeReferences.Any(handle =>
                {
                    TypeReference type = metadata.GetTypeReference(handle);
                    return metadata.GetString(type.Namespace) == "Caladesi.Compiler" &&
                        metadata.GetString(type.Name) == "Compilation" &&
                        type.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                        metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == "Caladesi.Compiler.SelfHost";
                }), "The native CLI must invoke the native Compilation implementation.");
            }
        }
    }

    [TestMethod]
    public void NativeCli_CompilesAndRunsWithoutBootstrapAssemblies()
    {
        InWorkspace(directory =>
        {
            string sourceDirectory = Path.GetDirectoryName(FindCli(selfHost: true))!;
            string tools = Path.Join(directory, "native-tools");
            Directory.CreateDirectory(tools);
            foreach (string file in new[] { "calac-selfhost.dll", "calac-selfhost.deps.json", "calac-selfhost.runtimeconfig.json",
                "Caladesi.Compiler.SelfHost.dll", "Caladesi.Runtime.SelfHost.dll" })
            {
                File.Copy(Path.Join(sourceDirectory, file), Path.Join(tools, file));
            }

            string source = Path.Join(directory, "input.cala");
            string output = Path.Join(directory, "native-app.dll");
            File.WriteAllText(source, "function main(): int { const value: int | string = 42; Console.WriteLine(value); return 42; }");
            Result result = RunCompiler(Path.Join(tools, "calac-selfhost.dll"),
                ["-out:" + output, "-projectdir:" + directory,
                    .. TestReferences.Paths.Select(path => "-ref:" + path),
                    "-ref:" + Path.Join(tools, "Caladesi.Runtime.SelfHost.dll"), source]);
            Assert.AreEqual(0, result.ExitCode, result.Stderr);
            Assert.AreEqual("", result.Stdout);
            Assert.AreEqual("", result.Stderr);
            Assert.IsTrue(File.Exists(output));
            using (FileStream stream = File.OpenRead(output))
            using (PEReader reader = new(stream))
            {
                MetadataReader metadata = reader.GetMetadataReader();
                string[] references = metadata.AssemblyReferences
                    .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
                CollectionAssert.Contains(references, "Caladesi.Runtime.SelfHost");
                CollectionAssert.DoesNotContain(references, "Caladesi.Runtime");
            }

            File.Copy(Path.Join(tools, "calac-selfhost.runtimeconfig.json"), Path.Join(directory, "native-app.runtimeconfig.json"));
            File.Copy(Path.Join(tools, "Caladesi.Runtime.SelfHost.dll"), Path.Join(directory, "Caladesi.Runtime.SelfHost.dll"));
            Result program = RunCompiler(output, []);
            Assert.AreEqual(42, program.ExitCode, program.Stderr);
            Assert.AreEqual("42" + Environment.NewLine, program.Stdout);
            Assert.AreEqual("", program.Stderr);
        });
    }

    [TestMethod]
    public void Version_PrecedesOptionErrors()
    {
        Result result = AssertParity(["--VERSION", "-not-an-option", "-version:invalid"], 0);
        StringAssert.StartsWith(result.Stdout, "calac ");
        Assert.AreEqual("", result.Stderr);
    }

    [TestMethod]
    public void Arguments_ReportAllErrorsInOrder()
    {
        Result result = AssertParity(["-target:invalid", "-version:bad", "-unknown:value"], 1);
        StringAssert.Contains(result.Stderr, "unknown target 'invalid'");
        StringAssert.Contains(result.Stderr, "invalid version 'bad'");
        StringAssert.Contains(result.Stderr, "unknown option '-unknown:value'");
        Assert.AreEqual(3, result.Stderr.Split("CALC0001", StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    [DataRow("1.no")]
    [DataRow("-1.0")]
    [DataRow("1.2.3.4.5")]
    [DataRow("999999999999999999999999.0")]
    public void InvalidVersions_PreserveTryParseFailureBehavior(string version)
    {
        StringAssert.Contains(AssertParity(["-version:" + version], 1).Stderr, "invalid version");
    }

    [TestMethod]
    public void MissingInputs_PreserveUsageAndExitCodes()
    {
        StringAssert.Contains(AssertParity([], 1).Stderr, "CALC0002");
        StringAssert.Contains(AssertParity(["missing.cala"], 1).Stderr, "CALC0003");
        StringAssert.Contains(AssertParity(["-out:unused.dll", "missing.cala"], 1).Stderr, "CALC0004");
        StringAssert.Contains(AssertParity(["@missing-response-file.rsp"], 1).Stderr, "response file not found");
    }

    [TestMethod]
    public void Diagnostics_AndUnreadableReferencesMatch()
    {
        InWorkspace(directory =>
        {
            string[] arguments = CompileArguments(directory, "Console.WriteLine(unknownName);");
            Result result = AssertParity(arguments, 1);
            StringAssert.Contains(result.Stderr, "input.cala(");
            Assert.IsFalse(File.Exists(Path.Join(directory, "app.dll")));
            arguments = CompileArguments(directory, "Console.WriteLine(1);", "-ref:" + Path.Join(directory, "missing.dll"));
            StringAssert.Contains(AssertParity(arguments, 1).Stderr, "CAL0100");
            Assert.IsFalse(File.Exists(Path.Join(directory, "app.dll")));
        });
    }

    [TestMethod]
    public void WarningOptions_ControlSeveritySuppressionAndOutputStream()
    {
        InWorkspace(directory =>
        {
            const string source = "const x: int = 1; Console.WriteLine(x instanceof string);";
            Result warning = AssertParity(CompileArguments(directory, source, "-warnaserror:-", "-optimize:-"), 0);
            StringAssert.Contains(warning.Stdout, "warning CAL0241");
            Assert.AreEqual("", warning.Stderr);
            Result error = AssertParity(CompileArguments(directory, source, "-warnaserror"), 1);
            StringAssert.Contains(error.Stderr, "error CAL0241");
            Assert.AreEqual("", error.Stdout);
            Result suppressed = AssertParity(CompileArguments(directory, source, "-warnaserror", "-nowarn:CAL0241"), 0);
            Assert.AreEqual("", suppressed.Stdout);
            Assert.AreEqual("", suppressed.Stderr);
        });
    }

    [TestMethod]
    [DataRow("exe", true)]
    [DataRow("console", true)]
    [DataRow("library", false)]
    [DataRow("dll", false)]
    public void TargetAliases_SetThePeEntryPoint(string target, bool hasEntryPoint)
    {
        InWorkspace(directory =>
        {
            string source = hasEntryPoint ? "Console.WriteLine(1);" : "export class LibraryType {}";
            AssertParity(CompileArguments(directory, source, "-target:" + target), 0);
            using FileStream stream = File.OpenRead(Path.Join(directory, "app.dll"));
            using PEReader reader = new(stream);
            Assert.AreEqual(hasEntryPoint, reader.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress != 0);
        });
    }

    [TestMethod]
    public void NullableOption_PreservesBackendNullAssignmentDiagnostics()
    {
        InWorkspace(directory =>
        {
            const string source = "let text: string = null; Console.WriteLine(text);";
            AssertParity(CompileArguments(directory, source, "-nullable:enable"), 1);
            AssertParity(CompileArguments(directory, source, "-nullable:DISABLE"), 1);
        });
    }

    [TestMethod]
    public void TestEntryPointGeneration_AndHooksMatch()
    {
        InWorkspace(directory =>
        {
            Result result = AssertParity(CompileArguments(directory, "", "-testentry", "-testhook:Missing.Hook"), 1);
            Assert.IsNotEmpty(result.Stderr);
        });
    }

    [TestMethod]
    public void ResponseFiles_ExpandCommentsQuotesAndWhitespaceButNotNestedFiles()
    {
        InWorkspace(directory =>
        {
            string[] arguments = CompileArguments(directory, "Console.WriteLine(42);");
            string response = Path.Join(directory, "arguments with spaces.rsp");
            File.WriteAllLines(response, ["  # ignored", "", .. arguments.Select(argument => "  \"" + argument + "\"  ")]);
            AssertParity(["@" + response], 0);
            Assert.IsTrue(File.Exists(Path.Join(directory, "app.dll")));
            File.WriteAllText(response, "@nested.rsp");
            StringAssert.Contains(AssertParity(["-out:unused.dll", "@" + response], 1).Stderr, "CALC0004");
        });
    }

    [TestMethod]
    public void Compilation_EmitsRunnableAssemblyAndPdbWithAllOptions()
    {
        InWorkspace(directory =>
        {
            string output = Path.Join(directory, "app.dll");
            string pdb = Path.Join(directory, "app.pdb");
            string[] arguments = CompileArguments(directory,
                "function main(args: string[]): int { Console.WriteLine(new StringBuilder(\"hello\").ToString()); return args.Length + 6; }",
                "-target:console", "-assemblyname:Parity", "-rootnamespace:Port",
                "-globalimport:System.Text", "-globalimport:", "-define:CLI_PORT",
                "-nowarn:CAL0000;CAL0001,CAL0002", "-nullable:disable", "-warnaserror:false",
                "-optimize:true", "-testentry:false", "-testhook:Unused.Hook",
                "-targetframework:.NETCoreApp,Version=v10.0", "-targetframeworkdisplay:CLI parity",
                "-version:2.3.4.5", "-pdb:" + pdb);
            AssertParity(arguments, 0);
            using (FileStream stream = File.OpenRead(output))
            using (PEReader reader = new(stream))
            {
                MetadataReader metadata = reader.GetMetadataReader();
                AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
                Assert.AreEqual("Parity", metadata.GetString(assembly.Name));
                Assert.AreEqual(new Version(2, 3, 4, 5), assembly.Version);
            }

            using (FileStream stream = File.OpenRead(pdb))
            using (MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream))
            {
                Assert.IsNotEmpty(provider.GetMetadataReader().Documents);
            }

            File.WriteAllText(Path.Join(directory, "app.runtimeconfig.json"),
                """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
            ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(output);
            using Process process = Process.Start(start)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(6, process.ExitCode, stderr);
            Assert.AreEqual("hello" + Environment.NewLine, stdout);
        });
    }

    [TestMethod]
    public void OutputFailures_DistinguishIoErrorsFromInternalErrors()
    {
        InWorkspace(directory =>
        {
            string[] arguments = CompileArguments(directory, "", "-target:dll");
            Directory.CreateDirectory(Path.Join(directory, "blocked"));
            arguments[0] = "-out:" + Path.Join(directory, "blocked");
            StringAssert.Contains(AssertParity(arguments, 1).Stderr, "CALC0005");
            arguments[0] = "-out:";
            StringAssert.Contains(AssertParity(arguments, 2).Stderr, "CALC9999: internal compiler error: ArgumentException:");
            string blocker = Path.Join(directory, "blocker");
            File.WriteAllText(blocker, "");
            arguments[0] = "-out:" + Path.Join(blocker, "app.dll");
            StringAssert.Contains(AssertParity(arguments, 1).Stderr, "CALC0005");
        });
    }

    [TestMethod]
    public void OutputDirectories_AndPdbFailureSideEffectsMatch()
    {
        InWorkspace(directory =>
        {
            string output = Path.Join(directory, "nested", "app.dll");
            string[] arguments = CompileArguments(directory, "", "-target:library");
            arguments[0] = "-out:" + output;
            AssertParity(arguments, 0);
            Assert.IsTrue(File.Exists(output));
            string pdb = Path.Join(directory, "missing-directory", "app.pdb");
            Result result = AssertParity([.. arguments, "-pdb:" + pdb], 1);
            StringAssert.Contains(result.Stderr, "CALC0005");
            Assert.IsTrue(File.Exists(output));
            Assert.IsFalse(File.Exists(pdb));
        });
    }

    [TestMethod]
    public void ReadingADirectory_ReportsAnInputError()
    {
        InWorkspace(directory =>
        {
            StringAssert.Contains(AssertParity(["-out:unused.dll", directory], 1).Stderr, "CALC0004");
        });
    }
}
