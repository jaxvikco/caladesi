namespace Caladesi.Compiler.Syntax;

public abstract class StatementSyntax(SyntaxKind kind, params SyntaxElement?[] slots) : SyntaxNode(kind, slots);
public abstract class ExpressionSyntax(SyntaxKind kind, params SyntaxElement?[] slots) : SyntaxNode(kind, slots);
public abstract class TypeSyntax(SyntaxKind kind, params SyntaxElement?[] slots) : ExpressionSyntax(kind, slots);
public abstract class NameSyntax(SyntaxKind kind, params SyntaxElement?[] slots) : TypeSyntax(kind, slots);
public abstract class SimpleNameSyntax(SyntaxKind kind, params SyntaxElement?[] slots) : NameSyntax(kind, slots)
{
    public abstract SyntaxToken Identifier { get; }
    public string Name => Identifier.Text;
}
public abstract class MemberDeclarationSyntax(SyntaxKind kind, params SyntaxElement?[] slots) : SyntaxNode(kind, slots);
public abstract class LiteralLikeExpressionSyntax(SyntaxKind kind, params SyntaxElement?[] slots) : ExpressionSyntax(kind, slots);

public sealed class CompilationUnitSyntax(SyntaxNode statements, SyntaxToken endOfFile)
    : SyntaxNode(SyntaxKind.CompilationUnit, statements, endOfFile)
{
    public SyntaxList<StatementSyntax> Statements => L<StatementSyntax>(0);
    public SyntaxToken EndOfFileToken => Tok(1);
}

// ---------------------------------------------------------------- names & types

public sealed class IdentifierNameSyntax(SyntaxToken identifier) : SimpleNameSyntax(SyntaxKind.IdentifierName, identifier)
{
    public override SyntaxToken Identifier => Tok(0);
}

public sealed class GenericNameSyntax(SyntaxToken identifier, TypeArgumentListSyntax args)
    : SimpleNameSyntax(SyntaxKind.GenericName, identifier, args)
{
    public override SyntaxToken Identifier => Tok(0);
    public TypeArgumentListSyntax TypeArguments => Node<TypeArgumentListSyntax>(1);
}

public sealed class QualifiedNameSyntax(NameSyntax left, SyntaxToken dot, SimpleNameSyntax right)
    : NameSyntax(SyntaxKind.QualifiedName, left, dot, right)
{
    public NameSyntax Left => Node<NameSyntax>(0);
    public SyntaxToken DotToken => Tok(1);
    public SimpleNameSyntax Right => Node<SimpleNameSyntax>(2);
}

public sealed class TypeArgumentListSyntax(SyntaxToken lt, SyntaxNode args, SyntaxToken gt)
    : SyntaxNode(SyntaxKind.TypeArgumentList, lt, args, gt)
{
    public SyntaxToken LessThanToken => Tok(0);
    public SeparatedSyntaxList<TypeSyntax> Arguments => SL<TypeSyntax>(1);
    public SyntaxToken GreaterThanToken => Tok(2);
}

public sealed class ArrayTypeSyntax(TypeSyntax element, SyntaxToken open, SyntaxToken close)
    : TypeSyntax(SyntaxKind.ArrayType, element, open, close)
{
    public TypeSyntax ElementType => Node<TypeSyntax>(0);
}

public sealed class ReadonlyArrayTypeSyntax(SyntaxToken readonlyKeyword, TypeSyntax type)
    : TypeSyntax(SyntaxKind.ReadonlyArrayType, readonlyKeyword, type)
{
    public SyntaxToken ReadonlyKeyword => Tok(0);
    public TypeSyntax Type => Node<TypeSyntax>(1);
}

public sealed class TupleTypeSyntax(SyntaxToken open, SyntaxNode elements, SyntaxToken close)
    : TypeSyntax(SyntaxKind.TupleType, open, elements, close)
{
    public SeparatedSyntaxList<TupleElementSyntax> Elements => SL<TupleElementSyntax>(1);
}

public sealed class TupleElementSyntax(SyntaxToken? name, SyntaxToken? colon, TypeSyntax type)
    : SyntaxNode(SyntaxKind.TupleElement, name, colon, type)
{
    public SyntaxToken? NameToken => TokOpt(0);
    public TypeSyntax Type => Node<TypeSyntax>(2);
}

public sealed class UnionTypeSyntax(SyntaxToken? leadingBar, SyntaxNode types)
    : TypeSyntax(SyntaxKind.UnionType, leadingBar, types)
{
    public SeparatedSyntaxList<TypeSyntax> Types => SL<TypeSyntax>(1);
}

public sealed class IntersectionTypeSyntax(SyntaxNode types) : TypeSyntax(SyntaxKind.IntersectionType, types)
{
    public SeparatedSyntaxList<TypeSyntax> Types => SL<TypeSyntax>(0);
}

public sealed class NullableTypeSyntax(TypeSyntax type, SyntaxToken question)
    : TypeSyntax(SyntaxKind.NullableType, type, question)
{
    public TypeSyntax ElementType => Node<TypeSyntax>(0);
}

public sealed class FunctionTypeSyntax(TypeParameterListSyntax? typeParams, ParameterListSyntax parameters, SyntaxToken arrow, TypeSyntax returnType)
    : TypeSyntax(SyntaxKind.FunctionType, typeParams, parameters, arrow, returnType)
{
    public TypeParameterListSyntax? TypeParameters => NodeOpt<TypeParameterListSyntax>(0);
    public ParameterListSyntax Parameters => Node<ParameterListSyntax>(1);
    public TypeSyntax ReturnType => Node<TypeSyntax>(3);
}

