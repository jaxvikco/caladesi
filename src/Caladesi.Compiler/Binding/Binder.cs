using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler;

/// <summary>
/// Binds syntax to symbols and bound trees. One binder instance handles one declaration context (a signature,
/// or a whole method body); it keeps its own scope stack and flow (narrowing) state.
/// </summary>
public sealed partial class Binder
{
    private DiagnosticBag? _diag;
    private readonly Dictionary<string, TypeParameterSymbol> _typeParams = new(StringComparer.Ordinal);

    /// <summary>Optional recorder used by the semantic model.</summary>
#pragma warning disable CA2227 // assigned by the semantic model after the binder is created
    public Dictionary<SyntaxNode, Symbol>? SymbolMap { get; set; }
    public Dictionary<SyntaxNode, TypeSymbol>? TypeMap { get; set; }
    public Dictionary<SyntaxNode, BoundNode>? BoundMap { get; set; }
#pragma warning restore CA2227
    internal Dictionary<(SyntaxTree, int), Symbol>? DeclarationMap { get; set; }

    public Binder(Compilation compilation, SourceFile file, Symbol? containing, DiagnosticBag? diagnostics)
    {
        Compilation = compilation;
        File = file;
        ContainingSymbol = containing;
        _diag = diagnostics;
        CollectTypeParameters(containing);
        _scope = new Scope(null, ScopeKind.Method);
    }

    public Compilation Compilation { get; }
    public SourceFile File { get; }
    public Symbol? ContainingSymbol { get; }

    private void CollectTypeParameters(Symbol? s)
    {
        // innermost first: method type parameters shadow type parameters
        List<Symbol> chain = [];
        for (Symbol? x = s; x != null; x = x.ContainingSymbol)
        {
            chain.Add(x);
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            switch (chain[i])
            {
                case NamedTypeSymbol nt:
                    foreach (TypeParameterSymbol tp in nt.TypeParameters)
                    {
                        _typeParams[tp.Name] = tp;
                    }

                    break;
                case MethodSymbol m:
                    foreach (TypeParameterSymbol tp in m.TypeParameters)
                    {
                        _typeParams[tp.Name] = tp;
                    }

                    break;
                case TypeAliasSymbol a:
                    foreach (TypeParameterSymbol tp in a.TypeParameters)
                    {
                        _typeParams[tp.Name] = tp;
                    }

                    break;
            }
            if (chain[i] is LambdaSymbol) { }
        }
    }

    // ------------------------------------------------------------------ diagnostics

    internal void Report(DiagnosticDescriptor d, SyntaxElement? at, params object?[] args)
    {
        Report(d, at?.Span ?? default, args);
    }

    internal void Report(DiagnosticDescriptor d, TextSpan span, params object?[] args)
    {
        if (_diag == null)
        {
            return;
        }

        _diag.Add(Diagnostic.Create(d, File.FilePath, span, args));
    }

    internal void Report(Diagnostic d)
    {
        _diag?.Add(d);
    }

    internal void ReportWithFix(DiagnosticDescriptor d, TextSpan span, FixIt fix, params object?[] args)
    {
        if (_diag == null)
        {
            return;
        }

        _diag.Add(new Diagnostic(d, File.FilePath, span, args, fix) { EffectiveSeverity = d.Severity });
    }

    /// <summary>Runs <paramref name="f"/> with diagnostics suppressed (for speculative binding).</summary>
    internal T Speculatively<T>(Func<T> f)
    {
        DiagnosticBag? saved = _diag;
        (Dictionary<SyntaxNode, Symbol>? SymbolMap, Dictionary<SyntaxNode, TypeSymbol>? TypeMap, Dictionary<SyntaxNode, BoundNode>? BoundMap) savedMaps = (SymbolMap, TypeMap, BoundMap);
        Dictionary<(SyntaxTree, int), Symbol>? savedDecls = DeclarationMap;
        _diag = null;
        SymbolMap = null; TypeMap = null; BoundMap = null; DeclarationMap = null;
        try { return f(); }
        finally
        {
            _diag = saved;
            (SymbolMap, TypeMap, BoundMap) = savedMaps;
            DeclarationMap = savedDecls;
        }
    }

    internal bool IsSpeculative => _diag == null;

    // ------------------------------------------------------------------ scopes

    internal enum ScopeKind { Method, Block, Loop, Switch, Lambda }

    internal sealed class Scope(Scope? parent, ScopeKind kind)
    {
        public Scope? Parent { get; } = parent;
        public ScopeKind Kind { get; } = kind;
        public Dictionary<string, Symbol> Symbols { get; } = new(StringComparer.Ordinal);
        public List<LocalSymbol> Locals { get; } = [];
        public LabelSymbol? BreakLabel { get; set; }
        public LabelSymbol? ContinueLabel { get; set; }
        public string? LabelName { get; set; }
        public bool BreakUsed { get; set; }
    }

    private Scope _scope;

    private Scope PushScope(ScopeKind kind)
    {
        _scope = new Scope(_scope, kind);
        return _scope;
    }

    private void PopScope()
    {
        _scope = _scope.Parent!;
    }

    private void DeclareSymbol(string name, Symbol symbol, SyntaxElement? at)
    {
        // duplicates in the same function scope chain (TS: no shadowing within a function for let/const in same block)
        if (_scope.Symbols.ContainsKey(name))
        {
            Report(Descriptors.DuplicateLocal, at, name);
            return;
        }
        _scope.Symbols[name] = symbol;
        if (symbol is LocalSymbol l)
        {
            _scope.Locals.Add(l);
        }

        if (DeclarationMap != null && at != null)
        {
            SyntaxToken? token = at as SyntaxToken ?? (at as ParameterSyntax)?.Identifier ?? (at as SyntaxNode)?.GetFirstToken();
            if (token != null && !token.IsMissing)
            {
                DeclarationMap[(File.Tree, token.Start)] = symbol;
            }
        }
    }

    private Symbol? LookupLocal(string name)
    {
        for (Scope? s = _scope; s != null; s = s.Parent)
        {
            if (s.Symbols.TryGetValue(name, out Symbol? sym))
            {
                return sym;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ type helpers

    internal NamedTypeSymbol Special(SpecialType t)
    {
        return Compilation.GetSpecialType(t);
    }

    internal TypeSymbol VoidType => Special(SpecialType.Void);
    internal TypeSymbol BoolType => Special(SpecialType.Boolean);
    internal TypeSymbol StringType => Special(SpecialType.String);
    internal TypeSymbol ObjectType => Special(SpecialType.Object);
    internal TypeSymbol IntType => Special(SpecialType.Int32);
    internal TypeSymbol DoubleType => Special(SpecialType.Double);
    internal static TypeSymbol ErrorType => ErrorTypeSymbol.Instance;

    internal static bool IsError(TypeSymbol? t)
    {
        return t == null || t.IsErrorType;
    }

    internal static BoundBadExpression Bad(SyntaxNode? syntax, params BoundNode[] children)
    {
        return new([.. children]) { Syntax = syntax };
    }

    internal static BoundBadExpression BadFrom(SyntaxNode? syntax, ImmutableArray<BoundNode> children)
    {
        return new(children) { Syntax = syntax };
    }

    internal static BoundLiteral Literal(object? value, TypeSymbol type, SyntaxNode? syntax = null)
    {
        return new(new ConstantValue(value), type) { Syntax = syntax };
    }
}
