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

            bool inProtected = protectedSpans.Any(p => lineStart > p.Start && lineStart < p.End);
            if (inProtected)
            {
                _ = sb.Append(raw);
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

                    _ = sb.Append(' ', indentDepth * indentSize).Append(trimmed.TrimEnd());
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
