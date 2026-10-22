using Caladesi.Compiler.Diagnostics;

namespace Caladesi.Compiler.Syntax;

public sealed partial class Parser
{
    private static bool IsDeclModifier(SyntaxKind k)
    {
        return k != SyntaxKind.ConstKeyword && SyntaxFacts.IsModifier(k);
    }

    private bool IsStartOfDeclaration()
    {
        switch (Kind)
        {
            case SyntaxKind.LetKeyword or SyntaxKind.ConstKeyword:
            case SyntaxKind.FunctionKeyword or SyntaxKind.ClassKeyword or SyntaxKind.InterfaceKeyword or SyntaxKind.EnumKeyword:
                return true;
            case SyntaxKind.UsingKeyword:
                return SyntaxFacts.IsIdentifierLike(PeekKind(1)) && !NewlineBeforePeek(1);
            case SyntaxKind.AwaitKeyword:
                return PeekKind(1) == SyntaxKind.UsingKeyword && SyntaxFacts.IsIdentifierLike(PeekKind(2));
            case SyntaxKind.TypeKeyword:
                return IsTypeAliasStart(_i);
        }
        if (!IsDeclModifier(Kind))
        {
            return false;
        }
        // modifiers followed by a declaration keyword
        int j = _i;
        while (j < _tokens.Count && IsDeclModifier(_tokens[j].Kind))
        {
            j++;
        }

        if (j >= _tokens.Count)
        {
            return false;
        }

        SyntaxKind k = _tokens[j].Kind;
        return k switch
        {
            SyntaxKind.FunctionKeyword or SyntaxKind.ClassKeyword or SyntaxKind.InterfaceKeyword or SyntaxKind.EnumKeyword
                or SyntaxKind.LetKeyword or SyntaxKind.ConstKeyword or SyntaxKind.DefaultKeyword => true,
            SyntaxKind.UsingKeyword => true,
            SyntaxKind.TypeKeyword => IsTypeAliasStart(j),
            SyntaxKind.OpenBraceToken or SyntaxKind.AsteriskToken => _tokens[_i].Kind == SyntaxKind.ExportKeyword, // export { } / export * : unsupported but recognized
            _ => false,
        };
    }

    private bool IsTypeAliasStart(int index)
    {
        SyntaxToken? t1 = index + 1 < _tokens.Count ? _tokens[index + 1] : null;
        SyntaxToken? t2 = index + 2 < _tokens.Count ? _tokens[index + 2] : null;
        return t1 != null && t2 != null && SyntaxFacts.IsIdentifierLike(t1.Kind) && (t2.Kind == SyntaxKind.EqualsToken || t2.Kind == SyntaxKind.LessThanToken)
               && !HasNewlineBefore(index + 1);
    }

    private ListNode ParseDecorators()
    {
        List<SyntaxElement> list = [];
        while (Kind == SyntaxKind.AtToken)
        {
            SyntaxToken at = Eat();
            list.Add(new DecoratorSyntax(at, ParseLeftHandSideExpression()));
        }
        return MakeList(list);
    }

    private ListNode ParseDeclModifiers()
    {
        List<SyntaxElement> mods = [];
        while (IsDeclModifier(Kind))
        {
            // an identifier that merely looks like a modifier (e.g. `async` as a variable) ends the run
            SyntaxKind next = PeekKind(1);
            if (next is SyntaxKind.EqualsToken or SyntaxKind.OpenParenToken or SyntaxKind.DotToken or SyntaxKind.SemicolonToken
                or SyntaxKind.ColonToken or SyntaxKind.CommaToken)
            {
                break;
            }

            mods.Add(Eat());
        }
        return MakeList(mods);
    }

