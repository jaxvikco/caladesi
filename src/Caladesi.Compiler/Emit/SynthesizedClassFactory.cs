using System.Collections.Immutable;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>Creates compiler-generated classes (closure environments, state machines) nested in the type that holds a method.</summary>
internal static class SynthesizedClassFactory
{
    /// <summary>
    /// A private sealed class nested in <paramref name="container"/>. When the container or the method is generic the class
    /// declares copies of their type parameters; <paramref name="closed"/> is the class instantiated with the originals,
    /// which is how the original method refers to it. Signatures inside the class map the method's type parameters to the copies.
    /// </summary>
    public static SynthesizedNamedType Create(Compilation c, NamedTypeSymbol container, MethodSymbol method, string name, NamedTypeSymbol? baseType, out NamedTypeSymbol closed)
    {
        var cls = new SynthesizedNamedType(name, container, TypeKind.Class, Accessibility.Private, baseType ?? c.GetSpecialType(SpecialType.Object));
        var classTps = container.TypeParameters;
        var methodTps = method.TypeParameters;
        closed = cls;
        if (classTps.Length + methodTps.Length == 0) return cls;
        var map = new Dictionary<TypeParameterSymbol, int>();
        var tps = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
        var args = ImmutableArray.CreateBuilder<TypeSymbol>();
        if (container is SynthesizedNamedType { MethodTypeParameterMap: { } inherited } enclosing)
        {
            // nested in a closure class of a generic method: the originals are reached through the enclosing class's map
            var byOrdinal = inherited.ToDictionary(kv => kv.Value, kv => kv.Key);
            foreach (var tp in classTps)
            {
                tps.Add(Copy(tp, tp.Ordinal, cls));
                args.Add(byOrdinal.TryGetValue(tp.Ordinal, out var original) ? original : tp);
            }
            foreach (var kv in inherited) map[kv.Key] = kv.Value;
            cls.SetTypeParameters(tps.ToImmutable());
            cls.OwnArityValue = 0;
            cls.MethodTypeParameterMap = map;
            closed = cls.Construct(args.ToImmutable());
            _ = enclosing;
            return cls;
        }
        foreach (var tp in classTps)
        {
            tps.Add(Copy(tp, tp.Ordinal, cls));
            args.Add(tp);
        }
        foreach (var tp in methodTps)
        {
            var ordinal = classTps.Length + tp.Ordinal;
            tps.Add(Copy(tp, ordinal, cls));
            args.Add(tp);
            map[tp] = ordinal;
        }
        cls.SetTypeParameters(tps.ToImmutable());
        cls.OwnArityValue = methodTps.Length;
        cls.MethodTypeParameterMap = map;
        closed = cls.Construct(args.ToImmutable());
        return cls;
    }

    private static TypeParameterSymbol Copy(TypeParameterSymbol tp, int ordinal, Symbol container) =>
        new(tp.Name, ordinal, container, VarianceKind.None, _ => new TypeParameterConstraints(
            tp.HasReferenceTypeConstraint, tp.HasValueTypeConstraint, tp.HasConstructorConstraint && !tp.HasValueTypeConstraint, tp.HasNotNullConstraint, tp.ConstraintTypes));

    /// <summary>The field of the instantiated class that corresponds to <paramref name="def"/>.</summary>
    public static FieldSymbol ClosedField(NamedTypeSymbol closed, FieldSymbol def) =>
        ReferenceEquals(closed, def.ContainingSymbol) ? def : closed.GetMembers(def.Name).OfType<FieldSymbol>().First();

    public static MethodSymbol ClosedMethod(NamedTypeSymbol closed, MethodSymbol def) =>
        ReferenceEquals(closed, def.ContainingSymbol) ? def : closed.GetMembers(def.Name).OfType<MethodSymbol>().First(m => m.Parameters.Length == def.Parameters.Length);
}
