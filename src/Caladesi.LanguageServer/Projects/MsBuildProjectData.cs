using System.Collections.Immutable;
using System.Text.Json;
using Caladesi.Compiler;

namespace Caladesi.LanguageServer.Projects;

/// <summary>What the language server needs to know about an <c>.calaproj</c>, as reported by <c>dotnet msbuild -getProperty -getItem</c>.</summary>
internal sealed class MsBuildProjectData
{
    public const string ProjectProperties = "RootNamespace,AssemblyName,OutputType,TargetFramework,TargetFrameworkMoniker,TargetFrameworkMonikerDisplayName,Nullable,TreatWarningsAsErrors,NoWarn,DefineConstants,AssemblyVersion,Version,CaladesiGenerateTestEntryPoint";

    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Compile { get; } = [];
    public List<string> GlobalImports { get; } = [];
    public List<string> References { get; } = [];
    public List<string> TestHooks { get; } = [];

    public string Property(string name)
    {
        return Properties.TryGetValue(name, out string? v) ? v : "";
    }

    /// <summary>
    /// Parses the output of <c>dotnet msbuild -getProperty:… -getItem:…</c>. Anything printed before the JSON object (restore
    /// chatter, warnings) is ignored. Returns null when no JSON object is present.
    /// </summary>
    public static MsBuildProjectData? Parse(string output, string projectDirectory)
    {
        string? json = ExtractJson(output);
        if (json == null)
        {
            return null;
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (document)
        {
            MsBuildProjectData data = new();
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty(nameof(Properties), out JsonElement props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in props.EnumerateObject())
                {
                    data.Properties[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
                }
            }

            if (root.TryGetProperty("Items", out JsonElement items) && items.ValueKind == JsonValueKind.Object)
            {
                Collect(items, "Compile", data.Compile, projectDirectory, path: true);
                Collect(items, "CaladesiGlobalImport", data.GlobalImports, projectDirectory, path: false);
                Collect(items, "ReferencePathWithRefAssemblies", data.References, projectDirectory, path: true);
                Collect(items, "TestingPlatformBuilderHook", data.TestHooks, projectDirectory, path: false);
            }
            return data;
        }
    }

    private static string? ExtractJson(string output)
    {
        int index = 0;
        while (index < output.Length)
        {
            int end = output.IndexOf('\n', index);
            if (end < 0)
            {
                end = output.Length;
            }

            if (output.AsSpan(index, end - index).Trim().SequenceEqual("{"))
            {
                return output[index..];
            }

            index = end + 1;
        }
        int first = output.IndexOf('{');
        return first >= 0 ? output[first..] : null;
    }

    private static void Collect(JsonElement items, string name, List<string> into, string projectDirectory, bool path)
    {
        if (!items.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? value = null;
            if (path && item.TryGetProperty("FullPath", out JsonElement full) && full.ValueKind == JsonValueKind.String)
            {
                value = full.GetString();
            }

            if (string.IsNullOrEmpty(value) && item.TryGetProperty("Identity", out JsonElement id) && id.ValueKind == JsonValueKind.String)
            {
                value = id.GetString();
            }

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (path && !Path.IsPathRooted(value))
            {
                value = Path.GetFullPath(Path.Combine(projectDirectory, value));
            }

            into.Add(value);
        }
    }

    public CompilationOptions ToCompilationOptions(string projectPath)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath) ?? "";
        string fileName = Path.GetFileNameWithoutExtension(projectPath);
        string assemblyName = Property("AssemblyName");
        string rootNamespace = Property("RootNamespace");
        string outputType = Property("OutputType");
        string version = Property("AssemblyVersion");
        if (version.Length == 0)
        {
            version = Property("Version");
        }

        string moniker = Property("TargetFrameworkMoniker");
        CompilationOptions defaults = new();
        return new CompilationOptions
        {
            AssemblyName = assemblyName.Length > 0 ? assemblyName : fileName,
            RootNamespace = rootNamespace.Length > 0 ? rootNamespace : fileName,
            ProjectDirectory = projectDirectory,
            OutputKind = outputType.Equals("Library", StringComparison.OrdinalIgnoreCase) ? OutputKind.DynamicallyLinkedLibrary : OutputKind.ConsoleApplication,
            NullableEnabled = !Property("Nullable").Equals("disable", StringComparison.OrdinalIgnoreCase),
            WarningsAsErrors = Property("TreatWarningsAsErrors").Equals("true", StringComparison.OrdinalIgnoreCase),
            NoWarn = Split(Property("NoWarn")),
            Defines = Split(Property("DefineConstants")),
            GlobalImports = [.. CompilationOptions.DefaultGlobalImports, .. GlobalImports.Where(g => !CompilationOptions.DefaultGlobalImports.Contains(g)).Distinct()],
            AssemblyVersion = ParseVersion(version) ?? defaults.AssemblyVersion,
            TargetFramework = moniker.Length > 0 ? moniker : defaults.TargetFramework,
            TargetFrameworkDisplayName = Property("TargetFrameworkMonikerDisplayName") is { Length: > 0 } display ? display : null,
            RecordSemanticInfo = true,
        };
    }

    private static ImmutableArray<string> Split(string value)
    {
        return [.. value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
    }

    private static Version? ParseVersion(string text)
    {
        string core = text.Split('-', '+')[0];
        return !Version.TryParse(core, out Version? v) ? null : new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
}
