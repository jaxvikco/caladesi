using System.Collections.Immutable;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler;

public sealed partial class Compilation
{
    private readonly Dictionary<string, SynthesizedAnonymousRecord> _anonymousRecords = new(StringComparer.Ordinal);
    private readonly List<SynthesizedAnonymousRecord> _anonymousRecordList = new();

    internal IReadOnlyList<SynthesizedAnonymousRecord> AnonymousRecords => _anonymousRecordList;

    /// <summary>Gets the instantiated anonymous record type for an object literal with the given property names and types.</summary>
    internal NamedTypeSymbol GetAnonymousRecord(IReadOnlyList<string> names, IReadOnlyList<TypeSymbol> types)
    {
        EnsureDeclarations();
        var key = string.Join("|", names);
        SynthesizedAnonymousRecord def;
        lock (_lock)
        {
            if (!_anonymousRecords.TryGetValue(key, out var existing))
            {
                var file = _files[SyntaxTrees[0]];
                existing = new SynthesizedAnonymousRecord(this, file, "<>f__AnonymousType" + _anonymousRecordList.Count, names.ToImmutableArray());
                _anonymousRecords[key] = existing;
                _anonymousRecordList.Add(existing);
            }
            def = existing;
        }
        return def.Construct(types.ToImmutableArray());
    }
}
