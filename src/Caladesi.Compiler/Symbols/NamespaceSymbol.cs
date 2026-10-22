using System.Collections.Immutable;

namespace Caladesi.Compiler.Symbols;

/// <summary>A namespace, merged across all assemblies (including the one being compiled).</summary>
public sealed class NamespaceSymbol(string name, NamespaceSymbol? parent, NamespaceSymbol? underlying = null) : Symbol
{
    private readonly string _name = name;
    private readonly NamespaceSymbol? _parent = parent;
    private readonly Dictionary<string, NamespaceSymbol> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NamedTypeSymbol>> _types = new(StringComparer.Ordinal);

    private readonly NamespaceSymbol? _underlying = underlying;

    public override SymbolKind Kind => SymbolKind.Namespace;
    public override string Name => _name;
    public override Symbol? ContainingSymbol => _parent;
    public override NamespaceSymbol? ContainingNamespace => _parent;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;
    public bool IsGlobalNamespace => _parent == null;

    public NamespaceSymbol GetOrAddNamespace(string dottedName)
    {
        ArgumentNullException.ThrowIfNull(dottedName);
        if (dottedName.Length == 0)
        {
            return this;
        }

        NamespaceSymbol ns = this;
        foreach (string part in dottedName.Split('.'))
        {
            NamespaceSymbol? child = ns.GetNamespaceMember(part);
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
        ArgumentNullException.ThrowIfNull(dottedName);
        if (dottedName.Length == 0)
        {
            return this;
        }

        NamespaceSymbol? ns = this;
        foreach (string part in dottedName.Split('.'))
        {
            ns = ns.GetNamespaceMember(part);
            if (ns == null)
            {
                return null;
            }
        }
        return ns;
    }

    public void AddType(NamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        string key = type.MetadataName;
        if (!_types.TryGetValue(key, out List<NamedTypeSymbol>? list))
        {
            _types[key] = list = [];
        }

        list.Add(type);
    }

    public NamespaceSymbol? GetNamespaceMember(string name)
    {
        if (_children.TryGetValue(name, out NamespaceSymbol? n))
        {
            return n;
        }

        NamespaceSymbol? u = _underlying?.GetNamespaceMember(name);
        if (u == null)
        {
            return null;
        }

        NamespaceSymbol child = new(name, this, u);
        _children[name] = child;
        return child;
    }

    public IEnumerable<NamespaceSymbol> GetNamespaceMembers()
    {
        if (_underlying != null)
        {
            foreach (NamespaceSymbol u in _underlying.GetNamespaceMembers())
            {
                _ = GetNamespaceMember(u.Name);
            }
        }

        return [.. _children.Values];
    }

    /// <summary>Looks up a top-level type by simple name and arity (metadata name key: <c>List`1</c>).</summary>
    public NamedTypeSymbol? GetTypeMember(string name, int arity)
    {
        string key = arity == 0 ? name : name + "`" + arity;
        return _types.TryGetValue(key, out List<NamedTypeSymbol>? l) ? l[0] : (_underlying?.GetTypeMember(name, arity));
    }

    public IEnumerable<NamedTypeSymbol> GetTypeMembersByName(string name)
    {
        HashSet<string> seen = [];
        foreach (NamedTypeSymbol t in GetTypeMembers())
        {
            if (t.Name == name)
            {
                yield return t;
            }
        }

        _ = seen;
    }

    public IEnumerable<NamedTypeSymbol> GetTypeMembers()
    {
        HashSet<string> seen = [];
        foreach (KeyValuePair<string, List<NamedTypeSymbol>> kv in _types)
        {
            _ = seen.Add(kv.Key);
            yield return kv.Value[0];
        }
        if (_underlying != null)
        {
            foreach (NamedTypeSymbol t in _underlying.GetTypeMembers())
            {
                if (seen.Add(t.MetadataName))
                {
                    yield return t;
                }
            }
        }
    }

    public override ImmutableArray<SourceLocation> Locations => [];

    public string ToDisplayString()
    {
        if (_parent == null)
        {
            return "";
        }

        string p = _parent.ToDisplayString();
        return p.Length == 0 ? _name : p + "." + _name;
    }

    public override string ToString()
    {
        return IsGlobalNamespace ? "<global namespace>" : ToDisplayString();
    }
}
