using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    /// <summary>The type whose members are accessible without qualification (the enclosing class).</summary>
    internal NamedTypeSymbol? CurrentType
    {
        get
        {
            for (var s = _containing; s != null; s = s.ContainingSymbol)
                if (s is NamedTypeSymbol nt) return nt;
            return null;
        }
    }

    internal bool IsAccessible(Symbol member, NamedTypeSymbol? from = null)
    {
        from ??= CurrentType;
        switch (member.DeclaredAccessibility)
        {
            case Accessibility.Public:
            case Accessibility.NotApplicable:
                return true;
            case Accessibility.Internal:
            case Accessibility.ProtectedAndInternal:
                return member.IsFromSource || member.ContainingType?.IsFromSource == true;
            case Accessibility.Protected:
            case Accessibility.ProtectedOrInternal:
            {
                if (member.IsFromSource && member.DeclaredAccessibility == Accessibility.ProtectedOrInternal) return true;
                var owner = member.ContainingType?.OriginalDefinition;
                for (var t = from; t != null; t = t.ContainingType)
                    for (var b = (NamedTypeSymbol?)t; b != null; b = b.BaseType)
                        if (ReferenceEquals(b.OriginalDefinition, owner)) return true;
                return false;
            }
            case Accessibility.Private:
            {
                var owner = member.ContainingType?.OriginalDefinition;
                for (var t = from; t != null; t = t.ContainingType)
                    if (ReferenceEquals(t.OriginalDefinition, owner)) return true;
                return false;
            }
        }
        return false;
    }

    private static bool IsSpecialAccessor(Symbol s) =>
        s is MethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.StaticConstructor };

    /// <summary>Looks up members named <paramref name="name"/> in a type and its bases / interfaces.</summary>
    internal ImmutableArray<Symbol> LookupMembers(TypeSymbol type, string name, NamedTypeSymbol? from = null)
    {
        var result = new List<Symbol>();
        var visited = new HashSet<TypeSymbol>();
        var foundNonMethod = false;

        void AddFrom(TypeSymbol t, bool isInterfaceWalk)
        {
            foreach (var m in t.GetMembers(name))
            {
                if (IsSpecialAccessor(m)) continue;
                if (m is MethodSymbol { MethodKind: MethodKind.Constructor }) continue;
                if (!IsAccessible(m, from)) continue;
                if (m is MethodSymbol ms)
                {
                    var hidden = false;
                    foreach (var r in result)
                        if (r is MethodSymbol rm && rm.Arity == ms.Arity && OverrideResolver.SignaturesMatch(rm, ms) && rm.Name == ms.Name) { hidden = true; break; }
                    if (!hidden) result.Add(ms);
                }
                else if (!foundNonMethod || m is NamedTypeSymbol)
                {
                    result.Add(m);
                }
            }
        }

        void Walk(TypeSymbol t)
        {
            if (t is NullableTypeSymbol nt) t = nt.Underlying;
            if (!visited.Add(t)) return;
            AddFrom(t, false);
            if (result.Any(r => r is not MethodSymbol and not NamedTypeSymbol)) foundNonMethod = true;
            if (foundNonMethod && result.All(r => r is not MethodSymbol)) return;
            if (t is TypeParameterSymbol tp)
            {
                foreach (var c in tp.ConstraintTypes) Walk(c);
                Walk(ObjectType);
                return;
            }
            if (t.TypeKind == TypeKind.Interface)
            {
                foreach (var i in t.Interfaces) Walk(i);
                Walk(ObjectType);
                return;
            }
            if (t is ArrayTypeSymbol)
            {
                if (t.BaseType != null) Walk(t.BaseType);
                return;
            }
            if (t.BaseType != null) Walk(t.BaseType);
        }

        Walk(type);
        // members from interfaces for classes are needed only for explicit interface calls on class-typed receivers: skip
        return result.ToImmutableArray();
    }

    /// <summary>Instance + static constructors accessible from here.</summary>
    internal ImmutableArray<MethodSymbol> LookupConstructors(NamedTypeSymbol type) =>
        type.GetMembers().OfType<MethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Constructor && IsAccessible(m))
            .ToImmutableArray();

    // ------------------------------------------------------------------ extension methods

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NamespaceSymbol, Dictionary<string, List<MethodSymbol>>> s_extensionIndex = new();

    private static Dictionary<string, List<MethodSymbol>> GetNamespaceExtensions(NamespaceSymbol ns)
    {
        return s_extensionIndex.GetValue(ns, n =>
        {
            var d = new Dictionary<string, List<MethodSymbol>>(StringComparer.Ordinal);
            foreach (var t in n.GetTypeMembers())
            {
                if (!t.IsStatic || t.IsGenericType) continue;
                foreach (var m in t.GetMembers().OfType<MethodSymbol>())
                {
                    if (!m.IsExtensionMethod || m.DeclaredAccessibility != Accessibility.Public && !m.IsFromSource) continue;
                    if (!d.TryGetValue(m.Name, out var l)) d[m.Name] = l = new List<MethodSymbol>();
                    l.Add(m);
                }
            }
            return d;
        });
    }

    /// <summary>Finds extension methods named <paramref name="name"/> visible from this file.</summary>
    internal List<MethodSymbol> LookupExtensionMethods(string name)
    {
        var result = new List<MethodSymbol>();
        void AddAll(IEnumerable<MethodSymbol> ms)
        {
            foreach (var m in ms)
                if (!result.Contains(m) && IsAccessible(m)) result.Add(m);
        }

        // this file's own module functions
        if (_file.Scope.Declared.TryGetValue(name, out var own))
            AddAll(own.OfType<MethodSymbol>().Where(m => m.IsExtensionMethod));
        // named imports
        foreach (var s in _file.Scope.NamedImportSymbols)
            if (s is MethodSymbol m && m.Name == name && m.IsExtensionMethod) AddAll([m]);
        // wildcard imports
        foreach (var w in _file.Scope.WildcardImports)
        {
            switch (w)
            {
                case NamespaceSymbol ns:
                    if (GetNamespaceExtensions(ns).TryGetValue(name, out var l)) AddAll(l);
                    // a source namespace contains the module classes of source files
                    break;
                case NamedTypeSymbol nt:
                    AddAll(nt.GetMembers(name).OfType<MethodSymbol>().Where(x => x.IsExtensionMethod));
                    break;
                case FileModuleSymbol fm:
                    AddAll(fm.File.Scope.GetExports(name).OfType<MethodSymbol>().Where(x => x.IsExtensionMethod));
                    break;
            }
        }
        return result;
    }
}
