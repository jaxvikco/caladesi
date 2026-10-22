using System.Collections.Immutable;
using Caladesi.Compiler;

namespace Caladesi.Cli;

/// <summary>Parsed <c>calac</c> command line. Arguments look like <c>-key:value</c>; <c>@file</c> reads more arguments (one per line).</summary>
internal sealed class CliOptions
{
    public string? Output { get; private set; }
    public string? PdbOutput { get; private set; }
    public OutputKind Target { get; private set; } = OutputKind.ConsoleApplication;
    public string AssemblyName { get; private set; } = "";
    public string RootNamespace { get; private set; } = "";
    public string? ProjectDirectory { get; private set; }
    public bool Nullable { get; private set; } = true;
    public bool WarnAsError { get; private set; }
    public bool Optimize { get; private set; }
    public bool GenerateTestEntryPoint { get; private set; }
    public string? TargetFramework { get; private set; }
    public string? TargetFrameworkDisplayName { get; private set; }
    public Version AssemblyVersion { get; private set; } = new(1, 0, 0, 0);
    public bool ShowVersion { get; private set; }
    public List<string> References { get; } = [];
    public List<string> Defines { get; } = [];
    public List<string> NoWarn { get; } = [];
    public List<string> GlobalImports { get; } = [];
    public List<string> TestHooks { get; } = [];
    public List<string> Sources { get; } = [];
    public List<string> Errors { get; } = [];
    private bool _globalImportsSet;

    public ImmutableArray<string> EffectiveGlobalImports => _globalImportsSet
        ? [.. CompilationOptions.DefaultGlobalImports, .. GlobalImports]
        : CompilationOptions.DefaultGlobalImports;

    public static CliOptions Parse(IEnumerable<string> args)
    {
        CliOptions o = new();
        foreach (string a in Expand(args, o))
        {
            o.Apply(a);
        }

        return o;
    }

    private static IEnumerable<string> Expand(IEnumerable<string> args, CliOptions o)
    {
        foreach (string a in args)
        {
            if (a.StartsWith('@'))
            {
                string path = a[1..];
                if (!File.Exists(path)) { o.Errors.Add($"response file not found: {path}"); continue; }
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith('#'))
                    {
                        continue;
                    }

                    if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
                    {
                        t = t[1..^1];
                    }

                    yield return t;
                }
            }
            else
            {
                yield return a;
            }
        }
    }

    private void Apply(string arg)
    {
        if (!arg.StartsWith('-') || arg.Length < 2)
        {
            Sources.Add(arg);
            return;
        }
        string body = arg.TrimStart('-');
        int colon = body.IndexOf(':');
        string key = (colon < 0 ? body : body[..colon]).ToLowerInvariant();
        string value = colon < 0 ? "" : body[(colon + 1)..];
        switch (key)
        {
            case "out": Output = value; break;
            case "pdb": PdbOutput = value; break;
            case "target":
                Target = value.ToLowerInvariant() switch
                {
                    "exe" or "console" => OutputKind.ConsoleApplication,
                    "library" or "dll" => OutputKind.DynamicallyLinkedLibrary,
                    _ => Bad($"unknown target '{value}'", OutputKind.ConsoleApplication),
                };
                break;
            case "assemblyname": AssemblyName = value; break;
            case "rootnamespace": RootNamespace = value; break;
            case "projectdir": ProjectDirectory = value; break;
            case "ref": if (value.Length > 0) { References.Add(value); } break;
            case "define": if (value.Length > 0) { Defines.Add(value); } break;
            case "nowarn": NoWarn.AddRange(value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)); break;
            case "globalimport": _globalImportsSet = true; if (value.Length > 0) { GlobalImports.Add(value); } break;
            case "nullable": Nullable = !value.Equals("disable", StringComparison.OrdinalIgnoreCase); break;
            case "warnaserror": WarnAsError = !value.Equals("-", StringComparison.Ordinal) && !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
            case "optimize": Optimize = !value.Equals("-", StringComparison.Ordinal) && !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
            case "testhook": if (value.Length > 0) { TestHooks.Add(value); } break;
            case "testentry": GenerateTestEntryPoint = !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
            case "targetframework": TargetFramework = value; break;
            case "targetframeworkdisplay": TargetFrameworkDisplayName = value; break;
            case "version":
                if (value.Length == 0)
                {
                    ShowVersion = true;
                }
                else if (Version.TryParse(value, out Version? v))
                {
                    AssemblyVersion = v;
                }
                else
                {
                    Errors.Add($"invalid version '{value}'");
                }

                break;
            default: Errors.Add($"unknown option '{arg}'"); break;
        }
    }

    private T Bad<T>(string message, T fallback)
    {
        Errors.Add(message);
        return fallback;
    }

    public CompilationOptions ToCompilationOptions()
    {
        return new()
        {
            OutputKind = Target,
            AssemblyName = AssemblyName.Length > 0 ? AssemblyName : Path.GetFileNameWithoutExtension(Output ?? "Caladesi"),
            RootNamespace = RootNamespace,
            ProjectDirectory = ProjectDirectory,
            NullableEnabled = Nullable,
            WarningsAsErrors = WarnAsError,
            NoWarn = [.. NoWarn],
            Defines = [.. Defines],
            GlobalImports = EffectiveGlobalImports,
            AssemblyVersion = AssemblyVersion,
            TargetFramework = TargetFramework ?? ".NETCoreApp,Version=v10.0",
            TargetFrameworkDisplayName = TargetFrameworkDisplayName,
            GenerateTestEntryPoint = GenerateTestEntryPoint,
            Optimize = Optimize,
        };
    }
}
