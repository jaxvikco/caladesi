namespace Caladesi.Compiler.Syntax;

public static class SyntaxFacts
{
    private static readonly Dictionary<string, SyntaxKind> s_keywords = new(StringComparer.Ordinal)
    {
        ["break"] = SyntaxKind.BreakKeyword, ["case"] = SyntaxKind.CaseKeyword, ["catch"] = SyntaxKind.CatchKeyword,
        ["class"] = SyntaxKind.ClassKeyword, ["const"] = SyntaxKind.ConstKeyword, ["continue"] = SyntaxKind.ContinueKeyword,
        ["default"] = SyntaxKind.DefaultKeyword, ["do"] = SyntaxKind.DoKeyword,
        ["else"] = SyntaxKind.ElseKeyword, ["enum"] = SyntaxKind.EnumKeyword, ["export"] = SyntaxKind.ExportKeyword,
        ["extends"] = SyntaxKind.ExtendsKeyword, ["false"] = SyntaxKind.FalseKeyword, ["finally"] = SyntaxKind.FinallyKeyword,
        ["for"] = SyntaxKind.ForKeyword, ["function"] = SyntaxKind.FunctionKeyword, ["if"] = SyntaxKind.IfKeyword,
        ["import"] = SyntaxKind.ImportKeyword, ["in"] = SyntaxKind.InKeyword, ["instanceof"] = SyntaxKind.InstanceOfKeyword,
        ["interface"] = SyntaxKind.InterfaceKeyword, ["let"] = SyntaxKind.LetKeyword, ["new"] = SyntaxKind.NewKeyword,
        ["null"] = SyntaxKind.NullKeyword, ["return"] = SyntaxKind.ReturnKeyword, ["super"] = SyntaxKind.SuperKeyword,
        ["switch"] = SyntaxKind.SwitchKeyword, ["this"] = SyntaxKind.ThisKeyword, ["throw"] = SyntaxKind.ThrowKeyword,
        ["true"] = SyntaxKind.TrueKeyword, ["try"] = SyntaxKind.TryKeyword, ["typeof"] = SyntaxKind.TypeOfKeyword,
        ["void"] = SyntaxKind.VoidKeyword, ["while"] = SyntaxKind.WhileKeyword,
        ["await"] = SyntaxKind.AwaitKeyword, ["yield"] = SyntaxKind.YieldKeyword,

        ["abstract"] = SyntaxKind.AbstractKeyword, ["as"] = SyntaxKind.AsKeyword, ["async"] = SyntaxKind.AsyncKeyword,
        ["from"] = SyntaxKind.FromKeyword, ["get"] = SyntaxKind.GetKeyword,
        ["implements"] = SyntaxKind.ImplementsKeyword, ["internal"] = SyntaxKind.InternalKeyword, ["of"] = SyntaxKind.OfKeyword,
        ["out"] = SyntaxKind.OutKeyword, ["override"] = SyntaxKind.OverrideKeyword, ["private"] = SyntaxKind.PrivateKeyword,
        ["protected"] = SyntaxKind.ProtectedKeyword, ["public"] = SyntaxKind.PublicKeyword, ["readonly"] = SyntaxKind.ReadonlyKeyword,
        ["satisfies"] = SyntaxKind.SatisfiesKeyword, ["sealed"] = SyntaxKind.SealedKeyword, ["set"] = SyntaxKind.SetKeyword,
        ["static"] = SyntaxKind.StaticKeyword, ["struct"] = SyntaxKind.StructKeyword, ["type"] = SyntaxKind.TypeKeyword,
        ["using"] = SyntaxKind.UsingKeyword,
        ["constructor"] = SyntaxKind.ConstructorKeyword, ["operator"] = SyntaxKind.OperatorKeyword, ["is"] = SyntaxKind.IsKeyword,
    };

    private static readonly Dictionary<SyntaxKind, string> s_texts = BuildTexts();