public sealed class ObjectTypeSyntax(SyntaxToken open, SyntaxNode members, SyntaxToken close)
    : TypeSyntax(SyntaxKind.ObjectType, open, members, close)
{
    public SyntaxList<MemberDeclarationSyntax> Members => L<MemberDeclarationSyntax>(1);
}

public sealed class LiteralTypeSyntax(ExpressionSyntax literal) : TypeSyntax(SyntaxKind.LiteralType, literal)
{
    public ExpressionSyntax Literal => Node<ExpressionSyntax>(0);
}

public sealed class ParenthesizedTypeSyntax(SyntaxToken open, TypeSyntax type, SyntaxToken close)
    : TypeSyntax(SyntaxKind.ParenthesizedType, open, type, close)
{
    public TypeSyntax Type => Node<TypeSyntax>(1);
}

public sealed class NewConstraintTypeSyntax(SyntaxToken newKeyword, SyntaxToken open, SyntaxToken close)
    : TypeSyntax(SyntaxKind.NewConstraintType, newKeyword, open, close);

public sealed class SpecialConstraintTypeSyntax(SyntaxToken keyword) : TypeSyntax(SyntaxKind.SpecialConstraintType, keyword)
{
    public SyntaxToken Keyword => Tok(0);
}

public sealed class MissingTypeSyntax(SyntaxToken missing) : TypeSyntax(SyntaxKind.MissingType, missing);

public sealed class TypeAnnotationSyntax(SyntaxToken colon, TypeSyntax type) : SyntaxNode(SyntaxKind.TypeAnnotation, colon, type)
{
    public SyntaxToken ColonToken => Tok(0);
    public TypeSyntax Type => Node<TypeSyntax>(1);
}

public sealed class EqualsValueClauseSyntax(SyntaxToken equals, ExpressionSyntax value)
    : SyntaxNode(SyntaxKind.EqualsValueClause, equals, value)
{
    public ExpressionSyntax Value => Node<ExpressionSyntax>(1);
}

public sealed class TypeParameterListSyntax(SyntaxToken lt, SyntaxNode parameters, SyntaxToken gt)
    : SyntaxNode(SyntaxKind.TypeParameterList, lt, parameters, gt)
{
    public SeparatedSyntaxList<TypeParameterSyntax> Parameters => SL<TypeParameterSyntax>(1);
}

public sealed class TypeParameterSyntax(SyntaxToken? variance, SyntaxToken identifier, SyntaxToken? extendsKeyword, TypeSyntax? constraint, EqualsValueClauseSyntax? defaultType)
    : SyntaxNode(SyntaxKind.TypeParameter, variance, identifier, extendsKeyword, constraint, defaultType)
{
    public SyntaxToken? VarianceKeyword => TokOpt(0);
    public SyntaxToken Identifier => Tok(1);
    public TypeSyntax? Constraint => NodeOpt<TypeSyntax>(3);
}

// ---------------------------------------------------------------- parameters, decorators

public sealed class DecoratorSyntax(SyntaxToken at, ExpressionSyntax expression) : SyntaxNode(SyntaxKind.Decorator, at, expression)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(1);
}

public sealed class ParameterListSyntax(SyntaxToken open, SyntaxNode parameters, SyntaxToken close)
    : SyntaxNode(SyntaxKind.ParameterList, open, parameters, close)
{
    public SeparatedSyntaxList<ParameterSyntax> Parameters => SL<ParameterSyntax>(1);
}

public sealed class ParameterSyntax(SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken? dotDotDot, SyntaxToken? identifier,
    BindingPatternSyntax? pattern, SyntaxToken? question, TypeAnnotationSyntax? type, EqualsValueClauseSyntax? initializer)
    : SyntaxNode(SyntaxKind.Parameter, decorators, modifiers, dotDotDot, identifier, pattern, question, type, initializer)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public SyntaxToken? DotDotDotToken => TokOpt(2);
    public SyntaxToken? Identifier => TokOpt(3);
    public BindingPatternSyntax? Pattern => NodeOpt<BindingPatternSyntax>(4);
    public SyntaxToken? QuestionToken => TokOpt(5);
    public TypeAnnotationSyntax? TypeAnnotation => NodeOpt<TypeAnnotationSyntax>(6);
    public EqualsValueClauseSyntax? Initializer => NodeOpt<EqualsValueClauseSyntax>(7);
}

public abstract class BindingPatternSyntax(SyntaxKind kind, params SyntaxElement?[] slots) : SyntaxNode(kind, slots);

public sealed class ObjectBindingPatternSyntax(SyntaxToken open, SyntaxNode elements, SyntaxToken close)
    : BindingPatternSyntax(SyntaxKind.ObjectBindingPattern, open, elements, close)
{
    public SeparatedSyntaxList<BindingElementSyntax> Elements => SL<BindingElementSyntax>(1);
}

public sealed class ArrayBindingPatternSyntax(SyntaxToken open, SyntaxNode elements, SyntaxToken close)
    : BindingPatternSyntax(SyntaxKind.ArrayBindingPattern, open, elements, close)
{
    public SeparatedSyntaxList<BindingElementSyntax> Elements => SL<BindingElementSyntax>(1);
}

