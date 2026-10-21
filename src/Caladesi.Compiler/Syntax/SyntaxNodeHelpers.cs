namespace Caladesi.Compiler.Syntax;

public abstract partial class SyntaxNode
{
    protected T Node<T>(int i) where T : SyntaxNode => (T)Slot(i)!;
    protected T? NodeOpt<T>(int i) where T : SyntaxNode => (T?)Slot(i);
    protected SyntaxToken Tok(int i) => (SyntaxToken)Slot(i)!;
    protected SyntaxToken? TokOpt(int i) => (SyntaxToken?)Slot(i);
    protected SyntaxList<T> L<T>(int i) where T : SyntaxElement => new((SyntaxNode?)Slot(i));
    protected SeparatedSyntaxList<T> SL<T>(int i) where T : SyntaxElement => new((SyntaxNode?)Slot(i));
}
