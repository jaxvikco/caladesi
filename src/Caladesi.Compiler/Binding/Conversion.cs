using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler;

/// <summary>The result of classifying a conversion between two types.</summary>
public readonly struct Conversion
{
    public Conversion(ConversionKind kind, bool isImplicit, MethodSymbol? method = null, int caseIndex = -1, ConstantValue? constant = null)
    {
        Exists = true;
        Kind = kind;
        IsImplicit = isImplicit;
        Method = method;
        CaseIndex = caseIndex;
        Constant = constant;
    }

    public bool Exists { get; }
    public ConversionKind Kind { get; }
    public bool IsImplicit { get; }
    public MethodSymbol? Method { get; }
    public int CaseIndex { get; }
    /// <summary>A constant-folded result when the operand was a constant.</summary>
    public ConstantValue? Constant { get; }
    /// <summary>After applying <see cref="Kind"/>, wrap the result in a nullable (T to T | null).</summary>
    public bool WrapNullable { get; init; }
    public Conversion WithNullableWrap() => new(Kind, IsImplicit, Method, CaseIndex, Constant) { WrapNullable = true };

    public static readonly Conversion None = default;
    public static readonly Conversion Identity = new(ConversionKind.Identity, true);
    public bool IsIdentity => Exists && Kind == ConversionKind.Identity;
    public static Conversion Implicit(ConversionKind k, MethodSymbol? m = null, int caseIndex = -1, ConstantValue? c = null) => new(k, true, m, caseIndex, c);
    public static Conversion Explicit(ConversionKind k, MethodSymbol? m = null, int caseIndex = -1) => new(k, false, m, caseIndex);
}