/// <summary>
/// An element of a binding pattern. For object patterns: <c>[...] propertyName? : target = default</c>;
/// for array patterns the property name is absent. An omitted element has no target.
/// </summary>
public sealed class BindingElementSyntax(SyntaxToken? dotDotDot, SyntaxToken? propertyName, SyntaxToken? colon, SyntaxToken? identifier,
    BindingPatternSyntax? nested, EqualsValueClauseSyntax? initializer)
    : SyntaxNode(SyntaxKind.BindingElement, dotDotDot, propertyName, colon, identifier, nested, initializer)
{
    public SyntaxToken? DotDotDotToken => TokOpt(0);
    public SyntaxToken? PropertyName => TokOpt(1);
    public SyntaxToken? Identifier => TokOpt(3);
    public BindingPatternSyntax? NestedPattern => NodeOpt<BindingPatternSyntax>(4);
    public EqualsValueClauseSyntax? Initializer => NodeOpt<EqualsValueClauseSyntax>(5);
    public bool IsOmitted => Identifier == null && NestedPattern == null;
}

// ---------------------------------------------------------------- declarations

public sealed class ImportDeclarationSyntax(SyntaxToken importKeyword, SyntaxToken? typeKeyword, SyntaxNode? clause,
    SyntaxToken? fromKeyword, SyntaxToken moduleSpecifier, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.ImportDeclaration, importKeyword, typeKeyword, clause, fromKeyword, moduleSpecifier, semicolon)
{
    public SyntaxToken ImportKeyword => Tok(0);
    public SyntaxToken? TypeKeyword => TokOpt(1);
    /// <summary><see cref="NamedImportsSyntax"/>, <see cref="NamespaceImportSyntax"/>, or null for a wildcard namespace import.</summary>
    public SyntaxNode? Clause => NodeOpt<SyntaxNode>(2);
    public SyntaxToken ModuleSpecifier => Tok(4);
    public string ModuleName => ModuleSpecifier.ValueText;
}

public sealed class NamedImportsSyntax(SyntaxToken open, SyntaxNode specifiers, SyntaxToken close)
    : SyntaxNode(SyntaxKind.NamedImports, open, specifiers, close)
{
    public SeparatedSyntaxList<ImportSpecifierSyntax> Specifiers => SL<ImportSpecifierSyntax>(1);
}

public sealed class ImportSpecifierSyntax(SyntaxToken? typeKeyword, SyntaxToken name, SyntaxToken? asKeyword, SyntaxToken? alias)
    : SyntaxNode(SyntaxKind.ImportSpecifier, typeKeyword, name, asKeyword, alias)
{
    public SyntaxToken? TypeKeyword => TokOpt(0);
    public SyntaxToken NameToken => Tok(1);
    public SyntaxToken? AliasToken => TokOpt(3);
    public string LocalName => (AliasToken ?? NameToken).Text;
}

public sealed class NamespaceImportSyntax(SyntaxToken star, SyntaxToken asKeyword, SyntaxToken alias)
    : SyntaxNode(SyntaxKind.NamespaceImport, star, asKeyword, alias)
{
    public SyntaxToken AliasToken => Tok(2);
}

public sealed class FunctionDeclarationSyntax(SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken functionKeyword, SyntaxToken? asterisk,
    SyntaxToken identifier, TypeParameterListSyntax? typeParams, ParameterListSyntax parameters, TypeAnnotationSyntax? returnType,
    BlockSyntax? body, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.FunctionDeclaration, decorators, modifiers, functionKeyword, asterisk, identifier, typeParams, parameters, returnType, body, semicolon)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public SyntaxToken? AsteriskToken => TokOpt(3);
    public SyntaxToken Identifier => Tok(4);
    public TypeParameterListSyntax? TypeParameters => NodeOpt<TypeParameterListSyntax>(5);
    public ParameterListSyntax Parameters => Node<ParameterListSyntax>(6);
    public TypeAnnotationSyntax? ReturnType => NodeOpt<TypeAnnotationSyntax>(7);
    public BlockSyntax? Body => NodeOpt<BlockSyntax>(8);
}

public sealed class HeritageClauseSyntax(SyntaxToken keyword, SyntaxNode types)
    : SyntaxNode(SyntaxKind.HeritageClause, keyword, types)
{
    public SyntaxToken Keyword => Tok(0);
    public SeparatedSyntaxList<TypeSyntax> Types => SL<TypeSyntax>(1);
}

public sealed class ClassDeclarationSyntax(SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken classKeyword, SyntaxToken identifier,
    TypeParameterListSyntax? typeParams, HeritageClauseSyntax? extendsClause, HeritageClauseSyntax? implementsClause,
    SyntaxToken open, SyntaxNode members, SyntaxToken close)
    : StatementSyntax(SyntaxKind.ClassDeclaration, decorators, modifiers, classKeyword, identifier, typeParams, extendsClause, implementsClause, open, members, close)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public SyntaxToken Identifier => Tok(3);
    public TypeParameterListSyntax? TypeParameters => NodeOpt<TypeParameterListSyntax>(4);
    public HeritageClauseSyntax? ExtendsClause => NodeOpt<HeritageClauseSyntax>(5);
    public HeritageClauseSyntax? ImplementsClause => NodeOpt<HeritageClauseSyntax>(6);
    public SyntaxList<MemberDeclarationSyntax> Members => L<MemberDeclarationSyntax>(8);
}

public sealed class InterfaceDeclarationSyntax(SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken interfaceKeyword, SyntaxToken identifier,
    TypeParameterListSyntax? typeParams, HeritageClauseSyntax? extendsClause, SyntaxToken open, SyntaxNode members, SyntaxToken close)
    : StatementSyntax(SyntaxKind.InterfaceDeclaration, decorators, modifiers, interfaceKeyword, identifier, typeParams, extendsClause, open, members, close)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public SyntaxToken Identifier => Tok(3);
    public TypeParameterListSyntax? TypeParameters => NodeOpt<TypeParameterListSyntax>(4);
    public HeritageClauseSyntax? ExtendsClause => NodeOpt<HeritageClauseSyntax>(5);
    public SyntaxList<MemberDeclarationSyntax> Members => L<MemberDeclarationSyntax>(7);
}

