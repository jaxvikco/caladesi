using System.Text;

namespace Caladesi.Compiler.Syntax;

/// <summary>
/// The Microsoft.Testing.Platform entry point for test projects. C# projects get theirs from an MSBuild task that only
/// speaks C#, VB and F#; for Caladesi the compiler generates the equivalent source: create the builder, let every
/// registered extension hook add itself (the MSTest hook last), build and run.
/// </summary>
public static class TestEntryPoint
{
    public static SyntaxTree Create(CompilationOptions options, IEnumerable<string> hookTypes)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> hooks = [.. hookTypes.Where(h => h.Contains('.')).Distinct()];
        // the MSTest registration has to come last
        hooks = [.. hooks.Where(h => !h.EndsWith("UnitTesting.TestingPlatformBuilderHook", StringComparison.Ordinal)),
                 .. hooks.Where(h => h.EndsWith("UnitTesting.TestingPlatformBuilderHook", StringComparison.Ordinal))];
        StringBuilder sb = new();
        _ = sb.AppendLine("import { TestApplication } from \"Microsoft.Testing.Platform.Builder\";");
        for (int i = 0; i < hooks.Count; i++)
        {
            string ns = hooks[i][..hooks[i].LastIndexOf('.')];
            _ = sb.AppendLine($"import * as __hook{i} from \"{ns}\";");
        }
        _ = sb.AppendLine("async function main(args: string[]): Task<int> {");
        _ = sb.AppendLine("  const builder = await TestApplication.CreateBuilderAsync(args);");
        for (int i = 0; i < hooks.Count; i++)
        {
            _ = sb.AppendLine($"  __hook{i}.{hooks[i][(hooks[i].LastIndexOf('.') + 1)..]}.AddExtensions(builder, args);");
        }

        _ = sb.AppendLine("  using app = await builder.BuildAsync();");
        _ = sb.AppendLine("  return await app.RunAsync();");
        _ = sb.AppendLine("}");
        string dir = options.ProjectDirectory ?? Directory.GetCurrentDirectory();
        return SyntaxTree.ParseText(sb.ToString(), Path.Combine(dir, "obj", "CaladesiTestEntryPoint.cala"));
    }
}
