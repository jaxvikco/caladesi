using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Syntax;

public sealed partial class Parser
{
    private ExpressionSyntax ParseExpression() => ParseAssignmentExpression();

    // ------------------------------------------------------------------ '>' recombination

    /// <summary>
    /// If the current token starts a '>'-family operator (>=, >>, >>>, >>=, >>>=), builds the composite token
    /// from adjacent '>'/'=' tokens. Returns the operator kind and the number of source tokens to consume.
    /// </summary>
    private (SyntaxKind kind, int count) PeekGreaterThanFamily()
    {
        if (Kind != SyntaxKind.GreaterThanToken) return (Kind, 1);
        var n = 1;
        while (n < 3 && PeekKind(n) == SyntaxKind.GreaterThanToken && IsAdjacent(_i + n)) n++;
        var hasEquals = PeekKind(n) == SyntaxKind.EqualsToken && IsAdjacent(_i + n);
        var kind = (n, hasEquals) switch
        {
            (1, false) => SyntaxKind.GreaterThanToken,
            (1, true) => SyntaxKind.GreaterThanEqualsToken,
            (2, false) => SyntaxKind.GreaterThanGreaterThanToken,
            (2, true) => SyntaxKind.GreaterThanGreaterThanEqualsToken,
            (_, false) => SyntaxKind.GreaterThanGreaterThanGreaterThanToken,
            _ => SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken,
        };
        return (kind, n + (hasEquals ? 1 : 0));
    }

    private bool IsAdjacent(int index) => _tokens[index].LeadingTrivia.Length == 0 && _tokens[index - 1].TrailingTrivia.Length == 0;

    private SyntaxToken EatComposite(SyntaxKind kind, int count)
    {
        if (count == 1) return Eat();
        var first = _tokens[_i];
        var last = _tokens[_i + count - 1];
        var token = new SyntaxToken(kind, SyntaxFacts.GetText(kind), first.FullStart, first.LeadingTrivia, last.TrailingTrivia);
        _i += count;
        return token;
    }

    // ------------------------------------------------------------------ assignment / conditional / binary

    private ExpressionSyntax ParseAssignmentExpression()
    {
        if (Kind == SyntaxKind.YieldKeyword) return ParseYield();

        var arrow = TryParseArrowFunction();
        if (arrow != null) return arrow;

        var left = ParseConditionalExpression();
        var (opKind, count) = PeekGreaterThanFamily();
        if (SyntaxFacts.IsAssignmentOperator(opKind))
        {
            if (!IsValidAssignmentTarget(left))
                Report(Descriptors.InvalidAssignmentTarget, left.Span);
            var op = EatComposite(opKind, count);
            var right = ParseAssignmentExpression();
            return new AssignmentExpressionSyntax(left, op, right);
        }
        return left;
    }

    private static bool IsValidAssignmentTarget(ExpressionSyntax e) => e.Kind switch
    {
        SyntaxKind.IdentifierName or SyntaxKind.MemberAccessExpression or SyntaxKind.ElementAccessExpression
            or SyntaxKind.ArrayLiteralExpression or SyntaxKind.ObjectLiteralExpression or SyntaxKind.MissingExpression
            or SyntaxKind.ThisExpression => true,
        SyntaxKind.ParenthesizedExpression => IsValidAssignmentTarget(((ParenthesizedExpressionSyntax)e).Expression),
        SyntaxKind.NonNullExpression => IsValidAssignmentTarget(((NonNullExpressionSyntax)e).Expression),
        _ => false,
    };

    private ExpressionSyntax ParseYield()
    {
        var kw = Eat();
        SyntaxToken? star = null;
        ExpressionSyntax? operand = null;
        if (Kind == SyntaxKind.AsteriskToken && !NewlineBeforeCurrent) star = Eat();
        if (star != null || (!NewlineBeforeCurrent && IsStartOfExpression()))
            operand = ParseAssignmentExpression();
        return new YieldExpressionSyntax(kw, star, operand);
    }

