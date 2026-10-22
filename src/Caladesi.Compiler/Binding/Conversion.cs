using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler;

/// <summary>The result of classifying a conversion between two types.</summary>
#pragma warning disable CA1815 // conversions are never compared for equality
public readonly struct Conversion(ConversionKind kind, bool isImplicit, MethodSymbol? method = null, int caseIndex = -1, ConstantValue? constant = null)
#pragma warning restore CA1815
{
    public bool Exists { get; } = true;
    public ConversionKind Kind { get; } = kind;
    public bool IsImplicit { get; } = isImplicit;
    public MethodSymbol? Method { get; } = method;
    public int CaseIndex { get; } = caseIndex;
    /// <summary>A constant-folded result when the operand was a constant.</summary>
    public ConstantValue? Constant { get; } = constant;
    /// <summary>After applying <see cref="Kind"/>, wrap the result in a nullable (T to T | null).</summary>
    public bool WrapNullable { get; init; }
    public Conversion WithNullableWrap()
    {
        return new(Kind, IsImplicit, Method, CaseIndex, Constant) { WrapNullable = true };
    }

    public static readonly Conversion None;
    public static readonly Conversion Identity = new(ConversionKind.Identity, true);
    public bool IsIdentity => Exists && Kind == ConversionKind.Identity;
    public static Conversion Implicit(ConversionKind k, MethodSymbol? m = null, int caseIndex = -1, ConstantValue? c = null)
    {
        return new(k, true, m, caseIndex, c);
    }

    public static Conversion Explicit(ConversionKind k, MethodSymbol? m = null, int caseIndex = -1)
    {
        return new(k, false, m, caseIndex);
    }
}