public sealed class EnumDeclarationSyntax(SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken enumKeyword, SyntaxToken identifier,
    SyntaxToken open, SyntaxNode members, SyntaxToken close)
    : StatementSyntax(SyntaxKind.EnumDeclaration, decorators, modifiers, enumKeyword, identifier, open, members, close)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public SyntaxToken Identifier => Tok(3);
    public SeparatedSyntaxList<EnumMemberSyntax> Members => SL<EnumMemberSyntax>(5);
}

public sealed class EnumMemberSyntax(SyntaxToken identifier, EqualsValueClauseSyntax? initializer)
    : SyntaxNode(SyntaxKind.EnumMember, identifier, initializer)
{
    public SyntaxToken Identifier => Tok(0);
    public EqualsValueClauseSyntax? Initializer => NodeOpt<EqualsValueClauseSyntax>(1);
}

public sealed class TypeAliasDeclarationSyntax(SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken typeKeyword, SyntaxToken identifier,
    TypeParameterListSyntax? typeParams, SyntaxToken equals, TypeSyntax type, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.TypeAliasDeclaration, decorators, modifiers, typeKeyword, identifier, typeParams, equals, type, semicolon)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public SyntaxToken Identifier => Tok(3);
    public TypeParameterListSyntax? TypeParameters => NodeOpt<TypeParameterListSyntax>(4);
    public TypeSyntax Type => Node<TypeSyntax>(6);
}

// ---- class / interface / object-type members

public sealed class PropertyDeclarationSyntax(SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken identifier, SyntaxToken? question,
    TypeAnnotationSyntax? type, EqualsValueClauseSyntax? initializer, SyntaxToken? separator)
    : MemberDeclarationSyntax(SyntaxKind.PropertyDeclaration, decorators, modifiers, identifier, question, type, initializer, separator)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public SyntaxToken Identifier => Tok(2);
    public SyntaxToken? QuestionToken => TokOpt(3);
    public TypeAnnotationSyntax? TypeAnnotation => NodeOpt<TypeAnnotationSyntax>(4);
    public EqualsValueClauseSyntax? Initializer => NodeOpt<EqualsValueClauseSyntax>(5);
}

public sealed class MethodDeclarationSyntax(SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken? asterisk, SyntaxToken identifier,
    SyntaxToken? question, TypeParameterListSyntax? typeParams, ParameterListSyntax parameters, TypeAnnotationSyntax? returnType,
    BlockSyntax? body, SyntaxToken? separator)
    : MemberDeclarationSyntax(SyntaxKind.MethodDeclaration, decorators, modifiers, asterisk, identifier, question, typeParams, parameters, returnType, body, separator)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public SyntaxToken? AsteriskToken => TokOpt(2);
    public SyntaxToken Identifier => Tok(3);
    public TypeParameterListSyntax? TypeParameters => NodeOpt<TypeParameterListSyntax>(5);
    public ParameterListSyntax Parameters => Node<ParameterListSyntax>(6);
    public TypeAnnotationSyntax? ReturnType => NodeOpt<TypeAnnotationSyntax>(7);
    public BlockSyntax? Body => NodeOpt<BlockSyntax>(8);
}

public sealed class ConstructorDeclarationSyntax(SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken keyword,
    ParameterListSyntax parameters, BlockSyntax? body, SyntaxToken? semicolon)
    : MemberDeclarationSyntax(SyntaxKind.ConstructorDeclaration, decorators, modifiers, keyword, parameters, body, semicolon)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public SyntaxToken Keyword => Tok(2);
    public ParameterListSyntax Parameters => Node<ParameterListSyntax>(3);
    public BlockSyntax? Body => NodeOpt<BlockSyntax>(4);
}

public sealed class AccessorDeclarationSyntax(SyntaxKind kind, SyntaxNode decorators, SyntaxNode modifiers, SyntaxToken accessorKeyword,
    SyntaxToken identifier, ParameterListSyntax parameters, TypeAnnotationSyntax? returnType, BlockSyntax? body, SyntaxToken? semicolon)
    : MemberDeclarationSyntax(kind, decorators, modifiers, accessorKeyword, identifier, parameters, returnType, body, semicolon)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public bool IsGetter => Kind == SyntaxKind.GetAccessorDeclaration;
    public SyntaxToken Identifier => Tok(3);
    public ParameterListSyntax Parameters => Node<ParameterListSyntax>(4);
    public TypeAnnotationSyntax? ReturnType => NodeOpt<TypeAnnotationSyntax>(5);
    public BlockSyntax? Body => NodeOpt<BlockSyntax>(6);
}

// ---------------------------------------------------------------- statements

public sealed class BlockSyntax(SyntaxToken open, SyntaxNode statements, SyntaxToken close)
    : StatementSyntax(SyntaxKind.Block, open, statements, close)
{
    public SyntaxToken OpenBraceToken => Tok(0);
    public SyntaxList<StatementSyntax> Statements => L<StatementSyntax>(1);
    public SyntaxToken CloseBraceToken => Tok(2);
}

public sealed class VariableStatementSyntax(SyntaxNode decorators, SyntaxNode modifiers, VariableDeclarationListSyntax declarations, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.VariableStatement, decorators, modifiers, declarations, semicolon)
{
    public SyntaxList<DecoratorSyntax> Decorators => L<DecoratorSyntax>(0);
    public SyntaxList<SyntaxToken> Modifiers => L<SyntaxToken>(1);
    public VariableDeclarationListSyntax Declaration => Node<VariableDeclarationListSyntax>(2);
}

