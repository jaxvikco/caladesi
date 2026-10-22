using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using Caladesi.Compiler.Diagnostics;

namespace Caladesi.Compiler.Symbols.Metadata;

/// <summary>
/// The set of referenced assemblies and their merged namespace tree. Immutable after creation and shareable
/// between compilations (the language server reuses it across edits).
/// </summary>
public sealed class ReferenceSet
{
    private readonly Dictionary<string, MetadataAssemblySymbol> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NamedTypeSymbol?> _typeCache = new(StringComparer.Ordinal);
    private readonly NamedTypeSymbol?[] _special = new NamedTypeSymbol?[Enum.GetValues<SpecialType>().Length + 1];
    private readonly Lock _lock = new();

    private ReferenceSet()
    {
        GlobalNamespace = new NamespaceSymbol("", null);
    }

    public NamespaceSymbol GlobalNamespace { get; }
    public ImmutableArray<MetadataAssemblySymbol> Assemblies { get; private set; } = [];
    public ImmutableArray<string> Paths { get; private set; } = [];

    public static ReferenceSet Empty { get; } = new();

    public static ReferenceSet Create(IEnumerable<string> paths, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ReferenceSet set = new();
        ImmutableArray<MetadataAssemblySymbol>.Builder assemblies = ImmutableArray.CreateBuilder<MetadataAssemblySymbol>();
        ImmutableArray<string>.Builder pathList = ImmutableArray.CreateBuilder<string>();
        foreach (string path in paths)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
#pragma warning disable CA2000 // ownership of the PEReader passes to MetadataAssemblySymbol, which keeps it alive for the compilation
                PEReader pe = new(ImmutableArray.Create(bytes));
#pragma warning restore CA2000
                if (!pe.HasMetadata)
                {
                    throw new BadImageFormatException("no metadata");
                }

                MetadataAssemblySymbol asm = new(set, pe, path);
                if (set._byName.ContainsKey(asm.Name))
                {
                    continue;
                }

                set._byName[asm.Name] = asm;
                assemblies.Add(asm);
                pathList.Add(path);
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidOperationException)
            {
                diagnostics?.Add(Diagnostic.Create(Descriptors.CannotReadReference, path, default, path, e.Message));
            }
        }
        set.Assemblies = assemblies.ToImmutable();
        set.Paths = pathList.ToImmutable();
        foreach (MetadataAssemblySymbol a in set.Assemblies)
        {
            a.PopulateNamespaces(set.GlobalNamespace);
        }

        return set;
    }

    public MetadataAssemblySymbol? FindAssembly(string name)
    {
        return _byName.TryGetValue(name, out MetadataAssemblySymbol? a) ? a : null;
    }

    /// <summary>Finds a type by metadata name, e.g. <c>System.Collections.Generic.List`1</c> or <c>A.B+C</c>.</summary>
    public NamedTypeSymbol? FindType(string metadataName)
    {
        ArgumentNullException.ThrowIfNull(metadataName);
        lock (_lock)
        {
            if (_typeCache.TryGetValue(metadataName, out NamedTypeSymbol? cached))
            {
                return cached;
            }

            NamedTypeSymbol? result = null;
            int plus = metadataName.IndexOf('+');
            if (plus >= 0)
            {
                NamedTypeSymbol? outer = FindType(metadataName[..plus]);
                string inner = metadataName[(plus + 1)..];
                result = outer?.GetMembers().OfType<NamedTypeSymbol>().FirstOrDefault(t => t.MetadataName == inner);
            }
            else
            {
                int lastDot = metadataName.LastIndexOf('.');
                string ns = lastDot < 0 ? "" : metadataName[..lastDot];
                string name = lastDot < 0 ? metadataName : metadataName[(lastDot + 1)..];
                NamespaceSymbol? nsSym = GlobalNamespace.FindNamespace(ns);
                int tick = name.IndexOf('`');
                int arity = 0;
                string simple = name;
                if (tick >= 0)
                {
                    arity = int.Parse(name[(tick + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                    simple = name[..tick];
                }
                result = nsSym?.GetTypeMember(simple, arity);
            }
            _typeCache[metadataName] = result;
            return result;
        }
    }

    public NamedTypeSymbol? GetSpecialType(SpecialType t)
    {
        int i = (int)t;
        NamedTypeSymbol? cached = _special[i];
        if (cached != null)
        {
            return cached;
        }

        string? name = t.MetadataName();
        return name == null ? null : (_special[i] = FindType(name));
    }

    internal MetadataAssemblySymbol? CoreLibrary => FindAssembly("System.Runtime") ?? FindAssembly("System.Private.CoreLib") ?? FindAssembly("mscorlib") ?? FindAssembly("netstandard");
}
