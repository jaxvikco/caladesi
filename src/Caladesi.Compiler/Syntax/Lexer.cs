using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Syntax;

/// <summary>
/// Converts source text into a flat token list with full trivia. '>' is always emitted as a single
/// token so nested generics parse; the parser recombines adjacent '>' tokens into shift/compare operators.
/// </summary>
public sealed class Lexer(SourceText source, string path, DiagnosticBag diagnostics)
{
    private readonly string _text = source.Text;
    private readonly string _path = path;
    private readonly DiagnosticBag _diagnostics = diagnostics;
    private int _pos;
    private readonly List<int> _templateBraceDepths = [];

#pragma warning disable CA1002 // internal-use token list shared with Parser, which mutates it
    public List<SyntaxToken> LexAll()
#pragma warning restore CA1002
    {
        List<SyntaxToken> tokens = [];
        if (_text.StartsWith("#!", StringComparison.Ordinal))
        {
            // shebang line is treated as a comment
        }
        ImmutableArray<SyntaxTrivia> leading = ReadLeadingTrivia(true);
        while (true)
        {
            int fullStart = _pos - TriviaWidth(leading);
            (SyntaxKind kind, string? text, object? value) = LexToken();
            ImmutableArray<SyntaxTrivia> trailing = ReadTrailingTrivia();
            tokens.Add(new SyntaxToken(kind, text, fullStart, leading, trailing, value));
            if (kind == SyntaxKind.EndOfFileToken)
            {
                break;
            }

            leading = ReadLeadingTrivia(false);
        }
        return tokens;
    }

    private static int TriviaWidth(ImmutableArray<SyntaxTrivia> trivia)
    {
        int w = 0;
        foreach (SyntaxTrivia t in trivia)
        {
            w += t.Width;
        }

        return w;
    }

    private void Report(DiagnosticDescriptor d, int start, int length, params object?[] args)
    {
        _diagnostics.Add(Diagnostic.Create(d, _path, new TextSpan(start, length), args));
    }

    private char Peek(int offset = 0)
    {
        return _pos + offset < _text.Length ? _text[_pos + offset] : '\0';
    }

    private static bool IsNewLine(char c)
    {
        return c is '\n' or '\r' or '\u2028' or '\u2029';
    }

