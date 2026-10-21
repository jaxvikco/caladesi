using System.Collections.Immutable;

namespace Caladesi.Compiler.Text;

/// <summary>Immutable source text with line mapping.</summary>
public sealed class SourceText
{
    private readonly ImmutableArray<int> _lineStarts;

    private SourceText(string text)
    {
        Text = text;
        _lineStarts = ComputeLineStarts(text);
    }

    public string Text { get; }
    public int Length => Text.Length;
    public int LineCount => _lineStarts.Length;
    public char this[int index] => Text[index];

    public static SourceText From(string text) => new(text);

    public override string ToString() => Text;
    public string ToString(TextSpan span) => Text.Substring(span.Start, span.Length);

    public SourceText WithChanges(TextSpan span, string newText) =>
        new(string.Concat(Text.AsSpan(0, span.Start), newText, Text.AsSpan(span.End)));

    private static ImmutableArray<int> ComputeLineStarts(string text)
    {
        var builder = ImmutableArray.CreateBuilder<int>();
        builder.Add(0);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                builder.Add(i + 1);
            }
            else if (c == '\n' || c == '\u2028' || c == '\u2029')
            {
                builder.Add(i + 1);
            }
        }
        return builder.ToImmutable();
    }

    public int GetLineIndex(int position)
    {
        if (position < 0) position = 0;
        if (position > Text.Length) position = Text.Length;
        var index = _lineStarts.BinarySearch(position);
        return index >= 0 ? index : ~index - 1;
    }

    public LinePosition GetLinePosition(int position)
    {
        var line = GetLineIndex(position);
        return new LinePosition(line, Math.Min(position, Text.Length) - _lineStarts[line]);
    }

    public LinePositionSpan GetLinePositionSpan(TextSpan span) =>
        new(GetLinePosition(span.Start), GetLinePosition(span.End));

    public int GetLineStart(int line) => _lineStarts[line];

    public int GetLineEnd(int line) => line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Text.Length;

    public int GetPosition(LinePosition position)
    {
        if (position.Line < 0) return 0;
        if (position.Line >= _lineStarts.Length) return Text.Length;
        var start = _lineStarts[position.Line];
        var end = GetLineEnd(position.Line);
        return Math.Min(start + position.Character, end);
    }
}
