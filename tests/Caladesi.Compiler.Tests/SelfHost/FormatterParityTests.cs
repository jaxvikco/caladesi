extern alias SelfHost;

using System.Diagnostics.CodeAnalysis;
using Caladesi.Compiler.Formatting;
using Caladesi.Compiler.Syntax;
using SelfFormatter = SelfHost::Caladesi.Compiler.Formatting.Formatter;
using SelfTree = SelfHost::Caladesi.Compiler.Syntax.SyntaxTree;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class FormatterParityTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow(" \r\n \t\n\n")]
    [DataRow("class C {\nlet x = 1;   \n\n\nf() {\nreturn [\n1,\n2\n]; \n}\n}\n")]
    [DataRow("const x = `a \n    b  \r\nc  `;   \n/* a\n  b \n c  */  \n")]
    [DataRow("const x = `a ${ {x: 1} } b\n   ${f()} c`; \n")]
    [DataRow("f(\n// comment\n1);\r\n// end\r\n")]
    [DataRow("} ] ) {\n[\n(\nx\n)\n]\n}\n")]
    [DataRow("   // only comment   \n")]
    public void Formatter_WhitespaceProtectedSpansAndRecoveryMatchBootstrap(string source)
    {
        AssertParity(source);
    }

    [TestMethod]
    public void Formatter_RepositorySourcesMatchBootstrap()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Caladesi.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        foreach (string folder in new[] { "samples", "src", "tests", "playground" })
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root.FullName, folder), "*.cala", SearchOption.AllDirectories))
            {
                if (!path.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin"))
                {
                    AssertParity(File.ReadAllText(path));
                }
            }
        }
    }

    [TestMethod]
    [SuppressMessage("Security", "CA5394", Justification = "Deterministic formatter fuzzing, not security-sensitive randomness.")]
    public void Formatter_DeterministicMalformedInputsMatchBootstrap()
    {
        const string alphabet = "ab09 \t\r\n{}[]()'\"`/$\\*?;:";
        Random random = new(0xF04A7);
        for (int sample = 0; sample < 500; sample++)
        {
            string source = new(Enumerable.Range(0, random.Next(100)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            AssertParity(source);
        }
    }

    [TestMethod]
    public void Formatter_NullTreeMatchesBootstrap()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SelfFormatter.Format(null!));
    }

    private static void AssertParity(string source)
    {
        SyntaxTree expected = SyntaxTree.ParseText(source);
        SelfTree actual = SelfTree.ParseText(source);
        foreach (int indent in new[] { 0, 1, 2, 4, 8 })
        {
            Assert.AreEqual(Formatter.Format(expected, indent), SelfFormatter.Format(actual, indent), source);
        }
    }
}
