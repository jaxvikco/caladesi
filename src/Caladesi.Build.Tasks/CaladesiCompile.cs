using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Caladesi.Build.Tasks;

/// <summary>
/// Runs <c>calac</c> (via the dotnet host) with a response file. Diagnostics printed by the compiler in the
/// canonical <c>path(line,col): error CAL0123: message</c> format are turned into MSBuild errors/warnings by
/// <see cref="ToolTask"/>'s canonical error parsing.
/// </summary>
public sealed class CaladesiCompile : ToolTask
{
    [Required] public string CompilerPath { get; set; } = "";
    [Required] public string OutputAssembly { get; set; } = "";
    public string? PdbFile { get; set; }
    public string OutputType { get; set; } = "Exe";
    public string? AssemblyName { get; set; }
    public string? RootNamespace { get; set; }
    public string? ProjectDirectory { get; set; }
    public ITaskItem[] References { get; set; } = [];
    public ITaskItem[] Sources { get; set; } = [];
    public ITaskItem[] GlobalImports { get; set; } = [];
    public string? DefineConstants { get; set; }
    public string? NullableContext { get; set; }
    public bool TreatWarningsAsErrors { get; set; }
    public string? NoWarn { get; set; }
    public string? Version { get; set; }
    public string? TargetFrameworkMoniker { get; set; }
    public string? TargetFrameworkDisplayName { get; set; }
    public bool Optimize { get; set; }
    public bool GenerateTestEntryPoint { get; set; }
    /// <summary>Full type names of Microsoft.Testing.Platform builder hooks (<c>TestingPlatformBuilderHook</c> items).</summary>
    public ITaskItem[]? TestHooks { get; set; }
    /// <summary>Path of the dotnet host. Defaults to <c>dotnet</c> on PATH.</summary>
    public string? DotnetHostPath { get; set; }

    protected override string ToolName => "dotnet";

    protected override string GenerateFullPathToTool()
        => !string.IsNullOrEmpty(DotnetHostPath) ? DotnetHostPath! : "dotnet";

    protected override MessageImportance StandardOutputLoggingImportance => MessageImportance.Normal;
    protected override MessageImportance StandardErrorLoggingImportance => MessageImportance.High;

    protected override string GenerateCommandLineCommands() => $"\"{CompilerPath}\"";

    protected override string GenerateResponseFileCommands()
    {
        var sb = new StringBuilder();
        void Add(string s) => sb.Append(s).Append('\n');

        Add("-out:" + OutputAssembly);
        if (!string.IsNullOrEmpty(PdbFile)) Add("-pdb:" + PdbFile);
        Add("-target:" + (string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(OutputType, "WinExe", StringComparison.OrdinalIgnoreCase) ? "exe" : "library"));
        if (!string.IsNullOrEmpty(AssemblyName)) Add("-assemblyname:" + AssemblyName);
        if (!string.IsNullOrEmpty(RootNamespace)) Add("-rootnamespace:" + RootNamespace);
        if (!string.IsNullOrEmpty(ProjectDirectory)) Add("-projectdir:" + ProjectDirectory!.TrimEnd('/', '\\'));
        foreach (var r in References) Add("-ref:" + r.ItemSpec);
        foreach (var d in (DefineConstants ?? "").Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
            Add("-define:" + d);
        if (!string.IsNullOrEmpty(NullableContext))
            Add("-nullable:" + (string.Equals(NullableContext, "disable", StringComparison.OrdinalIgnoreCase) ? "disable" : "enable"));
        if (TreatWarningsAsErrors) Add("-warnaserror");
        if (!string.IsNullOrWhiteSpace(NoWarn)) Add("-nowarn:" + NoWarn!.Trim());
        foreach (var g in GlobalImports) Add("-globalimport:" + g.ItemSpec);
        if (!string.IsNullOrEmpty(Version)) Add("-version:" + Version);
        if (!string.IsNullOrEmpty(TargetFrameworkMoniker)) Add("-targetframework:" + TargetFrameworkMoniker);
        if (!string.IsNullOrEmpty(TargetFrameworkDisplayName)) Add("-targetframeworkdisplay:" + TargetFrameworkDisplayName);
        if (Optimize) Add("-optimize");
        if (GenerateTestEntryPoint) Add("-testentry");
        if (TestHooks != null)
            foreach (var h in TestHooks)
            {
                var typeName = h.GetMetadata("TypeFullName");
                if (!string.IsNullOrEmpty(typeName)) Add("-testhook:" + typeName);
            }
        foreach (var s in Sources) Add(s.GetMetadata("FullPath"));
        return sb.ToString();
    }

    protected override bool ValidateParameters()
    {
        if (!File.Exists(CompilerPath))
        {
            Log.LogError("calac not found at '{0}'.", CompilerPath);
            return false;
        }
        return base.ValidateParameters();
    }

    protected override bool HandleTaskExecutionErrors()
    {
        // Canonical diagnostics were already logged; only add a generic error when none were.
        if (HasLoggedErrors) return false;
        return base.HandleTaskExecutionErrors();
    }
}