    private ImmutableArray<SyntaxTrivia> ReadLeadingTrivia(bool first)
    {
        ImmutableArray<SyntaxTrivia>.Builder b = ImmutableArray.CreateBuilder<SyntaxTrivia>();
        if (first && _text.StartsWith("#!", StringComparison.Ordinal))
        {
            int s = _pos;
            while (_pos < _text.Length && !IsNewLine(_text[_pos]))
            {
                _pos++;
            }

            b.Add(new SyntaxTrivia(SyntaxKind.SingleLineCommentTrivia, _text[s.._pos]));
        }
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (IsNewLine(c))
            {
                b.Add(ReadNewLine());
            }
            else if (char.IsWhiteSpace(c))
            {
                b.Add(ReadWhitespace());
            }
            else if (c == '/' && Peek(1) == '/')
            {
                b.Add(ReadSingleLineComment());
            }
            else if (c == '/' && Peek(1) == '*')
            {
                b.Add(ReadMultiLineComment());
            }
            else
            {
                break;
            }
        }
        return b.ToImmutable();
    }

    private ImmutableArray<SyntaxTrivia> ReadTrailingTrivia()
    {
        ImmutableArray<SyntaxTrivia>.Builder b = ImmutableArray.CreateBuilder<SyntaxTrivia>();
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (IsNewLine(c))
            {
                b.Add(ReadNewLine());
                break;
            }
            if (char.IsWhiteSpace(c))
            {
                b.Add(ReadWhitespace());
            }
            else if (c == '/' && Peek(1) == '/')
            {
                b.Add(ReadSingleLineComment());
            }
            else if (c == '/' && Peek(1) == '*')
            {
                // A multi-line comment containing a newline belongs to the next token's leading trivia.
                int end = _text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                bool hasNewline = false;
                int limit = end < 0 ? _text.Length : end;
                for (int i = _pos; i < limit; i++)
                {
                    if (IsNewLine(_text[i])) { hasNewline = true; break; }
                }

                if (hasNewline)
                {
                    break;
                }

                b.Add(ReadMultiLineComment());
            }
            else
            {
                break;
            }
        }
        return b.ToImmutable();
    }

    private SyntaxTrivia ReadNewLine()
    {
        int s = _pos;
        if (_text[_pos] == '\r' && Peek(1) == '\n')
        {
            _pos += 2;
        }
        else
        {
            _pos++;
        }

        return new SyntaxTrivia(SyntaxKind.EndOfLineTrivia, _text[s.._pos]);
    }

    private SyntaxTrivia ReadWhitespace()
    {
        int s = _pos;
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]) && !IsNewLine(_text[_pos]))
        {
            _pos++;
        }

        return new SyntaxTrivia(SyntaxKind.WhitespaceTrivia, _text[s.._pos]);
    }

    private SyntaxTrivia ReadSingleLineComment()
    {
        int s = _pos;
        while (_pos < _text.Length && !IsNewLine(_text[_pos]))
        {
            _pos++;
        }

        return new SyntaxTrivia(SyntaxKind.SingleLineCommentTrivia, _text[s.._pos]);
    }

    private SyntaxTrivia ReadMultiLineComment()
    {
        int s = _pos;
        int end = _text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
        if (end < 0)
        {
            _pos = _text.Length;
            Report(Descriptors.UnterminatedComment, s, 2);
        }
        else
        {
            _pos = end + 2;
        }

        return new SyntaxTrivia(SyntaxKind.MultiLineCommentTrivia, _text[s.._pos]);
    }

    private (SyntaxKind, string, object?) LexToken()
    {
        if (_pos >= _text.Length)
        {
            return (SyntaxKind.EndOfFileToken, "", null);
        }

        int start = _pos;
        char c = _text[_pos];

        if (IsIdentifierStart(c))
        {
            _pos++;
            while (_pos < _text.Length && IsIdentifierPart(_text[_pos]))
            {
                _pos++;
            }

            string text = _text[start.._pos];
            return (SyntaxFacts.GetKeywordKind(text), text, null);
        }
        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(1))))
        {
            return LexNumber();
        }

        if (c is '"' or '\'')
        {
            return LexString(c);
        }

        if (c == '`')
        {
            return LexTemplate(start, isContinuation: false);
        }

        SyntaxKind Make(SyntaxKind k, int len)
        {
            _pos += len;
            return k;
        }

        SyntaxKind kind;
        switch (c)
        {
            case '{':
                if (_templateBraceDepths.Count > 0)
                {
                    _templateBraceDepths[^1]++;
                }

                kind = Make(SyntaxKind.OpenBraceToken, 1);
                break;
            case '}':
                if (_templateBraceDepths.Count > 0)
                {
                    if (_templateBraceDepths[^1] == 0)
                    {
                        _templateBraceDepths.RemoveAt(_templateBraceDepths.Count - 1);
                        return LexTemplate(start, isContinuation: true);
                    }
                    _templateBraceDepths[^1]--;
                }
                kind = Make(SyntaxKind.CloseBraceToken, 1);
                break;
            case '(': kind = Make(SyntaxKind.OpenParenToken, 1); break;
            case ')': kind = Make(SyntaxKind.CloseParenToken, 1); break;
            case '[': kind = Make(SyntaxKind.OpenBracketToken, 1); break;
            case ']': kind = Make(SyntaxKind.CloseBracketToken, 1); break;
            case ';': kind = Make(SyntaxKind.SemicolonToken, 1); break;
            case ',': kind = Make(SyntaxKind.CommaToken, 1); break;
            case ':': kind = Make(SyntaxKind.ColonToken, 1); break;
            case '~': kind = Make(SyntaxKind.TildeToken, 1); break;
            case '@': kind = Make(SyntaxKind.AtToken, 1); break;
            case '.':
                kind = Peek(1) == '.' && Peek(2) == '.' ? Make(SyntaxKind.DotDotDotToken, 3) : Make(SyntaxKind.DotToken, 1);
                break;
            case '?':
                kind = Peek(1) == '?'
                    ? Peek(2) == '=' ? Make(SyntaxKind.QuestionQuestionEqualsToken, 3) : Make(SyntaxKind.QuestionQuestionToken, 2)
                    : Peek(1) == '.' && !char.IsAsciiDigit(Peek(2)) ? Make(SyntaxKind.QuestionDotToken, 2) : Make(SyntaxKind.QuestionToken, 1);

                break;
            case '<':
                kind = Peek(1) == '<'
                    ? Peek(2) == '=' ? Make(SyntaxKind.LessThanLessThanEqualsToken, 3) : Make(SyntaxKind.LessThanLessThanToken, 2)
                    : Peek(1) == '=' ? Make(SyntaxKind.LessThanEqualsToken, 2) : Make(SyntaxKind.LessThanToken, 1);

                break;
            case '>': kind = Make(SyntaxKind.GreaterThanToken, 1); break;
            case '=':
                kind = Peek(1) == '='
                    ? Make(SyntaxKind.EqualsEqualsToken, 2)
                    : Peek(1) == '>' ? Make(SyntaxKind.EqualsGreaterThanToken, 2) : Make(SyntaxKind.EqualsToken, 1);

                break;
            case '!':
                kind = Peek(1) == '=' ? Make(SyntaxKind.ExclamationEqualsToken, 2) : Make(SyntaxKind.ExclamationToken, 1);
                break;
            case '+':
                kind = Peek(1) == '+' ? Make(SyntaxKind.PlusPlusToken, 2) : Peek(1) == '=' ? Make(SyntaxKind.PlusEqualsToken, 2) : Make(SyntaxKind.PlusToken, 1);
                break;
            case '-':
                kind = Peek(1) == '-' ? Make(SyntaxKind.MinusMinusToken, 2) : Peek(1) == '=' ? Make(SyntaxKind.MinusEqualsToken, 2) : Make(SyntaxKind.MinusToken, 1);
                break;
            case '*':
                kind = Peek(1) == '*'
                    ? Peek(2) == '=' ? Make(SyntaxKind.AsteriskAsteriskEqualsToken, 3) : Make(SyntaxKind.AsteriskAsteriskToken, 2)
                    : Peek(1) == '=' ? Make(SyntaxKind.AsteriskEqualsToken, 2) : Make(SyntaxKind.AsteriskToken, 1);
                break;
            case '/': kind = Peek(1) == '=' ? Make(SyntaxKind.SlashEqualsToken, 2) : Make(SyntaxKind.SlashToken, 1); break;
            case '%': kind = Peek(1) == '=' ? Make(SyntaxKind.PercentEqualsToken, 2) : Make(SyntaxKind.PercentToken, 1); break;
            case '^': kind = Peek(1) == '=' ? Make(SyntaxKind.CaretEqualsToken, 2) : Make(SyntaxKind.CaretToken, 1); break;
            case '&':
                kind = Peek(1) == '&'
                    ? Peek(2) == '=' ? Make(SyntaxKind.AmpersandAmpersandEqualsToken, 3) : Make(SyntaxKind.AmpersandAmpersandToken, 2)
                    : Peek(1) == '=' ? Make(SyntaxKind.AmpersandEqualsToken, 2) : Make(SyntaxKind.AmpersandToken, 1);
                break;
            case '|':
                kind = Peek(1) == '|'
                    ? Peek(2) == '=' ? Make(SyntaxKind.BarBarEqualsToken, 3) : Make(SyntaxKind.BarBarToken, 2)
                    : Peek(1) == '=' ? Make(SyntaxKind.BarEqualsToken, 2) : Make(SyntaxKind.BarToken, 1);
                break;
            default:
                _pos++;
                Report(Descriptors.UnexpectedCharacter, start, 1, c);
                return (SyntaxKind.BadToken, _text[start.._pos], null);
        }
        return (kind, _text[start.._pos], null);
    }

    private static bool IsIdentifierStart(char c)
    {
        return c == '_' || c == '$' || char.IsLetter(c);
    }

    private static bool IsIdentifierPart(char c)
    {
        return c == '_' || c == '$' || char.IsLetterOrDigit(c);
    }

    private (SyntaxKind, string, object?) LexNumber()
    {
        int start = _pos;
        bool isHexOrBin = false;
        int radix = 10;
        bool isReal = false;
        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X')) { radix = 16; _pos += 2; isHexOrBin = true; }
        else if (Peek() == '0' && (Peek(1) == 'b' || Peek(1) == 'B')) { radix = 2; _pos += 2; isHexOrBin = true; }

        int digitsStart = _pos;
        if (isHexOrBin)
        {
            // binary literals take every decimal digit so a stray `2` is reported as part of the literal
            while ((radix == 2 ? char.IsAsciiDigit(Peek()) : Uri.IsHexDigit(Peek())) || Peek() == '_')
            {
                _pos++;
            }
        }
        else
        {
            while (char.IsAsciiDigit(Peek()) || Peek() == '_')
            {
                _pos++;
            }

            if (Peek() == '.' && char.IsAsciiDigit(Peek(1)))
            {
                isReal = true;
                _pos++;
                while (char.IsAsciiDigit(Peek()) || Peek() == '_')
                {
                    _pos++;
                }
            }
            if ((Peek() == 'e' || Peek() == 'E') &&
                (char.IsAsciiDigit(Peek(1)) || ((Peek(1) == '+' || Peek(1) == '-') && char.IsAsciiDigit(Peek(2)))))
            {
                isReal = true;
                _pos += 2;
                while (char.IsAsciiDigit(Peek()))
                {
                    _pos++;
                }
            }
        }
        string digits = _text[digitsStart.._pos].Replace("_", "", StringComparison.Ordinal);

        // suffix
        int suffixStart = _pos;
        if (!isHexOrBin || radix == 2)
        {
            // binary digits may be followed by suffix; hex digits already consumed any f/d letters
        }
        while (_pos < _text.Length && char.IsAsciiLetter(_text[_pos]) && _pos - suffixStart < 2)
        {
            _pos++;
        }

        string? suffix = _text[suffixStart.._pos].ToLowerInvariant();
        string tokenText = _text[start.._pos];

        object? value = null;
        SyntaxKind kind = SyntaxKind.NumericLiteralToken;
        bool ok = true;
        string? error = null;

        if (suffix is not ("" or "l" or "u" or "ul" or "lu" or "f" or "d" or "m" or "n"))
        {
            // not a valid suffix: leave the letters for the next token, but report
            _pos = suffixStart;
            suffix = "";
            tokenText = _text[start.._pos];
            Report(Descriptors.InvalidNumber, start, tokenText.Length, "invalid suffix");
            ok = false;
        }

        if (ok)
        {
            try
            {
                if (isHexOrBin && digits.Length == 0) { error = "missing digits"; ok = false; }
                else if (radix == 2 && digits.Any(c => c is not ('0' or '1'))) { error = "invalid digit in binary literal"; ok = false; }
                else if (suffix == "n")
                {
                    kind = SyntaxKind.BigIntLiteralToken;
                    value = isHexOrBin ? ParseBig(digits, radix) : BigInteger.Parse(digits, CultureInfo.InvariantCulture);
                }
                else if (suffix == "f")
                {
                    value = float.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (float.IsInfinity((float)value)) { error = "out of range"; ok = false; } // modern .NET parses overflow to Infinity instead of throwing
                }
                else if (suffix == "d")
                {
                    value = double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (double.IsInfinity((double)value)) { error = "out of range"; ok = false; }
                }
                else if (suffix == "m")
                {
                    value = decimal.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                else if (isReal)
                {
                    value = double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (double.IsInfinity((double)value)) { error = "out of range"; ok = false; }
                }
                else
                {
                    BigInteger big = isHexOrBin ? ParseBig(digits, radix) : BigInteger.Parse(digits, CultureInfo.InvariantCulture);
                    value = FitInteger(big, suffix, isHexOrBin, ref error);
                    if (value == null)
                    {
                        ok = false;
                    }
                }
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            {
                error = "malformed or out of range";
                ok = false;
            }
        }
        if (!ok && error != null)
        {
            Report(Descriptors.InvalidNumber, start, tokenText.Length, error);
        }

        if (!ok)
        {
            value = 0;
        }

        return (kind, tokenText, value);
    }

    private static BigInteger ParseBig(string digits, int radix)
    {
        BigInteger r = BigInteger.Zero;
        foreach (char ch in digits)
        {
            r = (r * radix) + Convert.ToInt32(ch.ToString(), 16);
        }

        return r;
    }

    // Returns a boxed long/ulong/BigInteger depending on the literal, or null when out of range, so the return type must stay object.
#pragma warning disable CA1859
    private static object? FitInteger(BigInteger v, string suffix, bool hexOrBin, ref string? error)
    {
        switch (suffix)
        {
            case "l":
                if (v <= long.MaxValue)
                {
                    return (long)v;
                }

                if (hexOrBin && v <= ulong.MaxValue)
                {
                    return (ulong)v;
                }

                break;
            case "u":
                if (v <= uint.MaxValue)
                {
                    return (uint)v;
                }

                if (v <= ulong.MaxValue)
                {
                    return (ulong)v;
                }

                break;
            case "ul" or "lu":
                if (v <= ulong.MaxValue)
                {
                    return (ulong)v;
                }

                break;
            default:
                if (v <= int.MaxValue)
                {
                    return (int)v;
                }

                if (hexOrBin && v <= uint.MaxValue)
                {
                    return (uint)v;
                }

                if (v <= long.MaxValue)
                {
                    return (long)v;
                }

                if (v <= ulong.MaxValue)
                {
                    return (ulong)v;
                }

                break;
        }
        error = "value is out of range";
        return null;
    }
#pragma warning restore CA1859

    private (SyntaxKind, string, object?) LexString(char quote)
    {
        int start = _pos;
        _pos++;
        StringBuilder sb = new();
        while (true)
        {
            if (_pos >= _text.Length || IsNewLine(_text[_pos]))
            {
                Report(Descriptors.UnterminatedString, start, _pos - start);
                break;
            }
            char c = _text[_pos];
            if (c == quote) { _pos++; break; }
            if (c == '\\')
            {
                ReadEscape(sb);
            }
            else { _ = sb.Append(c); _pos++; }
        }
        return (SyntaxKind.StringLiteralToken, _text[start.._pos], sb.ToString());
    }

    private void ReadEscape(StringBuilder sb)
    {
        int escStart = _pos;
        _pos++; // backslash
        if (_pos >= _text.Length)
        {
            return;
        }

        char c = _text[_pos++];
        switch (c)
        {
            case 'n': _ = sb.Append('\n'); break;
            case 'r': _ = sb.Append('\r'); break;
            case 't': _ = sb.Append('\t'); break;
            case 'b': _ = sb.Append('\b'); break;
            case 'f': _ = sb.Append('\f'); break;
            case 'v': _ = sb.Append('\v'); break;
            case '0': _ = sb.Append('\0'); break;
            case '\\': _ = sb.Append('\\'); break;
            case '\'': _ = sb.Append('\''); break;
            case '"': _ = sb.Append('"'); break;
            case '`': _ = sb.Append('`'); break;
            case '$': _ = sb.Append('$'); break;
            case '\r': if (Peek() == '\n') { _pos++; } break; // line continuation
            case '\n': break;
            case 'x':
                if (TryHex(2, out int x))
                {
                    _ = sb.Append((char)x);
                }
                else
                {
                    Report(Descriptors.InvalidEscape, escStart, _pos - escStart);
                }

                break;
            case 'u':
                if (Peek() == '{')
                {
                    int close = _text.IndexOf('}', _pos);
                    if (close > 0 && int.TryParse(_text.AsSpan(_pos + 1, close - _pos - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)
                        && cp is >= 0 and <= 0x10FFFF)
                    {
                        // like the 4-digit form, a lone surrogate is a valid UTF-16 code unit in a string
                        _ = cp <= 0xFFFF ? sb.Append((char)cp) : sb.Append(char.ConvertFromUtf32(cp));
                        _pos = close + 1;
                    }
                    else
                    {
                        Report(Descriptors.InvalidEscape, escStart, _pos - escStart);
                    }
                }
                else if (TryHex(4, out int u))
                {
                    _ = sb.Append((char)u);
                }
                else
                {
                    Report(Descriptors.InvalidEscape, escStart, _pos - escStart);
                }

                break;
            default:
                Report(Descriptors.InvalidEscape, escStart, _pos - escStart);
                _ = sb.Append(c);
                break;
        }
    }

    private bool TryHex(int count, out int value)
    {
        value = 0;
        if (_pos + count > _text.Length)
        {
            return false;
        }

        if (!int.TryParse(_text.AsSpan(_pos, count), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        _pos += count;
        return true;
    }

    /// <summary>Lexes a template chunk. Starts at a backtick, or at a '}' that closes a substitution.</summary>
    private (SyntaxKind, string, object?) LexTemplate(int start, bool isContinuation)
    {
        _pos++; // ` or }
        StringBuilder sb = new();
        while (true)
        {
            if (_pos >= _text.Length)
            {
                Report(Descriptors.UnterminatedTemplate, start, _pos - start);
                return (isContinuation ? SyntaxKind.TemplateTailToken : SyntaxKind.NoSubstitutionTemplateToken, _text[start.._pos], sb.ToString());
            }
            char c = _text[_pos];
            if (c == '`')
            {
                _pos++;
                return (isContinuation ? SyntaxKind.TemplateTailToken : SyntaxKind.NoSubstitutionTemplateToken, _text[start.._pos], sb.ToString());
            }
            if (c == '$' && Peek(1) == '{')
            {
                _pos += 2;
                _templateBraceDepths.Add(0);
                return (isContinuation ? SyntaxKind.TemplateMiddleToken : SyntaxKind.TemplateHeadToken, _text[start.._pos], sb.ToString());
            }
            if (c == '\\')
            {
                ReadEscape(sb);
            }
            else if (c == '\r')
            {
                // normalize CRLF to LF in cooked value
                _pos++;
                if (Peek() == '\n')
                {
                    _pos++;
                }

                _ = sb.Append('\n');
            }
            else { _ = sb.Append(c); _pos++; }
        }
    }
}
