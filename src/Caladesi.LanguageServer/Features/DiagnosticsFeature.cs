using System.Text.Json;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;
using Caladesi.LanguageServer.Protocol;

namespace Caladesi.LanguageServer;

public sealed partial class LanguageServer
{
    private void RunDiagnostics()
    {
        foreach (var retired in _workspace.TakeRetired()) Publish(retired.Uri, null, [], null);

        var units = _workspace.Dirty.ToList();
        _workspace.Dirty.Clear();
        foreach (var unit in units)
        {
            try
            {
                var compilation = unit.GetCompilation(_workspace);
                var diagnostics = compilation.GetDiagnostics();
                var trees = compilation.SyntaxTrees.ToDictionary(t => t.FilePath, StringComparer.Ordinal);
                foreach (var plan in _workspace.PlanPublish(unit, diagnostics))
                {
                    var path = Workspace.UriToPath(plan.Uri);
                    var version = _workspace.GetDocument(plan.Uri)?.Version;
                    trees.TryGetValue(path, out var tree);
                    Publish(plan.Uri, version, plan.Diagnostics, tree);
                }
            }
            catch (Exception ex)
            {
                Log.Write("diagnostics failed: " + ex);
            }
        }
    }

    private void Publish(string uri, int? version, IReadOnlyList<Diagnostic> diagnostics, SyntaxTree? tree)
    {
        Notify("textDocument/publishDiagnostics", w =>
        {
            w.WriteStartObject();
            w.WriteString("uri", uri);
            if (version != null) w.WriteNumber("version", version.Value);
            w.WritePropertyName("diagnostics");
            w.WriteStartArray();
            foreach (var d in diagnostics)
            {
                if (tree == null) break;
                w.WriteStartObject();
                w.WriteRange("range", tree.Text.GetLinePositionSpan(d.Span));
                w.WriteNumber("severity", d.EffectiveSeverity switch
                {
                    DiagnosticSeverity.Error => 1,
                    DiagnosticSeverity.Warning => 2,
                    DiagnosticSeverity.Info => 3,
                    _ => 4,
                });
                w.WriteString("code", d.Id);
                w.WriteString("source", "caladesi");
                w.WriteString("message", d.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
