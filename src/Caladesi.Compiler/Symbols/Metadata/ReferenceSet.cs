using System.Collections.Immutable;
using System.Reflection.Metadata;
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
    private readonly object _lock = new();

    private ReferenceSet()
    {
        GlobalNamespace = new NamespaceSymbol("", null);
    }

    public NamespaceSymbol GlobalNamespace { get; }
    public ImmutableArray<MetadataAssemblySymbol> Assemblies { get; private set; } = ImmutableArray<MetadataAssemblySymbol>.Empty;
    public ImmutableArray<string> Paths { get; private set; } = ImmutableArray<string>.Empty;

    public static ReferenceSet Empty { get; } = new();

    public static ReferenceSet Create(IEnumerable<string> paths, DiagnosticBag? diagnostics = null)
    {
        var set = new ReferenceSet();
        var assemblies = ImmutableArray.CreateBuilder<MetadataAssemblySymbol>();
        var pathList = ImmutableArray.CreateBuilder<string>();
        foreach (var path in paths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var pe = new PEReader(ImmutableArray.Create(bytes));
                if (!pe.HasMetadata) throw new BadImageFormatException("no metadata");
                var asm = new MetadataAssemblySymbol(set, pe, path);
                if (set._byName.ContainsKey(asm.Name)) continue;
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
        foreach (var a in set.Assemblies) a.PopulateNamespaces(set.GlobalNamespace);
        return set;
    }

    public MetadataAssemblySymbol? FindAssembly(string name) => _byName.TryGetValue(name, out var a) ? a : null;

    /// <summary>Finds a type by metadata name, e.g. <c>System.Collections.Generic.List`1</c> or <c>A.B+C</c>.</summary>
    public NamedTypeSymbol? FindType(string metadataName)
    {
        lock (_lock)
        {
            if (_typeCache.TryGetValue(metadataName, out var cached)) return cached;
            NamedTypeSymbol? result = null;
            var plus = metadataName.IndexOf('+');
            if (plus >= 0)
            {
                var outer = FindType(metadataName[..plus]);
                var inner = metadataName[(plus + 1)..];
                result = outer?.GetMembers().OfType<NamedTypeSymbol>().FirstOrDefault(t => t.MetadataName == inner);
            }
            else
            {
                var lastDot = metadataName.LastIndexOf('.');
                var ns = lastDot < 0 ? "" : metadataName[..lastDot];
                var name = lastDot < 0 ? metadataName : metadataName[(lastDot + 1)..];
                var nsSym = GlobalNamespace.FindNamespace(ns);
                var tick = name.IndexOf('`');
                var arity = 0;
                var simple = name;
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
        var i = (int)t;
        var cached = _special[i];
        if (cached != null) return cached;
        var name = t.MetadataName();
        if (name == null) return null;
        return _special[i] = FindType(name);
    }

    internal MetadataAssemblySymbol? CoreLibrary => FindAssembly("System.Runtime") ?? FindAssembly("System.Private.CoreLib") ?? FindAssembly("mscorlib") ?? FindAssembly("netstandard");
}
