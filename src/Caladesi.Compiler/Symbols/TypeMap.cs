using System.Collections.Immutable;

namespace Caladesi.Compiler.Symbols;

/// <summary>A substitution of type parameters by type arguments.</summary>
public sealed class TypeMap
{
    private readonly Dictionary<TypeParameterSymbol, TypeSymbol> _map = new();

    public TypeMap(ImmutableArray<TypeParameterSymbol> from, ImmutableArray<TypeSymbol> to)
    {
        for (var i = 0; i < from.Length && i < to.Length; i++) _map[from[i]] = to[i];
    }

    private TypeMap(Dictionary<TypeParameterSymbol, TypeSymbol> map) => _map = map;

    public bool IsEmpty => _map.Count == 0;

    public TypeMap Extend(ImmutableArray<TypeParameterSymbol> from, ImmutableArray<TypeSymbol> to)
    {
        var d = new Dictionary<TypeParameterSymbol, TypeSymbol>(_map);
        for (var i = 0; i < from.Length && i < to.Length; i++) d[from[i]] = to[i];
        return new TypeMap(d);
    }

    public bool TryGet(TypeParameterSymbol p, out TypeSymbol t) => _map.TryGetValue(p, out t!);

    public TypeSymbol Substitute(TypeSymbol type)
    {
        switch (type)
        {
            case TypeParameterSymbol tp:
                return _map.TryGetValue(tp, out var r) ? r : tp;
            case ArrayTypeSymbol a:
            {
                var e = Substitute(a.ElementType);
                return ReferenceEquals(e, a.ElementType) ? a : a.WithElementType(e);
            }
            case NullableTypeSymbol n:
            {
                var u = Substitute(n.Underlying);
                if (ReferenceEquals(u, n.Underlying)) return n;
                if (u is NullableTypeSymbol) return u;
                // C#-style T? on an unconstrained type parameter: a value type argument stays non-nullable.
                if (n.Underlying is TypeParameterSymbol { HasValueTypeConstraint: false, HasReferenceTypeConstraint: false } && u.IsValueType) return u;
                return new NullableTypeSymbol(u);
            }
            case NamedTypeSymbol nt when nt.IsGenericType:
            {
                var args = nt.TypeArguments;
                ImmutableArray<TypeSymbol>.Builder? b = null;
                for (var i = 0; i < args.Length; i++)
                {
                    var s = Substitute(args[i]);
                    if (!ReferenceEquals(s, args[i]) && b == null)
                    {
                        b = ImmutableArray.CreateBuilder<TypeSymbol>(args.Length);
                        for (var j = 0; j < i; j++) b.Add(args[j]);
                    }
                    b?.Add(s);
                }
                if (b == null) return nt;
                var def = nt.OriginalDefinition;
                return new ConstructedNamedType(def, b.MoveToImmutable(), nt.TupleElementNames);
            }
            default:
                return type;
        }
    }
}
