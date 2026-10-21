using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols.Source;

/// <summary>Decoded declaration modifiers.</summary>
public readonly struct ModifierSet
{
    private readonly HashSet<SyntaxKind>? _kinds;

    public ModifierSet(SyntaxList<SyntaxToken> tokens)
    {
        if (tokens.Count == 0) { _kinds = null; return; }
        _kinds = new HashSet<SyntaxKind>();
        foreach (var t in tokens) _kinds.Add(t.Kind);
    }

    private bool Has(SyntaxKind k) => _kinds != null && _kinds.Contains(k);
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
    public bool IsDeclare => Has(SyntaxKind.DeclareKeyword);

    /// <summary>Accessibility for a type-level declaration (export = public, otherwise internal).</summary>
    public Accessibility TypeAccessibility => IsExport || IsPublic ? Accessibility.Public : Accessibility.Internal;

    /// <summary>Accessibility for a class member (public by default).</summary>
    public Accessibility MemberAccessibility
    {
        get
        {
            if (IsPrivate) return Accessibility.Private;
            if (IsProtected && IsInternal) return Accessibility.ProtectedOrInternal;
            if (IsProtected) return Accessibility.Protected;
            if (IsInternal) return Accessibility.Internal;
            return Accessibility.Public;
        }
    }
}
