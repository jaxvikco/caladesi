using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Syntax;

public sealed partial class Parser
{
    private static bool IsDeclModifier(SyntaxKind k) => k != SyntaxKind.ConstKeyword && SyntaxFacts.IsModifier(k);

    private bool IsStartOfDeclaration()
    {
        switch (Kind)
        {
            case SyntaxKind.LetKeyword or SyntaxKind.ConstKeyword or SyntaxKind.VarKeyword:
            case SyntaxKind.FunctionKeyword or SyntaxKind.ClassKeyword or SyntaxKind.InterfaceKeyword or SyntaxKind.EnumKeyword:
                return true;
            case SyntaxKind.UsingKeyword:
                return SyntaxFacts.IsIdentifierLike(PeekKind(1)) && !NewlineBeforePeek(1);
            case SyntaxKind.AwaitKeyword:
                return PeekKind(1) == SyntaxKind.UsingKeyword && SyntaxFacts.IsIdentifierLike(PeekKind(2));
            case SyntaxKind.TypeKeyword:
                return IsTypeAliasStart(_i);
            case SyntaxKind.NamespaceKeyword or SyntaxKind.ModuleKeyword:
                return SyntaxFacts.IsIdentifierLike(PeekKind(1)) && PeekKind(2) == SyntaxKind.OpenBraceToken;
        }
        if (!IsDeclModifier(Kind)) return false;
        // modifiers followed by a declaration keyword
        var j = _i;
        while (j < _tokens.Count && IsDeclModifier(_tokens[j].Kind)) j++;
        if (j >= _tokens.Count) return false;
        var k = _tokens[j].Kind;
        switch (k)
        {
            case SyntaxKind.FunctionKeyword or SyntaxKind.ClassKeyword or SyntaxKind.InterfaceKeyword or SyntaxKind.EnumKeyword
                or SyntaxKind.LetKeyword or SyntaxKind.ConstKeyword or SyntaxKind.VarKeyword or SyntaxKind.DefaultKeyword
                or SyntaxKind.NamespaceKeyword or SyntaxKind.ModuleKeyword:
                return true;
            case SyntaxKind.UsingKeyword:
                return true;
            case SyntaxKind.TypeKeyword:
                return IsTypeAliasStart(j);
            case SyntaxKind.OpenBraceToken or SyntaxKind.AsteriskToken:
                return _tokens[_i].Kind == SyntaxKind.ExportKeyword; // export { } / export * : unsupported but recognized
        }
        return false;
    }

    private bool IsTypeAliasStart(int index)
    {
        var t1 = index + 1 < _tokens.Count ? _tokens[index + 1] : null;
        var t2 = index + 2 < _tokens.Count ? _tokens[index + 2] : null;
        if (t1 == null || t2 == null) return false;
        return SyntaxFacts.IsIdentifierLike(t1.Kind) && (t2.Kind == SyntaxKind.EqualsToken || t2.Kind == SyntaxKind.LessThanToken)
               && !HasNewlineBefore(index + 1);
    }

    private SyntaxNode ParseDecorators()
    {
        var list = new List<SyntaxElement>();
        while (Kind == SyntaxKind.AtToken)
        {
            var at = Eat();
            list.Add(new DecoratorSyntax(at, ParseLeftHandSideExpression()));
        }
        return MakeList(list);
    }

    private SyntaxNode ParseDeclModifiers()
    {
        var mods = new List<SyntaxElement>();
        while (IsDeclModifier(Kind))
        {
            // an identifier that merely looks like a modifier (e.g. `async` as a variable) ends the run
            var next = PeekKind(1);
            if (next is SyntaxKind.EqualsToken or SyntaxKind.OpenParenToken or SyntaxKind.DotToken or SyntaxKind.SemicolonToken
                or SyntaxKind.ColonToken or SyntaxKind.CommaToken) break;
            mods.Add(Eat());
        }
        return MakeList(mods);
    }

