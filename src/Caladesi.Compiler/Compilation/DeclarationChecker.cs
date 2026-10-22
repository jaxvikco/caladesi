using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler;

/// <summary>Forces completion of every declaration and checks cross-member rules (overrides, abstract and interface implementation).</summary>
internal sealed class DeclarationChecker(Compilation compilation)
{
    private readonly DiagnosticBag _bag = new();
    public IReadOnlyList<Diagnostic> Diagnostics => _bag.Items;

    public void CheckAll()
    {
        foreach (SourceNamedType? type in compilation.AllSourceTypes.ToList())
        {
            // force base types / interfaces / member signatures
            _ = type.BaseType;
            _ = type.Interfaces;
            foreach (TypeParameterSymbol tp in type.TypeParameters)
            {
                _ = tp.ConstraintTypes;
            }

            if (type is SourceUnionSymbol su)
            {
                _ = su.UnionCases;
            }

            foreach (Symbol? m in type.GetMembers().ToList())
            {
                switch (m)
                {
                    case SourceMethodSymbol ms:
                        _ = ms.ReturnType;
                        _ = ms.Parameters;
                        foreach (TypeParameterSymbol tp in ms.TypeParameters)
                        {
                            _ = tp.ConstraintTypes;
                        }

                        break;
                    case SourcePropertySymbol ps:
                        _ = ps.Type;
                        break;
                    case SourceFieldSymbol fs:
                        _ = fs.Type;
                        _ = fs.ConstantValue;
                        break;
                }
            }
        }
        // aliases are resolved lazily; resolve them all so their errors are reported
        foreach (SourceFile file in compilation.SourceFiles)
        {
            foreach (TypeAliasSymbol alias in file.Aliases) { _ = alias.TypeParameters; _ = alias.Target; }
        }

        foreach (SourceNamedType? type in compilation.AllSourceTypes.ToList())
        {
            foreach (SourceFieldSymbol f in type.GetMembers().OfType<SourceFieldSymbol>())
            {
                if (f.IsModuleVariable && f.Type is NamedTypeSymbol { OriginalDefinition: Symbols.Metadata.MetadataNamedType { IsByRefLike: true } } && f.Declarator != null)
                {
                    _bag.Add(Diagnostic.Create(Descriptors.RefStructModuleVariable, type.File.FilePath, f.Declarator.Span, f.Name, f.Type.ToDisplayString()));
                }
            }
        }
        // decorators
        foreach (SourceNamedType? type in compilation.AllSourceTypes.ToList())
        {
            foreach (DecoratorSyntax d in type.DecoratorSyntaxes)
            {
                _ = compilation.GetBoundAttribute(type.File, d, type);
            }

            foreach (Symbol m in type.GetMembers())
            {
                foreach (DecoratorSyntax d in DecoratorSyntaxHelper.Of(m is SourceMethodSymbol sm ? sm.Declaration : m is SourcePropertySymbol sp ? sp.Declaration : m is SourceFieldSymbol sf ? sf.Declaration : null))
                {
                    _ = compilation.GetBoundAttribute(type.File, d, type);
                }
            }
        }
        // bind bodies (may create anonymous records, closures, ...)
        foreach (SourceNamedType? type in compilation.AllSourceTypes.ToList())
        {
            foreach (SourceMethodSymbol? m in type.GetMembers().OfType<SourceMethodSymbol>().ToList())
            {
                if (m.HasBody || m.MethodKind == MethodKind.StaticConstructor)
                {
                    _ = m.GetBoundBody();
                }
            }
        }

        foreach (SourceNamedType? type in compilation.AllSourceTypes.ToList())
        {
            CheckMembers(type);
            if (type is SourceClassSymbol or SourceRecordSymbol)
            {
                CheckImplementations(type);
            }
        }
    }

    private void Report(DiagnosticDescriptor d, SourceNamedType type, TextSpan span, params object?[] args)
    {
        _bag.Add(Diagnostic.Create(d, type.File.FilePath, span, args));
    }

