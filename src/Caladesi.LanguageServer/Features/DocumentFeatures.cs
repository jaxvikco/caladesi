using System.Text.Json;
using Caladesi.Compiler;
using Caladesi.Compiler.Formatting;
using Caladesi.Compiler.Semantics;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;
using Caladesi.LanguageServer.Projects;
using Caladesi.LanguageServer.Protocol;

namespace Caladesi.LanguageServer;

internal sealed partial class LanguageServer
{
    private sealed record DocumentContext(OpenDocument Document, CompilationUnit Unit, Compilation Compilation, SyntaxTree Tree)
    {
        public SourceText Text => Tree.Text;
        public SemanticModel Model => Unit.GetModel(Compilation, Tree);
    }

    private DocumentContext? GetContext(JsonElement p)
    {
        if (p.Path("textDocument", "uri").GetString() is not { } uri)
        {
            return null;
        }

        if (_workspace.GetDocument(uri) is not { } document || _workspace.GetUnit(uri) is not { } unit)
        {
            return null;
        }

        Compilation compilation = unit.GetCompilation(_workspace);
        SyntaxTree? tree = compilation.SyntaxTrees.FirstOrDefault(t => string.Equals(t.FilePath, document.Path, StringComparison.Ordinal));
        return tree == null ? null : new DocumentContext(document, unit, compilation, tree);
    }

    private static LinePosition ReadPosition(JsonElement? position)
    {
        return new(Math.Max(0, position.Get("line").GetInt() ?? 0), Math.Max(0, position.Get("character").GetInt() ?? 0));
    }

    private static int ReadOffset(DocumentContext context, JsonElement p)
    {
        return context.Text.GetPosition(ReadPosition(p.Get("position")));
    }

    private static LinePositionSpan ToRange(SyntaxTree tree, TextSpan span)
    {
        return tree.Text.GetLinePositionSpan(span);
    }

    private void WriteLocation(Utf8JsonWriter w, SymbolReference reference)
    {
        w.WriteStartObject();
        w.WriteString("uri", _workspace.PathToUri(reference.Tree.FilePath));
        w.WriteRange("range", ToRange(reference.Tree, reference.Span));
        w.WriteEndObject();
    }

    // ------------------------------------------------------------------ hover / navigation

