using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    private bool _isAsyncContext;

    // ------------------------------------------------------------------ arguments

    private ImmutableArray<BoundExpression> BindArguments(SeparatedSyntaxList<ExpressionSyntax> list)
    {
        ImmutableArray<BoundExpression>.Builder b = ImmutableArray.CreateBuilder<BoundExpression>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            ExpressionSyntax a = list[i];
            if (a is SpreadElementSyntax sp)
            {
                if (i != list.Count - 1)
                {
                    Report(Descriptors.SpreadMustBeLast, sp);
                }

                b.Add(BindValue(sp.Expression));
            }
            else
            {
                b.Add(BindValue(a));
            }
        }
        return b.MoveToImmutable();
    }

    // ------------------------------------------------------------------ invocation

    private BoundExpression BindInvocation(InvocationExpressionSyntax inv, TypeSymbol? target)
    {
        ExpressionSyntax calleeSyntax = inv.Expression;
        if (calleeSyntax is SuperExpressionSyntax)
        {
            Report(Descriptors.SuperCallPosition, calleeSyntax);
            _ = BindArguments(inv.Arguments.Arguments);
            return Bad(inv);
        }
        BoundExpression callee = BindNaturalType(BindExpression(calleeSyntax), calleeSyntax);
        if (callee is BoundBadExpression)
        {
            _ = BindArguments(inv.Arguments.Arguments);
            return Bad(inv, callee);
        }
        BoundExpression result = callee is BoundConditionalAccess ca && calleeSyntax is not ParenthesizedExpressionSyntax
            ? LiftConditional(ca, inner => BindInvocationOn(inner, inv, target), inv)
            : BindInvocationOn(callee, inv, target);
        InvalidateAfterCall(CalledMethod(result));
        return result;
    }

    private static MethodSymbol? CalledMethod(BoundExpression e)
    {
        return e switch
        {
            BoundCall c => c.Method,
            BoundObjectCreation { Constructor: { } ctor } => ctor,
            BoundConditionalAccess { WhenNotNull: { } inner } => CalledMethod(inner),
            BoundSequence { Value: { } v } => CalledMethod(v),
            _ => null,
        };
    }

    private BoundExpression BindInvocationOn(BoundExpression callee, InvocationExpressionSyntax inv, TypeSymbol? target)
    {
        ImmutableArray<BoundExpression> args = BindArguments(inv.Arguments.Arguments);
        switch (callee)
        {
            case BoundMethodGroup mg:
                return BindMethodGroupCall(mg, args, inv, target);
            case BoundTypeExpression:
                Report(Descriptors.UseNewForType, inv.Expression);
                return Bad(inv, callee);
            case BoundNamespaceExpression or BoundModuleExpression:
                ReportNotAValue(callee, inv.Expression);
                return Bad(inv, callee);
        }
        TypeSymbol? t = callee.Type;
        if (t == null || t.IsErrorType)
        {
            return Bad(inv, callee);
        }

        if (t is NullableTypeSymbol nt)
        {
            Report(Descriptors.PossiblyNull, inv.Expression, inv.Expression.ToFullString().Trim());
            t = nt.Underlying;
        }
        if (t is NamedTypeSymbol { TypeKind: TypeKind.Delegate } d && d.DelegateInvokeMethod is { } invoke)
        {
            ResolvedCall? res = ResolveOverloads([invoke], args, [], inv, "invoke", report: true);
            return res == null ? Bad(inv, callee) : new BoundCall(callee, res.Method, res.Arguments) { Syntax = inv };
        }
        Report(Descriptors.NotCallable, inv.Expression, t.ToDisplayString());
        return Bad(inv, callee);
    }

    private BoundExpression BindMethodGroupCall(BoundMethodGroup mg, ImmutableArray<BoundExpression> args, InvocationExpressionSyntax inv, TypeSymbol? target)
    {
        if (args.Any(a => a is BoundBadExpression))
        {
            return Bad(inv, [mg, .. args]);
        }

        ImmutableArray<MethodSymbol> candidates = mg.Candidates;
        bool recvIsType = mg.Receiver is BoundTypeExpression;
        ResolvedCall? res = null;
        if (candidates.Length > 0)
        {
            res = ResolveOverloads(candidates, args, mg.TypeArguments, inv, mg.Name, report: false, expectedType: target);
        }

        BoundExpression? extReceiver = null;
        if (res == null && mg.Receiver is { } r && !recvIsType && r.Type != null)
        {
            List<MethodSymbol> ext = LookupExtensionMethods(mg.Name);
            if (ext.Count > 0)
            {
                res = ResolveOverloads(ext, args, mg.TypeArguments, inv, mg.Name, report: false, extensionReceiver: r, expectedType: target);
                if (res != null)
                {
                    extReceiver = r;
                }
            }
        }
        if (res == null)
        {
            if (candidates.Length > 0)
            {
                _ = ResolveOverloads(candidates, args, mg.TypeArguments, inv, mg.Name, report: true);
            }
            else if (mg.Receiver is { } rr && rr.Type != null)
            {
                List<MethodSymbol> ext = LookupExtensionMethods(mg.Name);
                if (ext.Count > 0)
                {
                    _ = ResolveOverloads(ext, args, mg.TypeArguments, inv, mg.Name, report: true, extensionReceiver: rr);
                }
                else
                {
                    Report(Descriptors.MemberNotFound, inv.Expression, mg.Name, rr.Type.ToDisplayString());
                }
            }
            else
            {
                Report(Descriptors.NotCallable, inv.Expression);
            }

            return Bad(inv, [mg, .. args]);
        }

        if (res.Ambiguity is var (first, second))
        {
            // the lookups above are not reported (an extension method may still apply), so an ambiguity is reported once a call is chosen
            Report(Descriptors.AmbiguousCall, inv.Expression, mg.Name, Signature(first), Signature(second));
        }

        MethodSymbol m = res.Method;
        if (SymbolMap != null)
        {
            // point the callee name at the overload that was chosen
            SyntaxNode? nameNode = inv.Expression switch { MemberAccessExpressionSyntax ma => ma.Name, _ => inv.Expression };
            if (nameNode != null)
            {
                SymbolMap[nameNode] = m.OriginalDefinition;
            }
        }
        BoundExpression? receiver = null;
#pragma warning disable CA1508 // extReceiver is only set on paths not yet reachable here; kept for the extension-call path
        if (extReceiver != null || m.IsStatic)
        {
            receiver = null;
            if (m.IsStatic && mg.Receiver is { } sr && sr is not BoundTypeExpression && extReceiver == null && mg.Receiver.Type != null)
            {
                Report(Descriptors.StaticViaInstance, inv.Expression, m.Name, mg.Receiver.Type.ToDisplayString());
            }
        }
#pragma warning restore CA1508
        else
        {
            if (recvIsType)
            {
                Report(Descriptors.InstanceViaType, inv.Expression, m.Name, ((BoundTypeExpression)mg.Receiver!).TypeSym.ToDisplayString());
                return Bad(inv, [mg, .. args]);
            }
            receiver = mg.Receiver ?? ImplicitThis(inv);
            if (receiver is BoundBadExpression)
            {
                return Bad(inv, [mg, .. args]);
            }
        }
        BoundCall call = new(receiver, m, res.Arguments) { Syntax = inv, IsBaseCall = mg.IsBaseAccess };
        return res.OutLocals.IsEmpty ? call : WrapOutResults(call, res.OutLocals, inv);
    }

    /// <summary>A call with omitted trailing out parameters evaluates to a tuple of the return value (unless void) and the out values.</summary>
    private BoundExpression WrapOutResults(BoundCall call, ImmutableArray<LocalSymbol> outs, SyntaxNode syntax)
    {
        ImmutableArray<LocalSymbol>.Builder locals = outs.ToBuilder();
        ImmutableArray<BoundStatement>.Builder stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        ImmutableArray<BoundExpression>.Builder elements = ImmutableArray.CreateBuilder<BoundExpression>();
        if (call.Method.ReturnType.IsVoid)
        {
            stmts.Add(new BoundExpressionStatement(call) { Syntax = syntax });
        }
        else
        {
            LocalSymbol ret = new("<ret>", call.Type!, false, LocalOwner) { IsSynthesized = true };
            locals.Add(ret);
            stmts.Add(new BoundExpressionStatement(new BoundAssignment(new BoundLocal(ret), call) { Syntax = syntax, IsDiscarded = true }) { Syntax = syntax });
            elements.Add(new BoundLocal(ret));
        }
        foreach (LocalSymbol o in outs)
        {
            elements.Add(new BoundLocal(o));
        }

        if (elements.Count == 1)
        {
            return new BoundSequence(locals.ToImmutable(), stmts.ToImmutable(), elements[0]) { Syntax = syntax };
        }

        NamedTypeSymbol? tupleDef = Compilation.GetWellKnownType("System.ValueTuple`" + elements.Count);
        if (tupleDef == null || elements.Count > 7) { Report(Descriptors.FeatureNotSupported, syntax, "Calls with that many out parameters"); return Bad(syntax); }
        NamedTypeSymbol tuple = tupleDef.Construct(elements.Select(e => e.Type!).ToImmutableArray());
        MethodSymbol ctor = tuple.InstanceConstructors.First(c => c.Parameters.Length == elements.Count);
        BoundObjectCreation value = new(ctor, tuple, elements.ToImmutable(), []) { Syntax = syntax };
        return new BoundSequence(locals.ToImmutable(), stmts.ToImmutable(), value) { Syntax = syntax };
    }

    private BoundExpression ImplicitThis(SyntaxNode at)
    {
        NamedTypeSymbol? t = CurrentType;
        if (t == null || _isStaticContext)
        {
            Report(Descriptors.ThisNotAvailable, at);
            return Bad(at);
        }
        UsesThis = true;
        return new BoundThis(t) { Syntax = at };
    }

    // ------------------------------------------------------------------ new

    private BoundExpression BindNew(NewExpressionSyntax n, TypeSymbol? target)
    {
        TypeSyntax typeSyntax = n.Type;
        TypeSymbol type = BindNewType(typeSyntax, target);
        ImmutableArray<BoundExpression> args = n.Arguments != null ? BindArguments(n.Arguments.Arguments) : [];
        if (type.IsErrorType || args.Any(a => a is BoundBadExpression))
        {
            return BadFrom(n, args.CastArray<BoundNode>());
        }

        BoundExpression created = BuildObjectCreation(type, args, n, typeSyntax);
        InvalidateAfterCall(CalledMethod(created));
        return created;
    }

    private TypeSymbol BindNewType(TypeSyntax typeSyntax, TypeSymbol? target)
    {
        // target-typed `new List()` : infer type arguments from the expected type
        if (typeSyntax is IdentifierNameSyntax id && target != null && !id.Identifier.IsMissing)
        {
            ImmutableArray<Symbol> cands = File.Scope.Lookup(id.Identifier.Text);
            NamedTypeSymbol? generic = cands.OfType<NamedTypeSymbol>().FirstOrDefault(c => c.OwnArity > 0);
            if (generic != null && !cands.OfType<NamedTypeSymbol>().Any(c => c.OwnArity == 0))
            {
                TypeSymbol tt = target.StripNullable();
                if (tt is NamedTypeSymbol { IsGenericType: true } nt && ReferenceEquals(nt.OriginalDefinition, generic.OriginalDefinition))
                {
                    return nt;
                }

                if (tt is NamedTypeSymbol && InferNewTypeArguments(generic, tt) is { } inferred)
                {
                    return generic.Construct(inferred);
                }
            }
        }
        return BindType(typeSyntax);
    }

    /// <summary>Infers the type arguments of <paramref name="generic"/> from a target type it derives from or implements (`new List()` against `IList&lt;int&gt;`), or null when the target does not determine them all.</summary>
    private static ImmutableArray<TypeSymbol>? InferNewTypeArguments(NamedTypeSymbol generic, TypeSymbol target)
    {
        if (target is not NamedTypeSymbol { IsGenericType: true } tn)
        {
            return null;
        }

        TypeSymbol?[] solved = new TypeSymbol?[generic.TypeParameters.Length];
        IEnumerable<NamedTypeSymbol> supers = generic.AllInterfaces.Concat(BaseChain(generic));
        foreach (NamedTypeSymbol super in supers)
        {
            if (ReferenceEquals(super.OriginalDefinition, tn.OriginalDefinition) && Unify(super, tn, generic, solved))
            {
                return solved.All(s => s != null) ? [.. solved!] : null;
            }
        }

        return null;
    }

    private static IEnumerable<NamedTypeSymbol> BaseChain(NamedTypeSymbol t)
    {
        for (NamedTypeSymbol? b = t.BaseType; b != null; b = b.BaseType)
        {
            yield return b;
        }
    }

    private static bool Unify(TypeSymbol pattern, TypeSymbol actual, NamedTypeSymbol generic, TypeSymbol?[] solved)
    {
        if (pattern is TypeParameterSymbol tp)
        {
            int i = generic.TypeParameters.IndexOf(tp);
            if (i < 0)
            {
                return pattern.Equals(actual);
            }

            if (solved[i] != null)
            {
                return solved[i]!.Equals(actual);
            }

            solved[i] = actual;
            return true;
        }

        if (pattern is NamedTypeSymbol { IsGenericType: true } pn && actual is NamedTypeSymbol an && ReferenceEquals(pn.OriginalDefinition, an.OriginalDefinition))
        {
            for (int i = 0; i < pn.TypeArguments.Length; i++)
            {
                if (!Unify(pn.TypeArguments[i], an.TypeArguments[i], generic, solved))
                {
                    return false;
                }
            }

            return true;
        }

        return pattern.Equals(actual);
    }

    internal BoundExpression BuildObjectCreation(TypeSymbol type, ImmutableArray<BoundExpression> args, SyntaxNode syntax, SyntaxNode? typeSyntax = null)
    {
        if (type is TypeParameterSymbol tp)
        {
            if (!tp.HasConstructorConstraint)
            {
                Report(Descriptors.NewRequiresConstraint, typeSyntax ?? syntax, tp.Name);
                return Bad(syntax);
            }
            if (args.Length > 0) { Report(Descriptors.NewTypeParamNoArgs, syntax); return Bad(syntax); }
            return new BoundObjectCreation(null, type, [], []) { Syntax = syntax };
        }
        if (type is not NamedTypeSymbol nt || nt.TypeKind is TypeKind.Interface or TypeKind.Enum or TypeKind.Array || (nt.IsAbstract && nt.TypeKind == TypeKind.Class))
        {
            Report(Descriptors.CannotInstantiate, typeSyntax ?? syntax, type.ToDisplayString());
            return Bad(syntax);
        }
        if (nt.IsStaticClass)
        {
            Report(Descriptors.CannotInstantiate, typeSyntax ?? syntax, type.ToDisplayString());
            return Bad(syntax);
        }
        if (nt.TypeKind == TypeKind.Delegate)
        {
            if (args.Length == 1)
            {
                return ConvertImplicit(args[0], nt, syntax);
            }

            Report(Descriptors.CannotInstantiate, typeSyntax ?? syntax, type.ToDisplayString());
            return Bad(syntax);
        }
        ImmutableArray<MethodSymbol> ctors = LookupConstructors(nt);
        if (ctors.Length == 0)
        {
            if (nt.TypeKind == TypeKind.Struct && args.Length == 0)
            {
                return new BoundObjectCreation(null, type, [], []) { Syntax = syntax };
            }

            Report(Descriptors.NoAccessibleConstructor, typeSyntax ?? syntax, type.ToDisplayString());
            return Bad(syntax);
        }
        if (nt.TypeKind == TypeKind.Struct && args.Length == 0 && !ctors.Any(c => c.Parameters.Length == 0))
        {
            return new BoundObjectCreation(null, type, [], []) { Syntax = syntax };
        }

        ResolvedCall? res = ResolveOverloads(ctors, args, [], syntax, nt.Name, report: true);
        return res == null
            ? BadFrom(syntax, args.CastArray<BoundNode>())
            : new BoundObjectCreation(res.Method, type, res.Arguments, ImmutableArray<BoundMemberInit>.Empty) { Syntax = syntax };
    }

    private BoundExpression BindArrayCreation(ArrayCreationExpressionSyntax ac)
    {
        TypeSymbol elem = BindType(ac.ElementType);
        BoundExpression size = BindValue(ac.Size);
        if (elem.IsErrorType)
        {
            return Bad(ac, size);
        }

        BoundExpression len = ConvertIndex(size, ac.Size);
        ArrayTypeSymbol at = Compilation.CreateArrayType(elem);
        return new BoundArrayCreation(at, len, []) { Syntax = ac };
    }

    // ------------------------------------------------------------------ await

    private BoundExpression BindAwait(AwaitExpressionSyntax aw)
    {
        BoundExpression operand = BindValue(aw.Operand);
        if (!_isAsyncContext)
        {
            Report(Descriptors.AwaitOutsideAsync, aw.GetFirstToken()!.Span);
            return Bad(aw, operand);
        }
        if (operand is BoundBadExpression)
        {
            return Bad(aw, operand);
        }

        if (operand.Type == null) { ReportNotAValue(operand, aw); return Bad(aw, operand); }
        AwaitInfo? info = GetAwaitInfo(operand.Type, aw);
        if (info == null)
        {
            return Bad(aw, operand);
        }

        TypeSymbol resultType = info.GetResult.ReturnType;
        return new BoundAwait(operand, info, resultType) { Syntax = aw };
    }

    internal AwaitInfo? GetAwaitInfo(TypeSymbol type, SyntaxNode at)
    {
        if (type is NullableTypeSymbol n)
        {
            type = n.Underlying;
        }

        MethodSymbol? getAwaiter = LookupMembers(type, "GetAwaiter").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0 && !m.IsStatic);
        if (getAwaiter == null)
        {
            List<MethodSymbol> ext = LookupExtensionMethods("GetAwaiter");
            getAwaiter = ext.FirstOrDefault(m => m.Parameters.Length == 1 && ClassifyImplicit(null, type, m.Parameters[0].Type).Exists);
        }
        if (getAwaiter == null)
        {
            Report(Descriptors.NotAwaitable, at, type.ToDisplayString());
            return null;
        }
        TypeSymbol awaiterType = getAwaiter.ReturnType;
        PropertySymbol? isCompleted = LookupMembers(awaiterType, "IsCompleted").OfType<PropertySymbol>().FirstOrDefault();
        MethodSymbol? getResult = LookupMembers(awaiterType, "GetResult").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0);
        if (isCompleted?.GetMethod == null || getResult == null)
        {
            Report(Descriptors.NotAwaitable, at, type.ToDisplayString());
            return null;
        }
        return new AwaitInfo(getAwaiter, isCompleted, isCompleted.GetMethod, getResult, awaiterType);
    }

    // ------------------------------------------------------------------ string concatenation

    internal BoundExpression BuildStringConcat(List<BoundExpression> parts, SyntaxNode syntax)
    {
        // fold adjacent constants
        List<BoundExpression> folded = [];
        foreach (BoundExpression p in parts)
        {
            if (folded.Count > 0 && folded[^1].ConstantValue is { Value: string a } && p.ConstantValue is { } cv && p.Type!.SpecialType is SpecialType.String)
            {
                folded[^1] = new BoundLiteral(new ConstantValue(a + (string?)cv.Value), StringType) { Syntax = syntax };
            }
            else if (folded.Count > 0 && folded[^1].ConstantValue is { Value: string a2 } && p.ConstantValue is { Value: { } v2 } && (p.Type!.SpecialType is SpecialType.Int32 or SpecialType.Boolean or SpecialType.Int64 or SpecialType.Char))
            {
                folded[^1] = new BoundLiteral(new ConstantValue(a2 + ConstantToString(v2)), StringType) { Syntax = syntax };
            }
            else
            {
                folded.Add(p);
            }
        }
        parts = folded;
        TypeSymbol strType = StringType;
        NamedTypeSymbol strSym = Special(SpecialType.String);
        List<MethodSymbol> concat = [.. strSym.GetMembers("Concat").OfType<MethodSymbol>()];

        if (parts.Count == 1)
        {
            BoundExpression only = parts[0];
            if (only.Type!.SpecialType == SpecialType.String)
            {
                return only;
            }

            MethodSymbol m1 = concat.First(m => m.Parameters.Length == 1 && m.Parameters[0].Type.StripNullable().SpecialType == SpecialType.Object);
            return new BoundCall(null, m1, [BoxToObject(only, syntax)]) { Syntax = syntax };
        }
        bool allStrings = parts.All(p => p.Type!.SpecialType == SpecialType.String);
        if (allStrings && parts.Count <= 4)
        {
            MethodSymbol m = concat.First(x => x.Parameters.Length == parts.Count && x.Parameters.All(pp => pp.Type.StripNullable().SpecialType == SpecialType.String && !pp.IsParams));
            return new BoundCall(null, m, [.. parts]) { Syntax = syntax };
        }
        if (!allStrings && parts.Count <= 3)
        {
            MethodSymbol m = concat.First(x => x.Parameters.Length == parts.Count && x.Parameters.All(pp => pp.Type.StripNullable().SpecialType == SpecialType.Object && !pp.IsParams));
            return new BoundCall(null, m, [.. parts.Select(p => BoxToObject(p, syntax))]) { Syntax = syntax };
        }
        if (allStrings)
        {
            MethodSymbol m = concat.First(x => x.Parameters.Length == 1 && x.Parameters[0].IsParams && x.Parameters[0].Type.StripNullable() is ArrayTypeSymbol { ElementType: var e1 } && e1.StripNullable().SpecialType == SpecialType.String);
            BoundArrayCreation arr = new((ArrayTypeSymbol)m.Parameters[0].Type.StripNullable(), new BoundLiteral(new ConstantValue(parts.Count), IntType), [.. parts]) { Syntax = syntax };
            return new BoundCall(null, m, [arr]) { Syntax = syntax };
        }
        else
        {
            MethodSymbol m = concat.First(x => x.Parameters.Length == 1 && x.Parameters[0].IsParams && x.Parameters[0].Type.StripNullable() is ArrayTypeSymbol { ElementType: var e2 } && e2.StripNullable().SpecialType == SpecialType.Object);
            BoundArrayCreation arr = new((ArrayTypeSymbol)m.Parameters[0].Type.StripNullable(), new BoundLiteral(new ConstantValue(parts.Count), IntType), [.. parts.Select(p => BoxToObject(p, syntax))]) { Syntax = syntax };
            return new BoundCall(null, m, [arr]) { Syntax = syntax };
        }
    }

    private static string ConstantToString(object v)
    {
        return v switch
        {
            bool b => b ? "True" : "False",
            char c => c.ToString(),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
    }

    internal BoundExpression BoxToObject(BoundExpression e, SyntaxNode syntax)
    {
        TypeSymbol t = e.Type!;
        if (t.SpecialType == SpecialType.Object && t is not NullableTypeSymbol)
        {
            return e;
        }

        if (t.SpecialType == SpecialType.String)
        {
            return new BoundConversion(e, ConversionKind.ImplicitReference, ObjectType) { Syntax = syntax };
        }

        if (t.IsValueType || t is TypeParameterSymbol)
        {
            return new BoundConversion(e, ConversionKind.Boxing, ObjectType) { Syntax = syntax };
        }

        if (t is NullableTypeSymbol n)
        {
            if (n.Underlying.IsValueType)
            {
                return new BoundConversion(e, ConversionKind.Boxing, ObjectType) { Syntax = syntax }; // boxes Nullable<T>
            }

            return new BoundConversion(e, ConversionKind.TypeOnly, ObjectType) { Syntax = syntax };
        }
        return new BoundConversion(e, ConversionKind.ImplicitReference, ObjectType) { Syntax = syntax };
    }

    // ------------------------------------------------------------------ method group -> delegate

    internal BoundExpression ConvertMethodGroup(BoundMethodGroup mg, TypeSymbol target, SyntaxNode? syntax, bool report)
    {
        NamedTypeSymbol? d = target.StripNullable() as NamedTypeSymbol;
        if (d is not { TypeKind: TypeKind.Delegate })
        {
            if (report && !target.IsErrorType)
            {
                Report(Descriptors.MethodGroupNeedsDelegate, syntax, mg.Name, target.ToDisplayString());
            }

            return Bad(syntax, mg);
        }
        MethodSymbol? method = ResolveMethodGroupForDelegate(mg, d);
        if (method == null)
        {
            if (report)
            {
                Report(Descriptors.MethodGroupNotConvertible, syntax, mg.Name, target.ToDisplayString());
            }

            return Bad(syntax, mg);
        }
        BoundExpression? receiver = null;
        bool isExt = method.IsExtensionMethod && mg.Receiver is { Type: not null } && !mg.Candidates.Contains(method) && !mg.Candidates.Any(c => ReferenceEquals(c.OriginalDefinition, method.OriginalDefinition));
        if (isExt)
        {
            receiver = mg.Receiver;
        }
        else if (!method.IsStatic)
        {
            receiver = mg.Receiver ?? ImplicitThis(syntax ?? mg.Syntax!);
        }

        BoundDelegateCreation created = new(receiver, method, d) { Syntax = syntax ?? mg.Syntax };
        return target is NullableTypeSymbol ? new BoundConversion(created, ConversionKind.ToNullable, target) { Syntax = syntax } : created;
    }
}

internal static class ImmutableArrayCasts
{
    public static ImmutableArray<BoundNode> CastArray<T>(this ImmutableArray<T> a) where T : BoundNode
    {
        return [.. a];
    }
}
