extern alias SelfHost;

using System.Text;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;
using SelfBag = SelfHost::Caladesi.Compiler.Diagnostics.DiagnosticBag;
using SelfLexer = SelfHost::Caladesi.Compiler.Syntax.Lexer;
using SelfText = SelfHost::Caladesi.Compiler.Text.SourceText;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class LexerParityTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("#!/usr/bin/env calac\r\nlet x = 1; // comment\r\n")]
    [DataRow("a /* same line */ b /* next\nline */ c // end\n d")]
    [DataRow("a\u2028b\u2029c\r\nd\re\nf\u0085g")]
    [DataRow("/* unterminated")]
    [DataRow("a /* unterminated\n")]
    [DataRow("a $b _c café αβ 𐐀 \0 😀 #")]
    [DataRow(">>>= >>= >>> >> >= > << <<= ?? ??= ?. ?.1 ** **= === !== ... ..")]
    [DataRow("'abc' \"abc\" '\\n\\r\\t\\b\\f\\v\\0\\\\\\'\\\"\\`\\$'")]
    [DataRow("'\\x41\\u0042\\u{1F600}\\u{D800}'")]
    [DataRow("'\\x0g\\uFFFF\\u{FFFFFFFF}\\u{}\\u{110000}\\z'")]
    [DataRow("'\\x1' '\\u123' '\\u{ 41 }' '\\u{FFFFFFFF}'")]
    [DataRow("'a\\\r\nb' 'a\\\nb' 'unterminated\n\"unterminated")]
    [DataRow("`a\r\nb\rc\n${a + { x: 1 }.x}z${`nested${b}`}`")]
    [DataRow("`unterminated${{a: `x${y}`}.a}z")]
    public void Lexer_TriviaOperatorsStringsAndTemplatesMatchBootstrap(string source)
    {
        AssertParity(source);
    }

    [TestMethod]
    public void Lexer_NumericValuesAndRuntimeTypesMatchBootstrap()
    {
        string[] literals =
        [
            "0", "1", "2147483647", "2147483648", "4294967295", "4294967296",
            "9223372036854775807", "9223372036854775808", "18446744073709551615",
            "18446744073709551616", "1l", "1u", "1ul", "1lu", "1L", "1U", "1UL",
            "2147483648u", "4294967296u", "9223372036854775808l", "18446744073709551616ul",
            "0x0", "0x7fffffff", "0x80000000", "0xffffffff", "0x100000000",
            "0xffffffffffffffff", "0xffffffffffffffffl", "0x10000000000000000",
            "0b0", "0b1010", "0b102", "0b10u", "0b10ul", "0b10lu", "0b10n",
            "0x", "0b", "0x_", "0b_", "1_2_3", "0x1_F_F", "0b1_0_1",
            ".5", "1.5", "1e10", "1e+10", "1e-10", "1e", "1e+", "1e1_2",
            "1f", "1d", "1m", "1n", "1.5f", "1.5d", "1.5m", "1.5n", "1.5l",
            "1e9999", "1e9999f", "1e9999d", "1e9999m", "1e9999n",
            "79228162514264337593543950335m", "79228162514264337593543950336m",
            "9999999999999999999999999999999999999999n", "0xffffffffffffffffffffn",
            "1bad", "1ff", "1uu", "1ll", "1abc", "0x123g", "0x123f", "0x123d",
        ];
        foreach (var literal in literals)
        {
            AssertParity(literal);
            AssertParity("a " + literal + "; // value\nb");
        }
    }

    [TestMethod]
    public void Lexer_AllRepositoryCaladesiFilesMatchBootstrap()
    {
        var root = FindRepositoryRoot();
        var count = 0;
        foreach (var folder in new[] { "samples", "src", "tests", "playground" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cala", SearchOption.AllDirectories))
            {
                if (path.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin"))
                {
                    continue;
                }

                AssertParity(File.ReadAllText(path), path);
                count++;
            }
        }

        Assert.IsGreaterThan(30, count);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394", Justification = "Seeded deterministic lexer fuzzing, not security-sensitive randomness.")]
    public void Lexer_DeterministicMalformedInputsMatchBootstrap()
    {
        const string alphabet = "abcXYZ09_ $'\"`{}[]()/\\*+-.?=;:\r\n\t\u2028\u0085α😀";
        Random random = new(0xCA1A);
        for (var sample = 0; sample < 2_000; sample++)
        {
            StringBuilder source = new();
            var length = random.Next(100);
            for (var i = 0; i < length; i++)
            {
                _ = source.Append(alphabet[random.Next(alphabet.Length)]);
            }

            AssertParity(source.ToString());
        }
    }

    internal static void AssertParity(string source, string path = "lexer.cala")
    {
        DiagnosticBag expectedDiagnostics = new();
        SelfBag actualDiagnostics = new();
        var expected = new Lexer(SourceText.From(source), path, expectedDiagnostics).LexAll();
        var actual = new SelfLexer(SelfText.From(source), path, actualDiagnostics).LexAll();
        Assert.HasCount(expected.Count, actual, source);
        StringBuilder reconstructed = new();
        for (var i = 0; i < expected.Count; i++)
        {
            var left = expected[i];
            var right = actual[i];
            var context = $"Token {i}, source: {source}";
            Assert.AreEqual((int)left.Kind, (int)right.Kind, context);
            Assert.AreEqual(left.Text, right.Text, context);
            Assert.AreEqual(left.Value?.GetType(), right.Value?.GetType(), context);
            Assert.AreEqual(left.Value, right.Value, context);
            Assert.AreEqual(left.ValueText, right.ValueText, context);
            Assert.AreEqual(left.Start, right.Start, context);
            Assert.AreEqual(left.End, right.End, context);
            Assert.AreEqual(left.Width, right.Width, context);
            Assert.AreEqual(left.FullStart, right.FullStart, context);
            Assert.AreEqual(left.FullEnd, right.FullEnd, context);
            Assert.AreEqual(left.FullWidth, right.FullWidth, context);
            Assert.AreEqual(left.IsMissing, right.IsMissing, context);
            Assert.AreEqual(left.ToFullString(), right.ToFullString(), context);
            Assert.AreEqual(left.ToString(), right.ToString(), context);
            Assert.HasCount(left.LeadingTrivia.Length, right.LeadingTrivia, context);
            Assert.HasCount(left.TrailingTrivia.Length, right.TrailingTrivia, context);
            for (var j = 0; j < left.LeadingTrivia.Length; j++)
            {
                Assert.AreEqual((int)left.LeadingTrivia[j].Kind, (int)right.LeadingTrivia[j].Kind, context);
                Assert.AreEqual(left.LeadingTrivia[j].Text, right.LeadingTrivia[j].Text, context);
            }

            for (var j = 0; j < left.TrailingTrivia.Length; j++)
            {
                Assert.AreEqual((int)left.TrailingTrivia[j].Kind, (int)right.TrailingTrivia[j].Kind, context);
                Assert.AreEqual(left.TrailingTrivia[j].Text, right.TrailingTrivia[j].Text, context);
            }

            _ = reconstructed.Append(right.ToFullString());
        }

        Assert.AreEqual(source, reconstructed.ToString());
        Assert.AreEqual(expectedDiagnostics.Count, actualDiagnostics.Count, source);
        for (var i = 0; i < expectedDiagnostics.Count; i++)
        {
            var left = expectedDiagnostics.Items[i];
            var right = actualDiagnostics.Items[i];
            Assert.AreEqual(left.Id, right.Id, source);
            Assert.AreEqual(left.Message, right.Message, source);
            Assert.AreEqual(left.FilePath, right.FilePath, source);
            Assert.AreEqual(left.Span.Start, right.Span.Start, source);
            Assert.AreEqual(left.Span.Length, right.Span.Length, source);
            Assert.AreEqual((int)left.EffectiveSeverity, (int)right.EffectiveSeverity, source);
            Assert.AreEqual(left.Format(SourceText.From(source)), right.Format(SelfText.From(source)), source);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Caladesi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
