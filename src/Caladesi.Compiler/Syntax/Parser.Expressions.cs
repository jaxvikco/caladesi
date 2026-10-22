using Caladesi.Compiler.Diagnostics;

namespace Caladesi.Compiler.Syntax;

public sealed partial class Parser
{
    private ExpressionSyntax ParseExpression()
    {
        return ParseAssignmentExpression();
    }

    // ------------------------------------------------------------------ '>' recombination

    /// <summary>
    /// If the current token starts a '>'-family operator (>=, >>, >>>, >>=, >>>=), builds the composite token
    /// from adjacent '>'/'=' tokens. Returns the operator kind and the number of source tokens to consume.
    /// </summary>
    private (SyntaxKind kind, int count) PeekGreaterThanFamily()
    {
        if (Kind != SyntaxKind.GreaterThanToken)
        {
            return (Kind, 1);
        }

        int n = 1;
        while (n < 3 && PeekKind(n) == SyntaxKind.GreaterThanToken && IsAdjacent(_i + n))
        {
            n++;
        }

        bool hasEquals = PeekKind(n) == SyntaxKind.EqualsToken && IsAdjacent(_i + n);
        SyntaxKind kind = (n, hasEquals) switch
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

    private bool IsAdjacent(int index)
    {
        return _tokens[index].LeadingTrivia.Length == 0 && _tokens[index - 1].TrailingTrivia.Length == 0;
    }

    private SyntaxToken EatComposite(SyntaxKind kind, int count)
    {
        if (count == 1)
        {
            return Eat();
        }

        SyntaxToken first = _tokens[_i];
        SyntaxToken last = _tokens[_i + count - 1];
        SyntaxToken token = new(kind, SyntaxFacts.GetText(kind), first.FullStart, first.LeadingTrivia, last.TrailingTrivia);
        _i += count;
        return token;
    }

    // ------------------------------------------------------------------ assignment / conditional / binary

    private ExpressionSyntax ParseAssignmentExpression()
    {
        if (!EnterNesting())
        {
            return new MissingExpressionSyntax(Missing(SyntaxKind.IdentifierToken));
        }

        try { return ParseAssignmentExpressionCore(); }
        finally { ExitNesting(); }
    }

    /// <summary>Set while parsing the operand after `?`: `c ? (x) : y => z` is a conditional, so a return-type annotation there only counts when a `:` follows the arrow.</summary>
    private bool _arrowNeedsColon;

    private ExpressionSyntax ParseAssignmentExpressionCore()
    {
        bool arrowNeedsColon = _arrowNeedsColon;
        _arrowNeedsColon = false;
        if (Kind == SyntaxKind.YieldKeyword)
        {
            return ParseYield();
        }

        ExpressionSyntax? arrow = TryParseArrowFunction(arrowNeedsColon);
        if (arrow != null)
        {
            return arrow;
        }

        ExpressionSyntax left = ParseConditionalExpression();
        (SyntaxKind opKind, int count) = PeekGreaterThanFamily();
        if (SyntaxFacts.IsAssignmentOperator(opKind))
        {
            if (!IsValidAssignmentTarget(left))
            {
                Report(Descriptors.InvalidAssignmentTarget, left.Span);
            }

            SyntaxToken op = EatComposite(opKind, count);
            ExpressionSyntax right = ParseAssignmentExpression();
            return new AssignmentExpressionSyntax(left, op, right);
        }
        return left;
    }

    private static bool IsValidAssignmentTarget(ExpressionSyntax e)
    {
        return e.Kind switch
        {
            SyntaxKind.IdentifierName or SyntaxKind.MemberAccessExpression or SyntaxKind.ElementAccessExpression
                or SyntaxKind.ArrayLiteralExpression or SyntaxKind.ObjectLiteralExpression or SyntaxKind.MissingExpression
                or SyntaxKind.ThisExpression => true,
            SyntaxKind.ParenthesizedExpression => IsValidAssignmentTarget(((ParenthesizedExpressionSyntax)e).Expression),
            SyntaxKind.NonNullExpression => IsValidAssignmentTarget(((NonNullExpressionSyntax)e).Expression),
            _ => false,
        };
    }

    private YieldExpressionSyntax ParseYield()
    {
        SyntaxToken kw = Eat();
        SyntaxToken? star = null;
        ExpressionSyntax? operand = null;
        if (Kind == SyntaxKind.AsteriskToken && !NewlineBeforeCurrent)
        {
            star = Eat();
        }

        if (star != null || (!NewlineBeforeCurrent && IsStartOfExpression()))
        {
            operand = ParseAssignmentExpression();
        }

        return new YieldExpressionSyntax(kw, star, operand);
    }

    private bool IsStartOfExpression()
    {
        return Kind switch
        {
            SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken or SyntaxKind.CommaToken
                or SyntaxKind.SemicolonToken or SyntaxKind.ColonToken or SyntaxKind.EndOfFileToken or SyntaxKind.EqualsToken
                or SyntaxKind.QuestionToken or SyntaxKind.EqualsGreaterThanToken => false,
            _ => true,
        };
    }

    private ExpressionSyntax ParseConditionalExpression()
    {
        ExpressionSyntax cond = ParseBinaryExpression(0);
        if (Kind != SyntaxKind.QuestionToken)
        {
            return cond;
        }

        SyntaxToken q = Eat();
        _arrowNeedsColon = true;
        ExpressionSyntax whenTrue = ParseAssignmentExpression();
        SyntaxToken colon = Expect(SyntaxKind.ColonToken);
        ExpressionSyntax whenFalse = ParseAssignmentExpression();
        return new ConditionalExpressionSyntax(cond, q, whenTrue, colon, whenFalse);
    }

    private ExpressionSyntax ParseBinaryExpression(int minPrecedence)
    {
        ExpressionSyntax left = ParseUnaryExpression();
        while (true)
        {
            (SyntaxKind opKind, int count) = PeekGreaterThanFamily();
            int prec = SyntaxFacts.GetBinaryPrecedence(opKind);
            if (opKind is SyntaxKind.AsKeyword or SyntaxKind.SatisfiesKeyword or SyntaxKind.InstanceOfKeyword && NewlineBeforeCurrent && opKind != SyntaxKind.InstanceOfKeyword)
            {
                break;
            }

            if (prec == 0 || prec <= minPrecedence)
            {
                break;
            }

            SyntaxToken op = EatComposite(opKind, count);
            switch (opKind)
            {
                case SyntaxKind.AsKeyword:
                    {
                        bool saved = _noNullableMarker;
                        _noNullableMarker = true;
                        TypeSyntax t = ParseType();
                        _noNullableMarker = saved;
                        left = new AsExpressionSyntax(left, op, t);
                        break;
                    }
                case SyntaxKind.SatisfiesKeyword:
                    {
                        bool saved = _noNullableMarker;
                        _noNullableMarker = true;
                        TypeSyntax t = ParseType();
                        _noNullableMarker = saved;
                        left = new SatisfiesExpressionSyntax(left, op, t);
                        break;
                    }
                case SyntaxKind.InstanceOfKeyword:
                    {
                        bool saved = _noNullableMarker;
                        _noNullableMarker = true;
                        TypeSyntax t = ParseType();
                        _noNullableMarker = saved;
                        left = new InstanceOfExpressionSyntax(left, op, t);
                        break;
                    }
                case SyntaxKind.AsteriskAsteriskToken:
                    {
                        // `-2 ** 2` is ambiguous between (-2) ** 2 and -(2 ** 2); like TypeScript it must be parenthesized
                        if (left is PrefixUnaryExpressionSyntax { OperatorToken.Kind: not (SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken) } or AwaitExpressionSyntax)
                        {
                            Report(Descriptors.UnaryBeforeExponent, op.Span);
                        }

                        // right associative
                        ExpressionSyntax right = ParseBinaryExpression(prec - 1);
                        left = new BinaryExpressionSyntax(left, op, right);
                        break;
                    }
                default:
                    {
                        ExpressionSyntax right = ParseBinaryExpression(prec);
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
                    SyntaxToken op = Eat();
                    return new PrefixUnaryExpressionSyntax(op, ParseUnaryExpression());
                }
            case SyntaxKind.AwaitKeyword:
                {
                    SyntaxToken kw = Eat();
                    return new AwaitExpressionSyntax(kw, ParseUnaryExpression());
                }
            case SyntaxKind.ThrowKeyword:
                {
                    SyntaxToken kw = Eat();
                    return new ThrowExpressionSyntax(kw, ParseAssignmentExpression());
                }
            case SyntaxKind.VoidKeyword:
                {
                    ReportAtCurrent(Descriptors.FeatureNotSupported, "The 'void' operator");
                    SyntaxToken kw = Eat();
                    return new PrefixUnaryExpressionSyntax(kw, ParseUnaryExpression());
                }
        }
        return ParsePostfixExpression();
    }

    private ExpressionSyntax ParsePostfixExpression()
    {
        ExpressionSyntax e = ParseLeftHandSideExpression();
        while (true)
        {
            if ((Kind == SyntaxKind.PlusPlusToken || Kind == SyntaxKind.MinusMinusToken) && !NewlineBeforeCurrent)
            {
                e = new PostfixUnaryExpressionSyntax(e, Eat());
            }
            else if (Kind == SyntaxKind.ExclamationToken && !NewlineBeforeCurrent)
            {
                e = new NonNullExpressionSyntax(e, Eat());
            }
            else
            {
                break;
            }
            // allow further member access after postfix '!' (x!.y)
            e = ParseCallSuffixes(e);
        }
        return e;
    }

    private ExpressionSyntax ParseLeftHandSideExpression()
    {
        ExpressionSyntax e = ParsePrimaryExpression();
        return ParseCallSuffixes(e);
    }

    private static bool CanFollowTypeArgumentsInExpression(SyntaxKind k)
    {
        return k is SyntaxKind.OpenParenToken or SyntaxKind.CloseParenToken
        or SyntaxKind.CloseBracketToken or SyntaxKind.ColonToken or SyntaxKind.SemicolonToken or SyntaxKind.CommaToken
        or SyntaxKind.DotToken or SyntaxKind.QuestionDotToken or SyntaxKind.QuestionToken or SyntaxKind.EqualsEqualsToken
        or SyntaxKind.ExclamationEqualsToken
        or SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken or SyntaxKind.QuestionQuestionToken
        or SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken or SyntaxKind.EqualsToken;
    }

    private ExpressionSyntax ParseCallSuffixes(ExpressionSyntax e)
    {
        while (true)
        {
            switch (Kind)
            {
                case SyntaxKind.DotToken:
                    {
                        SyntaxToken dot = Eat();
                        e = new MemberAccessExpressionSyntax(e, dot, ParseMemberName());
                        continue;
                    }
                case SyntaxKind.QuestionDotToken:
                    {
                        SyntaxToken qd = Eat();
                        if (Kind == SyntaxKind.OpenBracketToken)
                        {
                            SyntaxToken open = Eat();
                            SyntaxNode args = ParseSeparatedList(ParseArgument, () => Kind == SyntaxKind.CloseBracketToken);
                            SyntaxToken close = Expect(SyntaxKind.CloseBracketToken);
                            e = new ElementAccessExpressionSyntax(e, qd, open, args, close);
                        }
                        else if (Kind == SyntaxKind.OpenParenToken)
                        {
                            ReportAtCurrent(Descriptors.FeatureNotSupported, "Optional call '?.()'");
                            if (_speculating == 0)
                            {
                                _tokens[_i] = Absorb(qd, Current);
                            }

                            e = new InvocationExpressionSyntax(e, ParseArgumentList());
                        }
                        else
                        {
                            e = new MemberAccessExpressionSyntax(e, qd, ParseMemberName());
                        }

                        continue;
                    }
                case SyntaxKind.OpenBracketToken when !NewlineBeforeCurrent:
                    {
                        SyntaxToken open = Eat();
                        SyntaxNode args = ParseSeparatedList(ParseArgument, () => Kind == SyntaxKind.CloseBracketToken);
                        SyntaxToken close = Expect(SyntaxKind.CloseBracketToken);
                        e = new ElementAccessExpressionSyntax(e, null, open, args, close);
                        continue;
                    }
                case SyntaxKind.OpenParenToken when !NewlineBeforeCurrent || true:
                    {
                        e = new InvocationExpressionSyntax(e, ParseArgumentList());
                        continue;
                    }
                case SyntaxKind.LessThanToken when e is IdentifierNameSyntax or MemberAccessExpressionSyntax { Name: IdentifierNameSyntax }:
                    {
                        // possible generic invocation / generic name: f<T>(...)
                        TypeArgumentListSyntax? generic = Speculate(() =>
                        {
                            TypeArgumentListSyntax args = ParseTypeArgumentList();
                            // an `=` right after the `>` is the `>=` operator (`a < b >= c`), not an assignment to a generic name
                            return args.IsMissing || args.GreaterThanToken.IsMissing || (Kind == SyntaxKind.EqualsToken && IsAdjacent(_i)) ? null : CanFollowTypeArgumentsInExpression(Kind) ? args : null;
                        });
                        if (generic == null)
                        {
                            return e;
                        }

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
    private static SyntaxToken DetachToken(SyntaxToken t)
    {
        return new(t.Kind, t.Text, t.FullStart, t.LeadingTrivia, t.TrailingTrivia, t.Value, t.IsMissingToken);
    }

    private IdentifierNameSyntax ParseMemberName()
    {
        // any identifier-like token or keyword may follow a dot
        if (SyntaxFacts.IsIdentifierLike(Kind) || SyntaxFacts.IsKeyword(Kind))
        {
            SyntaxToken t = Eat();
            return new IdentifierNameSyntax(t.WithKind(SyntaxKind.IdentifierToken));
        }
        ReportExpected("identifier");
        return new IdentifierNameSyntax(Missing(SyntaxKind.IdentifierToken));
    }

    private ArgumentListSyntax ParseArgumentList()
    {
        SyntaxToken open = Expect(SyntaxKind.OpenParenToken);
        SyntaxNode args = ParseSeparatedList(ParseArgument, () => Kind == SyntaxKind.CloseParenToken);
        SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
        return new ArgumentListSyntax(open, args, close);
    }

    private ExpressionSyntax ParseArgument()
    {
        if (Kind == SyntaxKind.DotDotDotToken)
        {
            SyntaxToken dots = Eat();
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
                    SyntaxToken open = Eat();
                    ExpressionSyntax e = ParseExpression();
                    SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
                    return new ParenthesizedExpressionSyntax(open, e, close);
                }
            case SyntaxKind.TypeOfKeyword:
                {
                    // `typeof(T)` is the .NET type object, so members can follow it: typeof(T).Name
                    SyntaxToken kw = Eat();
                    SyntaxToken open = Expect(SyntaxKind.OpenParenToken);
                    TypeSyntax t = ParseType();
                    SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
                    return new TypeOfExpressionSyntax(kw, open, t, close);
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
                if (SyntaxFacts.IsIdentifierLike(Kind))
                {
                    return new IdentifierNameSyntax(Eat());
                }

                break;
        }
        // error recovery
        ReportAtCurrent(Descriptors.ExpectedExpression);
        return new MissingExpressionSyntax(Missing(SyntaxKind.IdentifierToken));
    }

    private TemplateExpressionSyntax ParseTemplateExpression()
    {
        SyntaxToken head = Eat();
        List<SyntaxElement> spans = [];
        while (true)
        {
            ExpressionSyntax expr = ParseExpression();
            SyntaxToken literal;
            if (Kind == SyntaxKind.TemplateMiddleToken)
            {
                literal = Eat();
            }
            else if (Kind == SyntaxKind.TemplateTailToken)
            {
                literal = Eat();
            }
            else
            {
                ReportExpected("}");
                literal = Missing(SyntaxKind.TemplateTailToken);
                spans.Add(new TemplateSpanSyntax(expr, literal));
                break;
            }
            spans.Add(new TemplateSpanSyntax(expr, literal));
            if (literal.Kind == SyntaxKind.TemplateTailToken)
            {
                break;
            }
        }
        return new TemplateExpressionSyntax(head, MakeList(spans));
    }

    private ArrayLiteralExpressionSyntax ParseArrayLiteral()
    {
        SyntaxToken open = Eat();
        SyntaxNode elements = ParseSeparatedList(ParseArgument, () => Kind == SyntaxKind.CloseBracketToken);
        SyntaxToken close = Expect(SyntaxKind.CloseBracketToken);
        return new ArrayLiteralExpressionSyntax(open, elements, close);
    }

    private ObjectLiteralExpressionSyntax ParseObjectLiteral()
    {
        SyntaxToken open = Eat();
        SyntaxNode members = ParseSeparatedList(ParseObjectLiteralMember, () => Kind == SyntaxKind.CloseBraceToken);
        SyntaxToken close = Expect(SyntaxKind.CloseBraceToken);
        return new ObjectLiteralExpressionSyntax(open, members, close);
    }

    private SyntaxNode ParseObjectLiteralMember()
    {
        if (Kind == SyntaxKind.DotDotDotToken)
        {
            SyntaxToken dots = Eat();
            return new SpreadAssignmentSyntax(dots, ParseAssignmentExpression());
        }
        SyntaxToken name;
        if (Kind is SyntaxKind.StringLiteralToken or SyntaxKind.NumericLiteralToken)
        {
            name = Eat();
        }
        else if (SyntaxFacts.IsIdentifierLike(Kind) || SyntaxFacts.IsKeyword(Kind))
        {
            name = Eat().WithKind(SyntaxKind.IdentifierToken);
        }
        else
        {
            ReportExpected("property name");
            SkipCurrent();
            name = Missing(SyntaxKind.IdentifierToken);
        }
        if (Kind == SyntaxKind.ColonToken)
        {
            SyntaxToken colon = Eat();
            return new PropertyAssignmentSyntax(name, colon, ParseAssignmentExpression());
        }
        if (Kind == SyntaxKind.OpenParenToken)
        {
            ReportAtCurrent(Descriptors.FeatureNotSupported, "Method shorthand in object literals");
            // skip the unsupported method shorthand so the text is preserved
            int depth = 0;
            while (Kind != SyntaxKind.EndOfFileToken)
            {
                if (Kind is SyntaxKind.OpenParenToken or SyntaxKind.OpenBraceToken)
                {
                    depth++;
                }
                else if (Kind is SyntaxKind.CloseParenToken or SyntaxKind.CloseBraceToken)
                {
                    depth--;
                }

                SkipCurrent();
                if (depth == 0 && (PeekKind(-1) == SyntaxKind.CloseBraceToken || (PeekKind(-1) == SyntaxKind.CloseParenToken && Kind != SyntaxKind.OpenBraceToken && Kind != SyntaxKind.ColonToken)))
                {
                    break;
                }
            }
        }
        return new ShorthandPropertyAssignmentSyntax(name);
    }

    private ExpressionSyntax ParseNewExpression()
    {
        SyntaxToken kw = Eat();
        // type without type-arguments ambiguity: after `new`, always a type
        TypeSyntax type = ParseNewTypeName();
        if (Kind == SyntaxKind.OpenBracketToken && PeekKind(1) != SyntaxKind.CloseBracketToken)
        {
            SyntaxToken open = Eat();
            ExpressionSyntax size = ParseExpression();
            SyntaxToken close = Expect(SyntaxKind.CloseBracketToken);
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

    private FunctionExpressionSyntax ParseFunctionExpression(SyntaxToken? asyncKw)
    {
        SyntaxToken fn = Expect(SyntaxKind.FunctionKeyword);
        SyntaxToken? star = TryEat(SyntaxKind.AsteriskToken);
        SyntaxToken? name = SyntaxFacts.IsIdentifierLike(Kind) ? Eat() : null;
        TypeParameterListSyntax? tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        ParameterListSyntax ps = ParseParameterList();
        TypeAnnotationSyntax? ret = ParseOptionalTypeAnnotation();
        BlockSyntax body = ParseBlock();
        return new FunctionExpressionSyntax(asyncKw, fn, star, name, tp, ps, ret, body);
    }

    // ------------------------------------------------------------------ arrow functions

    private ArrowFunctionSyntax? TryParseArrowFunction(bool arrowNeedsColon = false)
    {
        switch (Kind)
        {
            case SyntaxKind.AsyncKeyword when !NewlineBeforePeek(1):
                {
                    SyntaxKind k1 = PeekKind(1);
                    if (SyntaxFacts.IsIdentifierLike(k1) && PeekKind(2) == SyntaxKind.EqualsGreaterThanToken)
                    {
                        SyntaxToken asyncKw = Eat();
                        return ParseSimpleArrow(asyncKw);
                    }
                    return k1 is SyntaxKind.OpenParenToken or SyntaxKind.LessThanToken ? Speculate(() => ParseParenthesizedArrow(true)) : null;
                }
            case SyntaxKind.OpenParenToken:
            case SyntaxKind.LessThanToken:
                {
                    if (Kind == SyntaxKind.OpenParenToken)
                    {
                        int after = SkipBalanced(_i);
                        if (after >= _tokens.Count)
                        {
                            return null;
                        }

                        SyntaxKind ak = _tokens[after].Kind;
                        if (ak is not SyntaxKind.EqualsGreaterThanToken and not SyntaxKind.ColonToken)
                        {
                            return null;
                        }

                        if (ak == SyntaxKind.ColonToken && arrowNeedsColon)
                        {
                            return Speculate(() =>
                            {
                                ArrowFunctionSyntax? candidate = ParseParenthesizedArrow(false);
                                return candidate != null && Kind == SyntaxKind.ColonToken ? candidate : null;
                            });
                        }
                    }
                    return Speculate(() => ParseParenthesizedArrow(false));
                }
            default:
                if (SyntaxFacts.IsIdentifierLike(Kind) && PeekKind(1) == SyntaxKind.EqualsGreaterThanToken)
                {
                    return ParseSimpleArrow(null);
                }

                return null;
        }
    }

    private ArrowFunctionSyntax ParseSimpleArrow(SyntaxToken? asyncKw)
    {
        SyntaxToken id = Eat();
        SyntaxToken arrow = Expect(SyntaxKind.EqualsGreaterThanToken);
        SyntaxNode body = ParseArrowBody();
        return new ArrowFunctionSyntax(asyncKw, null, null, id, null, arrow, body);
    }

    private ArrowFunctionSyntax? ParseParenthesizedArrow(bool isAsync)
    {
        SyntaxToken? asyncKw = isAsync ? Eat() : null;
        TypeParameterListSyntax? tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        if (Kind != SyntaxKind.OpenParenToken)
        {
            return null;
        }

        ParameterListSyntax ps = ParseParameterList();
        TypeAnnotationSyntax? ret = null;
        if (Kind == SyntaxKind.ColonToken)
        {
            bool saved = _noNullableMarker;
            ret = ParseOptionalTypeAnnotation();
            _noNullableMarker = saved;
        }
        if (Kind != SyntaxKind.EqualsGreaterThanToken)
        {
            return null;
        }

        SyntaxToken arrow = Eat();
        SyntaxNode body = ParseArrowBody();
        return new ArrowFunctionSyntax(asyncKw, tp, ps, null, ret, arrow, body);
    }

    private SyntaxNode ParseArrowBody()
    {
        return Kind == SyntaxKind.OpenBraceToken ? ParseBlock() : ParseAssignmentExpression();
    }
}
