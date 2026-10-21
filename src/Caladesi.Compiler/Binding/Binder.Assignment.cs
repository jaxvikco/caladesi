using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    private SyntaxNode? _assignTargetSyntax;
    private bool _inConstructor;
    private bool _inStaticConstructor;
    private bool _inInitializer;

    private static ExpressionSyntax Unparenthesize(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        return e;
    }

    /// <summary>Binds an assignment target (no flow narrowing on the outermost node) and verifies it is assignable.</summary>
    private BoundExpression BindAssignmentTarget(ExpressionSyntax left, bool forCompound = false)
    {
        var syntax = Unparenthesize(left);
        var saved = _assignTargetSyntax;
        _assignTargetSyntax = syntax;
        BoundExpression target;
        try { target = BindValue(syntax); }
        finally { _assignTargetSyntax = saved; }
        return CheckAssignable(target, syntax, forCompound);
    }

    private BoundExpression CheckAssignable(BoundExpression target, SyntaxNode syntax, bool forCompound)
    {
        switch (target)
        {
            case BoundBadExpression:
                return target;
            case BoundLocal l:
                if (l.Local.IsConst)
                {
                    Report(Descriptors.CannotAssignConst, syntax, l.Local.Name);
                    return Bad(syntax, target);
                }
                l.Local.IsReassigned = true;
                return target;
            case BoundParameter p:
                p.Parameter.IsReassigned = true;
                return target;
            case BoundFieldAccess f:
            {
                if (f.Field.IsConst || f.Field is SourceFieldSymbol { IsEnumMember: true })
                {
                    Report(Descriptors.CannotAssignConst, syntax, f.Field.Name);
                    return Bad(syntax, target);
                }
                if (f.Field.IsReadOnly && !CanAssignReadonly(f.Field, f.Receiver))
                {
                    Report(Descriptors.CannotAssignReadonly, syntax, f.Field.Name);
                    return Bad(syntax, target);
                }
                if (f.Field is SourceFieldSymbol { IsConstDeclaration: true, IsModuleVariable: true })
                {
                    Report(Descriptors.CannotAssignConst, syntax, f.Field.Name);
                    return Bad(syntax, target);
                }
                return target;
            }
            case BoundPropertyAccess pa:
            {
                var prop = pa.Property;
                if (prop.SetMethod == null)
                {
                    // auto property assigned in its own constructor: write the backing field
                    if (prop is SourcePropertySymbol { BackingField: { } bf } && (_inConstructor || _inStaticConstructor || _inInitializer) && IsOwnMember(prop))
                        return new BoundFieldAccess(pa.Receiver, bf) { Syntax = target.Syntax };
                    Report(Descriptors.CannotAssignReadonlyProperty, syntax, prop.Name);
                    return Bad(syntax, target);
                }
                if (prop.SetMethod.IsInitOnly && !(_inConstructor || _inInitializer || _inStaticConstructor))
                {
                    Report(Descriptors.CannotAssignInitOnly, syntax, prop.Name);
                    return Bad(syntax, target);
                }
                return target;
            }
            case BoundArrayAccess:
                return target;
            case BoundConversion { Kind: ConversionKind.FromNullable or ConversionKind.TypeOnly or ConversionKind.ExplicitReference or ConversionKind.UnionExtract } c:
                // narrowed reads: assign through to the underlying storage
                return CheckAssignable(c.Operand, syntax, forCompound);
        }
        Report(Descriptors.InvalidAssignmentTarget, syntax);
        return Bad(syntax, target);
    }

    private bool IsOwnMember(Symbol member)
    {
        var owner = member.ContainingType?.OriginalDefinition;
        var cur = CurrentType?.OriginalDefinition;
        return owner != null && ReferenceEquals(owner, cur);
    }

    private bool CanAssignReadonly(FieldSymbol f, BoundExpression? receiver)
    {
        if (!IsOwnMember(f)) return false;
        if (f.IsStatic) return _inStaticConstructor || _inInitializer;
        return (_inConstructor || _inInitializer) && receiver is BoundThis or null;
    }

    // ------------------------------------------------------------------ assignment

    private BoundExpression BindAssignment(AssignmentExpressionSyntax a, bool valueUsed)
    {
        var opKind = a.OperatorToken.Kind;
        var leftSyntax = Unparenthesize(a.Left);

        if (opKind == SyntaxKind.EqualsToken && leftSyntax is ArrayLiteralExpressionSyntax or ObjectLiteralExpressionSyntax)
            return BindDestructuringAssignment(a, leftSyntax);

        var target = BindAssignmentTarget(a.Left, forCompound: opKind != SyntaxKind.EqualsToken);
        if (target is BoundBadExpression)
        {
            BindValue(a.Right);
            return Bad(a, target);
        }
        var targetType = target.Type!;

        if (opKind == SyntaxKind.EqualsToken)
        {
            var rawValue = BindValue(a.Right, targetType);
            if (rawValue is BoundBadExpression) return Bad(a, target, rawValue);
            var value = ConvertImplicit(rawValue, targetType, a.Right);
            if (value.HasErrors) return Bad(a, target, value);
            var assign = new BoundAssignment(target, value) { Syntax = a, IsDiscarded = !valueUsed };
            AfterAssignment(target, rawValue, targetType);
            return assign;
        }

        // compound
        switch (opKind)
        {
            case SyntaxKind.QuestionQuestionEqualsToken:
                return BindCoalesceAssign(a, target);
            case SyntaxKind.AmpersandAmpersandEqualsToken or SyntaxKind.BarBarEqualsToken:
            {
                var isAnd = opKind == SyntaxKind.AmpersandAmpersandEqualsToken;
                return BuildCompound(target, valueUsed, returnOld: false, a, read =>
                {
                    var l = ConvertToBool(read, a.Left);
                    var r = ConvertToBool(BindValue(a.Right), a.Right);
                    return new BoundLogicalBinary(isAnd, l, r, BoolType) { Syntax = a };
                });
            }
        }
        var (_, count) = (opKind, 1);
        var binKind = ToBinaryKind(opKind) ?? ToBinaryKind(opKind switch
        {
            SyntaxKind.AsteriskAsteriskEqualsToken => SyntaxKind.AsteriskAsteriskToken,
            _ => opKind,
        });
        if (opKind == SyntaxKind.AsteriskAsteriskEqualsToken)
        {
            return BuildCompound(target, valueUsed, returnOld: false, a, read =>
            {
                var r = BindValue(a.Right);
                var pw = BindPower(MakeFakeBinary(a), read, r);
                return pw;
            });
        }
        if (binKind == null)
        {
            Report(Descriptors.FeatureNotSupported, a.OperatorToken, "Operator '" + a.OperatorToken.Text + "'");
            return Bad(a, target);
        }
        var bk = binKind.Value;
        // bind the right operand once, outside the closure (it must not be bound twice)
        var rightBound = BindValue(a.Right);
        if (rightBound is BoundBadExpression) return Bad(a, target, rightBound);
        return BuildCompound(target, valueUsed, returnOld: false, a, read =>
        {
            var result = BindArithmeticOrComparison(a, bk, read, rightBound);
            if (result.HasErrors) return result;
            // implicit narrowing back to the target type, as in C#
            if (!ClassifyImplicit(result, result.Type!, targetType).Exists)
            {
                var ex = ClassifyExplicit(result, result.Type!, targetType);
                if (ex.Exists && IsNumericType(result.Type!) && IsNumericType(targetType)) return CreateConversion(result, ex, targetType, a);
                if (targetType.StripNullable().Equals(result.Type)) return ConvertImplicit(result, targetType, a);
                Report(Descriptors.NotAssignable, a, result.Type!.ToDisplayString(), targetType.ToDisplayString());
                return Bad(a, result);
            }
            return ConvertImplicit(result, targetType, a);
        });
    }

    private BinaryExpressionSyntax MakeFakeBinary(AssignmentExpressionSyntax a) =>
        new(a.Left, a.OperatorToken, a.Right);

    private void AfterAssignment(BoundExpression target, BoundExpression rawValue, TypeSymbol declaredType)
    {
        InvalidateNarrowing(target);
        var path = PathOf(target);
        if (path == null || rawValue.Type == null) return;
        var vt = rawValue.Type;
        // flow typing: x = "a" narrows `string | null` to string; assigning a union case narrows the union
        if (declaredType is NullableTypeSymbol dn && vt is not NullableTypeSymbol && !vt.IsNullType && ClassifyImplicit(null, vt, dn.Underlying).Exists)
            _narrow = _narrow.SetItem(path, vt.Equals(dn.Underlying) ? dn.Underlying : vt);
        else if (declaredType is NamedTypeSymbol { IsUnionType: true } u && u.UnionCases.Any(c => c.Equals(vt)))
            _narrow = _narrow.SetItem(path, vt);
    }

    private BoundExpression BindCoalesceAssign(AssignmentExpressionSyntax a, BoundExpression target)
    {
        var tt = target.Type!;
        if (tt is not NullableTypeSymbol nt)
        {
            Report(Descriptors.CoalesceOnNonNullable, a.OperatorToken, tt.ToDisplayString());
            BindValue(a.Right);
            return Bad(a, target);
        }
        // x ??= y  =>  x ?? (x = y)
        return BuildCompound(target, valueUsed: true, returnOld: false, a, read =>
        {
            var rhs = ConvertImplicit(BindValue(a.Right, nt.Underlying), nt, a.Right);
            return new BoundCoalesce(read, rhs, nt) { Syntax = a };
        }, coalesceMode: true);
    }

    // ------------------------------------------------------------------ increment / decrement

    private BoundExpression BindIncrementDecrement(ExpressionSyntax operand, bool isIncrement, bool isPrefix, bool valueUsed, SyntaxNode syntax)
    {
        var target = BindAssignmentTarget(operand, forCompound: true);
        if (target is BoundBadExpression) return target;
        var t = target.Type!;
        var st = t.StripNullable().SpecialType;
        if (t is NullableTypeSymbol) { ReportPossiblyNull(target, null, syntax); return Bad(syntax, target); }
        if (!st.IsNumeric() || st == SpecialType.Decimal && false)
        {
            // user-defined ++/-- is not supported; fall back to + 1 for decimal / BigInteger
            if (t.SpecialType != SpecialType.Decimal && t.SpecialType != SpecialType.BigInteger)
            {
                Report(Descriptors.BadUnaryOperand, syntax, isIncrement ? "++" : "--", t.ToDisplayString());
                return Bad(syntax, target);
            }
        }
        return BuildCompound(target, valueUsed, returnOld: !isPrefix && valueUsed, syntax, read =>
        {
            var one = ConvertImplicit(new BoundLiteral(new ConstantValue(1), IntType), t.SpecialType == SpecialType.BigInteger || t.SpecialType == SpecialType.Decimal ? t : (IsNumericType(t) ? (st.IsFloating() ? t : IntType) : t), syntax);
            var result = BindArithmeticOrComparison(syntax, isIncrement ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract, read, one);
            if (result.HasErrors) return result;
            if (result.Type!.Equals(t)) return result;
            var ex = ClassifyExplicit(result, result.Type!, t);
            return ex.Exists ? CreateConversion(result, ex, t, syntax) : result;
        });
    }

    // ------------------------------------------------------------------ compound desugaring

    private static bool IsSimpleOperand(BoundExpression e) => e switch
    {
        BoundLocal or BoundParameter or BoundThis or BoundLiteral or BoundDefaultValue => true,
        BoundFieldAccess { Receiver: null } => true,
        BoundConversion { Kind: ConversionKind.ImplicitReference or ConversionKind.TypeOnly } c => IsSimpleOperand(c.Operand),
        _ => false,
    };

    /// <summary>
    /// Builds <c>target = compute(target)</c> evaluating the target's receiver/indices once.
    /// When <paramref name="returnOld"/> is set the result is the value before the update (postfix ++/--).
    /// </summary>
    private BoundExpression BuildCompound(BoundExpression target, bool valueUsed, bool returnOld, SyntaxNode syntax,
        Func<BoundExpression, BoundExpression> compute, bool coalesceMode = false)
    {
        var locals = new List<LocalSymbol>();
        var init = new List<BoundStatement>();

        BoundExpression Stabilize(BoundExpression e)
        {
            if (IsSimpleOperand(e)) return e;
            var tmp = new LocalSymbol("<t" + locals.Count + ">", e.Type!, false, _containing) { IsSynthesized = true };
            locals.Add(tmp);
            init.Add(new BoundLocalDeclaration(tmp, e) { Syntax = syntax });
            return new BoundLocal(tmp) { Syntax = syntax };
        }

        BoundExpression stable = target switch
        {
            BoundFieldAccess { Receiver: { } r } f => new BoundFieldAccess(Stabilize(r), f.Field) { Syntax = f.Syntax },
            BoundPropertyAccess p => new BoundPropertyAccess(p.Receiver == null ? null : Stabilize(p.Receiver), p.Property,
                p.Arguments.Select(Stabilize).ToImmutableArray()) { Syntax = p.Syntax, IsBaseAccess = p.IsBaseAccess },
            BoundArrayAccess aa => new BoundArrayAccess(Stabilize(aa.Array), Stabilize(aa.Index), aa.Type!) { Syntax = aa.Syntax },
            _ => target,
        };

        BoundExpression read = stable;
        if (returnOld)
        {
            var old = new LocalSymbol("<old>", target.Type!, false, _containing) { IsSynthesized = true };
            locals.Add(old);
            init.Add(new BoundLocalDeclaration(old, stable) { Syntax = syntax });
            var newValue = compute(new BoundLocal(old));
            if (newValue.HasErrors) return Bad(syntax, target);
            init.Add(new BoundExpressionStatement(new BoundAssignment(stable, newValue) { Syntax = syntax, IsDiscarded = true }) { Syntax = syntax });
            return new BoundSequence(locals.ToImmutableArray(), init.ToImmutableArray(), new BoundLocal(old)) { Syntax = syntax };
        }

        BoundExpression computed;
        if (coalesceMode)
        {
            // x ?? (x = y) : only assign when null
            var coalesce = (BoundCoalesce)compute(read);
            var assignRight = new BoundAssignment(stable, coalesce.Right) { Syntax = syntax };
            computed = new BoundCoalesce(read, assignRight, coalesce.Type!) { Syntax = syntax };
            if (locals.Count == 0) return computed;
            return new BoundSequence(locals.ToImmutableArray(), init.ToImmutableArray(), computed) { Syntax = syntax };
        }
        computed = compute(read);
        if (computed.HasErrors) return Bad(syntax, target);
        var assign = new BoundAssignment(stable, computed) { Syntax = syntax, IsDiscarded = !valueUsed };
        if (locals.Count == 0) return assign;
        return new BoundSequence(locals.ToImmutableArray(), init.ToImmutableArray(), assign) { Syntax = syntax };
    }
}
