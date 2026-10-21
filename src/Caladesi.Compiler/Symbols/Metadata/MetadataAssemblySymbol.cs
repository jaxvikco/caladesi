using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Caladesi.Compiler.Symbols.Metadata;

public sealed class MetadataAssemblySymbol : AssemblySymbol
{
    private readonly ReferenceSet _set;
    private readonly PEReader _pe;
    private readonly Dictionary<TypeDefinitionHandle, MetadataNamedType> _types = new();
    private readonly Dictionary<string, TypeForwardTarget?> _forwarders = new(StringComparer.Ordinal);
    private NamespaceSymbol? _ownGlobal;

    private sealed record TypeForwardTarget(string AssemblyName);

    public MetadataAssemblySymbol(ReferenceSet set, PEReader pe, string path)
    {
        _set = set;
        _pe = pe;
        Path = path;
        Reader = pe.GetMetadataReader();
        var def = Reader.GetAssemblyDefinition();
        _name = Reader.GetString(def.Name);
        _version = def.Version;
        Provider = new MetadataTypeProvider(this);
    }

    private readonly string _name;
    private readonly Version _version;
    public string Path { get; }
    public MetadataReader Reader { get; }
    internal MetadataTypeProvider Provider { get; }
    public ReferenceSet ReferenceSet => _set;
    public override string Name => _name;
    public override Version Version => _version;
    public override NamespaceSymbol GlobalNamespace => _ownGlobal ?? _set.GlobalNamespace;

    internal void PopulateNamespaces(NamespaceSymbol global)
    {
        _ownGlobal = global;
        foreach (var h in Reader.TypeDefinitions)
        {
            var td = Reader.GetTypeDefinition(h);
            var vis = td.Attributes & TypeAttributes.VisibilityMask;
            if (vis != TypeAttributes.Public) continue; // top-level public types only; nested are members
            var ns = Reader.GetString(td.Namespace);
            var nsSym = global.GetOrAddNamespace(ns);
            var type = GetTypeDefinition(h);
            // the first definition wins when several assemblies define the same type
            if (nsSym.GetTypeMember(type.Name, type.OwnArity) == null) nsSym.AddType(type);
        }
        // type forwarders are resolved lazily through FindType fallback
    }

    internal MetadataNamedType GetTypeDefinition(TypeDefinitionHandle h)
    {
        if (!_types.TryGetValue(h, out var t))
        {
            t = new MetadataNamedType(this, h);
            _types[h] = t;
        }
        return t;
    }

    internal NamedTypeSymbol ResolveTypeReference(TypeReferenceHandle h)
    {
        var tr = Reader.GetTypeReference(h);
        var name = Reader.GetString(tr.Name);
        var scope = tr.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
        {
            var outer = ResolveTypeReference((TypeReferenceHandle)scope);
            var found = outer.GetMembers().OfType<NamedTypeSymbol>().FirstOrDefault(n => n.MetadataName == name);
            return found ?? new ErrorTypeSymbol(name);
        }
        var ns = Reader.GetString(tr.Namespace);
        var full = ns.Length == 0 ? name : ns + "." + name;
        string? asmName = null;
        if (scope.Kind == HandleKind.AssemblyReference)
            asmName = Reader.GetString(Reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        else if (scope.Kind == HandleKind.ModuleDefinition || scope.Kind == HandleKind.ModuleReference)
            asmName = _name;

        if (asmName != null)
        {
            var asm = _set.FindAssembly(asmName);
            var t = asm?.FindDefinedType(ns, name);
            if (t != null) return t;
        }
        // type forwarding / unexpected layout: search every referenced assembly
        var global = _set.FindType(full);
        return global ?? new ErrorTypeSymbol(full);
    }

    private Dictionary<(string, string), TypeDefinitionHandle>? _defIndex;

    internal MetadataNamedType? FindDefinedType(string ns, string metadataName)
    {
        if (_defIndex == null)
        {
            var d = new Dictionary<(string, string), TypeDefinitionHandle>();
            foreach (var h in Reader.TypeDefinitions)
            {
                var td = Reader.GetTypeDefinition(h);
                if (!td.GetDeclaringType().IsNil) continue;
                d.TryAdd((Reader.GetString(td.Namespace), Reader.GetString(td.Name)), h);
            }
            _defIndex = d;
        }
        return _defIndex.TryGetValue((ns, metadataName), out var handle) ? GetTypeDefinition(handle) : null;
    }
}
