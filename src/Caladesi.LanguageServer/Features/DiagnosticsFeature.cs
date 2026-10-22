using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Caladesi.Compiler;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;
using Caladesi.LanguageServer.Projects;
using Caladesi.LanguageServer.Protocol;

namespace Caladesi.LanguageServer;

internal sealed partial class LanguageServer
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A compiler failure in one unit must not stop diagnostics for the others or crash the server.")]
    private void RunDiagnostics()
    {
        foreach (Workspace.PublishPlan retired in _workspace.TakeRetired())
        {
            Publish(retired.Uri, null, [], null);
        }

        List<CompilationUnit> units = [.. _workspace.Dirty];
        _workspace.Dirty.Clear();
        foreach (CompilationUnit? unit in units)
        {
            try
            {
                Compilation compilation = unit.GetCompilation(_workspace);
                ImmutableArray<Diagnostic> diagnostics = compilation.GetDiagnostics();
                Dictionary<string, SyntaxTree> trees = compilation.SyntaxTrees.ToDictionary(t => t.FilePath, StringComparer.Ordinal);
                foreach (Workspace.PublishPlan plan in _workspace.PlanPublish(unit, diagnostics))
                {
                    string path = Workspace.UriToPath(plan.Uri);
                    int? version = _workspace.GetDocument(plan.Uri)?.Version;
                    _ = trees.TryGetValue(path, out SyntaxTree? tree);
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
            if (version != null)
            {
                w.WriteNumber("version", version.Value);
            }

            w.WritePropertyName("diagnostics");
            w.WriteStartArray();
            foreach (Diagnostic d in diagnostics)
            {
                if (tree == null)
                {
                    break;
                }

                w.WriteStartObject();
                w.WriteRange("range", tree.Text.GetLinePositionSpan(d.Span));
                w.WriteNumber("severity", d.EffectiveSeverity switch
                {
                    DiagnosticSeverity.Error => 1,
                    DiagnosticSeverity.Warning => 2,
                    DiagnosticSeverity.Info => 3,
                    DiagnosticSeverity.Hidden => 4,
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
