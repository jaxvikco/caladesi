namespace Caladesi.Compiler.Symbols;

/// <summary>Walks the types whose members are available through a receiver's lookup type.</summary>
internal static class MemberLookup
{
    /// <param name="receiver">The receiver type whose members are being looked up.</param>
    /// <param name="objectType">The root object type, available on interface and type-parameter receivers.</param>
    /// <param name="descend">Returns false to skip this type's bases or constraints while continuing sibling branches.</param>
    public static void Walk(TypeSymbol receiver, TypeSymbol objectType, Func<TypeSymbol, bool> descend)
    {
        HashSet<TypeSymbol> visited = [];

        void Visit(TypeSymbol type)
        {
            if (type is NullableTypeSymbol nullable)
            {
                type = nullable.Underlying;
            }

            if (!visited.Add(type) || !descend(type))
            {
                return;
            }

            if (type is TypeParameterSymbol parameter)
            {
                foreach (TypeSymbol constraint in parameter.ConstraintTypes)
                {
                    Visit(constraint);
                }

                Visit(objectType);
                return;
            }

            if (type.TypeKind == TypeKind.Interface)
            {
                foreach (NamedTypeSymbol parent in type.Interfaces)
                {
                    Visit(parent);
                }

                Visit(objectType);
                return;
            }

            if (type.BaseType is { } baseType)
            {
                Visit(baseType);
            }
        }

        Visit(receiver);
    }
}
