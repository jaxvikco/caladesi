using System.Collections.Immutable;

namespace Caladesi.Compiler;

public enum OutputKind { ConsoleApplication, DynamicallyLinkedLibrary }

public sealed record CompilationOptions
{
    public static readonly ImmutableArray<string> DefaultGlobalImports =
        ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"];

    public OutputKind OutputKind { get; init; } = OutputKind.ConsoleApplication;
    public string AssemblyName { get; init; } = "Caladesi";
    public string RootNamespace { get; init; } = "";
    /// <summary>Directory used to derive namespaces from file paths.</summary>
    public string? ProjectDirectory { get; init; }
    public bool NullableEnabled { get; init; } = true;
    public bool WarningsAsErrors { get; init; }
    public ImmutableArray<string> NoWarn { get; init; } = [];
    public ImmutableArray<string> Defines { get; init; } = [];
    public ImmutableArray<string> GlobalImports { get; init; } = DefaultGlobalImports;
    public Version AssemblyVersion { get; init; } = new(1, 0, 0, 0);
    public string? TargetFramework { get; init; } = ".NETCoreApp,Version=v10.0";
    public string? TargetFrameworkDisplayName { get; init; }
    /// <summary>When true, the compiler generates a Microsoft.Testing.Platform entry point for MSTest projects.</summary>
    public bool GenerateTestEntryPoint { get; init; }
    public bool Optimize { get; init; }
}
