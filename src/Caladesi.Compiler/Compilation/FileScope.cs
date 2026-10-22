using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler;

/// <summary>A local source file referenced as a module (<c>import * as G from "./geometry"</c>).</summary>
public sealed class FileModuleSymbol(SourceFile file, string alias) : Symbol
{
    public SourceFile File { get; } = file;
    public override SymbolKind Kind => SymbolKind.Alias;
    public override string Name => alias;
}

/// <summary>Module-level name resolution for one file: its own declarations, named imports, aliases and wildcard imports.</summary>
public sealed class FileScope(SourceFile file)
{
    private readonly SourceFile _file = file;
    private Dictionary<string, List<Symbol>>? _named;
    private Dictionary<string, Symbol>? _aliases;
    private List<Symbol>? _wildcards;
    private List<ImportDiagnostic>? _importDiagnostics;
    private bool _importsResolved;

    private readonly record struct ImportDiagnostic(DiagnosticDescriptor Descriptor, TextSpan Span, object?[] Args);

    private Compilation Compilation => _file.Compilation;

    // ------------------------------------------------------------------ own declarations

    public Dictionary<string, List<Symbol>> Declared
    {
        get
        {
            if (field != null)
            {
                return field;
            }

            Dictionary<string, List<Symbol>> d = new(StringComparer.Ordinal);
            void Add(string n, Symbol s)
            {
                if (!d.TryGetValue(n, out List<Symbol>? l))
                {
                    d[n] = l = [];
                }

                l.Add(s);
            }
            foreach (SourceNamedType t in _file.Types)
            {
                Add(t.Name, t);
            }

            foreach (TypeAliasSymbol a in _file.Aliases)
            {
                Add(a.Name, a);
            }

            if (_file.ModuleClass != null)
            {
                foreach (Symbol m in _file.ModuleClass.GetMembers())
                {
                    if (m is MethodSymbol { MethodKind: MethodKind.Ordinary } ms)
                    {
                        Add(ms.Name, ms);
                    }
                    else if (m is FieldSymbol fs && fs is SourceFieldSymbol { IsModuleVariable: true })
                    {
                        Add(fs.Name, fs);
                    }
                }
            }

            field = d;
            return d;
        }
    }

    /// <summary>Symbols that other files may import from this one (exported members).</summary>
    public IEnumerable<Symbol> GetExports(string name)
    {
        if (!Declared.TryGetValue(name, out List<Symbol>? l))
        {
            yield break;
        }

        foreach (Symbol s in l)
        {
            Accessibility acc = s switch
            {
                TypeAliasSymbol a => a.IsExported ? Accessibility.Public : Accessibility.Internal,
                _ => s.DeclaredAccessibility,
            };
            if (acc == Accessibility.Public)
            {
                yield return s;
            }
        }
    }

    public IEnumerable<Symbol> AllExports()
    {
        foreach (KeyValuePair<string, List<Symbol>> kv in Declared)
        {
            foreach (Symbol s in GetExports(kv.Key))
            {
                yield return s;
            }
        }
    }

    // ------------------------------------------------------------------ imports

    private void ResolveImports()
    {
        if (_importsResolved)
        {
            return;
        }

        _importsResolved = true;
        _named = new Dictionary<string, List<Symbol>>(StringComparer.Ordinal);
        _aliases = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        _wildcards = [];
        _importDiagnostics = [];

        foreach (string g in Compilation.Options.GlobalImports)
        {
            NamespaceSymbol? ns = Compilation.GlobalNamespace.FindNamespace(g);
            if (ns != null)
            {
                _wildcards.Add(ns);
            }
        }

        foreach (ImportDeclarationSyntax imp in _file.Imports)
        {
            string spec = imp.ModuleName;
            TextSpan specSpan = imp.ModuleSpecifier.Span;
            if (imp.ModuleSpecifier.IsMissing)
            {
                continue;
            }

            Symbol? target = ResolveModule(spec);
            if (target == null)
            {
                _importDiagnostics.Add(new ImportDiagnostic(Descriptors.ModuleNotFound, specSpan, [spec]));
                continue;
            }
            switch (imp.Clause)
            {
                case null:
                    _wildcards.Add(target);
                    break;
                case NamespaceImportSyntax ni:
                    if (!ni.AliasToken.IsMissing)
                    {
                        _aliases[ni.AliasToken.Text] = target is FileModuleSymbol fm ? new FileModuleSymbol(fm.File, ni.AliasToken.Text) : target;
                    }

                    break;
                case NamedImportsSyntax named:
                    foreach (ImportSpecifierSyntax spc in named.Specifiers)
                    {
                        if (spc.NameToken.IsMissing)
                        {
                            continue;
                        }

                        List<Symbol> members = [.. LookupMemberOfModule(target, spc.NameToken.Text)];
                        // attributes can be imported without their `Attribute` suffix, as they are used in decorators
                        if (members.Count == 0 && target is NamespaceSymbol)
                        {
                            members = [.. LookupMemberOfModule(target, spc.NameToken.Text + "Attribute")];
                        }

                        if (members.Count == 0)
                        {
                            _importDiagnostics.Add(new ImportDiagnostic(Descriptors.ModuleHasNoExport, spc.NameToken.Span, [spec, spc.NameToken.Text]));
                            continue;
                        }
                        if (Compilation.Options.RecordSemanticInfo)
                        {
                            Compilation.EnsureRecording();
                            Compilation.RecordedSymbols![spc] = members[0];
                        }
                        string local = spc.LocalName;
                        if (!_named.TryGetValue(local, out List<Symbol>? l))
                        {
                            _named[local] = l = [];
                        }

                        l.AddRange(members);
                    }
                    break;
            }
        }
    }

