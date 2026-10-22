using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Emit;

/// <summary>The program after lowering: every type to emit and the final bound body of every method.</summary>
internal sealed class LoweredProgram
{
    public List<NamedTypeSymbol> Types { get; } = [];
    public Dictionary<MethodSymbol, BoundBlock> Bodies { get; } = [];
    /// <summary>Members added to existing types by lowering (e.g. entry-point bridge).</summary>
    public Dictionary<NamedTypeSymbol, List<Symbol>> ExtraMembers { get; } = [];
    public MethodSymbol? EntryPoint { get; set; }
    /// <summary>Methods whose type parameters were re-mapped onto their synthesized generic container: method -> (type parameter -> class ordinal).</summary>
    public Dictionary<MethodSymbol, Dictionary<TypeParameterSymbol, int>> ClassTypeParameterMaps { get; } = [];
    public Dictionary<MethodSymbol, SourceFile> MethodFiles { get; } = [];

    public IEnumerable<Symbol> MembersOf(NamedTypeSymbol t)
    {
        foreach (Symbol m in t.GetMembers())
        {
            yield return m;
        }

        if (ExtraMembers.TryGetValue(t, out List<Symbol>? extra))
        {
            foreach (Symbol m in extra)
            {
                yield return m;
            }
        }
    }
}