    private static Dictionary<SyntaxKind, string> BuildTexts()
    {
        var d = new Dictionary<SyntaxKind, string>();
        foreach (var kv in s_keywords) d[kv.Value] = kv.Key;
        void P(SyntaxKind k, string t) => d[k] = t;
        P(SyntaxKind.OpenBraceToken, "{"); P(SyntaxKind.CloseBraceToken, "}"); P(SyntaxKind.OpenParenToken, "(");
        P(SyntaxKind.CloseParenToken, ")"); P(SyntaxKind.OpenBracketToken, "["); P(SyntaxKind.CloseBracketToken, "]");
        P(SyntaxKind.DotToken, "."); P(SyntaxKind.DotDotDotToken, "..."); P(SyntaxKind.SemicolonToken, ";");
        P(SyntaxKind.CommaToken, ","); P(SyntaxKind.ColonToken, ":"); P(SyntaxKind.QuestionToken, "?");
        P(SyntaxKind.QuestionDotToken, "?."); P(SyntaxKind.LessThanToken, "<"); P(SyntaxKind.GreaterThanToken, ">");
        P(SyntaxKind.LessThanEqualsToken, "<="); P(SyntaxKind.LessThanLessThanToken, "<<");
        P(SyntaxKind.LessThanLessThanEqualsToken, "<<="); P(SyntaxKind.EqualsEqualsToken, "==");
        P(SyntaxKind.ExclamationEqualsToken, "!="); P(SyntaxKind.EqualsEqualsEqualsToken, "===");
        P(SyntaxKind.ExclamationEqualsEqualsToken, "!=="); P(SyntaxKind.EqualsGreaterThanToken, "=>");
        P(SyntaxKind.PlusToken, "+"); P(SyntaxKind.MinusToken, "-"); P(SyntaxKind.AsteriskToken, "*");
        P(SyntaxKind.AsteriskAsteriskToken, "**"); P(SyntaxKind.SlashToken, "/"); P(SyntaxKind.PercentToken, "%");
        P(SyntaxKind.PlusPlusToken, "++"); P(SyntaxKind.MinusMinusToken, "--"); P(SyntaxKind.AmpersandToken, "&");
        P(SyntaxKind.BarToken, "|"); P(SyntaxKind.CaretToken, "^"); P(SyntaxKind.ExclamationToken, "!");
        P(SyntaxKind.TildeToken, "~"); P(SyntaxKind.AmpersandAmpersandToken, "&&"); P(SyntaxKind.BarBarToken, "||");
        P(SyntaxKind.QuestionQuestionToken, "??"); P(SyntaxKind.EqualsToken, "="); P(SyntaxKind.PlusEqualsToken, "+=");
        P(SyntaxKind.MinusEqualsToken, "-="); P(SyntaxKind.AsteriskEqualsToken, "*=");
        P(SyntaxKind.AsteriskAsteriskEqualsToken, "**="); P(SyntaxKind.SlashEqualsToken, "/=");
        P(SyntaxKind.PercentEqualsToken, "%="); P(SyntaxKind.AmpersandEqualsToken, "&=");
        P(SyntaxKind.BarEqualsToken, "|="); P(SyntaxKind.CaretEqualsToken, "^=");
        P(SyntaxKind.AmpersandAmpersandEqualsToken, "&&="); P(SyntaxKind.BarBarEqualsToken, "||=");
        P(SyntaxKind.QuestionQuestionEqualsToken, "??="); P(SyntaxKind.GreaterThanGreaterThanToken, ">>");
        P(SyntaxKind.GreaterThanGreaterThanGreaterThanToken, ">>>"); P(SyntaxKind.GreaterThanEqualsToken, ">=");
        P(SyntaxKind.GreaterThanGreaterThanEqualsToken, ">>="); P(SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken, ">>>=");
        P(SyntaxKind.AtToken, "@");
        return d;
    }