    private StatementSyntax ParseDeclarationWithModifiers()
    {
        var decorators = ParseDecorators();
        var modifiers = ParseDeclModifiers();
        CheckDuplicateModifiers(modifiers);

        switch (Kind)
        {
            case SyntaxKind.FunctionKeyword: return ParseFunctionDeclaration(decorators, modifiers);
            case SyntaxKind.ClassKeyword: return ParseClassDeclaration(decorators, modifiers);
            case SyntaxKind.InterfaceKeyword: return ParseInterfaceDeclaration(decorators, modifiers);
            case SyntaxKind.EnumKeyword: return ParseEnumDeclaration(decorators, modifiers);
            case SyntaxKind.TypeKeyword: return ParseTypeAlias(decorators, modifiers);
            case SyntaxKind.LetKeyword or SyntaxKind.ConstKeyword or SyntaxKind.VarKeyword or SyntaxKind.UsingKeyword:
            case SyntaxKind.AwaitKeyword when PeekKind(1) == SyntaxKind.UsingKeyword:
            {
                var decl = ParseVariableDeclarationList();
                return new VariableStatementSyntax(decorators, modifiers, decl, ParseSemicolon());
            }
            case SyntaxKind.DefaultKeyword:
            {
                Report(Descriptors.ExportDefaultNotSupported, Current.Span);
                SkipCurrent();
                return ParseStatement();
            }
            case SyntaxKind.NamespaceKeyword or SyntaxKind.ModuleKeyword:
            {
                Report(Descriptors.NamespaceNotSupported, Current.Span);
                SkipCurrent();
                SkipCurrent();
                return ParseBlock();
            }
            case SyntaxKind.OpenBraceToken or SyntaxKind.AsteriskToken when modifiers.SlotCount > 0:
                ReportAtCurrent(Descriptors.FeatureNotSupported, "Re-exports and export lists");
                break;
        }
        ReportAtCurrent(Descriptors.ExpectedDeclaration);
        // keep the modifiers/decorators in the tree via an expression statement wrapper with a missing expression
        var missing = new MissingExpressionSyntax(Missing(SyntaxKind.IdentifierToken));
        var stmt = new ExpressionStatementSyntax(missing, null);
        if (modifiers.SlotCount == 0 && decorators.SlotCount == 0) return stmt;
        return new VariableStatementSyntax(decorators, modifiers,
            new VariableDeclarationListSyntax(null, Missing(SyntaxKind.LetKeyword),
                MakeSepList(new List<SyntaxElement> { new VariableDeclaratorSyntax(Missing(SyntaxKind.IdentifierToken), null, null, null) })),
            null);
    }

    private void CheckDuplicateModifiers(SyntaxNode modifiers)
    {
        var seen = new HashSet<SyntaxKind>();
        foreach (var m in ((ListNode)modifiers).Children())
            if (m is SyntaxToken t && !seen.Add(t.Kind))
                Report(Descriptors.DuplicateModifier, t.Span, t.Text);
    }

    private StatementSyntax ParseFunctionDeclaration(SyntaxNode decorators, SyntaxNode modifiers)
    {
        var fn = Eat();
        var star = TryEat(SyntaxKind.AsteriskToken);
        var name = ParseIdentifierToken();
        var tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        var ps = ParseParameterList();
        var ret = ParseOptionalTypeAnnotation();
        BlockSyntax? body = null;
        SyntaxToken? semi = null;
        if (Kind == SyntaxKind.OpenBraceToken) body = ParseBlock();
        else semi = ParseSemicolon();
        return new FunctionDeclarationSyntax(decorators, modifiers, fn, star, name, tp, ps, ret, body, semi);
    }

    private StatementSyntax ParseClassDeclaration(SyntaxNode decorators, SyntaxNode modifiers)
    {
        var kw = Eat();
        var name = ParseIdentifierToken();
        var tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        HeritageClauseSyntax? ext = null, impl = null;
        while (Kind == SyntaxKind.ExtendsKeyword || Kind == SyntaxKind.ImplementsKeyword)
        {
            var isExt = Kind == SyntaxKind.ExtendsKeyword;
            var hkw = Eat();
            var types = ParseSeparatedList(ParseHeritageType, () => Kind == SyntaxKind.OpenBraceToken);
            var clause = new HeritageClauseSyntax(hkw, types);
            if (isExt) ext = clause; else impl = clause;
        }
        var open = Expect(SyntaxKind.OpenBraceToken);
        var members = ParseMembers(isInterfaceOrObjectType: false);
        var close = Expect(SyntaxKind.CloseBraceToken);
        return new ClassDeclarationSyntax(decorators, modifiers, kw, name, tp, ext, impl, open, members, close);
    }

