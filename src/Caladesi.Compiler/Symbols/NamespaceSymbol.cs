using System.Collections.Immutable;

namespace Caladesi.Compiler.Symbols;

/// <summary>A namespace, merged across all assemblies (including the one being compiled).</summary>
public sealed class NamespaceSymbol : Symbol
{
    private readonly string _name;
    private readonly NamespaceSymbol? _parent;
    private readonly Dictionary<string, NamespaceSymbol> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NamedTypeSymbol>> _types = new(StringComparer.Ordinal);

    private readonly NamespaceSymbol? _underlying;

    public NamespaceSymbol(string name, NamespaceSymbol? parent, NamespaceSymbol? underlying = null)
    {
        _name = name;
        _parent = parent;
        _underlying = underlying;
    }

    public override SymbolKind Kind => SymbolKind.Namespace;
    public override string Name => _name;
    public override Symbol? ContainingSymbol => _parent;
    public override NamespaceSymbol? ContainingNamespace => _parent;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;
    public bool IsGlobalNamespace => _parent == null;

    public NamespaceSymbol GetOrAddNamespace(string dottedName)
    {
        if (dottedName.Length == 0) return this;
        var ns = this;
        foreach (var part in dottedName.Split('.'))
        {
            var child = ns.GetNamespaceMember(part);
            if (child == null)
            {
                child = new NamespaceSymbol(part, ns);
                ns._children[part] = child;
            }
            ns = child;
        }
        return ns;
    }

    public NamespaceSymbol? FindNamespace(string dottedName)
    {
        if (dottedName.Length == 0) return this;
        NamespaceSymbol? ns = this;
        foreach (var part in dottedName.Split('.'))
        {
            ns = ns.GetNamespaceMember(part);
            if (ns == null) return null;
        }
        return ns;
    }

    public void AddType(NamedTypeSymbol type)
    {
        var key = type.MetadataName;
        if (!_types.TryGetValue(key, out var list)) _types[key] = list = new List<NamedTypeSymbol>();
        list.Add(type);
    }

    public NamespaceSymbol? GetNamespaceMember(string name)
    {
        if (_children.TryGetValue(name, out var n)) return n;
        var u = _underlying?.GetNamespaceMember(name);
        if (u == null) return null;
        var child = new NamespaceSymbol(name, this, u);
        _children[name] = child;
        return child;
    }

    public IEnumerable<NamespaceSymbol> GetNamespaceMembers()
    {
        if (_underlying != null)
            foreach (var u in _underlying.GetNamespaceMembers())
                GetNamespaceMember(u.Name);
        return _children.Values.ToList();
    }

    /// <summary>Looks up a top-level type by simple name and arity (metadata name key: <c>List`1</c>).</summary>
    public NamedTypeSymbol? GetTypeMember(string name, int arity)
    {
        var key = arity == 0 ? name : name + "`" + arity;
        if (_types.TryGetValue(key, out var l)) return l[0];
        return _underlying?.GetTypeMember(name, arity);
    }

    public IEnumerable<NamedTypeSymbol> GetTypeMembersByName(string name)
    {
        var seen = new HashSet<string>();
        foreach (var t in GetTypeMembers())
            if (t.Name == name) yield return t;
        _ = seen;
    }

    public IEnumerable<NamedTypeSymbol> GetTypeMembers()
    {
        var seen = new HashSet<string>();
        foreach (var kv in _types)
        {
            seen.Add(kv.Key);
            yield return kv.Value[0];
        }
        if (_underlying != null)
            foreach (var t in _underlying.GetTypeMembers())
                if (seen.Add(t.MetadataName)) yield return t;
    }

    public override ImmutableArray<SourceLocation> Locations => ImmutableArray<SourceLocation>.Empty;

    public string ToDisplayString()
    {
        if (_parent == null) return "";
        var p = _parent.ToDisplayString();
        return p.Length == 0 ? _name : p + "." + _name;
    }

    public override string ToString() => IsGlobalNamespace ? "<global namespace>" : ToDisplayString();
}