    public static SyntaxKind GetKeywordKind(string text) =>
        s_keywords.TryGetValue(text, out var k) ? k : SyntaxKind.IdentifierToken;

    public static string GetText(SyntaxKind kind) => s_texts.TryGetValue(kind, out var t) ? t : kind.ToString();

    public static bool IsKeyword(SyntaxKind k) => k >= SyntaxKind.BreakKeyword && k <= SyntaxKind.IsKeyword;

    public static bool IsReservedKeyword(SyntaxKind k) => k >= SyntaxKind.BreakKeyword && k <= SyntaxKind.YieldKeyword;

    public static bool IsContextualKeyword(SyntaxKind k) => k >= SyntaxKind.AbstractKeyword && k <= SyntaxKind.IsKeyword;

    /// <summary>Tokens that can be used as an identifier in a binding position.</summary>
    public static bool IsIdentifierLike(SyntaxKind k) => k == SyntaxKind.IdentifierToken || IsContextualKeyword(k);

    public static bool IsAssignmentOperator(SyntaxKind k) => k switch
    {
        SyntaxKind.EqualsToken or SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken or SyntaxKind.AsteriskEqualsToken
            or SyntaxKind.AsteriskAsteriskEqualsToken or SyntaxKind.SlashEqualsToken or SyntaxKind.PercentEqualsToken
            or SyntaxKind.AmpersandEqualsToken or SyntaxKind.BarEqualsToken or SyntaxKind.CaretEqualsToken
            or SyntaxKind.AmpersandAmpersandEqualsToken or SyntaxKind.BarBarEqualsToken or SyntaxKind.QuestionQuestionEqualsToken
            or SyntaxKind.LessThanLessThanEqualsToken or SyntaxKind.GreaterThanGreaterThanEqualsToken
            or SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken => true,
        _ => false,
    };

    public static bool IsModifier(SyntaxKind k) => k switch
    {
        SyntaxKind.AbstractKeyword or SyntaxKind.AsyncKeyword or SyntaxKind.ExportKeyword
            or SyntaxKind.InternalKeyword or SyntaxKind.OverrideKeyword or SyntaxKind.PrivateKeyword
            or SyntaxKind.ProtectedKeyword or SyntaxKind.PublicKeyword or SyntaxKind.ReadonlyKeyword
            or SyntaxKind.SealedKeyword or SyntaxKind.StaticKeyword or SyntaxKind.StructKeyword
            or SyntaxKind.ConstKeyword => true,
        _ => false,
    };

    /// <summary>Returns the binary operator precedence (higher binds tighter) or 0 if not a binary operator.</summary>
    public static int GetBinaryPrecedence(SyntaxKind k) => k switch
    {
        SyntaxKind.QuestionQuestionToken => 1,
        SyntaxKind.BarBarToken => 2,
        SyntaxKind.AmpersandAmpersandToken => 3,
        SyntaxKind.BarToken => 4,
        SyntaxKind.CaretToken => 5,
        SyntaxKind.AmpersandToken => 6,
        SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken or SyntaxKind.EqualsEqualsEqualsToken
            or SyntaxKind.ExclamationEqualsEqualsToken => 7,
        SyntaxKind.LessThanToken or SyntaxKind.GreaterThanToken or SyntaxKind.LessThanEqualsToken
            or SyntaxKind.GreaterThanEqualsToken or SyntaxKind.InstanceOfKeyword or SyntaxKind.AsKeyword
            or SyntaxKind.SatisfiesKeyword => 8,
        SyntaxKind.LessThanLessThanToken or SyntaxKind.GreaterThanGreaterThanToken
            or SyntaxKind.GreaterThanGreaterThanGreaterThanToken => 9,
        SyntaxKind.PlusToken or SyntaxKind.MinusToken => 10,
        SyntaxKind.AsteriskToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken => 11,
        SyntaxKind.AsteriskAsteriskToken => 12,
        _ => 0,
    };
}
