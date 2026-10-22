using System.Collections.Immutable;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Semantics;

/// <summary>
/// Answers questions about one syntax tree of a <see cref="Compilation"/> created with
/// <see cref="CompilationOptions.RecordSemanticInfo"/>: what a name refers to, where it is declared, who uses it, what can be
/// typed at a position. Creating the model binds the whole compilation.
/// </summary>
public sealed partial class SemanticModel
{
    private readonly Dictionary<(SyntaxTree, int), Symbol> _declared;

    public Compilation Compilation { get; }
    public SyntaxTree Tree { get; }

    public SemanticModel(Compilation compilation, SyntaxTree tree)
    {
        if (!compilation.Options.RecordSemanticInfo)
            throw new ArgumentException("The compilation must be created with RecordSemanticInfo to build a semantic model.", nameof(compilation));
        Compilation = compilation;
        Tree = tree;
        _ = compilation.GetDiagnostics(); // binds everything and fills the recorded maps
        _declared = compilation.GetDeclarationIndex();
    }

    // ------------------------------------------------------------------ names

    internal static SyntaxToken? NameToken(SyntaxNode node) => node switch
    {
        SimpleNameSyntax s => s.Identifier,
        QualifiedNameSyntax q => q.Right.Identifier,
        ImportSpecifierSyntax i => i.NameToken,
        _ => null,
    };

    private SyntaxToken? TokenAt(int position)
    {
        var root = Tree.Root;
        var token = root.FindToken(position);
        if (token != null && token.Kind is SyntaxKind.IdentifierToken || token != null && SyntaxFacts.IsContextualKeyword(token.Kind) && token.Start <= position && position <= token.End)
            if (position >= token.Start && position <= token.End) return token;
        // cursor right after an identifier: FindToken may return the next token
        if (position > 0)
        {
            var prev = root.FindToken(position - 1);
            if (prev != null && prev.End == position && (prev.Kind == SyntaxKind.IdentifierToken || SyntaxFacts.IsContextualKeyword(prev.Kind))) return prev;
        }
        return null;
    }

    /// <summary>The symbol that the identifier at <paramref name="position"/> refers to or declares.</summary>
    public SymbolAt? GetSymbolAt(int position)
    {
        var token = TokenAt(position);
        if (token == null || token.IsMissing) return null;
        if (_declared.TryGetValue((Tree, token.Start), out var decl)) return new SymbolAt(decl, token.Span, true);
        var recorded = Compilation.RecordedSymbols;
        if (recorded == null) return null;
        for (var node = token.Parent; node != null; node = node.Parent)
        {
            if (recorded.TryGetValue(node, out var symbol) && NameToken(node) is { } name && name.Start == token.Start)
                return new SymbolAt(symbol, token.Span, false);
            if (node is not (SimpleNameSyntax or QualifiedNameSyntax)) break;
        }
        return null;
    }

    /// <summary>The type of the expression whose last token is at <paramref name="position"/> (used for hover on expressions).</summary>
    public TypeSymbol? GetExpressionTypeAt(int position)
    {
        var token = TokenAt(position);
        if (token == null || Compilation.RecordedTypes == null) return null;
        for (var node = token.Parent; node != null; node = node.Parent)
        {
            if (node is ExpressionSyntax && Compilation.RecordedTypes.TryGetValue(node, out var t)) return t;
            if (node is StatementSyntax) break;
        }
        return null;
    }

    public string? GetHover(int position)
    {
        var at = GetSymbolAt(position);
        if (at != null) return SymbolDisplay.Describe(at.Symbol);
        var t = GetExpressionTypeAt(position);
        return t == null || t.IsErrorType ? null : t.ToDisplayString();
    }

    /// <summary>Where a symbol is declared in source, or null for symbols from metadata.</summary>
    public SymbolReference? GetDefinition(Symbol symbol)
    {
        symbol = symbol.OriginalDefinition;
        foreach (var kv in _declared)
            if (Same(kv.Value, symbol))
            {
                var name = kv.Key.Item1.Root.FindToken(kv.Key.Item2);
                if (name != null) return new SymbolReference(kv.Key.Item1, name.Span, true);
            }
        return null;
    }

    // ------------------------------------------------------------------ references

