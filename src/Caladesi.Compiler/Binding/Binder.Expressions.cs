using System.Collections.Immutable;
using System.Numerics;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    // ------------------------------------------------------------------ entry points

    /// <summary>Binds an expression; the result may be a type/namespace/method group or a target-typed placeholder.</summary>
    internal BoundExpression BindExpression(ExpressionSyntax syntax, TypeSymbol? target = null)
    {
        var e = BindExpressionCore(syntax, target);
        if (target != null && e is BoundUnbound u && !target.IsErrorType)
        {
            var bound = u.BindToType(target);
            if (bound is not BoundUnbound) e = bound;
        }
        if (e.Syntax == null && e is not BoundUnbound) { }
        if (BoundMap != null) BoundMap[syntax] = e;
        if (TypeMap != null && e.Type != null) TypeMap[syntax] = e.Type;
        return e;
    }

    /// <summary>Binds an expression that must produce a value (not a type, namespace, or module).</summary>
    internal BoundExpression BindValue(ExpressionSyntax syntax, TypeSymbol? target = null)
    {
        var e = BindExpression(syntax, target);
        switch (e)
        {
            case BoundTypeExpression or BoundNamespaceExpression or BoundModuleExpression:
                ReportNotAValue(e, syntax);
                return Bad(syntax, e);
        }
        return e;
    }

    /// <summary>Binds <paramref name="syntax"/> and converts it to <paramref name="target"/>.</summary>
    internal BoundExpression BindToType(ExpressionSyntax syntax, TypeSymbol target)
    {
        var e = BindValue(syntax, target);
        return ConvertImplicit(e, target, syntax);
    }

    /// <summary>Binds an expression whose result is used as a value of unconstrained type (e.g., an initializer without annotation).</summary>
    internal BoundExpression BindRValue(ExpressionSyntax syntax)
    {
        var e = BindValue(syntax);
        if (e is BoundUnbound || e is BoundMethodGroup || e.Type == null)
        {
            if (e is BoundUnbound { Type: { } nt }) return ConvertImplicit(e, nt, syntax);
            var t = WidenInferredType(e, syntax, report: true);
            return e is BoundBadExpression ? e : Bad(syntax, e);
        }
        return e;
    }

    private BoundExpression BindExpressionCore(ExpressionSyntax syntax, TypeSymbol? target)
    {
        switch (syntax)
        {
            case LiteralExpressionSyntax lit: return BindLiteral(lit, target);
            case TemplateExpressionSyntax t: return BindTemplate(t);
            case IdentifierNameSyntax id: return BindIdentifier(id);
            case GenericNameSyntax g: return BindGenericName(g);
            case ThisExpressionSyntax th: return BindThis(th);
            case SuperExpressionSyntax sup:
                Report(Descriptors.SuperNotAllowedHere, sup);
                return Bad(sup);
            case ParenthesizedExpressionSyntax p:
            {
                var inner = BindExpression(p.Expression, target);
                return inner;
            }
            case ArrayLiteralExpressionSyntax arr: return BindCollectionExpression(arr, target);
            case ObjectLiteralExpressionSyntax obj: return BindObjectLiteral(obj, target);
            case ArrowFunctionSyntax af: return BindArrowFunction(af, target);
            case FunctionExpressionSyntax fe: return BindFunctionExpression(fe, target);
            case PrefixUnaryExpressionSyntax pu: return BindPrefixUnary(pu, target);
            case PostfixUnaryExpressionSyntax pf: return BindPostfixUnary(pf, valueUsed: true);
            case AwaitExpressionSyntax aw: return BindAwait(aw);
            case TypeOfExpressionSyntax to: return BindTypeOf(to);
            case YieldExpressionSyntax y:
                Report(Descriptors.YieldNotAllowedHere, y);
                return Bad(y);
            case BinaryExpressionSyntax b: return BindBinary(b, target);
            case AssignmentExpressionSyntax a: return BindAssignment(a, valueUsed: true);
            case ConditionalExpressionSyntax c: return BindConditional(c, target);
            case InvocationExpressionSyntax inv: return BindInvocation(inv, target);
            case NewExpressionSyntax n: return BindNew(n, target);
            case ArrayCreationExpressionSyntax ac: return BindArrayCreation(ac);
            case MemberAccessExpressionSyntax ma: return BindMemberAccess(ma);
            case ElementAccessExpressionSyntax ea: return BindElementAccess(ea);
            case NonNullExpressionSyntax nn: return BindNonNull(nn);
            case AsExpressionSyntax asx: return BindAs(asx);
            case SatisfiesExpressionSyntax sat: return BindSatisfies(sat);
            case InstanceOfExpressionSyntax io: return BindInstanceOf(io);
            case SpreadElementSyntax sp:
                Report(Descriptors.SpreadNotAllowedHere, sp);
                return Bad(sp);
            case MissingExpressionSyntax:
                return Bad(syntax);
            case TypeSyntax ts:
                // a type used where an expression is expected (e.g. array type as a generic argument)
                return new BoundTypeExpression(BindType(ts)) { Syntax = syntax };
        }
        Report(Descriptors.FeatureNotSupported, syntax, syntax.Kind.ToString());
        return Bad(syntax);
    }

    // ------------------------------------------------------------------ literals

    private BoundExpression BindLiteral(LiteralExpressionSyntax lit, TypeSymbol? target)
    {
        var tok = lit.Token;
        switch (lit.Kind)
        {
            case SyntaxKind.NumericLiteralExpression:
            {
                var v = tok.Value;
                var st = v switch
                {
                    int => SpecialType.Int32, long => SpecialType.Int64, uint => SpecialType.UInt32, ulong => SpecialType.UInt64,
                    double => SpecialType.Double, float => SpecialType.Single, decimal => SpecialType.Decimal, _ => SpecialType.Int32,
                };
                return new BoundLiteral(new ConstantValue(v ?? 0), Special(st)) { Syntax = lit };
            }
            case SyntaxKind.BigIntLiteralExpression:
                return new BoundLiteral(new ConstantValue(tok.Value ?? BigInteger.Zero), Special(SpecialType.BigInteger)) { Syntax = lit };
            case SyntaxKind.StringLiteralExpression:
                return new BoundLiteral(new ConstantValue(tok.Value as string ?? ""), StringType) { Syntax = lit };
            case SyntaxKind.NoSubstitutionTemplateExpression:
                return new BoundLiteral(new ConstantValue(tok.Value as string ?? ""), StringType) { Syntax = lit };
            case SyntaxKind.TrueLiteralExpression:
                return new BoundLiteral(new ConstantValue(true), BoolType) { Syntax = lit };
            case SyntaxKind.FalseLiteralExpression:
                return new BoundLiteral(new ConstantValue(false), BoolType) { Syntax = lit };
            case SyntaxKind.NullLiteralExpression:
                return new BoundLiteral(ConstantValue.Null, NullTypeSymbol.Instance) { Syntax = lit };
        }
        return Bad(lit);
    }

    private BoundExpression BindTemplate(TemplateExpressionSyntax t)
    {
        var parts = new List<BoundExpression>();
        void AddText(string s)
        {
            if (s.Length > 0) parts.Add(new BoundLiteral(new ConstantValue(s), StringType) { Syntax = t });
        }
        AddText(t.Head.Value as string ?? "");
        foreach (var span in t.Spans)
        {
            var e = BindRValue(span.Expression);
            parts.Add(e);
            AddText(span.Literal.Value as string ?? "");
        }
        if (parts.Count == 0) return new BoundLiteral(new ConstantValue(""), StringType) { Syntax = t };
        return BuildStringConcat(parts, t);
    }

    // ------------------------------------------------------------------ identifiers

    private BoundExpression BindIdentifier(IdentifierNameSyntax id)
    {
        if (id.Identifier.IsMissing) return Bad(id);
        var name = id.Identifier.Text;
        var local = LookupLocal(name);
        if (local != null)
        {
            Record(id, local);
            return SymbolToExpression(local, id);
        }
        return BindModuleLevelName(name, 0, ImmutableArray<TypeSymbol>.Empty, id, id.Identifier.Span);
    }

    private BoundExpression BindGenericName(GenericNameSyntax g)
    {
        var args = BindTypeArguments(g.TypeArguments);
        var name = g.Name;
        var local = LookupLocal(name);
        if (local != null)
        {
            Report(Descriptors.NotGeneric, g, name);
            return Bad(g);
        }
        return BindModuleLevelName(name, args.Length, args, g, g.Identifier.Span);
    }

    private BoundExpression SymbolToExpression(Symbol s, SyntaxNode syntax)
    {
        switch (s)
        {
            case LocalSymbol l:
                return ApplyNarrowing(new BoundLocal(l) { Syntax = syntax });
            case ParameterSymbol p:
                return ApplyNarrowing(new BoundParameter(p) { Syntax = syntax });
            case FieldSymbol f:
                return new BoundFieldAccess(null, f) { Syntax = syntax };
        }
        return Bad(syntax);
    }

    private BoundExpression BindModuleLevelName(string name, int arity, ImmutableArray<TypeSymbol> typeArgs, SyntaxNode syntax, Text.TextSpan span)
    {
        if (arity == 0 && _typeParams.TryGetValue(name, out var tp))
            return new BoundTypeExpression(tp) { Syntax = syntax };

        var cands = _file.Scope.Lookup(name);
        if (cands.Length > 0)
        {
            var methods = cands.OfType<MethodSymbol>().ToImmutableArray();
            if (methods.Length > 0)
            {
                Record(syntax, methods[0]);
                return new BoundMethodGroup(null, name, methods, typeArgs) { Syntax = syntax };
            }
            foreach (var c in cands)
            {
                switch (c)
                {
                    case FieldSymbol f:
                        Record(syntax, f);
                        return new BoundFieldAccess(null, f) { Syntax = syntax };
                    case PropertySymbol { IsStatic: true } p:
                        Record(syntax, p);
                        return new BoundPropertyAccess(null, p, ImmutableArray<BoundExpression>.Empty) { Syntax = syntax };
                }
            }
            foreach (var c in cands)
            {
                if (c is NamedTypeSymbol nt && nt.OwnArity == arity)
                {
                    Record(syntax, nt);
                    var ty = arity == 0 ? NamedOrNullable(nt) : nt.Construct(typeArgs);
                    return new BoundTypeExpression(ty) { Syntax = syntax };
                }
                if (c is TypeAliasSymbol al && al.TypeParameters.Length == arity)
                    return new BoundTypeExpression(ExpandAlias(al, typeArgs)) { Syntax = syntax };
            }
            foreach (var c in cands)
            {
                if (c is NamespaceSymbol ns) { Record(syntax, ns); return new BoundNamespaceExpression(ns) { Syntax = syntax }; }
                if (c is FileModuleSymbol fm) { Record(syntax, fm); return new BoundModuleExpression(fm.File) { Syntax = syntax }; }
            }
            var generic = cands.OfType<NamedTypeSymbol>().FirstOrDefault();
            if (generic != null)
            {
                Report(arity == 0 ? Descriptors.TypeRequiresArguments : Descriptors.TypeNotGeneric, span, name, arity == 0 ? generic.OwnArity : arity);
                return Bad(syntax);
            }
        }
        if (arity == 0)
        {
            var p = PredefinedType(name);
            if (p != null && p is not NullableTypeSymbol) return new BoundTypeExpression(p) { Syntax = syntax };
            switch (name)
            {
                case "NaN": return new BoundLiteral(new ConstantValue(double.NaN), DoubleType) { Syntax = syntax };
                case "Infinity": return new BoundLiteral(new ConstantValue(double.PositiveInfinity), DoubleType) { Syntax = syntax };
                case "undefined": Report(Descriptors.UndefinedNotAllowed, span); return Bad(syntax);
                case "any": Report(Descriptors.AnyNotAllowed, span); return Bad(syntax);
            }
        }
        else if (s_predefinedGenericNames.Contains(name))
        {
            var pg = PredefinedGenericType(name, typeArgs, syntax);
            if (pg != null) return new BoundTypeExpression(pg) { Syntax = syntax };
        }
        Report(Descriptors.NameNotFound, span, name);
        return Bad(syntax);
    }

    private BoundExpression BindThis(ThisExpressionSyntax th)
    {
        var type = CurrentType;
        if (type == null || _isStaticContext)
        {
            Report(Descriptors.ThisNotAvailable, th);
            return Bad(th);
        }
        _usesThis = true;
        return new BoundThis(type.IsGenericType ? type.Construct(type.TypeParameters.CastArray<TypeSymbol>()) : type) { Syntax = th };
    }

    // ------------------------------------------------------------------ typeof, non-null, as, satisfies, instanceof

    private BoundExpression BindTypeOf(TypeOfExpressionSyntax to)
    {
        var t = BindType(to.Type);
        var sys = _c.GetSpecialType(SpecialType.Type);
        return new BoundTypeOf(t, sys) { Syntax = to };
    }

    private BoundExpression BindNonNull(NonNullExpressionSyntax nn)
    {
        var e = BindValue(nn.Expression);
        if (e.Type is NullableTypeSymbol n)
        {
            var kind = ConversionKind.FromNullable;
            return new BoundConversion(e, kind, n.Underlying) { Syntax = nn };
        }
        return e;
    }

    private BoundExpression BindAs(AsExpressionSyntax asx)
    {
        var target = BindType(asx.Type);
        var e = BindValue(asx.Expression, target);
        if (target.IsErrorType) return Bad(asx, e);
        if (e is BoundUnbound or BoundMethodGroup) return ConvertImplicit(e, target, asx);
        return ConvertExplicit(e, target, asx);
    }

    private BoundExpression BindSatisfies(SatisfiesExpressionSyntax sat)
    {
        var target = BindType(sat.Type);
        var e = BindValue(sat.Expression, target);
        if (target.IsErrorType) return e;
        var converted = ConvertImplicit(e, target, sat);
        // the result keeps the expression's own type (satisfies is a compile-time check only)
        return e is BoundUnbound ? converted : e;
    }

    private BoundExpression BindInstanceOf(InstanceOfExpressionSyntax io)
    {
        var target = BindType(io.Type);
        var e = BindValue(io.Expression);
        if (target.IsErrorType || e.HasErrors) return new BoundLiteral(new ConstantValue(false), BoolType) { Syntax = io };
        var source = e.Type;
        if (source == null) { ReportNotAValue(e, io); return Bad(io, e); }
        var targetBase = target.StripNullable();
        // union cases are tested by tag
        if (source.StripNullable() is NamedTypeSymbol { IsUnionType: true } u)
        {
            var cases = u.UnionCases;
            for (var i = 0; i < cases.Length; i++)
                if (cases[i].Equals(targetBase))
                    return new BoundTypeTest(UnwrapNullableValue(e), targetBase, BoolType, TypeTestKind.UnionCase, i) { Syntax = io };
        }
        var conv = ClassifyExplicit(null, source.StripNullable(), targetBase);
        if (!conv.Exists)
        {
            Report(Descriptors.InstanceOfImpossible, io, source.ToDisplayString(), target.ToDisplayString());
            return new BoundLiteral(new ConstantValue(false), BoolType) { Syntax = io };
        }
        return new BoundTypeTest(e, targetBase, BoolType) { Syntax = io };
    }

    private BoundExpression UnwrapNullableValue(BoundExpression e) =>
        e.Type is NullableTypeSymbol n ? new BoundConversion(e, ConversionKind.FromNullable, n.Underlying) { Syntax = e.Syntax } : e;

    // ------------------------------------------------------------------ conditional

    private BoundExpression BindConditional(ConditionalExpressionSyntax c, TypeSymbol? target)
    {
        var cond = ConvertToBool(BindValue(c.Condition), c.Condition);
        var (whenTrueNarrow, whenFalseNarrow) = GetNarrowing(cond);

        var saved = _narrow;
        _narrow = ApplyNarrowings(saved, whenTrueNarrow);
        var a = BindValue(c.WhenTrue, target);
        _narrow = ApplyNarrowings(saved, whenFalseNarrow);
        var b = BindValue(c.WhenFalse, target);
        _narrow = saved;

        if (target != null)
        {
            var ca = ConvertImplicit(a, target, c.WhenTrue);
            var cb = ConvertImplicit(b, target, c.WhenFalse);
            return new BoundConditional(cond, ca, cb, target) { Syntax = c };
        }
        if (a is BoundUnbound || b is BoundUnbound || a.Type == null || b.Type == null)
        {
            if (a is BoundUnbound && b.Type != null) return Build(ConvertImplicit(a, b.Type, c.WhenTrue), b);
            if (b is BoundUnbound && a.Type != null) return Build(a, ConvertImplicit(b, a.Type, c.WhenFalse));
            Report(Descriptors.CannotInferType, c);
            return Bad(c, a, b);
        }
        var type = BestCommonType([a, b], c);
        if (type == null)
        {
            Report(Descriptors.NoBestCommonType, c, a.Type.ToDisplayString(), b.Type.ToDisplayString());
            return Bad(c, a, b);
        }
        return Build(ConvertImplicit(a, type, c.WhenTrue), ConvertImplicit(b, type, c.WhenFalse));

        BoundExpression Build(BoundExpression x, BoundExpression y) =>
            new BoundConditional(cond, x, y, x.Type ?? y.Type!) { Syntax = c };
    }

    /// <summary>Finds the best common type of several expressions (C# rules: a type all others convert to), including null handling.</summary>
    internal TypeSymbol? BestCommonType(IReadOnlyList<BoundExpression> exprs, SyntaxNode at)
    {
        var hasNull = false;
        var candidates = new List<TypeSymbol>();
        foreach (var e in exprs)
        {
            var t = e.Type;
            if (t == null) continue;
            if (t.IsErrorType) return ErrorType;
            if (t.IsNullType) { hasNull = true; continue; }
            if (t.IsNeverType) continue;
            if (t is NullableTypeSymbol) hasNull = true;
            if (!candidates.Any(x => x.Equals(t))) candidates.Add(t);
        }
        if (candidates.Count == 0) return hasNull ? NullTypeSymbol.Instance : null;
        TypeSymbol? best = null;
        foreach (var cand in candidates)
        {
            var ok = true;
            foreach (var e in exprs)
            {
                if (e.Type == null || e.Type.IsNullType || e.Type.IsNeverType) continue;
                if (!ClassifyImplicit(e, e.Type, cand).Exists) { ok = false; break; }
            }
            if (ok) { best = cand; break; }
        }
        if (best == null)
        {
            // try the nullable-stripped candidates
            foreach (var cand in candidates)
            {
                var stripped = cand.StripNullable();
                var ok = true;
                foreach (var e in exprs)
                {
                    if (e.Type == null || e.Type.IsNullType || e.Type.IsNeverType) continue;
                    if (!ClassifyImplicit(e, e.Type.StripNullable(), stripped).Exists) { ok = false; break; }
                }
                if (ok) { best = stripped; break; }
            }
        }
        if (best == null) return null;
        if (hasNull && best is not NullableTypeSymbol) best = new NullableTypeSymbol(best);
        return best;
    }
}
