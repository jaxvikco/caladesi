using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Syntax;

/// <summary>A parsed source file.</summary>
public sealed class SyntaxTree
{
    private SyntaxTree(SourceText text, string filePath, CompilationUnitSyntax root, ImmutableArray<Diagnostic> diagnostics)
    {
        Text = text;
        FilePath = filePath;
        Root = root;
        Diagnostics = diagnostics;
        root.OwnerTree = this;
    }

    public SourceText Text { get; }
    public string FilePath { get; }
    public CompilationUnitSyntax Root { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public static SyntaxTree ParseText(string text, string filePath = "")
    {
        SourceText source = SourceText.From(text);
        DiagnosticBag bag = new();
        Lexer lexer = new(source, filePath, bag);
        List<SyntaxToken> tokens = lexer.LexAll();
        Parser parser = new(filePath, tokens, bag);
        CompilationUnitSyntax root = parser.ParseCompilationUnit();
        return new SyntaxTree(source, filePath, root, bag.ToImmutable());
    }

    public SyntaxTree WithText(string newText)
    {
        return ParseText(newText, FilePath);
    }
}
