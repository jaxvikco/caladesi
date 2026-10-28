extern alias SelfHostBuild;

using System.Collections;
using System.Reflection;
using Caladesi.Build.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MigratedTask = SelfHostBuild::Caladesi.Build.Tasks.CaladesiCompile;

namespace Caladesi.Compiler.Tests.SelfHostBuild;

[TestClass]
public sealed class CaladesiCompileParityTests
{
    [TestMethod]
    public void Task_PublicContractAttributesDefaultsAndProtectedOverridesMatchBootstrap()
    {
        Type original = typeof(CaladesiCompile);
        Type migrated = typeof(MigratedTask);
        Assert.AreEqual(original.FullName, migrated.FullName);
        Assert.AreEqual(original.BaseType, migrated.BaseType);
        Assert.AreEqual(original.IsSealed, migrated.IsSealed);
        const BindingFlags publicFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        PropertyInfo[] expected = original.GetProperties(publicFlags);
        PropertyInfo[] actual = migrated.GetProperties(publicFlags);
        CollectionAssert.AreEquivalent(expected.Select(p => p.Name).ToArray(), actual.Select(p => p.Name).ToArray());
        CaladesiCompile bootstrap = new();
        MigratedTask selfHost = new();
        foreach (PropertyInfo property in expected)
        {
            PropertyInfo counterpart = migrated.GetProperty(property.Name)!;
            Assert.AreEqual(property.PropertyType, counterpart.PropertyType, property.Name);
            Assert.AreEqual(property.CanRead, counterpart.CanRead, property.Name);
            Assert.AreEqual(property.CanWrite, counterpart.CanWrite, property.Name);
            Assert.AreEqual(property.IsDefined(typeof(RequiredAttribute)), counterpart.IsDefined(typeof(RequiredAttribute)), property.Name);
            object? value = property.GetValue(bootstrap);
            object? other = counterpart.GetValue(selfHost);
            if (value is Array array)
            {
                CollectionAssert.AreEqual(array, (Array)other!, property.Name);
            }
            else
            {
                Assert.AreEqual(value, other, property.Name);
            }
        }

        const BindingFlags protectedFlags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (MethodInfo method in original.GetMethods(protectedFlags).Where(m => m.IsFamily))
        {
            MethodInfo counterpart = migrated.GetMethod(method.Name, protectedFlags)!;
            Assert.IsNotNull(counterpart, method.Name);
            Assert.AreEqual(method.ReturnType, counterpart.ReturnType, method.Name);
            Assert.AreEqual(method.IsFamily, counterpart.IsFamily, method.Name);
            Assert.AreEqual(method.IsVirtual, counterpart.IsVirtual, method.Name);
            Assert.AreEqual(method.GetBaseDefinition().DeclaringType, counterpart.GetBaseDefinition().DeclaringType, method.Name);
        }
    }

