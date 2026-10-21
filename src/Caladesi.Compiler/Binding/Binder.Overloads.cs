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
}

public sealed partial class Binder
{
    private sealed class Candidate
    {
        public required MethodSymbol Method { get; init; }
        public required ImmutableArray<ParameterSymbol> Parameters { get; init; }
        public bool Expanded { get; init; }
        public int DefaultsUsed { get; init; }
        public MethodSymbol Original { get; init; } = null!;
    }

    private static ImmutableArray<ParameterSymbol> EffectiveParameters(MethodSymbol m, bool isIndexer, int indexerCount)
    {
        var ps = m.Parameters;
        if (isIndexer && ps.Length > indexerCount && m.MethodKind == MethodKind.PropertySet)
            return ps.Take(indexerCount).ToImmutableArray();
        return ps;
    }

    /// <summary>Resolves a call to one of <paramref name="methods"/>. Returns null (after reporting) if none applies.</summary>
    internal ResolvedCall? ResolveOverloads(IEnumerable<MethodSymbol> methods, ImmutableArray<BoundExpression> args, ImmutableArray<TypeSymbol> typeArgs,
        SyntaxNode at, string name, bool report, bool isIndexer = false, int indexerParamCount = 0, BoundExpression? extensionReceiver = null,
        TypeSymbol? expectedType = null)
    {
        var all = methods.Distinct().ToList();
        var applicable = new List<Candidate>();
        var fullArgs = extensionReceiver != null ? args.Insert(0, extensionReceiver) : args;
        foreach (var m in all)
        {
            if (m.IsExtensionMethod != (extensionReceiver != null) && extensionReceiver != null) continue;
            var cand = Evaluate(m, fullArgs, typeArgs, isIndexer, indexerParamCount, expectedType);
            if (cand != null) applicable.Add(cand);
        }

        if (applicable.Count == 0)
        {
            if (report) ReportNoApplicable(all, fullArgs, typeArgs, at, name, isIndexer, indexerParamCount, extensionReceiver != null);
            return null;
        }

        Candidate best = applicable[0];
        if (applicable.Count > 1)
        {
            var winners = new List<Candidate>();
            foreach (var c in applicable)
            {
                var isBest = true;
                foreach (var o in applicable)
                {
                    if (ReferenceEquals(c, o)) continue;
                    if (!IsBetter(c, o, fullArgs)) { isBest = false; break; }
                }
                if (isBest) winners.Add(c);
            }
            if (winners.Count == 1) best = winners[0];
            else
            {
                if (report)
                {
                    var a = applicable[0].Method;
                    var b = applicable[1].Method;
                    Report(Descriptors.AmbiguousCall, at, name, Signature(a), Signature(b));
                }
                best = winners.Count > 0 ? winners[0] : applicable[0];
            }
        }

        var result = BuildArguments(best, fullArgs, at, report);
        if (extensionReceiver != null) { }
        return new ResolvedCall { Method = best.Method, Arguments = result, Expanded = best.Expanded };
    }

    private static string Signature(MethodSymbol m)
    {
        var sb = new StringBuilder();
        sb.Append(m.ContainingType?.Name).Append('.').Append(m.MethodKind == MethodKind.Constructor ? m.ContainingType?.Name : m.Name).Append('(');
        for (var i = 0; i < m.Parameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(m.Parameters[i].Type.ToDisplayString());
        }
        return sb.Append(')').ToString();
    }

    // ------------------------------------------------------------------ evaluation

    private Candidate? Evaluate(MethodSymbol m, ImmutableArray<BoundExpression> args, ImmutableArray<TypeSymbol> typeArgs, bool isIndexer, int indexerCount, TypeSymbol? expected)
    {
        var original = m;
        if (!IsAccessible(m)) return null;
        if (m.IsGenericMethod)
        {
            if (!typeArgs.IsDefaultOrEmpty)
            {
                if (typeArgs.Length != m.Arity) return null;
                m = m.Construct(typeArgs);
                if (!SatisfiesConstraints(m)) return null;
            }
            else
            {
                var inferred = InferTypeArguments(m, args, isIndexer, indexerCount, expected);
                if (inferred.IsDefault) return null;
                m = m.Construct(inferred);
                if (!SatisfiesConstraints(m)) return null;
            }
        }
        else if (!typeArgs.IsDefaultOrEmpty) return null;

        var ps = EffectiveParameters(m, isIndexer, indexerCount);
        // normal form
        if (TryApplicable(ps, args, expanded: false, out var defaults))
            return new Candidate { Method = m, Parameters = ps, Expanded = false, DefaultsUsed = defaults, Original = original };
        if (ps.Length > 0 && ps[^1].IsParams && TryApplicable(ps, args, expanded: true, out defaults))
            return new Candidate { Method = m, Parameters = ps, Expanded = true, DefaultsUsed = defaults, Original = original };
        return null;
    }