    private bool IsStartOfExpression() => Kind switch
    {
        SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken or SyntaxKind.CommaToken
            or SyntaxKind.SemicolonToken or SyntaxKind.ColonToken or SyntaxKind.EndOfFileToken or SyntaxKind.EqualsToken
            or SyntaxKind.QuestionToken or SyntaxKind.EqualsGreaterThanToken => false,
        _ => true,
    };

    private ExpressionSyntax ParseConditionalExpression()
    {
        var cond = ParseBinaryExpression(0);
        if (Kind != SyntaxKind.QuestionToken) return cond;
        var q = Eat();
        var whenTrue = ParseAssignmentExpression();
        var colon = Expect(SyntaxKind.ColonToken);
        var whenFalse = ParseAssignmentExpression();
        return new ConditionalExpressionSyntax(cond, q, whenTrue, colon, whenFalse);
    }

    private ExpressionSyntax ParseBinaryExpression(int minPrecedence)
    {
        var left = ParseUnaryExpression();
        while (true)
        {
            var (opKind, count) = PeekGreaterThanFamily();
            var prec = SyntaxFacts.GetBinaryPrecedence(opKind);
            if (opKind is SyntaxKind.AsKeyword or SyntaxKind.SatisfiesKeyword or SyntaxKind.InstanceOfKeyword && NewlineBeforeCurrent && opKind != SyntaxKind.InstanceOfKeyword)
                break;
            if (prec == 0 || prec <= minPrecedence) break;
            var op = EatComposite(opKind, count);
            switch (opKind)
            {
                case SyntaxKind.AsKeyword:
                {
                    var saved = _noNullableMarker;
                    _noNullableMarker = true;
                    var t = ParseType();
                    _noNullableMarker = saved;
                    left = new AsExpressionSyntax(left, op, t);
                    break;
                }
                case SyntaxKind.SatisfiesKeyword:
                {
                    var saved = _noNullableMarker;
                    _noNullableMarker = true;
                    var t = ParseType();
                    _noNullableMarker = saved;
                    left = new SatisfiesExpressionSyntax(left, op, t);
                    break;
                }
                case SyntaxKind.InstanceOfKeyword:
                {
                    var saved = _noNullableMarker;
                    _noNullableMarker = true;
                    var t = ParseType();
                    _noNullableMarker = saved;
                    left = new InstanceOfExpressionSyntax(left, op, t);
                    break;
                }
                case SyntaxKind.AsteriskAsteriskToken:
                {
                    // right associative
                    var right = ParseBinaryExpression(prec - 1);
                    left = new BinaryExpressionSyntax(left, op, right);
                    break;
                }
                default:
                {
                    var right = ParseBinaryExpression(prec);
                    left = new BinaryExpressionSyntax(left, op, right);
                    break;
                }
            }
        }
        return left;
    }

    // ------------------------------------------------------------------ unary / postfix

    private ExpressionSyntax ParseUnaryExpression()
    {
        switch (Kind)
        {
            case SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.ExclamationToken or SyntaxKind.TildeToken
                or SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken:
            {
                var op = Eat();
                return new PrefixUnaryExpressionSyntax(op, ParseUnaryExpression());
            }
            case SyntaxKind.AwaitKeyword:
            {
                var kw = Eat();
                return new AwaitExpressionSyntax(kw, ParseUnaryExpression());
            }
            case SyntaxKind.ThrowKeyword:
            {
                var kw = Eat();
                return new ThrowExpressionSyntax(kw, ParseAssignmentExpression());
            }
            case SyntaxKind.TypeOfKeyword:
            {
                var kw = Eat();
                var open = Expect(SyntaxKind.OpenParenToken);
                var t = ParseType();
                var close = Expect(SyntaxKind.CloseParenToken);
                return new TypeOfExpressionSyntax(kw, open, t, close);
            }
            case SyntaxKind.VoidKeyword:
            {
                ReportAtCurrent(Descriptors.FeatureNotSupported, "The 'void' operator");
                var kw = Eat();
                return new PrefixUnaryExpressionSyntax(kw, ParseUnaryExpression());
            }
        }
        return ParsePostfixExpression();
    }

