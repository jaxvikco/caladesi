extern alias SelfHost;

using Caladesi.Compiler.Syntax;
using PortFacts = SelfHost::Caladesi.Compiler.Syntax.SyntaxFacts;
using PortKind = SelfHost::Caladesi.Compiler.Syntax.SyntaxKind;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class SyntaxFactsParityTests
{
    [TestMethod]
    public void SyntaxKind_AllNamesAndNumericIdentitiesMatch()
    {
        CollectionAssert.AreEqual(Enum.GetNames<SyntaxKind>(), Enum.GetNames<PortKind>());
        foreach (SyntaxKind kind in Enum.GetValues<SyntaxKind>())
        {
            PortKind port = Enum.Parse<PortKind>(kind.ToString());
            Assert.AreEqual((int)kind, (int)port, kind.ToString());
        }
    }

    [TestMethod]
    public void GetText_AllEnumValuesMatch()
    {
        foreach (SyntaxKind kind in Enum.GetValues<SyntaxKind>())
        {
            Assert.AreEqual(SyntaxFacts.GetText(kind), PortFacts.GetText((PortKind)(int)kind), kind.ToString());
        }
    }

    [TestMethod]
    public void ClassificationsAndPrecedence_AllEnumValuesMatch()
    {
        foreach (SyntaxKind kind in Enum.GetValues<SyntaxKind>())
        {
            AssertFactsMatch(kind);
        }
    }

    [TestMethod]
    public void GetKeywordKind_AllKeywordsAndPunctuationMatch()
    {
        foreach (SyntaxKind kind in Enum.GetValues<SyntaxKind>())
        {
            string text = SyntaxFacts.GetText(kind);
            AssertKeywordMatches(text);
            if (SyntaxFacts.IsKeyword(kind))
            {
                Assert.AreEqual((int)kind, (int)PortFacts.GetKeywordKind(text), text);
                AssertKeywordMatches(text.ToUpperInvariant());
                AssertKeywordMatches(char.ToUpperInvariant(text[0]) + text[1..]);
                AssertKeywordMatches(" " + text);
                AssertKeywordMatches(text + " ");
                AssertKeywordMatches(text + "x");
                AssertKeywordMatches(text + "\0");
            }
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("identifier")]
    [DataRow("delete")]
    [DataRow("var")]
    [DataRow("namespace")]
    [DataRow("===・")]
    [DataRow("===")]
    [DataRow("!==")]
    [DataRow(">>>==")]
    [DataRow("Break")]
    [DataRow("İn")]
    [DataRow("ｆｏｒ")]
    [DataRow("\0")]
    [DataRow("\r\n")]
    public void GetKeywordKind_UnknownTextMatches(string text)
    {
        AssertKeywordMatches(text);
    }

    [TestMethod]
    public void GetKeywordKind_NullThrowsLikeOriginal()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SyntaxFacts.GetKeywordKind(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => PortFacts.GetKeywordKind(null!));
    }

    [TestMethod]
    [DataRow(int.MinValue)]
    [DataRow(-1)]
    [DataRow(1000)]
    [DataRow(int.MaxValue)]
    public void Facts_UndefinedEnumValuesMatch(int value)
    {
        SyntaxKind kind = (SyntaxKind)value;
        Assert.AreEqual(SyntaxFacts.GetText(kind), PortFacts.GetText((PortKind)value));
        AssertFactsMatch(kind);
    }

    private static void AssertKeywordMatches(string text)
    {
        Assert.AreEqual((int)SyntaxFacts.GetKeywordKind(text), (int)PortFacts.GetKeywordKind(text), text);
    }

    private static void AssertFactsMatch(SyntaxKind kind)
    {
        PortKind port = (PortKind)(int)kind;
        string message = kind.ToString();
        Assert.AreEqual(SyntaxFacts.IsKeyword(kind), PortFacts.IsKeyword(port), message);
        Assert.AreEqual(SyntaxFacts.IsReservedKeyword(kind), PortFacts.IsReservedKeyword(port), message);
        Assert.AreEqual(SyntaxFacts.IsContextualKeyword(kind), PortFacts.IsContextualKeyword(port), message);
        Assert.AreEqual(SyntaxFacts.IsIdentifierLike(kind), PortFacts.IsIdentifierLike(port), message);
        Assert.AreEqual(SyntaxFacts.IsAssignmentOperator(kind), PortFacts.IsAssignmentOperator(port), message);
        Assert.AreEqual(SyntaxFacts.IsModifier(kind), PortFacts.IsModifier(port), message);
        Assert.AreEqual(SyntaxFacts.GetBinaryPrecedence(kind), PortFacts.GetBinaryPrecedence(port), message);
    }
}
