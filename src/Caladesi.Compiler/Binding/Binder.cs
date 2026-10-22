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
    private readonly Compilation _c;
    private readonly SourceFile _file;
    private readonly Symbol? _containing;
    private DiagnosticBag? _diag;
    private readonly Dictionary<string, TypeParameterSymbol> _typeParams = new(StringComparer.Ordinal);

    /// <summary>Optional recorder used by the semantic model.</summary>
    public Dictionary<SyntaxNode, Symbol>? SymbolMap { get; set; }
    public Dictionary<SyntaxNode, TypeSymbol>? TypeMap { get; set; }
    public Dictionary<SyntaxNode, BoundNode>? BoundMap { get; set; }
    internal Dictionary<(SyntaxTree, int), Symbol>? DeclarationMap { get; set; }

    public Binder(Compilation compilation, SourceFile file, Symbol? containing, DiagnosticBag? diagnostics)
    {
        _c = compilation;
        _file = file;
        _containing = containing;
        _diag = diagnostics;
        CollectTypeParameters(containing);
        _scope = new Scope(null, ScopeKind.Method);
    }

    public Compilation Compilation => _c;
    public SourceFile File => _file;
    public Symbol? ContainingSymbol => _containing;

    private void CollectTypeParameters(Symbol? s)
    {
        // innermost first: method type parameters shadow type parameters
        var chain = new List<Symbol>();
        for (var x = s; x != null; x = x.ContainingSymbol) chain.Add(x);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            switch (chain[i])
            {
                case NamedTypeSymbol nt:
                    foreach (var tp in nt.TypeParameters) _typeParams[tp.Name] = tp;
                    break;
                case MethodSymbol m:
                    foreach (var tp in m.TypeParameters) _typeParams[tp.Name] = tp;
                    break;
                case TypeAliasSymbol a:
                    foreach (var tp in a.TypeParameters) _typeParams[tp.Name] = tp;
                    break;
            }
            if (chain[i] is LambdaSymbol) { }
        }
    }

    // ------------------------------------------------------------------ diagnostics

    internal void Report(DiagnosticDescriptor d, SyntaxElement? at, params object?[] args) =>
        Report(d, at?.Span ?? default, args);

    internal void Report(DiagnosticDescriptor d, TextSpan span, params object?[] args)
    {
        if (_diag == null) return;
        _diag.Add(Diagnostic.Create(d, _file.FilePath, span, args));
    }

    internal void Report(Diagnostic d) => _diag?.Add(d);

    internal void ReportWithFix(DiagnosticDescriptor d, TextSpan span, FixIt fix, params object?[] args)
    {
        if (_diag == null) return;
        _diag.Add(new Diagnostic(d, _file.FilePath, span, args, fix) { EffectiveSeverity = d.Severity });
    }

    /// <summary>Runs <paramref name="f"/> with diagnostics suppressed (for speculative binding).</summary>
    internal T Speculatively<T>(Func<T> f)
    {
        var saved = _diag;
        var savedMaps = (SymbolMap, TypeMap, BoundMap);
        var savedDecls = DeclarationMap;
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

    internal sealed class Scope
    {
        public Scope(Scope? parent, ScopeKind kind)
        {
            Parent = parent;
            Kind = kind;
        }

        public Scope? Parent { get; }
        public ScopeKind Kind { get; }
        public Dictionary<string, Symbol> Symbols { get; } = new(StringComparer.Ordinal);
        public List<LocalSymbol> Locals { get; } = new();
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

    private void PopScope() => _scope = _scope.Parent!;

    private void DeclareSymbol(string name, Symbol symbol, SyntaxElement? at)
    {
        // duplicates in the same function scope chain (TS: no shadowing within a function for let/const in same block)
        if (_scope.Symbols.ContainsKey(name))
        {
            Report(Descriptors.DuplicateLocal, at, name);
            return;
        }
        _scope.Symbols[name] = symbol;
        if (symbol is LocalSymbol l) _scope.Locals.Add(l);
        if (DeclarationMap != null && at != null)
        {
            var token = at as SyntaxToken ?? (at as ParameterSyntax)?.Identifier ?? (at as SyntaxNode)?.GetFirstToken();
            if (token != null && !token.IsMissing) DeclarationMap[(_file.Tree, token.Start)] = symbol;
        }
    }

    private Symbol? LookupLocal(string name)
    {
        for (var s = _scope; s != null; s = s.Parent)
            if (s.Symbols.TryGetValue(name, out var sym)) return sym;
        return null;
    }

    // ------------------------------------------------------------------ type helpers

    internal NamedTypeSymbol Special(SpecialType t) => _c.GetSpecialType(t);
    internal TypeSymbol VoidType => Special(SpecialType.Void);
    internal TypeSymbol BoolType => Special(SpecialType.Boolean);
    internal TypeSymbol StringType => Special(SpecialType.String);
    internal TypeSymbol ObjectType => Special(SpecialType.Object);
    internal TypeSymbol IntType => Special(SpecialType.Int32);
    internal TypeSymbol DoubleType => Special(SpecialType.Double);
    internal TypeSymbol ErrorType => ErrorTypeSymbol.Instance;

    internal static bool IsError(TypeSymbol? t) => t == null || t.IsErrorType;

    internal BoundBadExpression Bad(SyntaxNode? syntax, params BoundNode[] children) =>
        new(children.ToImmutableArray()) { Syntax = syntax };

    internal BoundBadExpression BadFrom(SyntaxNode? syntax, ImmutableArray<BoundNode> children) =>
        new(children) { Syntax = syntax };

    internal BoundLiteral Literal(object? value, TypeSymbol type, SyntaxNode? syntax = null) =>
        new(new ConstantValue(value), type) { Syntax = syntax };
}
