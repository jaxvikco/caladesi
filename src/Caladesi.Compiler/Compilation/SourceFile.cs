using System.Collections.Immutable;
using System.Text;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

/// <summary>A source file viewed as a module: its namespace, its generated static class, declared types and imports.</summary>
public sealed class SourceFile
{
    private FileScope? _scope;

    public SourceFile(Compilation compilation, SyntaxTree tree)
    {
        Compilation = compilation;
        Tree = tree;
        NamespaceName = DeriveNamespace(compilation.Options, tree.FilePath);
        ModuleClassBaseName = NameUtilities.ToPascalCase(Path.GetFileNameWithoutExtension(tree.FilePath));
        if (ModuleClassBaseName.Length == 0) ModuleClassBaseName = "Program";
    }

    public Compilation Compilation { get; }
    public SyntaxTree Tree { get; }
    public string NamespaceName { get; }
    public string ModuleClassBaseName { get; }
    public NamespaceSymbol Namespace { get; internal set; } = null!;
    public SourceModuleClassSymbol? ModuleClass { get; internal set; }
    public ImmutableArray<SourceNamedType> Types { get; internal set; } = ImmutableArray<SourceNamedType>.Empty;
    public ImmutableArray<TypeAliasSymbol> Aliases { get; internal set; } = ImmutableArray<TypeAliasSymbol>.Empty;
    public ImmutableArray<ImportDeclarationSyntax> Imports { get; internal set; } = ImmutableArray<ImportDeclarationSyntax>.Empty;
    public ImmutableArray<StatementSyntax> TopLevelStatements { get; internal set; } = ImmutableArray<StatementSyntax>.Empty;
    public FileScope Scope => _scope ??= new FileScope(this);
    public string FilePath => Tree.FilePath;

    private static string DeriveNamespace(CompilationOptions options, string filePath)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(options.RootNamespace)) parts.Add(options.RootNamespace);
        if (!string.IsNullOrEmpty(options.ProjectDirectory) && !string.IsNullOrEmpty(filePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "";
                var rel = Path.GetRelativePath(Path.GetFullPath(options.ProjectDirectory), dir);
                if (rel != "." && !rel.StartsWith("..", StringComparison.Ordinal))
                {
                    foreach (var seg in rel.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = NameUtilities.ToPascalCase(seg);
                        if (p.Length > 0) parts.Add(p);
                    }
                }
            }
            catch (ArgumentException)
            {
            }
        }
        return string.Join('.', parts);
    }
}

public static class NameUtilities
{
    /// <summary>Converts <c>data-access</c> / <c>my_file</c> / <c>foo bar</c> to PascalCase identifiers.</summary>
    public static string ToPascalCase(string s)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else upper = true;
        }
        if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>Flips the case of the first letter (for matching camelCase member names to .NET PascalCase names).</summary>
    public static string FlipFirstCase(string s)
    {
        if (s.Length == 0) return s;
        var c = s[0];
        var f = char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
        return f + s[1..];
    }
}
