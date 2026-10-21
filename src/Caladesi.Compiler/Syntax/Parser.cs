using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Syntax;

/// <summary>
/// Hand-written, error-tolerant recursive-descent parser. Always produces a full-fidelity tree whose
/// text round-trips to the input; unexpected tokens are preserved as skipped-token trivia and missing
/// tokens are synthesized with zero width.
/// </summary>
public sealed partial class Parser
{
    private readonly SourceText _source;
    private readonly string _path;
    private readonly List<SyntaxToken> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _i;
    private int _speculating;

    public Parser(SourceText source, string path, List<SyntaxToken> tokens, DiagnosticBag diagnostics)
    {
        _source = source;
        _path = path;
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    // ------------------------------------------------------------------ token access

    private SyntaxToken Current => _tokens[_i];
    private SyntaxKind Kind => _tokens[_i].Kind;
    private SyntaxToken Previous => _tokens[Math.Max(0, _i - 1)];

    private SyntaxToken PeekToken(int n)
    {
        var idx = _i + n;
        return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
    }

    private SyntaxKind PeekKind(int n) => PeekToken(n).Kind;

    private bool At(SyntaxKind k) => Kind == k;

    private SyntaxToken Eat()
    {
        var t = _tokens[_i];
        if (t.Kind != SyntaxKind.EndOfFileToken) _i++;
        return t;
    }

    private SyntaxToken? TryEat(SyntaxKind k) => Kind == k ? Eat() : null;

    private SyntaxToken Expect(SyntaxKind k)
    {
        if (Kind == k) return Eat();
        ReportExpected(SyntaxFacts.GetText(k));
        return Missing(k);
    }

    private SyntaxToken Missing(SyntaxKind k) =>
        new(k, "", Current.FullStart, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty, null, isMissing: true);

    private void ReportExpected(string what)
    {
        var pos = ErrorPosition();
        Report(Descriptors.ExpectedToken, new TextSpan(pos, 0), what);
    }

    private int ErrorPosition() => _i > 0 && _speculating == 0 ? Previous.End : Current.Start;

    private void Report(DiagnosticDescriptor d, TextSpan span, params object?[] args)
    {
        if (_speculating > 0) { _specErrors++; return; }
        _diagnostics.Add(Diagnostic.Create(d, _path, span, args));
    }

    private void ReportAtCurrent(DiagnosticDescriptor d, params object?[] args) =>
        Report(d, Current.Span.IsEmpty && Kind == SyntaxKind.EndOfFileToken ? new TextSpan(Current.Start, 0) : Current.Span, args);

    /// <summary>Skips the current token, folding its text into the next token's leading trivia.</summary>
    private void SkipCurrent()
    {
        if (Kind == SyntaxKind.EndOfFileToken) return;
        if (_speculating > 0) { _i++; return; }
        var skipped = Current;
        var next = _tokens[_i + 1];
        var trivia = ImmutableArray.CreateBuilder<SyntaxTrivia>();
        trivia.Add(new SyntaxTrivia(SyntaxKind.SkippedTokensTrivia, skipped.ToFullString()));
        trivia.AddRange(next.LeadingTrivia);
        _tokens[_i + 1] = new SyntaxToken(next.Kind, next.Text, skipped.FullStart, trivia.ToImmutable(), next.TrailingTrivia, next.Value, next.IsMissingToken);
        _i++;
    }

    /// <summary>Folds a token that has no place in the tree into the leading trivia of <paramref name="next"/>.</summary>
    private static SyntaxToken Absorb(SyntaxToken skipped, SyntaxToken next)
    {
        var trivia = ImmutableArray.CreateBuilder<SyntaxTrivia>();
        trivia.Add(new SyntaxTrivia(SyntaxKind.SkippedTokensTrivia, skipped.ToFullString()));
        trivia.AddRange(next.LeadingTrivia);
        var start = skipped.IsMissing ? next.FullStart : skipped.FullStart;
        return new SyntaxToken(next.Kind, next.Text, start, trivia.ToImmutable(), next.TrailingTrivia, next.Value, next.IsMissingToken);
    }

    private bool HasNewlineBefore(int index)
    {
        var t = _tokens[index];
        foreach (var tr in t.LeadingTrivia)
            if (tr.Kind == SyntaxKind.EndOfLineTrivia || (tr.Kind == SyntaxKind.MultiLineCommentTrivia && tr.Text.Contains('\n'))) return true;
        if (index > 0)
            foreach (var tr in _tokens[index - 1].TrailingTrivia)
                if (tr.Kind == SyntaxKind.EndOfLineTrivia) return true;
        return false;
    }

    private bool NewlineBeforeCurrent => HasNewlineBefore(_i);

    private bool NewlineBeforePeek(int n) => HasNewlineBefore(Math.Min(_i + n, _tokens.Count - 1));

    // ------------------------------------------------------------------ speculation

    private int _specErrors;

    /// <summary>
    /// Runs <paramref name="parse"/> without side effects; if it produced no errors and a result, runs it again for real.
    /// </summary>
    private T? Speculate<T>(Func<T?> parse) where T : class
    {
        var save = _i;
        var errors = _specErrors;
        _speculating++;
        T? result;
        try { result = parse(); }
        finally { _speculating--; }
        var failed = result == null || _specErrors != errors;
        _specErrors = errors;
        _i = save;
        return failed ? null : parse();
    }

    // ------------------------------------------------------------------ list helpers

    private static SyntaxNode MakeList(List<SyntaxElement> items) => new ListNode(SyntaxKind.List, items.ToArray());
    private static SyntaxNode MakeSepList(List<SyntaxElement> items) => new ListNode(SyntaxKind.SeparatedList, items.ToArray());
    private static SyntaxNode EmptyList() => new ListNode(SyntaxKind.List, Array.Empty<SyntaxElement>());

    private SyntaxNode ParseSeparatedList<T>(Func<T> parseElement, Func<bool> isEnd, Func<bool>? isStartOfElement = null) where T : SyntaxElement
    {
        var items = new List<SyntaxElement>();
        while (!isEnd() && Kind != SyntaxKind.EndOfFileToken)
        {
            var before = _i;
            items.Add(parseElement());
            if (Kind == SyntaxKind.CommaToken) { items.Add(Eat()); continue; }
            if (isEnd()) break;
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
        var statements = new List<SyntaxElement>();
        while (Kind != SyntaxKind.EndOfFileToken)
        {
            var before = _i;
            var s = ParseStatement(topLevel: true);
            statements.Add(s);
            if (_i == before) SkipCurrent();
        }
        var eof = Expect(SyntaxKind.EndOfFileToken);
        return new CompilationUnitSyntax(MakeList(statements), eof);
    }

    private SyntaxToken ParseIdentifierToken()
    {
        if (SyntaxFacts.IsIdentifierLike(Kind)) return Eat();
        // Reserved keywords used as identifiers: report but consume so progress is made in member positions.
        ReportExpected("identifier");
        return Missing(SyntaxKind.IdentifierToken);
    }

    /// <summary>Optional statement terminator with automatic semicolon insertion.</summary>
    private SyntaxToken? ParseSemicolon()
    {
        if (Kind == SyntaxKind.SemicolonToken) return Eat();
        if (Kind == SyntaxKind.CloseBraceToken || Kind == SyntaxKind.EndOfFileToken || NewlineBeforeCurrent) return null;
        ReportExpected(";");
        return null;
    }
}