    private StatementSyntax ParseDeclarationWithModifiers()
    {
        SyntaxNode decorators = ParseDecorators();
        SyntaxNode modifiers = ParseDeclModifiers();
        CheckDuplicateModifiers(modifiers);

        switch (Kind)
        {
            case SyntaxKind.FunctionKeyword: return ParseFunctionDeclaration(decorators, modifiers);
            case SyntaxKind.ClassKeyword: return ParseClassDeclaration(decorators, modifiers);
            case SyntaxKind.InterfaceKeyword: return ParseInterfaceDeclaration(decorators, modifiers);
            case SyntaxKind.EnumKeyword: return ParseEnumDeclaration(decorators, modifiers);
            case SyntaxKind.TypeKeyword: return ParseTypeAlias(decorators, modifiers);
            case SyntaxKind.LetKeyword or SyntaxKind.ConstKeyword or SyntaxKind.UsingKeyword:
            case SyntaxKind.AwaitKeyword when PeekKind(1) == SyntaxKind.UsingKeyword:
                {
                    VariableDeclarationListSyntax decl = ParseVariableDeclarationList();
                    return new VariableStatementSyntax(decorators, modifiers, decl, ParseSemicolon());
                }
            case SyntaxKind.DefaultKeyword:
                {
                    Report(Descriptors.ExportDefaultNotSupported, Current.Span);
                    SkipCurrent();
                    return ParseStatement();
                }
            case SyntaxKind.OpenBraceToken or SyntaxKind.AsteriskToken when modifiers.SlotCount > 0:
                ReportAtCurrent(Descriptors.FeatureNotSupported, "Re-exports and export lists");
                break;
        }
        ReportAtCurrent(Descriptors.ExpectedDeclaration);
        // keep the modifiers/decorators in the tree via an expression statement wrapper with a missing expression
        MissingExpressionSyntax missing = new(Missing(SyntaxKind.IdentifierToken));
        ExpressionStatementSyntax stmt = new(missing, null);
        return modifiers.SlotCount == 0 && decorators.SlotCount == 0
            ? stmt
            : new VariableStatementSyntax(decorators, modifiers,
            new VariableDeclarationListSyntax(null, Missing(SyntaxKind.LetKeyword),
                MakeSepList(new List<SyntaxElement> { new VariableDeclaratorSyntax(Missing(SyntaxKind.IdentifierToken), null, null, null) })),
            null);
    }

    private void CheckDuplicateModifiers(SyntaxNode modifiers)
    {
        HashSet<SyntaxKind> seen = [];
        foreach (SyntaxElement m in ((ListNode)modifiers).Children())
        {
            if (m is SyntaxToken t && !seen.Add(t.Kind))
            {
                Report(Descriptors.DuplicateModifier, t.Span, t.Text);
            }
        }
    }

    private FunctionDeclarationSyntax ParseFunctionDeclaration(SyntaxNode decorators, SyntaxNode modifiers)
    {
        SyntaxToken fn = Eat();
        SyntaxToken? star = TryEat(SyntaxKind.AsteriskToken);
        SyntaxToken name = ParseIdentifierToken();
        TypeParameterListSyntax? tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        ParameterListSyntax ps = ParseParameterList();
        TypeAnnotationSyntax? ret = ParseOptionalTypeAnnotation();
        BlockSyntax? body = null;
        SyntaxToken? semi = null;
        if (Kind == SyntaxKind.OpenBraceToken)
        {
            body = ParseBlock();
        }
        else
        {
            semi = ParseSemicolon();
        }

        return new FunctionDeclarationSyntax(decorators, modifiers, fn, star, name, tp, ps, ret, body, semi);
    }

    private ClassDeclarationSyntax ParseClassDeclaration(SyntaxNode decorators, SyntaxNode modifiers)
    {
        SyntaxToken kw = Eat();
        SyntaxToken name = ParseIdentifierToken();
        TypeParameterListSyntax? tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        HeritageClauseSyntax? ext = null, impl = null;
        while (Kind is SyntaxKind.ExtendsKeyword or SyntaxKind.ImplementsKeyword)
        {
            bool isExt = Kind == SyntaxKind.ExtendsKeyword;
            SyntaxToken hkw = Eat();
            SyntaxNode types = ParseSeparatedList(ParseHeritageType, () => Kind == SyntaxKind.OpenBraceToken);
            HeritageClauseSyntax clause = new(hkw, types);
            if (isExt)
            {
                ext = clause;
            }
            else
            {
                impl = clause;
            }
        }
        SyntaxToken open = Expect(SyntaxKind.OpenBraceToken);
        SyntaxNode members = ParseMembers(isInterfaceOrObjectType: false);
        SyntaxToken close = Expect(SyntaxKind.CloseBraceToken);
        return new ClassDeclarationSyntax(decorators, modifiers, kw, name, tp, ext, impl, open, members, close);
    }

    private TypeSyntax ParseHeritageType()
    {
        return ParseTypeName();
    }