    private bool TryApplicable(ImmutableArray<ParameterSymbol> ps, ImmutableArray<BoundExpression> args, bool expanded, out int defaultsUsed)
    {
        defaultsUsed = 0;
        var n = ps.Length;
        if (!expanded)
        {
            if (args.Length > n) return false;
            for (var i = 0; i < n; i++)
            {
                if (i < args.Length)
                {
                    if (!ArgumentConvertible(args[i], ps[i].Type, ps[i].RefKind)) return false;
                }
                else
                {
                    if (ps[i].IsParams) { defaultsUsed++; continue; }
                    if (!ps[i].IsOptional) return false;
                    defaultsUsed++;
                }
            }
            return true;
        }
        if (args.Length < n - 1) return false;
        for (var i = 0; i < n - 1; i++)
        {
            if (!ArgumentConvertible(args[i], ps[i].Type, ps[i].RefKind)) return false;
        }
        var elemType = ((ArrayTypeSymbol)ps[^1].Type).ElementType;
        for (var i = n - 1; i < args.Length; i++)
            if (!ArgumentConvertible(args[i], elemType, RefKind.None)) return false;
        return true;
    }

    internal bool ArgumentConvertible(BoundExpression arg, TypeSymbol paramType, RefKind refKind)
    {
        if (paramType.IsErrorType) return true;
        if (arg is BoundUnbound u)
        {
            if (u.IsLambda) return LambdaConvertible(u.Lambda!, paramType);
            if (u.Type != null && ClassifyImplicit(null, u.Type, paramType).Exists) return true;
            var b = Speculatively(() => u.BindToType(paramType));
            return b is not BoundUnbound && !b.HasErrors;
        }
        if (arg is BoundMethodGroup mg) return MethodGroupConvertible(mg, paramType);
        if (arg.Type == null) return false;
        if (arg.HasErrors) return true;
        if (refKind != RefKind.None) return arg.Type.Equals(paramType);
        return ClassifyImplicit(arg, arg.Type, paramType).Exists;
    }

    private readonly Dictionary<(LambdaInfo, string), bool> _lambdaConvertibleCache = new();

    internal bool LambdaConvertible(LambdaInfo info, TypeSymbol target)
    {
        var d = target.StripNullable() as NamedTypeSymbol;
        if (d is not { TypeKind: TypeKind.Delegate }) return false;
        var invoke = d.DelegateInvokeMethod;
        if (invoke == null || invoke.Parameters.Length != info.ParameterCount) return false;
        var key = (info, d.ToDisplayString());
        if (_lambdaConvertibleCache.TryGetValue(key, out var cached)) return cached;
        var paramTypes = invoke.Parameters.Select(p => p.Type).ToImmutableArray();
        for (var i = 0; i < paramTypes.Length; i++)
        {
            var ex = info.ExplicitParameterTypes[i];
            if (ex != null && !ex.Equals(paramTypes[i]) && !paramTypes[i].IsErrorType) { _lambdaConvertibleCache[key] = false; return false; }
        }
        var ok = Speculatively(() => info.Bind(paramTypes, invoke.ReturnType, true)) is { } bl && !bl.HasErrors;
        _lambdaConvertibleCache[key] = ok;
        return ok;
    }

    internal bool MethodGroupConvertible(BoundMethodGroup group, TypeSymbol target)
    {
        var d = target.StripNullable() as NamedTypeSymbol;
        if (d is not { TypeKind: TypeKind.Delegate }) return false;
        return ResolveMethodGroupForDelegate(group, d) != null;
    }

