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
        var source = SourceText.From(text);
        var bag = new DiagnosticBag();
        var lexer = new Lexer(source, filePath, bag);
        var tokens = lexer.LexAll();
        var parser = new Parser(source, filePath, tokens, bag);
        var root = parser.ParseCompilationUnit();
        return new SyntaxTree(source, filePath, root, bag.ToImmutable());
    }

    public SyntaxTree WithText(string newText) => ParseText(newText, FilePath);
}
