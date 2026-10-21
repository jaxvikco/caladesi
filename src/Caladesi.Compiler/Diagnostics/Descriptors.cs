namespace Caladesi.Compiler.Diagnostics;

/// <summary>The catalog of every diagnostic the compiler can produce. Codes are stable.</summary>
public static class Descriptors
{
    private static DiagnosticDescriptor E(int n, string msg) => new($"CAL{n:D4}", DiagnosticSeverity.Error, msg);
    private static DiagnosticDescriptor W(int n, string msg) => new($"CAL{n:D4}", DiagnosticSeverity.Warning, msg);

    // ---- Lexical / syntax (0001-0099)
    public static readonly DiagnosticDescriptor UnexpectedCharacter = E(1, "Unexpected character '{0}'.");
    public static readonly DiagnosticDescriptor UnterminatedString = E(2, "Unterminated string literal.");
    public static readonly DiagnosticDescriptor UnterminatedComment = E(3, "Unterminated comment.");
    public static readonly DiagnosticDescriptor UnterminatedTemplate = E(4, "Unterminated template literal.");
    public static readonly DiagnosticDescriptor InvalidEscape = E(5, "Invalid escape sequence.");
    public static readonly DiagnosticDescriptor InvalidNumber = E(6, "Invalid numeric literal: {0}");
    public static readonly DiagnosticDescriptor ExpectedToken = E(10, "'{0}' expected.");
    public static readonly DiagnosticDescriptor ExpectedIdentifier = E(11, "Identifier expected.");
    public static readonly DiagnosticDescriptor ExpectedExpression = E(12, "Expression expected.");
    public static readonly DiagnosticDescriptor ExpectedType = E(13, "Type expected.");
    public static readonly DiagnosticDescriptor ExpectedStatement = E(14, "Statement expected.");
    public static readonly DiagnosticDescriptor ExpectedDeclaration = E(15, "Declaration or statement expected.");
    public static readonly DiagnosticDescriptor UnexpectedToken = E(16, "Unexpected token '{0}'.");
    public static readonly DiagnosticDescriptor ExpectedMember = E(17, "Class member expected.");
    public static readonly DiagnosticDescriptor InvalidAssignmentTarget = E(18, "The left-hand side of an assignment must be a variable, property or element access.");
    public static readonly DiagnosticDescriptor ModifierNotAllowed = E(19, "Modifier '{0}' is not valid on this declaration.");
    public static readonly DiagnosticDescriptor DuplicateModifier = E(20, "Duplicate modifier '{0}'.");
    public static readonly DiagnosticDescriptor ForInNotSupported = E(21, "'for...in' is not supported; use 'for...of' over the keys, e.g. 'for (const k of dict.Keys)'.");
    public static readonly DiagnosticDescriptor VarNotSupported = E(22, "'var' is not supported; use 'let' or 'const'.");
    public static readonly DiagnosticDescriptor LooseEqualityNotSupported = E(23, "'{0}' is not supported; use '{1}'.");
    public static readonly DiagnosticDescriptor AnyNotSupported = E(24, "'any' is not supported; use a specific type, 'unknown' or 'object'.");
    public static readonly DiagnosticDescriptor UndefinedNotSupported = E(25, "'undefined' is not supported; use 'null'.");
    public static readonly DiagnosticDescriptor ExportDefaultNotSupported = E(26, "'export default' is not supported; use a named export.");
    public static readonly DiagnosticDescriptor NamespaceNotSupported = E(27, "Namespace declarations are not supported; the namespace is derived from the folder path.");
    public static readonly DiagnosticDescriptor FeatureNotSupported = E(28, "{0} is not supported.");
    public static readonly DiagnosticDescriptor RegexNotSupported = E(29, "Regular expression literals are not supported.");
}
