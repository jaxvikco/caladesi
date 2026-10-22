using Caladesi.Compiler.Diagnostics;

namespace Caladesi.Compiler.Syntax;

public sealed partial class Parser
{
    private TypeSyntax ParseType()
    {
        if (!EnterNesting())
        {
            return new MissingTypeSyntax(Missing(SyntaxKind.IdentifierToken));
        }

        try { return IsStartOfFunctionType() ? ParseFunctionType() : ParseUnionType(); }
        finally { ExitNesting(); }
    }

    private bool IsStartOfFunctionType()
    {
        if (Kind == SyntaxKind.LessThanToken)
        {
            return true;
        }

        if (Kind != SyntaxKind.OpenParenToken)
        {
            return false;
        }
        // ( ) =>   |  ( ident :|,|?|=|) ... ) =>   | ( ...x
        SyntaxKind k1 = PeekKind(1);
        if (k1 == SyntaxKind.CloseParenToken)
        {
            return PeekKind(2) == SyntaxKind.EqualsGreaterThanToken;
        }

        if (k1 == SyntaxKind.DotDotDotToken)
        {
            return true;
        }

        if (SyntaxFacts.IsIdentifierLike(k1) || k1 == SyntaxKind.ThisKeyword)
        {
            SyntaxKind k2 = PeekKind(2);
            if (k2 is SyntaxKind.ColonToken or SyntaxKind.CommaToken or SyntaxKind.QuestionToken or SyntaxKind.EqualsToken)
            {
                return true;
            }

            if (k2 == SyntaxKind.CloseParenToken && PeekKind(3) == SyntaxKind.EqualsGreaterThanToken)
            {
                return true;
            }
        }
        if (k1 is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken)
        {
            // destructured parameter: scan to matching ')' and look for '=>'
            int j = SkipBalanced(_i);
            return j < _tokens.Count && _tokens[j].Kind == SyntaxKind.EqualsGreaterThanToken;
        }
        return false;
    }

    /// <summary>Given the index of an opening bracket, returns the index just past its matching close.</summary>
    private int SkipBalanced(int index)
    {
        int depth = 0;
        for (int j = index; j < _tokens.Count; j++)
        {
            switch (_tokens[j].Kind)
            {
                case SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenBraceToken
                    or SyntaxKind.TemplateHeadToken:
                    depth++;
                    break;
                case SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken
                    or SyntaxKind.TemplateTailToken:
                    depth--;
                    if (depth == 0)
                    {
                        return j + 1;
                    }

                    break;
                case SyntaxKind.EndOfFileToken:
                    return j;
            }
        }
        return _tokens.Count;
    }

    private FunctionTypeSyntax ParseFunctionType()
    {
        TypeParameterListSyntax? typeParams = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        ParameterListSyntax parameters = ParseParameterList();
        SyntaxToken arrow = Expect(SyntaxKind.EqualsGreaterThanToken);
        TypeSyntax ret = ParseType();
        return new FunctionTypeSyntax(typeParams, parameters, arrow, ret);
    }

    private TypeSyntax ParseUnionType()
    {
        SyntaxToken? leading = TryEat(SyntaxKind.BarToken);
        TypeSyntax first = ParseIntersectionType();
        if (Kind != SyntaxKind.BarToken && leading == null)
        {
            return first;
        }

        List<SyntaxElement> items = [first];
        while (Kind == SyntaxKind.BarToken)
        {
            items.Add(Eat());
            items.Add(ParseIntersectionType());
        }
        return new UnionTypeSyntax(leading, MakeSepList(items));
    }

    private TypeSyntax ParseIntersectionType()
    {
        TypeSyntax first = ParsePostfixType();
        if (Kind != SyntaxKind.AmpersandToken)
        {
            return first;
        }

        List<SyntaxElement> items = [first];
        while (Kind == SyntaxKind.AmpersandToken)
        {
            items.Add(Eat());
            items.Add(ParsePostfixType());
        }
        return new IntersectionTypeSyntax(MakeSepList(items));
    }

