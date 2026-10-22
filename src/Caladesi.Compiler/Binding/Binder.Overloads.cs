using System.Collections.Immutable;
using System.Text;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

internal sealed class ResolvedCall
{
    public required MethodSymbol Method { get; init; }
    public required ImmutableArray<BoundExpression> Arguments { get; init; }
    public bool Expanded { get; init; }
    /// <summary>Locals standing for omitted trailing <c>out</c> parameters; the call returns them in a tuple.</summary>
    public ImmutableArray<LocalSymbol> OutLocals { get; init; } = [];
    /// <summary>The two applicable candidates when none was better than all the others; the first was picked.</summary>
    public (MethodSymbol First, MethodSymbol Second)? Ambiguity { get; init; }
}

public sealed partial class Binder
{
    private sealed class Candidate
    {
        public required MethodSymbol Method { get; init; }
        public required ImmutableArray<ParameterSymbol> Parameters { get; init; }
        public bool Expanded { get; init; }
        public int DefaultsUsed { get; init; }
        public int OmittedOuts { get; init; }
        public MethodSymbol Original { get; init; } = null!;
    }

    private static ImmutableArray<ParameterSymbol> EffectiveParameters(MethodSymbol m, bool isIndexer, int indexerCount)
    {
        ImmutableArray<ParameterSymbol> ps = m.Parameters;
        return isIndexer && ps.Length > indexerCount && m.MethodKind == MethodKind.PropertySet
            ? [.. ps.Take(indexerCount)]
            : ps;
    }

    /// <summary>Resolves a call to one of <paramref name="methods"/>. Returns null (after reporting) if none applies.</summary>
    internal ResolvedCall? ResolveOverloads(IEnumerable<MethodSymbol> methods, ImmutableArray<BoundExpression> args, ImmutableArray<TypeSymbol> typeArgs,
        SyntaxNode at, string name, bool report, bool isIndexer = false, int indexerParamCount = 0, BoundExpression? extensionReceiver = null,
        TypeSymbol? expectedType = null)
    {
        List<MethodSymbol> all = [.. methods.Distinct()];
        List<Candidate> applicable = [];
        ImmutableArray<BoundExpression> fullArgs = extensionReceiver != null ? args.Insert(0, extensionReceiver) : args;
        foreach (MethodSymbol? m in all)
        {
            if (m.IsExtensionMethod != (extensionReceiver != null) && extensionReceiver != null)
            {
                continue;
            }

            Candidate? cand = Evaluate(m, fullArgs, typeArgs, isIndexer, indexerParamCount, expectedType);
            if (cand != null)
            {
                applicable.Add(cand);
            }
        }

        if (applicable.Count == 0)
        {
            if (report)
            {
                ReportNoApplicable(all, fullArgs, typeArgs, at, name, isIndexer, indexerParamCount, extensionReceiver != null);
            }

            return null;
        }

        Candidate best = applicable[0];
        (MethodSymbol, MethodSymbol)? ambiguity = null;
        if (applicable.Count > 1)
        {
            List<Candidate> winners = [];
            foreach (Candidate c in applicable)
            {
                bool isBest = true;
                foreach (Candidate o in applicable)
                {
                    if (ReferenceEquals(c, o))
                    {
                        continue;
                    }

                    if (!IsBetter(c, o, fullArgs)) { isBest = false; break; }
                }
                if (isBest)
                {
                    winners.Add(c);
                }
            }
            if (winners.Count == 1)
            {
                best = winners[0];
            }
            else
            {
                MethodSymbol a = applicable[0].Method;
                MethodSymbol b = applicable[1].Method;
                if (report)
                {
                    Report(Descriptors.AmbiguousCall, at, name, Signature(a), Signature(b));
                }

                ambiguity = (a, b);
                best = winners.Count > 0 ? winners[0] : applicable[0];
            }
        }

        (ImmutableArray<BoundExpression> Arguments, ImmutableArray<LocalSymbol> OutLocals) = BuildArguments(best, fullArgs, at, report);
        return new ResolvedCall { Method = best.Method, Arguments = Arguments, Expanded = best.Expanded, OutLocals = OutLocals, Ambiguity = ambiguity };
    }