public sealed class VariableDeclarationListSyntax(SyntaxToken? awaitKeyword, SyntaxToken keyword, SyntaxNode declarators)
    : SyntaxNode(SyntaxKind.VariableDeclarationList, awaitKeyword, keyword, declarators)
{
    public SyntaxToken? AwaitKeyword => TokOpt(0);
    /// <summary>let, const, var (error) or using.</summary>
    public SyntaxToken Keyword => Tok(1);
    public SeparatedSyntaxList<VariableDeclaratorSyntax> Declarators => SL<VariableDeclaratorSyntax>(2);
    public bool IsConst => Keyword.Kind == SyntaxKind.ConstKeyword;
    public bool IsUsing => Keyword.Kind == SyntaxKind.UsingKeyword;
}

public sealed class VariableDeclaratorSyntax(SyntaxToken? identifier, BindingPatternSyntax? pattern, TypeAnnotationSyntax? type, EqualsValueClauseSyntax? initializer)
    : SyntaxNode(SyntaxKind.VariableDeclarator, identifier, pattern, type, initializer)
{
    public SyntaxToken? Identifier => TokOpt(0);
    public BindingPatternSyntax? Pattern => NodeOpt<BindingPatternSyntax>(1);
    public TypeAnnotationSyntax? TypeAnnotation => NodeOpt<TypeAnnotationSyntax>(2);
    public EqualsValueClauseSyntax? Initializer => NodeOpt<EqualsValueClauseSyntax>(3);
}

public sealed class ExpressionStatementSyntax(ExpressionSyntax expression, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.ExpressionStatement, expression, semicolon)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(0);
}

public sealed class IfStatementSyntax(SyntaxToken ifKeyword, SyntaxToken open, ExpressionSyntax condition, SyntaxToken close,
    StatementSyntax thenStatement, SyntaxToken? elseKeyword, StatementSyntax? elseStatement)
    : StatementSyntax(SyntaxKind.IfStatement, ifKeyword, open, condition, close, thenStatement, elseKeyword, elseStatement)
{
    public ExpressionSyntax Condition => Node<ExpressionSyntax>(2);
    public StatementSyntax Then => Node<StatementSyntax>(4);
    public StatementSyntax? Else => NodeOpt<StatementSyntax>(6);
}

public sealed class ForStatementSyntax(SyntaxToken forKeyword, SyntaxToken open, SyntaxNode? initializer, SyntaxToken semi1,
    ExpressionSyntax? condition, SyntaxToken semi2, ExpressionSyntax? incrementor, SyntaxToken close, StatementSyntax body)
    : StatementSyntax(SyntaxKind.ForStatement, forKeyword, open, initializer, semi1, condition, semi2, incrementor, close, body)
{
    /// <summary>A <see cref="VariableDeclarationListSyntax"/> or an <see cref="ExpressionSyntax"/>.</summary>
    public SyntaxNode? Initializer => NodeOpt<SyntaxNode>(2);
    public ExpressionSyntax? Condition => NodeOpt<ExpressionSyntax>(4);
    public ExpressionSyntax? Incrementor => NodeOpt<ExpressionSyntax>(6);
    public StatementSyntax Body => Node<StatementSyntax>(8);
}

public sealed class ForOfStatementSyntax(SyntaxToken forKeyword, SyntaxToken? awaitKeyword, SyntaxToken open, SyntaxNode left,
    SyntaxToken ofKeyword, ExpressionSyntax expression, SyntaxToken close, StatementSyntax body)
    : StatementSyntax(SyntaxKind.ForOfStatement, forKeyword, awaitKeyword, open, left, ofKeyword, expression, close, body)
{
    public SyntaxToken? AwaitKeyword => TokOpt(1);
    /// <summary>A <see cref="VariableDeclarationListSyntax"/> or an assignment-target expression.</summary>
    public SyntaxNode Left => Node<SyntaxNode>(3);
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(5);
    public StatementSyntax Body => Node<StatementSyntax>(7);
}

public sealed class ForInStatementSyntax(SyntaxToken forKeyword, SyntaxToken open, SyntaxNode left, SyntaxToken inKeyword,
    ExpressionSyntax expression, SyntaxToken close, StatementSyntax body)
    : StatementSyntax(SyntaxKind.ForInStatement, forKeyword, open, left, inKeyword, expression, close, body)
{
    public SyntaxToken InKeyword => Tok(3);
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(4);
    public StatementSyntax Body => Node<StatementSyntax>(6);
}

public sealed class WhileStatementSyntax(SyntaxToken whileKeyword, SyntaxToken open, ExpressionSyntax condition, SyntaxToken close, StatementSyntax body)
    : StatementSyntax(SyntaxKind.WhileStatement, whileKeyword, open, condition, close, body)
{
    public ExpressionSyntax Condition => Node<ExpressionSyntax>(2);
    public StatementSyntax Body => Node<StatementSyntax>(4);
}

public sealed class DoStatementSyntax(SyntaxToken doKeyword, StatementSyntax body, SyntaxToken whileKeyword, SyntaxToken open,
    ExpressionSyntax condition, SyntaxToken close, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.DoStatement, doKeyword, body, whileKeyword, open, condition, close, semicolon)
{
    public StatementSyntax Body => Node<StatementSyntax>(1);
    public ExpressionSyntax Condition => Node<ExpressionSyntax>(4);
}

