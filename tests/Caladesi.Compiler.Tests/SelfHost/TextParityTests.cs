extern alias SelfHost;

using System.Reflection;
using Caladesi.Compiler.Tests.Emit;
using Caladesi.Compiler.Text;
using SelfText = SelfHost::Caladesi.Compiler.Text.SourceText;
using SelfSpan = SelfHost::Caladesi.Compiler.Text.TextSpan;
using SelfSpans = SelfHost::Caladesi.Compiler.Text.TextSpans;
using SelfPosition = SelfHost::Caladesi.Compiler.Text.LinePosition;
using SelfPositions = SelfHost::Caladesi.Compiler.Text.LinePositions;
using SelfPositionSpan = SelfHost::Caladesi.Compiler.Text.LinePositionSpan;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class TextParityTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("abc")]
    [DataRow("\r")]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("a\r\nb\nc\rd")]
    [DataRow("\r\r\n\n\r")]
    [DataRow("last line\n")]
    [DataRow("a\u0085b\u2028c\u2029d")]
    [DataRow("a\0b\tc")]
    [DataRow("a😀\r\n𐐀b")]
    public void SourceText_LinesAndUtf16CharactersMatchBootstrap(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        AssertSourceParity(text);
    }

    [TestMethod]
    public void SourceText_AllShortLineBreakCombinationsMatchBootstrap()
    {
        // Include non-LSP Unicode separators: they must remain ordinary characters.
        const string alphabet = "x\r\n\u2028";
        for (int length = 0, count = 1; length <= 5; length++, count *= alphabet.Length)
        {
            for (int value = 0; value < count; value++)
            {
                char[] characters = new char[length];
                int remaining = value;
                for (int i = 0; i < length; i++, remaining /= alphabet.Length)
                {
                    characters[i] = alphabet[remaining % alphabet.Length];
                }

                AssertSourceParity(new string(characters));
            }
        }
    }

    [TestMethod]
    public void SourceText_ExtremePositionsAndInvalidLinesMatchBootstrap()
    {
        const string text = "a\r\nbb\nccc\r";
        SourceText original = SourceText.From(text);
        SelfText migrated = SelfText.From(text);
        int[] positions = [int.MinValue, -100, -1, 0, 1, text.Length, text.Length + 1, int.MaxValue];
        foreach (int position in positions)
        {
            Assert.AreEqual(original.GetLineIndex(position), migrated.GetLineIndex(position));
            AssertPosition(original.GetLinePosition(position), migrated.GetLinePosition(position));
            AssertEquivalent(() => original[position], () => migrated.GetCharacter(position));
        }

        int[] lines = [int.MinValue, -2, -1, 0, 1, original.LineCount - 1, original.LineCount, int.MaxValue];
        foreach (int line in lines)
        {
            AssertEquivalent(() => original.GetLineStart(line), () => migrated.GetLineStart(line));
            AssertEquivalent(() => original.GetLineEnd(line), () => migrated.GetLineEnd(line));
            foreach (int character in positions)
            {
                Assert.AreEqual(original.GetPosition(new(line, character)), migrated.GetPosition(new(line, character)));
            }
        }
    }

    [TestMethod]
    public void SourceText_LargeLineMapMatchesBootstrap()
    {
        string text = string.Concat(Enumerable.Repeat("a\r\nbb\nccc\rd😀\u2028", 1_000));
        SourceText original = SourceText.From(text);
        SelfText migrated = SelfText.From(text);
        Assert.AreEqual(original.LineCount, migrated.LineCount);
        for (int position = 0; position <= text.Length; position++)
        {
            Assert.AreEqual(original.GetLineIndex(position), migrated.GetLineIndex(position));
            AssertPosition(original.GetLinePosition(position), migrated.GetLinePosition(position));
        }

        for (int line = 0; line < original.LineCount; line++)
        {
            Assert.AreEqual(original.GetLineStart(line), migrated.GetLineStart(line));
            Assert.AreEqual(original.GetLineEnd(line), migrated.GetLineEnd(line));
        }
    }

    [TestMethod]
    public void SourceText_SlicesChangesAndPositionSpansMatchBootstrap()
    {
        const string text = "a\r\nb\n😀";
        SourceText original = SourceText.From(text);
        SelfText migrated = SelfText.From(text);
        string?[] replacements = ["", "x", "\r\n", "a\rb\nc", "😀", null];
        for (int start = -2; start <= text.Length + 2; start++)
        {
            for (int length = -2; length <= text.Length + 2; length++)
            {
                TextSpan span = new(start, length);
                SelfSpan selfSpan = new(start, length);
                AssertEquivalent(() => original.ToString(span), () => migrated.ToString(selfSpan));
                LinePositionSpan expected = original.GetLinePositionSpan(span);
                SelfPositionSpan actual = migrated.GetLinePositionSpan(selfSpan);
                AssertPosition(expected.Start, actual.Start);
                AssertPosition(expected.End, actual.End);
                foreach (string? replacement in replacements)
                {
                    AssertEquivalent(
                        () => original.WithChanges(span, replacement!).Text,
                        () => migrated.WithChanges(selfSpan, replacement!).Text);
                    if (start >= 0 && start <= text.Length && span.End >= 0 && span.End <= text.Length)
                    {
                        AssertSourceParity(migrated.WithChanges(selfSpan, replacement!).Text);
                    }
                }
            }
        }

        Assert.AreEqual(text, original.Text);
        Assert.AreEqual(text, migrated.Text);
    }

    [TestMethod]
    public void SourceText_OverflowingRangesMatchBootstrap()
    {
        SourceText original = SourceText.From("a\r\nb");
        SelfText migrated = SelfText.From("a\r\nb");
        int[] bounds = [int.MinValue, -1, 0, 1, int.MaxValue];
        foreach (int start in bounds)
        {
            foreach (int length in bounds)
            {
                TextSpan span = new(start, length);
                SelfSpan selfSpan = new(start, length);
                AssertEquivalent(() => original.ToString(span), () => migrated.ToString(selfSpan));
                AssertEquivalent(() => original.WithChanges(span, "x").Text, () => migrated.WithChanges(selfSpan, "x").Text);
                LinePositionSpan expected = original.GetLinePositionSpan(span);
                SelfPositionSpan actual = migrated.GetLinePositionSpan(selfSpan);
                AssertPosition(expected.Start, actual.Start);
                AssertPosition(expected.End, actual.End);
            }
        }
    }

    [TestMethod]
    public void SourceText_NullInputMatchesBootstrap()
    {
        AssertEquivalent(() => SourceText.From(null!).Length, () => SelfText.From(null!).Length);
    }

    [TestMethod]
    public void TextSpan_OperationsIncludingEmptyNegativeAndOverflowingRangesMatchBootstrap()
    {
        int[] values = [int.MinValue, -10, -1, 0, 1, 10, int.MaxValue];
        foreach (int start in values)
        {
            foreach (int length in values)
            {
                TextSpan original = new(start, length);
                SelfSpan migrated = new(start, length);
                Assert.AreEqual(original.Start, migrated.Start);
                Assert.AreEqual(original.Length, migrated.Length);
                Assert.AreEqual(original.End, SelfSpans.End(migrated));
                Assert.AreEqual(original.IsEmpty, SelfSpans.IsEmpty(migrated));
                Assert.AreEqual(original.ToString(), SelfSpans.Format(migrated));
                TextSpan bounds = TextSpan.FromBounds(start, length);
                SelfSpan selfBounds = SelfSpans.FromBounds(start, length);
                Assert.AreEqual(bounds.Start, selfBounds.Start);
                Assert.AreEqual(bounds.Length, selfBounds.Length);
                foreach (int position in values)
                {
                    Assert.AreEqual(original.Contains(position), SelfSpans.Contains(migrated, position));
                    Assert.AreEqual(original.ContainsInclusive(position), SelfSpans.ContainsInclusive(migrated, position));
                    foreach (int otherLength in values)
                    {
                        Assert.AreEqual(
                            original.OverlapsWith(new(position, otherLength)),
                            SelfSpans.OverlapsWith(migrated, new(position, otherLength)));
                    }
                }
            }
        }
    }

    [TestMethod]
    public void LinePosition_FormattingMatchesBootstrap()
    {
        int[] values = [int.MinValue, -1, 0, 1, int.MaxValue];
        foreach (int line in values)
        {
            foreach (int character in values)
            {
                Assert.AreEqual(new LinePosition(line, character).ToString(), SelfPositions.Format(new(line, character)));
            }
        }
    }

    [TestMethod]
    public void TextPrimitives_AreImmutableValuesWithStructuralEquality()
    {
        Assert.AreEqual(new SelfSpan(2, 3), new SelfSpan(2, 3));
        Assert.AreNotEqual(new SelfSpan(2, 3), new SelfSpan(2, 4));
        Assert.AreEqual(new SelfSpan(2, 3).GetHashCode(), new SelfSpan(2, 3).GetHashCode());
        Assert.AreEqual(new SelfPosition(2, 3), new SelfPosition(2, 3));
        Assert.AreNotEqual(new SelfPosition(2, 3), new SelfPosition(3, 2));
        Assert.AreEqual(new SelfPositionSpan(new(1, 2), new(3, 4)), new SelfPositionSpan(new(1, 2), new(3, 4)));
        Assert.AreEqual(0, default(SelfSpan).Start);
        Assert.AreEqual(0, default(SelfPosition).Character);
        foreach (Type type in new[] { typeof(SelfSpan), typeof(SelfPosition), typeof(SelfPositionSpan) })
        {
            Assert.IsTrue(type.IsValueType);
            foreach (PropertyInfo property in type.GetProperties())
            {
                Assert.IsTrue(property.SetMethod is null ||
                    property.SetMethod.ReturnParameter.GetRequiredCustomModifiers()
                        .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)));
            }
        }
    }

    [TestMethod]
    public void SelfHostLibrary_DoesNotDelegateToBootstrapCompiler()
    {
        Assembly library = typeof(SelfText).Assembly;
        Assert.AreEqual("Caladesi.Compiler.SelfHost", library.GetName().Name);
        Assert.AreEqual("Caladesi.Compiler.Text.SourceText", typeof(SelfText).FullName);
        Assert.IsFalse(library.GetReferencedAssemblies().Any(a => a.Name == "Caladesi.Compiler" || a.Name == "calac"));
        Assert.IsTrue(typeof(SelfText).IsSealed);
        Assert.IsEmpty(typeof(SelfText).GetConstructors());
    }

    [TestMethod]
    public void SelfHostLibrary_EmittedIlVerifiesAndCaladesiCanConsumeIt()
    {
        Assembly library = typeof(SelfText).Assembly;
        List<string> errors = ILVerifier.Verify(library.GetName().Name!, File.ReadAllBytes(library.Location));
        Assert.IsEmpty(errors, string.Join(Environment.NewLine, errors));

        (string output, int exitCode) = EmitHarness.RunWithReferences("""
            import { SourceText, TextSpans, LinePosition, LinePositions } from "Caladesi.Compiler.Text";
            const text = SourceText.From("a\r\nb");
            const span = TextSpans.FromBounds(0, 1);
            Console.WriteLine(text.LineCount);
            Console.WriteLine(text.ToString(span));
            Console.WriteLine(TextSpans.Format(span));
            Console.WriteLine(LinePositions.Format(text.GetLinePosition(3)));
            Console.WriteLine(text.GetPosition(new LinePosition(0, 100)));
            Console.WriteLine(text.WithChanges(span, "x").GetCharacter(0));
            """, library.Location);
        Assert.AreEqual("2\na\n[0..1)\n2:1\n1\nx\n", output);
        Assert.AreEqual(0, exitCode);
    }

    private static void AssertSourceParity(string text)
    {
        SourceText original = SourceText.From(text);
        SelfText migrated = SelfText.From(text);
        Assert.AreEqual(original.Text, migrated.Text);
        Assert.AreEqual(original.ToString(), migrated.ToString());
        Assert.AreEqual(original.Length, migrated.Length);
        Assert.AreEqual(original.LineCount, migrated.LineCount);
        for (int position = -1; position <= text.Length + 1; position++)
        {
            Assert.AreEqual(original.GetLineIndex(position), migrated.GetLineIndex(position));
            AssertPosition(original.GetLinePosition(position), migrated.GetLinePosition(position));
            if (position >= 0 && position < text.Length)
            {
                Assert.AreEqual(original[position], migrated.GetCharacter(position));
            }
        }

        for (int line = 0; line < original.LineCount; line++)
        {
            Assert.AreEqual(original.GetLineStart(line), migrated.GetLineStart(line));
            Assert.AreEqual(original.GetLineEnd(line), migrated.GetLineEnd(line));
            for (int character = -1; character <= text.Length + 1; character++)
            {
                Assert.AreEqual(original.GetPosition(new(line, character)), migrated.GetPosition(new(line, character)));
            }
        }
    }

    private static void AssertPosition(LinePosition expected, SelfPosition actual)
    {
        Assert.AreEqual(expected.Line, actual.Line);
        Assert.AreEqual(expected.Character, actual.Character);
    }

    private static void AssertEquivalent<T>(Func<T> expected, Func<T> actual)
    {
        (T? Value, Type? Error, string? Parameter) first = Capture(expected);
        (T? Value, Type? Error, string? Parameter) second = Capture(actual);
        Assert.AreEqual(first.Error, second.Error);
        Assert.AreEqual(first.Parameter, second.Parameter);
        Assert.AreEqual(first.Value, second.Value);
    }

    private static (T? Value, Type? Error, string? Parameter) Capture<T>(Func<T> operation)
    {
        try
        {
            return (operation(), null, null);
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or NullReferenceException)
        {
            return (default, error.GetType(), (error as ArgumentException)?.ParamName);
        }
    }
}
