using System.Text;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Formatting;

/// <summary>
/// A conservative formatter: re-indents lines by bracket nesting (two spaces), removes trailing whitespace, collapses runs of
/// blank lines and ends the file with a newline. Lines that start inside a multi-line token or comment are left alone.
/// </summary>
public static class Formatter
{
    public static string Format(SyntaxTree tree, int indentSize = 2)
    {
        ArgumentNullException.ThrowIfNull(tree);
        string text = tree.Text.Text;
        List<SyntaxToken> tokens = [.. tree.Root.DescendantTokens().Where(t => !t.IsMissing || t.Kind == SyntaxKind.EndOfFileToken)];

        // spans whose interior lines must not be touched: multi-line tokens and block comments
        List<(int Start, int End)> protectedSpans = [];
        foreach (SyntaxToken? t in tokens)
        {
            if (t.Text.Contains('\n') && t.Kind != SyntaxKind.EndOfFileToken)
            {
                protectedSpans.Add((t.Start, t.End));
            }

            AddTriviaSpans(t.LeadingTrivia, t.FullStart, protectedSpans);
            AddTriviaSpans(t.TrailingTrivia, t.End, protectedSpans);
        }

        // depth before each token
        int[] depthBefore = new int[tokens.Count];
        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            depthBefore[i] = depth;
            depth += Delta(tokens[i].Kind);
            if (depth < 0)
            {
                depth = 0;
            }
        }

        StringBuilder sb = new(text.Length);
        int lineStart = 0;
        int ti = 0;
        int blankRun = 0;
        while (lineStart <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            bool hasNewline = lineEnd >= 0;
            if (!hasNewline)
            {
                lineEnd = text.Length;
            }

            ReadOnlySpan<char> raw = text.AsSpan(lineStart, lineEnd - lineStart);
            if (raw.Length > 0 && raw[^1] == '\r')
            {
                raw = raw[..^1];
            }

            (int Start, int End) containing = protectedSpans.FirstOrDefault(p => lineStart > p.Start && lineStart < p.End);
            bool inProtected = containing != default;
            if (inProtected)
            {
                // whitespace inside the token belongs to its value; whitespace after the token ends on this line is not
                int tokenEnd = containing.End - lineStart;
                _ = tokenEnd >= raw.Length ? sb.Append(raw) : sb.Append(raw[..tokenEnd]).Append(raw[tokenEnd..].TrimEnd());
                blankRun = 0;
            }
            else
            {
                ReadOnlySpan<char> trimmed = raw.Trim();
                if (trimmed.Length == 0)
                {
                    blankRun++;
                    if (blankRun <= 1) { /* keep a single blank line */ }
                    else { lineStart = hasNewline ? lineEnd + 1 : text.Length + 1; continue; }
                }
                else
                {
                    blankRun = 0;
                    while (ti < tokens.Count && tokens[ti].End <= lineStart + (raw.Length - raw.TrimStart().Length))
                    {
                        ti++;
                    }

                    int firstOnLine = ti;
                    int indentDepth = firstOnLine < tokens.Count ? depthBefore[firstOnLine] : depth;
                    if (firstOnLine < tokens.Count && tokens[firstOnLine].Start <= lineEnd && IsCloser(tokens[firstOnLine].Kind) && tokens[firstOnLine].Start >= lineStart)
                    {
                        indentDepth = Math.Max(0, indentDepth - 1);
                    }

                    // a multi-line token that starts on this line continues past its end: trailing whitespace is part of its value
                    bool startsMultiLineToken = protectedSpans.Any(p => p.Start >= lineStart && p.Start < lineEnd && p.End > lineEnd);
                    _ = sb.Append(' ', indentDepth * indentSize).Append(startsMultiLineToken ? raw.TrimStart() : trimmed);
                }
            }
            if (hasNewline)
            {
                _ = sb.Append('\n');
            }

            if (!hasNewline)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        string result = sb.ToString().TrimEnd('\n', ' ', '\t', '\r');
        return result.Length == 0 ? "" : result + "\n";
    }

    private static void AddTriviaSpans(System.Collections.Immutable.ImmutableArray<SyntaxTrivia> trivia, int start, List<(int, int)> spans)
    {
        int pos = start;
        foreach (SyntaxTrivia tr in trivia)
        {
            if (tr.Kind == SyntaxKind.MultiLineCommentTrivia && tr.Text.Contains('\n'))
            {
                spans.Add((pos, pos + tr.Text.Length));
            }

            pos += tr.Text.Length;
        }
    }

    private static bool IsCloser(SyntaxKind k)
    {
        return k is SyntaxKind.CloseBraceToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseParenToken;
    }

    private static int Delta(SyntaxKind k)
    {
        return k switch
        {
            SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenParenToken => 1,
            SyntaxKind.CloseBraceToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseParenToken => -1,
            _ => 0,
        };
    }
}
