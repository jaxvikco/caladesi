using Caladesi.Compiler.Diagnostics;

namespace Caladesi.Compiler.Syntax;

public sealed partial class Parser
{
    private StatementSyntax ParseStatement(bool _1 = false)
    {
        if (!EnterNesting())
        {
            return new EmptyStatementSyntax(Missing(SyntaxKind.SemicolonToken));
        }

        try { return ParseStatementCore(); }
        finally { ExitNesting(); }
    }

    private StatementSyntax ParseStatementCore()
    {
        switch (Kind)
        {
            case SyntaxKind.OpenBraceToken: return ParseBlock();
            case SyntaxKind.SemicolonToken: return new EmptyStatementSyntax(Eat());
            case SyntaxKind.IfKeyword: return ParseIf();
            case SyntaxKind.ForKeyword: return ParseFor();
            case SyntaxKind.WhileKeyword: return ParseWhile();
            case SyntaxKind.DoKeyword: return ParseDo();
            case SyntaxKind.ReturnKeyword: return ParseReturn();
            case SyntaxKind.BreakKeyword: return ParseBreak();
            case SyntaxKind.ContinueKeyword: return ParseContinue();
            case SyntaxKind.ThrowKeyword: return ParseThrow();
            case SyntaxKind.TryKeyword: return ParseTry();
            case SyntaxKind.SwitchKeyword: return ParseSwitch();
            case SyntaxKind.ImportKeyword when PeekKind(1) is not SyntaxKind.OpenParenToken and not SyntaxKind.DotToken:
                return ParseImport();
            case SyntaxKind.AtToken:
                return ParseDeclarationWithModifiers();
        }

        if (IsStartOfDeclaration())
        {
            return ParseDeclarationWithModifiers();
        }

        if (SyntaxFacts.IsIdentifierLike(Kind) && PeekKind(1) == SyntaxKind.ColonToken)
        {
            SyntaxToken label = Eat();
            SyntaxToken colon = Eat();
            return new LabeledStatementSyntax(label, colon, ParseStatement());
        }

        return ParseExpressionStatement();
    }

    private ExpressionStatementSyntax ParseExpressionStatement()
    {
        int before = _i;
        ExpressionSyntax e = ParseExpression();
        if (_i == before && e is MissingExpressionSyntax)
        {
            // Nothing could be parsed: skip a token to guarantee progress.
            SkipCurrent();
            return new ExpressionStatementSyntax(e, null);
        }
        return new ExpressionStatementSyntax(e, ParseSemicolon());
    }

    public BlockSyntax ParseBlock()
    {
        SyntaxToken open = Expect(SyntaxKind.OpenBraceToken);
        List<SyntaxElement> statements = [];
        while (Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            int before = _i;
            statements.Add(ParseStatement());
            if (_i == before)
            {
                SkipCurrent();
            }
        }
        SyntaxToken close = Expect(SyntaxKind.CloseBraceToken);
        return new BlockSyntax(open, MakeList(statements), close);
    }

    private IfStatementSyntax ParseIf()
    {
        SyntaxToken kw = Eat();
        SyntaxToken open = Expect(SyntaxKind.OpenParenToken);
        ExpressionSyntax cond = ParseExpression();
        SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
        StatementSyntax then = ParseStatement();
        SyntaxToken? elseKw = null;
        StatementSyntax? els = null;
        if (Kind == SyntaxKind.ElseKeyword)
        {
            elseKw = Eat();
            els = ParseStatement();
        }
        return new IfStatementSyntax(kw, open, cond, close, then, elseKw, els);
    }

    private WhileStatementSyntax ParseWhile()
    {
        SyntaxToken kw = Eat();
        SyntaxToken open = Expect(SyntaxKind.OpenParenToken);
        ExpressionSyntax cond = ParseExpression();
        SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
        return new WhileStatementSyntax(kw, open, cond, close, ParseStatement());
    }

    private DoStatementSyntax ParseDo()
    {
        SyntaxToken kw = Eat();
        StatementSyntax body = ParseStatement();
        SyntaxToken whileKw = Expect(SyntaxKind.WhileKeyword);
        SyntaxToken open = Expect(SyntaxKind.OpenParenToken);
        ExpressionSyntax cond = ParseExpression();
        SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
        SyntaxToken? semi = TryEat(SyntaxKind.SemicolonToken);
        return new DoStatementSyntax(kw, body, whileKw, open, cond, close, semi);
    }