    internal MethodSymbol? ResolveMethodGroupForDelegate(BoundMethodGroup group, NamedTypeSymbol delegateType)
    {
        var invoke = delegateType.DelegateInvokeMethod;
        if (invoke == null) return null;
        var args = invoke.Parameters.Select(p => (BoundExpression)new BoundDefaultValue(p.Type)).ToImmutableArray();
        var candidates = group.Candidates.ToList();
        ResolvedCall? res = null;
        if (candidates.Count > 0)
            res = Speculatively(() => ResolveOverloads(candidates, args, group.TypeArguments, group.Syntax!, group.Name, report: false));
        if (res == null && group.Receiver is { Type: not null } recv && group.Receiver is not BoundTypeExpression)
        {
            var ext = LookupExtensionMethods(group.Name);
            res = Speculatively(() => ResolveOverloads(ext, args, group.TypeArguments, group.Syntax!, group.Name, false, extensionReceiver: recv));
        }
        if (res == null) return null;
        // return type must be convertible
        var rt = res.Method.ReturnType;
        if (!invoke.ReturnType.IsVoid && !rt.IsVoid && !ClassifyImplicit(null, rt, invoke.ReturnType).Exists) return null;
        if (invoke.ReturnType.IsVoid == false && rt.IsVoid) return null;
        return res.Method;
    }

    private bool SatisfiesConstraints(MethodSymbol constructed)
    {
        var tps = constructed.ConstructedFrom.TypeParameters;
        var args = constructed.TypeArguments;
        if (tps.Length != args.Length) return true;
        var map = new TypeMap(tps, args);
        for (var i = 0; i < tps.Length; i++)
            if (!SatisfiesConstraint(tps[i], args[i], map)) return false;
        return true;
    }

