extern alias SelfHost;

using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using SelfModifiers = SelfHost::Caladesi.Compiler.Symbols.Source.ModifierSets;
using SelfModifierSet = SelfHost::Caladesi.Compiler.Symbols.Source.ModifierSet;
using SelfKind = SelfHost::Caladesi.Compiler.Syntax.SyntaxKind;
using SelfToken = SelfHost::Caladesi.Compiler.Syntax.SyntaxToken;
using SelfElement = SelfHost::Caladesi.Compiler.Syntax.SyntaxElement;
using SelfList = SelfHost::Caladesi.Compiler.Syntax.SyntaxList<SelfHost::Caladesi.Compiler.Syntax.SyntaxToken>;
using SelfListNode = SelfHost::Caladesi.Compiler.Syntax.ListNode;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class ModifierParityTests
{
    [TestMethod]
    public void Modifiers_DefaultAndEmptyMatchBootstrap()
    {
        AssertModifiers(default, default);
        AssertModifiers(new ModifierSet(default), SelfModifiers.Create(new SelfList(null)));
        AssertKinds([]);
    }

    [TestMethod]
    public void Modifiers_EverySyntaxKindAndDuplicatesMatchBootstrap()
    {
        foreach (var kind in Enum.GetValues<SyntaxKind>())
        {
            AssertKinds([kind]);
            AssertKinds([kind, kind, SyntaxKind.IdentifierToken]);
        }
    }

    [TestMethod]
    public void Modifiers_AllFlagCombinationsAndAccessibilityPrecedenceMatchBootstrap()
    {
        SyntaxKind[] flags =
        [
            SyntaxKind.ExportKeyword, SyntaxKind.StaticKeyword, SyntaxKind.AbstractKeyword, SyntaxKind.AsyncKeyword,
            SyntaxKind.OverrideKeyword, SyntaxKind.PrivateKeyword, SyntaxKind.ProtectedKeyword, SyntaxKind.InternalKeyword,
            SyntaxKind.PublicKeyword, SyntaxKind.ReadonlyKeyword, SyntaxKind.SealedKeyword, SyntaxKind.StructKeyword
        ];
        for (var mask = 0; mask < 1 << flags.Length; mask++)
        {
            AssertKinds([.. flags.Where((_, index) => (mask & (1 << index)) != 0)]);
        }
    }

    private static void AssertKinds(SyntaxKind[] kinds)
    {
        SyntaxElement[] expectedTokens = [.. kinds.Select(kind =>
            (SyntaxElement)new SyntaxToken(kind, "", 0, default, default))];
        SelfElement[] actualTokens = [.. kinds.Select(kind =>
            (SelfElement)new SelfToken((SelfKind)(int)kind, "", 0, default, default, null))];
        AssertModifiers(new ModifierSet(new SyntaxList<SyntaxToken>(new ListNode(SyntaxKind.List, expectedTokens))),
            SelfModifiers.Create(new SelfList(new SelfListNode(SelfKind.List, actualTokens))));
    }

    private static void AssertModifiers(ModifierSet expected, SelfModifierSet actual)
    {
        Assert.AreEqual(expected.IsExport, actual.IsExport);
        Assert.AreEqual(expected.IsStatic, actual.IsStatic);
        Assert.AreEqual(expected.IsAbstract, actual.IsAbstract);
        Assert.AreEqual(expected.IsAsync, actual.IsAsync);
        Assert.AreEqual(expected.IsOverride, actual.IsOverride);
        Assert.AreEqual(expected.IsPrivate, actual.IsPrivate);
        Assert.AreEqual(expected.IsProtected, actual.IsProtected);
        Assert.AreEqual(expected.IsInternal, actual.IsInternal);
        Assert.AreEqual(expected.IsPublic, actual.IsPublic);
        Assert.AreEqual(expected.IsReadonly, actual.IsReadonly);
        Assert.AreEqual(expected.IsSealed, actual.IsSealed);
        Assert.AreEqual(expected.IsStruct, actual.IsStruct);
        Assert.AreEqual((int)expected.TypeAccessibility, (int)SelfModifiers.TypeAccessibility(actual));
        Assert.AreEqual((int)expected.MemberAccessibility, (int)SelfModifiers.MemberAccessibility(actual));
    }
}