    private StatementSyntax ParseFor()
    {
        SyntaxToken kw = Eat();
        SyntaxToken? awaitKw = TryEat(SyntaxKind.AwaitKeyword);
        SyntaxToken open = Expect(SyntaxKind.OpenParenToken);

        SyntaxNode? init = null;
        if (Kind == SyntaxKind.SemicolonToken)
        {
            // empty initializer
        }
        else
        {
            init = Kind is SyntaxKind.LetKeyword or SyntaxKind.ConstKeyword
                 || (Kind == SyntaxKind.UsingKeyword && SyntaxFacts.IsIdentifierLike(PeekKind(1)))
                ? ParseVariableDeclarationList(forHeader: true)
                : ParseExpression();
        }

        if (Kind == SyntaxKind.OfKeyword && init != null)
        {
            SyntaxToken of = Eat();
            ExpressionSyntax expr = ParseAssignmentExpression();
            SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
            return new ForOfStatementSyntax(kw, awaitKw, open, init, of, expr, close, ParseStatement());
        }
        if (Kind == SyntaxKind.InKeyword && init != null)
        {
            Report(Descriptors.ForInNotSupported, Current.Span);
            if (awaitKw != null)
            {
                open = Absorb(awaitKw, open);
            }

            SyntaxToken inKw = Eat();
            ExpressionSyntax expr = ParseExpression();
            SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
            return new ForInStatementSyntax(kw, open, init, inKw, expr, close, ParseStatement());
        }

        if (awaitKw != null)
        {
            Report(Descriptors.ExpectedToken, awaitKw.Span, "of");
            open = Absorb(awaitKw, open);
        }
        SyntaxToken semi1 = Expect(SyntaxKind.SemicolonToken);
        ExpressionSyntax? cond = Kind is not SyntaxKind.SemicolonToken and not SyntaxKind.CloseParenToken ? ParseExpression() : null;
        SyntaxToken semi2 = Expect(SyntaxKind.SemicolonToken);
        ExpressionSyntax? incr = Kind != SyntaxKind.CloseParenToken ? ParseExpression() : null;
        SyntaxToken closeParen = Expect(SyntaxKind.CloseParenToken);
        return new ForStatementSyntax(kw, open, init, semi1, cond, semi2, incr, closeParen, ParseStatement());
    }