    internal bool SatisfiesConstraint(TypeParameterSymbol tp, TypeSymbol arg, TypeMap map)
    {
        if (arg.IsErrorType) return true;
        if (tp.HasValueTypeConstraint && !(arg.IsValueType || arg is TypeParameterSymbol { HasValueTypeConstraint: true })) return false;
        if (tp.HasValueTypeConstraint && arg is NullableTypeSymbol) return false;
        if (tp.HasReferenceTypeConstraint && !(arg.IsReferenceType || arg is TypeParameterSymbol { HasReferenceTypeConstraint: true })) return false;
        if (tp.HasConstructorConstraint && !tp.HasValueTypeConstraint)
        {
            if (arg is NamedTypeSymbol nt)
            {
                if (nt.IsAbstract && nt.TypeKind == TypeKind.Class) return false;
                if (nt.TypeKind == TypeKind.Class && !nt.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)) return false;
            }
            else if (arg is TypeParameterSymbol atp && !atp.HasConstructorConstraint) return false;
        }
        foreach (var c in tp.ConstraintTypes)
        {
            var sc = map.Substitute(c);
            if (!ClassifyImplicit(null, arg.StripNullable(), sc).Exists && !(arg is TypeParameterSymbol)) return false;
            if (arg is TypeParameterSymbol atp2 && !ClassifyImplicit(null, atp2, sc).Exists) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ better function member

    private bool IsBetter(Candidate c1, Candidate c2, ImmutableArray<BoundExpression> args)
    {
        var atLeastOneBetter = false;
        var count = args.Length;
        for (var i = 0; i < count; i++)
        {
            var t1 = ParamTypeAt(c1, i);
            var t2 = ParamTypeAt(c2, i);
            if (t1 == null || t2 == null) continue;
            var r = CompareConversions(args[i], t1, t2);
            if (r < 0) return false;
            if (r > 0) atLeastOneBetter = true;
        }
        if (atLeastOneBetter) return true;

        // tie-breakers
        var g1 = c1.Original.IsGenericMethod;
        var g2 = c2.Original.IsGenericMethod;
        if (!g1 && g2) return true;
        if (g1 && !g2) return false;
        if (!c1.Expanded && c2.Expanded) return true;
        if (c1.Expanded && !c2.Expanded) return false;
        if (c1.DefaultsUsed < c2.DefaultsUsed) return true;
        if (c1.DefaultsUsed > c2.DefaultsUsed) return false;
        // more specific parameter types (compare original declarations)
        if (c1.Original.Parameters.Length == c2.Original.Parameters.Length)
        {
            var allAtLeast = true;
            var anyBetter = false;
            for (var i = 0; i < c1.Original.Parameters.Length; i++)
            {
                var p1 = c1.Original.Parameters[i].Type;
                var p2 = c2.Original.Parameters[i].Type;
                if (p1.Equals(p2)) continue;
                var a = ClassifyImplicit(null, p1, p2).Exists;
                var b = ClassifyImplicit(null, p2, p1).Exists;
                if (a && !b) anyBetter = true;
                else if (!a) allAtLeast = false;
            }
            if (allAtLeast && anyBetter) return true;
            if (allAtLeast && !anyBetter && !c2.Method.Equals(c1.Method)) return !AnyStrictlyMoreSpecific(c2, c1);
        }
        return false;
    }

    private bool AnyStrictlyMoreSpecific(Candidate a, Candidate b)
    {
        for (var i = 0; i < a.Original.Parameters.Length && i < b.Original.Parameters.Length; i++)
        {
            var p1 = a.Original.Parameters[i].Type;
            var p2 = b.Original.Parameters[i].Type;
            if (!p1.Equals(p2) && ClassifyImplicit(null, p1, p2).Exists && !ClassifyImplicit(null, p2, p1).Exists) return true;
        }
        return false;
    }

    private TypeSymbol? ParamTypeAt(Candidate c, int i)
    {
        var ps = c.Parameters;
        if (i < ps.Length - 1 || (!c.Expanded && i < ps.Length)) return ps[i].Type;
        if (c.Expanded && ps.Length > 0) return ((ArrayTypeSymbol)ps[^1].Type).ElementType;
        return null;
    }

    /// <summary>Positive if converting to <paramref name="t1"/> is better, negative if <paramref name="t2"/> is better, 0 if equal.</summary>
    private int CompareConversions(BoundExpression arg, TypeSymbol t1, TypeSymbol t2)
    {
        if (t1.Equals(t2)) return 0;
        if (arg is BoundUnbound { IsLambda: true } or BoundMethodGroup)
        {
            // prefer delegates whose return type is "more specific" (non-void over void, then by conversion)
            if (t1.StripNullable() is NamedTypeSymbol { TypeKind: TypeKind.Delegate } d1 && t2.StripNullable() is NamedTypeSymbol { TypeKind: TypeKind.Delegate } d2)
            {
                var r1 = d1.DelegateInvokeMethod?.ReturnType;
                var r2 = d2.DelegateInvokeMethod?.ReturnType;
                if (r1 != null && r2 != null && !r1.Equals(r2))
                {
                    if (r1.IsVoid) return -1;
                    if (r2.IsVoid) return 1;
                    var a = ClassifyImplicit(null, r1, r2).Exists;
                    var b = ClassifyImplicit(null, r2, r1).Exists;
                    if (a && !b) return 1;
                    if (b && !a) return -1;
                }
            }
            return 0;
        }
        var st = arg.Type;
        if (st != null)
        {
            if (st.Equals(t1)) return 1;
            if (st.Equals(t2)) return -1;
        }
        var c1 = ClassifyImplicit(null, t1, t2).Exists;
        var c2 = ClassifyImplicit(null, t2, t1).Exists;
        if (c1 && !c2) return 1;
        if (c2 && !c1) return -1;
        // signed vs unsigned preference
        if (t1.SpecialType.IsSignedIntegral() && t2.SpecialType.IsUnsignedIntegral()) return 1;
        if (t2.SpecialType.IsSignedIntegral() && t1.SpecialType.IsUnsignedIntegral()) return -1;
        return 0;
    }

    // ------------------------------------------------------------------ building arguments

    private ImmutableArray<BoundExpression> BuildArguments(Candidate c, ImmutableArray<BoundExpression> args, SyntaxNode at, bool report)
    {
        var ps = c.Parameters;
        var result = ImmutableArray.CreateBuilder<BoundExpression>(ps.Length);
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var isParamsExpanded = c.Expanded && i == ps.Length - 1;
            if (isParamsExpanded)
            {
                var elemType = ((ArrayTypeSymbol)p.Type).ElementType;
                var elems = ImmutableArray.CreateBuilder<BoundExpression>();
                for (var j = i; j < args.Length; j++)
                    elems.Add(ConvertArg(args[j], elemType, report));
                result.Add(new BoundArrayCreation((ArrayTypeSymbol)p.Type, new BoundLiteral(new ConstantValue(elems.Count), IntType), elems.ToImmutable()) { Syntax = at });
            }
            else if (i < args.Length)
            {
                result.Add(ConvertArg(args[i], p.Type, report, p.RefKind));
            }
            else if (p.IsParams)
            {
                var at2 = (ArrayTypeSymbol)p.Type;
                result.Add(new BoundArrayCreation(at2, new BoundLiteral(new ConstantValue(0), IntType), ImmutableArray<BoundExpression>.Empty) { Syntax = at });
            }
            else
            {
                result.Add(DefaultArgument(p, at));
            }
        }
        return result.MoveToImmutable();
    }

