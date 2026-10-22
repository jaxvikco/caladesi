using System.Collections.Immutable;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler;

#pragma warning disable CA1724 // the name is part of the public compiler API
public sealed partial class Compilation
#pragma warning restore CA1724
{
    private readonly Dictionary<string, SynthesizedAnonymousRecord> _anonymousRecords = new(StringComparer.Ordinal);
    private readonly List<SynthesizedAnonymousRecord> _anonymousRecordList = [];

    internal IReadOnlyList<SynthesizedAnonymousRecord> AnonymousRecords => _anonymousRecordList;

    /// <summary>Gets the instantiated anonymous record type for an object literal with the given property names and types.</summary>
    internal NamedTypeSymbol GetAnonymousRecord(IReadOnlyList<string> names, IReadOnlyList<TypeSymbol> types)
    {
        EnsureDeclarations();
        string key = string.Join("|", names);
        SynthesizedAnonymousRecord def;
        lock (_lock)
        {
            if (!_anonymousRecords.TryGetValue(key, out SynthesizedAnonymousRecord? existing))
            {
                SourceFile file = _files[SyntaxTrees[0]];
                existing = new SynthesizedAnonymousRecord(this, file, "<>f__AnonymousType" + _anonymousRecordList.Count, [.. names]);
                _anonymousRecords[key] = existing;
                _anonymousRecordList.Add(existing);
            }
            def = existing;
        }
        return def.Construct(types.ToImmutableArray());
    }
}