    private ExpressionSyntax ParsePostfixExpression()
    {
        var e = ParseLeftHandSideExpression();
        while (true)
        {
            if ((Kind == SyntaxKind.PlusPlusToken || Kind == SyntaxKind.MinusMinusToken) && !NewlineBeforeCurrent)
                e = new PostfixUnaryExpressionSyntax(e, Eat());
            else if (Kind == SyntaxKind.ExclamationToken && !NewlineBeforeCurrent)
                e = new NonNullExpressionSyntax(e, Eat());
            else break;
            // allow further member access after postfix '!' (x!.y)
            e = ParseCallSuffixes(e);
        }
        return e;
    }

    private ExpressionSyntax ParseLeftHandSideExpression()
    {
        var e = ParsePrimaryExpression();
        return ParseCallSuffixes(e);
    }

    private static bool CanFollowTypeArgumentsInExpression(SyntaxKind k) => k is SyntaxKind.OpenParenToken or SyntaxKind.CloseParenToken
        or SyntaxKind.CloseBracketToken or SyntaxKind.ColonToken or SyntaxKind.SemicolonToken or SyntaxKind.CommaToken
        or SyntaxKind.DotToken or SyntaxKind.QuestionDotToken or SyntaxKind.QuestionToken or SyntaxKind.EqualsEqualsToken
        or SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.ExclamationEqualsToken or SyntaxKind.ExclamationEqualsEqualsToken
        or SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken or SyntaxKind.QuestionQuestionToken
        or SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken or SyntaxKind.EqualsToken;

    private ExpressionSyntax ParseCallSuffixes(ExpressionSyntax e)
    {
        while (true)
        {
            switch (Kind)
            {
                case SyntaxKind.DotToken:
                {
                    var dot = Eat();
                    e = new MemberAccessExpressionSyntax(e, dot, ParseMemberName());
                    continue;
                }
                case SyntaxKind.QuestionDotToken:
                {
                    var qd = Eat();
                    if (Kind == SyntaxKind.OpenBracketToken)
                    {
                        var open = Eat();
                        var args = ParseSeparatedList(ParseArgument, () => Kind == SyntaxKind.CloseBracketToken);
                        var close = Expect(SyntaxKind.CloseBracketToken);
                        e = new ElementAccessExpressionSyntax(e, qd, open, args, close);
                    }
                    else if (Kind == SyntaxKind.OpenParenToken)
                    {
                        ReportAtCurrent(Descriptors.FeatureNotSupported, "Optional call '?.()'");
                        if (_speculating == 0) _tokens[_i] = Absorb(qd, Current);
                        e = new InvocationExpressionSyntax(e, ParseArgumentList());
                    }
                    else e = new MemberAccessExpressionSyntax(e, qd, ParseMemberName());
                    continue;
                }
                case SyntaxKind.OpenBracketToken when !NewlineBeforeCurrent:
                {
                    var open = Eat();
                    var args = ParseSeparatedList(ParseArgument, () => Kind == SyntaxKind.CloseBracketToken);
                    var close = Expect(SyntaxKind.CloseBracketToken);
                    e = new ElementAccessExpressionSyntax(e, null, open, args, close);
                    continue;
                }
                case SyntaxKind.OpenParenToken when !NewlineBeforeCurrent || true:
                {
                    e = new InvocationExpressionSyntax(e, ParseArgumentList());
                    continue;
                }
                case SyntaxKind.LessThanToken when e is IdentifierNameSyntax || (e is MemberAccessExpressionSyntax { Name: IdentifierNameSyntax }):
                {
                    // possible generic invocation / generic name: f<T>(...)
                    var generic = Speculate(() =>
                    {
                        var args = ParseTypeArgumentList();
                        if (args.IsMissing || args.GreaterThanToken.IsMissing) return null;
                        return CanFollowTypeArgumentsInExpression(Kind) ? args : null;
                    });
                    if (generic == null) return e;
                    e = WithTypeArguments(e, generic);
                    continue;
                }
                default:
                    return e;
            }
        }
    }