    private Action<Utf8JsonWriter>? Hover(JsonElement p)
    {
        if (GetContext(p) is not { } c)
        {
            return null;
        }

        SemanticModel model = c.Model;
        int offset = ReadOffset(c, p);
        string? text = model.GetHover(offset);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        SymbolAt? at = model.GetSymbolAt(offset);
        return w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("contents");
            w.WriteStartObject();
            w.WriteString("kind", "markdown");
            w.WriteString("value", "```caladesi\n" + text + "\n```");
            w.WriteEndObject();
            if (at != null)
            {
                w.WriteRange("range", ToRange(c.Tree, at.Span));
            }

            w.WriteEndObject();
        };
    }

    private Action<Utf8JsonWriter>? Definition(JsonElement p)
    {
        if (GetContext(p) is not { } c)
        {
            return null;
        }

        SemanticModel model = c.Model;
        return model.GetSymbolAt(ReadOffset(c, p)) is not { } at
            ? null
            : model.GetDefinition(at.Symbol) is not { } definition ? null : (w => WriteLocation(w, definition));
    }

    private Action<Utf8JsonWriter>? References(JsonElement p)
    {
        if (GetContext(p) is not { } c)
        {
            return null;
        }

        SemanticModel model = c.Model;
        if (model.GetSymbolAt(ReadOffset(c, p)) is not { } at)
        {
            return null;
        }

        bool includeDeclaration = p.Path("context", "includeDeclaration").GetBool(true);
        IReadOnlyList<SymbolReference> references = ImportReferences.Find(model, at.Symbol, includeDeclaration);
        return w =>
        {
            w.WriteStartArray();
            foreach (SymbolReference r in references)
            {
                WriteLocation(w, r);
            }

            w.WriteEndArray();
        };
    }

    // ------------------------------------------------------------------ rename

    private static bool IsRenameable(SemanticModel model, SymbolAt at)
    {
        return model.GetDefinition(at.Symbol) != null;
    }

    private Action<Utf8JsonWriter>? PrepareRename(JsonElement p)
    {
        if (GetContext(p) is not { } c)
        {
            return null;
        }

        SemanticModel model = c.Model;
        if (model.GetSymbolAt(ReadOffset(c, p)) is not { } at || !IsRenameable(model, at))
        {
            return null;
        }

        string placeholder = c.Text.ToString(at.Span);
        return w =>
        {
            w.WriteStartObject();
            w.WriteRange("range", ToRange(c.Tree, at.Span));
            w.WriteString("placeholder", placeholder);
            w.WriteEndObject();
        };
    }

    private Action<Utf8JsonWriter>? Rename(JsonElement p)
    {
        if (GetContext(p) is not { } c)
        {
            return null;
        }

        string? newName = p.Get("newName").GetString();
        if (!IsValidIdentifier(newName))
        {
            throw new RpcException(InvalidParams, $"'{newName}' is not a valid identifier");
        }

        SemanticModel model = c.Model;
        if (model.GetSymbolAt(ReadOffset(c, p)) is not { } at)
        {
            throw new RpcException(InvalidParams, "There is no symbol to rename at this position");
        }

        if (!IsRenameable(model, at))
        {
            throw new RpcException(InvalidParams, "Only symbols declared in source can be renamed");
        }

        string oldName = c.Text.ToString(at.Span);
        SortedDictionary<string, List<(SyntaxTree Tree, TextSpan Span)>> edits = new(StringComparer.Ordinal);
        HashSet<(string, int)> seen = [];
        foreach (SymbolReference reference in ImportReferences.Find(model, at.Symbol, includeDeclaration: true))
        {
            string path = reference.Tree.FilePath;
            if (!_workspace.IsWorkspaceFile(path))
            {
                continue;
            }

            if (reference.Span.End > reference.Tree.Text.Length || reference.Tree.Text.ToString(reference.Span) != oldName)
            {
                continue;
            }

            if (!seen.Add((path, reference.Span.Start)))
            {
                continue;
            }

            string uri = _workspace.PathToUri(path);
            if (!edits.TryGetValue(uri, out List<(SyntaxTree Tree, TextSpan Span)>? list))
            {
                edits[uri] = list = [];
            }

            list.Add((reference.Tree, reference.Span));
        }
        return w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("changes");
            w.WriteStartObject();
            foreach ((string? uri, List<(SyntaxTree Tree, TextSpan Span)>? list) in edits)
            {
                w.WritePropertyName(uri);
                w.WriteStartArray();
                foreach ((SyntaxTree? tree, TextSpan span) in list)
                {
                    w.WriteStartObject();
                    w.WriteRange("range", ToRange(tree, span));
                    w.WriteString("newText", newName);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        };
    }

    private static bool IsValidIdentifier([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (!(char.IsLetter(name[0]) || name[0] is '_' or '$'))
        {
            return false;
        }

        foreach (char ch in name)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '$'))
            {
                return false;
            }
        }

        SyntaxKind kind = SyntaxFacts.GetKeywordKind(name);
        return kind == SyntaxKind.IdentifierToken || !SyntaxFacts.IsReservedKeyword(kind);
    }

    // ------------------------------------------------------------------ completion / signature help

    private static int CompletionItemKind(CompletionKind kind)
    {
        return kind switch
        {
            CompletionKind.Keyword => 14,
            CompletionKind.Variable => 6,
            CompletionKind.Constant => 21,
            CompletionKind.Function => 3,
            CompletionKind.Method => 2,
            CompletionKind.Property => 10,
            CompletionKind.Field => 5,
            CompletionKind.Class => 7,
            CompletionKind.Interface => 8,
            CompletionKind.Enum => 13,
            CompletionKind.EnumMember => 20,
            CompletionKind.Struct => 22,
            CompletionKind.Module => 9,
            CompletionKind.Namespace => 9,
            CompletionKind.TypeParameter => 25,
            CompletionKind.TypeAlias => 7,
            CompletionKind.Snippet => 15,
            _ => 1,
        };
    }

    private Action<Utf8JsonWriter>? Completion(JsonElement p)
    {
        if (GetContext(p) is not { } c)
        {
            return null;
        }

        IReadOnlyList<CompletionEntry> entries = c.Model.GetCompletions(ReadOffset(c, p));
        return w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("isIncomplete", false);
            w.WritePropertyName("items");
            w.WriteStartArray();
            foreach (CompletionEntry entry in entries)
            {
                w.WriteStartObject();
                w.WriteString("label", entry.Label);
                w.WriteNumber("kind", CompletionItemKind(entry.Kind));
                if (entry.Detail != null)
                {
                    w.WriteString("detail", entry.Detail);
                }

                if (entry.InsertText != null)
                {
                    w.WriteString("insertText", entry.InsertText);
                    if (entry.Kind == CompletionKind.Snippet)
                    {
                        w.WriteNumber("insertTextFormat", 2);
                    }
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        };
    }

    private Action<Utf8JsonWriter>? SignatureHelp(JsonElement p)
    {
        return GetContext(p) is not { } c
            ? null
            : c.Model.GetSignatureHelp(ReadOffset(c, p)) is not { } help
            ? null
            : (w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("signatures");
            w.WriteStartArray();
            foreach (SignatureEntry signature in help.Signatures)
            {
                w.WriteStartObject();
                w.WriteString("label", signature.Label);
                w.WritePropertyName("parameters");
                w.WriteStartArray();
                foreach (string parameter in signature.Parameters)
                {
                    w.WriteStartObject();
                    w.WriteString("label", parameter);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("activeSignature", help.ActiveSignature);
            w.WriteNumber("activeParameter", help.ActiveParameter);
            w.WriteEndObject();
        });
    }

    // ------------------------------------------------------------------ outline / tokens / formatting

    private static int SymbolKindNumber(DocumentSymbolKind kind)
    {
        return kind switch
        {
            DocumentSymbolKind.Module => 2,
            DocumentSymbolKind.Namespace => 3,
            DocumentSymbolKind.Class => 5,
            DocumentSymbolKind.Method => 6,
            DocumentSymbolKind.Property => 7,
            DocumentSymbolKind.Field => 8,
            DocumentSymbolKind.Constructor => 9,
            DocumentSymbolKind.Enum => 10,
            DocumentSymbolKind.Interface => 11,
            DocumentSymbolKind.Function => 12,
            DocumentSymbolKind.Variable => 13,
            DocumentSymbolKind.Constant => 14,
            DocumentSymbolKind.EnumMember => 22,
            DocumentSymbolKind.Struct => 23,
            DocumentSymbolKind.TypeParameter => 26,
            DocumentSymbolKind.TypeAlias => 5,
            _ => 13,
        };
    }

    private Action<Utf8JsonWriter>? DocumentSymbols(JsonElement p)
    {
        if (GetContext(p) is not { } c)
        {
            return null;
        }

        IReadOnlyList<DocumentSymbolInfo> symbols = DocumentOutline.Compute(c.Tree);
        return w =>
        {
            w.WriteStartArray();
            foreach (DocumentSymbolInfo s in symbols)
            {
                WriteDocumentSymbol(w, c.Tree, s);
            }

            w.WriteEndArray();
        };
    }

    private static void WriteDocumentSymbol(Utf8JsonWriter w, SyntaxTree tree, DocumentSymbolInfo symbol)
    {
        LinePositionSpan range = ToRange(tree, symbol.Span);
        LinePositionSpan selection = symbol.SelectionSpan.Start >= symbol.Span.Start && symbol.SelectionSpan.End <= symbol.Span.End
            ? ToRange(tree, symbol.SelectionSpan)
            : range;
        w.WriteStartObject();
        w.WriteString("name", string.IsNullOrEmpty(symbol.Name) ? "<anonymous>" : symbol.Name);
        if (!string.IsNullOrEmpty(symbol.Detail))
        {
            w.WriteString("detail", symbol.Detail);
        }

        w.WriteNumber("kind", SymbolKindNumber(symbol.Kind));
        w.WriteRange("range", range);
        w.WriteRange("selectionRange", selection);
        if (symbol.Children.Count > 0)
        {
            w.WritePropertyName("children");
            w.WriteStartArray();
            foreach (DocumentSymbolInfo child in symbol.Children)
            {
                WriteDocumentSymbol(w, tree, child);
            }

            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private Action<Utf8JsonWriter>? SemanticTokensFull(JsonElement p)
    {
        if (GetContext(p) is not { } c)
        {
            return null;
        }

        List<SemanticTokenInfo> tokens = [.. c.Model.GetSemanticTokens().OrderBy(t => t.Span.Start)];
        List<int> data = new(tokens.Count * 5);
        int previousLine = 0;
        int previousCharacter = 0;
        int previousEnd = -1;
        foreach (SemanticTokenInfo token in tokens)
        {
            if (token.Span.Start < previousEnd || token.Span.Length == 0)
            {
                continue;
            }

            LinePosition position = c.Text.GetLinePosition(token.Span.Start);
            int deltaLine = position.Line - previousLine;
            int deltaStart = deltaLine == 0 ? position.Character - previousCharacter : position.Character;
            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(token.Span.Length);
            data.Add((int)token.Type);
            data.Add((int)token.Modifiers);
            previousLine = position.Line;
            previousCharacter = position.Character;
            previousEnd = token.Span.End;
        }
        return w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("data");
            w.WriteStartArray();
            foreach (int n in data)
            {
                w.WriteNumberValue(n);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        };
    }

    private Action<Utf8JsonWriter>? Formatting(JsonElement p)
    {
        if (GetContext(p) is not { } c)
        {
            return null;
        }

        int tabSize = p.Path("options", "tabSize").GetInt() is { } n && n is > 0 and <= 16 ? n : 2;
        string original = c.Text.Text;
        string formatted = Formatter.Format(SyntaxTree.ParseText(original, c.Document.Path), tabSize);
        if (string.Equals(original, formatted, StringComparison.Ordinal))
        {
            return w => { w.WriteStartArray(); w.WriteEndArray(); };
        }

        LinePosition end = c.Text.GetLinePosition(original.Length);
        return w =>
        {
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteRange("range", new LinePositionSpan(new LinePosition(0, 0), end));
            w.WriteString("newText", formatted);
            w.WriteEndObject();
            w.WriteEndArray();
        };
    }
}