    private static bool CanFollowNullableMarker(SyntaxKind k)
    {
        return k is SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken
        or SyntaxKind.CloseBraceToken or SyntaxKind.GreaterThanToken or SyntaxKind.CommaToken or SyntaxKind.SemicolonToken
        or SyntaxKind.EqualsToken or SyntaxKind.BarToken or SyntaxKind.AmpersandToken or SyntaxKind.OpenBracketToken
        or SyntaxKind.OpenBraceToken or SyntaxKind.EqualsGreaterThanToken or SyntaxKind.EndOfFileToken;
    }

    private TypeSyntax ParsePostfixType()
    {
        if (Kind == SyntaxKind.ReadonlyKeyword)
        {
            SyntaxToken ro = Eat();
            return new ReadonlyArrayTypeSyntax(ro, ParsePostfixType());
        }
        TypeSyntax type = ParsePrimaryType();
        while (true)
        {
            if (Kind == SyntaxKind.OpenBracketToken && PeekKind(1) == SyntaxKind.CloseBracketToken && !NewlineBeforeCurrent)
            {
                SyntaxToken open = Eat();
                SyntaxToken close = Eat();
                type = new ArrayTypeSyntax(type, open, close);
            }
            else if (Kind == SyntaxKind.QuestionToken && !_noNullableMarker && CanFollowNullableMarker(PeekKind(1)))
            {
                type = new NullableTypeSyntax(type, Eat());
            }
            else
            {
                break;
            }
        }
        return type;
    }

    private bool _noNullableMarker;

    private TypeSyntax ParsePrimaryType()
    {
        switch (Kind)
        {
            case SyntaxKind.OpenParenToken:
                {
                    SyntaxToken open = Eat();
                    TypeSyntax t = ParseType();
                    SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
                    return new ParenthesizedTypeSyntax(open, t, close);
                }
            case SyntaxKind.OpenBracketToken: return ParseTupleType();
            case SyntaxKind.OpenBraceToken: return ParseObjectType();
            case SyntaxKind.StringLiteralToken:
                return new LiteralTypeSyntax(new LiteralExpressionSyntax(SyntaxKind.StringLiteralExpression, Eat()));
            case SyntaxKind.NumericLiteralToken:
                return new LiteralTypeSyntax(new LiteralExpressionSyntax(SyntaxKind.NumericLiteralExpression, Eat()));
            case SyntaxKind.TrueKeyword:
                return new LiteralTypeSyntax(new LiteralExpressionSyntax(SyntaxKind.TrueLiteralExpression, Eat()));
            case SyntaxKind.FalseKeyword:
                return new LiteralTypeSyntax(new LiteralExpressionSyntax(SyntaxKind.FalseLiteralExpression, Eat()));
            case SyntaxKind.NullKeyword:
                return new LiteralTypeSyntax(new LiteralExpressionSyntax(SyntaxKind.NullLiteralExpression, Eat()));
            case SyntaxKind.MinusToken when PeekKind(1) == SyntaxKind.NumericLiteralToken:
                {
                    SyntaxToken minus = Eat();
                    SyntaxToken num = Eat();
                    return new LiteralTypeSyntax(new PrefixUnaryExpressionSyntax(minus, new LiteralExpressionSyntax(SyntaxKind.NumericLiteralExpression, num)));
                }
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.ThisKeyword:
                return new IdentifierNameSyntax(Eat());
            case SyntaxKind.NewKeyword when PeekKind(1) == SyntaxKind.OpenParenToken:
                {
                    SyntaxToken n = Eat();
                    SyntaxToken o = Eat();
                    SyntaxToken c = Expect(SyntaxKind.CloseParenToken);
                    return new NewConstraintTypeSyntax(n, o, c);
                }
            case SyntaxKind.ClassKeyword:
                return new SpecialConstraintTypeSyntax(Eat());
            case SyntaxKind.StructKeyword:
                // `struct` as a constraint keyword (only in constraint position); as a type name it is not valid.
                return new SpecialConstraintTypeSyntax(Eat());
            case SyntaxKind.TypeOfKeyword:
                {
                    // typeof X in type position is not supported; parse the name for recovery
                    ReportAtCurrent(Descriptors.FeatureNotSupported, "'typeof' types");
                    _ = Eat();
                    return ParsePrimaryType();
                }
        }
        if (SyntaxFacts.IsIdentifierLike(Kind))
        {
            return ParseTypeName();
        }

        ReportExpected("type");
        return new MissingTypeSyntax(Missing(SyntaxKind.IdentifierToken));
    }

