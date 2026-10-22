namespace Caladesi.LanguageServer;

public sealed class ServerOptions
{
    /// <summary>Quiet period after the last edit before diagnostics are computed.</summary>
    public TimeSpan DiagnosticsDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Path of <c>Caladesi.Runtime.dll</c> (or its folder) used for files without a project.</summary>
    public string? RuntimePath { get; set; }

    /// <summary>The <c>dotnet</c> executable used to evaluate projects; discovered when null.</summary>
    public string? DotnetPath { get; set; }

    /// <summary>Extra environment variables for <c>dotnet</c> child processes (a null value removes the variable).</summary>
    public Dictionary<string, string?> ChildEnvironment { get; } = new();
}