    private ReturnStatementSyntax ParseReturn()
    {
        SyntaxToken kw = Eat();
        ExpressionSyntax? e = null;
        if (!NewlineBeforeCurrent && Kind is not (SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
        {
            e = ParseExpression();
        }

        return new ReturnStatementSyntax(kw, e, ParseSemicolon());
    }

    private BreakStatementSyntax ParseBreak()
    {
        SyntaxToken kw = Eat();
        SyntaxToken? label = null;
        if (!NewlineBeforeCurrent && SyntaxFacts.IsIdentifierLike(Kind))
        {
            label = Eat();
        }

        return new BreakStatementSyntax(kw, label, ParseSemicolon());
    }

    private ContinueStatementSyntax ParseContinue()
    {
        SyntaxToken kw = Eat();
        SyntaxToken? label = null;
        if (!NewlineBeforeCurrent && SyntaxFacts.IsIdentifierLike(Kind))
        {
            label = Eat();
        }

        return new ContinueStatementSyntax(kw, label, ParseSemicolon());
    }

    private ThrowStatementSyntax ParseThrow()
    {
        SyntaxToken kw = Eat();
        ExpressionSyntax? e = null;
        if (!NewlineBeforeCurrent && Kind is not (SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
        {
            e = ParseExpression();
        }

        return new ThrowStatementSyntax(kw, e, ParseSemicolon());
    }

    private TryStatementSyntax ParseTry()
    {
        SyntaxToken kw = Eat();
        BlockSyntax block = ParseBlock();
        List<SyntaxElement> catches = [];
        while (Kind == SyntaxKind.CatchKeyword)
        {
            SyntaxToken catchKw = Eat();
            SyntaxToken? open = null, id = null, close = null;
            TypeAnnotationSyntax? type = null;
            if (Kind == SyntaxKind.OpenParenToken)
            {
                open = Eat();
                id = ParseIdentifierToken();
                type = ParseOptionalTypeAnnotation();
                close = Expect(SyntaxKind.CloseParenToken);
            }
            catches.Add(new CatchClauseSyntax(catchKw, open, id, type, close, ParseBlock()));
        }
        FinallyClauseSyntax? fin = null;
        if (Kind == SyntaxKind.FinallyKeyword)
        {
            SyntaxToken fkw = Eat();
            fin = new FinallyClauseSyntax(fkw, ParseBlock());
        }
        if (catches.Count == 0 && fin == null)
        {
            ReportExpected("catch' or 'finally");
        }

        return new TryStatementSyntax(kw, block, MakeList(catches), fin);
    }

    private SwitchStatementSyntax ParseSwitch()
    {
        SyntaxToken kw = Eat();
        SyntaxToken open = Expect(SyntaxKind.OpenParenToken);
        ExpressionSyntax expr = ParseExpression();
        SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
        SyntaxToken openBrace = Expect(SyntaxKind.OpenBraceToken);
        List<SyntaxElement> sections = [];
        while (Kind is SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword)
        {
            List<SyntaxElement> labels = [];
            while (Kind is SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword)
            {
                if (Kind == SyntaxKind.CaseKeyword)
                {
                    SyntaxToken ck = Eat();
                    ExpressionSyntax v = ParseExpression();
                    SyntaxToken colon = Expect(SyntaxKind.ColonToken);
                    labels.Add(new SwitchLabelSyntax(SyntaxKind.CaseSwitchLabel, ck, v, colon));
                }
                else
                {
                    SyntaxToken dk = Eat();
                    SyntaxToken colon = Expect(SyntaxKind.ColonToken);
                    labels.Add(new SwitchLabelSyntax(SyntaxKind.DefaultSwitchLabel, dk, null, colon));
                }
            }
            List<SyntaxElement> stmts = [];
            while (Kind is not (SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword or SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
            {
                int before = _i;
                stmts.Add(ParseStatement());
                if (_i == before)
                {
                    SkipCurrent();
                }
            }
            sections.Add(new SwitchSectionSyntax(MakeList(labels), MakeList(stmts)));
        }
        // Stray tokens inside the switch body
        while (Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            ReportAtCurrent(Descriptors.UnexpectedToken, Current.Text);
            SkipCurrent();
        }
        SyntaxToken closeBrace = Expect(SyntaxKind.CloseBraceToken);
        return new SwitchStatementSyntax(kw, open, expr, close, openBrace, MakeList(sections), closeBrace);
    }

    // ------------------------------------------------------------------ variables

    private VariableDeclarationListSyntax ParseVariableDeclarationList(bool forHeader = false)
    {
        SyntaxToken? awaitKw = null;
        if (Kind == SyntaxKind.AwaitKeyword && PeekKind(1) == SyntaxKind.UsingKeyword)
        {
            awaitKw = Eat();
        }

        SyntaxToken kw = Eat(); // let / const / using
        List<SyntaxElement> items = [];
        while (true)
        {
            items.Add(ParseVariableDeclarator(forHeader));
            if (Kind == SyntaxKind.CommaToken) { items.Add(Eat()); continue; }
            break;
        }
        return new VariableDeclarationListSyntax(awaitKw, kw, MakeSepList(items));
    }

    private VariableDeclaratorSyntax ParseVariableDeclarator(bool _1)
    {
        SyntaxToken? id = null;
        BindingPatternSyntax? pattern = null;
        if (Kind is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken)
        {
            pattern = ParseBindingPattern();
        }
        else
        {
            id = ParseIdentifierToken();
        }

        TypeAnnotationSyntax? type = ParseOptionalTypeAnnotation();
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            SyntaxToken eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        return new VariableDeclaratorSyntax(id, pattern, type, init);
    }

    private BindingPatternSyntax ParseBindingPattern()
    {
        if (Kind == SyntaxKind.OpenBraceToken)
        {
            SyntaxToken open = Eat();
            SyntaxNode elems = ParseSeparatedList(() => ParseBindingElement(true), () => Kind == SyntaxKind.CloseBraceToken);
            SyntaxToken close = Expect(SyntaxKind.CloseBraceToken);
            return new ObjectBindingPatternSyntax(open, elems, close);
        }
        else
        {
            SyntaxToken open = Eat();
            SyntaxNode elems = ParseSeparatedList(() => ParseBindingElement(false), () => Kind == SyntaxKind.CloseBracketToken, null);
            SyntaxToken close = Expect(SyntaxKind.CloseBracketToken);
            return new ArrayBindingPatternSyntax(open, elems, close);
        }
    }

    private BindingElementSyntax ParseBindingElement(bool isObject)
    {
        if (!isObject && Kind == SyntaxKind.CommaToken)
        {
            return new BindingElementSyntax(null, null, null, null, null, null); // omitted
        }

        SyntaxToken? dots = TryEat(SyntaxKind.DotDotDotToken);
        SyntaxToken? propName = null, colon = null, id = null;
        BindingPatternSyntax? nested = null;
        if (isObject && dots == null)
        {
            if (SyntaxFacts.IsIdentifierLike(Kind) && PeekKind(1) == SyntaxKind.ColonToken)
            {
                propName = Eat();
                colon = Eat();
                if (Kind is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken)
                {
                    nested = ParseBindingPattern();
                }
                else
                {
                    id = ParseIdentifierToken();
                }
            }
            else
            {
                id = ParseIdentifierToken();
            }
        }
        else
        {
            if (Kind is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken)
            {
                nested = ParseBindingPattern();
            }
            else
            {
                id = ParseIdentifierToken();
            }
        }
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            SyntaxToken eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        return new BindingElementSyntax(dots, propName, colon, id, nested, init);
    }

    private TypeAnnotationSyntax? ParseOptionalTypeAnnotation()
    {
        if (Kind != SyntaxKind.ColonToken)
        {
            return null;
        }

        SyntaxToken colon = Eat();
        return new TypeAnnotationSyntax(colon, ParseType());
    }

    // ------------------------------------------------------------------ imports

    private ImportDeclarationSyntax ParseImport()
    {
        SyntaxToken kw = Eat();
        SyntaxToken? typeKw = null;
        SyntaxNode? clause = null;
        SyntaxToken? fromKw = null;
        if (Kind == SyntaxKind.TypeKeyword && (PeekKind(1) == SyntaxKind.OpenBraceToken || PeekKind(1) == SyntaxKind.AsteriskToken))
        {
            typeKw = Eat();
        }

        if (Kind == SyntaxKind.OpenBraceToken)
        {
            SyntaxToken open = Eat();
            SyntaxNode specs = ParseSeparatedList(ParseImportSpecifier, () => Kind == SyntaxKind.CloseBraceToken);
            SyntaxToken close = Expect(SyntaxKind.CloseBraceToken);
            clause = new NamedImportsSyntax(open, specs, close);
            fromKw = Expect(SyntaxKind.FromKeyword);
        }
        else if (Kind == SyntaxKind.AsteriskToken)
        {
            SyntaxToken star = Eat();
            SyntaxToken asKw = Expect(SyntaxKind.AsKeyword);
            SyntaxToken alias = ParseIdentifierToken();
            clause = new NamespaceImportSyntax(star, asKw, alias);
            fromKw = Expect(SyntaxKind.FromKeyword);
        }
        else if (Kind == SyntaxKind.DefaultKeyword || (SyntaxFacts.IsIdentifierLike(Kind) && PeekKind(1) is SyntaxKind.FromKeyword or SyntaxKind.CommaToken))
        {
            ReportAtCurrent(Descriptors.FeatureNotSupported, "Default imports");
            SkipCurrent();
            fromKw = Expect(SyntaxKind.FromKeyword);
        }
        SyntaxToken spec;
        if (Kind == SyntaxKind.StringLiteralToken)
        {
            spec = Eat();
        }
        else
        {
            ReportExpected("module specifier string");
            spec = Missing(SyntaxKind.StringLiteralToken);
        }
        return new ImportDeclarationSyntax(kw, typeKw, clause, fromKw, spec, ParseSemicolon());
    }

    private ImportSpecifierSyntax ParseImportSpecifier()
    {
        SyntaxToken? typeKw = null;
        if (Kind == SyntaxKind.TypeKeyword && SyntaxFacts.IsIdentifierLike(PeekKind(1)) && PeekKind(1) != SyntaxKind.AsKeyword)
        {
            typeKw = Eat();
        }

        SyntaxToken name = ParseIdentifierToken();
        SyntaxToken? asKw = null, alias = null;
        if (Kind == SyntaxKind.AsKeyword)
        {
            asKw = Eat();
            alias = ParseIdentifierToken();
        }
        return new ImportSpecifierSyntax(typeKw, name, asKw, alias);
    }
}