    private static string Signature(MethodSymbol m)
    {
        StringBuilder sb = new();
        _ = sb.Append(m.ContainingType?.Name).Append('.').Append(m.MethodKind == MethodKind.Constructor ? m.ContainingType?.Name : m.Name).Append('(');
        for (int i = 0; i < m.Parameters.Length; i++)
        {
            if (i > 0)
            {
                _ = sb.Append(", ");
            }

            _ = sb.Append(m.Parameters[i].Type.ToDisplayString());
        }
        return sb.Append(')').ToString();
    }

    // ------------------------------------------------------------------ evaluation

    private Candidate? Evaluate(MethodSymbol m, ImmutableArray<BoundExpression> args, ImmutableArray<TypeSymbol> typeArgs, bool isIndexer, int indexerCount, TypeSymbol? expected)
    {
        MethodSymbol original = m;
        if (!IsAccessible(m))
        {
            return null;
        }

        if (m.IsGenericMethod)
        {
            if (!typeArgs.IsDefaultOrEmpty)
            {
                if (typeArgs.Length != m.Arity)
                {
                    return null;
                }

                m = m.Construct(typeArgs);
                if (!SatisfiesConstraints(m))
                {
                    return null;
                }
            }
            else
            {
                ImmutableArray<TypeSymbol> inferred = InferTypeArguments(m, args, isIndexer, indexerCount, expected);
                if (inferred.IsDefault)
                {
                    return null;
                }

                m = m.Construct(inferred);
                if (!SatisfiesConstraints(m))
                {
                    return null;
                }
            }
        }
        else if (!typeArgs.IsDefaultOrEmpty)
        {
            return null;
        }

        ImmutableArray<ParameterSymbol> ps = EffectiveParameters(m, isIndexer, indexerCount);
        // normal form
        if (TryApplicable(ps, args, expanded: false, out int defaults))
        {
            return new Candidate { Method = m, Parameters = ps, Expanded = false, DefaultsUsed = defaults, Original = original };
        }

        if (ps.Length > 0 && ps[^1].IsParams && ps[^1].Type is ArrayTypeSymbol && TryApplicable(ps, args, expanded: true, out defaults))
        {
            return new Candidate { Method = m, Parameters = ps, Expanded = true, DefaultsUsed = defaults, Original = original };
        }
        // trailing `out` parameters may be omitted; the call then returns them (after the return value) in a tuple
        int outs = 0;
        while (outs < ps.Length && ps[ps.Length - 1 - outs].RefKind == RefKind.Out)
        {
            outs++;
        }

        if (outs > 0 && !isIndexer)
        {
            ImmutableArray<ParameterSymbol> visible = [.. ps.Take(ps.Length - outs)];
            if (TryApplicable(visible, args, expanded: false, out defaults))
            {
                return new Candidate { Method = m, Parameters = ps, Expanded = false, DefaultsUsed = defaults + outs, OmittedOuts = outs, Original = original };
            }
        }
        return null;
    }

    private bool TryApplicable(ImmutableArray<ParameterSymbol> ps, ImmutableArray<BoundExpression> args, bool expanded, out int defaultsUsed)
    {
        defaultsUsed = 0;
        int n = ps.Length;
        if (!expanded)
        {
            if (args.Length > n)
            {
                return false;
            }

            for (int i = 0; i < n; i++)
            {
                if (i < args.Length)
                {
                    if (!ArgumentConvertible(args[i], ps[i].Type, ps[i].RefKind))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!ps[i].IsOptional)
                    {
                        return false;
                    }

                    defaultsUsed++;
                }
            }
            return true;
        }
        for (int i = 0; i < n - 1; i++)
        {
            if (i < args.Length)
            {
                if (!ArgumentConvertible(args[i], ps[i].Type, ps[i].RefKind))
                {
                    return false;
                }
            }
            else if (ps[i].IsOptional)
            {
                defaultsUsed++;
            }
            else
            {
                return false;
            }
        }
        TypeSymbol elemType = ((ArrayTypeSymbol)ps[^1].Type).ElementType;
        for (int i = n - 1; i < args.Length; i++)
        {
            if (!ArgumentConvertible(args[i], elemType, RefKind.None))
            {
                return false;
            }
        }

