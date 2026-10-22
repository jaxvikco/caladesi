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
        var text = tree.Text.Text;
        var tokens = tree.Root.DescendantTokens().Where(t => !t.IsMissing || t.Kind == SyntaxKind.EndOfFileToken).ToList();

        // spans whose interior lines must not be touched: multi-line tokens and block comments
        var protectedSpans = new List<(int Start, int End)>();
        foreach (var t in tokens)
        {
            if (t.Text.Contains('\n') && t.Kind != SyntaxKind.EndOfFileToken) protectedSpans.Add((t.Start, t.End));
            AddTriviaSpans(t.LeadingTrivia, t.FullStart, protectedSpans);
            AddTriviaSpans(t.TrailingTrivia, t.End, protectedSpans);
        }

        // depth before each token
        var depthBefore = new int[tokens.Count];
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            depthBefore[i] = depth;
            depth += Delta(tokens[i].Kind);
            if (depth < 0) depth = 0;
        }

        var sb = new StringBuilder(text.Length);
        var lineStart = 0;
        var ti = 0;
        var blankRun = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            var hasNewline = lineEnd >= 0;
            if (!hasNewline) lineEnd = text.Length;
            var raw = text.AsSpan(lineStart, lineEnd - lineStart);
            if (raw.Length > 0 && raw[^1] == '\r') raw = raw[..^1];

            var inProtected = protectedSpans.Any(p => lineStart > p.Start && lineStart < p.End);
            if (inProtected)
            {
                sb.Append(raw);
                blankRun = 0;
            }
            else
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0)
                {
                    blankRun++;
                    if (blankRun <= 1) { /* keep a single blank line */ }
                    else { lineStart = hasNewline ? lineEnd + 1 : text.Length + 1; continue; }
                }
                else
                {
                    blankRun = 0;
                    while (ti < tokens.Count && tokens[ti].End <= lineStart + (raw.Length - raw.TrimStart().Length)) ti++;
                    var firstOnLine = ti;
                    var indentDepth = firstOnLine < tokens.Count ? depthBefore[firstOnLine] : depth;
                    if (firstOnLine < tokens.Count && tokens[firstOnLine].Start <= lineEnd && IsCloser(tokens[firstOnLine].Kind) && tokens[firstOnLine].Start >= lineStart)
                        indentDepth = Math.Max(0, indentDepth - 1);
                    sb.Append(' ', indentDepth * indentSize).Append(trimmed.TrimEnd());
                }
            }
            if (hasNewline) sb.Append('\n');
            if (!hasNewline) break;
            lineStart = lineEnd + 1;
        }

        var result = sb.ToString().TrimEnd('\n', ' ', '\t', '\r');
        return result.Length == 0 ? "" : result + "\n";
    }

    private static void AddTriviaSpans(System.Collections.Immutable.ImmutableArray<SyntaxTrivia> trivia, int start, List<(int, int)> spans)
    {
        var pos = start;
        foreach (var tr in trivia)
        {
            if (tr.Kind == SyntaxKind.MultiLineCommentTrivia && tr.Text.Contains('\n')) spans.Add((pos, pos + tr.Text.Length));
            pos += tr.Text.Length;
        }
    }

    private static bool IsCloser(SyntaxKind k) => k is SyntaxKind.CloseBraceToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseParenToken;

    private static int Delta(SyntaxKind k) => k switch
    {
        SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenParenToken => 1,
        SyntaxKind.CloseBraceToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseParenToken => -1,
        _ => 0,
    };
}
