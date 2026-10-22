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
        List<SymbolReference> references = [.. model.FindReferences(symbol, includeDeclaration)];
        Symbol original = symbol.OriginalDefinition;
        if (original.ContainingSymbol is not (SourceModuleClassSymbol or NamespaceSymbol))
        {
            return references;
        }

        if (model.GetDefinition(symbol) is not { } definition)
        {
            return references;
        }

        string declaringPath = Normalize(definition.Tree.FilePath);
        string name = original.Name;
        foreach (SyntaxTree tree in model.Compilation.SyntaxTrees)
        {
            foreach (ImportDeclarationSyntax import in tree.Root.Statements.OfType<ImportDeclarationSyntax>())
            {
                if (import.Clause is not NamedImportsSyntax named)
                {
                    continue;
                }

                string module = import.ModuleName;
                if (!module.StartsWith('.'))
                {
                    continue;
                }

                if (!string.Equals(Resolve(tree.FilePath, module, model.Compilation), declaringPath, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (ImportSpecifierSyntax specifier in named.Specifiers)
                {
                    SyntaxToken token = specifier.NameToken;
                    if (token.IsMissing || token.Text != name)
                    {
                        continue;
                    }

                    if (references.Any(r => r.Tree == tree && r.Span == token.Span))
                    {
                        continue;
                    }

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
        HashSet<string> known = compilation.SyntaxTrees.Select(t => Normalize(t.FilePath)).ToHashSet(StringComparer.Ordinal);
        foreach (string? candidate in new[] { combined, combined + ".cala", Path.Combine(combined, "index.cala") })
        {
            if (known.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
    }
}
