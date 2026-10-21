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
        var hooks = hookTypes.Where(h => h.Contains('.')).Distinct().ToList();
        // the MSTest registration has to come last
        hooks = [.. hooks.Where(h => !h.EndsWith("UnitTesting.TestingPlatformBuilderHook", StringComparison.Ordinal)),
                 .. hooks.Where(h => h.EndsWith("UnitTesting.TestingPlatformBuilderHook", StringComparison.Ordinal))];
        var sb = new StringBuilder();
        sb.AppendLine("import { TestApplication } from \"Microsoft.Testing.Platform.Builder\";");
        for (var i = 0; i < hooks.Count; i++)
        {
            var ns = hooks[i][..hooks[i].LastIndexOf('.')];
            sb.AppendLine($"import * as __hook{i} from \"{ns}\";");
        }
        sb.AppendLine("async function main(args: string[]): Task<int> {");
        sb.AppendLine("  const builder = await TestApplication.CreateBuilderAsync(args);");
        for (var i = 0; i < hooks.Count; i++)
            sb.AppendLine($"  __hook{i}.{hooks[i][(hooks[i].LastIndexOf('.') + 1)..]}.AddExtensions(builder, args);");
        sb.AppendLine("  using app = await builder.BuildAsync();");
        sb.AppendLine("  return await app.RunAsync();");
        sb.AppendLine("}");
        var dir = options.ProjectDirectory ?? Directory.GetCurrentDirectory();
        return SyntaxTree.ParseText(sb.ToString(), Path.Combine(dir, "obj", "CaladesiTestEntryPoint.cala"));
    }
}
