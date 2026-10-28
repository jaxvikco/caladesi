extern alias SelfHostCli;
using Caladesi.Compiler;
using PortOptions = SelfHostCli::Caladesi.Cli.CliOptions;

namespace Caladesi.Compiler.Tests.SelfHostCli;

[TestClass]
public sealed class OptionsTests
{
    [TestMethod]
    public void Defaults_MatchBootstrapCompilationDefaults()
    {
        PortOptions options = PortOptions.Parse([]);
        CompilationOptions compilation = options.ToCompilationOptions();
        Assert.IsNull(options.Output);
        Assert.IsNull(options.PdbOutput);
        Assert.IsNull(options.ProjectDirectory);
        Assert.IsFalse(options.ShowVersion);
        Assert.IsEmpty(options.Errors);
        Assert.IsEmpty(options.Sources);
        Assert.AreEqual(new CompilationOptions(), compilation);
    }

    [TestMethod]
    public void EverySwitch_MapsToItsCompilationSetting()
    {
        PortOptions options = PortOptions.Parse([
            "---OUT:program.dll", "-PDB:program.pdb", "-TARGET:LiBrArY",
            "-assemblyname:Assembly", "-rootnamespace:Root", "-projectdir:project",
            "-ref:one.dll", "-ref:", "-define:FIRST", "-define:", "-define:SECOND",
            "-nowarn:CAL0001,CAL0002;CAL0003,,;", "-globalimport:System.Text", "-globalimport:",
            "-nullable:DISABLE", "-warnaserror:true", "-optimize", "-testentry:true",
            "-testhook:First.Hook", "-testhook:", "-testhook:Second.Hook",
            "-targetframework:Framework", "-targetframeworkdisplay:Display", "-version:2.4.6.8",
            "first.cala", "-", "second.cala"
        ]);
        Assert.AreEqual("program.dll", options.Output);
        Assert.AreEqual("program.pdb", options.PdbOutput);
        Assert.AreEqual(OutputKind.DynamicallyLinkedLibrary, options.Target);
        Assert.AreEqual("Assembly", options.AssemblyName);
        Assert.AreEqual("Root", options.RootNamespace);
        Assert.AreEqual("project", options.ProjectDirectory);
        Assert.IsFalse(options.Nullable);
        Assert.IsTrue(options.WarnAsError);
        Assert.IsTrue(options.Optimize);
        Assert.IsTrue(options.GenerateTestEntryPoint);
        Assert.IsFalse(options.ShowVersion);
        Assert.AreEqual("one.dll", string.Join("|", options.References));
        Assert.AreEqual("FIRST|SECOND", string.Join("|", options.Defines));
        Assert.AreEqual("CAL0001|CAL0002|CAL0003", string.Join("|", options.NoWarn));
        Assert.AreEqual("First.Hook|Second.Hook", string.Join("|", options.TestHooks));
        Assert.AreEqual("first.cala|-|second.cala", string.Join("|", options.Sources));
        Assert.IsEmpty(options.Errors);

        CompilationOptions compilation = options.ToCompilationOptions();
        Assert.AreEqual(OutputKind.DynamicallyLinkedLibrary, compilation.OutputKind);
        Assert.AreEqual("Assembly", compilation.AssemblyName);
        Assert.AreEqual("Root", compilation.RootNamespace);
        Assert.AreEqual("project", compilation.ProjectDirectory);
        Assert.IsFalse(compilation.NullableEnabled);
        Assert.IsTrue(compilation.WarningsAsErrors);
        Assert.IsTrue(compilation.Optimize);
        Assert.IsTrue(compilation.GenerateTestEntryPoint);
        Assert.AreEqual(new Version(2, 4, 6, 8), compilation.AssemblyVersion);
        Assert.AreEqual("Framework", compilation.TargetFramework);
        Assert.AreEqual("Display", compilation.TargetFrameworkDisplayName);
        CollectionAssert.AreEqual(options.NoWarn, compilation.NoWarn.ToArray());
        CollectionAssert.AreEqual(options.Defines, compilation.Defines.ToArray());
        CollectionAssert.AreEqual(CompilationOptions.DefaultGlobalImports.Add("System.Text").ToArray(), compilation.GlobalImports.ToArray());
    }

    [TestMethod]
    public void RepeatedSwitches_KeepTheLastValueAndBooleanRules()
    {
        PortOptions options = PortOptions.Parse([
            "-out:first.dll", "-out:second.dll", "-target:library", "-target:invalid",
            "-warnaserror", "-warnaserror:-", "-optimize:true", "-optimize:FALSE",
            "-nullable:disable", "-nullable:anything", "-testentry:true", "-testentry:false",
            "-version", "-version:3.0", "-version:invalid", "-unknown"
        ]);
        Assert.AreEqual("second.dll", options.Output);
        Assert.AreEqual(OutputKind.ConsoleApplication, options.Target);
        Assert.IsFalse(options.WarnAsError);
        Assert.IsFalse(options.Optimize);
        Assert.IsTrue(options.Nullable);
        Assert.IsFalse(options.GenerateTestEntryPoint);
        Assert.IsTrue(options.ShowVersion);
        Assert.AreEqual(new Version(3, 0), options.AssemblyVersion);
        Assert.AreEqual("unknown target 'invalid'|invalid version 'invalid'|unknown option '-unknown'", string.Join("|", options.Errors));
        Assert.AreEqual("second", options.ToCompilationOptions().AssemblyName);
    }

    [TestMethod]
    [DataRow("", true, true, true, true)]
    [DataRow("-", true, false, false, true)]
    [DataRow("FaLsE", true, false, false, false)]
    [DataRow("DISABLE", false, true, true, true)]
    public void BooleanSwitches_PreserveTheirDistinctValueRules(string value, bool nullable, bool warningsAsErrors, bool optimize, bool testEntry)
    {
        PortOptions options = PortOptions.Parse([
            "-nullable:" + value, "-warnaserror:" + value,
            "-optimize:" + value, "-testentry:" + value
        ]);
        Assert.AreEqual(nullable, options.Nullable);
        Assert.AreEqual(warningsAsErrors, options.WarnAsError);
        Assert.AreEqual(optimize, options.Optimize);
        Assert.AreEqual(testEntry, options.GenerateTestEntryPoint);
        Assert.IsEmpty(options.Errors);
    }
}
