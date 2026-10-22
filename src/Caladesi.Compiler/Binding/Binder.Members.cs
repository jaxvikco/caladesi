using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    // ------------------------------------------------------------------ member access

    private BoundExpression BindMemberAccess(MemberAccessExpressionSyntax ma)
    {
        if (ma.Expression is SuperExpressionSyntax)
        {
            return BindBaseMemberAccess(ma);
        }

        BoundExpression left = BindNaturalType(BindExpression(ma.Expression), ma.Expression);
        if (left is BoundBadExpression || (left.HasErrors && left.Type != null && left is not BoundTypeExpression))
        {
            return Bad(ma, left);
        }

        if (ma.IsConditional)
        {
            return BindConditionalMemberAccess(ma, left);
        }

        // a?.b.c : the chain continues inside the null-conditional access
        return left is BoundConditionalAccess ca && ma.Expression is not ParenthesizedExpressionSyntax
            ? LiftConditional(ca, inner => BindMemberOf(inner, ma.Name, ma), ma)
            : BindMemberOf(left, ma.Name, ma);
    }

    private static BoundExpression LiftConditional(BoundConditionalAccess ca, Func<BoundExpression, BoundExpression> bindOn, SyntaxNode syntax)
    {
        BoundExpression inner = bindOn(ca.WhenNotNull);
        return MakeConditionalAccess(ca.Receiver, ca.Placeholder, inner, syntax);
    }

    private static BoundExpression MakeConditionalAccess(BoundExpression receiver, BoundConditionalReceiver placeholder, BoundExpression whenNotNull, SyntaxNode syntax)
    {
        if (whenNotNull.HasErrors)
        {
            return Bad(syntax, whenNotNull);
        }

        TypeSymbol? t = whenNotNull.Type;
        TypeSymbol? resultType = t;
        if (t != null && !t.IsVoid && !t.IsNeverType && t is not NullableTypeSymbol)
        {
            resultType = new NullableTypeSymbol(t);
        }

        return new BoundConditionalAccess(receiver, placeholder, whenNotNull, resultType) { Syntax = syntax };
    }

    private BoundExpression BindConditionalMemberAccess(MemberAccessExpressionSyntax ma, BoundExpression left)
    {
        if (left is BoundTypeExpression or BoundNamespaceExpression or BoundModuleExpression or BoundMethodGroup)
        {
            Report(Descriptors.ConditionalOnNonValue, ma);
            return Bad(ma, left);
        }
        if (left is BoundConditionalAccess ca && ma.Expression is not ParenthesizedExpressionSyntax)
        {
            // a?.b?.c => a?.(b?.c)
            BoundExpression inner = ca.WhenNotNull;
            BoundExpression nested = BindConditionalMemberAccess(ma, inner);
            return nested is BoundConditionalAccess nca
                ? MakeConditionalAccess(ca.Receiver, ca.Placeholder, nca, ma)
                : nested;
        }
        TypeSymbol? lt = left.Type;
        if (lt == null) { ReportNotAValue(left, ma.Expression); return Bad(ma, left); }
        TypeSymbol underlying = lt.StripNullable();
        if (underlying.IsValueType && lt is not NullableTypeSymbol)
        {
            // non-null value: ?. is a no-op
            return BindMemberOf(left, ma.Name, ma);
        }
        BoundConditionalReceiver placeholder = new(underlying) { Syntax = ma.Expression };
        BoundExpression access = BindMemberOf(placeholder, ma.Name, ma);
        return MakeConditionalAccess(left, placeholder, access, ma);
    }

    private BoundExpression BindBaseMemberAccess(MemberAccessExpressionSyntax ma)
    {
        NamedTypeSymbol? cur = CurrentType;
        if (cur?.BaseType == null || _isStaticContext)
        {
            Report(Descriptors.SuperNotAllowedHere, ma.Expression);
            return Bad(ma);
        }
        UsesThis = true;
        NamedTypeSymbol baseType = cur.BaseType;
        BoundBaseReference receiver = new(baseType) { Syntax = ma.Expression };
        BoundExpression r = BindMemberOf(receiver, ma.Name, ma);
        return r switch
        {
            BoundMethodGroup g => new BoundMethodGroup(g.Receiver, g.Name, g.Candidates, g.TypeArguments) { Syntax = g.Syntax, IsBaseAccess = true },
            BoundPropertyAccess p => new BoundPropertyAccess(p.Receiver, p.Property, p.Arguments) { Syntax = p.Syntax, IsBaseAccess = true },
            _ => r,
        };
    }

    internal BoundExpression BindMemberOf(BoundExpression left, SimpleNameSyntax nameSyntax, SyntaxNode access)
    {
        string name = nameSyntax.Name;
        int arity = nameSyntax is GenericNameSyntax gn ? gn.TypeArguments.Arguments.Count : 0;
        ImmutableArray<TypeSymbol> typeArgs = nameSyntax is GenericNameSyntax gn2 ? BindTypeArguments(gn2.TypeArguments) : [];
        TextSpan nameSpan = nameSyntax.Span;
        if (nameSyntax.Identifier.IsMissing)
        {
            return Bad(access, left);
        }

        switch (left)
        {
            case BoundNamespaceExpression ns:
                {
                    NamespaceSymbol? child = arity == 0 ? ns.Namespace.GetNamespaceMember(name) : null;
                    if (child != null) { Record(nameSyntax, child); return new BoundNamespaceExpression(child) { Syntax = access }; }
                    NamedTypeSymbol? t = ns.Namespace.GetTypeMember(name, arity);
                    if (t != null)
                    {
                        Record(nameSyntax, t);
                        return new BoundTypeExpression(arity == 0 ? NamedOrNullable(t) : t.Construct(typeArgs)) { Syntax = access };
                    }
                    Report(Descriptors.NamespaceHasNoMember, nameSpan, ns.Namespace.ToDisplayString(), name);
                    return Bad(access, left);
                }
            case BoundModuleExpression mod:
                return BindModuleMember(mod, name, arity, typeArgs, nameSyntax, access);
            case BoundTypeExpression te:
                return BindStaticMember(te.TypeSym, name, arity, typeArgs, nameSyntax, access);
        }

        TypeSymbol? lt = left.Type;
        if (lt == null)
        {
            ReportNotAValue(left, access);
            return Bad(access, left);
        }
        if (lt.IsErrorType)
        {
            return Bad(access, left);
        }

        if (lt is NullableTypeSymbol nullable)
        {
            ReportPossiblyNull(left, nameSyntax, access);
            lt = nullable.Underlying;
        }
        if (lt.IsNullType) { Report(Descriptors.NullDeref, access); return Bad(access, left); }
        if (lt.IsVoid) { Report(Descriptors.VoidDeref, access); return Bad(access, left); }

        ImmutableArray<Symbol> members = LookupMembers(lt, name);
        if (members.Length == 0)
        {
            // maybe an extension method
            List<MethodSymbol> ext = LookupExtensionMethods(name);
            if (ext.Count > 0)
            {
                return new BoundMethodGroup(left, name, [], typeArgs) { Syntax = access };
            }

            ReportMissingMember(lt, name, nameSpan, true);
            return Bad(access, left);
        }
        ImmutableArray<MethodSymbol> methods = [.. members.OfType<MethodSymbol>()];
        if (methods.Length > 0 && members.All(m => m is MethodSymbol))
        {
            Record(nameSyntax, methods[0]);
            return new BoundMethodGroup(left, name, methods, typeArgs) { Syntax = access };
        }
        Symbol first = members.First(m => m is not MethodSymbol);
        Record(nameSyntax, first);
        switch (first)
        {
            case FieldSymbol f:
                if (f.IsStatic)
                {
                    Report(Descriptors.StaticViaInstance, nameSpan, name, lt.ToDisplayString());
                    return new BoundFieldAccess(null, f) { Syntax = access };
                }
                return ApplyNarrowing(new BoundFieldAccess(left, f) { Syntax = access });
            case PropertySymbol p:
                if (p.IsIndexer)
                {
                    Report(Descriptors.IndexerNeedsArguments, nameSpan, name);
                    return Bad(access, left);
                }
                if (p.IsStatic)
                {
                    Report(Descriptors.StaticViaInstance, nameSpan, name, lt.ToDisplayString());
                    return new BoundPropertyAccess(null, p, []) { Syntax = access };
                }
                return ApplyNarrowing(new BoundPropertyAccess(left, p, []) { Syntax = access });
            case NamedTypeSymbol:
                Report(Descriptors.TypeViaInstance, nameSpan, name);
                return Bad(access, left);
        }
        return Bad(access, left);
    }

    private void ReportMissingMember(TypeSymbol type, string name, TextSpan span, bool _1)
    {
        string flipped = NameUtilities.FlipFirstCase(name);
        ImmutableArray<Symbol> alt = LookupMembers(type, flipped);
        if (alt.Length > 0 && flipped != name)
        {
            ReportWithFix(Descriptors.MemberNotFoundDidYouMean, span, new FixIt("Change to '" + flipped + "'", span, flipped), name, type.ToDisplayString(), flipped);
        }
        else
        {
            Report(Descriptors.MemberNotFound, span, name, type.ToDisplayString());
        }
    }

    private void ReportPossiblyNull(BoundExpression e, SyntaxNode? _1, SyntaxNode access)
    {
        TextSpan span = access.Span;
        if (access is MemberAccessExpressionSyntax ma)
        {
            SyntaxToken dot = ma.OperatorToken;
            ReportWithFix(Descriptors.PossiblyNull, ma.Expression.Span, new FixIt("Use '?.'", dot.Span, "?."), e.Syntax?.ToFullString().Trim() ?? "expression");
            return;
        }
        Report(Descriptors.PossiblyNull, span, "expression");
    }

    private BoundExpression BindModuleMember(BoundModuleExpression mod, string name, int arity, ImmutableArray<TypeSymbol> typeArgs, SimpleNameSyntax nameSyntax, SyntaxNode access)
    {
        List<Symbol> exports = [.. mod.File.Scope.GetExports(name)];
        ImmutableArray<MethodSymbol> methods = [.. exports.OfType<MethodSymbol>()];
        if (methods.Length > 0)
        {
            return new BoundMethodGroup(null, name, methods, typeArgs) { Syntax = access };
        }

        foreach (Symbol? e in exports)
        {
            switch (e)
            {
                case FieldSymbol f: return new BoundFieldAccess(null, f) { Syntax = access };
                case NamedTypeSymbol nt when nt.OwnArity == arity:
                    return new BoundTypeExpression(arity == 0 ? NamedOrNullable(nt) : nt.Construct(typeArgs)) { Syntax = access };
                case TypeAliasSymbol al when al.TypeParameters.Length == arity:
                    return new BoundTypeExpression(ExpandAlias(al, typeArgs)) { Syntax = access };
            }
        }
        Report(Descriptors.ModuleHasNoExport, nameSyntax.Span, mod.File.ModuleClassBaseName, name);
        return Bad(access);
    }

    private BoundExpression BindStaticMember(TypeSymbol type, string name, int arity, ImmutableArray<TypeSymbol> typeArgs, SimpleNameSyntax nameSyntax, SyntaxNode access)
    {
        if (type.IsErrorType)
        {
            return Bad(access);
        }

        ImmutableArray<Symbol> members = LookupMembers(type, name);
        if (members.Length == 0)
        {
            ReportMissingMember(type, name, nameSyntax.Span, false);
            return Bad(access);
        }
        ImmutableArray<MethodSymbol> methods = [.. members.OfType<MethodSymbol>()];
        if (methods.Length > 0 && members.All(m => m is MethodSymbol))
        {
            Record(nameSyntax, methods[0]);
            return new BoundMethodGroup(new BoundTypeExpression(type), name, methods, typeArgs) { Syntax = access };
        }
        Symbol first = members.First(m => m is not MethodSymbol);
        Record(nameSyntax, first);
        switch (first)
        {
            case FieldSymbol f:
                if (!f.IsStatic)
                {
                    Report(Descriptors.InstanceViaType, nameSyntax.Span, name, type.ToDisplayString());
                    return Bad(access);
                }
                return ApplyNarrowing(new BoundFieldAccess(null, f) { Syntax = access });
            case PropertySymbol p:
                if (!p.IsStatic)
                {
                    Report(Descriptors.InstanceViaType, nameSyntax.Span, name, type.ToDisplayString());
                    return Bad(access);
                }
                return ApplyNarrowing(new BoundPropertyAccess(null, p, []) { Syntax = access });
            case NamedTypeSymbol nt:
                {
                    NamedTypeSymbol? own = members.OfType<NamedTypeSymbol>().FirstOrDefault(x => x.OwnArity == arity);
                    if (own == null) { Report(Descriptors.TypeNotGeneric, nameSyntax.Span, name, arity); return Bad(access); }
                    TypeSymbol full = type is NamedTypeSymbol { IsGenericType: true } outer && own.Arity > own.OwnArity
                        ? own.Construct(outer.TypeArguments.AddRange(typeArgs))
                        : (arity == 0 ? (TypeSymbol)own : own.Construct(typeArgs));
                    return new BoundTypeExpression(full) { Syntax = access };
                }
        }
        return Bad(access);
    }

    // ------------------------------------------------------------------ element access

    private BoundExpression BindElementAccess(ElementAccessExpressionSyntax ea)
    {
        BoundExpression left = BindNaturalType(BindValue(ea.Expression), ea.Expression);
        if (left is BoundBadExpression)
        {
            return Bad(ea, left);
        }

        if (left is BoundConditionalAccess ca && ea.Expression is not ParenthesizedExpressionSyntax && !ea.IsConditional)
        {
            return LiftConditional(ca, inner => BindElementAccessOn(inner, ea), ea);
        }

        if (ea.IsConditional)
        {
            TypeSymbol? lt0 = left.Type;
            if (lt0 == null) { ReportNotAValue(left, ea); return Bad(ea, left); }
            TypeSymbol underlying = lt0.StripNullable();
            BoundConditionalReceiver placeholder = new(underlying) { Syntax = ea.Expression };
            BoundExpression access = BindElementAccessOn(placeholder, ea);
            return MakeConditionalAccess(left, placeholder, access, ea);
        }
        return BindElementAccessOn(left, ea);
    }

    private BoundExpression BindElementAccessOn(BoundExpression left, ElementAccessExpressionSyntax ea)
    {
        TypeSymbol? lt = left.Type;
        if (lt == null) { ReportNotAValue(left, ea); return Bad(ea, left); }
        if (lt.IsErrorType)
        {
            return Bad(ea, left);
        }

        if (lt is NullableTypeSymbol nullable)
        {
            ReportPossiblyNullElement(left, ea);
            lt = nullable.Underlying;
        }
        ImmutableArray<BoundExpression> args = [.. ea.Arguments.Select(a => BindValue(a))];
        if (args.Any(a => a is BoundBadExpression))
        {
            return Bad(ea, [left, .. args]);
        }

        if (lt is ArrayTypeSymbol arr)
        {
            if (arr.Rank != 1 || args.Length != 1)
            {
                Report(Descriptors.FeatureNotSupported, ea, "Multi-dimensional arrays");
                return Bad(ea, left);
            }
            BoundExpression idx = ConvertIndex(args[0], ea.Arguments[0]);
            return ApplyNarrowing(new BoundArrayAccess(left, idx, arr.ElementType) { Syntax = ea });
        }

        if (lt is NamedTypeSymbol { IsTuple: true } tuple && args.Length == 1)
        {
            if (args[0].ConstantValue is { Value: int ti } && ti >= 0 && ti < Math.Min(tuple.TypeArguments.Length, 7))
            {
                FieldSymbol? itemField = tuple.GetMembers("Item" + (ti + 1)).OfType<FieldSymbol>().FirstOrDefault();
                if (itemField != null)
                {
                    return new BoundFieldAccess(left, itemField) { Syntax = ea };
                }
            }
            Report(Descriptors.TupleIndexMustBeConstant, ea, tuple.ToDisplayString());
            return Bad(ea, left);
        }

        // indexers
        List<PropertySymbol> indexers = [];
        HashSet<TypeSymbol> seen = [];
        void Collect(TypeSymbol t)
        {
            if (!seen.Add(t))
            {
                return;
            }

            foreach (Symbol m in t.GetMembers())
            {
                if (m is PropertySymbol { IsIndexer: true, IsStatic: false } p && IsAccessible(p))
                {
                    indexers.Add(p);
                }
            }

            if (indexers.Count > 0 && t.TypeKind != TypeKind.Interface)
            {
                return;
            }

            if (t.TypeKind == TypeKind.Interface || t is TypeParameterSymbol)
            {
                foreach (NamedTypeSymbol i in t.AllInterfaces)
                {
                    Collect(i);
                }
            }

            if (t is TypeParameterSymbol tp)
            {
                foreach (TypeSymbol c in tp.ConstraintTypes)
                {
                    Collect(c);
                }
            }

            if (t.BaseType != null)
            {
                Collect(t.BaseType);
            }
        }
        Collect(lt);
        if (indexers.Count == 0)
        {
            Report(Descriptors.NotIndexable, ea, lt.ToDisplayString());
            return Bad(ea, left);
        }
        ImmutableArray<MethodSymbol> getters = [.. indexers.Select(p => p.GetMethod ?? p.SetMethod).Where(m => m != null).Select(m => m!)];
        // resolve against the indexer parameter lists via their accessor methods
        ResolvedCall? res = ResolveOverloads(getters, args, [], ea, "this[]", report: true, isIndexer: true, indexerParamCount: args.Length);
        if (res == null)
        {
            return Bad(ea, [left, .. args]);
        }

        PropertySymbol prop = res.Method.AssociatedSymbol as PropertySymbol ?? indexers.First();
        // the resolved method may be a substituted accessor; find the corresponding property on the constructed type
        if (res.Method.ContainingType != null)
        {
            PropertySymbol? match = res.Method.ContainingType.GetMembers().OfType<PropertySymbol>()
                .FirstOrDefault(p => ReferenceEquals(p.GetMethod?.OriginalDefinition, res.Method.OriginalDefinition) || ReferenceEquals(p.SetMethod?.OriginalDefinition, res.Method.OriginalDefinition));
            if (match != null)
            {
                prop = match;
            }
        }
        BoundPropertyAccess access = new(left, prop, res.Arguments) { Syntax = ea };
        return ApplyNarrowing(access);
    }

    private void ReportPossiblyNullElement(BoundExpression left, ElementAccessExpressionSyntax ea)
    {
        ReportWithFix(Descriptors.PossiblyNull, ea.Expression.Span, new FixIt("Use '?.'", new TextSpan(ea.Expression.Span.End, 0), "?."), left.Syntax?.ToFullString().Trim() ?? "expression");
    }

    private BoundExpression ConvertIndex(BoundExpression idx, SyntaxNode syntax)
    {
        if (idx.Type == null)
        {
            return ConvertImplicit(idx, IntType, syntax);
        }

        SpecialType st = idx.Type.SpecialType;
        return st is SpecialType.Int32 or SpecialType.UInt32 or SpecialType.Int64 or SpecialType.UInt64 or SpecialType.IntPtr or SpecialType.UIntPtr
            ? idx
            : ConvertImplicit(idx, IntType, syntax);
    }
}