    private ExpressionSyntax WithTypeArguments(ExpressionSyntax e, TypeArgumentListSyntax args)
    {
        switch (e)
        {
            case IdentifierNameSyntax id:
                return new GenericNameSyntax(DetachToken(id.Identifier), args);
            case MemberAccessExpressionSyntax ma when ma.Name is IdentifierNameSyntax n:
                return new MemberAccessExpressionSyntax(ma.Expression, ma.OperatorToken, new GenericNameSyntax(DetachToken(n.Identifier), args));
            default:
                ReportAtCurrent(Descriptors.UnexpectedToken, "<");
                return e;
        }
    }

    // Re-attaching an existing node's token to a new parent requires a fresh token object (tokens have one parent).
    private static SyntaxToken DetachToken(SyntaxToken t) =>
        new(t.Kind, t.Text, t.FullStart, t.LeadingTrivia, t.TrailingTrivia, t.Value, t.IsMissingToken);

    private SimpleNameSyntax ParseMemberName()
    {
        // any identifier-like token or keyword may follow a dot
        if (SyntaxFacts.IsIdentifierLike(Kind) || SyntaxFacts.IsKeyword(Kind))
        {
            var t = Eat();
            return new IdentifierNameSyntax(t.WithKind(SyntaxKind.IdentifierToken));
        }
        ReportExpected("identifier");
        return new IdentifierNameSyntax(Missing(SyntaxKind.IdentifierToken));
    }

    private ArgumentListSyntax ParseArgumentList()
    {
        var open = Expect(SyntaxKind.OpenParenToken);
        var args = ParseSeparatedList(ParseArgument, () => Kind == SyntaxKind.CloseParenToken);
        var close = Expect(SyntaxKind.CloseParenToken);
        return new ArgumentListSyntax(open, args, close);
    }

    private ExpressionSyntax ParseArgument()
    {
        if (Kind == SyntaxKind.DotDotDotToken)
        {
            var dots = Eat();
            return new SpreadElementSyntax(dots, ParseAssignmentExpression());
        }
        return ParseAssignmentExpression();
    }

    // ------------------------------------------------------------------ primary

    private ExpressionSyntax ParsePrimaryExpression()
    {
        switch (Kind)
        {
            case SyntaxKind.NumericLiteralToken: return new LiteralExpressionSyntax(SyntaxKind.NumericLiteralExpression, Eat());
            case SyntaxKind.BigIntLiteralToken: return new LiteralExpressionSyntax(SyntaxKind.BigIntLiteralExpression, Eat());
            case SyntaxKind.StringLiteralToken: return new LiteralExpressionSyntax(SyntaxKind.StringLiteralExpression, Eat());
            case SyntaxKind.TrueKeyword: return new LiteralExpressionSyntax(SyntaxKind.TrueLiteralExpression, Eat());
            case SyntaxKind.FalseKeyword: return new LiteralExpressionSyntax(SyntaxKind.FalseLiteralExpression, Eat());
            case SyntaxKind.NullKeyword: return new LiteralExpressionSyntax(SyntaxKind.NullLiteralExpression, Eat());
            case SyntaxKind.NoSubstitutionTemplateToken:
                return new LiteralExpressionSyntax(SyntaxKind.NoSubstitutionTemplateExpression, Eat());
            case SyntaxKind.TemplateHeadToken: return ParseTemplateExpression();
            case SyntaxKind.ThisKeyword: return new ThisExpressionSyntax(Eat());
            case SyntaxKind.SuperKeyword: return new SuperExpressionSyntax(Eat());
            case SyntaxKind.OpenParenToken:
            {
                var open = Eat();
                var e = ParseExpression();
                var close = Expect(SyntaxKind.CloseParenToken);
                return new ParenthesizedExpressionSyntax(open, e, close);
            }
            case SyntaxKind.OpenBracketToken: return ParseArrayLiteral();
            case SyntaxKind.OpenBraceToken: return ParseObjectLiteral();
            case SyntaxKind.FunctionKeyword: return ParseFunctionExpression(null);
            case SyntaxKind.AsyncKeyword when PeekKind(1) == SyntaxKind.FunctionKeyword && !NewlineBeforePeek(1):
                return ParseFunctionExpression(Eat());
            case SyntaxKind.NewKeyword: return ParseNewExpression();
            case SyntaxKind.ClassKeyword:
                ReportAtCurrent(Descriptors.FeatureNotSupported, "Class expressions");
                break;
            case SyntaxKind.SlashToken or SyntaxKind.SlashEqualsToken:
                ReportAtCurrent(Descriptors.RegexNotSupported);
                break;
            default:
                if (SyntaxFacts.IsIdentifierLike(Kind)) return new IdentifierNameSyntax(Eat());
                break;
        }
        // error recovery
        ReportAtCurrent(Descriptors.ExpectedExpression);
        return new MissingExpressionSyntax(Missing(SyntaxKind.IdentifierToken));
    }