        return true;
    }

    internal bool ArgumentConvertible(BoundExpression arg, TypeSymbol paramType, RefKind refKind)
    {
        if (paramType.IsErrorType)
        {
            return true;
        }

        if (arg is BoundUnbound u)
        {
            if (u.IsLambda)
            {
                return LambdaConvertible(u.Lambda!, paramType);
            }

            if (u.Type != null && ClassifyImplicit(null, u.Type, paramType).Exists)
            {
                return true;
            }

            BoundExpression b = Speculatively(() => u.BindToType(paramType));
            return b is not BoundUnbound && !b.HasErrors;
        }
        return arg is BoundMethodGroup mg
            ? MethodGroupConvertible(mg, paramType)
            : arg.Type != null && (arg.HasErrors || (refKind != RefKind.None ? arg.Type.Equals(paramType) : ClassifyImplicit(arg, arg.Type, paramType).Exists));
    }

    private readonly Dictionary<(LambdaInfo, string), bool> _lambdaConvertibleCache = [];

    internal bool LambdaConvertible(LambdaInfo info, TypeSymbol target)
    {
        NamedTypeSymbol? d = target.StripNullable() as NamedTypeSymbol;
        if (d is not { TypeKind: TypeKind.Delegate })
        {
            return false;
        }

        MethodSymbol? invoke = d.DelegateInvokeMethod;
        if (invoke == null || invoke.Parameters.Length != info.ParameterCount)
        {
            return false;
        }

        (LambdaInfo info, string) key = (info, d.ToDisplayString());
        if (_lambdaConvertibleCache.TryGetValue(key, out bool cached))
        {
            return cached;
        }

        ImmutableArray<TypeSymbol> paramTypes = [.. invoke.Parameters.Select(p => p.Type)];
        for (int i = 0; i < paramTypes.Length; i++)
        {
            TypeSymbol? ex = info.ExplicitParameterTypes[i];
            if (ex != null && !ex.Equals(paramTypes[i]) && !paramTypes[i].IsErrorType) { _lambdaConvertibleCache[key] = false; return false; }
        }
        bool ok = Speculatively(() => info.Bind(paramTypes, invoke.ReturnType, true)) is { } bl && !bl.HasErrors;
        _lambdaConvertibleCache[key] = ok;
        return ok;
    }

    internal bool MethodGroupConvertible(BoundMethodGroup group, TypeSymbol target)
    {
        NamedTypeSymbol? d = target.StripNullable() as NamedTypeSymbol;
        return d is { TypeKind: TypeKind.Delegate } && ResolveMethodGroupForDelegate(group, d) != null;
    }

    internal MethodSymbol? ResolveMethodGroupForDelegate(BoundMethodGroup group, NamedTypeSymbol delegateType)
    {
        MethodSymbol? invoke = delegateType.DelegateInvokeMethod;
        if (invoke == null)
        {
            return null;
        }

        ImmutableArray<BoundExpression> args = [.. invoke.Parameters.Select(p => (BoundExpression)new BoundDefaultValue(p.Type))];
        List<MethodSymbol> candidates = [.. group.Candidates];
        ResolvedCall? res = null;
        if (candidates.Count > 0)
        {
            res = Speculatively(() => ResolveOverloads(candidates, args, group.TypeArguments, group.Syntax!, group.Name, report: false));
        }

        if (res == null && group.Receiver is { Type: not null } recv && group.Receiver is not BoundTypeExpression)
        {
            List<MethodSymbol> ext = LookupExtensionMethods(group.Name);
            res = Speculatively(() => ResolveOverloads(ext, args, group.TypeArguments, group.Syntax!, group.Name, false, extensionReceiver: recv));
        }
        if (res == null)
        {
            return null;
        }
        // return type must be convertible
        TypeSymbol rt = res.Method.ReturnType;
        return !invoke.ReturnType.IsVoid && !rt.IsVoid && !ClassifyImplicit(null, rt, invoke.ReturnType).Exists
            ? null
            : !invoke.ReturnType.IsVoid && rt.IsVoid ? null : res.Method;
    }

    private bool SatisfiesConstraints(MethodSymbol constructed)
    {
        ImmutableArray<TypeParameterSymbol> tps = constructed.ConstructedFrom.TypeParameters;
        ImmutableArray<TypeSymbol> args = constructed.TypeArguments;
        if (tps.Length != args.Length)
        {
            return true;
        }

        TypeMap map = new(tps, args);
        for (int i = 0; i < tps.Length; i++)
        {
            if (!SatisfiesConstraint(tps[i], args[i], map))
            {
                return false;
            }
        }

        return true;
    }

    internal bool SatisfiesConstraint(TypeParameterSymbol tp, TypeSymbol arg, TypeMap map)
    {
        if (arg.IsErrorType)
        {
            return true;
        }

        if (tp.HasValueTypeConstraint && !(arg.IsValueType || arg is TypeParameterSymbol { HasValueTypeConstraint: true }))
        {
            return false;
        }

        if (tp.HasValueTypeConstraint && arg is NullableTypeSymbol)
        {
            return false;
        }

        if (tp.HasReferenceTypeConstraint && !(arg.IsReferenceType || arg is TypeParameterSymbol { HasReferenceTypeConstraint: true }))
        {
            return false;
        }

        if (tp.HasConstructorConstraint && !tp.HasValueTypeConstraint)
        {
            if (arg is NamedTypeSymbol nt)
            {
                if (nt.IsAbstract && nt.TypeKind == TypeKind.Class)
                {
                    return false;
                }

                if (nt.TypeKind == TypeKind.Class && !nt.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                {
                    return false;
                }
            }
            else if (arg is TypeParameterSymbol atp && !atp.HasConstructorConstraint)
            {
                return false;
            }
        }
        foreach (TypeSymbol c in tp.ConstraintTypes)
        {
            TypeSymbol sc = map.Substitute(c);
            if (!ClassifyImplicit(null, arg.StripNullable(), sc).Exists && arg is not TypeParameterSymbol)
            {
                return false;
            }

            if (arg is TypeParameterSymbol atp2 && !ClassifyImplicit(null, atp2, sc).Exists)
            {
                return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ better function member

    private bool IsBetter(Candidate c1, Candidate c2, ImmutableArray<BoundExpression> args)
    {
        bool atLeastOneBetter = false;
        int count = args.Length;
        for (int i = 0; i < count; i++)
        {
            TypeSymbol? t1 = ParamTypeAt(c1, i);
            TypeSymbol? t2 = ParamTypeAt(c2, i);
            if (t1 == null || t2 == null)
            {
                continue;
            }

            int r = CompareConversions(args[i], t1, t2);
            if (r < 0)
            {
                return false;
            }

            if (r > 0)
            {
                atLeastOneBetter = true;
            }
        }
        if (atLeastOneBetter)
        {
            return true;
        }

        // tie-breakers
        bool g1 = c1.Original.IsGenericMethod;
        bool g2 = c2.Original.IsGenericMethod;
        if (!g1 && g2)
        {
            return true;
        }

        if (g1 && !g2)
        {
            return false;
        }

        if (!c1.Expanded && c2.Expanded)
        {
            return true;
        }

        if (c1.Expanded && !c2.Expanded)
        {
            return false;
        }

        if (c1.OmittedOuts < c2.OmittedOuts)
        {
            return true;
        }

        if (c1.OmittedOuts > c2.OmittedOuts)
        {
            return false;
        }

        if (c1.DefaultsUsed < c2.DefaultsUsed)
        {
            return true;
        }

        if (c1.DefaultsUsed > c2.DefaultsUsed)
        {
            return false;
        }
        // more specific parameter types (compare original declarations)
        if (c1.Original.Parameters.Length == c2.Original.Parameters.Length)
        {
            bool allAtLeast = true;
            bool anyBetter = false;
            for (int i = 0; i < c1.Original.Parameters.Length; i++)
            {
                TypeSymbol p1 = c1.Original.Parameters[i].Type;
                TypeSymbol p2 = c2.Original.Parameters[i].Type;
                if (p1.Equals(p2))
                {
                    continue;
                }

                bool a = ClassifyImplicit(null, p1, p2).Exists;
                bool b = ClassifyImplicit(null, p2, p1).Exists;
                if (a && !b)
                {
                    anyBetter = true;
                }
                else if (!a)
                {
                    allAtLeast = false;
                }
            }
            if (allAtLeast && anyBetter)
            {
                return true;
            }

            if (allAtLeast && !anyBetter && !c2.Method.Equals(c1.Method))
            {
                return !AnyStrictlyMoreSpecific(c2, c1);
            }
        }
        return false;
    }

    private bool AnyStrictlyMoreSpecific(Candidate a, Candidate b)
    {
        for (int i = 0; i < a.Original.Parameters.Length && i < b.Original.Parameters.Length; i++)
        {
            TypeSymbol p1 = a.Original.Parameters[i].Type;
            TypeSymbol p2 = b.Original.Parameters[i].Type;
            if (!p1.Equals(p2) && ClassifyImplicit(null, p1, p2).Exists && !ClassifyImplicit(null, p2, p1).Exists)
            {
                return true;
            }
        }
        return false;
    }

    private static TypeSymbol? ParamTypeAt(Candidate c, int i)
    {
        ImmutableArray<ParameterSymbol> ps = c.Parameters;
        return i < ps.Length - 1 || (!c.Expanded && i < ps.Length)
            ? ps[i].Type
            : c.Expanded && ps.Length > 0 ? ((ArrayTypeSymbol)ps[^1].Type).ElementType : null;
    }

    /// <summary>Strips the nullable wrapper from reference types so nullability does not affect conversion ranking.</summary>
    private static TypeSymbol StripReferenceNullability(TypeSymbol t)
    {
        return t is NullableTypeSymbol { Underlying: { } u } && !u.IsValueType && u is not TypeParameterSymbol ? u : t;
    }

    /// <summary>Positive if converting to <paramref name="t1"/> is better, negative if <paramref name="t2"/> is better, 0 if equal.</summary>
    private int CompareConversions(BoundExpression arg, TypeSymbol t1, TypeSymbol t2)
    {
        t1 = StripReferenceNullability(t1);
        t2 = StripReferenceNullability(t2);
        if (t1.Equals(t2))
        {
            return 0;
        }

        if (arg is BoundUnbound { IsLambda: true } or BoundMethodGroup)
        {
            // prefer delegates whose return type is "more specific" (non-void over void, then by conversion)
            if (t1.StripNullable() is NamedTypeSymbol { TypeKind: TypeKind.Delegate } d1 && t2.StripNullable() is NamedTypeSymbol { TypeKind: TypeKind.Delegate } d2)
            {
                TypeSymbol? r1 = d1.DelegateInvokeMethod?.ReturnType;
                TypeSymbol? r2 = d2.DelegateInvokeMethod?.ReturnType;
                if (r1 != null && r2 != null && !r1.Equals(r2))
                {
                    if (r1.IsVoid)
                    {
                        return -1;
                    }

                    if (r2.IsVoid)
                    {
                        return 1;
                    }

                    bool a = ClassifyImplicit(null, r1, r2).Exists;
                    bool b = ClassifyImplicit(null, r2, r1).Exists;
                    if (a && !b)
                    {
                        return 1;
                    }

                    if (b && !a)
                    {
                        return -1;
                    }
                }
            }
            return 0;
        }
        TypeSymbol? st = arg.Type;
        if (st != null)
        {
            st = StripReferenceNullability(st);
            if (st.Equals(t1))
            {
                return 1;
            }

            if (st.Equals(t2))
            {
                return -1;
            }
        }
        bool c1 = ClassifyImplicit(null, t1, t2).Exists;
        bool c2 = ClassifyImplicit(null, t2, t1).Exists;
        if (c1 && !c2)
        {
            return 1;
        }

        if (c2 && !c1)
        {
            return -1;
        }
        // signed vs unsigned preference
        return t1.SpecialType.IsSignedIntegral() && t2.SpecialType.IsUnsignedIntegral()
            ? 1
            : t2.SpecialType.IsSignedIntegral() && t1.SpecialType.IsUnsignedIntegral() ? -1 : 0;
    }

    // ------------------------------------------------------------------ building arguments

    private (ImmutableArray<BoundExpression> Arguments, ImmutableArray<LocalSymbol> OutLocals) BuildArguments(Candidate c, ImmutableArray<BoundExpression> args, SyntaxNode at, bool report)
    {
        ImmutableArray<ParameterSymbol> ps = c.Parameters;
        ImmutableArray<BoundExpression>.Builder result = ImmutableArray.CreateBuilder<BoundExpression>(ps.Length);
        ImmutableArray<LocalSymbol>.Builder outLocals = ImmutableArray.CreateBuilder<LocalSymbol>();
        for (int i = 0; i < ps.Length; i++)
        {
            ParameterSymbol p = ps[i];
            if (c.OmittedOuts > 0 && i >= ps.Length - c.OmittedOuts)
            {
                LocalSymbol local = new("<out>" + p.Name, p.Type, false, LocalOwner) { IsSynthesized = true };
                outLocals.Add(local);
                result.Add(new BoundAddressOf(new BoundLocal(local)));
                continue;
            }
            bool isParamsExpanded = c.Expanded && i == ps.Length - 1;
            if (isParamsExpanded)
            {
                TypeSymbol elemType = ((ArrayTypeSymbol)p.Type).ElementType;
                ImmutableArray<BoundExpression>.Builder elems = ImmutableArray.CreateBuilder<BoundExpression>();
                for (int j = i; j < args.Length; j++)
                {
                    elems.Add(ConvertArg(args[j], elemType, report));
                }

                result.Add(new BoundArrayCreation((ArrayTypeSymbol)p.Type, new BoundLiteral(new ConstantValue(elems.Count), IntType), elems.ToImmutable()) { Syntax = at });
            }
            else if (i < args.Length)
            {
                result.Add(ConvertArg(args[i], p.Type, report, p.RefKind));
            }
            else if (p.IsParams && p.Type is ArrayTypeSymbol at2)
            {
                result.Add(new BoundArrayCreation(at2, new BoundLiteral(new ConstantValue(0), IntType), []) { Syntax = at });
            }
            else
            {
                result.Add(DefaultArgument(p, at));
            }
        }
        return (result.MoveToImmutable(), outLocals.ToImmutable());
    }

    private BoundExpression ConvertArg(BoundExpression arg, TypeSymbol paramType, bool report, RefKind _1 = RefKind.None)
    {
        if (arg is BoundUnbound u && u.IsLambda)
        {
            TypeSymbol d = paramType.StripNullable();
            BoundExpression bound = u.BindToType(d);
            return ConvertImplicit(bound, paramType, arg.Syntax, report);
        }
        return ConvertImplicit(arg, paramType, arg.Syntax, report);
    }

    internal BoundExpression DefaultArgument(ParameterSymbol p, SyntaxNode at)
    {
        ConstantValue? dv = p.DefaultValue;
        TypeSymbol t = p.Type;
        if (dv is
        { IsNull: true } or null)
        {
            return t is NullableTypeSymbol || !t.IsValueType
                ? new BoundConversion(new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance), ConversionKind.NullLiteral, t) { Syntax = at }
                : new BoundDefaultValue(t) { Syntax = at };
        }
        TypeSymbol inner = t.StripNullable();
        TypeSymbol litType = inner.TypeKind == TypeKind.Enum ? inner : inner.SpecialType != SpecialType.None ? inner : TypeOfConstant(dv.Value.Value);
        BoundExpression lit = new BoundLiteral(dv.Value, litType) { Syntax = at };
        if (t is NullableTypeSymbol)
        {
            lit = new BoundConversion(lit, ConversionKind.ToNullable, t) { Syntax = at };
        }

        return lit;
    }

    private TypeSymbol TypeOfConstant(object? v)
    {
        return v switch
        {
            int => IntType,
            long => Special(SpecialType.Int64),
            double => DoubleType,
            float => Special(SpecialType.Single),
            decimal => Special(SpecialType.Decimal),
            bool => BoolType,
            string => StringType,
            char => Special(SpecialType.Char),
            _ => ObjectType,
        };
    }

    // ------------------------------------------------------------------ errors

    private void ReportNoApplicable(List<MethodSymbol> all, ImmutableArray<BoundExpression> args, ImmutableArray<TypeSymbol> typeArgs, SyntaxNode at, string name,
        bool isIndexer, int indexerCount, bool _1)
    {
        if (all.Any(a => args.Any(x => x.HasErrors)))
        {
            return;
        }

        if (all.Count == 0)
        {
            Report(Descriptors.NotCallable, at);
            return;
        }
        string argDesc = string.Join(", ", args.Select(DescribeArgType));
        if (all.Count == 1)
        {
            MethodSymbol m = all[0];
            ImmutableArray<ParameterSymbol> ps = EffectiveParameters(m, isIndexer, indexerCount);
            if (m.IsGenericMethod && typeArgs.IsDefaultOrEmpty)
            {
                ImmutableArray<TypeSymbol> inferred = InferTypeArguments(m, args, isIndexer, indexerCount, null);
                if (inferred.IsDefault)
                {
                    Report(Descriptors.CannotInferTypeArguments, at, name);
                    return;
                }
            }
            int required = ps.Count(p => !p.IsOptional && !p.IsParams);
            bool hasParams = ps.Length > 0 && ps[^1].IsParams;
            if (args.Length < required || (!hasParams && args.Length > ps.Length))
            {
                string expected = hasParams ? $"at least {required}" : required == ps.Length ? ps.Length.ToString() : $"{required}-{ps.Length}";
                Report(Descriptors.WrongArgumentCount, at, name, expected, args.Length);
                return;
            }
            for (int i = 0; i < args.Length && i < ps.Length; i++)
            {
                TypeSymbol pt = ps[i].Type;
                if (!ArgumentConvertible(args[i], pt, ps[i].RefKind))
                {
                    if (args[i] is BoundUnbound { IsLambda: true })
                    {
                        Report(Descriptors.LambdaNotConvertible, args[i].Syntax ?? at, pt.ToDisplayString());
                        return;
                    }
                    BoundExpression a = args[i];
                    if (a.Type != null)
                    {
                        // reuse the assignability diagnostics for a precise message
                        ReportNotAssignableArg(a, pt, args[i].Syntax ?? at);
                        return;
                    }
                    Report(Descriptors.ArgumentNotAssignable, args[i].Syntax ?? at, DescribeArgType(a), pt.ToDisplayString(), ps[i].Name);
                    return;
                }
            }
            Report(Descriptors.NoMatchingOverload, at, name, argDesc);
            return;
        }
        Report(Descriptors.NoMatchingOverload, at, name, argDesc);
    }

    private void ReportNotAssignableArg(BoundExpression a, TypeSymbol pt, SyntaxNode at)
    {
        TypeSymbol source = a.Type!;
        if (source.IsNullType) { Report(Descriptors.NullNotAssignable, at, pt.ToDisplayString()); return; }
        if (source is NullableTypeSymbol sn && ClassifyImplicit(null, sn.Underlying, pt).Exists)
        {
            Report(Descriptors.PossiblyNullAssign, at, source.ToDisplayString(), pt.ToDisplayString());
            return;
        }
        Report(Descriptors.ArgumentNotAssignable, at, source.ToDisplayString(), pt.ToDisplayString(), "");
    }

    private string DescribeArgType(BoundExpression a)
    {
        return a switch
        {
            BoundUnbound { IsLambda: true } => "lambda",
            BoundUnbound { IsObjectLiteral: true } => "object literal",
            BoundUnbound => "collection",
            BoundMethodGroup g => g.Name,
            _ => a.Type?.ToDisplayString() ?? "?",
        };
    }
}