    private InterfaceDeclarationSyntax ParseInterfaceDeclaration(SyntaxNode decorators, SyntaxNode modifiers)
    {
        SyntaxToken kw = Eat();
        SyntaxToken name = ParseIdentifierToken();
        TypeParameterListSyntax? tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        HeritageClauseSyntax? ext = null;
        if (Kind == SyntaxKind.ExtendsKeyword)
        {
            SyntaxToken hkw = Eat();
            ext = new HeritageClauseSyntax(hkw, ParseSeparatedList(ParseHeritageType, () => Kind == SyntaxKind.OpenBraceToken));
        }
        SyntaxToken open = Expect(SyntaxKind.OpenBraceToken);
        SyntaxNode members = ParseMembers(isInterfaceOrObjectType: true);
        SyntaxToken close = Expect(SyntaxKind.CloseBraceToken);
        return new InterfaceDeclarationSyntax(decorators, modifiers, kw, name, tp, ext, open, members, close);
    }

    private EnumDeclarationSyntax ParseEnumDeclaration(SyntaxNode decorators, SyntaxNode modifiers)
    {
        SyntaxToken kw = Eat();
        SyntaxToken name = ParseIdentifierToken();
        SyntaxToken open = Expect(SyntaxKind.OpenBraceToken);
        SyntaxNode members = ParseSeparatedList(ParseEnumMember, () => Kind == SyntaxKind.CloseBraceToken);
        SyntaxToken close = Expect(SyntaxKind.CloseBraceToken);
        return new EnumDeclarationSyntax(decorators, modifiers, kw, name, open, members, close);
    }

    private EnumMemberSyntax ParseEnumMember()
    {
        SyntaxToken id = Kind == SyntaxKind.StringLiteralToken ? Eat() : ParseIdentifierToken();
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            SyntaxToken eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        return new EnumMemberSyntax(id, init);
    }

    private TypeAliasDeclarationSyntax ParseTypeAlias(SyntaxNode decorators, SyntaxNode modifiers)
    {
        SyntaxToken kw = Eat();
        SyntaxToken name = ParseIdentifierToken();
        TypeParameterListSyntax? tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        SyntaxToken eq = Expect(SyntaxKind.EqualsToken);
        TypeSyntax type = ParseType();
        return new TypeAliasDeclarationSyntax(decorators, modifiers, kw, name, tp, eq, type, ParseSemicolon());
    }

    // ------------------------------------------------------------------ parameters

    private ParameterListSyntax ParseParameterList()
    {
        SyntaxToken open = Expect(SyntaxKind.OpenParenToken);
        SyntaxNode ps = ParseSeparatedList(ParseParameter, () => Kind == SyntaxKind.CloseParenToken);
        SyntaxToken close = Expect(SyntaxKind.CloseParenToken);
        return new ParameterListSyntax(open, ps, close);
    }

    private static bool IsParameterModifier(SyntaxKind k)
    {
        return k is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword
        or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ReadonlyKeyword or SyntaxKind.OverrideKeyword
        or SyntaxKind.ThisKeyword;
    }

    private ParameterSyntax ParseParameter()
    {
        SyntaxNode decorators = ParseDecorators();
        List<SyntaxElement> mods = [];
        while (IsParameterModifier(Kind))
        {
            SyntaxKind next = PeekKind(1);
            if (!(SyntaxFacts.IsIdentifierLike(next) || next is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken or SyntaxKind.DotDotDotToken))
            {
                break;
            }

            mods.Add(Eat());
        }
        SyntaxToken? dots = TryEat(SyntaxKind.DotDotDotToken);
        SyntaxToken? id = null;
        BindingPatternSyntax? pattern = null;
        if (Kind is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken)
        {
            pattern = ParseBindingPattern();
        }
        else
        {
            id = Kind == SyntaxKind.ThisKeyword ? Eat() : ParseIdentifierToken();
        }

        SyntaxToken? q = TryEat(SyntaxKind.QuestionToken);
        TypeAnnotationSyntax? type = ParseOptionalTypeAnnotation();
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            SyntaxToken eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        return new ParameterSyntax(decorators, MakeList(mods), dots, id, pattern, q, type, init);
    }

    // ------------------------------------------------------------------ members

    private ListNode ParseMembers(bool isInterfaceOrObjectType)
    {
        List<SyntaxElement> members = [];
        while (Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            int before = _i;
            if (Kind == SyntaxKind.SemicolonToken) { SkipCurrent(); continue; }
            MemberDeclarationSyntax? m = ParseMember(isInterfaceOrObjectType);
            if (m == null)
            {
                ReportAtCurrent(Descriptors.ExpectedMember);
                SkipCurrent();
                continue;
            }
            members.Add(m);
            if (_i == before)
            {
                SkipCurrent();
            }
        }
        return MakeList(members);
    }