    private static Symbol Canonical(Symbol s)
    {
        s = s.OriginalDefinition;
        return s switch
        {
            MethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet, AssociatedSymbol: { } p } => p.OriginalDefinition,
            SourceFieldSymbol { IsBackingField: true } f when f.ContainingType?.GetMembers().OfType<SourcePropertySymbol>().FirstOrDefault(p => ReferenceEquals(p.BackingField, f)) is { } prop => prop,
            _ => s,
        };
    }

    private static bool Same(Symbol a, Symbol b) => ReferenceEquals(Canonical(a), Canonical(b)) || Canonical(a).Equals(Canonical(b));

    public IReadOnlyList<SymbolReference> FindReferences(Symbol symbol, bool includeDeclaration = true)
    {
        var result = new List<SymbolReference>();
        var target = Canonical(symbol);
        if (includeDeclaration)
            foreach (var kv in _declared)
                if (Same(kv.Value, target) && kv.Key.Item1.Root.FindToken(kv.Key.Item2) is { } t)
                    result.Add(new SymbolReference(kv.Key.Item1, t.Span, true));
        if (Compilation.RecordedSymbols != null)
            foreach (var kv in Compilation.RecordedSymbols)
            {
                if (NameToken(kv.Key) is not { } name || name.IsMissing) continue;
                if (!Same(kv.Value, target)) continue;
                var tree = kv.Key.SyntaxTree;
                if (tree == null) continue;
                var reference = new SymbolReference(tree, name.Span, false);
                if (!result.Contains(reference) && !result.Any(r => r.Tree == tree && r.Span == name.Span)) result.Add(reference);
            }
        result.Sort((a, b) => a.Tree == b.Tree ? a.Span.Start.CompareTo(b.Span.Start) : string.CompareOrdinal(a.Tree.FilePath, b.Tree.FilePath));
        return result;
    }

    // ------------------------------------------------------------------ semantic tokens

    public IReadOnlyList<SemanticTokenInfo> GetSemanticTokens()
    {
        var list = new List<SemanticTokenInfo>();
        foreach (var token in Tree.Root.DescendantTokens())
        {
            if (token.IsMissing || token.Kind != SyntaxKind.IdentifierToken) continue;
            Symbol? symbol = null;
            var isDecl = false;
            if (_declared.TryGetValue((Tree, token.Start), out var d)) { symbol = d; isDecl = true; }
            else if (Compilation.RecordedSymbols != null)
                for (var node = token.Parent; node != null; node = node.Parent)
                {
                    if (Compilation.RecordedSymbols.TryGetValue(node, out var s) && NameToken(node) is { } n && n.Start == token.Start) { symbol = s; break; }
                    if (node is not (SimpleNameSyntax or QualifiedNameSyntax)) break;
                }
            if (symbol == null) continue;
            var type = ClassifySymbol(symbol);
            if (type == null) continue;
            var mods = SemanticTokenModifiers.None;
            if (isDecl) mods |= SemanticTokenModifiers.Declaration;
            if (symbol is LocalSymbol { IsConst: true } or FieldSymbol { IsConst: true } or FieldSymbol { IsReadOnly: true } or SourceFieldSymbol { IsConstDeclaration: true }) mods |= SemanticTokenModifiers.Readonly;
            if (symbol.IsStatic) mods |= SemanticTokenModifiers.Static;
            list.Add(new SemanticTokenInfo(token.Span, type.Value, mods));
        }
        return list;
    }

    private static SemanticTokenType? ClassifySymbol(Symbol s) => s.OriginalDefinition switch
    {
        NamespaceSymbol => SemanticTokenType.Namespace,
        FileModuleSymbol => SemanticTokenType.Namespace,
        NamedTypeSymbol { TypeKind: TypeKind.Interface } => SemanticTokenType.Interface,
        NamedTypeSymbol { TypeKind: TypeKind.Enum } => SemanticTokenType.Enum,
        NamedTypeSymbol { TypeKind: TypeKind.Struct } => SemanticTokenType.Struct,
        NamedTypeSymbol => SemanticTokenType.Class,
        TypeAliasSymbol => SemanticTokenType.Type,
        TypeParameterSymbol => SemanticTokenType.TypeParameter,
        ParameterSymbol => SemanticTokenType.Parameter,
        LocalSymbol => SemanticTokenType.Variable,
        FieldSymbol { IsConst: true, ContainingType.TypeKind: TypeKind.Enum } => SemanticTokenType.EnumMember,
        FieldSymbol => SemanticTokenType.Variable,
        PropertySymbol => SemanticTokenType.Property,
        MethodSymbol { MethodKind: MethodKind.Constructor } => SemanticTokenType.Class,
        MethodSymbol m when m.ContainingSymbol is SourceModuleClassSymbol || m.ContainingType == null => SemanticTokenType.Function,
        MethodSymbol => SemanticTokenType.Method,
        _ => null,
    };
}
