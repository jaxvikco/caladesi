using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Syntax;

/// <summary>
/// Hand-written, error-tolerant recursive-descent parser. Always produces a full-fidelity tree whose
/// text round-trips to the input; unexpected tokens are preserved as skipped-token trivia and missing
/// tokens are synthesized with zero width.
/// </summary>
#pragma warning disable CA1002 // token list is owned and indexed by the parser
public sealed partial class Parser(string path, List<SyntaxToken> tokens, DiagnosticBag diagnostics)
#pragma warning restore CA1002
{
    private readonly string _path = path;
    private readonly List<SyntaxToken> _tokens = tokens;
    private readonly DiagnosticBag _diagnostics = diagnostics;
    private int _i;
    private int _speculating;
    private int _depth;
    private bool _nestingReported;
    private bool _tooDeep;

    /// <summary>Deepest nesting of expressions, statements and types the parser follows before giving up (it recurses once per level).</summary>
    private const int MaxNesting = 256;

    /// <summary>Counts one level of nesting; false (after one diagnostic) when the limit is passed, in which case the caller must return without recursing.</summary>
    private bool EnterNesting()
    {
        if (_depth >= MaxNesting)
        {
            if (!_nestingReported)
            {
                _nestingReported = true;
                ReportAtCurrent(Descriptors.NestingTooDeep);
            }

            _tooDeep = true; // unwinding reports one error per level otherwise
            SkipRemainingTokens();
            return false;
        }

        _depth++;
        return true;
    }

    private void ExitNesting()
    {
        if (--_depth == 0)
        {
            _tooDeep = false;
        }
    }

    // ------------------------------------------------------------------ token access

    private SyntaxToken Current => _tokens[_i];
    private SyntaxKind Kind => _tokens[_i].Kind;
    private SyntaxToken Previous => _tokens[Math.Max(0, _i - 1)];

    private SyntaxToken PeekToken(int n)
    {
        int idx = _i + n;
        return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
    }

    private SyntaxKind PeekKind(int n)
    {
        return PeekToken(n).Kind;
    }

    private SyntaxToken Eat()
    {
        SyntaxToken t = _tokens[_i];
        if (t.Kind != SyntaxKind.EndOfFileToken)
        {
            _i++;
        }

        return t;
    }

    private SyntaxToken? TryEat(SyntaxKind k)
    {
        return Kind == k ? Eat() : null;
    }

    private SyntaxToken Expect(SyntaxKind k)
    {
        if (Kind == k)
        {
            return Eat();
        }

        ReportExpected(SyntaxFacts.GetText(k));
        return Missing(k);
    }

    private SyntaxToken Missing(SyntaxKind k)
    {
        return new(k, "", Current.FullStart, [], [], null, isMissing: true);
    }

    private void ReportExpected(string what)
    {
        int pos = ErrorPosition();
        Report(Descriptors.ExpectedToken, new TextSpan(pos, 0), what);
    }

    private int ErrorPosition()
    {
        return _i > 0 && _speculating == 0 ? Previous.End : Current.Start;
    }

    private void Report(DiagnosticDescriptor d, TextSpan span, params object?[] args)
    {
        if (_speculating > 0) { _specErrors++; return; }
        if (_tooDeep) { return; }
        _diagnostics.Add(Diagnostic.Create(d, _path, span, args));
    }

    private void ReportAtCurrent(DiagnosticDescriptor d, params object?[] args)
    {
        Report(d, Current.Span.IsEmpty && Kind == SyntaxKind.EndOfFileToken ? new TextSpan(Current.Start, 0) : Current.Span, args);
    }

    /// <summary>Skips the current token, folding its text into the next token's leading trivia.</summary>
    private void SkipCurrent()
    {
        if (Kind == SyntaxKind.EndOfFileToken)
        {
            return;
        }

        if (_speculating > 0) { _i++; return; }
        SyntaxToken skipped = Current;
        SyntaxToken next = _tokens[_i + 1];
        _tokens[_i + 1] = new SyntaxToken(next.Kind, next.Text, skipped.FullStart, [new SyntaxTrivia(SyntaxKind.SkippedTokensTrivia, skipped.ToFullString()), .. next.LeadingTrivia], next.TrailingTrivia, next.Value, next.IsMissingToken);
        _i++;
    }

    /// <summary>Adds already-consumed text to the front of the current token's leading trivia as skipped text, so the tree still round-trips.</summary>
    private void PrependSkipped(string text)
    {
        if (text.Length == 0 || _speculating > 0)
        {
            return;
        }

        SyntaxToken t = _tokens[_i];
        _tokens[_i] = new SyntaxToken(t.Kind, t.Text, t.FullStart - text.Length, [new SyntaxTrivia(SyntaxKind.SkippedTokensTrivia, text), .. t.LeadingTrivia], t.TrailingTrivia, t.Value, t.IsMissingToken);
    }

    /// <summary>Gives up on the rest of the file: its text is kept as skipped trivia on the end-of-file token so the tree still round-trips.</summary>
    private void SkipRemainingTokens()
    {
        int eof = _tokens.Count - 1;
        if (_i >= eof)
        {
            return;
        }

        if (_speculating == 0)
        {
            System.Text.StringBuilder text = new();
            for (int k = _i; k < eof; k++)
            {
                _ = text.Append(_tokens[k].ToFullString());
            }

            SyntaxToken end = _tokens[eof];
            _tokens[eof] = new SyntaxToken(end.Kind, end.Text, _tokens[_i].FullStart, [new SyntaxTrivia(SyntaxKind.SkippedTokensTrivia, text.ToString()), .. end.LeadingTrivia], end.TrailingTrivia, end.Value, end.IsMissingToken);
        }

        _i = eof;
    }

    /// <summary>Folds a token that has no place in the tree into the leading trivia of <paramref name="next"/>.</summary>
    private static SyntaxToken Absorb(SyntaxToken skipped, SyntaxToken next)
    {
        ImmutableArray<SyntaxTrivia>.Builder trivia = ImmutableArray.CreateBuilder<SyntaxTrivia>();
        trivia.Add(new SyntaxTrivia(SyntaxKind.SkippedTokensTrivia, skipped.ToFullString()));
        trivia.AddRange(next.LeadingTrivia);
        int start = skipped.IsMissing ? next.FullStart : skipped.FullStart;
        return new SyntaxToken(next.Kind, next.Text, start, trivia.ToImmutable(), next.TrailingTrivia, next.Value, next.IsMissingToken);
    }

    private bool HasNewlineBefore(int index)
    {
        SyntaxToken t = _tokens[index];
        foreach (SyntaxTrivia tr in t.LeadingTrivia)
        {
            if (tr.Kind == SyntaxKind.EndOfLineTrivia || (tr.Kind == SyntaxKind.MultiLineCommentTrivia && tr.Text.Contains('\n')))
            {
                return true;
            }
        }

        if (index > 0)
        {
            foreach (SyntaxTrivia tr in _tokens[index - 1].TrailingTrivia)
            {
                if (tr.Kind == SyntaxKind.EndOfLineTrivia)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool NewlineBeforeCurrent => HasNewlineBefore(_i);

    private bool NewlineBeforePeek(int n)
    {
        return HasNewlineBefore(Math.Min(_i + n, _tokens.Count - 1));
    }

    // ------------------------------------------------------------------ speculation

    private int _specErrors;

    /// <summary>
    /// Runs <paramref name="parse"/> without side effects; if it produced no errors and a result, runs it again for real.
    /// </summary>
    private T? Speculate<T>(Func<T?> parse) where T : class
    {
        int save = _i;
        int errors = _specErrors;
        _speculating++;
        T? result;
        try { result = parse(); }
        finally { _speculating--; }
        bool failed = result == null || _specErrors != errors;
        _specErrors = errors;
        _i = save;
        return failed ? null : parse();
    }

    // ------------------------------------------------------------------ list helpers

    private static ListNode MakeList(List<SyntaxElement> items)
    {
        return new ListNode(SyntaxKind.List, [.. items]);
    }

    private static ListNode MakeSepList(List<SyntaxElement> items)
    {
        return new ListNode(SyntaxKind.SeparatedList, [.. items]);
    }

    private ListNode ParseSeparatedList<T>(Func<T> parseElement, Func<bool> isEnd, Func<bool>? isStartOfElement = null) where T : SyntaxElement
    {
        List<SyntaxElement> items = [];
        while (!isEnd() && Kind != SyntaxKind.EndOfFileToken)
        {
            int before = _i;
            items.Add(parseElement());
            if (Kind == SyntaxKind.CommaToken) { items.Add(Eat()); continue; }
            if (isEnd())
            {
                break;
            }
            // missing comma: if the next token could start another element, report and continue; otherwise stop
            if (isStartOfElement != null && isStartOfElement() && _i > before)
            {
                ReportExpected(",");
                items.Add(Missing(SyntaxKind.CommaToken));
                continue;
            }
            break;
        }
        return MakeSepList(items);
    }

    // ------------------------------------------------------------------ entry

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        List<SyntaxElement> statements = [];
        while (Kind != SyntaxKind.EndOfFileToken)
        {
            int before = _i;
            StatementSyntax s = ParseStatement(true);
            statements.Add(s);
            if (_i == before)
            {
                SkipCurrent();
            }
        }
        SyntaxToken eof = Expect(SyntaxKind.EndOfFileToken);
        return new CompilationUnitSyntax(MakeList(statements), eof);
    }

    private SyntaxToken ParseIdentifierToken()
    {
        if (SyntaxFacts.IsIdentifierLike(Kind))
        {
            return Eat();
        }
        // Reserved keywords used as identifiers: report but consume so progress is made in member positions.
        ReportExpected("identifier");
        return Missing(SyntaxKind.IdentifierToken);
    }

    /// <summary>Optional statement terminator with automatic semicolon insertion.</summary>
    private SyntaxToken? ParseSemicolon()
    {
        if (Kind == SyntaxKind.SemicolonToken)
        {
            return Eat();
        }

        if (Kind == SyntaxKind.CloseBraceToken || Kind == SyntaxKind.EndOfFileToken || NewlineBeforeCurrent)
        {
            return null;
        }

        ReportExpected(";");
        return null;
    }
}