    private static bool IsMemberNameToken(SyntaxKind k)
    {
        return SyntaxFacts.IsIdentifierLike(k) || SyntaxFacts.IsKeyword(k) || k is SyntaxKind.StringLiteralToken or SyntaxKind.NumericLiteralToken;
    }

    private MemberDeclarationSyntax? ParseMember(bool inInterface)
    {
        SyntaxNode decorators = ParseDecorators();
        List<SyntaxElement> mods = [];
        while (IsDeclModifier(Kind))
        {
            SyntaxKind next = PeekKind(1);
            if (next is SyntaxKind.OpenParenToken or SyntaxKind.ColonToken or SyntaxKind.EqualsToken or SyntaxKind.SemicolonToken
                or SyntaxKind.QuestionToken or SyntaxKind.LessThanToken or SyntaxKind.ExclamationToken or SyntaxKind.CloseBraceToken
                or SyntaxKind.CommaToken)
            {
                break;
            }

            if (NewlineBeforePeek(1) && !inInterface && false)
            {
                break;
            }

            mods.Add(Eat());
        }
        SyntaxNode modList = MakeList(mods);
        CheckDuplicateModifiers(modList);

        if (Kind == SyntaxKind.ConstructorKeyword && PeekKind(1) == SyntaxKind.OpenParenToken)
        {
            SyntaxToken kw = Eat();
            ParameterListSyntax ps = ParseParameterList();
            BlockSyntax? body = null;
            SyntaxToken? semi = null;
            if (Kind == SyntaxKind.OpenBraceToken)
            {
                body = ParseBlock();
            }
            else
            {
                semi = ParseSemicolon();
            }

            return new ConstructorDeclarationSyntax(decorators, modList, kw, ps, body, semi);
        }

        if ((Kind == SyntaxKind.GetKeyword || Kind == SyntaxKind.SetKeyword) && IsMemberNameToken(PeekKind(1))
            && PeekKind(1) != SyntaxKind.OpenParenToken && PeekKind(2) is SyntaxKind.OpenParenToken)
        {
            SyntaxToken kw = Eat();
            SyntaxToken name = Eat();
            ParameterListSyntax ps = ParseParameterList();
            TypeAnnotationSyntax? ret = ParseOptionalTypeAnnotation();
            BlockSyntax? body = null;
            SyntaxToken? semi = null;
            if (Kind == SyntaxKind.OpenBraceToken)
            {
                body = ParseBlock();
            }
            else
            {
                semi = ParseSemicolon();
            }

            SyntaxKind kind = kw.Kind == SyntaxKind.GetKeyword ? SyntaxKind.GetAccessorDeclaration : SyntaxKind.SetAccessorDeclaration;
            return new AccessorDeclarationSyntax(kind, decorators, modList, kw, name, ps, ret, body, semi);
        }

        SyntaxToken? star = TryEat(SyntaxKind.AsteriskToken);
        if (!IsMemberNameToken(Kind))
        {
            if (star != null || mods.Count > 0 || decorators.SlotCount > 0)
            {
                // Recover: member with a missing name
                ReportExpected("member name");
                return new PropertyDeclarationSyntax(decorators, modList, Missing(SyntaxKind.IdentifierToken), null, null, null, null);
            }
            return null;
        }
        SyntaxToken id = Eat();
        SyntaxToken? q = TryEat(SyntaxKind.QuestionToken);
        if (Kind is SyntaxKind.OpenParenToken or SyntaxKind.LessThanToken)
        {
            TypeParameterListSyntax? tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
            ParameterListSyntax ps = ParseParameterList();
            TypeAnnotationSyntax? ret = ParseOptionalTypeAnnotation();
            BlockSyntax? body = null;
            SyntaxToken? sep = null;
            if (Kind == SyntaxKind.OpenBraceToken)
            {
                body = ParseBlock();
            }
            else
            {
                sep = ParseMemberSeparator(inInterface);
            }

            return new MethodDeclarationSyntax(decorators, modList, star, id, q, tp, ps, ret, body, sep);
        }
        TypeAnnotationSyntax? type = ParseOptionalTypeAnnotation();
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            SyntaxToken eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        SyntaxToken? sep2 = ParseMemberSeparator(inInterface);
        return new PropertyDeclarationSyntax(decorators, modList, id, q, type, init, sep2);
    }

    private SyntaxToken? ParseMemberSeparator(bool inInterface)
    {
        if (Kind == SyntaxKind.SemicolonToken)
        {
            return Eat();
        }

        if (inInterface && Kind == SyntaxKind.CommaToken)
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
