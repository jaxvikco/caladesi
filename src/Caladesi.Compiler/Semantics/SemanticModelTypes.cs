using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Semantics;

public enum DocumentSymbolKind { Module, Namespace, Class, Interface, Enum, EnumMember, Struct, Function, Method, Constructor, Property, Field, Variable, Constant, TypeAlias, TypeParameter }

public sealed record DocumentSymbolInfo(string Name, string? Detail, DocumentSymbolKind Kind, TextSpan Span, TextSpan SelectionSpan, IReadOnlyList<DocumentSymbolInfo> Children);

public enum CompletionKind { Keyword, Variable, Constant, Function, Method, Property, Field, Class, Interface, Enum, EnumMember, Struct, Module, Namespace, TypeParameter, TypeAlias, Snippet }

public sealed record CompletionEntry(string Label, CompletionKind Kind, string? Detail, string? InsertText = null);

public sealed record SignatureEntry(string Label, IReadOnlyList<string> Parameters);

public sealed record SignatureHelpInfo(IReadOnlyList<SignatureEntry> Signatures, int ActiveSignature, int ActiveParameter);

/// <summary>A use or declaration of a symbol: the identifier span in a tree.</summary>
public readonly record struct SymbolReference(SyntaxTree Tree, TextSpan Span, bool IsDeclaration);

/// <summary>The symbol under a position and the identifier's span.</summary>
public sealed record SymbolAt(Symbol Symbol, TextSpan Span, bool IsDeclaration);

public enum SemanticTokenType { Namespace, Type, Class, Enum, Interface, Struct, TypeParameter, Parameter, Variable, Property, EnumMember, Function, Method, Decorator }

[Flags]
public enum SemanticTokenModifiers { None = 0, Declaration = 1, Readonly = 2, Static = 4 }

public readonly record struct SemanticTokenInfo(TextSpan Span, SemanticTokenType Type, SemanticTokenModifiers Modifiers);
