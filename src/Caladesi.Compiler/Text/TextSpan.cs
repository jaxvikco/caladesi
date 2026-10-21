namespace Caladesi.Compiler.Text;

/// <summary>A half-open range of characters in a source text.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
    public bool IsEmpty => Length == 0;

    public static TextSpan FromBounds(int start, int end) => new(start, Math.Max(0, end - start));

    public bool Contains(int position) => position >= Start && position < End;
    public bool ContainsInclusive(int position) => position >= Start && position <= End;
    public bool OverlapsWith(TextSpan other) => Start < other.End && other.Start < End;

    public override string ToString() => $"[{Start}..{End})";
}

public readonly record struct LinePosition(int Line, int Character)
{
    public override string ToString() => $"{Line + 1}:{Character + 1}";
}

public readonly record struct LinePositionSpan(LinePosition Start, LinePosition End);
