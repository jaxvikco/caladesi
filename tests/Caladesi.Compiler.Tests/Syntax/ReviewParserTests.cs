using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.Syntax;

/// <summary>Regression tests for the lexer and parser findings of the 2026-10-22 review.</summary>
[TestClass]
public class ReviewParserTests
{
    private static SyntaxTree Parse(string src)
    {
        SyntaxTree tree = SyntaxTree.ParseText(src, "t.cala");
        Assert.AreEqual(src, tree.Root.ToFullString());
        return tree;
    }

    [TestMethod]
    public void Item30a_BinaryLiteralRejectsOtherDigits()
    {
        SyntaxTree tree = Parse("const x = 0b102;");
        Assert.IsTrue(tree.Diagnostics.Any(d => d.Id == Descriptors.InvalidNumber.Id), TestCompiler.Format(tree.Diagnostics));
        Assert.IsEmpty(Parse("const x = 0b1_01;").Diagnostics);
    }

    [TestMethod]
    public void Item30b_LessThanThenGreaterEqualsIsAComparisonChain()
    {
        SyntaxTree tree = Parse("const r = a < b >= c;");
        Assert.IsEmpty(tree.Diagnostics, TestCompiler.Format(tree.Diagnostics));
        Assert.IsFalse(tree.Root.DescendantNodes().OfType<GenericNameSyntax>().Any());
        BinaryExpressionSyntax top = tree.Root.DescendantNodes().OfType<BinaryExpressionSyntax>().First();
        Assert.AreEqual(">=", top.OperatorToken.Text);
        Assert.IsEmpty(Parse("const r = f<int>(1); const g = a<b>;").Diagnostics);
    }

    [TestMethod]
    public void Item30c_ConditionalWithParenthesizedOperandAndArrowAlternative()
    {
        SyntaxTree tree = Parse("const v = c ? (x) : y => y * 2;");
        Assert.IsEmpty(tree.Diagnostics, TestCompiler.Format(tree.Diagnostics));
        ConditionalExpressionSyntax cond = tree.Root.DescendantNodes().OfType<ConditionalExpressionSyntax>().Single();
        Assert.IsInstanceOfType<ParenthesizedExpressionSyntax>(cond.WhenTrue);
        Assert.IsInstanceOfType<ArrowFunctionSyntax>(cond.WhenFalse);
    }

    [TestMethod]
    public void Item30c_ArrowWithReturnTypeStillParsesInsideConditional()
    {
        SyntaxTree tree = Parse("const v = c ? (a: int): int => a : null;");
        Assert.IsEmpty(tree.Diagnostics, TestCompiler.Format(tree.Diagnostics));
        Assert.IsInstanceOfType<ArrowFunctionSyntax>(tree.Root.DescendantNodes().OfType<ConditionalExpressionSyntax>().Single().WhenTrue);
        Assert.IsEmpty(Parse("const w = c ? f((a: int): int => a) : null;").Diagnostics);
    }

    [TestMethod]
    public void Item30d_TypeOfCanBeFollowedByMemberAccess()
    {
        SyntaxTree tree = Parse("Console.WriteLine(typeof(int).Name);");
        Assert.IsEmpty(tree.Diagnostics, TestCompiler.Format(tree.Diagnostics));
        Assert.IsInstanceOfType<MemberAccessExpressionSyntax>(tree.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().First().Arguments.Arguments[0]);
    }

    [TestMethod]
    public void Item34_UnaryOperandOfExponentiationMustBeParenthesized()
    {
        foreach (string src in new[] { "const x = -2 ** 2;", "const x = +a ** 2;", "const x = !a ** 2;", "async function f() { return await g() ** 2; }" })
        {
            SyntaxTree tree = Parse(src);
            Assert.IsTrue(tree.Diagnostics.Any(d => d.Id == Descriptors.UnaryBeforeExponent.Id), src + "\n" + TestCompiler.Format(tree.Diagnostics));
        }

        foreach (string src in new[] { "const x = (-2) ** 2;", "const x = -(2 ** 2);", "const x = 2 ** -2;", "const x = a * -2 ** b;" })
        {
            SyntaxTree tree = Parse(src);
            Assert.AreEqual(src.Contains("a * -2", StringComparison.Ordinal), tree.Diagnostics.Any(d => d.Id == Descriptors.UnaryBeforeExponent.Id), src);
        }
    }

    [TestMethod]
    public void Item35_OverflowingRealLiteralIsReported()
    {
        foreach (string src in new[] { "const x = 1e400;", "const x = 1e400d;", "const x = 1e50f;" })
        {
            SyntaxTree tree = Parse(src);
            Assert.IsTrue(tree.Diagnostics.Any(d => d.Id == Descriptors.InvalidNumber.Id), src);
        }

        Assert.IsEmpty(Parse("const x = 1e308; const y = 3.4e38f; const z = 1e-400;").Diagnostics);
    }

    [TestMethod]
    public void Item36_StrictEqualityIsOneTargetedError()
    {
        foreach (string src in new[] { "const b = a === 1;", "const c = a !== 2;" })
        {
            SyntaxTree tree = Parse(src);
            Assert.HasCount(1, tree.Diagnostics, src + "\n" + TestCompiler.Format(tree.Diagnostics));
            Assert.AreEqual(Descriptors.NotSupportedUseInstead.Id, tree.Diagnostics[0].Id);
        }
    }

    [TestMethod]
    public void Item37_ExportDefaultRecoveryKeepsExportAndDecoratorsInTheTree()
    {
        foreach (string src in new[] { "export default function f() {}", "@A export default class C {}", "export default 1;", "@A\nexport default function () {}" })
        {
            SyntaxTree tree = SyntaxTree.ParseText(src, "t.cala");
            Assert.AreEqual(src, tree.Root.ToFullString(), src);
            Assert.IsTrue(tree.Diagnostics.Any(d => d.Id == Descriptors.ExportDefaultNotSupported.Id), src);
        }
    }
}