    /// <summary>Resolves a module specifier to a namespace, a type (static members import), or a source file.</summary>
    private Symbol? ResolveModule(string spec)
    {
        if (spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal))
        {
            SourceFile? f = Compilation.FindSourceFile(_file, spec);
            return f == null ? null : new FileModuleSymbol(f, f.ModuleClassBaseName);
        }
        NamespaceSymbol? ns = Compilation.GlobalNamespace.FindNamespace(spec);
        if (ns != null)
        {
            return ns;
        }

        NamedTypeSymbol? type = Compilation.FindTypeByFullName(spec);
        return type;
    }

    private static IEnumerable<Symbol> LookupMemberOfModule(Symbol module, string name)
    {
        switch (module)
        {
            case NamespaceSymbol ns:
                foreach (NamedTypeSymbol t in ns.GetTypeMembersByName(name))
                {
                    yield return t;
                }

                NamespaceSymbol? child = ns.GetNamespaceMember(name);
                if (child != null)
                {
                    yield return child;
                }

                break;
            case NamedTypeSymbol nt:
                foreach (Symbol m in nt.GetMembers(name))
                {
                    if (m.IsStatic || m is NamedTypeSymbol)
                    {
                        yield return m;
                    }
                }

                break;
            case FileModuleSymbol fm:
                foreach (Symbol s in fm.File.Scope.GetExports(name))
                {
                    yield return s;
                }

                break;
        }
    }

    public ImmutableArray<Diagnostic> GetImportDiagnostics()
    {
        ResolveImports();
        ImmutableArray<Diagnostic>.Builder b = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (ImportDiagnostic d in _importDiagnostics!)
        {
            b.Add(Diagnostic.Create(d.Descriptor, _file.FilePath, d.Span, d.Args));
        }

        return b.ToImmutable();
    }

    // ------------------------------------------------------------------ lookup

    /// <summary>Looks up a simple name at module level. Returns all candidates at the first level that has any.</summary>
    public ImmutableArray<Symbol> Lookup(string name)
    {
        if (Declared.TryGetValue(name, out List<Symbol>? own) && own.Count > 0)
        {
            return [.. own];
        }

        ResolveImports();
        if (_named!.TryGetValue(name, out List<Symbol>? imported) && imported.Count > 0)
        {
            return [.. imported];
        }

        if (_aliases!.TryGetValue(name, out Symbol? alias))
        {
            return [alias];
        }

        List<Symbol> result = [];
        foreach (Symbol w in _wildcards!)
        {
            foreach (Symbol m in LookupMemberOfModule(w, name))
            {
                if (!result.Contains(m))
                {
                    result.Add(m);
                }
            }
        }

        if (result.Count > 0)
        {
            return [.. result];
        }

        NamespaceSymbol g = Compilation.GlobalNamespace;
        NamespaceSymbol? gn = g.GetNamespaceMember(name);
        if (gn != null)
        {
            result.Add(gn);
        }

        foreach (NamedTypeSymbol t in g.GetTypeMembersByName(name))
        {
            result.Add(t);
        }

        return [.. result];
    }

    /// <summary>Every name usable at module level in this file (own declarations, imports, global namespaces and types).</summary>
    public IEnumerable<(string Name, Symbol Symbol)> VisibleNames()
    {
        foreach (KeyValuePair<string, List<Symbol>> kv in Declared)
        {
            foreach (Symbol s in kv.Value)
            {
                yield return (kv.Key, s);
            }
        }

        ResolveImports();
        foreach (KeyValuePair<string, List<Symbol>> kv in _named!)
        {
            foreach (Symbol s in kv.Value)
            {
                yield return (kv.Key, s);
            }
        }

        foreach (KeyValuePair<string, Symbol> kv in _aliases!)
        {
            yield return (kv.Key, kv.Value);
        }

        foreach (Symbol w in _wildcards!)
        {
            if (w is NamespaceSymbol ns)
            {
                foreach (NamedTypeSymbol? t in ns.GetTypeMembers().Where(t => t.DeclaredAccessibility == Accessibility.Public))
                {
                    yield return (t.Name, t);
                }
            }
            else if (w is NamedTypeSymbol nt)
            {
                foreach (Symbol? m in nt.GetMembers().Where(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public))
                {
                    yield return (m.Name, m);
                }
            }
        }

        NamespaceSymbol g = Compilation.GlobalNamespace;
        foreach (NamespaceSymbol n in g.GetNamespaceMembers())
        {
            yield return (n.Name, n);
        }

        foreach (NamedTypeSymbol t in g.GetTypeMembers())
        {
            yield return (t.Name, t);
        }
    }

    /// <summary>Static type containers and namespaces imported with wildcard imports (for extension-method lookup).</summary>
    public IReadOnlyList<Symbol> WildcardImports
    {
        get
        {
            ResolveImports();
            return _wildcards!;
        }
    }

    public IEnumerable<Symbol> NamedImportSymbols
    {
        get
        {
            ResolveImports();
            return _named!.Values.SelectMany(l => l);
        }
    }
}