public sealed class ReturnStatementSyntax(SyntaxToken keyword, ExpressionSyntax? expression, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.ReturnStatement, keyword, expression, semicolon)
{
    public SyntaxToken Keyword => Tok(0);
    public ExpressionSyntax? Expression => NodeOpt<ExpressionSyntax>(1);
}

public sealed class BreakStatementSyntax(SyntaxToken keyword, SyntaxToken? label, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.BreakStatement, keyword, label, semicolon)
{
    public SyntaxToken? Label => TokOpt(1);
}

public sealed class ContinueStatementSyntax(SyntaxToken keyword, SyntaxToken? label, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.ContinueStatement, keyword, label, semicolon)
{
    public SyntaxToken? Label => TokOpt(1);
}

public sealed class ThrowStatementSyntax(SyntaxToken keyword, ExpressionSyntax? expression, SyntaxToken? semicolon)
    : StatementSyntax(SyntaxKind.ThrowStatement, keyword, expression, semicolon)
{
    public SyntaxToken Keyword => Tok(0);
    public ExpressionSyntax? Expression => NodeOpt<ExpressionSyntax>(1);
}

public sealed class TryStatementSyntax(SyntaxToken tryKeyword, BlockSyntax block, SyntaxNode catches, FinallyClauseSyntax? finallyClause)
    : StatementSyntax(SyntaxKind.TryStatement, tryKeyword, block, catches, finallyClause)
{
    public BlockSyntax Block => Node<BlockSyntax>(1);
    public SyntaxList<CatchClauseSyntax> Catches => L<CatchClauseSyntax>(2);
    public FinallyClauseSyntax? Finally => NodeOpt<FinallyClauseSyntax>(3);
}

public sealed class CatchClauseSyntax(SyntaxToken catchKeyword, SyntaxToken? open, SyntaxToken? identifier, TypeAnnotationSyntax? type,
    SyntaxToken? close, BlockSyntax block)
    : SyntaxNode(SyntaxKind.CatchClause, catchKeyword, open, identifier, type, close, block)
{
    public SyntaxToken? Identifier => TokOpt(2);
    public TypeAnnotationSyntax? TypeAnnotation => NodeOpt<TypeAnnotationSyntax>(3);
    public BlockSyntax Block => Node<BlockSyntax>(5);
}

public sealed class FinallyClauseSyntax(SyntaxToken keyword, BlockSyntax block) : SyntaxNode(SyntaxKind.FinallyClause, keyword, block)
{
    public BlockSyntax Block => Node<BlockSyntax>(1);
}

public sealed class SwitchStatementSyntax(SyntaxToken keyword, SyntaxToken open, ExpressionSyntax expression, SyntaxToken close,
    SyntaxToken openBrace, SyntaxNode sections, SyntaxToken closeBrace)
    : StatementSyntax(SyntaxKind.SwitchStatement, keyword, open, expression, close, openBrace, sections, closeBrace)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(2);
    public SyntaxList<SwitchSectionSyntax> Sections => L<SwitchSectionSyntax>(5);
}

public sealed class SwitchSectionSyntax(SyntaxNode labels, SyntaxNode statements)
    : SyntaxNode(SyntaxKind.SwitchSection, labels, statements)
{
    public SyntaxList<SwitchLabelSyntax> Labels => L<SwitchLabelSyntax>(0);
    public SyntaxList<StatementSyntax> Statements => L<StatementSyntax>(1);
}

public sealed class SwitchLabelSyntax(SyntaxKind kind, SyntaxToken keyword, ExpressionSyntax? value, SyntaxToken colon)
    : SyntaxNode(kind, keyword, value, colon)
{
    public bool IsDefault => Kind == SyntaxKind.DefaultSwitchLabel;
    public ExpressionSyntax? Value => NodeOpt<ExpressionSyntax>(1);
}

public sealed class LabeledStatementSyntax(SyntaxToken label, SyntaxToken colon, StatementSyntax statement)
    : StatementSyntax(SyntaxKind.LabeledStatement, label, colon, statement)
{
    public SyntaxToken Label => Tok(0);
    public StatementSyntax Statement => Node<StatementSyntax>(2);
}

public sealed class EmptyStatementSyntax(SyntaxToken semicolon) : StatementSyntax(SyntaxKind.EmptyStatement, semicolon);

public sealed class MissingStatementSyntax(SyntaxToken missing) : StatementSyntax(SyntaxKind.MissingStatement, missing);

// ---------------------------------------------------------------- expressions

public sealed class LiteralExpressionSyntax(SyntaxKind kind, SyntaxToken token) : LiteralLikeExpressionSyntax(kind, token)
{
    public SyntaxToken Token => Tok(0);
}

public sealed class TemplateExpressionSyntax(SyntaxToken head, SyntaxNode spans) : ExpressionSyntax(SyntaxKind.TemplateExpression, head, spans)
{
    public SyntaxToken Head => Tok(0);
    public SyntaxList<TemplateSpanSyntax> Spans => L<TemplateSpanSyntax>(1);
}

public sealed class TemplateSpanSyntax(ExpressionSyntax expression, SyntaxToken literal)
    : SyntaxNode(SyntaxKind.TemplateSpan, expression, literal)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(0);
    public SyntaxToken Literal => Tok(1);
}

public sealed class ThisExpressionSyntax(SyntaxToken keyword) : ExpressionSyntax(SyntaxKind.ThisExpression, keyword);
public sealed class SuperExpressionSyntax(SyntaxToken keyword) : ExpressionSyntax(SyntaxKind.SuperExpression, keyword);
public sealed class MissingExpressionSyntax(SyntaxToken missing) : ExpressionSyntax(SyntaxKind.MissingExpression, missing);

