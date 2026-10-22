using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Semantics;

/// <summary>The outline of a file, computed from syntax alone (no binding needed).</summary>
public static class DocumentOutline
{
    public static IReadOnlyList<DocumentSymbolInfo> Compute(SyntaxTree tree)
    {
        var result = new List<DocumentSymbolInfo>();
        foreach (var s in tree.Root.Statements) AddStatement(s, result);
        return result;
    }

    private static TextSpan Sel(SyntaxToken? t, SyntaxNode n) => t is { IsMissing: false } ? t.Span : n.Span;

    private static void AddStatement(StatementSyntax s, List<DocumentSymbolInfo> into)
    {
        switch (s)
        {
            case FunctionDeclarationSyntax f when !f.Identifier.IsMissing:
                into.Add(new(f.Identifier.Text, Detail(f.Parameters, f.ReturnType), DocumentSymbolKind.Function, f.Span, f.Identifier.Span, []));
                break;
            case ClassDeclarationSyntax c when !c.Identifier.IsMissing:
                into.Add(new(c.Identifier.Text, null, DocumentSymbolKind.Class, c.Span, c.Identifier.Span, Members(c.Members)));
                break;
            case InterfaceDeclarationSyntax i when !i.Identifier.IsMissing:
                into.Add(new(i.Identifier.Text, null, DocumentSymbolKind.Interface, i.Span, i.Identifier.Span, Members(i.Members)));
                break;
            case EnumDeclarationSyntax e when !e.Identifier.IsMissing:
                into.Add(new(e.Identifier.Text, null, DocumentSymbolKind.Enum, e.Span, e.Identifier.Span,
                    e.Members.Where(m => !m.Identifier.IsMissing).Select(m => new DocumentSymbolInfo(m.Identifier.Text, null, DocumentSymbolKind.EnumMember, m.Span, m.Identifier.Span, [])).ToList()));
                break;
            case TypeAliasDeclarationSyntax t when !t.Identifier.IsMissing:
            {
                var isObject = t.Type is ObjectTypeSyntax;
                var children = t.Type is ObjectTypeSyntax o ? Members(o.Members) : [];
                into.Add(new(t.Identifier.Text, null, isObject ? DocumentSymbolKind.Struct : DocumentSymbolKind.TypeAlias, t.Span, t.Identifier.Span, children));
                break;
            }
            case VariableStatementSyntax v:
                foreach (var d in v.Declaration.Declarators)
                    if (d.Identifier is { IsMissing: false } id)
                        into.Add(new(id.Text, null, v.Declaration.IsConst ? DocumentSymbolKind.Constant : DocumentSymbolKind.Variable, d.Span, id.Span, []));
                break;
        }
    }

    private static string Detail(ParameterListSyntax ps, TypeAnnotationSyntax? ret) =>
        "(" + string.Join(", ", ps.Parameters.Select(p => p.Identifier?.Text ?? "_")) + ")" + (ret == null ? "" : ": " + ret.Type.ToFullString().Trim());

    private static List<DocumentSymbolInfo> Members(SyntaxList<MemberDeclarationSyntax> members)
    {
        var list = new List<DocumentSymbolInfo>();
        foreach (var m in members)
        {
            switch (m)
            {
                case MethodDeclarationSyntax md when !md.Identifier.IsMissing:
                    list.Add(new(md.Identifier.Text, Detail(md.Parameters, md.ReturnType), DocumentSymbolKind.Method, md.Span, md.Identifier.Span, []));
                    break;
                case PropertyDeclarationSyntax pd when !pd.Identifier.IsMissing:
                    list.Add(new(pd.Identifier.Text, pd.TypeAnnotation?.Type.ToFullString().Trim(), DocumentSymbolKind.Property, pd.Span, pd.Identifier.Span, []));
                    break;
                case ConstructorDeclarationSyntax cd:
                    list.Add(new("constructor", Detail(cd.Parameters, null), DocumentSymbolKind.Constructor, cd.Span, cd.Keyword.Span, []));
                    break;
                case AccessorDeclarationSyntax ad when !ad.Identifier.IsMissing:
                    list.Add(new(ad.Identifier.Text, ad.IsGetter ? "get" : "set", DocumentSymbolKind.Property, ad.Span, ad.Identifier.Span, []));
                    break;
            }
        }
        return list;
    }
}

public sealed partial class SemanticModel
{
    public IReadOnlyList<DocumentSymbolInfo> GetDocumentSymbols() => DocumentOutline.Compute(Tree);
}
