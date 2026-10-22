using System.Collections.Immutable;
using System.Numerics;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    private static string? OperatorMethodName(BinaryOperatorKind k) => k switch
    {
        BinaryOperatorKind.Add => "op_Addition", BinaryOperatorKind.Subtract => "op_Subtraction", BinaryOperatorKind.Multiply => "op_Multiply",
        BinaryOperatorKind.Divide => "op_Division", BinaryOperatorKind.Remainder => "op_Modulus", BinaryOperatorKind.ShiftLeft => "op_LeftShift",
        BinaryOperatorKind.ShiftRight => "op_RightShift", BinaryOperatorKind.UnsignedShiftRight => "op_UnsignedRightShift",
        BinaryOperatorKind.And => "op_BitwiseAnd", BinaryOperatorKind.Or => "op_BitwiseOr", BinaryOperatorKind.Xor => "op_ExclusiveOr",
        BinaryOperatorKind.Equal => "op_Equality", BinaryOperatorKind.NotEqual => "op_Inequality", BinaryOperatorKind.LessThan => "op_LessThan",
        BinaryOperatorKind.LessThanOrEqual => "op_LessThanOrEqual", BinaryOperatorKind.GreaterThan => "op_GreaterThan",
        BinaryOperatorKind.GreaterThanOrEqual => "op_GreaterThanOrEqual", _ => null,
    };

    private static BinaryOperatorKind? ToBinaryKind(SyntaxKind k) => k switch
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
        SyntaxKind.EqualsEqualsEqualsToken => BinaryOperatorKind.Equal,
        SyntaxKind.ExclamationEqualsEqualsToken => BinaryOperatorKind.NotEqual,
        SyntaxKind.LessThanToken => BinaryOperatorKind.LessThan,
        SyntaxKind.LessThanEqualsToken => BinaryOperatorKind.LessThanOrEqual,
        SyntaxKind.GreaterThanToken => BinaryOperatorKind.GreaterThan,
        SyntaxKind.GreaterThanEqualsToken => BinaryOperatorKind.GreaterThanOrEqual,
        _ => null,
    };

    // ------------------------------------------------------------------ binary

    private BoundExpression BindBinary(BinaryExpressionSyntax b, TypeSymbol? target)
    {
        var opKind = b.OperatorKind;
        switch (opKind)
        {
            case SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken:
            {
                var good = opKind == SyntaxKind.EqualsEqualsToken ? "===" : "!==";
                ReportWithFix(Descriptors.LooseEqualityNotSupported, b.OperatorToken.Span, new FixIt("Replace with '" + good + "'", b.OperatorToken.Span, good), b.OperatorToken.Text, good);
                // recover by treating it as the strict operator
                var l0 = BindValue(b.Left);
                var r0 = BindValue(b.Right);
                return BindEquality(b, l0, r0, opKind == SyntaxKind.EqualsEqualsToken ? BinaryOperatorKind.Equal : BinaryOperatorKind.NotEqual);
            }
            case SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken:
                return BindLogical(b, opKind == SyntaxKind.AmpersandAmpersandToken);
            case SyntaxKind.QuestionQuestionToken:
                return BindCoalesce(b, target);
        }

        var left = BindValue(b.Left);
        BoundExpression right;
        right = BindValue(b.Right);
        if (left is BoundBadExpression || right is BoundBadExpression) return Bad(b, left, right);

        if (opKind is SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.ExclamationEqualsEqualsToken)
            return BindEquality(b, left, right, opKind == SyntaxKind.EqualsEqualsEqualsToken ? BinaryOperatorKind.Equal : BinaryOperatorKind.NotEqual);
        if (opKind == SyntaxKind.AsteriskAsteriskToken) return BindPower(b, left, right);
        var kind = ToBinaryKind(opKind);
        if (kind == null)
        {
            Report(Descriptors.FeatureNotSupported, b.OperatorToken, "Operator '" + b.OperatorToken.Text + "'");
            return Bad(b, left, right);
        }
        return BindArithmeticOrComparison(b, kind.Value, left, right);
    }

    private BoundExpression BindLogical(BinaryExpressionSyntax b, bool isAnd)
    {
        var left = ConvertToBool(BindValue(b.Left), b.Left);
        var (t, f) = GetNarrowing(left);
        var saved = _narrow;
        _narrow = ApplyNarrowings(saved, isAnd ? t : f);
        var right = ConvertToBool(BindValue(b.Right), b.Right);
        _narrow = saved;
        if (left.HasErrors || right.HasErrors) return Bad(b, left, right);
        var node = new BoundLogicalBinary(isAnd, left, right, BoolType) { Syntax = b };
        if (left.ConstantValue is { Value: bool lv } && right.ConstantValue is { Value: bool rv })
            return new BoundLogicalBinary(isAnd, left, right, BoolType) { Syntax = b, Folded = new ConstantValue(isAnd ? lv && rv : lv || rv) };
        return node;
    }

    private BoundExpression BindCoalesce(BinaryExpressionSyntax b, TypeSymbol? target)
    {
        var left = BindValue(b.Left);
        if (left is BoundBadExpression) { BindValue(b.Right); return left; }
        if (left.Type == null) { ReportNotAValue(left, b.Left); return Bad(b, left); }
        var saved = _narrow;
        var right = BindValue(b.Right, target ?? (left.Type is NullableTypeSymbol n0 ? n0.Underlying : left.Type));
        _narrow = saved;
        if (right is BoundBadExpression) return Bad(b, left, right);

        var lt = left.Type;
        var underlying = lt.StripNullable();
        TypeSymbol resultType;
        if (right is BoundUnbound) right = ConvertImplicit(right, underlying, b.Right);
        var rt = right.Type;
        if (rt == null) { ReportNotAValue(right, b.Right); return Bad(b, left, right); }
        if (rt is NullableTypeSymbol rn)
        {
            // a ?? b where b may be null: result nullable of the common type
            var common = BestCommonType([UnwrapForCommon(left), UnwrapForCommon(right)], b);
            if (common == null) { Report(Descriptors.NoBestCommonType, b, lt.ToDisplayString(), rt.ToDisplayString()); return Bad(b, left, right); }
            resultType = common is NullableTypeSymbol ? common : new NullableTypeSymbol(common);
            var rc = ConvertImplicit(right, resultType, b.Right);
            var lc = lt is NullableTypeSymbol ? left : ConvertImplicit(left, resultType, b.Left);
            return new BoundCoalesce(ConvertImplicit(left, resultType, b.Left), rc, resultType) { Syntax = b };
        }
        if (rt.IsNullType) return left;
        // right is non-null: result is the non-null common type
        var cv = ClassifyImplicit(right, rt, underlying);
        if (cv.Exists) { resultType = underlying; }
        else
        {
            var common = BestCommonType([UnwrapForCommon(left), right], b);
            if (common == null) { Report(Descriptors.NoBestCommonType, b, lt.ToDisplayString(), rt.ToDisplayString()); return Bad(b, left, right); }
            resultType = common;
            left = ConvertImplicit(UnwrapNullableValueIfNeeded(left), resultType, b.Left);
        }
        if (lt is not NullableTypeSymbol && lt.IsValueType)
            Report(Descriptors.CoalesceOnNonNullable, b.OperatorToken, lt.ToDisplayString());
        return new BoundCoalesce(left, ConvertImplicit(right, resultType, b.Right), resultType) { Syntax = b };
    }

    private BoundExpression UnwrapForCommon(BoundExpression e) => e;

    private BoundExpression UnwrapNullableValueIfNeeded(BoundExpression e) => e;

    // ------------------------------------------------------------------ arithmetic / comparison

    private static SpecialType Promote(SpecialType t) => t switch
    {
        SpecialType.SByte or SpecialType.Byte or SpecialType.Int16 or SpecialType.UInt16 or SpecialType.Char => SpecialType.Int32,
        _ => t,
    };

    /// <summary>Binary numeric promotion; returns None if the operands have no common arithmetic type.</summary>
    private SpecialType BinaryPromotion(BoundExpression l, BoundExpression r)
    {
        var a = l.Type!.SpecialType;
        var b = r.Type!.SpecialType;
        if (!a.IsNumeric() || !b.IsNumeric()) return SpecialType.None;
        if (a == SpecialType.Decimal || b == SpecialType.Decimal)
        {
            var other = a == SpecialType.Decimal ? b : a;
            return other.IsFloating() ? SpecialType.None : SpecialType.Decimal;
        }
        if (a == SpecialType.Double || b == SpecialType.Double) return SpecialType.Double;
        if (a == SpecialType.Single || b == SpecialType.Single) return SpecialType.Single;
        if (a == SpecialType.IntPtr || b == SpecialType.IntPtr) return SpecialType.IntPtr;
        if (a == SpecialType.UIntPtr || b == SpecialType.UIntPtr) return SpecialType.UIntPtr;
        a = Promote(a); b = Promote(b);
        if (a == SpecialType.UInt64 || b == SpecialType.UInt64)
        {
            var other = a == SpecialType.UInt64 ? b : a;
            var otherExpr = a == SpecialType.UInt64 ? r : l;
            if (other.IsSignedIntegral())
            {
                // signed constant that fits is allowed
                if (otherExpr.ConstantValue is { Value: { } cv } && ConstantFits(cv, SpecialType.UInt64)) return SpecialType.UInt64;
                return SpecialType.None;
            }
            return SpecialType.UInt64;
        }
        if (a == SpecialType.Int64 || b == SpecialType.Int64) return SpecialType.Int64;
        if (a == SpecialType.UInt32 || b == SpecialType.UInt32)
        {
            var other = a == SpecialType.UInt32 ? b : a;
            var otherExpr = a == SpecialType.UInt32 ? r : l;
            if (other == SpecialType.Int32)
            {
                if (otherExpr.ConstantValue is { Value: int ci } && ci >= 0) return SpecialType.UInt32;
                return SpecialType.Int64;
            }
            return SpecialType.UInt32;
        }
        return SpecialType.Int32;
    }

    private BoundExpression BindArithmeticOrComparison(SyntaxNode syntax, BinaryOperatorKind kind, BoundExpression left, BoundExpression right)
    {
        var lt = left.Type;
        var rt = right.Type;
        if (lt == null || rt == null)
        {
            if (lt == null) ReportNotAValue(left, syntax);
            if (rt == null) ReportNotAValue(right, syntax);
            return Bad(syntax, left, right);
        }
        if (lt.IsErrorType || rt.IsErrorType) return Bad(syntax, left, right);
        var isComparison = kind is >= BinaryOperatorKind.LessThan and <= BinaryOperatorKind.GreaterThanOrEqual;

        // string concatenation
        if (kind == BinaryOperatorKind.Add && (lt.SpecialType == SpecialType.String || rt.SpecialType == SpecialType.String))
        {
            if (lt.IsVoid || rt.IsVoid) { ReportBadOperands(syntax, kind, lt, rt); return Bad(syntax, left, right); }
            if (lt is NullableTypeSymbol { Underlying.SpecialType: SpecialType.String } || rt is NullableTypeSymbol { Underlying.SpecialType: SpecialType.String } || lt.IsNullType || rt.IsNullType)
            {
                // allowed: null concatenates as empty (C# semantics) but only for explicit string | null
            }
            else if (lt is NullableTypeSymbol || rt is NullableTypeSymbol)
            {
                ReportPossiblyNullOperand(left, right, syntax);
                return Bad(syntax, left, right);
            }
            var l2 = lt.StripNullable().SpecialType == SpecialType.String ? left : left;
            return BuildStringConcat([StringOperand(left), StringOperand(right)], syntax);
        }

        if (lt is NullableTypeSymbol || rt is NullableTypeSymbol)
        {
            ReportPossiblyNullOperand(left, right, syntax);
            return Bad(syntax, left, right);
        }

        // user-defined operators (also covers decimal, BigInteger, DateTime ...)
        var opName = OperatorMethodName(kind);
        if (opName != null && !(IsPrimitiveArithmetic(lt) && IsPrimitiveArithmetic(rt)))
        {
            var udo = TryUserBinary(opName, left, right, syntax);
            if (udo != null) return udo;
        }

        // enums
        if (lt.TypeKind == TypeKind.Enum || rt.TypeKind == TypeKind.Enum)
            return BindEnumOperator(syntax, kind, left, right);

        if (kind is BinaryOperatorKind.ShiftLeft or BinaryOperatorKind.ShiftRight or BinaryOperatorKind.UnsignedShiftRight)
            return BindShift(syntax, kind, left, right);

        if (kind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.Xor && lt.SpecialType == SpecialType.Boolean && rt.SpecialType == SpecialType.Boolean)
            return MakeBinary(syntax, kind, left, right, BoolType, BoolType);

        // string comparison
        if (isComparison && lt.SpecialType == SpecialType.String && rt.SpecialType == SpecialType.String)
        {
            var cmp = Special(SpecialType.String).GetMembers("CompareOrdinal").OfType<MethodSymbol>().First(m => m.Parameters.Length == 2);
            var call = new BoundCall(null, cmp, [left, right]) { Syntax = syntax };
            return MakeBinary(syntax, kind, call, new BoundLiteral(new ConstantValue(0), IntType), BoolType, IntType);
        }

        var promoted = BinaryPromotion(left, right);
        if (promoted == SpecialType.None)
        {
            // char vs string literal etc.
            ReportBadOperands(syntax, kind, lt, rt);
            return Bad(syntax, left, right);
        }
        var opType = Special(promoted);
        var lc = ConvertImplicit(left, opType, syntax);
        var rc = ConvertImplicit(right, opType, syntax);
        if (lc.HasErrors || rc.HasErrors) return Bad(syntax, left, right);
        if (kind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.Xor && !promoted.IsIntegral())
        {
            ReportBadOperands(syntax, kind, lt, rt);
            return Bad(syntax, left, right);
        }
        if (promoted == SpecialType.Decimal || promoted == SpecialType.IntPtr || promoted == SpecialType.UIntPtr) { }
        var resultType = isComparison ? BoolType : opType;
        return MakeBinary(syntax, kind, lc, rc, resultType, opType);
    }

    private static bool IsPrimitiveArithmetic(TypeSymbol t) =>
        t.SpecialType.IsNumeric() && t.SpecialType != SpecialType.Decimal && t.SpecialType != SpecialType.IntPtr && t.SpecialType != SpecialType.UIntPtr
        || t.SpecialType == SpecialType.Boolean;

    private BoundExpression StringOperand(BoundExpression e)
    {
        var t = e.Type!;
        if (t.StripNullable().SpecialType == SpecialType.String) return e;
        if (t.IsNullType) return new BoundLiteral(new ConstantValue(""), StringType) { Syntax = e.Syntax };
        return e;
    }

    private BoundExpression MakeBinary(SyntaxNode syntax, BinaryOperatorKind kind, BoundExpression l, BoundExpression r, TypeSymbol resultType, TypeSymbol opType)
    {
        ConstantValue? folded = null;
        if (l.ConstantValue is { Value: { } lv } && r.ConstantValue is { Value: { } rv } && opType.SpecialType != SpecialType.None)
        {
            var f = ConstantFolding.FoldBinary(kind, lv, rv, opType.SpecialType);
            if (f != null) folded = new ConstantValue(f);
        }
        return new BoundBinary(kind, l, r, resultType, opType) { Syntax = syntax, Folded = folded };
    }

    private BoundExpression BindShift(SyntaxNode syntax, BinaryOperatorKind kind, BoundExpression left, BoundExpression right)
    {
        var lt = Promote(left.Type!.SpecialType);
        if (!lt.IsIntegral() || lt is SpecialType.Char)
        {
            ReportBadOperands(syntax, kind, left.Type!, right.Type!);
            return Bad(syntax, left, right);
        }
        var leftType = Special(lt);
        var lc = ConvertImplicit(left, leftType, syntax);
        var rc = ConvertImplicit(right, IntType, syntax);
        if (lc.HasErrors || rc.HasErrors) return Bad(syntax, left, right);
        return MakeBinary(syntax, kind, lc, rc, leftType, leftType);
    }

    private BoundExpression BindEnumOperator(SyntaxNode syntax, BinaryOperatorKind kind, BoundExpression left, BoundExpression right)
    {
        var lt = left.Type!;
        var rt = right.Type!;
        var isComparison = kind is >= BinaryOperatorKind.LessThan and <= BinaryOperatorKind.GreaterThanOrEqual;
        if (lt.TypeKind == TypeKind.Enum && rt.TypeKind == TypeKind.Enum && lt.Equals(rt))
        {
            var under = ((NamedTypeSymbol)lt).EnumUnderlyingType!;
            if (isComparison || kind is BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.Xor)
                return MakeBinary(syntax, kind, left, right, isComparison ? BoolType : lt, under);
            if (kind == BinaryOperatorKind.Subtract)
                return MakeBinary(syntax, kind, left, right, under, under);
        }
        else if (lt.TypeKind == TypeKind.Enum && rt.SpecialType.IsIntegral() && kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
        {
            var under = ((NamedTypeSymbol)lt).EnumUnderlyingType!;
            return MakeBinary(syntax, kind, left, ConvertImplicit(right, under, syntax), lt, under);
        }
        ReportBadOperands(syntax, kind, lt, rt);
        return Bad(syntax, left, right);
    }

    private BoundExpression BindPower(BinaryExpressionSyntax b, BoundExpression left, BoundExpression right)
    {
        var lt = left.Type;
        var rt = right.Type;
        if (lt == null || rt == null) return Bad(b, left, right);
        if (lt.SpecialType == SpecialType.BigInteger)
        {
            var pow = lt.GetMembers("Pow").OfType<MethodSymbol>().First();
            return new BoundCall(null, pow, [left, rt.SpecialType == SpecialType.BigInteger ? ConvertExplicit(right, IntType, b) : ConvertImplicit(right, IntType, b)]) { Syntax = b };
        }
        if (!lt.SpecialType.IsNumeric() || !rt.SpecialType.IsNumeric() || lt.SpecialType == SpecialType.Char)
        {
            ReportBadOperands(b, BinaryOperatorKind.Multiply, lt, rt, "**");
            return Bad(b, left, right);
        }
        var math = _c.GetWellKnownType("System.Math");
        var pw = math?.GetMembers("Pow").OfType<MethodSymbol>().FirstOrDefault();
        if (pw == null) { Report(Descriptors.RuntimeMissing, b, "System.Math.Pow"); return Bad(b); }
        return new BoundCall(null, pw, [ConvertImplicit(left, DoubleType, b), ConvertImplicit(right, DoubleType, b)]) { Syntax = b };
    }

    private BoundExpression? TryUserBinary(string opName, BoundExpression left, BoundExpression right, SyntaxNode syntax)
    {
        var cands = new List<MethodSymbol>();
        void Collect(TypeSymbol t)
        {
            t = t.StripNullable();
            for (var x = t; x != null; x = x.BaseType)
            {
                foreach (var m in x.GetMembers(opName))
                    if (m is MethodSymbol { IsStatic: true, Parameters.Length: 2 } ms && IsAccessible(ms) && !cands.Contains(ms)) cands.Add(ms);
                if (x is not NamedTypeSymbol) break;
            }
        }
        Collect(left.Type!);
        Collect(right.Type!);
        if (cands.Count == 0) return null;
        var res = ResolveOverloads(cands, [left, right], ImmutableArray<TypeSymbol>.Empty, syntax, opName, report: false);
        if (res == null) return null;
        return new BoundCall(null, res.Method, res.Arguments) { Syntax = syntax };
    }

    private void ReportBadOperands(SyntaxNode syntax, BinaryOperatorKind kind, TypeSymbol l, TypeSymbol r, string? text = null)
    {
        var op = text ?? kind switch
        {
            BinaryOperatorKind.Add => "+", BinaryOperatorKind.Subtract => "-", BinaryOperatorKind.Multiply => "*", BinaryOperatorKind.Divide => "/",
            BinaryOperatorKind.Remainder => "%", BinaryOperatorKind.ShiftLeft => "<<", BinaryOperatorKind.ShiftRight => ">>",
            BinaryOperatorKind.UnsignedShiftRight => ">>>", BinaryOperatorKind.And => "&", BinaryOperatorKind.Or => "|", BinaryOperatorKind.Xor => "^",
            BinaryOperatorKind.LessThan => "<", BinaryOperatorKind.LessThanOrEqual => "<=", BinaryOperatorKind.GreaterThan => ">",
            BinaryOperatorKind.GreaterThanOrEqual => ">=", BinaryOperatorKind.Equal => "===", BinaryOperatorKind.NotEqual => "!==", _ => "?",
        };
        Report(Descriptors.BadOperands, syntax is BinaryExpressionSyntax be ? be.OperatorToken.Span : syntax.Span, op, l.ToDisplayString(), r.ToDisplayString());
    }

    private void ReportPossiblyNullOperand(BoundExpression l, BoundExpression r, SyntaxNode syntax)
    {
        var which = l.Type is NullableTypeSymbol ? l : r;
        Report(Descriptors.PossiblyNull, which.Syntax ?? syntax, which.Syntax?.ToFullString().Trim() ?? "expression");
    }

    // ------------------------------------------------------------------ equality

    private BoundExpression BindEquality(SyntaxNode syntax, BoundExpression left, BoundExpression right, BinaryOperatorKind kind)
    {
        if (left is BoundBadExpression || right is BoundBadExpression) return Bad(syntax, left, right);
        var lt = left.Type;
        var rt = right.Type;
        if (lt == null || rt == null)
        {
            if (lt == null) ReportNotAValue(left, syntax);
            if (rt == null) ReportNotAValue(right, syntax);
            return Bad(syntax, left, right);
        }
        if (lt.IsErrorType || rt.IsErrorType) return Bad(syntax, left, right);

        // comparisons with null
        var leftNull = left is BoundLiteral { Value.IsNull: true };
        var rightNull = right is BoundLiteral { Value.IsNull: true };
        if (leftNull || rightNull)
        {
            var other = leftNull ? right : left;
            var ot = other.Type!;
            if (leftNull && rightNull) return new BoundLiteral(new ConstantValue(kind == BinaryOperatorKind.Equal), BoolType) { Syntax = syntax };
            return new BoundBinary(kind, other, new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance), BoolType, ot) { Syntax = syntax };
        }

        // user-defined operator==
        if (!(IsPrimitiveArithmetic(lt) && IsPrimitiveArithmetic(rt)))
        {
            var udo = TryUserBinary(kind == BinaryOperatorKind.Equal ? "op_Equality" : "op_Inequality", left, right, syntax);
            if (udo != null) return udo;
        }

        var lu = lt.StripNullable();
        var ru = rt.StripNullable();
        var anyNullableValue = (lt is NullableTypeSymbol && lu.IsValueType) || (rt is NullableTypeSymbol && ru.IsValueType);

        // numeric
        if (lu.SpecialType.IsNumeric() && ru.SpecialType.IsNumeric() && !anyNullableValue)
        {
            var promoted = BinaryPromotion(left, right);
            if (promoted != SpecialType.None)
            {
                var opType = Special(promoted);
                return MakeBinary(syntax, kind, ConvertImplicit(left, opType, syntax), ConvertImplicit(right, opType, syntax), BoolType, opType);
            }
        }
        if (lu.SpecialType == SpecialType.Boolean && ru.SpecialType == SpecialType.Boolean && !anyNullableValue)
            return MakeBinary(syntax, kind, left, right, BoolType, BoolType);
        if (lu.SpecialType == SpecialType.Char && ru.SpecialType == SpecialType.Char && !anyNullableValue)
            return MakeBinary(syntax, kind, left, right, BoolType, Special(SpecialType.Char));
        if (lu.TypeKind == TypeKind.Enum && ru.TypeKind == TypeKind.Enum && lu.Equals(ru) && !anyNullableValue)
            return MakeBinary(syntax, kind, left, right, BoolType, ((NamedTypeSymbol)lu).EnumUnderlyingType!);
        // enum vs string-literal constant
        if (lu.TypeKind == TypeKind.Enum && right.ConstantValue is { Value: string })
        {
            var rc = ConvertImplicit(right, lu, syntax);
            if (!rc.HasErrors) return MakeBinary(syntax, kind, left, rc, BoolType, ((NamedTypeSymbol)lu).EnumUnderlyingType!);
            return Bad(syntax, left, right);
        }
        if (ru.TypeKind == TypeKind.Enum && left.ConstantValue is { Value: string })
        {
            var lc = ConvertImplicit(left, ru, syntax);
            if (!lc.HasErrors) return MakeBinary(syntax, kind, lc, right, BoolType, ((NamedTypeSymbol)ru).EnumUnderlyingType!);
            return Bad(syntax, left, right);
        }
        // char vs 1-char string literal
        if (lu.SpecialType == SpecialType.Char && right.ConstantValue is { Value: string })
        {
            var rc = ConvertImplicit(right, lu, syntax);
            if (!rc.HasErrors) return MakeBinary(syntax, kind, left, rc, BoolType, lu);
        }

        // reference types: reference equality when one converts to the other
        if ((lu.IsReferenceType || lu is TypeParameterSymbol) && (ru.IsReferenceType || ru is TypeParameterSymbol) && !anyNullableValue)
        {
            if (ClassifyImplicit(null, lu, ru).Exists || ClassifyImplicit(null, ru, lu).Exists || ClassifyExplicit(null, lu, ru).Exists)
            {
                var lo = lt is NullableTypeSymbol ? left : left;
                return new BoundBinary(kind, left, right, BoolType, ObjectType) { Syntax = syntax };
            }
        }

        // anything else comparable via Object.Equals (value types without operators, nullable values, tuples)
        if (ClassifyImplicit(null, lu, ru).Exists || ClassifyImplicit(null, ru, lu).Exists)
        {
            var common = ClassifyImplicit(null, lu, ru).Exists ? ru : lu;
            var objEquals = Special(SpecialType.Object).GetMembers("Equals").OfType<MethodSymbol>().First(m => m.IsStatic && m.Parameters.Length == 2);
            var call = new BoundCall(null, objEquals, [BoxToObject(left, syntax), BoxToObject(right, syntax)]) { Syntax = syntax };
            if (kind == BinaryOperatorKind.Equal) return call;
            return new BoundUnary(UnaryOperatorKind.Not, call, BoolType) { Syntax = syntax };
        }
        Report(Descriptors.NoOverlap, syntax is BinaryExpressionSyntax be ? be.OperatorToken.Span : syntax.Span, lt.ToDisplayString(), rt.ToDisplayString());
        return Bad(syntax, left, right);
    }

    // ------------------------------------------------------------------ unary

    private BoundExpression BindPrefixUnary(PrefixUnaryExpressionSyntax pu, TypeSymbol? target)
    {
        var opKind = pu.OperatorToken.Kind;
        if (opKind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
            return BindIncrementDecrement(pu.Operand, opKind == SyntaxKind.PlusPlusToken, isPrefix: true, valueUsed: true, pu);
        if (opKind == SyntaxKind.VoidKeyword)
        {
            BindValue(pu.Operand);
            return Bad(pu);
        }
        var operand = BindValue(pu.Operand);
        if (operand is BoundBadExpression) return Bad(pu, operand);
        var t = operand.Type;
        if (t == null) { ReportNotAValue(operand, pu); return Bad(pu, operand); }
        if (t.IsErrorType) return Bad(pu, operand);

        switch (opKind)
        {
            case SyntaxKind.ExclamationToken:
            {
                var b = ConvertToBool(operand, pu.Operand);
                if (b.HasErrors) return Bad(pu, operand);
                ConstantValue? folded = b.ConstantValue is { Value: bool bv } ? new ConstantValue(!bv) : null;
                return new BoundUnary(UnaryOperatorKind.Not, b, BoolType) { Syntax = pu, Folded = folded };
            }
            case SyntaxKind.MinusToken or SyntaxKind.PlusToken or SyntaxKind.TildeToken:
            {
                if (t is NullableTypeSymbol) { ReportPossiblyNull(operand, null, pu); return Bad(pu, operand); }
                var uk = opKind switch { SyntaxKind.MinusToken => UnaryOperatorKind.Minus, SyntaxKind.PlusToken => UnaryOperatorKind.Plus, _ => UnaryOperatorKind.BitwiseNot };
                // negative literal folding (handles int.MinValue written as -2147483648)
                if (uk == UnaryOperatorKind.Minus && operand is BoundLiteral { Value.Value: long lv } && lv == 2147483648L && pu.Operand is LiteralExpressionSyntax)
                    return new BoundLiteral(new ConstantValue(int.MinValue), IntType) { Syntax = pu };
                if (uk == UnaryOperatorKind.Minus && operand is BoundLiteral { Value.Value: ulong ulv } && ulv == 9223372036854775808UL)
                    return new BoundLiteral(new ConstantValue(long.MinValue), Special(SpecialType.Int64)) { Syntax = pu };
                var opName = uk switch { UnaryOperatorKind.Minus => "op_UnaryNegation", UnaryOperatorKind.Plus => "op_UnaryPlus", _ => "op_OnesComplement" };
                if (!t.SpecialType.IsNumeric() || t.SpecialType is SpecialType.Decimal or SpecialType.IntPtr or SpecialType.UIntPtr)
                {
                    var cands = LookupMembers(t, opName).OfType<MethodSymbol>().Where(m => m.IsStatic && m.Parameters.Length == 1).ToList();
                    if (cands.Count > 0)
                    {
                        var res = ResolveOverloads(cands, [operand], ImmutableArray<TypeSymbol>.Empty, pu, opName, report: false);
                        if (res != null) return new BoundCall(null, res.Method, res.Arguments) { Syntax = pu };
                    }
                    if (t.TypeKind == TypeKind.Enum && uk == UnaryOperatorKind.BitwiseNot)
                        return new BoundUnary(uk, operand, t) { Syntax = pu };
                    Report(Descriptors.BadUnaryOperand, pu.OperatorToken, pu.OperatorToken.Text, t.ToDisplayString());
                    return Bad(pu, operand);
                }
                var promoted = Promote(t.SpecialType);
                if (uk == UnaryOperatorKind.Minus && promoted == SpecialType.UInt32) promoted = SpecialType.Int64;
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
                var rt = Special(promoted);
                var conv = ConvertImplicit(operand, rt, pu);
                ConstantValue? folded = null;
                if (conv.ConstantValue is { Value: { } cv })
                {
                    var f = ConstantFolding.FoldUnary(uk, cv, promoted);
                    if (f != null) folded = new ConstantValue(f);
                }
                return new BoundUnary(uk, conv, rt) { Syntax = pu, Folded = folded };
            }
        }
        Report(Descriptors.FeatureNotSupported, pu, "Operator '" + pu.OperatorToken.Text + "'");
        return Bad(pu, operand);
    }

    private BoundExpression BindPostfixUnary(PostfixUnaryExpressionSyntax pf, bool valueUsed) =>
        BindIncrementDecrement(pf.Operand, pf.OperatorToken.Kind == SyntaxKind.PlusPlusToken, isPrefix: false, valueUsed, pf);
}