    private TupleTypeSyntax ParseTupleType()
    {
        SyntaxToken open = Eat();
        List<SyntaxElement> items = [];
        while (Kind is not SyntaxKind.CloseBracketToken and not SyntaxKind.EndOfFileToken)
        {
            int before = _i;
            SyntaxToken? name = null, colon = null;
            if (SyntaxFacts.IsIdentifierLike(Kind) && PeekKind(1) == SyntaxKind.ColonToken)
            {
                name = Eat();
                colon = Eat();
            }
            items.Add(new TupleElementSyntax(name, colon, ParseType()));
            if (Kind == SyntaxKind.CommaToken)
            {
                items.Add(Eat());
            }
            else
            {
                break;
            }

            if (_i == before)
            {
                break;
            }
        }
        SyntaxToken close = Expect(SyntaxKind.CloseBracketToken);
        return new TupleTypeSyntax(open, MakeSepList(items), close);
    }

    private ObjectTypeSyntax ParseObjectType()
    {
        SyntaxToken open = Eat();
        SyntaxNode members = ParseMembers(isInterfaceOrObjectType: true);
        SyntaxToken close = Expect(SyntaxKind.CloseBraceToken);
        return new ObjectTypeSyntax(open, members, close);
    }

    /// <summary>Parses a (possibly qualified, possibly generic) type name.</summary>
    private NameSyntax ParseTypeName()
    {
        NameSyntax name = ParseSimpleTypeName();
        while (Kind == SyntaxKind.DotToken && SyntaxFacts.IsIdentifierLike(PeekKind(1)))
        {
            SyntaxToken dot = Eat();
            SimpleNameSyntax right = ParseSimpleTypeName();
            name = new QualifiedNameSyntax(name, dot, right);
        }
        return name;
    }

    private SimpleNameSyntax ParseSimpleTypeName()
    {
        SyntaxToken id = ParseIdentifierToken();
        return Kind == SyntaxKind.LessThanToken ? new GenericNameSyntax(id, ParseTypeArgumentList()) : new IdentifierNameSyntax(id);
    }

    private TypeArgumentListSyntax ParseTypeArgumentList()
    {
        SyntaxToken lt = Expect(SyntaxKind.LessThanToken);
        bool saved = _noNullableMarker;
        _noNullableMarker = false;
        SyntaxNode args = ParseSeparatedList(ParseType, () => Kind == SyntaxKind.GreaterThanToken);
        _noNullableMarker = saved;
        SyntaxToken gt = Expect(SyntaxKind.GreaterThanToken);
        return new TypeArgumentListSyntax(lt, args, gt);
    }

    private TypeParameterListSyntax ParseTypeParameterList()
    {
        SyntaxToken lt = Expect(SyntaxKind.LessThanToken);
        SyntaxNode ps = ParseSeparatedList(ParseTypeParameter, () => Kind == SyntaxKind.GreaterThanToken);
        SyntaxToken gt = Expect(SyntaxKind.GreaterThanToken);
        return new TypeParameterListSyntax(lt, ps, gt);
    }

    private TypeParameterSyntax ParseTypeParameter()
    {
        SyntaxToken? variance = null;
        if ((Kind == SyntaxKind.InKeyword || Kind == SyntaxKind.OutKeyword) && SyntaxFacts.IsIdentifierLike(PeekKind(1)))
        {
            variance = Eat();
        }
        // `in out T` is not valid TS; a single variance modifier is accepted.
        SyntaxToken id = ParseIdentifierToken();
        SyntaxToken? ext = null;
        TypeSyntax? constraint = null;
        if (Kind == SyntaxKind.ExtendsKeyword)
        {
            ext = Eat();
            constraint = ParseConstraintType();
        }
        EqualsValueClauseSyntax? def = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            SyntaxToken eq = Eat();
            def = new EqualsValueClauseSyntax(eq, ParseType());
        }
        return new TypeParameterSyntax(variance, id, ext, constraint, def);
    }

    /// <summary>Constraint: a type, possibly an intersection that includes class / struct / new().</summary>
    private TypeSyntax ParseConstraintType()
    {
        return ParseIntersectionType();
    }
}
