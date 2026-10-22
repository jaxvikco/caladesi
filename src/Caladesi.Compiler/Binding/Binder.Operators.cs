using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    private static string? OperatorMethodName(BinaryOperatorKind k)
    {
        return k switch
        {
            BinaryOperatorKind.Add => "op_Addition",
            BinaryOperatorKind.Subtract => "op_Subtraction",
            BinaryOperatorKind.Multiply => "op_Multiply",
            BinaryOperatorKind.Divide => "op_Division",
            BinaryOperatorKind.Remainder => "op_Modulus",
            BinaryOperatorKind.ShiftLeft => "op_LeftShift",
            BinaryOperatorKind.ShiftRight => "op_RightShift",
            BinaryOperatorKind.UnsignedShiftRight => "op_UnsignedRightShift",
            BinaryOperatorKind.And => "op_BitwiseAnd",
            BinaryOperatorKind.Or => "op_BitwiseOr",
            BinaryOperatorKind.Xor => "op_ExclusiveOr",
            BinaryOperatorKind.Equal => "op_Equality",
            BinaryOperatorKind.NotEqual => "op_Inequality",
            BinaryOperatorKind.LessThan => "op_LessThan",
            BinaryOperatorKind.LessThanOrEqual => "op_LessThanOrEqual",
            BinaryOperatorKind.GreaterThan => "op_GreaterThan",
            BinaryOperatorKind.GreaterThanOrEqual => "op_GreaterThanOrEqual",
            _ => null,
        };
    }

    private static BinaryOperatorKind? ToBinaryKind(SyntaxKind k)
    {
        return k switch
        {
            SyntaxKind.PlusToken or SyntaxKind.PlusEqualsToken => BinaryOperatorKind.Add,
            SyntaxKind.MinusToken or SyntaxKind.MinusEqualsToken => BinaryOperatorKind.Subtract,
            SyntaxKind.AsteriskToken or SyntaxKind.AsteriskEqualsToken => BinaryOperatorKind.Multiply,
            SyntaxKind.SlashToken or SyntaxKind.SlashEqualsToken => BinaryOperatorKind.Divide,
            SyntaxKind.PercentToken or SyntaxKind.PercentEqualsToken => BinaryOperatorKind.Remainder,
            SyntaxKind.LessThanLessThanToken or SyntaxKind.LessThanLessThanEqualsToken => BinaryOperatorKind.ShiftLeft,
            SyntaxKind.GreaterThanGreaterThanToken or SyntaxKind.GreaterThanGreaterThanEqualsToken => BinaryOperatorKind.ShiftRight,
            SyntaxKind.GreaterThanGreaterThanGreaterThanToken or SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken => BinaryOperatorKind.UnsignedShiftRight,
            SyntaxKind.AmpersandToken or SyntaxKind.AmpersandEqualsToken => BinaryOperatorKind.And,
            SyntaxKind.BarToken or SyntaxKind.BarEqualsToken => BinaryOperatorKind.Or,
            SyntaxKind.CaretToken or SyntaxKind.CaretEqualsToken => BinaryOperatorKind.Xor,
            SyntaxKind.EqualsEqualsToken => BinaryOperatorKind.Equal,
            SyntaxKind.ExclamationEqualsToken => BinaryOperatorKind.NotEqual,
            SyntaxKind.LessThanToken => BinaryOperatorKind.LessThan,
            SyntaxKind.LessThanEqualsToken => BinaryOperatorKind.LessThanOrEqual,
            SyntaxKind.GreaterThanToken => BinaryOperatorKind.GreaterThan,
            SyntaxKind.GreaterThanEqualsToken => BinaryOperatorKind.GreaterThanOrEqual,
            _ => null,
        };
    }

    // ------------------------------------------------------------------ binary

    private BoundExpression BindBinary(BinaryExpressionSyntax b, TypeSymbol? target)
    {
        SyntaxKind opKind = b.OperatorKind;
        switch (opKind)
        {
            case SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken:
                return BindLogical(b, opKind == SyntaxKind.AmpersandAmpersandToken);
            case SyntaxKind.QuestionQuestionToken:
                return BindCoalesce(b, target);
        }

        BoundExpression left = BindValue(b.Left);
        BoundExpression right;
        right = BindValue(b.Right);
        if (left is BoundBadExpression || right is BoundBadExpression)
        {
            return Bad(b, left, right);
        }

        if (opKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken)
        {
            return BindEquality(b, left, right, opKind == SyntaxKind.EqualsEqualsToken ? BinaryOperatorKind.Equal : BinaryOperatorKind.NotEqual);
        }

        if (opKind == SyntaxKind.AsteriskAsteriskToken)
        {
            return BindPower(b, left, right);
        }

        BinaryOperatorKind? kind = ToBinaryKind(opKind);
        if (kind == null)
        {
            Report(Descriptors.FeatureNotSupported, b.OperatorToken, "Operator '" + b.OperatorToken.Text + "'");
            return Bad(b, left, right);
        }
        return BindArithmeticOrComparison(b, kind.Value, left, right);
    }

    private BoundExpression BindLogical(BinaryExpressionSyntax b, bool isAnd)
    {
        BoundExpression left = ConvertToBool(BindValue(b.Left), b.Left);
        (Narrowings t, Narrowings f) = GetNarrowing(left);
        ImmutableDictionary<NarrowPath, TypeSymbol> saved = _narrow;
        _narrow = ApplyNarrowings(saved, isAnd ? t : f);
        BoundExpression right = ConvertToBool(BindValue(b.Right), b.Right);
        _narrow = saved;
        if (left.HasErrors || right.HasErrors)
        {
            return Bad(b, left, right);
        }

        BoundLogicalBinary node = new(isAnd, left, right, BoolType) { Syntax = b };
        return left.ConstantValue is { Value: bool lv } && right.ConstantValue is { Value: bool rv }
            ? new BoundLogicalBinary(isAnd, left, right, BoolType) { Syntax = b, Folded = new ConstantValue(isAnd ? lv && rv : lv || rv) }
            : node;
    }

    private BoundExpression BindCoalesce(BinaryExpressionSyntax b, TypeSymbol? target)
    {
        BoundExpression left = BindValue(b.Left);
        if (left is BoundBadExpression) { _ = BindValue(b.Right); return left; }
        if (left.Type == null) { ReportNotAValue(left, b.Left); return Bad(b, left); }
        ImmutableDictionary<NarrowPath, TypeSymbol> saved = _narrow;
        BoundExpression right = BindValue(b.Right, target ?? (left.Type is NullableTypeSymbol n0 ? n0.Underlying : left.Type));
        _narrow = saved;
        if (right is BoundBadExpression)
        {
            return Bad(b, left, right);
        }

        TypeSymbol lt = left.Type;
        TypeSymbol underlying = lt.StripNullable();
        TypeSymbol resultType;
        if (right is BoundUnbound)
        {
            right = ConvertImplicit(right, underlying, b.Right);
        }

        TypeSymbol? rt = right.Type;
        if (rt == null) { ReportNotAValue(right, b.Right); return Bad(b, left, right); }

        if (rt is NullableTypeSymbol)
        {
            // a ?? b where b may be null: result nullable of the common type
            TypeSymbol? common = BestCommonType([UnwrapForCommon(left), UnwrapForCommon(right)], b);
            if (common == null) { Report(Descriptors.NoBestCommonType, b, lt.ToDisplayString(), rt.ToDisplayString()); return Bad(b, left, right); }
            resultType = common is NullableTypeSymbol ? common : new NullableTypeSymbol(common);
            BoundExpression rc = ConvertImplicit(right, resultType, b.Right);
            _ = lt is NullableTypeSymbol ? left : ConvertImplicit(left, resultType, b.Left);
            return new BoundCoalesce(ConvertImplicit(left, resultType, b.Left), rc, resultType) { Syntax = b };
        }
        if (rt.IsNullType)
        {
            return left;
        }
        // right is non-null: result is the non-null common type
        Conversion cv = ClassifyImplicit(right, rt, underlying);
        if (cv.Exists) { resultType = underlying; }
        else
        {
            TypeSymbol? common = BestCommonType([UnwrapForCommon(left), right], b);
            if (common == null) { Report(Descriptors.NoBestCommonType, b, lt.ToDisplayString(), rt.ToDisplayString()); return Bad(b, left, right); }
            resultType = common;
            left = ConvertImplicit(UnwrapNullableValueIfNeeded(left), resultType, b.Left);
        }
        if (lt is not NullableTypeSymbol && lt.IsValueType)
        {
            Report(Descriptors.CoalesceOnNonNullable, b.OperatorToken, lt.ToDisplayString());
        }

        return new BoundCoalesce(left, ConvertImplicit(right, resultType, b.Right), resultType) { Syntax = b };
    }

    private static BoundExpression UnwrapForCommon(BoundExpression e)
    {
        return e;
    }

    private static BoundExpression UnwrapNullableValueIfNeeded(BoundExpression e)
    {
        return e;
    }

    // ------------------------------------------------------------------ arithmetic / comparison

    private static SpecialType Promote(SpecialType t)
    {
        return t switch
        {
            SpecialType.SByte or SpecialType.Byte or SpecialType.Int16 or SpecialType.UInt16 or SpecialType.Char => SpecialType.Int32,
            _ => t,
        };
    }

    /// <summary>Binary numeric promotion; returns None if the operands have no common arithmetic type.</summary>
    private static SpecialType BinaryPromotion(BoundExpression l, BoundExpression r)
    {
        return BinaryPromotion(l.Type!.SpecialType, r.Type!.SpecialType, l, r);
    }

    private static SpecialType BinaryPromotion(SpecialType a, SpecialType b, BoundExpression? l = null, BoundExpression? r = null)
    {
        if (!a.IsNumeric() || !b.IsNumeric())
        {
            return SpecialType.None;
        }

        if (a == SpecialType.Decimal || b == SpecialType.Decimal)
        {
            SpecialType other = a == SpecialType.Decimal ? b : a;
            return other.IsFloating() ? SpecialType.None : SpecialType.Decimal;
        }
        if (a == SpecialType.Double || b == SpecialType.Double)
        {
            return SpecialType.Double;
        }

        if (a == SpecialType.Single || b == SpecialType.Single)
        {
            return SpecialType.Single;
        }

        if (a == SpecialType.IntPtr || b == SpecialType.IntPtr)
        {
            return SpecialType.IntPtr;
        }

        if (a == SpecialType.UIntPtr || b == SpecialType.UIntPtr)
        {
            return SpecialType.UIntPtr;
        }

        a = Promote(a); b = Promote(b);
        if (a == SpecialType.UInt64 || b == SpecialType.UInt64)
        {
            SpecialType other = a == SpecialType.UInt64 ? b : a;
            BoundExpression? otherExpr = a == SpecialType.UInt64 ? r : l;
            if (other.IsSignedIntegral())
            {
                // signed constant that fits is allowed
                return otherExpr?.ConstantValue is { Value: { } cv } && ConstantFits(cv, SpecialType.UInt64) ? SpecialType.UInt64 : SpecialType.None;
            }
            return SpecialType.UInt64;
        }
        if (a == SpecialType.Int64 || b == SpecialType.Int64)
        {
            return SpecialType.Int64;
        }

        if (a == SpecialType.UInt32 || b == SpecialType.UInt32)
        {
            SpecialType other = a == SpecialType.UInt32 ? b : a;
            BoundExpression? otherExpr = a == SpecialType.UInt32 ? r : l;
            return other == SpecialType.Int32
                ? otherExpr?.ConstantValue is { Value: int ci } && ci >= 0 ? SpecialType.UInt32 : SpecialType.Int64
                : SpecialType.UInt32;
        }
        return SpecialType.Int32;
    }

    private BoundExpression BindArithmeticOrComparison(SyntaxNode syntax, BinaryOperatorKind kind, BoundExpression left, BoundExpression right)
    {
        TypeSymbol? lt = left.Type;
        TypeSymbol? rt = right.Type;
        if (lt == null || rt == null)
        {
            if (lt == null)
            {
                ReportNotAValue(left, syntax);
            }

            if (rt == null)
            {
                ReportNotAValue(right, syntax);
            }

            return Bad(syntax, left, right);
        }
        if (lt.IsErrorType || rt.IsErrorType)
        {
            return Bad(syntax, left, right);
        }

        bool isComparison = kind is >= BinaryOperatorKind.LessThan and <= BinaryOperatorKind.GreaterThanOrEqual;

        // string concatenation
        if (kind == BinaryOperatorKind.Add && (lt.SpecialType == SpecialType.String || rt.SpecialType == SpecialType.String))
        {
            if (lt.IsVoid || rt.IsVoid) { ReportBadOperands(syntax, kind, lt, rt); return Bad(syntax, left, right); }
#pragma warning disable CA1508 // flow analysis cannot see that nullable-string operands reach here
            if (lt is NullableTypeSymbol { Underlying.SpecialType: SpecialType.String } || rt is NullableTypeSymbol { Underlying.SpecialType: SpecialType.String } || lt.IsNullType || rt.IsNullType)
#pragma warning restore CA1508
            {
                // allowed: null concatenates as empty (C# semantics) but only for explicit string | null
            }
            else if (lt is NullableTypeSymbol || rt is NullableTypeSymbol)
            {
                ReportPossiblyNullOperand(left, right, syntax);
                return Bad(syntax, left, right);
            }
            BoundExpression l2 = lt.StripNullable().SpecialType == SpecialType.String ? left : left;
            return BuildStringConcat([StringOperand(left), StringOperand(right)], syntax);
        }

        if (lt is NullableTypeSymbol || rt is NullableTypeSymbol)
        {
            ReportPossiblyNullOperand(left, right, syntax);
            return Bad(syntax, left, right);
        }

        // user-defined operators (also covers decimal, BigInteger, DateTime ...)
        string? opName = OperatorMethodName(kind);
        if (opName != null && !(IsPrimitiveArithmetic(lt) && IsPrimitiveArithmetic(rt)))
        {
            BoundExpression? udo = TryUserBinary(opName, left, right, syntax);
            if (udo != null)
            {
                return udo;
            }
        }

        // enums
        if (lt.TypeKind == TypeKind.Enum || rt.TypeKind == TypeKind.Enum)
        {
            return BindEnumOperator(syntax, kind, left, right);
        }

        if (kind is BinaryOperatorKind.ShiftLeft or BinaryOperatorKind.ShiftRight or BinaryOperatorKind.UnsignedShiftRight)
        {
            return BindShift(syntax, kind, left, right);
        }

        if (kind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.Xor && lt.SpecialType == SpecialType.Boolean && rt.SpecialType == SpecialType.Boolean)
        {
            return MakeBinary(syntax, kind, left, right, BoolType, BoolType);
        }

        // string comparison
        if (isComparison && lt.SpecialType == SpecialType.String && rt.SpecialType == SpecialType.String)
        {
            MethodSymbol cmp = Special(SpecialType.String).GetMembers("CompareOrdinal").OfType<MethodSymbol>().First(m => m.Parameters.Length == 2);
            BoundCall call = new(null, cmp, [left, right]) { Syntax = syntax };
            return MakeBinary(syntax, kind, call, new BoundLiteral(new ConstantValue(0), IntType), BoolType, IntType);
        }

        SpecialType promoted = BinaryPromotion(left, right);
        if (promoted == SpecialType.None)
        {
            // char vs string literal etc.
            ReportBadOperands(syntax, kind, lt, rt);
            return Bad(syntax, left, right);
        }
        NamedTypeSymbol opType = Special(promoted);
        BoundExpression lc = ConvertImplicit(left, opType, syntax);
        BoundExpression rc = ConvertImplicit(right, opType, syntax);
        if (lc.HasErrors || rc.HasErrors)
        {
            return Bad(syntax, left, right);
        }

        if (kind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.Xor && !promoted.IsIntegral())
        {
            ReportBadOperands(syntax, kind, lt, rt);
            return Bad(syntax, left, right);
        }
        TypeSymbol resultType = isComparison ? BoolType : opType;
        return MakeBinary(syntax, kind, lc, rc, resultType, opType);
    }

    private static bool IsPrimitiveArithmetic(TypeSymbol t)
    {
        return (t.SpecialType.IsNumeric() && t.SpecialType != SpecialType.Decimal && t.SpecialType != SpecialType.IntPtr && t.SpecialType != SpecialType.UIntPtr)
        || t.SpecialType == SpecialType.Boolean;
    }

    private BoundExpression StringOperand(BoundExpression e)
    {
        TypeSymbol t = e.Type!;
        return t.StripNullable().SpecialType == SpecialType.String
            ? e
            : t.IsNullType ? new BoundLiteral(new ConstantValue(""), StringType) { Syntax = e.Syntax } : e;
    }

    private static BoundBinary MakeBinary(SyntaxNode syntax, BinaryOperatorKind kind, BoundExpression l, BoundExpression r, TypeSymbol resultType, TypeSymbol opType)
    {
        ConstantValue? folded = null;
        if (l.ConstantValue is { Value: { } lv } && r.ConstantValue is { Value: { } rv } && opType.SpecialType != SpecialType.None)
        {
            object? f = ConstantFolding.FoldBinary(kind, lv, rv, opType.SpecialType);
            if (f != null)
            {
                folded = new ConstantValue(f);
            }
        }
        return new BoundBinary(kind, l, r, resultType, opType) { Syntax = syntax, Folded = folded };
    }

    private BoundExpression BindShift(SyntaxNode syntax, BinaryOperatorKind kind, BoundExpression left, BoundExpression right)
    {
        SpecialType lt = Promote(left.Type!.SpecialType);
        if (!lt.IsIntegral() || lt is SpecialType.Char)
        {
            ReportBadOperands(syntax, kind, left.Type!, right.Type!);
            return Bad(syntax, left, right);
        }
        NamedTypeSymbol leftType = Special(lt);
        BoundExpression lc = ConvertImplicit(left, leftType, syntax);
        BoundExpression rc = ConvertImplicit(right, IntType, syntax);
        return lc.HasErrors || rc.HasErrors ? Bad(syntax, left, right) : MakeBinary(syntax, kind, lc, rc, leftType, leftType);
    }

    private BoundExpression BindEnumOperator(SyntaxNode syntax, BinaryOperatorKind kind, BoundExpression left, BoundExpression right)
    {
        TypeSymbol lt = left.Type!;
        TypeSymbol rt = right.Type!;
        bool isComparison = kind is >= BinaryOperatorKind.LessThan and <= BinaryOperatorKind.GreaterThanOrEqual;
        if (lt.TypeKind == TypeKind.Enum && rt.TypeKind == TypeKind.Enum && lt.Equals(rt))
        {
            NamedTypeSymbol under = ((NamedTypeSymbol)lt).EnumUnderlyingType!;
            if (isComparison || kind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.Xor)
            {
                return MakeBinary(syntax, kind, left, right, isComparison ? BoolType : lt, under);
            }

            if (kind == BinaryOperatorKind.Subtract)
            {
                return MakeBinary(syntax, kind, left, right, under, under);
            }
        }
        else if (lt.TypeKind == TypeKind.Enum && rt.SpecialType.IsIntegral() && kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
        {
            NamedTypeSymbol under = ((NamedTypeSymbol)lt).EnumUnderlyingType!;
            return MakeBinary(syntax, kind, left, ConvertImplicit(right, under, syntax), lt, under);
        }
        ReportBadOperands(syntax, kind, lt, rt);
        return Bad(syntax, left, right);
    }

    private BoundExpression BindPower(BinaryExpressionSyntax b, BoundExpression left, BoundExpression right)
    {
        TypeSymbol? lt = left.Type;
        TypeSymbol? rt = right.Type;
        if (lt == null || rt == null)
        {
            return Bad(b, left, right);
        }

        if (lt.SpecialType == SpecialType.BigInteger)
        {
            MethodSymbol pow = lt.GetMembers("Pow").OfType<MethodSymbol>().First();
            return new BoundCall(null, pow, [left, rt.SpecialType == SpecialType.BigInteger ? ConvertExplicit(right, IntType, b) : ConvertImplicit(right, IntType, b)]) { Syntax = b };
        }
        if (!lt.SpecialType.IsNumeric() || !rt.SpecialType.IsNumeric() || lt.SpecialType == SpecialType.Char)
        {
            ReportBadOperands(b, BinaryOperatorKind.Multiply, lt, rt, "**");
            return Bad(b, left, right);
        }
        NamedTypeSymbol? math = Compilation.GetWellKnownType("System.Math");
        MethodSymbol? pw = math?.GetMembers("Pow").OfType<MethodSymbol>().FirstOrDefault();
        if (pw == null) { Report(Descriptors.RuntimeMissing, b, "System.Math.Pow"); return Bad(b); }
        return new BoundCall(null, pw, [ConvertImplicit(left, DoubleType, b), ConvertImplicit(right, DoubleType, b)]) { Syntax = b };
    }

    private BoundCall? TryUserBinary(string opName, BoundExpression left, BoundExpression right, SyntaxNode syntax)
    {
        List<MethodSymbol> cands = [];
        void Collect(TypeSymbol t)
        {
            t = t.StripNullable();
            for (TypeSymbol? x = t; x != null; x = x.BaseType)
            {
                foreach (Symbol m in x.GetMembers(opName))
                {
                    if (m is MethodSymbol { IsStatic: true, Parameters.Length: 2 } ms && IsAccessible(ms) && !cands.Contains(ms))
                    {
                        cands.Add(ms);
                    }
                }

                if (x is not NamedTypeSymbol)
                {
                    break;
                }
            }
        }
        Collect(left.Type!);
        Collect(right.Type!);
        if (cands.Count == 0)
        {
            return null;
        }

        ResolvedCall? res = ResolveOverloads(cands, [left, right], [], syntax, opName, report: false);
        return res == null ? null : new BoundCall(null, res.Method, res.Arguments) { Syntax = syntax };
    }

    private void ReportBadOperands(SyntaxNode syntax, BinaryOperatorKind kind, TypeSymbol l, TypeSymbol r, string? text = null)
    {
        string op = text ?? kind switch
        {
            BinaryOperatorKind.Add => "+",
            BinaryOperatorKind.Subtract => "-",
            BinaryOperatorKind.Multiply => "*",
            BinaryOperatorKind.Divide => "/",
            BinaryOperatorKind.Remainder => "%",
            BinaryOperatorKind.ShiftLeft => "<<",
            BinaryOperatorKind.ShiftRight => ">>",
            BinaryOperatorKind.UnsignedShiftRight => ">>>",
            BinaryOperatorKind.And => "&",
            BinaryOperatorKind.Or => "|",
            BinaryOperatorKind.Xor => "^",
            BinaryOperatorKind.LessThan => "<",
            BinaryOperatorKind.LessThanOrEqual => "<=",
            BinaryOperatorKind.GreaterThan => ">",
            BinaryOperatorKind.GreaterThanOrEqual => ">=",
            BinaryOperatorKind.Equal => "===",
            BinaryOperatorKind.NotEqual => "!==",
            _ => "?",
        };
        Report(Descriptors.BadOperands, syntax is BinaryExpressionSyntax be ? be.OperatorToken.Span : syntax.Span, op, l.ToDisplayString(), r.ToDisplayString());
    }

    private void ReportPossiblyNullOperand(BoundExpression l, BoundExpression r, SyntaxNode syntax)
    {
        BoundExpression which = l.Type is NullableTypeSymbol ? l : r;
        Report(Descriptors.PossiblyNull, which.Syntax ?? syntax, which.Syntax?.ToFullString().Trim() ?? "expression");
    }

    // ------------------------------------------------------------------ equality

    private BoundExpression BindEquality(SyntaxNode syntax, BoundExpression left, BoundExpression right, BinaryOperatorKind kind)
    {
        if (left is BoundBadExpression || right is BoundBadExpression)
        {
            return Bad(syntax, left, right);
        }

        TypeSymbol? lt = left.Type;
        TypeSymbol? rt = right.Type;
        if (lt == null || rt == null)
        {
            if (lt == null)
            {
                ReportNotAValue(left, syntax);
            }

            if (rt == null)
            {
                ReportNotAValue(right, syntax);
            }

            return Bad(syntax, left, right);
        }
        if (lt.IsErrorType || rt.IsErrorType)
        {
            return Bad(syntax, left, right);
        }

        // comparisons with null
        bool leftNull = left is BoundLiteral { Value.IsNull: true };
        bool rightNull = right is BoundLiteral { Value.IsNull: true };
        if (leftNull || rightNull)
        {
            BoundExpression other = leftNull ? right : left;
            TypeSymbol ot = other.Type!;
            return leftNull && rightNull
                ? new BoundLiteral(new ConstantValue(kind == BinaryOperatorKind.Equal), BoolType) { Syntax = syntax }
                : new BoundBinary(kind, other, new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance), BoolType, ot) { Syntax = syntax };
        }

        // user-defined operator==
        if (!(IsPrimitiveArithmetic(lt) && IsPrimitiveArithmetic(rt)))
        {
            BoundExpression? udo = TryUserBinary(kind == BinaryOperatorKind.Equal ? "op_Equality" : "op_Inequality", left, right, syntax);
            if (udo != null)
            {
                return udo;
            }
        }

        TypeSymbol lu = lt.StripNullable();
        TypeSymbol ru = rt.StripNullable();
        bool anyNullableValue = (lt is NullableTypeSymbol && lu.IsValueType) || (rt is NullableTypeSymbol && ru.IsValueType);

        // numeric
        if (lu.SpecialType.IsNumeric() && ru.SpecialType.IsNumeric() && !anyNullableValue)
        {
            SpecialType promoted = BinaryPromotion(left, right);
            if (promoted != SpecialType.None)
            {
                NamedTypeSymbol opType = Special(promoted);
                return MakeBinary(syntax, kind, ConvertImplicit(left, opType, syntax), ConvertImplicit(right, opType, syntax), BoolType, opType);
            }
        }
        if (lu.SpecialType == SpecialType.Boolean && ru.SpecialType == SpecialType.Boolean && !anyNullableValue)
        {
            return MakeBinary(syntax, kind, left, right, BoolType, BoolType);
        }

        if (lu.SpecialType == SpecialType.Char && ru.SpecialType == SpecialType.Char && !anyNullableValue)
        {
            return MakeBinary(syntax, kind, left, right, BoolType, Special(SpecialType.Char));
        }

        if (lu.TypeKind == TypeKind.Enum && ru.TypeKind == TypeKind.Enum && lu.Equals(ru) && !anyNullableValue)
        {
            return MakeBinary(syntax, kind, left, right, BoolType, ((NamedTypeSymbol)lu).EnumUnderlyingType!);
        }
        // enum vs string-literal constant
        if (lu.TypeKind == TypeKind.Enum && right.ConstantValue is { Value: string })
        {
            BoundExpression rc = ConvertImplicit(right, lu, syntax);
            return !rc.HasErrors
                ? MakeBinary(syntax, kind, left, rc, BoolType, ((NamedTypeSymbol)lu).EnumUnderlyingType!)
                : Bad(syntax, left, right);
        }
        if (ru.TypeKind == TypeKind.Enum && left.ConstantValue is { Value: string })
        {
            BoundExpression lc = ConvertImplicit(left, ru, syntax);
            return !lc.HasErrors
                ? MakeBinary(syntax, kind, lc, right, BoolType, ((NamedTypeSymbol)ru).EnumUnderlyingType!)
                : Bad(syntax, left, right);
        }
        // char vs 1-char string literal
        if (lu.SpecialType == SpecialType.Char && right.ConstantValue is { Value: string })
        {
            BoundExpression rc = ConvertImplicit(right, lu, syntax);
            if (!rc.HasErrors)
            {
                return MakeBinary(syntax, kind, left, rc, BoolType, lu);
            }
        }

        // reference types: reference equality when one converts to the other
        if ((lu.IsReferenceType || lu is TypeParameterSymbol) && (ru.IsReferenceType || ru is TypeParameterSymbol) && !anyNullableValue)
        {
            if (ClassifyImplicit(null, lu, ru).Exists || ClassifyImplicit(null, ru, lu).Exists || ClassifyExplicit(null, lu, ru).Exists)
            {
                BoundExpression lo = lt is NullableTypeSymbol ? left : left;
                return new BoundBinary(kind, left, right, BoolType, ObjectType) { Syntax = syntax };
            }
        }

        // anything else comparable via Object.Equals (value types without operators, nullable values, tuples)
        if (ClassifyImplicit(null, lu, ru).Exists || ClassifyImplicit(null, ru, lu).Exists)
        {
            // numeric operands of different widths (`long | null == int`) are compared in their common type, lifted to nullable
            if (anyNullableValue && lu.SpecialType.IsNumeric() && ru.SpecialType.IsNumeric() && !lu.Equals(ru))
            {
                SpecialType promoted = BinaryPromotion(lu.SpecialType, ru.SpecialType);
                if (promoted != SpecialType.None)
                {
                    NullableTypeSymbol lifted = new(Special(promoted));
                    left = ConvertImplicit(left, lifted, syntax);
                    right = ConvertImplicit(right, lifted, syntax);
                }
            }

            MethodSymbol objEquals = Special(SpecialType.Object).GetMembers("Equals").OfType<MethodSymbol>().First(m => m.IsStatic && m.Parameters.Length == 2);
            BoundCall call = new(null, objEquals, [BoxToObject(left, syntax), BoxToObject(right, syntax)]) { Syntax = syntax };
            return kind == BinaryOperatorKind.Equal ? call : new BoundUnary(UnaryOperatorKind.Not, call, BoolType) { Syntax = syntax };
        }
        Report(Descriptors.NoOverlap, syntax is BinaryExpressionSyntax be ? be.OperatorToken.Span : syntax.Span, lt.ToDisplayString(), rt.ToDisplayString());
        return Bad(syntax, left, right);
    }

    // ------------------------------------------------------------------ unary

    private BoundExpression BindPrefixUnary(PrefixUnaryExpressionSyntax pu, TypeSymbol? _1)
    {
        SyntaxKind opKind = pu.OperatorToken.Kind;
        if (opKind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
        {
            return BindIncrementDecrement(pu.Operand, opKind == SyntaxKind.PlusPlusToken, isPrefix: true, valueUsed: true, pu);
        }

        if (opKind == SyntaxKind.VoidKeyword)
        {
            _ = BindValue(pu.Operand);
            return Bad(pu);
        }
        BoundExpression operand = BindValue(pu.Operand);
        if (operand is BoundBadExpression)
        {
            return Bad(pu, operand);
        }

        TypeSymbol? t = operand.Type;
        if (t == null) { ReportNotAValue(operand, pu); return Bad(pu, operand); }
        if (t.IsErrorType)
        {
            return Bad(pu, operand);
        }

        switch (opKind)
        {
            case SyntaxKind.ExclamationToken:
                {
                    BoundExpression b = ConvertToBool(operand, pu.Operand);
                    if (b.HasErrors)
                    {
                        return Bad(pu, operand);
                    }

                    ConstantValue? folded = b.ConstantValue is { Value: bool bv } ? new ConstantValue(!bv) : null;
                    return new BoundUnary(UnaryOperatorKind.Not, b, BoolType) { Syntax = pu, Folded = folded };
                }
            case SyntaxKind.MinusToken or SyntaxKind.PlusToken or SyntaxKind.TildeToken:
                {
                    if (t is NullableTypeSymbol) { ReportPossiblyNull(operand, null, pu); return Bad(pu, operand); }
                    UnaryOperatorKind uk = opKind switch { SyntaxKind.MinusToken => UnaryOperatorKind.Minus, SyntaxKind.PlusToken => UnaryOperatorKind.Plus, _ => UnaryOperatorKind.BitwiseNot };
                    // negative literal folding (handles int.MinValue written as -2147483648)
                    if (uk == UnaryOperatorKind.Minus && operand is BoundLiteral { Value.Value: long lv } && lv == 2147483648L && pu.Operand is LiteralExpressionSyntax)
                    {
                        return new BoundLiteral(new ConstantValue(int.MinValue), IntType) { Syntax = pu };
                    }

                    if (uk == UnaryOperatorKind.Minus && operand is BoundLiteral { Value.Value: ulong ulv } && ulv == 9223372036854775808UL)
                    {
                        return new BoundLiteral(new ConstantValue(long.MinValue), Special(SpecialType.Int64)) { Syntax = pu };
                    }

                    string opName = uk switch { UnaryOperatorKind.Minus => "op_UnaryNegation", UnaryOperatorKind.Plus => "op_UnaryPlus", _ => "op_OnesComplement" };
                    if (!t.SpecialType.IsNumeric() || t.SpecialType is SpecialType.Decimal or SpecialType.IntPtr or SpecialType.UIntPtr)
                    {
                        List<MethodSymbol> cands = [.. LookupMembers(t, opName).OfType<MethodSymbol>().Where(m => m.IsStatic && m.Parameters.Length == 1)];
                        if (cands.Count > 0)
                        {
                            ResolvedCall? res = ResolveOverloads(cands, [operand], [], pu, opName, report: false);
                            if (res != null)
                            {
                                return new BoundCall(null, res.Method, res.Arguments) { Syntax = pu };
                            }
                        }
                        if (t.TypeKind == TypeKind.Enum && uk == UnaryOperatorKind.BitwiseNot)
                        {
                            return new BoundUnary(uk, operand, t) { Syntax = pu };
                        }

                        Report(Descriptors.BadUnaryOperand, pu.OperatorToken, pu.OperatorToken.Text, t.ToDisplayString());
                        return Bad(pu, operand);
                    }
                    SpecialType promoted = Promote(t.SpecialType);
                    if (uk == UnaryOperatorKind.Minus && promoted == SpecialType.UInt32)
                    {
                        promoted = SpecialType.Int64;
                    }

                    if (uk == UnaryOperatorKind.Minus && promoted == SpecialType.UInt64)
                    {
                        Report(Descriptors.BadUnaryOperand, pu.OperatorToken, "-", t.ToDisplayString());
                        return Bad(pu, operand);
                    }
                    if (uk == UnaryOperatorKind.BitwiseNot && !promoted.IsIntegral())
                    {
                        Report(Descriptors.BadUnaryOperand, pu.OperatorToken, "~", t.ToDisplayString());
                        return Bad(pu, operand);
                    }
                    NamedTypeSymbol rt = Special(promoted);
                    BoundExpression conv = ConvertImplicit(operand, rt, pu);
                    ConstantValue? folded = null;
                    if (conv.ConstantValue is { Value: { } cv })
                    {
                        object? f = ConstantFolding.FoldUnary(uk, cv, promoted);
                        if (f != null)
                        {
                            folded = new ConstantValue(f);
                        }
                    }
                    return new BoundUnary(uk, conv, rt) { Syntax = pu, Folded = folded };
                }
        }
        Report(Descriptors.FeatureNotSupported, pu, "Operator '" + pu.OperatorToken.Text + "'");
        return Bad(pu, operand);
    }

    private BoundExpression BindPostfixUnary(PostfixUnaryExpressionSyntax pf, bool valueUsed)
    {
        return BindIncrementDecrement(pf.Operand, pf.OperatorToken.Kind == SyntaxKind.PlusPlusToken, isPrefix: false, valueUsed, pf);
    }
}
