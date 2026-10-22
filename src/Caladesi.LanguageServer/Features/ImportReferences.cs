using Caladesi.Compiler;
using Caladesi.Compiler.Semantics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.LanguageServer;

/// <summary>
/// <c>SemanticModel.FindReferences</c> does not report the imported name in <c>import { x } from "./file"</c>, which would leave
/// a rename half done. This adds those specifiers by matching the name and the module the specifier resolves to.
/// </summary>
internal static class ImportReferences
{
    public static IReadOnlyList<SymbolReference> Find(SemanticModel model, Symbol symbol, bool includeDeclaration)
    {
        var references = model.FindReferences(symbol, includeDeclaration).ToList();
        var original = symbol.OriginalDefinition;
        if (original.ContainingSymbol is not (SourceModuleClassSymbol or NamespaceSymbol)) return references;
        if (model.GetDefinition(symbol) is not { } definition) return references;

        var declaringPath = Normalize(definition.Tree.FilePath);
        var name = original.Name;
        foreach (var tree in model.Compilation.SyntaxTrees)
        {
            foreach (var import in tree.Root.Statements.OfType<ImportDeclarationSyntax>())
            {
                if (import.Clause is not NamedImportsSyntax named) continue;
                var module = import.ModuleName;
                if (!module.StartsWith('.')) continue;
                if (!string.Equals(Resolve(tree.FilePath, module, model.Compilation), declaringPath, StringComparison.Ordinal)) continue;
                foreach (var specifier in named.Specifiers)
                {
                    var token = specifier.NameToken;
                    if (token.IsMissing || token.Text != name) continue;
                    if (references.Any(r => r.Tree == tree && r.Span == token.Span)) continue;
                    references.Add(new SymbolReference(tree, token.Span, false));
                }
            }
        }
        references.Sort((a, b) => a.Tree == b.Tree ? a.Span.Start.CompareTo(b.Span.Start) : string.CompareOrdinal(a.Tree.FilePath, b.Tree.FilePath));
        return references;
    }

    private static string? Resolve(string fromFile, string module, Compilation compilation)
    {
        string combined;
        try { combined = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Normalize(fromFile)) ?? "", module)); }
        catch (ArgumentException) { return null; }
        var known = compilation.SyntaxTrees.Select(t => Normalize(t.FilePath)).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in new[] { combined, combined + ".cala", Path.Combine(combined, "index.cala") })
            if (known.Contains(candidate)) return candidate;
        return null;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
    }
}
