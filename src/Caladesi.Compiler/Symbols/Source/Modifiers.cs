using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols.Source;

/// <summary>Decoded declaration modifiers.</summary>
#pragma warning disable CA1815 // modifier sets are never compared for equality
public readonly struct ModifierSet
#pragma warning restore CA1815
{
    private readonly HashSet<SyntaxKind>? _kinds;

    public ModifierSet(SyntaxList<SyntaxToken> tokens)
    {
        if (tokens.Count == 0) { _kinds = null; return; }
        _kinds = [];
        foreach (SyntaxToken t in tokens)
        {
            _ = _kinds.Add(t.Kind);
        }
    }

    private bool Has(SyntaxKind k)
    {
        return _kinds != null && _kinds.Contains(k);
    }

    public bool IsExport => Has(SyntaxKind.ExportKeyword);
    public bool IsStatic => Has(SyntaxKind.StaticKeyword);
    public bool IsAbstract => Has(SyntaxKind.AbstractKeyword);
    public bool IsAsync => Has(SyntaxKind.AsyncKeyword);
    public bool IsOverride => Has(SyntaxKind.OverrideKeyword);
    public bool IsPrivate => Has(SyntaxKind.PrivateKeyword);
    public bool IsProtected => Has(SyntaxKind.ProtectedKeyword);
    public bool IsInternal => Has(SyntaxKind.InternalKeyword);
    public bool IsPublic => Has(SyntaxKind.PublicKeyword);
    public bool IsReadonly => Has(SyntaxKind.ReadonlyKeyword);
    public bool IsSealed => Has(SyntaxKind.SealedKeyword);
    public bool IsStruct => Has(SyntaxKind.StructKeyword);

    /// <summary>Accessibility for a type-level declaration (export = public, otherwise internal).</summary>
    public Accessibility TypeAccessibility => IsExport || IsPublic ? Accessibility.Public : Accessibility.Internal;

    /// <summary>Accessibility for a class member (public by default).</summary>
    public Accessibility MemberAccessibility => IsPrivate
        ? Accessibility.Private
        : IsProtected && IsInternal
        ? Accessibility.ProtectedOrInternal
        : IsProtected ? Accessibility.Protected : IsInternal ? Accessibility.Internal : Accessibility.Public;
}
