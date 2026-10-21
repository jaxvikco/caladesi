using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Syntax;

public sealed partial class Parser
{
    private StatementSyntax ParseStatement(bool topLevel = false)
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
            case SyntaxKind.ImportKeyword when PeekKind(1) != SyntaxKind.OpenParenToken && PeekKind(1) != SyntaxKind.DotToken:
                return ParseImport();
            case SyntaxKind.AtToken:
                return ParseDeclarationWithModifiers();
        }

        if (IsStartOfDeclaration()) return ParseDeclarationWithModifiers();

        if (SyntaxFacts.IsIdentifierLike(Kind) && PeekKind(1) == SyntaxKind.ColonToken)
        {
            var label = Eat();
            var colon = Eat();
            return new LabeledStatementSyntax(label, colon, ParseStatement());
        }

        return ParseExpressionStatement();
    }

    private StatementSyntax ParseExpressionStatement()
    {
        var before = _i;
        var e = ParseExpression();
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
        var open = Expect(SyntaxKind.OpenBraceToken);
        var statements = new List<SyntaxElement>();
        while (Kind != SyntaxKind.CloseBraceToken && Kind != SyntaxKind.EndOfFileToken)
        {
            var before = _i;
            statements.Add(ParseStatement());
            if (_i == before) SkipCurrent();
        }
        var close = Expect(SyntaxKind.CloseBraceToken);
        return new BlockSyntax(open, MakeList(statements), close);
    }

    private StatementSyntax ParseIf()
    {
        var kw = Eat();
        var open = Expect(SyntaxKind.OpenParenToken);
        var cond = ParseExpression();
        var close = Expect(SyntaxKind.CloseParenToken);
        var then = ParseStatement();
        SyntaxToken? elseKw = null;
        StatementSyntax? els = null;
        if (Kind == SyntaxKind.ElseKeyword)
        {
            elseKw = Eat();
            els = ParseStatement();
        }
        return new IfStatementSyntax(kw, open, cond, close, then, elseKw, els);
    }

    private StatementSyntax ParseWhile()
    {
        var kw = Eat();
        var open = Expect(SyntaxKind.OpenParenToken);
        var cond = ParseExpression();
        var close = Expect(SyntaxKind.CloseParenToken);
        return new WhileStatementSyntax(kw, open, cond, close, ParseStatement());
    }

    private StatementSyntax ParseDo()
    {
        var kw = Eat();
        var body = ParseStatement();
        var whileKw = Expect(SyntaxKind.WhileKeyword);
        var open = Expect(SyntaxKind.OpenParenToken);
        var cond = ParseExpression();
        var close = Expect(SyntaxKind.CloseParenToken);
        var semi = TryEat(SyntaxKind.SemicolonToken);
        return new DoStatementSyntax(kw, body, whileKw, open, cond, close, semi);
    }

    private StatementSyntax ParseFor()
    {
        var kw = Eat();
        var awaitKw = TryEat(SyntaxKind.AwaitKeyword);
        var open = Expect(SyntaxKind.OpenParenToken);

        SyntaxNode? init = null;
        if (Kind == SyntaxKind.SemicolonToken)
        {
            // empty initializer
        }
        else if (Kind is SyntaxKind.LetKeyword or SyntaxKind.ConstKeyword or SyntaxKind.VarKeyword
                 || (Kind == SyntaxKind.UsingKeyword && SyntaxFacts.IsIdentifierLike(PeekKind(1))))
        {
            init = ParseVariableDeclarationList(forHeader: true);
        }
        else
        {
            init = ParseExpression();
        }

        if (Kind == SyntaxKind.OfKeyword && init != null)
        {
            var of = Eat();
            var expr = ParseAssignmentExpression();
            var close = Expect(SyntaxKind.CloseParenToken);
            return new ForOfStatementSyntax(kw, awaitKw, open, init, of, expr, close, ParseStatement());
        }
        if (Kind == SyntaxKind.InKeyword && init != null)
        {
            Report(Descriptors.ForInNotSupported, Current.Span);
            if (awaitKw != null) open = Absorb(awaitKw, open);
            var inKw = Eat();
            var expr = ParseExpression();
            var close = Expect(SyntaxKind.CloseParenToken);
            return new ForInStatementSyntax(kw, open, init, inKw, expr, close, ParseStatement());
        }

        if (awaitKw != null)
        {
            Report(Descriptors.ExpectedToken, awaitKw.Span, "of");
            open = Absorb(awaitKw, open);
        }
        var semi1 = Expect(SyntaxKind.SemicolonToken);
        ExpressionSyntax? cond = Kind != SyntaxKind.SemicolonToken && Kind != SyntaxKind.CloseParenToken ? ParseExpression() : null;
        var semi2 = Expect(SyntaxKind.SemicolonToken);
        ExpressionSyntax? incr = Kind != SyntaxKind.CloseParenToken ? ParseExpression() : null;
        var closeParen = Expect(SyntaxKind.CloseParenToken);
        return new ForStatementSyntax(kw, open, init, semi1, cond, semi2, incr, closeParen, ParseStatement());
    }

    private StatementSyntax ParseReturn()
    {
        var kw = Eat();
        ExpressionSyntax? e = null;
        if (!NewlineBeforeCurrent && Kind is not (SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
            e = ParseExpression();
        return new ReturnStatementSyntax(kw, e, ParseSemicolon());
    }

    private StatementSyntax ParseBreak()
    {
        var kw = Eat();
        SyntaxToken? label = null;
        if (!NewlineBeforeCurrent && SyntaxFacts.IsIdentifierLike(Kind)) label = Eat();
        return new BreakStatementSyntax(kw, label, ParseSemicolon());
    }

    private StatementSyntax ParseContinue()
    {
        var kw = Eat();
        SyntaxToken? label = null;
        if (!NewlineBeforeCurrent && SyntaxFacts.IsIdentifierLike(Kind)) label = Eat();
        return new ContinueStatementSyntax(kw, label, ParseSemicolon());
    }

    private StatementSyntax ParseThrow()
    {
        var kw = Eat();
        ExpressionSyntax? e = null;
        if (!NewlineBeforeCurrent && Kind is not (SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
            e = ParseExpression();
        return new ThrowStatementSyntax(kw, e, ParseSemicolon());
    }

    private StatementSyntax ParseTry()
    {
        var kw = Eat();
        var block = ParseBlock();
        var catches = new List<SyntaxElement>();
        while (Kind == SyntaxKind.CatchKeyword)
        {
            var catchKw = Eat();
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
            var fkw = Eat();
            fin = new FinallyClauseSyntax(fkw, ParseBlock());
        }
        if (catches.Count == 0 && fin == null) ReportExpected("catch' or 'finally");
        return new TryStatementSyntax(kw, block, MakeList(catches), fin);
    }

    private StatementSyntax ParseSwitch()
    {
        var kw = Eat();
        var open = Expect(SyntaxKind.OpenParenToken);
        var expr = ParseExpression();
        var close = Expect(SyntaxKind.CloseParenToken);
        var openBrace = Expect(SyntaxKind.OpenBraceToken);
        var sections = new List<SyntaxElement>();
        while (Kind is SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword)
        {
            var labels = new List<SyntaxElement>();
            while (Kind is SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword)
            {
                if (Kind == SyntaxKind.CaseKeyword)
                {
                    var ck = Eat();
                    var v = ParseExpression();
                    var colon = Expect(SyntaxKind.ColonToken);
                    labels.Add(new SwitchLabelSyntax(SyntaxKind.CaseSwitchLabel, ck, v, colon));
                }
                else
                {
                    var dk = Eat();
                    var colon = Expect(SyntaxKind.ColonToken);
                    labels.Add(new SwitchLabelSyntax(SyntaxKind.DefaultSwitchLabel, dk, null, colon));
                }
            }
            var stmts = new List<SyntaxElement>();
            while (Kind is not (SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword or SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
            {
                var before = _i;
                stmts.Add(ParseStatement());
                if (_i == before) SkipCurrent();
            }
            sections.Add(new SwitchSectionSyntax(MakeList(labels), MakeList(stmts)));
        }
        // Stray tokens inside the switch body
        while (Kind != SyntaxKind.CloseBraceToken && Kind != SyntaxKind.EndOfFileToken)
        {
            ReportAtCurrent(Descriptors.UnexpectedToken, Current.Text);
            SkipCurrent();
        }
        var closeBrace = Expect(SyntaxKind.CloseBraceToken);
        return new SwitchStatementSyntax(kw, open, expr, close, openBrace, MakeList(sections), closeBrace);
    }

    // ------------------------------------------------------------------ variables

    private VariableDeclarationListSyntax ParseVariableDeclarationList(bool forHeader = false)
    {
        SyntaxToken? awaitKw = null;
        if (Kind == SyntaxKind.AwaitKeyword && PeekKind(1) == SyntaxKind.UsingKeyword) awaitKw = Eat();
        var kw = Eat(); // let / const / var / using
        if (kw.Kind == SyntaxKind.VarKeyword)
            Report(Descriptors.VarNotSupported, kw.Span);
        var items = new List<SyntaxElement>();
        while (true)
        {
            items.Add(ParseVariableDeclarator(forHeader));
            if (Kind == SyntaxKind.CommaToken) { items.Add(Eat()); continue; }
            break;
        }
        return new VariableDeclarationListSyntax(awaitKw, kw, MakeSepList(items));
    }

    private VariableDeclaratorSyntax ParseVariableDeclarator(bool forHeader)
    {
        SyntaxToken? id = null;
        BindingPatternSyntax? pattern = null;
        if (Kind == SyntaxKind.OpenBraceToken || Kind == SyntaxKind.OpenBracketToken) pattern = ParseBindingPattern();
        else id = ParseIdentifierToken();
        var type = ParseOptionalTypeAnnotation();
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            var eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        return new VariableDeclaratorSyntax(id, pattern, type, init);
    }

    private BindingPatternSyntax ParseBindingPattern()
    {
        if (Kind == SyntaxKind.OpenBraceToken)
        {
            var open = Eat();
            var elems = ParseSeparatedList(() => ParseBindingElement(true), () => Kind == SyntaxKind.CloseBraceToken);
            var close = Expect(SyntaxKind.CloseBraceToken);
            return new ObjectBindingPatternSyntax(open, elems, close);
        }
        else
        {
            var open = Eat();
            var elems = ParseSeparatedList(() => ParseBindingElement(false), () => Kind == SyntaxKind.CloseBracketToken, null);
            var close = Expect(SyntaxKind.CloseBracketToken);
            return new ArrayBindingPatternSyntax(open, elems, close);
        }
    }

    private BindingElementSyntax ParseBindingElement(bool isObject)
    {
        if (!isObject && Kind == SyntaxKind.CommaToken)
            return new BindingElementSyntax(null, null, null, null, null, null); // omitted
        var dots = TryEat(SyntaxKind.DotDotDotToken);
        SyntaxToken? propName = null, colon = null, id = null;
        BindingPatternSyntax? nested = null;
        if (isObject && dots == null)
        {
            if (SyntaxFacts.IsIdentifierLike(Kind) && PeekKind(1) == SyntaxKind.ColonToken)
            {
                propName = Eat();
                colon = Eat();
                if (Kind is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken) nested = ParseBindingPattern();
                else id = ParseIdentifierToken();
            }
            else id = ParseIdentifierToken();
        }
        else
        {
            if (Kind is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken) nested = ParseBindingPattern();
            else id = ParseIdentifierToken();
        }
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            var eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        return new BindingElementSyntax(dots, propName, colon, id, nested, init);
    }

    private TypeAnnotationSyntax? ParseOptionalTypeAnnotation()
    {
        if (Kind != SyntaxKind.ColonToken) return null;
        var colon = Eat();
        return new TypeAnnotationSyntax(colon, ParseType());
    }

    // ------------------------------------------------------------------ imports

    private StatementSyntax ParseImport()
    {
        var kw = Eat();
        SyntaxToken? typeKw = null;
        SyntaxNode? clause = null;
        SyntaxToken? fromKw = null;
        if (Kind == SyntaxKind.TypeKeyword && (PeekKind(1) == SyntaxKind.OpenBraceToken || PeekKind(1) == SyntaxKind.AsteriskToken))
            typeKw = Eat();
        if (Kind == SyntaxKind.OpenBraceToken)
        {
            var open = Eat();
            var specs = ParseSeparatedList(ParseImportSpecifier, () => Kind == SyntaxKind.CloseBraceToken);
            var close = Expect(SyntaxKind.CloseBraceToken);
            clause = new NamedImportsSyntax(open, specs, close);
            fromKw = Expect(SyntaxKind.FromKeyword);
        }
        else if (Kind == SyntaxKind.AsteriskToken)
        {
            var star = Eat();
            var asKw = Expect(SyntaxKind.AsKeyword);
            var alias = ParseIdentifierToken();
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
        if (Kind == SyntaxKind.StringLiteralToken) spec = Eat();
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
            typeKw = Eat();
        var name = ParseIdentifierToken();
        SyntaxToken? asKw = null, alias = null;
        if (Kind == SyntaxKind.AsKeyword)
        {
            asKw = Eat();
            alias = ParseIdentifierToken();
        }
        return new ImportSpecifierSyntax(typeKw, name, asKw, alias);
    }
}
