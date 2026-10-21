using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

/// <summary>Forces completion of every declaration and checks cross-member rules (overrides, abstract and interface implementation).</summary>
internal sealed class DeclarationChecker(Compilation compilation)
{
    private readonly DiagnosticBag _bag = new();
    public IReadOnlyList<Diagnostic> Diagnostics => _bag.Items;

    public void CheckAll()
    {
        foreach (var type in compilation.AllSourceTypes.ToList())
        {
            // force base types / interfaces / member signatures
            _ = type.BaseType;
            _ = type.Interfaces;
            foreach (var tp in type.TypeParameters) _ = tp.ConstraintTypes;
            if (type is SourceUnionSymbol su) _ = su.UnionCases;
            foreach (var m in type.GetMembers().ToList())
            {
                switch (m)
                {
                    case SourceMethodSymbol ms:
                        _ = ms.ReturnType;
                        _ = ms.Parameters;
                        foreach (var tp in ms.TypeParameters) _ = tp.ConstraintTypes;
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
        foreach (var file in compilation.SourceFiles)
            foreach (var alias in file.Aliases) { _ = alias.TypeParameters; _ = alias.Target; }
        // decorators
        foreach (var type in compilation.AllSourceTypes.ToList())
        {
            foreach (var d in type.DecoratorSyntaxes) compilation.GetBoundAttribute(type.File, d, type);
            foreach (var m in type.GetMembers())
                foreach (var d in DecoratorSyntaxHelper.Of(m is SourceMethodSymbol sm ? sm.Declaration : m is SourcePropertySymbol sp ? sp.Declaration : m is SourceFieldSymbol sf ? sf.Declaration : null))
                    compilation.GetBoundAttribute(type.File, d, type);
        }
        // bind bodies (may create anonymous records, closures, ...)
        foreach (var type in compilation.AllSourceTypes.ToList())
            foreach (var m in type.GetMembers().OfType<SourceMethodSymbol>().ToList())
                if (m.HasBody) _ = m.GetBoundBody();
        foreach (var type in compilation.AllSourceTypes.ToList())
        {
            CheckMembers(type);
            if (type is SourceClassSymbol or SourceRecordSymbol) CheckImplementations(type);
        }
    }

    private void Report(DiagnosticDescriptor d, SourceNamedType type, Text.TextSpan span, params object?[] args) =>
        _bag.Add(Diagnostic.Create(d, type.File.FilePath, span, args));

    private void CheckMembers(SourceNamedType type)
    {
        var members = type.GetMembers();
        // duplicates
        var seen = new List<MethodSymbol>();
        foreach (var m in members.OfType<SourceMethodSymbol>())
        {
            if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet) continue;
            if (m.SynthesizedKind != SynthesizedBodyKind.None && m.Declaration == null) continue;
            foreach (var o in seen)
                if (o.Name == m.Name && o.MethodKind == m.MethodKind && o.Arity == m.Arity && o.Parameters.Length == m.Parameters.Length
                    && o.Parameters.Zip(m.Parameters).All(p => p.First.Type.Equals(p.Second.Type)))
                {
                    var span = m.Locations.FirstOrDefault().Span;
                    Report(Descriptors.DuplicateMember, type, span, m.Name, type.Name);
                }
            seen.Add(m);
        }
        var names = new HashSet<string>();
        foreach (var m in members)
        {
            if (m is SourcePropertySymbol or SourceFieldSymbol { IsBackingField: false } or NamedTypeSymbol)
                if (!names.Add(m.Name) && m is not SourceFieldSymbol { IsModuleVariable: true })
                    Report(Descriptors.DuplicateMember, type, m.Locations.FirstOrDefault().Span, m.Name, type.Name);
        }

        if (type is not SourceClassSymbol cls) return;
        foreach (var m in members.OfType<SourceMethodSymbol>())
        {
            if (m.MethodKind != MethodKind.Ordinary && m.MethodKind != MethodKind.PropertyGet && m.MethodKind != MethodKind.PropertySet) continue;
            var span = m.Locations.FirstOrDefault().Span;
            if (m.IsAbstract && !cls.IsAbstract) Report(Descriptors.AbstractInNonAbstractClass, type, span, m.Name);
            if (m.IsOverrideFlag)
            {
                var o = m.OverriddenMethod;
                if (o == null) Report(Descriptors.OverrideNothing, type, span, m.Name);
                else
                {
                    m.SetEmitName(o.Name);
                    if (!(o.IsVirtual || o.IsAbstract || o.IsOverride)) Report(Descriptors.CannotOverrideSealed, type, span, o.Name);
                }
            }
            else if (!m.IsStatic && m.MethodKind == MethodKind.Ordinary)
            {
                // hiding an inherited virtual member without `override`
                for (var b = cls.BaseType; b != null; b = b.BaseType)
                {
                    var hit = b.GetMembers(m.Name).OfType<MethodSymbol>().FirstOrDefault(x => !x.IsStatic && x.Arity == m.Arity && x.Name == m.Name &&
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
        foreach (var p in members.OfType<SourcePropertySymbol>())
        {
            if (!p.IsOverrideFlag) continue;
            var span = p.Locations.FirstOrDefault().Span;
            var o = FindOverriddenProperty(cls, p);
            if (o == null) Report(Descriptors.OverrideNothing, type, span, p.Name);
            else if (p.Getter != null) p.Getter.SetEmitName("get_" + o.Name);
            if (o != null && p.Setter != null) p.Setter.SetEmitName("set_" + o.Name);
        }
    }

    internal static PropertySymbol? FindOverriddenProperty(NamedTypeSymbol cls, PropertySymbol p)
    {
        for (var b = cls.BaseType; b != null; b = b.BaseType)
            foreach (var cand in OverrideResolver.LookupNames(b, p.Name))
                if (cand is PropertySymbol bp && !bp.IsStatic && (bp.IsVirtual || bp.IsAbstract || bp.IsOverride)) return bp;
        return null;
    }

    private void CheckImplementations(SourceNamedType type)
    {
        if (type.IsAbstract || type.TypeKind == TypeKind.Interface) { }
        var span = type.Locations.FirstOrDefault().Span;
        // abstract members of base classes
        if (!type.IsAbstract && type is SourceClassSymbol)
        {
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                foreach (var am in b.GetMembers().OfType<MethodSymbol>().Where(x => x.IsAbstract && x.MethodKind == MethodKind.Ordinary))
                {
                    if (FindImplementation(type, am, requireOverride: true) == null)
                        Report(Descriptors.AbstractMemberNotImplemented, type, span, type.Name, b.Name + "." + am.Name);
                }
                foreach (var ap in b.GetMembers().OfType<PropertySymbol>().Where(x => x.IsAbstract))
                {
                    var impl = type.BaseTypesAndSelf().Select(t => t.GetMembers().OfType<PropertySymbol>().FirstOrDefault(p => p.Name == ap.Name && p.IsOverride)).FirstOrDefault(x => x != null);
                    if (impl == null) Report(Descriptors.AbstractMemberNotImplemented, type, span, type.Name, b.Name + "." + ap.Name);
                }
            }
        }
        if (type.TypeKind is TypeKind.Interface) return;
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var im in iface.GetMembers().OfType<MethodSymbol>())
            {
                if (im.MethodKind is not (MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet) || im.IsStatic) continue;
                if (!im.IsAbstract) continue;
                if (FindImplementation(type, im, requireOverride: false) == null && !(type.IsAbstract && false))
                    Report(Descriptors.InterfaceMemberNotImplemented, type, span, type.Name, iface.Name + "." + (im.AssociatedSymbol?.Name ?? im.Name));
            }
        }
    }

    /// <summary>Finds the member of <paramref name="type"/> (or its bases) implementing <paramref name="target"/>.</summary>
    public static MethodSymbol? FindImplementation(NamedTypeSymbol type, MethodSymbol target, bool requireOverride)
    {
        for (var t = (NamedTypeSymbol?)type; t != null; t = t.BaseType)
        {
            if (target.AssociatedSymbol is PropertySymbol tp)
            {
                foreach (var cand in OverrideResolver.LookupNames(t, tp.Name))
                {
                    if (cand is not PropertySymbol cp || cp.IsStatic || cp.IsAbstract && t.Equals(type) && false) continue;
                    var acc = target.MethodKind == MethodKind.PropertyGet ? cp.GetMethod : cp.SetMethod;
                    if (acc != null && (!requireOverride || acc.IsOverride)) return acc;
                }
                continue;
            }
            foreach (var cand in OverrideResolver.LookupNames(t, target.Name))
            {
                if (cand is MethodSymbol cm && cm.MethodKind == target.MethodKind && !cm.IsStatic && cm.Arity == target.Arity
                    && !cm.IsAbstract | !ReferenceEquals(t, type) | true && SignatureMatchesForImplementation(cm, target)
                    && (!requireOverride || cm.IsOverride) && cm.DeclaredAccessibility == Accessibility.Public | requireOverride)
                    return cm;
            }
        }
        return null;
    }

    private static bool SignatureMatchesForImplementation(MethodSymbol impl, MethodSymbol target)
    {
        if (impl.Parameters.Length != target.Parameters.Length) return false;
        for (var i = 0; i < impl.Parameters.Length; i++)
        {
            var a = impl.Parameters[i].Type;
            var b = target.Parameters[i].Type;
            if (a is TypeParameterSymbol { IsMethodTypeParameter: true } pa && b is TypeParameterSymbol { IsMethodTypeParameter: true } pb) { if (pa.Ordinal != pb.Ordinal) return false; continue; }
            if (!a.Equals(b)) return false;
        }
        return true;
    }
}

internal static class TypeExtensions
{
    public static IEnumerable<NamedTypeSymbol> BaseTypesAndSelf(this NamedTypeSymbol t)
    {
        for (NamedTypeSymbol? x = t; x != null; x = x.BaseType) yield return x;
    }
}

/// <summary>Determines the program entry point and validates entry-point related rules.</summary>
internal sealed class EntryPointChecker(Compilation compilation)
{
    public IEnumerable<Diagnostic> Check()
    {
        var diags = new List<Diagnostic>();
        var candidates = new List<(SourceMethodSymbol Method, SourceFile File)>();
        foreach (var file in compilation.SourceFiles)
        {
            var module = file.ModuleClass;
            if (module == null) continue;
            if (module.TopLevelEntry is { } entry) candidates.Add((entry, file));
            if (module.UserMain is { } main) candidates.Add((main, file));
        }
        if (compilation.Options.OutputKind == OutputKind.DynamicallyLinkedLibrary)
        {
            foreach (var (m, f) in candidates.Where(c => c.Method.Name == "<Main>$"))
            {
                var first = f.Tree.Root.Statements.FirstOrDefault(s => s is not (FunctionDeclarationSyntax or ClassDeclarationSyntax or InterfaceDeclarationSyntax
                    or EnumDeclarationSyntax or TypeAliasDeclarationSyntax or ImportDeclarationSyntax or VariableStatementSyntax or EmptyStatementSyntax))
                    ?? f.Tree.Root.Statements.FirstOrDefault();
                diags.Add(Diagnostic.Create(Descriptors.EntryPointInLibrary, f.FilePath, first?.Span ?? default));
            }
            return diags;
        }
        if (candidates.Count > 1)
        {
            foreach (var (m, f) in candidates.Skip(1))
                diags.Add(Diagnostic.Create(m.Name == "<Main>$" ? Descriptors.TopLevelStatementsNotAllowed : Descriptors.MultipleEntryPoints, f.FilePath,
                    m.Name == "<Main>$" ? (f.Tree.Root.Statements.FirstOrDefault(s => s is not (FunctionDeclarationSyntax or ClassDeclarationSyntax or InterfaceDeclarationSyntax
                        or EnumDeclarationSyntax or TypeAliasDeclarationSyntax or ImportDeclarationSyntax))?.Span ?? default) : m.Locations.FirstOrDefault().Span));
        }
        else if (candidates.Count == 0 && !compilation.Options.GenerateTestEntryPoint)
        {
            diags.Add(Diagnostic.Create(Descriptors.NoEntryPoint, null, default));
        }
        if (candidates.Count > 0)
        {
            var main = candidates[0].Method;
            if (main.Name == "main")
            {
                var ok = main.Parameters.Length == 0 || (main.Parameters.Length == 1 && main.Parameters[0].Type is ArrayTypeSymbol { ElementType.SpecialType: SpecialType.String });
                var rt = main.ReturnType;
                var rtOk = rt.IsVoid || rt.SpecialType == SpecialType.Int32 || rt.SpecialType == SpecialType.Task || (rt is NamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.TaskT && n.TypeArguments[0].SpecialType == SpecialType.Int32);
                if (!ok || !rtOk) diags.Add(Diagnostic.Create(Descriptors.InvalidMainSignature, candidates[0].File.FilePath, main.Locations.FirstOrDefault().Span));
            }
        }
        return diags;
    }

    public SourceMethodSymbol? FindEntryPoint()
    {
        foreach (var file in compilation.SourceFiles)
        {
            var module = file.ModuleClass;
            if (module == null) continue;
            if (module.TopLevelEntry is { } e) return e;
            if (module.UserMain is { } m) return m;
        }
        return null;
    }
}

internal static class DecoratorSyntaxHelper
{
    public static IEnumerable<DecoratorSyntax> Of(SyntaxNode? declaration) => declaration switch
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
