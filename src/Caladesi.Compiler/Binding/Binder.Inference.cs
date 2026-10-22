using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    private sealed class InferenceContext
    {
        public required ImmutableArray<TypeParameterSymbol> TypeParameters { get; init; }
        public TypeSymbol?[] Fixed { get; init; } = null!;
        public List<TypeSymbol>[] Lower { get; init; } = null!;
        public List<TypeSymbol>[] Upper { get; init; } = null!;
        public List<TypeSymbol>[] Exact { get; init; } = null!;

        public int IndexOf(TypeSymbol t)
        {
            if (t is not TypeParameterSymbol tp)
            {
                return -1;
            }

            for (int i = 0; i < TypeParameters.Length; i++)
            {
                if (ReferenceEquals(TypeParameters[i], tp))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>Infers the type arguments of a generic method from the arguments (C# two-phase inference).</summary>
    internal ImmutableArray<TypeSymbol> InferTypeArguments(MethodSymbol m, ImmutableArray<BoundExpression> args, bool isIndexer, int indexerCount, TypeSymbol? expected)
    {
        ImmutableArray<TypeParameterSymbol> tps = m.TypeParameters;
        int n = tps.Length;
        InferenceContext ctx = new()
        {
            TypeParameters = tps,
            Fixed = new TypeSymbol?[n],
            Lower = [.. Enumerable.Range(0, n).Select(_ => new List<TypeSymbol>())],
            Upper = [.. Enumerable.Range(0, n).Select(_ => new List<TypeSymbol>())],
            Exact = [.. Enumerable.Range(0, n).Select(_ => new List<TypeSymbol>())],
        };
        ImmutableArray<ParameterSymbol> ps = EffectiveParameters(m, isIndexer, indexerCount);

        TypeSymbol? ParamTypeFor(int i, bool expanded)
        {
            return ps.Length == 0
                ? null
                : i < ps.Length - 1
                ? ps[i].Type
                : i == ps.Length - 1 && !expanded
                ? ps[i].Type
                : ps[^1].IsParams && ps[^1].Type is ArrayTypeSymbol at ? at.ElementType : i < ps.Length ? ps[i].Type : null;
        }

        // decide between normal and expanded form by argument count
        bool expandedForm = ps.Length > 0 && ps[^1].IsParams && !(args.Length == ps.Length && args[^1].Type is ArrayTypeSymbol);

        // phase 1
        for (int i = 0; i < args.Length; i++)
        {
            TypeSymbol? pt = ParamTypeFor(i, expandedForm);
            if (pt == null)
            {
                continue;
            }

            BoundExpression a = args[i];
            if (a is BoundUnbound u)
            {
                if (u.IsLambda && u.Lambda != null)
                {
                    ExplicitLambdaInference(ctx, u.Lambda, pt);
                }
                else if (u.Type != null)
                {
                    LowerBoundInference(ctx, u.Type, pt);
                }
                else if (u.IsCollection && pt is ArrayTypeSymbol cat) { /* handled later by element inference below */ _ = cat; }
                continue;
            }
            if (a is BoundMethodGroup)
            {
                continue;
            }

            if (a.Type == null || a.Type.IsNullType)
            {
                continue;
            }

            LowerBoundInference(ctx, a.Type, pt);
        }

        // collection-expression arguments: infer from element types when the parameter is T[], List<T>, IEnumerable<T> ...
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is BoundUnbound { IsCollection: true, Collection.NaturalElementType: { } elem })
            {
                TypeSymbol? pt = ParamTypeFor(i, expandedForm);
                if (pt != null)
                {
                    InferFromElementType(ctx, elem, pt);
                }
            }
        }

        // phase 2
        bool[] lambdaDone = new bool[args.Length];
        for (int iter = 0; iter < (2 * n) + args.Length + 4; iter++)
        {
            bool progress = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (lambdaDone[i] || args[i] is not BoundUnbound { IsLambda: true, Lambda: { } info })
                {
                    continue;
                }

                TypeSymbol? pt = ParamTypeFor(i, expandedForm);
                if (pt == null)
                {
                    continue;
                }

                NamedTypeSymbol? d = pt.StripNullable() as NamedTypeSymbol;
                MethodSymbol? invoke = d?.DelegateInvokeMethod;
                if (invoke == null || invoke.Parameters.Length != info.ParameterCount) { lambdaDone[i] = true; continue; }
                // input types must be fixed
                ImmutableArray<TypeSymbol> inputs = [.. invoke.Parameters.Select(p => FixedSubstitute(ctx, p.Type))];
                if (inputs.Any(t => MentionsUnfixed(ctx, t)))
                {
                    continue;
                }

                TypeSymbol? rt = info.InferReturnType(inputs);
                lambdaDone[i] = true;
                progress = true;
                if (rt != null && !rt.IsNullType)
                {
                    LowerBoundInference(ctx, rt, invoke.ReturnType);
                }
            }
            if (progress)
            {
                continue;
            }
            // method group arguments whose delegate inputs are fixed
            for (int i = 0; i < args.Length; i++)
            {
                if (lambdaDone[i] || args[i] is not BoundMethodGroup mg)
                {
                    continue;
                }

                TypeSymbol? pt = ParamTypeFor(i, expandedForm);
                NamedTypeSymbol? d = pt?.StripNullable() as NamedTypeSymbol;
                MethodSymbol? invoke = d?.DelegateInvokeMethod;
                if (invoke == null) { lambdaDone[i] = true; continue; }
                ImmutableArray<TypeSymbol> inputs = [.. invoke.Parameters.Select(p => FixedSubstitute(ctx, p.Type))];
                if (inputs.Any(t => MentionsUnfixed(ctx, t)))
                {
                    continue;
                }

                lambdaDone[i] = true;
                progress = true;
                MethodSymbol? chosen = ResolveMethodGroupByTypes(mg, inputs);
                if (chosen != null && !chosen.ReturnType.IsVoid)
                {
                    LowerBoundInference(ctx, chosen.ReturnType, invoke.ReturnType);
                }
            }
            if (progress)
            {
                continue;
            }

            // fix a type parameter: prefer one that appears in some lambda's input types
            int toFix = -1;
            for (int j = 0; j < n && toFix < 0; j++)
            {
                if (ctx.Fixed[j] != null)
                {
                    continue;
                }

                if (!HasBounds(ctx, j))
                {
                    continue;
                }
                // is it needed as input for a pending lambda?
                for (int i = 0; i < args.Length; i++)
                {
                    if (lambdaDone[i] || args[i] is not (BoundUnbound { IsLambda: true } or BoundMethodGroup))
                    {
                        continue;
                    }

                    TypeSymbol? pt = ParamTypeFor(i, expandedForm);
                    MethodSymbol? invoke = (pt?.StripNullable() as NamedTypeSymbol)?.DelegateInvokeMethod;
                    if (invoke != null && invoke.Parameters.Any(p => Mentions(p.Type, tps[j]))) { toFix = j; break; }
                }
            }
            if (toFix < 0)
            {
                for (int j = 0; j < n; j++)
                {
                    if (ctx.Fixed[j] == null && HasBounds(ctx, j)) { toFix = j; break; }
                }
            }

            if (toFix < 0)
            {
                break;
            }

            if (!FixTypeParameter(ctx, toFix))
            {
                return default;
            }
        }

        for (int j = 0; j < n; j++)
        {
            if (ctx.Fixed[j] != null)
            {
                continue;
            }

            if (!HasBounds(ctx, j))
            {
                // use the expected return type as a last resort
                if (expected != null && j >= 0)
                {
                    InferFromExpected(ctx, m.ReturnType, expected);
                    if (HasBounds(ctx, j)) { if (!FixTypeParameter(ctx, j)) { return default; } continue; }
                }
                return default;
            }
            if (!FixTypeParameter(ctx, j))
            {
                return default;
            }
        }
        return [.. ctx.Fixed.Select(t => t!)];
    }

    private static void InferFromExpected(InferenceContext ctx, TypeSymbol returnType, TypeSymbol expected)
    {
        LowerBoundInference(ctx, expected, returnType);
    }

    private MethodSymbol? ResolveMethodGroupByTypes(BoundMethodGroup mg, ImmutableArray<TypeSymbol> inputs)
    {
        ImmutableArray<BoundExpression> args = [.. inputs.Select(t => (BoundExpression)new BoundDefaultValue(t))];
        ResolvedCall? res = Speculatively(() => ResolveOverloads(mg.Candidates, args, mg.TypeArguments, mg.Syntax!, mg.Name, false));
        return res?.Method;
    }

    private static bool HasBounds(InferenceContext c, int j)
    {
        return c.Lower[j].Count + c.Upper[j].Count + c.Exact[j].Count > 0;
    }

    private static bool Mentions(TypeSymbol t, TypeParameterSymbol tp)
    {
        return t switch
        {
            TypeParameterSymbol p => ReferenceEquals(p, tp),
            ArrayTypeSymbol a => Mentions(a.ElementType, tp),
            NullableTypeSymbol n => Mentions(n.Underlying, tp),
            NamedTypeSymbol nt => nt.TypeArguments.Any(x => Mentions(x, tp)),
            _ => false,
        };
    }

    private static bool MentionsUnfixed(InferenceContext c, TypeSymbol t)
    {
        for (int j = 0; j < c.TypeParameters.Length; j++)
        {
            if (c.Fixed[j] == null && Mentions(t, c.TypeParameters[j]))
            {
                return true;
            }
        }

        return false;
    }

    private static TypeSymbol FixedSubstitute(InferenceContext c, TypeSymbol t)
    {
        ImmutableArray<TypeParameterSymbol>.Builder from = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
        ImmutableArray<TypeSymbol>.Builder to = ImmutableArray.CreateBuilder<TypeSymbol>();
        for (int j = 0; j < c.TypeParameters.Length; j++)
        {
            if (c.Fixed[j] != null) { from.Add(c.TypeParameters[j]); to.Add(c.Fixed[j]!); }
        }

        return from.Count == 0 ? t : new TypeMap(from.ToImmutable(), to.ToImmutable()).Substitute(t);
    }

    private static void ExplicitLambdaInference(InferenceContext ctx, LambdaInfo info, TypeSymbol paramType)
    {
        MethodSymbol? invoke = (paramType.StripNullable() as NamedTypeSymbol)?.DelegateInvokeMethod;
        if (invoke == null || invoke.Parameters.Length != info.ParameterCount)
        {
            return;
        }

        for (int i = 0; i < info.ParameterCount; i++)
        {
            if (info.ExplicitParameterTypes[i] is { } et)
            {
                ExactBoundInference(ctx, et, invoke.Parameters[i].Type);
            }
        }

        if (info.ExplicitReturnType is { } rt)
        {
            LowerBoundInference(ctx, rt, invoke.ReturnType);
        }
    }

    private static void InferFromElementType(InferenceContext ctx, TypeSymbol elem, TypeSymbol paramType)
    {
        switch (paramType.StripNullable())
        {
            case ArrayTypeSymbol at:
                LowerBoundInference(ctx, elem, at.ElementType);
                break;
            case NamedTypeSymbol { IsGenericType: true } nt when nt.TypeArguments.Length == 1:
                LowerBoundInference(ctx, elem, nt.TypeArguments[0]);
                break;
        }
    }

    // ------------------------------------------------------------------ bounds

    private static void LowerBoundInference(InferenceContext c, TypeSymbol u, TypeSymbol v)
    {
        int idx = c.IndexOf(v);
        if (idx >= 0)
        {
            if (!u.IsNullType)
            {
                AddBound(c.Lower[idx], u);
            }

            return;
        }
        if (v is NullableTypeSymbol vn)
        {
            LowerBoundInference(c, u is NullableTypeSymbol un ? un.Underlying : u, vn.Underlying);
            return;
        }
        if (u is NullableTypeSymbol u2 && v is not TypeParameterSymbol) { LowerBoundInference(c, u2.Underlying, v); return; }
        switch (v)
        {
            case ArrayTypeSymbol va:
                if (u is ArrayTypeSymbol uarr && uarr.Rank == va.Rank)
                {
                    if (uarr.ElementType.IsReferenceType)
                    {
                        LowerBoundInference(c, uarr.ElementType, va.ElementType);
                    }
                    else
                    {
                        ExactBoundInference(c, uarr.ElementType, va.ElementType);
                    }
                }
                return;
            case NamedTypeSymbol { IsGenericType: true } vn2:
                {
                    NamedTypeSymbol? match = FindConstructedMatch(u, vn2);
                    if (match == null)
                    {
                        return;
                    }

                    NamedTypeSymbol def = vn2.OriginalDefinition;
                    for (int i = 0; i < vn2.TypeArguments.Length; i++)
                    {
                        TypeSymbol ua = match.TypeArguments[i];
                        TypeSymbol va = vn2.TypeArguments[i];
                        switch (def.TypeParameters[i].Variance)
                        {
                            case VarianceKind.Out when ua.IsReferenceType: LowerBoundInference(c, ua, va); break;
                            case VarianceKind.In when ua.IsReferenceType: UpperBoundInference(c, ua, va); break;
                            default: ExactBoundInference(c, ua, va); break;
                        }
                    }
                    return;
                }
        }
    }

    private static void UpperBoundInference(InferenceContext c, TypeSymbol u, TypeSymbol v)
    {
        int idx = c.IndexOf(v);
        if (idx >= 0) { AddBound(c.Upper[idx], u); return; }
        if (v is ArrayTypeSymbol va && u is ArrayTypeSymbol ua) { UpperBoundInference(c, ua.ElementType, va.ElementType); return; }
        if (v is NamedTypeSymbol { IsGenericType: true } vn && u is NamedTypeSymbol { IsGenericType: true } un && ReferenceEquals(vn.OriginalDefinition, un.OriginalDefinition))
        {
            for (int i = 0; i < vn.TypeArguments.Length; i++)
            {
                ExactBoundInference(c, un.TypeArguments[i], vn.TypeArguments[i]);
            }
        }
    }

    private static void ExactBoundInference(InferenceContext c, TypeSymbol u, TypeSymbol v)
    {
        int idx = c.IndexOf(v);
        if (idx >= 0) { AddBound(c.Exact[idx], u); return; }
        if (v is NullableTypeSymbol vn && u is NullableTypeSymbol un) { ExactBoundInference(c, un.Underlying, vn.Underlying); return; }
        if (v is ArrayTypeSymbol va && u is ArrayTypeSymbol ua && ua.Rank == va.Rank) { ExactBoundInference(c, ua.ElementType, va.ElementType); return; }
        if (v is NamedTypeSymbol { IsGenericType: true } vn2 && u is NamedTypeSymbol { IsGenericType: true } un2 && ReferenceEquals(vn2.OriginalDefinition, un2.OriginalDefinition))
        {
            for (int i = 0; i < vn2.TypeArguments.Length; i++)
            {
                ExactBoundInference(c, un2.TypeArguments[i], vn2.TypeArguments[i]);
            }
        }
    }

    private static void AddBound(List<TypeSymbol> list, TypeSymbol t)
    {
        foreach (TypeSymbol e in list)
        {
            if (e.Equals(t))
            {
                return;
            }
        }

        list.Add(t);
    }

    /// <summary>Finds, among <paramref name="u"/>, its base types and its interfaces, the one built from the same generic type as <paramref name="target"/>.</summary>
    private static NamedTypeSymbol? FindConstructedMatch(TypeSymbol u, NamedTypeSymbol target)
    {
        NamedTypeSymbol def = target.OriginalDefinition;
        if (u is NamedTypeSymbol un && ReferenceEquals(un.OriginalDefinition, def))
        {
            return un;
        }

        for (NamedTypeSymbol? b = u.BaseType; b != null; b = b.BaseType)
        {
            if (ReferenceEquals(b.OriginalDefinition, def))
            {
                return b;
            }
        }

        NamedTypeSymbol? found = null;
        foreach (NamedTypeSymbol i in u.AllInterfaces)
        {
            if (!ReferenceEquals(i.OriginalDefinition, def))
            {
                continue;
            }

            if (found == null)
            {
                found = i;
            }
            else if (!found.Equals(i))
            {
                return null; // ambiguous
            }
        }
        return found == null && u is ArrayTypeSymbol { Rank: 1 } arr && def.TypeParameters.Length == 1 &&
            def.ToMetadataString() is "System.Collections.Generic.IEnumerable`1" or "System.Collections.Generic.IList`1" or "System.Collections.Generic.ICollection`1"
                or "System.Collections.Generic.IReadOnlyList`1" or "System.Collections.Generic.IReadOnlyCollection`1"
            ? def.Construct(arr.ElementType)
            : found;
    }

    private bool FixTypeParameter(InferenceContext c, int j)
    {
        List<TypeSymbol> candidates = [];
        foreach (TypeSymbol t in c.Lower[j])
        {
            AddBound(candidates, t);
        }

        foreach (TypeSymbol t in c.Upper[j])
        {
            AddBound(candidates, t);
        }

        foreach (TypeSymbol t in c.Exact[j])
        {
            AddBound(candidates, t);
        }
        // lower-bound candidates may be widened via their nullable-stripped form
        for (int k = candidates.Count - 1; k >= 0; k--)
        {
            TypeSymbol cand = candidates[k];
            bool ok = true;
            foreach (TypeSymbol e in c.Exact[j])
            {
                if (!cand.Equals(e)) { ok = false; break; }
            }

            if (ok)
            {
                foreach (TypeSymbol l in c.Lower[j])
                {
                    if (!ClassifyImplicit(null, l, cand).Exists) { ok = false; break; }
                }
            }

            if (ok)
            {
                foreach (TypeSymbol u in c.Upper[j])
                {
                    if (!ClassifyImplicit(null, cand, u).Exists) { ok = false; break; }
                }
            }

            if (!ok)
            {
                candidates.RemoveAt(k);
            }
        }
        if (candidates.Count == 0)
        {
            // try a common type for lower bounds that differ only in nullability
            return false;
        }
        TypeSymbol best = candidates[0];
        if (candidates.Count > 1)
        {
            TypeSymbol? winner = null;
            foreach (TypeSymbol cand in candidates)
            {
                if (candidates.All(o => ClassifyImplicit(null, o, cand).Exists)) { winner = cand; break; }
            }
            if (winner == null)
            {
                return false;
            }

            best = winner;
        }
        c.Fixed[j] = best;
        return true;
    }
}