    private TypeSyntax ParseHeritageType() => ParseTypeName();

    private StatementSyntax ParseInterfaceDeclaration(SyntaxNode decorators, SyntaxNode modifiers)
    {
        var kw = Eat();
        var name = ParseIdentifierToken();
        var tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        HeritageClauseSyntax? ext = null;
        if (Kind == SyntaxKind.ExtendsKeyword)
        {
            var hkw = Eat();
            ext = new HeritageClauseSyntax(hkw, ParseSeparatedList(ParseHeritageType, () => Kind == SyntaxKind.OpenBraceToken));
        }
        var open = Expect(SyntaxKind.OpenBraceToken);
        var members = ParseMembers(isInterfaceOrObjectType: true);
        var close = Expect(SyntaxKind.CloseBraceToken);
        return new InterfaceDeclarationSyntax(decorators, modifiers, kw, name, tp, ext, open, members, close);
    }

    private StatementSyntax ParseEnumDeclaration(SyntaxNode decorators, SyntaxNode modifiers)
    {
        var kw = Eat();
        var name = ParseIdentifierToken();
        var open = Expect(SyntaxKind.OpenBraceToken);
        var members = ParseSeparatedList(ParseEnumMember, () => Kind == SyntaxKind.CloseBraceToken);
        var close = Expect(SyntaxKind.CloseBraceToken);
        return new EnumDeclarationSyntax(decorators, modifiers, kw, name, open, members, close);
    }

    private EnumMemberSyntax ParseEnumMember()
    {
        var id = Kind == SyntaxKind.StringLiteralToken ? Eat() : ParseIdentifierToken();
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            var eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        return new EnumMemberSyntax(id, init);
    }

    private StatementSyntax ParseTypeAlias(SyntaxNode decorators, SyntaxNode modifiers)
    {
        var kw = Eat();
        var name = ParseIdentifierToken();
        var tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
        var eq = Expect(SyntaxKind.EqualsToken);
        var type = ParseType();
        return new TypeAliasDeclarationSyntax(decorators, modifiers, kw, name, tp, eq, type, ParseSemicolon());
    }

    // ------------------------------------------------------------------ parameters

    private ParameterListSyntax ParseParameterList()
    {
        var open = Expect(SyntaxKind.OpenParenToken);
        var ps = ParseSeparatedList(ParseParameter, () => Kind == SyntaxKind.CloseParenToken);
        var close = Expect(SyntaxKind.CloseParenToken);
        return new ParameterListSyntax(open, ps, close);
    }

    private static bool IsParameterModifier(SyntaxKind k) => k is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword
        or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ReadonlyKeyword or SyntaxKind.OverrideKeyword
        or SyntaxKind.ThisKeyword;

    private ParameterSyntax ParseParameter()
    {
        var decorators = ParseDecorators();
        var mods = new List<SyntaxElement>();
        while (IsParameterModifier(Kind))
        {
            var next = PeekKind(1);
            if (!(SyntaxFacts.IsIdentifierLike(next) || next is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken or SyntaxKind.DotDotDotToken))
                break;
            mods.Add(Eat());
        }
        var dots = TryEat(SyntaxKind.DotDotDotToken);
        SyntaxToken? id = null;
        BindingPatternSyntax? pattern = null;
        if (Kind is SyntaxKind.OpenBraceToken or SyntaxKind.OpenBracketToken) pattern = ParseBindingPattern();
        else if (Kind == SyntaxKind.ThisKeyword) id = Eat();
        else id = ParseIdentifierToken();
        var q = TryEat(SyntaxKind.QuestionToken);
        var type = ParseOptionalTypeAnnotation();
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            var eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        return new ParameterSyntax(decorators, MakeList(mods), dots, id, pattern, q, type, init);
    }

    // ------------------------------------------------------------------ members

    private SyntaxNode ParseMembers(bool isInterfaceOrObjectType)
    {
        var members = new List<SyntaxElement>();
        while (Kind != SyntaxKind.CloseBraceToken && Kind != SyntaxKind.EndOfFileToken)
        {
            var before = _i;
            if (Kind == SyntaxKind.SemicolonToken) { SkipCurrent(); continue; }
            var m = ParseMember(isInterfaceOrObjectType);
            if (m == null)
            {
                ReportAtCurrent(Descriptors.ExpectedMember);
                SkipCurrent();
                continue;
            }
            members.Add(m);
            if (_i == before) SkipCurrent();
        }
        return MakeList(members);
    }