    [TestMethod]
    public void Task_DefaultAndCompleteResponseFilesMatchBootstrapByteForByte()
    {
        AssertResponses(new CaladesiCompile(), new MigratedTask());
        CaladesiCompile original = new()
        {
            CompilerPath = "directory with spaces/calac.dll",
            OutputAssembly = "directory with spaces/output.dll",
            PdbFile = "directory with spaces/output.pdb",
            OutputType = "wInExE",
            AssemblyName = "Task.Assembly",
            RootNamespace = "Task.Root",
            ProjectDirectory = "/project with spaces/\\\\/",
            References = [new TaskItem("/references/one.dll"), new TaskItem("/references with spaces/two.dll")],
            DefineConstants = "DEBUG;\n TRACE,\r\n\tOTHER  DEBUG",
            NullableContext = "DiSaBlE",
            TreatWarningsAsErrors = true,
            NoWarn = "CAL0001;\n CAL0002,\r\n\tCAL0003 ",
            GlobalImports = [new TaskItem("System"), new TaskItem("System.Collections.Generic")],
            Version = "2.3.4.5",
            TargetFrameworkMoniker = ".NETCoreApp,Version=v10.0",
            TargetFrameworkDisplayName = ".NET 10.0",
            Optimize = true,
            GenerateTestEntryPoint = true,
            TestHooks =
            [
                new TaskItem("hook", new Dictionary<string, string> { ["TypeFullName"] = "Example.Hook" }),
                new TaskItem("empty"),
                new TaskItem("hook2", new Dictionary<string, string> { ["TypeFullName"] = "Example.OtherHook" }),
            ],
            Sources = [new TaskItem(Path.GetFullPath("one.cala")), new TaskItem(Path.GetFullPath("with spaces/two.cala"))],
        };
        MigratedTask migrated = CopyParameters(original);
        AssertResponses(original, migrated);
        string response = Invoke<string>(migrated, "GenerateResponseFileCommands");
        Assert.EndsWith("\n", response);
        StringAssert.Contains(response, "-target:exe\n");
        StringAssert.Contains(response, "-projectdir:/project with spaces\n");
        StringAssert.Contains(response, "-define:DEBUG\n-define:TRACE\n-define:OTHER\n-define:DEBUG\n");
        StringAssert.Contains(response, "-nowarn:CAL0001;CAL0002;CAL0003\n");
        Assert.AreEqual("\"directory with spaces/calac.dll\"", Invoke<string>(migrated, "GenerateCommandLineCommands"));
    }

    [TestMethod]
    [DataRow("Exe", "exe", null, null)]
    [DataRow("EXE", "exe", "", "")]
    [DataRow("WinExe", "exe", "enable", "dotnet-custom")]
    [DataRow("Library", "library", "unexpected", "/host with spaces/dotnet")]
    [DataRow("", "library", "DISABLE", null)]
    [DataRow(null, "library", " ", null)]
    public void Task_OptionBranchesAndHostSelectionMatchBootstrap(string? outputType, string target, string? nullable, string? host)
    {
        CaladesiCompile original = new()
        {
            OutputType = outputType!,
            NullableContext = nullable,
            DotnetHostPath = host,
            DefineConstants = " \t\r\n;,",
            NoWarn = " \t\r\n;,",
            TestHooks = [],
        };
        MigratedTask migrated = CopyParameters(original);
        AssertResponses(original, migrated);
        StringAssert.Contains(Invoke<string>(migrated, "GenerateResponseFileCommands"), "-target:" + target + "\n");
        Assert.AreEqual(Invoke<string>(original, "GenerateFullPathToTool"), Invoke<string>(migrated, "GenerateFullPathToTool"));
        Assert.AreEqual("dotnet", Invoke<string>(migrated, "get_ToolName"));
        Assert.AreEqual(MessageImportance.Normal, Invoke<MessageImportance>(migrated, "get_StandardOutputLoggingImportance"));
        Assert.AreEqual(MessageImportance.High, Invoke<MessageImportance>(migrated, "get_StandardErrorLoggingImportance"));
    }

    [TestMethod]
    public void Task_MissingCompilerValidationAndAlreadyLoggedErrorsMatchBootstrap()
    {
        RecordingEngine originalEngine = new();
        RecordingEngine migratedEngine = new();
        CaladesiCompile original = new() { CompilerPath = Path.GetFullPath("missing-calac.dll"), BuildEngine = originalEngine };
        MigratedTask migrated = new() { CompilerPath = original.CompilerPath, BuildEngine = migratedEngine };
        Assert.IsFalse(Invoke<bool>(original, "ValidateParameters"));
        Assert.IsFalse(Invoke<bool>(migrated, "ValidateParameters"));
        CollectionAssert.AreEqual(originalEngine.Errors.Select(e => e.Message).ToArray(), migratedEngine.Errors.Select(e => e.Message).ToArray());
        Assert.HasCount(1, migratedEngine.Errors);
        Assert.IsFalse(Invoke<bool>(original, "HandleTaskExecutionErrors"));
        Assert.IsFalse(Invoke<bool>(migrated, "HandleTaskExecutionErrors"));
        Assert.HasCount(1, migratedEngine.Errors);
    }