public sealed class ParenthesizedExpressionSyntax(SyntaxToken open, ExpressionSyntax expression, SyntaxToken close)
    : ExpressionSyntax(SyntaxKind.ParenthesizedExpression, open, expression, close)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(1);
}

public sealed class ArrayLiteralExpressionSyntax(SyntaxToken open, SyntaxNode elements, SyntaxToken close)
    : ExpressionSyntax(SyntaxKind.ArrayLiteralExpression, open, elements, close)
{
    public SeparatedSyntaxList<ExpressionSyntax> Elements => SL<ExpressionSyntax>(1);
}

public sealed class SpreadElementSyntax(SyntaxToken dotDotDot, ExpressionSyntax expression)
    : ExpressionSyntax(SyntaxKind.SpreadElement, dotDotDot, expression)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(1);
}

public sealed class ObjectLiteralExpressionSyntax(SyntaxToken open, SyntaxNode members, SyntaxToken close)
    : ExpressionSyntax(SyntaxKind.ObjectLiteralExpression, open, members, close)
{
    public SeparatedSyntaxList<SyntaxNode> Members => SL<SyntaxNode>(1);
}

public sealed class PropertyAssignmentSyntax(SyntaxToken name, SyntaxToken colon, ExpressionSyntax value)
    : SyntaxNode(SyntaxKind.PropertyAssignment, name, colon, value)
{
    public SyntaxToken NameToken => Tok(0);
    public ExpressionSyntax Value => Node<ExpressionSyntax>(2);
}

public sealed class ShorthandPropertyAssignmentSyntax(SyntaxToken name) : SyntaxNode(SyntaxKind.ShorthandPropertyAssignment, name)
{
    public SyntaxToken NameToken => Tok(0);
}

public sealed class SpreadAssignmentSyntax(SyntaxToken dotDotDot, ExpressionSyntax expression)
    : SyntaxNode(SyntaxKind.SpreadAssignment, dotDotDot, expression)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(1);
}

public sealed class FunctionExpressionSyntax(SyntaxToken? asyncKeyword, SyntaxToken functionKeyword, SyntaxToken? asterisk, SyntaxToken? identifier,
    TypeParameterListSyntax? typeParams, ParameterListSyntax parameters, TypeAnnotationSyntax? returnType, BlockSyntax body)
    : ExpressionSyntax(SyntaxKind.FunctionExpression, asyncKeyword, functionKeyword, asterisk, identifier, typeParams, parameters, returnType, body)
{
    public SyntaxToken? AsyncKeyword => TokOpt(0);
    public SyntaxToken? AsteriskToken => TokOpt(2);
    public ParameterListSyntax Parameters => Node<ParameterListSyntax>(5);
    public TypeAnnotationSyntax? ReturnType => NodeOpt<TypeAnnotationSyntax>(6);
    public BlockSyntax Body => Node<BlockSyntax>(7);
}

public sealed class ArrowFunctionSyntax(SyntaxToken? asyncKeyword, TypeParameterListSyntax? typeParams, ParameterListSyntax? parameters,
    SyntaxToken? singleParameter, TypeAnnotationSyntax? returnType, SyntaxToken arrow, SyntaxNode body)
    : ExpressionSyntax(SyntaxKind.ArrowFunction, asyncKeyword, typeParams, parameters, singleParameter, returnType, arrow, body)
{
    public SyntaxToken? AsyncKeyword => TokOpt(0);
    public TypeParameterListSyntax? TypeParameters => NodeOpt<TypeParameterListSyntax>(1);
    public ParameterListSyntax? Parameters => NodeOpt<ParameterListSyntax>(2);
    /// <summary>For the form <c>x =&gt; ...</c>.</summary>
    public SyntaxToken? SingleParameter => TokOpt(3);
    public TypeAnnotationSyntax? ReturnType => NodeOpt<TypeAnnotationSyntax>(4);
    /// <summary>A <see cref="BlockSyntax"/> or an <see cref="ExpressionSyntax"/>.</summary>
    public SyntaxNode Body => Node<SyntaxNode>(6);
}

public sealed class PrefixUnaryExpressionSyntax(SyntaxToken op, ExpressionSyntax operand)
    : ExpressionSyntax(SyntaxKind.PrefixUnaryExpression, op, operand)
{
    public SyntaxToken OperatorToken => Tok(0);
    public ExpressionSyntax Operand => Node<ExpressionSyntax>(1);
}

public sealed class PostfixUnaryExpressionSyntax(ExpressionSyntax operand, SyntaxToken op)
    : ExpressionSyntax(SyntaxKind.PostfixUnaryExpression, operand, op)
{
    public ExpressionSyntax Operand => Node<ExpressionSyntax>(0);
    public SyntaxToken OperatorToken => Tok(1);
}

public sealed class AwaitExpressionSyntax(SyntaxToken keyword, ExpressionSyntax operand)
    : ExpressionSyntax(SyntaxKind.AwaitExpression, keyword, operand)
{
    public ExpressionSyntax Operand => Node<ExpressionSyntax>(1);
}

public sealed class TypeOfExpressionSyntax(SyntaxToken keyword, SyntaxToken open, TypeSyntax type, SyntaxToken close)
    : ExpressionSyntax(SyntaxKind.TypeOfExpression, keyword, open, type, close)
{
    public TypeSyntax Type => Node<TypeSyntax>(2);
}