    private void CheckMembers(SourceNamedType type)
    {
        ImmutableArray<Symbol> members = type.GetMembers();
        // duplicates
        List<MethodSymbol> seen = [];
        foreach (SourceMethodSymbol m in members.OfType<SourceMethodSymbol>())
        {
            if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet)
            {
                continue;
            }

            if (m.SynthesizedKind != SynthesizedBodyKind.None && m.Declaration == null)
            {
                continue;
            }

            foreach (MethodSymbol o in seen)
            {
                if (o.Name == m.Name && o.MethodKind == m.MethodKind && o.Arity == m.Arity && o.Parameters.Length == m.Parameters.Length
                    && o.Parameters.Zip(m.Parameters).All(p => p.First.Type.Equals(p.Second.Type)))
                {
                    TextSpan span = m.Locations.FirstOrDefault().Span;
                    Report(Descriptors.DuplicateMember, type, span, m.Name, type.Name);
                }
            }

            seen.Add(m);
        }
        HashSet<string> names = [];
        foreach (Symbol m in members)
        {
            if (m is SourcePropertySymbol or SourceFieldSymbol { IsBackingField: false } or NamedTypeSymbol)
            {
                if (!names.Add(m.Name) && m is not SourceFieldSymbol { IsModuleVariable: true })
                {
                    Report(Descriptors.DuplicateMember, type, m.Locations.FirstOrDefault().Span, m.Name, type.Name);
                }
            }
        }

        if (type is not SourceClassSymbol cls)
        {
            return;
        }

