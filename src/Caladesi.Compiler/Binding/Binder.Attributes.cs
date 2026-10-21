using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

/// <summary>A decorator bound to an attribute constructor call: positional arguments plus optional named property/field values.</summary>
internal sealed class BoundAttribute(NamedTypeSymbol attributeType, MethodSymbol constructor, ImmutableArray<BoundExpression> arguments,
    ImmutableArray<(Symbol Member, BoundExpression Value)> named)
{
    public NamedTypeSymbol AttributeType { get; } = attributeType;
    public MethodSymbol Constructor { get; } = constructor;
    public ImmutableArray<BoundExpression> Arguments { get; } = arguments;
    public ImmutableArray<(Symbol Member, BoundExpression Value)> Named { get; } = named;
}

public sealed partial class Binder
{
    /// <summary>
    /// Binds <c>@Name</c>, <c>@Name(args)</c> or <c>@Name(args, { Prop: value })</c> to an attribute. The name may omit the
    /// <c>Attribute</c> suffix. A trailing object literal supplies named property and field values.
    /// </summary>
    internal BoundAttribute? BindDecorator(DecoratorSyntax d)
    {
        var expr = d.Expression;
        ExpressionSyntax callee = expr;
        var argSyntax = new List<ExpressionSyntax>();
        if (expr is InvocationExpressionSyntax inv)
        {
            callee = inv.Expression;
            argSyntax.AddRange(inv.Arguments.Arguments);
        }
        var attrType = ResolveAttributeType(callee);
        if (attrType == null)
        {
            Report(Descriptors.AttributeNotFound, callee, callee.ToFullString().Trim());
            return null;
        }

        ObjectLiteralExpressionSyntax? namedLiteral = null;
        if (argSyntax.Count > 0 && argSyntax[^1] is ObjectLiteralExpressionSyntax lit)
        {
            namedLiteral = lit;
            argSyntax.RemoveAt(argSyntax.Count - 1);
        }
        var args = ImmutableArray.CreateBuilder<BoundExpression>();
        foreach (var a in argSyntax)
        {
            var b = BindValue(a);
            if (b is BoundUnbound u && !u.IsLambda) b = u.BindToType(null);
            if (b is BoundBadExpression) return null;
            args.Add(b);
        }
        var ctors = attrType.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToImmutableArray();
        var res = ResolveOverloads(ctors, args.ToImmutable(), ImmutableArray<TypeSymbol>.Empty, d, attrType.Name, report: true);
        if (res == null) return null;
        foreach (var a in res.Arguments)
            if (!IsAttributeConstant(a))
            {
                Report(Descriptors.AttributeArgumentNotConstant, a.Syntax ?? d);
                return null;
            }

        var named = ImmutableArray.CreateBuilder<(Symbol, BoundExpression)>();
        if (namedLiteral != null)
        {
            foreach (var m in namedLiteral.Members)
            {
                if (m is not PropertyAssignmentSyntax pa) continue;
                var name = pa.NameToken.ValueText;
                Symbol? member = LookupMembers(attrType, name).FirstOrDefault(x => x is PropertySymbol { SetMethod: not null } or FieldSymbol { IsReadOnly: false, IsConst: false })
                    ?? LookupMembers(attrType, NameUtilities.FlipFirstCase(name)).FirstOrDefault(x => x is PropertySymbol { SetMethod: not null } or FieldSymbol { IsReadOnly: false, IsConst: false });
                if (member == null) { Report(Descriptors.AttributeMemberNotFound, pa.NameToken.Parent ?? pa, name, attrType.Name); continue; }
                var memberType = member is PropertySymbol p ? p.Type : ((FieldSymbol)member).Type;
                var v = ConvertImplicit(BindValue(pa.Value, memberType), memberType, pa.Value);
                if (v.HasErrors) continue;
                if (!IsAttributeConstant(v)) { Report(Descriptors.AttributeArgumentNotConstant, pa.Value); continue; }
                named.Add((member, v));
            }
        }
        return new BoundAttribute(attrType, res.Method, res.Arguments, named.ToImmutable());
    }

    private static bool IsAttributeConstant(BoundExpression e) => e switch
    {
        _ when e.ConstantValue != null => true,
        BoundTypeOf => true,
        BoundArrayCreation ac => ac.Initializers.All(IsAttributeConstant),
        BoundConversion { Kind: ConversionKind.Boxing or ConversionKind.ImplicitReference or ConversionKind.Identity } c => IsAttributeConstant(c.Operand),
        BoundLiteral => true,
        _ => false,
    };

    private NamedTypeSymbol? ResolveAttributeType(ExpressionSyntax callee)
    {
        var baseAttr = _c.GetWellKnownType("System.Attribute");
        bool IsAttribute(NamedTypeSymbol t)
        {
            for (var x = t; x != null; x = x.BaseType)
                if (baseAttr != null && ReferenceEquals(x.OriginalDefinition, baseAttr.OriginalDefinition)) return true;
            return false;
        }
        if (callee is IdentifierNameSyntax id)
        {
            foreach (var name in new[] { id.Identifier.Text, id.Identifier.Text + "Attribute" })
            {
                foreach (var c in _file.Scope.Lookup(name))
                    if (c is NamedTypeSymbol nt && nt.OwnArity == 0 && IsAttribute(nt)) return nt;
            }
            return null;
        }
        var bound = Speculatively(() => BindExpression(callee));
        if (bound is BoundTypeExpression { TypeSym: NamedTypeSymbol t } && IsAttribute(t)) return t;
        if (callee is MemberAccessExpressionSyntax ma)
        {
            var left = Speculatively(() => BindExpression(ma.Expression));
            var sym = ma.Name.Identifier.Text + "Attribute";
            switch (left)
            {
                case BoundNamespaceExpression ns:
                    return ns.Namespace.GetTypeMembersByName(sym).FirstOrDefault(IsAttribute);
                case BoundTypeExpression { TypeSym: NamedTypeSymbol outer }:
                    return outer.GetMembers(sym).OfType<NamedTypeSymbol>().FirstOrDefault(IsAttribute);
            }
        }
        return null;
    }
}
