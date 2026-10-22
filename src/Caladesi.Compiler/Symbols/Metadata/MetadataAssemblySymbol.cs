using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Caladesi.Compiler.Symbols.Metadata;

public sealed class MetadataAssemblySymbol : AssemblySymbol
{
    // Held only to keep the PEReader (and the image memory the MetadataReader points into) alive for this symbol's lifetime.
#pragma warning disable IDE0052
    private readonly PEReader _pe;
#pragma warning restore IDE0052
    private readonly Dictionary<TypeDefinitionHandle, MetadataNamedType> _types = [];
    private NamespaceSymbol? _ownGlobal;

    public MetadataAssemblySymbol(ReferenceSet set, PEReader pe, string path)
    {
        ReferenceSet = set;
        _pe = pe;
        Path = path;
        Reader = pe.GetMetadataReader();
        AssemblyDefinition def = Reader.GetAssemblyDefinition();
        _name = Reader.GetString(def.Name);
        _version = def.Version;
        Provider = new MetadataTypeProvider(this);
    }

    private readonly string _name;
    private readonly Version _version;
    public string Path { get; }
    public MetadataReader Reader { get; }
    internal MetadataTypeProvider Provider { get; }
    public ReferenceSet ReferenceSet { get; }
    public override string Name => _name;
    public override Version Version => _version;
    public override NamespaceSymbol GlobalNamespace => _ownGlobal ?? ReferenceSet.GlobalNamespace;

    internal void PopulateNamespaces(NamespaceSymbol global)
    {
        _ownGlobal = global;
        foreach (TypeDefinitionHandle h in Reader.TypeDefinitions)
        {
            TypeDefinition td = Reader.GetTypeDefinition(h);
            TypeAttributes vis = td.Attributes & TypeAttributes.VisibilityMask;
            if (vis != TypeAttributes.Public)
            {
                continue; // top-level public types only; nested are members
            }

            string ns = Reader.GetString(td.Namespace);
            NamespaceSymbol nsSym = global.GetOrAddNamespace(ns);
            MetadataNamedType type = GetTypeDefinition(h);
            // the first definition wins when several assemblies define the same type
            if (nsSym.GetTypeMember(type.Name, type.OwnArity) == null)
            {
                nsSym.AddType(type);
            }
        }
        // type forwarders are resolved lazily through FindType fallback
    }

    internal MetadataNamedType GetTypeDefinition(TypeDefinitionHandle h)
    {
        if (!_types.TryGetValue(h, out MetadataNamedType? t))
        {
            t = new MetadataNamedType(this, h);
            _types[h] = t;
        }
        return t;
    }

    internal NamedTypeSymbol ResolveTypeReference(TypeReferenceHandle h)
    {
        TypeReference tr = Reader.GetTypeReference(h);
        string name = Reader.GetString(tr.Name);
        EntityHandle scope = tr.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
        {
            NamedTypeSymbol outer = ResolveTypeReference((TypeReferenceHandle)scope);
            NamedTypeSymbol? found = outer.GetMembers().OfType<NamedTypeSymbol>().FirstOrDefault(n => n.MetadataName == name);
            return found ?? new ErrorTypeSymbol(name);
        }
        string ns = Reader.GetString(tr.Namespace);
        string full = ns.Length == 0 ? name : ns + "." + name;
        string? asmName = null;
        if (scope.Kind == HandleKind.AssemblyReference)
        {
            asmName = Reader.GetString(Reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        }
        else if (scope.Kind is HandleKind.ModuleDefinition or HandleKind.ModuleReference)
        {
            asmName = _name;
        }

        if (asmName != null)
        {
            MetadataAssemblySymbol? asm = ReferenceSet.FindAssembly(asmName);
            MetadataNamedType? t = asm?.FindDefinedType(ns, name);
            if (t != null)
            {
                return t;
            }
        }
        // type forwarding / unexpected layout: search every referenced assembly
        NamedTypeSymbol? global = ReferenceSet.FindType(full);
        return global ?? new ErrorTypeSymbol(full);
    }

    private Dictionary<(string, string), TypeDefinitionHandle>? _defIndex;

    internal MetadataNamedType? FindDefinedType(string ns, string metadataName)
    {
        if (_defIndex == null)
        {
            Dictionary<(string, string), TypeDefinitionHandle> d = [];
            foreach (TypeDefinitionHandle h in Reader.TypeDefinitions)
            {
                TypeDefinition td = Reader.GetTypeDefinition(h);
                if (!td.GetDeclaringType().IsNil)
                {
                    continue;
                }

                _ = d.TryAdd((Reader.GetString(td.Namespace), Reader.GetString(td.Name)), h);
            }
            _defIndex = d;
        }
        return _defIndex.TryGetValue((ns, metadataName), out TypeDefinitionHandle handle) ? GetTypeDefinition(handle) : null;
    }
}