public sealed class YieldExpressionSyntax(SyntaxToken keyword, SyntaxToken? asterisk, ExpressionSyntax? operand)
    : ExpressionSyntax(SyntaxKind.YieldExpression, keyword, asterisk, operand)
{
    public bool IsDelegating => TokOpt(1) != null;
    public ExpressionSyntax? Operand => NodeOpt<ExpressionSyntax>(2);
}

public sealed class BinaryExpressionSyntax(ExpressionSyntax left, SyntaxToken op, ExpressionSyntax right)
    : ExpressionSyntax(SyntaxKind.BinaryExpression, left, op, right)
{
    public ExpressionSyntax Left => Node<ExpressionSyntax>(0);
    public SyntaxToken OperatorToken => Tok(1);
    public ExpressionSyntax Right => Node<ExpressionSyntax>(2);
    /// <summary>The effective operator kind (composite '>>' etc. are single tokens built by the parser).</summary>
    public SyntaxKind OperatorKind => OperatorToken.Kind;
}

public sealed class AssignmentExpressionSyntax(ExpressionSyntax left, SyntaxToken op, ExpressionSyntax right)
    : ExpressionSyntax(SyntaxKind.AssignmentExpression, left, op, right)
{
    public ExpressionSyntax Left => Node<ExpressionSyntax>(0);
    public SyntaxToken OperatorToken => Tok(1);
    public ExpressionSyntax Right => Node<ExpressionSyntax>(2);
}

public sealed class ConditionalExpressionSyntax(ExpressionSyntax condition, SyntaxToken question, ExpressionSyntax whenTrue,
    SyntaxToken colon, ExpressionSyntax whenFalse)
    : ExpressionSyntax(SyntaxKind.ConditionalExpression, condition, question, whenTrue, colon, whenFalse)
{
    public ExpressionSyntax Condition => Node<ExpressionSyntax>(0);
    public ExpressionSyntax WhenTrue => Node<ExpressionSyntax>(2);
    public ExpressionSyntax WhenFalse => Node<ExpressionSyntax>(4);
}

public sealed class ArgumentListSyntax(SyntaxToken open, SyntaxNode arguments, SyntaxToken close)
    : SyntaxNode(SyntaxKind.ArgumentList, open, arguments, close)
{
    public SeparatedSyntaxList<ExpressionSyntax> Arguments => SL<ExpressionSyntax>(1);
}

public sealed class InvocationExpressionSyntax(ExpressionSyntax expression, ArgumentListSyntax arguments)
    : ExpressionSyntax(SyntaxKind.InvocationExpression, expression, arguments)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(0);
    public ArgumentListSyntax Arguments => Node<ArgumentListSyntax>(1);
}

public sealed class NewExpressionSyntax(SyntaxToken newKeyword, TypeSyntax type, ArgumentListSyntax? arguments)
    : ExpressionSyntax(SyntaxKind.NewExpression, newKeyword, type, arguments)
{
    public TypeSyntax Type => Node<TypeSyntax>(1);
    public ArgumentListSyntax? Arguments => NodeOpt<ArgumentListSyntax>(2);
}

public sealed class ArrayCreationExpressionSyntax(SyntaxToken newKeyword, TypeSyntax elementType, SyntaxToken open, ExpressionSyntax size, SyntaxToken close)
    : ExpressionSyntax(SyntaxKind.ArrayCreationExpression, newKeyword, elementType, open, size, close)
{
    public TypeSyntax ElementType => Node<TypeSyntax>(1);
    public ExpressionSyntax Size => Node<ExpressionSyntax>(3);
}

public sealed class MemberAccessExpressionSyntax(ExpressionSyntax expression, SyntaxToken operatorToken, SimpleNameSyntax name)
    : ExpressionSyntax(SyntaxKind.MemberAccessExpression, expression, operatorToken, name)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(0);
    public SyntaxToken OperatorToken => Tok(1);
    public bool IsConditional => OperatorToken.Kind == SyntaxKind.QuestionDotToken;
    public SimpleNameSyntax Name => Node<SimpleNameSyntax>(2);
}

public sealed class ElementAccessExpressionSyntax(ExpressionSyntax expression, SyntaxToken? questionDot, SyntaxToken open, SyntaxNode arguments, SyntaxToken close)
    : ExpressionSyntax(SyntaxKind.ElementAccessExpression, expression, questionDot, open, arguments, close)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(0);
    public bool IsConditional => TokOpt(1) != null;
    public SeparatedSyntaxList<ExpressionSyntax> Arguments => SL<ExpressionSyntax>(3);
}

public sealed class NonNullExpressionSyntax(ExpressionSyntax expression, SyntaxToken bang)
    : ExpressionSyntax(SyntaxKind.NonNullExpression, expression, bang)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(0);
}

public sealed class AsExpressionSyntax(ExpressionSyntax expression, SyntaxToken asKeyword, TypeSyntax type)
    : ExpressionSyntax(SyntaxKind.AsExpression, expression, asKeyword, type)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(0);
    public TypeSyntax Type => Node<TypeSyntax>(2);
}

public sealed class SatisfiesExpressionSyntax(ExpressionSyntax expression, SyntaxToken keyword, TypeSyntax type)
    : ExpressionSyntax(SyntaxKind.SatisfiesExpression, expression, keyword, type)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(0);
    public TypeSyntax Type => Node<TypeSyntax>(2);
}

public sealed class InstanceOfExpressionSyntax(ExpressionSyntax expression, SyntaxToken keyword, TypeSyntax type)
    : ExpressionSyntax(SyntaxKind.InstanceOfExpression, expression, keyword, type)
{
    public ExpressionSyntax Expression => Node<ExpressionSyntax>(0);
    public TypeSyntax Type => Node<TypeSyntax>(2);
}