    private ExpressionSyntax ParseTemplateExpression()
    {
        var head = Eat();
        var spans = new List<SyntaxElement>();
        while (true)
        {
            var expr = ParseExpression();
            SyntaxToken literal;
            if (Kind == SyntaxKind.TemplateMiddleToken) literal = Eat();
            else if (Kind == SyntaxKind.TemplateTailToken) literal = Eat();
            else
            {
                ReportExpected("}");
                literal = Missing(SyntaxKind.TemplateTailToken);
                spans.Add(new TemplateSpanSyntax(expr, literal));
                break;
            }
            spans.Add(new TemplateSpanSyntax(expr, literal));
            if (literal.Kind == SyntaxKind.TemplateTailToken) break;
        }
        return new TemplateExpressionSyntax(head, MakeList(spans));
    }

    private ExpressionSyntax ParseArrayLiteral()
    {
        var open = Eat();
        var elements = ParseSeparatedList(ParseArgument, () => Kind == SyntaxKind.CloseBracketToken);
        var close = Expect(SyntaxKind.CloseBracketToken);
        return new ArrayLiteralExpressionSyntax(open, elements, close);
    }

    private ExpressionSyntax ParseObjectLiteral()
    {
        var open = Eat();
        var members = ParseSeparatedList<SyntaxNode>(ParseObjectLiteralMember, () => Kind == SyntaxKind.CloseBraceToken);
        var close = Expect(SyntaxKind.CloseBraceToken);
        return new ObjectLiteralExpressionSyntax(open, members, close);
    }

    private SyntaxNode ParseObjectLiteralMember()
    {
        if (Kind == SyntaxKind.DotDotDotToken)
        {
            var dots = Eat();
            return new SpreadAssignmentSyntax(dots, ParseAssignmentExpression());
        }
        SyntaxToken name;
        if (Kind == SyntaxKind.StringLiteralToken || Kind == SyntaxKind.NumericLiteralToken) name = Eat();
        else if (SyntaxFacts.IsIdentifierLike(Kind) || SyntaxFacts.IsKeyword(Kind)) name = Eat().WithKind(SyntaxKind.IdentifierToken);
        else
        {
            ReportExpected("property name");
            SkipCurrent();
            name = Missing(SyntaxKind.IdentifierToken);
        }
        if (Kind == SyntaxKind.ColonToken)
        {
            var colon = Eat();
            return new PropertyAssignmentSyntax(name, colon, ParseAssignmentExpression());
        }
        if (Kind == SyntaxKind.OpenParenToken)
        {
            ReportAtCurrent(Descriptors.FeatureNotSupported, "Method shorthand in object literals");
            // skip the unsupported method shorthand so the text is preserved
            var depth = 0;
            while (Kind != SyntaxKind.EndOfFileToken)
            {
                if (Kind is SyntaxKind.OpenParenToken or SyntaxKind.OpenBraceToken) depth++;
                else if (Kind is SyntaxKind.CloseParenToken or SyntaxKind.CloseBraceToken) depth--;
                SkipCurrent();
                if (depth == 0 && (PeekKind(-1) == SyntaxKind.CloseBraceToken || (PeekKind(-1) == SyntaxKind.CloseParenToken && Kind != SyntaxKind.OpenBraceToken && Kind != SyntaxKind.ColonToken))) break;
            }
        }
        return new ShorthandPropertyAssignmentSyntax(name);
    }