    private BoundExpression ConvertArg(BoundExpression arg, TypeSymbol paramType, bool report, RefKind refKind = RefKind.None)
    {
        if (arg is BoundUnbound u && u.IsLambda)
        {
            var d = paramType.StripNullable();
            var bound = u.BindToType(d);
            return ConvertImplicit(bound, paramType, arg.Syntax, report);
        }
        return ConvertImplicit(arg, paramType, arg.Syntax, report);
    }

    internal BoundExpression DefaultArgument(ParameterSymbol p, SyntaxNode at)
    {
        var dv = p.DefaultValue;
        var t = p.Type;
        if (dv is { IsNull: true } || dv == null)
        {
            if (t is NullableTypeSymbol || !t.IsValueType) return new BoundConversion(new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance), ConversionKind.NullLiteral, t) { Syntax = at };
            return new BoundDefaultValue(t) { Syntax = at };
        }
        var inner = t.StripNullable();
        TypeSymbol litType = inner.TypeKind == TypeKind.Enum ? inner : inner.SpecialType != SpecialType.None ? inner : TypeOfConstant(dv.Value.Value);
        BoundExpression lit = new BoundLiteral(dv.Value, litType) { Syntax = at };
        if (t is NullableTypeSymbol) lit = new BoundConversion(lit, ConversionKind.ToNullable, t) { Syntax = at };
        return lit;
    }

    private TypeSymbol TypeOfConstant(object? v) => v switch
    {
        int => IntType, long => Special(SpecialType.Int64), double => DoubleType, float => Special(SpecialType.Single),
        decimal => Special(SpecialType.Decimal), bool => BoolType, string => StringType, char => Special(SpecialType.Char),
        _ => ObjectType,
    };

    // ------------------------------------------------------------------ errors

    private void ReportNoApplicable(List<MethodSymbol> all, ImmutableArray<BoundExpression> args, ImmutableArray<TypeSymbol> typeArgs, SyntaxNode at, string name,
        bool isIndexer, int indexerCount, bool isExtension)
    {
        if (all.Any(a => args.Any(x => x.HasErrors))) return;
        if (all.Count == 0)
        {
            Report(Descriptors.NotCallable, at);
            return;
        }
        var argDesc = string.Join(", ", args.Select(DescribeArgType));
        if (all.Count == 1)
        {
            var m = all[0];
            var ps = EffectiveParameters(m, isIndexer, indexerCount);
            if (m.IsGenericMethod && typeArgs.IsDefaultOrEmpty)
            {
                var inferred = InferTypeArguments(m, args, isIndexer, indexerCount, null);
                if (inferred.IsDefault)
                {
                    Report(Descriptors.CannotInferTypeArguments, at, name);
                    return;
                }
            }
            var required = ps.Count(p => !p.IsOptional && !p.IsParams);
            var hasParams = ps.Length > 0 && ps[^1].IsParams;
            if (args.Length < required || (!hasParams && args.Length > ps.Length))
            {
                var expected = hasParams ? $"at least {required}" : required == ps.Length ? ps.Length.ToString() : $"{required}-{ps.Length}";
                Report(Descriptors.WrongArgumentCount, at, name, expected, args.Length);
                return;
            }
            for (var i = 0; i < args.Length && i < ps.Length; i++)
            {
                var pt = ps[i].Type;
                if (!ArgumentConvertible(args[i], pt, ps[i].RefKind))
                {
                    if (args[i] is BoundUnbound { IsLambda: true })
                    {
                        Report(Descriptors.LambdaNotConvertible, args[i].Syntax ?? at, pt.ToDisplayString());
                        return;
                    }
                    var a = args[i];
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
        var source = a.Type!;
        if (source.IsNullType) { Report(Descriptors.NullNotAssignable, at, pt.ToDisplayString()); return; }
        if (source is NullableTypeSymbol sn && ClassifyImplicit(null, sn.Underlying, pt).Exists)
        {
            Report(Descriptors.PossiblyNullAssign, at, source.ToDisplayString(), pt.ToDisplayString());
            return;
        }
        Report(Descriptors.ArgumentNotAssignable, at, source.ToDisplayString(), pt.ToDisplayString(), "");
    }

    private string DescribeArgType(BoundExpression a) => a switch
    {
        BoundUnbound { IsLambda: true } => "lambda",
        BoundUnbound { IsObjectLiteral: true } => "object literal",
        BoundUnbound => "collection",
        BoundMethodGroup g => g.Name,
        _ => a.Type?.ToDisplayString() ?? "?",
    };
}
