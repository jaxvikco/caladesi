extern alias SelfHostCli;
extern alias SelfHost;
using PortCompilationOptions = SelfHost::Caladesi.Compiler.CompilationOptions;
using PortOutputKind = SelfHost::Caladesi.Compiler.OutputKind;
using PortOptions = SelfHostCli::Caladesi.Cli.CliOptions;

namespace Caladesi.Compiler.Tests.SelfHostCli;

[TestClass]
public sealed class OptionsTests
{
    [TestMethod]
    public void Defaults_MatchBootstrapCompilationDefaults()
    {
        var options = PortOptions.Parse([]);
        var compilation = options.ToCompilationOptions();
        Assert.IsNull(options.Output);
        Assert.IsNull(options.PdbOutput);
        Assert.IsNull(options.ProjectDirectory);
        Assert.IsFalse(options.ShowVersion);
        Assert.IsEmpty(options.Errors);
        Assert.IsEmpty(options.Sources);
        CompilationOptions defaults = new();
        Assert.AreEqual(defaults.OutputKind.ToString(), compilation.OutputKind.ToString());
        Assert.AreEqual(defaults.AssemblyName, compilation.AssemblyName);
        Assert.AreEqual(defaults.RootNamespace, compilation.RootNamespace);
        Assert.AreEqual(defaults.ProjectDirectory, compilation.ProjectDirectory);
        Assert.AreEqual(defaults.NullableEnabled, compilation.NullableEnabled);
        Assert.AreEqual(defaults.WarningsAsErrors, compilation.WarningsAsErrors);
        CollectionAssert.AreEqual(defaults.NoWarn.ToArray(), compilation.NoWarn.ToArray());
        CollectionAssert.AreEqual(defaults.Defines.ToArray(), compilation.Defines.ToArray());
        CollectionAssert.AreEqual(defaults.GlobalImports.ToArray(), compilation.GlobalImports.ToArray());
        Assert.AreEqual(defaults.AssemblyVersion, compilation.AssemblyVersion);
        Assert.AreEqual(defaults.InformationalVersion, compilation.InformationalVersion);
        Assert.AreEqual(defaults.TargetFramework, compilation.TargetFramework);
        Assert.AreEqual(defaults.TargetFrameworkDisplayName, compilation.TargetFrameworkDisplayName);
        Assert.AreEqual(defaults.GenerateTestEntryPoint, compilation.GenerateTestEntryPoint);
        Assert.AreEqual(defaults.Optimize, compilation.Optimize);
        Assert.AreEqual(defaults.RecordSemanticInfo, compilation.RecordSemanticInfo);
    }

    [TestMethod]
    public void EverySwitch_MapsToItsCompilationSetting()
    {
        var options = PortOptions.Parse([
            "---OUT:program.dll", "-PDB:program.pdb", "-TARGET:LiBrArY",
            "-assemblyname:Assembly", "-rootnamespace:Root", "-projectdir:project",
            "-ref:one.dll", "-ref:", "-define:FIRST", "-define:", "-define:SECOND",
            "-nowarn:CAL0001,CAL0002;CAL0003,,;", "-globalimport:System.Text", "-globalimport:",
            "-nullable:DISABLE", "-warnaserror:true", "-optimize", "-testentry:true",
            "-testhook:First.Hook", "-testhook:", "-testhook:Second.Hook",
            "-targetframework:Framework", "-targetframeworkdisplay:Display", "-version:2.4.6.8",
            "-informationalversion:2.4.6-preview.1+build",
            "first.cala", "-", "second.cala"
        ]);
        Assert.AreEqual("program.dll", options.Output);
        Assert.AreEqual("program.pdb", options.PdbOutput);
        Assert.AreEqual(PortOutputKind.DynamicallyLinkedLibrary, options.Target);
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

        var compilation = options.ToCompilationOptions();
        Assert.AreEqual(PortOutputKind.DynamicallyLinkedLibrary, compilation.OutputKind);
        Assert.AreEqual("Assembly", compilation.AssemblyName);
        Assert.AreEqual("Root", compilation.RootNamespace);
        Assert.AreEqual("project", compilation.ProjectDirectory);
        Assert.IsFalse(compilation.NullableEnabled);
        Assert.IsTrue(compilation.WarningsAsErrors);
        Assert.IsTrue(compilation.Optimize);
        Assert.IsTrue(compilation.GenerateTestEntryPoint);
        Assert.AreEqual(new Version(2, 4, 6, 8), compilation.AssemblyVersion);
        Assert.AreEqual("2.4.6-preview.1+build", compilation.InformationalVersion);
        Assert.AreEqual("Framework", compilation.TargetFramework);
        Assert.AreEqual("Display", compilation.TargetFrameworkDisplayName);
        CollectionAssert.AreEqual(options.NoWarn, compilation.NoWarn.ToArray());
        CollectionAssert.AreEqual(options.Defines, compilation.Defines.ToArray());
        CollectionAssert.AreEqual(PortCompilationOptions.DefaultGlobalImports.Add("System.Text").ToArray(), compilation.GlobalImports.ToArray());
    }

    [TestMethod]
    public void RepeatedSwitches_KeepTheLastValueAndBooleanRules()
    {
        var options = PortOptions.Parse([
            "-out:first.dll", "-out:second.dll", "-target:library", "-target:invalid",
            "-warnaserror", "-warnaserror:-", "-optimize:true", "-optimize:FALSE",
            "-nullable:disable", "-nullable:anything", "-testentry:true", "-testentry:false",
            "-version", "-version:3.0", "-version:invalid", "-unknown"
        ]);
        Assert.AreEqual("second.dll", options.Output);
        Assert.AreEqual(PortOutputKind.ConsoleApplication, options.Target);
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
        var options = PortOptions.Parse([
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