    private ExpressionSyntax ParseNewExpression()
    {
        var kw = Eat();
        // type without type-arguments ambiguity: after `new`, always a type
        var type = ParseNewTypeName();
        if (Kind == SyntaxKind.OpenBracketToken && PeekKind(1) != SyntaxKind.CloseBracketToken)
        {
            var open = Eat();
            var size = ParseExpression();
            var close = Expect(SyntaxKind.CloseBracketToken);
            return new ArrayCreationExpressionSyntax(kw, type, open, size, close);
        }
        ArgumentListSyntax? args = Kind == SyntaxKind.OpenParenToken ? ParseArgumentList() : null;
        return new NewExpressionSyntax(kw, type, args);
    }

    private TypeSyntax ParseNewTypeName()
    {
        if (Kind == SyntaxKind.OpenParenToken) { ReportAtCurrent(Descriptors.ExpectedType); return new MissingTypeSyntax(Missing(SyntaxKind.IdentifierToken)); }
        return ParseTypeName();
    }

    private ExpressionSyntax ParseFunctionExpression(SyntaxToken? asyncKw)
    {
        var fn = Expect(SyntaxKind.FunctionKeyword);
        var star = TryEat(SyntaxKind.AsteriskToken);
        SyntaxToken? name = SyntaxFacts.IsIdentifierLike(Kind) ? Eat() : null;
        var tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        var ps = ParseParameterList();
        var ret = ParseOptionalTypeAnnotation();
        var body = ParseBlock();
        return new FunctionExpressionSyntax(asyncKw, fn, star, name, tp, ps, ret, body);
    }

    // ------------------------------------------------------------------ arrow functions

    private ExpressionSyntax? TryParseArrowFunction()
    {
        switch (Kind)
        {
            case SyntaxKind.AsyncKeyword when !NewlineBeforePeek(1):
            {
                var k1 = PeekKind(1);
                if (SyntaxFacts.IsIdentifierLike(k1) && PeekKind(2) == SyntaxKind.EqualsGreaterThanToken)
                {
                    var asyncKw = Eat();
                    return ParseSimpleArrow(asyncKw);
                }
                if (k1 == SyntaxKind.OpenParenToken || k1 == SyntaxKind.LessThanToken)
                    return Speculate(() => ParseParenthesizedArrow(true));
                return null;
            }
            case SyntaxKind.OpenParenToken:
            case SyntaxKind.LessThanToken:
            {
                if (Kind == SyntaxKind.OpenParenToken)
                {
                    var after = SkipBalanced(_i);
                    if (after >= _tokens.Count) return null;
                    var ak = _tokens[after].Kind;
                    if (ak != SyntaxKind.EqualsGreaterThanToken && ak != SyntaxKind.ColonToken) return null;
                }
                return Speculate(() => ParseParenthesizedArrow(false));
            }
            default:
                if (SyntaxFacts.IsIdentifierLike(Kind) && PeekKind(1) == SyntaxKind.EqualsGreaterThanToken)
                    return ParseSimpleArrow(null);
                return null;
        }
    }

    private ExpressionSyntax ParseSimpleArrow(SyntaxToken? asyncKw)
    {
        var id = Eat();
        var arrow = Expect(SyntaxKind.EqualsGreaterThanToken);
        var body = ParseArrowBody();
        return new ArrowFunctionSyntax(asyncKw, null, null, id, null, arrow, body);
    }

    private ExpressionSyntax? ParseParenthesizedArrow(bool isAsync)
    {
        SyntaxToken? asyncKw = isAsync ? Eat() : null;
        var tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        if (Kind != SyntaxKind.OpenParenToken) return null;
        var ps = ParseParameterList();
        TypeAnnotationSyntax? ret = null;
        if (Kind == SyntaxKind.ColonToken)
        {
            var saved = _noNullableMarker;
            ret = ParseOptionalTypeAnnotation();
            _noNullableMarker = saved;
        }
        if (Kind != SyntaxKind.EqualsGreaterThanToken) return null;
        var arrow = Eat();
        var body = ParseArrowBody();
        return new ArrowFunctionSyntax(asyncKw, tp, ps, null, ret, arrow, body);
    }

    private SyntaxNode ParseArrowBody()
    {
        if (Kind == SyntaxKind.OpenBraceToken) return ParseBlock();
        return ParseAssignmentExpression();
    }
}
