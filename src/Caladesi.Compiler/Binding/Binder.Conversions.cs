using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    // ------------------------------------------------------------------ numeric tables

    private static readonly Dictionary<SpecialType, SpecialType[]> s_implicitNumeric = new()
    {
        [SpecialType.SByte] = [SpecialType.Int16, SpecialType.Int32, SpecialType.Int64, SpecialType.Single, SpecialType.Double, SpecialType.Decimal, SpecialType.IntPtr],
        [SpecialType.Byte] = [SpecialType.Int16, SpecialType.UInt16, SpecialType.Int32, SpecialType.UInt32, SpecialType.Int64, SpecialType.UInt64, SpecialType.Single, SpecialType.Double, SpecialType.Decimal, SpecialType.IntPtr, SpecialType.UIntPtr],
        [SpecialType.Int16] = [SpecialType.Int32, SpecialType.Int64, SpecialType.Single, SpecialType.Double, SpecialType.Decimal, SpecialType.IntPtr],
        [SpecialType.UInt16] = [SpecialType.Int32, SpecialType.UInt32, SpecialType.Int64, SpecialType.UInt64, SpecialType.Single, SpecialType.Double, SpecialType.Decimal, SpecialType.IntPtr, SpecialType.UIntPtr],
        [SpecialType.Int32] = [SpecialType.Int64, SpecialType.Single, SpecialType.Double, SpecialType.Decimal, SpecialType.IntPtr],
        [SpecialType.UInt32] = [SpecialType.Int64, SpecialType.UInt64, SpecialType.Single, SpecialType.Double, SpecialType.Decimal, SpecialType.UIntPtr],
        [SpecialType.Int64] = [SpecialType.Single, SpecialType.Double, SpecialType.Decimal],
        [SpecialType.UInt64] = [SpecialType.Single, SpecialType.Double, SpecialType.Decimal],
        [SpecialType.Char] = [SpecialType.UInt16, SpecialType.Int32, SpecialType.UInt32, SpecialType.Int64, SpecialType.UInt64, SpecialType.Single, SpecialType.Double, SpecialType.Decimal, SpecialType.IntPtr, SpecialType.UIntPtr],
        [SpecialType.Single] = [SpecialType.Double],
        [SpecialType.IntPtr] = [SpecialType.Int64, SpecialType.Single, SpecialType.Double, SpecialType.Decimal],
        [SpecialType.UIntPtr] = [SpecialType.UInt64, SpecialType.Single, SpecialType.Double, SpecialType.Decimal],
    };

    internal static bool HasImplicitNumeric(SpecialType from, SpecialType to)
    {
        return s_implicitNumeric.TryGetValue(from, out SpecialType[]? targets) && Array.IndexOf(targets, to) >= 0;
    }

    internal static bool IsNumericType(TypeSymbol t)
    {
        return t.SpecialType.IsNumeric() && t.SpecialType != SpecialType.None;
    }

    // ------------------------------------------------------------------ type relations

    internal static bool IsDerivedFrom(TypeSymbol derived, TypeSymbol baseType)
    {
        for (NamedTypeSymbol? t = derived.BaseType; t != null; t = t.BaseType)
        {
            if (t.Equals(baseType))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ImplementsInterface(TypeSymbol type, NamedTypeSymbol iface)
    {
        foreach (NamedTypeSymbol i in type.AllInterfaces)
        {
            if (i.Equals(iface))
            {
                return true;
            }
        }

        return false;
    }

    private bool ImplementsInterfaceVariant(TypeSymbol type, NamedTypeSymbol iface)
    {
        if (type is NamedTypeSymbol { TypeKind: TypeKind.Interface } self && IsVarianceConvertible(self, iface))
        {
            return true;
        }

        foreach (NamedTypeSymbol i in type.AllInterfaces)
        {
            if (i.Equals(iface) || IsVarianceConvertible(i, iface))
            {
                return true;
            }
        }

        return false;
    }

    internal bool IsVarianceConvertible(NamedTypeSymbol source, NamedTypeSymbol target)
    {
        if (!ReferenceEquals(source.OriginalDefinition, target.OriginalDefinition) || !source.IsGenericType)
        {
            return false;
        }

        NamedTypeSymbol def = source.OriginalDefinition;
        for (int i = 0; i < def.TypeParameters.Length; i++)
        {
            TypeSymbol sa = source.TypeArguments[i];
            TypeSymbol ta = target.TypeArguments[i];
            if (sa.Equals(ta))
            {
                continue;
            }

            switch (def.TypeParameters[i].Variance)
            {
                case VarianceKind.Out:
                    if (!(sa.IsReferenceType && ta.IsReferenceType && ImplicitReferenceConversionExists(sa, ta)))
                    {
                        return false;
                    }

                    break;
                case VarianceKind.In:
                    if (!(sa.IsReferenceType && ta.IsReferenceType && ImplicitReferenceConversionExists(ta, sa)))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>Implicit reference conversion (also identity) between reference types.</summary>
    internal bool ImplicitReferenceConversionExists(TypeSymbol source, TypeSymbol target)
    {
        if (source.Equals(target))
        {
            return true;
        }

        if (source is NullableTypeSymbol sn)
        {
            source = sn.Underlying;
        }

        if (target is NullableTypeSymbol tn)
        {
            target = tn.Underlying;
        }

        if (source.Equals(target))
        {
            return true;
        }

        if (!source.IsReferenceType && source is not TypeParameterSymbol)
        {
            return false;
        }

        if (target.SpecialType == SpecialType.Object)
        {
            return true;
        }

        if (source is ArrayTypeSymbol sa)
        {
            if (target is ArrayTypeSymbol ta)
            {
                return sa.Rank == ta.Rank && sa.ElementType.IsReferenceType && ta.ElementType.IsReferenceType && ImplicitReferenceConversionExists(sa.ElementType, ta.ElementType);
            }

            if (target.SpecialType == SpecialType.Array)
            {
                return true;
            }

            if (target is NamedTypeSymbol { IsGenericType: true } tgt && sa.Rank == 1)
            {
                string n = tgt.OriginalDefinition.ToMetadataString();
                if (n is "System.Collections.Generic.IList`1" or "System.Collections.Generic.ICollection`1" or "System.Collections.Generic.IEnumerable`1"
                    or "System.Collections.Generic.IReadOnlyList`1" or "System.Collections.Generic.IReadOnlyCollection`1")
                {
                    TypeSymbol ea = tgt.TypeArguments[0];
                    return sa.ElementType.Equals(ea) || (sa.ElementType.IsReferenceType && ea.IsReferenceType && ImplicitReferenceConversionExists(sa.ElementType, ea));
                }
            }
            return false;
        }
        if (source is TypeParameterSymbol sp)
        {
            foreach (TypeSymbol c in sp.ConstraintTypes)
            {
                if (ImplicitReferenceConversionExists(c, target))
                {
                    return true;
                }
            }

            return sp.HasReferenceTypeConstraint && target.SpecialType == SpecialType.Object;
        }
        if (target is NamedTypeSymbol tt)
        {
            if (tt.TypeKind == TypeKind.Interface)
            {
                return ImplementsInterface(source, tt) || ImplementsInterfaceVariant(source, tt);
            }
            if (IsDerivedFrom(source, tt))
            {
                return true;
            }

            if (source.TypeKind == TypeKind.Delegate && tt.SpecialType is SpecialType.Delegate or SpecialType.MulticastDelegate)
            {
                return true;
            }

            if (IsDelegateVarianceConvertible(source, tt))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsDelegateVarianceConvertible(TypeSymbol source, NamedTypeSymbol target)
    {
        return source is NamedTypeSymbol { TypeKind: TypeKind.Delegate } sd && target.TypeKind == TypeKind.Delegate && IsVarianceConvertible(sd, target);
    }

    internal bool BoxingConversionExists(TypeSymbol source, TypeSymbol target)
    {
        if (target is NullableTypeSymbol)
        {
            return false;
        }

        if (source is TypeParameterSymbol sp)
        {
            if (target.SpecialType is SpecialType.Object or SpecialType.ValueType)
            {
                return true;
            }

            foreach (TypeSymbol c in sp.ConstraintTypes)
            {
                if (ImplicitReferenceConversionExists(c, target) || c.Equals(target))
                {
                    return true;
                }
            }

            return false;
        }
        return source.IsValueType
            && (target.SpecialType == SpecialType.Object
                || (source.TypeKind == TypeKind.Enum && target.SpecialType is SpecialType.Enum or SpecialType.ValueType)
                || target.SpecialType == SpecialType.ValueType
                || (target is NamedTypeSymbol { TypeKind: TypeKind.Interface } ti && (ImplementsInterface(source, ti) || ImplementsInterfaceVariant(source, ti))));
    }

    // ------------------------------------------------------------------ classification

    internal Conversion ClassifyImplicit(BoundExpression? expr, TypeSymbol source, TypeSymbol target, bool allowUserDefined = true)
    {
        if (IsError(source) || IsError(target))
        {
            return Conversion.Identity;
        }

        if (source.Equals(target))
        {
            return Conversion.Identity;
        }

        if (source.IsNeverType)
        {
            return Conversion.Implicit(ConversionKind.TypeOnly);
        }

        if (target.IsVoid || source.IsVoid)
        {
            return Conversion.None;
        }

        if (source.IsNullType)
        {
            return target is NullableTypeSymbol ? Conversion.Implicit(ConversionKind.NullLiteral) : Conversion.None;
        }

        ConstantValue? cv = expr?.ConstantValue;
        if (cv.HasValue)
        {
            Conversion c = ClassifyConstantConversion(cv.Value, source, target);
            if (c.Exists)
            {
                return c;
            }
        }

        if (target is NullableTypeSymbol tn)
        {
            if (source is NullableTypeSymbol sn)
            {
                Conversion inner = ClassifyImplicit(null, sn.Underlying, tn.Underlying);
                return !inner.Exists
                    ? Conversion.None
                    : inner.IsIdentity
                    ? Conversion.Identity
                    : inner.Kind switch
                    {
                        ConversionKind.ImplicitNumeric or ConversionKind.EnumNumeric => Conversion.Implicit(ConversionKind.LiftedNumeric),
                        ConversionKind.Boxing when sn.Underlying.IsValueType || sn.Underlying is TypeParameterSymbol => Conversion.Implicit(ConversionKind.Boxing),
                        _ when sn.Underlying.IsValueType => Conversion.None,
                        _ => Conversion.Implicit(ConversionKind.TypeOnly),
                    };
            }
            Conversion c = ClassifyImplicit(expr, source, tn.Underlying);
            return !c.Exists ? Conversion.None : c.IsIdentity ? Conversion.Implicit(ConversionKind.ToNullable) : c.WithNullableWrap();
        }
        if (source is NullableTypeSymbol)
        {
            return Conversion.None; // T | null is not assignable to T
        }

        // numeric
        if (HasImplicitNumeric(source.SpecialType, target.SpecialType) && source.SpecialType != SpecialType.None)
        {
            return Conversion.Implicit(ConversionKind.ImplicitNumeric);
        }

        // a type parameter is only known to be a reference at run time; converting it to a reference type needs a box
        if (source is TypeParameterSymbol && BoxingConversionExists(source, target))
        {
            return Conversion.Implicit(ConversionKind.Boxing);
        }

        // reference
        if (ImplicitReferenceConversionExists(source, target))
        {
            return Conversion.Implicit(ConversionKind.ImplicitReference);
        }

        if (BoxingConversionExists(source, target))
        {
            return Conversion.Implicit(ConversionKind.Boxing);
        }

        // unions
        if (target is NamedTypeSymbol { IsUnionType: true } tu)
        {
            Conversion u = ClassifyToUnion(expr, source, tu);
            if (u.Exists)
            {
                return u;
            }
        }

        // user-defined
        if (allowUserDefined && _userDefinedDepth == 0)
        {
            Conversion ud = FindUserDefinedConversion(expr, source, target, isExplicit: false);
            if (ud.Exists)
            {
                return ud;
            }
        }

        // tuples
        if (source is NamedTypeSymbol { IsTuple: true } st && target is NamedTypeSymbol { IsTuple: true } tt
            && st.TypeArguments.Length == tt.TypeArguments.Length)
        {
            bool ok = true;
            for (int i = 0; i < st.TypeArguments.Length && ok; i++)
            {
                ok = ClassifyImplicit(null, st.TypeArguments[i], tt.TypeArguments[i]).Exists;
            }

            if (ok)
            {
                return Conversion.Implicit(ConversionKind.TypeOnly, caseIndex: -2); // marker: elementwise tuple conversion
            }
        }
        return Conversion.None;
    }

    private static Conversion ClassifyConstantConversion(ConstantValue cv, TypeSymbol source, TypeSymbol target)
    {
        SpecialType tSpecial = target.SpecialType;
        SpecialType sSpecial = source.SpecialType;
        // integer constant to narrower integral types
        if (cv.Value != null && sSpecial is SpecialType.Int32 or SpecialType.Int64 or SpecialType.UInt32 or SpecialType.UInt64 or SpecialType.Int16 or SpecialType.UInt16 or SpecialType.Byte or SpecialType.SByte)
        {
            if (tSpecial.IsIntegral() && tSpecial != SpecialType.Char && tSpecial != sSpecial && ConstantFits(cv.Value, tSpecial))
            {
                object? folded = ConstantFolding.ConvertNumeric(cv.Value, tSpecial);
                if (folded != null)
                {
                    return Conversion.Implicit(ConversionKind.ImplicitNumeric, c: new ConstantValue(folded));
                }
            }
        }
        if (cv.Value is string str)
        {
            if (tSpecial == SpecialType.Char && str.Length == 1)
            {
                return Conversion.Implicit(ConversionKind.ImplicitNumeric, c: new ConstantValue(str[0]));
            }

            if (target is SourceLiteralEnumSymbol le)
            {
                SourceFieldSymbol? member = le.FindMember(str);
                if (member != null)
                {
                    return Conversion.Implicit(ConversionKind.EnumNumeric, c: new ConstantValue(member.ConstantValue?.Value));
                }
            }
            if (target is NullableTypeSymbol { Underlying: SourceLiteralEnumSymbol le2 })
            {
                SourceFieldSymbol? member = le2.FindMember(str);
                if (member != null)
                {
                    return Conversion.Implicit(ConversionKind.EnumNumeric, c: new ConstantValue(member.ConstantValue?.Value)).WithNullableWrap();
                }
            }
        }
        return Conversion.None;
    }

    private static bool ConstantFits(object value, SpecialType to)
    {
        try
        {
            decimal d = value switch
            {
                int i => i,
                long l => l,
                uint u => u,
                ulong ul => ul,
                short s => s,
                ushort us => us,
                byte b => b,
                sbyte sb => sb,
                _ => decimal.MinValue,
            };
            return (d != decimal.MinValue || value is int or long or uint or ulong or short or ushort or byte or sbyte) && to switch
            {
                SpecialType.SByte => d is >= sbyte.MinValue and <= sbyte.MaxValue,
                SpecialType.Byte => d is >= byte.MinValue and <= byte.MaxValue,
                SpecialType.Int16 => d is >= short.MinValue and <= short.MaxValue,
                SpecialType.UInt16 => d is >= ushort.MinValue and <= ushort.MaxValue,
                SpecialType.Int32 => d is >= int.MinValue and <= int.MaxValue,
                SpecialType.UInt32 => d is >= uint.MinValue and <= uint.MaxValue,
                SpecialType.Int64 => d is >= long.MinValue and <= long.MaxValue,
                SpecialType.UInt64 => d is >= 0 and <= ulong.MaxValue,
                SpecialType.IntPtr => d is >= int.MinValue and <= int.MaxValue,
                SpecialType.UIntPtr => d is >= 0 and <= uint.MaxValue,
                _ => false,
            };
        }
        catch (OverflowException) { return false; }
    }

    private Conversion ClassifyToUnion(BoundExpression? expr, TypeSymbol source, NamedTypeSymbol union)
    {
        if (source is NamedTypeSymbol { IsUnionType: true } su && !su.Equals(union))
        {
            // union to wider union: every source case must convert to some target case
            bool map = true;
            foreach (TypeSymbol sc in su.UnionCases)
            {
                bool found = false;
                foreach (TypeSymbol tc in union.UnionCases)
                {
                    if (ClassifyImplicit(null, sc, tc).Exists) { found = true; break; }
                }

                if (!found) { map = false; break; }
            }
            if (map)
            {
                return Conversion.Implicit(ConversionKind.UnionWrap, caseIndex: -3); // union-to-union
            }

            return Conversion.None;
        }
        ImmutableArray<TypeSymbol> cases = union.UnionCases;
        // exact match first
        for (int i = 0; i < cases.Length; i++)
        {
            if (cases[i].Equals(source))
            {
                return Conversion.Implicit(ConversionKind.UnionWrap, caseIndex: i);
            }
        }

        int best = -1;
        for (int i = 0; i < cases.Length; i++)
        {
            Conversion c = ClassifyImplicit(expr, source, cases[i]);
            if (!c.Exists)
            {
                continue;
            }

            if (best < 0)
            {
                best = i;
            }
            else if (c.Kind is ConversionKind.ImplicitNumeric && !ClassifyImplicit(expr, source, cases[best]).IsIdentity)
            {
                // prefer the "smaller" numeric target: keep the earlier unless the new one is strictly better
                if (HasImplicitNumeric(cases[i].SpecialType, cases[best].SpecialType))
                {
                    best = i;
                }
            }
        }
        return best >= 0 ? Conversion.Implicit(ConversionKind.UnionWrap, caseIndex: best) : Conversion.None;
    }

    internal Conversion ClassifyExplicit(BoundExpression? expr, TypeSymbol source, TypeSymbol target)
    {
        Conversion imp = ClassifyImplicit(expr, source, target);
        if (imp.Exists)
        {
            return imp;
        }

        if (IsError(source) || IsError(target))
        {
            return Conversion.Identity;
        }

        // nullable
        if (source is NullableTypeSymbol sn)
        {
            if (target is NullableTypeSymbol tn)
            {
                Conversion inner = ClassifyExplicit(null, sn.Underlying, tn.Underlying);
                return inner.Exists
                    ? inner.Kind == ConversionKind.Identity ? Conversion.Identity : Conversion.Explicit(inner.Kind is ConversionKind.ExplicitNumeric or ConversionKind.EnumNumeric ? ConversionKind.LiftedNumeric : ConversionKind.ExplicitReference)
                    : Conversion.None;
            }
            Conversion c = ClassifyImplicit(null, sn.Underlying, target);
            if (c.Exists && c.IsIdentity)
            {
                return Conversion.Explicit(ConversionKind.FromNullable);
            }

            Conversion e = ClassifyExplicit(null, sn.Underlying, target);
            if (e.Exists)
            {
                // unwrap then convert: expressed as FromNullable with a follow-up conversion (handled by CreateConversion)
                return new Conversion(ConversionKind.FromNullable, false, e.Method, e.CaseIndex) { };
            }

            return Conversion.None;
        }
        if (target is NullableTypeSymbol tnn)
        {
            Conversion e = ClassifyExplicit(expr, source, tnn.Underlying);
            return e.Exists ? e.WithNullableWrap() : Conversion.None;
        }

        SpecialType s = source.SpecialType;
        SpecialType t = target.SpecialType;
        if (IsNumericType(source) && IsNumericType(target))
        {
            return Conversion.Explicit(ConversionKind.ExplicitNumeric);
        }

        if (source.TypeKind == TypeKind.Enum && (IsNumericType(target) || target.TypeKind == TypeKind.Enum))
        {
            return Conversion.Explicit(ConversionKind.EnumNumeric);
        }

        if (target.TypeKind == TypeKind.Enum && IsNumericType(source))
        {
            return Conversion.Explicit(ConversionKind.EnumNumeric);
        }

        _ = s; _ = t;

        // unions: extract a case
        if (source is NamedTypeSymbol { IsUnionType: true } su)
        {
            ImmutableArray<TypeSymbol> cases = su.UnionCases;
            for (int i = 0; i < cases.Length; i++)
            {
                if (cases[i].Equals(target))
                {
                    return Conversion.Explicit(ConversionKind.UnionExtract, caseIndex: i);
                }
            }

            for (int i = 0; i < cases.Length; i++)
            {
                if (ImplicitReferenceConversionExists(target, cases[i]) || ImplicitReferenceConversionExists(cases[i], target))
                {
                    return Conversion.Explicit(ConversionKind.UnionExtract, caseIndex: i);
                }
            }
        }

        // reference downcasts
        if (ExplicitReferenceConversionExists(source, target))
        {
            return Conversion.Explicit(ConversionKind.ExplicitReference);
        }
        // unboxing
        if (target.IsValueType && !source.IsValueType)
        {
            if (source.SpecialType is SpecialType.Object or SpecialType.ValueType or SpecialType.Enum)
            {
                return Conversion.Explicit(ConversionKind.Unboxing);
            }

            if (source is NamedTypeSymbol { TypeKind: TypeKind.Interface } si && ImplementsInterface(target, si))
            {
                return Conversion.Explicit(ConversionKind.Unboxing);
            }
        }
        if (target is TypeParameterSymbol && (source.SpecialType == SpecialType.Object || source.IsReferenceType || source is TypeParameterSymbol))
        {
            return Conversion.Explicit(ConversionKind.Unboxing);
        }

        if (source is TypeParameterSymbol && target.IsReferenceType)
        {
            return Conversion.Explicit(ConversionKind.ExplicitReference);
        }

        Conversion ud = FindUserDefinedConversion(expr, source, target, isExplicit: true);
        return ud.Exists ? ud : Conversion.None;
    }

    private bool ExplicitReferenceConversionExists(TypeSymbol source, TypeSymbol target)
    {
        if (!(source.IsReferenceType || source is TypeParameterSymbol) || !target.IsReferenceType)
        {
            return false;
        }

        if (ImplicitReferenceConversionExists(target, source))
        {
            return true; // downcast
        }

        if (source is ArrayTypeSymbol sa && target is ArrayTypeSymbol ta)
        {
            return sa.Rank == ta.Rank && ExplicitReferenceConversionExists(sa.ElementType, ta.ElementType) && sa.ElementType.IsReferenceType && ta.ElementType.IsReferenceType;
        }
        // class to interface (non-sealed) / interface to class
        return (target is NamedTypeSymbol { TypeKind: TypeKind.Interface } && source is NamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: false })
            || (source is NamedTypeSymbol { TypeKind: TypeKind.Interface } && target is NamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: false })
            || (source is NamedTypeSymbol { TypeKind: TypeKind.Interface } && target is NamedTypeSymbol { TypeKind: TypeKind.Interface })
            || (source.TypeKind == TypeKind.Interface && target.TypeKind == TypeKind.Class && ImplementsInterface(target, (NamedTypeSymbol)source));
    }

    private int _userDefinedDepth;

    private Conversion FindUserDefinedConversion(BoundExpression? expr, TypeSymbol source, TypeSymbol target, bool isExplicit)
    {
        _userDefinedDepth++;
        try { return FindUserDefinedConversionCore(expr, source, target, isExplicit); }
        finally { _userDefinedDepth--; }
    }

    private Conversion FindUserDefinedConversionCore(BoundExpression? expr, TypeSymbol source, TypeSymbol target, bool isExplicit)
    {
        TypeSymbol src = source.StripNullable();
        TypeSymbol tgt = target.StripNullable();
        List<MethodSymbol> candidates = [];
        void Collect(TypeSymbol t)
        {
            if (t is not NamedTypeSymbol nt)
            {
                return;
            }

            foreach (Symbol m in nt.GetMembers())
            {
                if (m is MethodSymbol { MethodKind: MethodKind.Conversion, IsStatic: true } ms && ms.Parameters.Length == 1
                    && (ms.Name == "op_Implicit" || (isExplicit && ms.Name == "op_Explicit")))
                {
                    candidates.Add(ms);
                }
            }

            if (nt.BaseType is { } b && b.SpecialType != SpecialType.Object)
            {
                Collect(b);
            }
        }
        Collect(src);
        if (!ReferenceEquals(src, tgt) && !src.Equals(tgt))
        {
            Collect(tgt);
        }

        MethodSymbol? best = null;
        int bestScore = int.MaxValue;
        foreach (MethodSymbol m in candidates)
        {
            TypeSymbol p = m.Parameters[0].Type;
            TypeSymbol r = m.ReturnType;
            Conversion inC = ClassifyImplicit(expr, src, p, allowUserDefined: false);
            if (!inC.Exists)
            {
                continue;
            }

            Conversion outC = ClassifyImplicit(null, r, tgt, allowUserDefined: false);
            if (!outC.Exists)
            {
                continue;
            }

            int score = (inC.IsIdentity ? 0 : 1) + (outC.IsIdentity ? 0 : 1);
            if (score < bestScore) { best = m; bestScore = score; }
        }
        return best == null ? Conversion.None : new Conversion(ConversionKind.UserDefined, best.Name == "op_Implicit", best);
    }

    // ------------------------------------------------------------------ creating bound conversions

    /// <summary>Implicitly converts <paramref name="expr"/> to <paramref name="target"/>, reporting an error if impossible.</summary>
    internal BoundExpression ConvertImplicit(BoundExpression expr, TypeSymbol target, SyntaxNode? syntax, bool report = true)
    {
        if (expr is BoundUnbound u)
        {
            BoundExpression bound = u.BindToType(target);
            return bound is BoundUnbound ? Bad(syntax) : ConvertImplicit(bound, target, syntax, report);
        }
        if (expr is BoundMethodGroup mg)
        {
            return ConvertMethodGroup(mg, target, syntax, report);
        }

        if (expr.Type == null)
        {
            if (report)
            {
                ReportNotAValue(expr, syntax);
            }

            return Bad(syntax, expr);
        }
        if (IsError(expr.Type) || IsError(target))
        {
            return expr;
        }

        TypeSymbol source = expr.Type;
        Conversion conv = ClassifyImplicit(expr, source, target);
        if (!conv.Exists)
        {
            if (report)
            {
                ReportNotAssignable(expr, source, target, syntax);
            }

            return new BoundConversion(expr, ConversionKind.Identity, target) { Syntax = syntax ?? expr.Syntax };
        }
        return CreateConversion(expr, conv, target, syntax);
    }

    internal BoundExpression ConvertExplicit(BoundExpression expr, TypeSymbol target, SyntaxNode? syntax, bool report = true)
    {
        if (expr is BoundUnbound or BoundMethodGroup)
        {
            return ConvertImplicit(expr, target, syntax, report);
        }

        if (expr.Type == null) { if (report) { ReportNotAValue(expr, syntax); } return Bad(syntax, expr); }
        if (IsError(expr.Type) || IsError(target))
        {
            return expr;
        }

        Conversion conv = ClassifyExplicit(expr, expr.Type, target);
        if (!conv.Exists)
        {
            if (report)
            {
                Report(Descriptors.CannotConvert, syntax, expr.Type.ToDisplayString(), target.ToDisplayString());
            }

            return new BoundConversion(expr, ConversionKind.Identity, target) { Syntax = syntax ?? expr.Syntax };
        }
        return CreateConversion(expr, conv, target, syntax);
    }

    internal BoundExpression CreateConversion(BoundExpression expr, Conversion conv, TypeSymbol target, SyntaxNode? syntax)
    {
        syntax ??= expr.Syntax;
        if (conv.IsIdentity)
        {
            return expr;
        }

        TypeSymbol innerTarget = conv.WrapNullable && target is NullableTypeSymbol nt ? nt.Underlying : target;

        BoundExpression result;
        if (conv.Constant.HasValue && conv.Kind is ConversionKind.ImplicitNumeric or ConversionKind.EnumNumeric)
        {
            result = new BoundLiteral(conv.Constant.Value, innerTarget) { Syntax = syntax };
        }
        else if (conv.Kind == ConversionKind.FromNullable && conv.Method != null)
        {
            // explicit nullable unwrap followed by a user-defined / further conversion
            BoundConversion unwrapped = new(expr, ConversionKind.FromNullable, expr.Type!.StripNullable()) { Syntax = syntax };
            result = new BoundConversion(unwrapped, ConversionKind.UserDefined, innerTarget, conv.Method) { Syntax = syntax };
        }
        else if (conv.Kind == ConversionKind.FromNullable && !innerTarget.Equals(expr.Type!.StripNullable()) && expr.Type is NullableTypeSymbol sn)
        {
            BoundConversion unwrapped = new(expr, ConversionKind.FromNullable, sn.Underlying) { Syntax = syntax };
            Conversion second = ClassifyExplicit(null, sn.Underlying, innerTarget);
            result = second.IsIdentity ? unwrapped : CreateConversion(unwrapped, second, innerTarget, syntax);
        }
        else if (conv.Kind == ConversionKind.TypeOnly && conv.CaseIndex == -2)
        {
            result = ConvertTuple(expr, innerTarget, syntax);
        }
        else
        {
            ConstantValue? folded = TryFoldConversion(expr, conv, innerTarget);
            result = new BoundConversion(expr, conv.Kind, innerTarget, conv.Method) { Syntax = syntax, CaseIndex = conv.CaseIndex, ConstantFolded = folded };
        }
        if (conv.WrapNullable)
        {
            result = new BoundConversion(result, ConversionKind.ToNullable, target) { Syntax = syntax };
        }

        return result;
    }

    private BoundExpression ConvertTuple(BoundExpression expr, TypeSymbol target, SyntaxNode? syntax)
    {
        NamedTypeSymbol ts = (NamedTypeSymbol)target;
        NamedTypeSymbol ss = (NamedTypeSymbol)expr.Type!;
        if (expr is BoundObjectCreation { Constructor: not null } oc && oc.Initializers.IsDefaultOrEmpty && oc.Arguments.Length == ts.TypeArguments.Length)
        {
            ImmutableArray<BoundExpression>.Builder args = ImmutableArray.CreateBuilder<BoundExpression>();
            for (int i = 0; i < oc.Arguments.Length; i++)
            {
                args.Add(ConvertImplicit(oc.Arguments[i], ts.TypeArguments[i], syntax));
            }

            MethodSymbol? ctor = ts.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == args.Count);
            return new BoundObjectCreation(ctor, target, args.ToImmutable(), []) { Syntax = syntax };
        }
        // general case: temp + element-wise conversion
        LocalSymbol tmp = new("<tuple>", ss, false, ContainingSymbol) { IsSynthesized = true };
        ImmutableArray<BoundExpression>.Builder elems = ImmutableArray.CreateBuilder<BoundExpression>();
        for (int i = 0; i < ss.TypeArguments.Length; i++)
        {
            FieldSymbol field = ss.GetMembers("Item" + (i + 1)).OfType<FieldSymbol>().First();
            BoundFieldAccess get = new(new BoundLocal(tmp), field) { Syntax = syntax };
            elems.Add(ConvertImplicit(get, ts.TypeArguments[i], syntax));
        }
        MethodSymbol? ctor2 = ts.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == elems.Count);
        BoundObjectCreation create = new(ctor2, target, elems.ToImmutable(), []) { Syntax = syntax };
        return new BoundSequence([tmp], [new BoundLocalDeclaration(tmp, expr)], create) { Syntax = syntax };
    }

    private static ConstantValue? TryFoldConversion(BoundExpression expr, Conversion conv, TypeSymbol target)
    {
        ConstantValue? cv = expr.ConstantValue;
        if (!cv.HasValue || cv.Value.Value == null)
        {
            return null;
        }

        if (conv.Kind is ConversionKind.ImplicitNumeric or ConversionKind.ExplicitNumeric && target.SpecialType != SpecialType.None)
        {
            object? f = ConstantFolding.ConvertNumeric(cv.Value.Value, target.SpecialType, conv.Kind == ConversionKind.ExplicitNumeric);
            if (f != null)
            {
                return new ConstantValue(f);
            }
        }
        return conv.Kind is ConversionKind.ImplicitReference or ConversionKind.Boxing && target.SpecialType == SpecialType.Object && false
            ? null
            : null;
    }

    private void ReportNotAValue(BoundExpression expr, SyntaxNode? syntax)
    {
        switch (expr)
        {
            case BoundTypeExpression te: Report(Descriptors.TypeUsedAsValue, syntax, te.TypeSym.ToDisplayString()); break;
            case BoundNamespaceExpression ns: Report(Descriptors.NamespaceUsedAsValue, syntax, ns.Namespace.ToDisplayString()); break;
            case BoundModuleExpression: Report(Descriptors.NamespaceUsedAsValue, syntax, "module"); break;
            default: Report(Descriptors.ExpressionNotAValue, syntax); break;
        }
    }

    private void ReportNotAssignable(BoundExpression expr, TypeSymbol source, TypeSymbol target, SyntaxNode? syntax)
    {
        if (source.IsNullType)
        {
            Report(Descriptors.NullNotAssignable, syntax, target.ToDisplayString());
            return;
        }
        if (source is NullableTypeSymbol sn && ClassifyImplicit(null, sn.Underlying, target).Exists)
        {
            Report(Descriptors.PossiblyNullAssign, syntax, source.ToDisplayString(), target.ToDisplayString());
            return;
        }
        if (expr.ConstantValue is { Value: string s } && target is SourceLiteralEnumSymbol or NullableTypeSymbol { Underlying: SourceLiteralEnumSymbol })
        {
            Report(Descriptors.NotAssignable, syntax, "\"" + s + "\"", target.ToDisplayString());
            return;
        }
        Conversion explicitC = ClassifyExplicit(expr, source, target);
        if (explicitC.Exists)
        {
            Report(Descriptors.NotAssignableExplicitExists, syntax, source.ToDisplayString(), target.ToDisplayString(), target.ToDisplayString());
        }
        else
        {
            Report(Descriptors.NotAssignable, syntax, source.ToDisplayString(), target.ToDisplayString());
        }
    }

    /// <summary>Converts to boolean for conditions.</summary>
    internal BoundExpression ConvertToBool(BoundExpression e, SyntaxNode syntax)
    {
        return e.Type != null && e.Type.SpecialType == SpecialType.Boolean ? e : ConvertImplicit(e, BoolType, syntax);
    }
}