        foreach (SourceMethodSymbol m in members.OfType<SourceMethodSymbol>())
        {
            if (m.MethodKind is not MethodKind.Ordinary and not MethodKind.PropertyGet and not MethodKind.PropertySet)
            {
                continue;
            }

            TextSpan span = m.Locations.FirstOrDefault().Span;
            if (m.IsAbstract && !cls.IsAbstract)
            {
                Report(Descriptors.AbstractInNonAbstractClass, type, span, m.Name);
            }

            if (m.IsOverrideFlag && m.MethodKind == MethodKind.Ordinary)
            {
                MethodSymbol? o = m.OverriddenMethod;
                if (o == null)
                {
                    Report(Descriptors.OverrideNothing, type, span, m.Name);
                }
                else
                {
                    m.SetEmitName(o.Name);
                    if (!(o.IsVirtual || o.IsAbstract || o.IsOverride))
                    {
                        Report(Descriptors.CannotOverrideSealed, type, span, o.Name);
                    }

                    if (!TypesMatchForImplementation(m.ReturnType, o.ReturnType))
                    {
                        Report(Descriptors.OverrideTypeMismatch, type, span, m.Name, m.ReturnType.ToDisplayString(), o.ContainingType?.Name + "." + o.Name, o.ReturnType.ToDisplayString());
                    }
                }
            }
            else if (m.IsOverrideFlag)
            {
                // property accessors are checked with their property below
            }
            else if (!m.IsStatic && m.MethodKind == MethodKind.Ordinary)
            {
                // hiding an inherited virtual member without `override`
                for (NamedTypeSymbol? b = cls.BaseType; b != null; b = b.BaseType)
                {
                    MethodSymbol? hit = b.GetMembers(m.Name).OfType<MethodSymbol>().FirstOrDefault(x => !x.IsStatic && x.Arity == m.Arity && x.Name == m.Name &&
                        x.MethodKind == MethodKind.Ordinary && (x.IsVirtual || x.IsAbstract || x.IsOverride) && OverrideResolver.SignaturesMatch(x, m)
                        && x.DeclaredAccessibility != Accessibility.Private);
                    if (hit != null)
                    {
                        Report(Descriptors.MissingOverride, type, span, m.Name, hit.ContainingType?.Name + "." + hit.Name);
                        break;
                    }
                }
            }
        }
        foreach (SourcePropertySymbol p in members.OfType<SourcePropertySymbol>())
        {
            if (!p.IsOverrideFlag)
            {
                continue;
            }

            TextSpan span = p.Locations.FirstOrDefault().Span;
            PropertySymbol? o = FindOverriddenProperty(cls, p);
            if (o == null)
            {
                Report(Descriptors.OverrideNothing, type, span, p.Name);
            }
            else
            {
                p.Getter?.SetEmitName("get_" + o.Name);
                if (!TypesMatchForImplementation(p.Type, o.Type))
                {
                    Report(Descriptors.OverrideTypeMismatch, type, span, p.Name, p.Type.ToDisplayString(), o.ContainingType?.Name + "." + o.Name, o.Type.ToDisplayString());
                }
            }

            if (o != null && p.Setter != null)
            {
                p.Setter.SetEmitName("set_" + o.Name);
            }
        }
    }

    internal static PropertySymbol? FindOverriddenProperty(NamedTypeSymbol cls, PropertySymbol p)
    {
        for (NamedTypeSymbol? b = cls.BaseType; b != null; b = b.BaseType)
        {
            foreach (Symbol cand in OverrideResolver.LookupNames(b, p.Name))
            {
                if (cand is PropertySymbol bp && !bp.IsStatic && (bp.IsVirtual || bp.IsAbstract || bp.IsOverride))
                {
                    return bp;
                }
            }
        }

        return null;
    }

    private void CheckImplementations(SourceNamedType type)
    {
        if (type.IsAbstract || type.TypeKind == TypeKind.Interface) { }
        TextSpan span = type.Locations.FirstOrDefault().Span;
        // abstract members of base classes
        if (!type.IsAbstract && type is SourceClassSymbol)
        {
            for (NamedTypeSymbol? b = type.BaseType; b != null; b = b.BaseType)
            {
                foreach (MethodSymbol? am in b.GetMembers().OfType<MethodSymbol>().Where(x => x.IsAbstract && x.MethodKind == MethodKind.Ordinary))
                {
                    if (FindImplementation(type, am, requireOverride: true) == null)
                    {
                        Report(Descriptors.AbstractMemberNotImplemented, type, span, type.Name, b.Name + "." + am.Name);
                    }
                }
                foreach (PropertySymbol? ap in b.GetMembers().OfType<PropertySymbol>().Where(x => x.IsAbstract))
                {
                    PropertySymbol? impl = type.BaseTypesAndSelf().Select(t => OverrideResolver.LookupNames(t, ap.Name).OfType<PropertySymbol>().FirstOrDefault(p => p.IsOverride && TypesMatchForImplementation(p.Type, ap.Type))).FirstOrDefault(x => x != null);
                    if (impl == null)
                    {
                        Report(Descriptors.AbstractMemberNotImplemented, type, span, type.Name, b.Name + "." + ap.Name);
                    }
                }
            }
        }
        if (type.TypeKind is TypeKind.Interface)
        {
            return;
        }

        foreach (NamedTypeSymbol iface in type.AllInterfaces)
        {
            // a base class that already implements the interface (including explicitly, which metadata types do not expose) supplies the members
            if (type.BaseType is { } baseType && baseType.AllInterfaces.Any(i => i.Equals(iface)))
            {
                continue;
            }

            foreach (MethodSymbol im in iface.GetMembers().OfType<MethodSymbol>())
            {
                if (im.MethodKind is not (MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet) || im.IsStatic)
                {
                    continue;
                }

                if (!im.IsAbstract)
                {
                    continue;
                }

                if (FindImplementation(type, im, requireOverride: false) == null && FindBridgeTarget(type, im) == null)
                {
                    Report(Descriptors.InterfaceMemberNotImplemented, type, span, type.Name, iface.Name + "." + (im.AssociatedSymbol?.Name ?? im.Name));
                }
            }
        }
    }

    /// <summary>Finds the member of <paramref name="type"/> (or its bases) implementing <paramref name="target"/>.</summary>
    public static MethodSymbol? FindImplementation(NamedTypeSymbol type, MethodSymbol target, bool requireOverride)
    {
        for (NamedTypeSymbol? t = (NamedTypeSymbol?)type; t != null; t = t.BaseType)
        {
            if (target.AssociatedSymbol is PropertySymbol tp)
            {
                foreach (Symbol cand in OverrideResolver.LookupNames(t, tp.Name))
                {
                    if (cand is not PropertySymbol cp || cp.IsStatic || (cp.IsAbstract && t.Equals(type) && false) || !TypesMatchForImplementation(cp.Type, tp.Type))
                    {
                        continue;
                    }

                    MethodSymbol? acc = target.MethodKind == MethodKind.PropertyGet ? cp.GetMethod : cp.SetMethod;
                    if (acc != null && (!requireOverride || acc.IsOverride))
                    {
                        return acc;
                    }
                }
                continue;
            }
            foreach (Symbol cand in OverrideResolver.LookupNames(t, target.Name))
            {
                if (cand is MethodSymbol cm && cm.MethodKind == target.MethodKind && !cm.IsStatic && cm.Arity == target.Arity
                    && !cm.IsAbstract | !ReferenceEquals(t, type) | true && SignatureMatchesForImplementation(cm, target)
                    && (!requireOverride || cm.IsOverride) && cm.DeclaredAccessibility == Accessibility.Public | requireOverride)
                {
                    return cm;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// For an interface member the class does not implement directly: a public member with the same name and parameters whose return
    /// type converts to the interface's by a reference conversion (e.g. <c>IEnumerator&lt;T&gt; GetEnumerator()</c> for
    /// <c>IEnumerable.GetEnumerator()</c>). The compiler then emits an explicit bridge, as C# programmers write by hand.
    /// </summary>
    public static MethodSymbol? FindBridgeTarget(NamedTypeSymbol type, MethodSymbol target)
    {
        for (NamedTypeSymbol? t = (NamedTypeSymbol?)type; t != null; t = t.BaseType)
        {
            if (target.AssociatedSymbol is PropertySymbol tp)
            {
                if (target.MethodKind != MethodKind.PropertyGet)
                {
                    continue;
                }

                foreach (Symbol cand in OverrideResolver.LookupNames(t, tp.Name))
                {
                    if (cand is PropertySymbol { IsStatic: false, GetMethod: { } g } cp && cp.DeclaredAccessibility == Accessibility.Public && ReferenceConvertible(cp.Type, tp.Type))
                    {
                        return g;
                    }
                }

                continue;
            }
            foreach (Symbol cand in OverrideResolver.LookupNames(t, target.Name))
            {
                if (cand is not MethodSymbol cm || cm.MethodKind != target.MethodKind || cm.IsStatic || cm.Arity != target.Arity || cm.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                if (cm.Parameters.Length != target.Parameters.Length)
                {
                    continue;
                }

                bool paramsMatch = true;
                for (int i = 0; i < cm.Parameters.Length && paramsMatch; i++)
                {
                    paramsMatch = TypesMatchForImplementation(cm.Parameters[i].Type, target.Parameters[i].Type);
                }

                if (paramsMatch && ReferenceConvertible(cm.ReturnType, target.ReturnType))
                {
                    return cm;
                }
            }
        }
        return null;
    }

    private static bool ReferenceConvertible(TypeSymbol from, TypeSymbol to)
    {
        from = StripReferenceNullability(from);
        to = StripReferenceNullability(to);
        if (from.Equals(to))
        {
            return true;
        }

        if (to.SpecialType == SpecialType.Object && !from.IsValueType)
        {
            return true;
        }

        if (from.IsValueType || to.IsValueType)
        {
            return false;
        }

        for (TypeSymbol? x = from; x != null; x = x.BaseType)
        {
            if (x.Equals(to))
            {
                return true;
            }
        }

        foreach (NamedTypeSymbol i in from.AllInterfaces)
        {
            if (i.Equals(to))
            {
                return true;
            }
        }

        return false;
    }

    private static TypeSymbol StripReferenceNullability(TypeSymbol t)
    {
        return t is NullableTypeSymbol { Underlying: { } u } && !u.IsValueType && u is not TypeParameterSymbol ? u : t;
    }

    private static bool TypesMatchForImplementation(TypeSymbol a, TypeSymbol b)
    {
        if (a is TypeParameterSymbol { IsMethodTypeParameter: true } pa && b is TypeParameterSymbol { IsMethodTypeParameter: true } pb)
        {
            return pa.Ordinal == pb.Ordinal;
        }
        // nullability annotations of reference types do not change the CLR signature
        return StripReferenceNullability(a).Equals(StripReferenceNullability(b));
    }

    private static bool SignatureMatchesForImplementation(MethodSymbol impl, MethodSymbol target)
    {
        if (impl.Parameters.Length != target.Parameters.Length)
        {
            return false;
        }

        for (int i = 0; i < impl.Parameters.Length; i++)
        {
            if (!TypesMatchForImplementation(impl.Parameters[i].Type, target.Parameters[i].Type))
            {
                return false;
            }
        }

        return impl.MethodKind is MethodKind.Constructor || TypesMatchForImplementation(impl.ReturnType, target.ReturnType);
    }
}

internal static class TypeExtensions
{
    public static IEnumerable<NamedTypeSymbol> BaseTypesAndSelf(this NamedTypeSymbol t)
    {
        for (NamedTypeSymbol? x = t; x != null; x = x.BaseType)
        {
            yield return x;
        }
    }
}

/// <summary>Determines the program entry point and validates entry-point related rules.</summary>
internal sealed class EntryPointChecker(Compilation compilation)
{
    public IEnumerable<Diagnostic> Check()
    {
        List<Diagnostic> diags = [];
        List<(SourceMethodSymbol Method, SourceFile File)> candidates = [];
        foreach (SourceFile file in compilation.SourceFiles)
        {
            SourceModuleClassSymbol? module = file.ModuleClass;
            if (module == null)
            {
                continue;
            }

            if (module.TopLevelEntry is { } entry)
            {
                candidates.Add((entry, file));
            }

            if (module.UserMain is { } main)
            {
                candidates.Add((main, file));
            }
        }
        if (compilation.Options.OutputKind == OutputKind.DynamicallyLinkedLibrary)
        {
            foreach ((SourceMethodSymbol? m, SourceFile? f) in candidates.Where(c => c.Method.Name == "<Main>$"))
            {
                StatementSyntax? first = f.Tree.Root.Statements.FirstOrDefault(s => s is not (FunctionDeclarationSyntax or ClassDeclarationSyntax or InterfaceDeclarationSyntax
                    or EnumDeclarationSyntax or TypeAliasDeclarationSyntax or ImportDeclarationSyntax or VariableStatementSyntax or EmptyStatementSyntax))
                    ?? f.Tree.Root.Statements.FirstOrDefault();
                diags.Add(Diagnostic.Create(Descriptors.EntryPointInLibrary, f.FilePath, first?.Span ?? default));
            }
            return diags;
        }
        if (candidates.Count > 1)
        {
            foreach ((SourceMethodSymbol? m, SourceFile? f) in candidates.Skip(1))
            {
                diags.Add(Diagnostic.Create(m.Name == "<Main>$" ? Descriptors.TopLevelStatementsNotAllowed : Descriptors.MultipleEntryPoints, f.FilePath,
                    m.Name == "<Main>$" ? (f.Tree.Root.Statements.FirstOrDefault(s => s is not (FunctionDeclarationSyntax or ClassDeclarationSyntax or InterfaceDeclarationSyntax
                        or EnumDeclarationSyntax or TypeAliasDeclarationSyntax or ImportDeclarationSyntax))?.Span ?? default) : m.Locations.FirstOrDefault().Span));
            }
        }
        else if (candidates.Count == 0 && !compilation.Options.GenerateTestEntryPoint)
        {
            diags.Add(Diagnostic.Create(Descriptors.NoEntryPoint, null, default));
        }
        if (candidates.Count > 0)
        {
            SourceMethodSymbol main = candidates[0].Method;
            if (main.Name == "main")
            {
                bool ok = main.Parameters.Length == 0 || (main.Parameters.Length == 1 && main.Parameters[0].Type is ArrayTypeSymbol { ElementType.SpecialType: SpecialType.String });
                TypeSymbol rt = main.ReturnType;
                bool rtOk = rt.IsVoid || rt.SpecialType == SpecialType.Int32 || rt.SpecialType == SpecialType.Task || (rt is NamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.TaskT && n.TypeArguments[0].SpecialType == SpecialType.Int32);
                if (!ok || !rtOk)
                {
                    diags.Add(Diagnostic.Create(Descriptors.InvalidMainSignature, candidates[0].File.FilePath, main.Locations.FirstOrDefault().Span));
                }
            }
        }
        return diags;
    }

    public SourceMethodSymbol? FindEntryPoint()
    {
        foreach (SourceFile file in compilation.SourceFiles)
        {
            SourceModuleClassSymbol? module = file.ModuleClass;
            if (module == null)
            {
                continue;
            }

            if (module.TopLevelEntry is { } e)
            {
                return e;
            }

            if (module.UserMain is { } m)
            {
                return m;
            }
        }
        return null;
    }
}

internal static class DecoratorSyntaxHelper
{
    public static IEnumerable<DecoratorSyntax> Of(SyntaxNode? declaration)
    {
        return declaration switch
        {
            FunctionDeclarationSyntax f => f.Decorators,
            MethodDeclarationSyntax m => m.Decorators,
            PropertyDeclarationSyntax p => p.Decorators,
            ConstructorDeclarationSyntax c => c.Decorators,
            AccessorDeclarationSyntax a => a.Decorators,
            ClassDeclarationSyntax c => c.Decorators,
            ParameterSyntax p => p.Decorators,
            _ => [],
        };
    }
}