    [TestMethod]
    [DataRow("export class Example {}", true)]
    [DataRow("export class Example { value: MissingType; }", false)]
    public void Task_ExecutesCompilerAndParsesCanonicalDiagnosticsLikeBootstrap(string source, bool succeeds)
    {
        string work = Path.Join(RepositoryRoot(), ".artifacts", "selfhost-build-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string input = Path.Join(work, "source with spaces.cala");
            File.WriteAllText(input, source);
            CaladesiCompile original = new()
            {
                CompilerPath = CompilerPath(),
                OutputAssembly = Path.Join(work, "output with spaces.dll"),
                PdbFile = Path.Join(work, "output with spaces.pdb"),
                OutputType = "Library",
                AssemblyName = "Parity",
                RootNamespace = "Parity",
                ProjectDirectory = work,
                References = TestReferences.Paths.Select(p => (ITaskItem)new TaskItem(p)).ToArray(),
                Sources = [new TaskItem(input)],
            };
            MigratedTask migrated = CopyParameters(original);
            RecordingEngine originalEngine = new();
            RecordingEngine migratedEngine = new();
            original.BuildEngine = originalEngine;
            migrated.BuildEngine = migratedEngine;
            Assert.AreEqual(succeeds, original.Execute(), string.Join("\n", originalEngine.Errors.Select(e => e.Message)));
            byte[]? expectedAssembly = succeeds ? File.ReadAllBytes(original.OutputAssembly) : null;
            if (succeeds)
            {
                File.Delete(original.OutputAssembly);
                File.Delete(original.PdbFile!);
            }

            Assert.AreEqual(succeeds, migrated.Execute(), string.Join("\n", migratedEngine.Errors.Select(e => e.Message)));
            CollectionAssert.AreEqual(
                originalEngine.Errors.Select(e => $"{e.Code}|{e.File}|{e.LineNumber}|{e.ColumnNumber}|{e.Message}").ToArray(),
                migratedEngine.Errors.Select(e => $"{e.Code}|{e.File}|{e.LineNumber}|{e.ColumnNumber}|{e.Message}").ToArray());
            if (succeeds)
            {
                CollectionAssert.AreEqual(expectedAssembly!, File.ReadAllBytes(migrated.OutputAssembly));
                Assert.IsTrue(File.Exists(migrated.PdbFile));
            }
            else
            {
                Assert.IsGreaterThan(0, migratedEngine.Errors.Count);
                Assert.IsTrue(migratedEngine.Errors.All(e => e.Code?.StartsWith("CAL", StringComparison.Ordinal) == true));
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void AssertResponses(CaladesiCompile original, MigratedTask migrated)
    {
        Assert.AreEqual(Invoke<string>(original, "GenerateResponseFileCommands"), Invoke<string>(migrated, "GenerateResponseFileCommands"));
        Assert.AreEqual(Invoke<string>(original, "GenerateCommandLineCommands"), Invoke<string>(migrated, "GenerateCommandLineCommands"));
    }

    private static MigratedTask CopyParameters(CaladesiCompile original)
    {
        MigratedTask result = new();
        foreach (PropertyInfo property in typeof(CaladesiCompile).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            typeof(MigratedTask).GetProperty(property.Name)!.SetValue(result, property.GetValue(original));
        }
        return result;
    }

    private static T Invoke<T>(ToolTask task, string method)
    {
        return (T)task.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(task, null)!;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "Caladesi.slnx")))
        {
            directory = directory.Parent;
        }
        return directory!.FullName;
    }

    private static string CompilerPath()
    {
        DirectoryInfo output = new(AppContext.BaseDirectory);
        return Path.Join(RepositoryRoot(), "src", "Caladesi.Cli", "bin", output.Parent!.Name, output.Name, "calac.dll");
    }

    private sealed class RecordingEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "";
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;
    }
}