    private static bool IsMemberNameToken(SyntaxKind k) =>
        SyntaxFacts.IsIdentifierLike(k) || SyntaxFacts.IsKeyword(k) || k is SyntaxKind.StringLiteralToken or SyntaxKind.NumericLiteralToken;

    private MemberDeclarationSyntax? ParseMember(bool inInterface)
    {
        var decorators = ParseDecorators();
        var mods = new List<SyntaxElement>();
        while (IsDeclModifier(Kind))
        {
            var next = PeekKind(1);
            if (next is SyntaxKind.OpenParenToken or SyntaxKind.ColonToken or SyntaxKind.EqualsToken or SyntaxKind.SemicolonToken
                or SyntaxKind.QuestionToken or SyntaxKind.LessThanToken or SyntaxKind.ExclamationToken or SyntaxKind.CloseBraceToken
                or SyntaxKind.CommaToken) break;
            if (NewlineBeforePeek(1) && !inInterface && false) break;
            mods.Add(Eat());
        }
        var modList = MakeList(mods);
        CheckDuplicateModifiers(modList);

        if (Kind == SyntaxKind.ConstructorKeyword && PeekKind(1) == SyntaxKind.OpenParenToken)
        {
            var kw = Eat();
            var ps = ParseParameterList();
            BlockSyntax? body = null;
            SyntaxToken? semi = null;
            if (Kind == SyntaxKind.OpenBraceToken) body = ParseBlock();
            else semi = ParseSemicolon();
            return new ConstructorDeclarationSyntax(decorators, modList, kw, ps, body, semi);
        }

        if ((Kind == SyntaxKind.GetKeyword || Kind == SyntaxKind.SetKeyword) && IsMemberNameToken(PeekKind(1))
            && PeekKind(1) != SyntaxKind.OpenParenToken && PeekKind(2) is SyntaxKind.OpenParenToken)
        {
            var kw = Eat();
            var name = Eat();
            var ps = ParseParameterList();
            var ret = ParseOptionalTypeAnnotation();
            BlockSyntax? body = null;
            SyntaxToken? semi = null;
            if (Kind == SyntaxKind.OpenBraceToken) body = ParseBlock();
            else semi = ParseSemicolon();
            var kind = kw.Kind == SyntaxKind.GetKeyword ? SyntaxKind.GetAccessorDeclaration : SyntaxKind.SetAccessorDeclaration;
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
        var id = Eat();
        var q = TryEat(SyntaxKind.QuestionToken);
        if (Kind == SyntaxKind.OpenParenToken || Kind == SyntaxKind.LessThanToken)
        {
            var tp = Kind == SyntaxKind.LessThanToken ? ParseTypeParameterList() : null;
            var ps = ParseParameterList();
            var ret = ParseOptionalTypeAnnotation();
            BlockSyntax? body = null;
            SyntaxToken? sep = null;
            if (Kind == SyntaxKind.OpenBraceToken) body = ParseBlock();
            else sep = ParseMemberSeparator(inInterface);
            return new MethodDeclarationSyntax(decorators, modList, star, id, q, tp, ps, ret, body, sep);
        }
        var type = ParseOptionalTypeAnnotation();
        EqualsValueClauseSyntax? init = null;
        if (Kind == SyntaxKind.EqualsToken)
        {
            var eq = Eat();
            init = new EqualsValueClauseSyntax(eq, ParseAssignmentExpression());
        }
        var sep2 = ParseMemberSeparator(inInterface);
        return new PropertyDeclarationSyntax(decorators, modList, id, q, type, init, sep2);
    }

    private SyntaxToken? ParseMemberSeparator(bool inInterface)
    {
        if (Kind == SyntaxKind.SemicolonToken) return Eat();
        if (inInterface && Kind == SyntaxKind.CommaToken) return Eat();
        if (Kind == SyntaxKind.CloseBraceToken || Kind == SyntaxKind.EndOfFileToken || NewlineBeforeCurrent) return null;
        ReportExpected(";");
        return null;
    }
}
