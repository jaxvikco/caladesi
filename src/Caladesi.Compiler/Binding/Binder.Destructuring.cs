using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    private BoundStatement BindDestructuringDeclaration(VariableDeclaratorSyntax d, bool isConst)
    {
        if (d.Initializer == null)
        {
            Report(Descriptors.DestructuringNeedsInitializer, d);
            return new BoundNoOp { Syntax = d };
        }
        TypeSymbol? declared = d.TypeAnnotation != null ? BindType(d.TypeAnnotation.Type) : null;
        BoundExpression v = BindValue(d.Initializer.Value, declared);
        if (v is BoundBadExpression)
        {
            return new BoundNoOp { Syntax = d };
        }

        if (v is BoundUnbound u && !u.IsLambda)
        {
            v = u.BindToType(declared);
        }

        TypeSymbol t = declared ?? WidenInferredType(v, d.Initializer.Value, true);
        if (t.IsErrorType)
        {
            return new BoundNoOp { Syntax = d };
        }

        BoundExpression init = ConvertImplicit(v, t, d.Initializer.Value);
        LocalSymbol tmp = new("<d>", t, true, LocalOwner) { IsSynthesized = true };
        return new BoundStatementList(
        [
            new BoundLocalDeclaration(tmp, init) { Syntax = d },
            BindDestructuringFrom(d.Pattern!, new BoundLocal(tmp) { Syntax = d }, t, isConst, d),
        ])
        { Syntax = d };
    }

    /// <summary>Declares the variables of <paramref name="pattern"/> initialized from components of <paramref name="source"/>.</summary>
    internal BoundStatement BindDestructuringFrom(BindingPatternSyntax pattern, BoundExpression source, TypeSymbol sourceType, bool isConst, SyntaxNode at)
    {
        ImmutableArray<BoundStatement>.Builder stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        TypeSymbol st = sourceType.StripNullable();
        if (sourceType is NullableTypeSymbol)
        {
            Report(Descriptors.PossiblyNull, at, "destructured value");
            source = new BoundConversion(source, ConversionKind.FromNullable, st) { Syntax = at };
        }
        switch (pattern)
        {
            case ArrayBindingPatternSyntax ap:
                {
                    int index = 0;
                    foreach (BindingElementSyntax el in ap.Elements)
                    {
                        int i = index++;
                        if (el.IsOmitted)
                        {
                            continue;
                        }

                        if (el.DotDotDotToken != null)
                        {
                            BoundExpression? rest = BuildRest(source, st, i, at);
                            if (rest == null)
                            {
                                continue;
                            }

                            DeclareDestructured(el, rest, isConst, stmts, at);
                            continue;
                        }
                        BoundExpression? access = ElementAccessor(source, st, i, el, at);
                        if (access == null)
                        {
                            continue;
                        }

                        DeclareDestructured(el, access, isConst, stmts, at);
                    }
                    break;
                }
            case ObjectBindingPatternSyntax op:
                {
                    foreach (BindingElementSyntax el in op.Elements)
                    {
                        if (el.DotDotDotToken != null) { Report(Descriptors.FeatureNotSupported, el, "Object rest in destructuring"); continue; }
                        string? propName = el.PropertyName?.Text ?? el.Identifier?.Text;
                        if (propName == null)
                        {
                            continue;
                        }

                        BoundExpression? access = MemberAccessor(source, st, propName, el, at);
                        if (access == null)
                        {
                            continue;
                        }

                        DeclareDestructured(el, access, isConst, stmts, at);
                    }
                    break;
                }
        }
        return new BoundStatementList(stmts.ToImmutable()) { Syntax = at };
    }

    private void DeclareDestructured(BindingElementSyntax el, BoundExpression value, bool isConst, ImmutableArray<BoundStatement>.Builder stmts, SyntaxNode _1)
    {
        if (el.Initializer != null)
        {
            if (value.Type is NullableTypeSymbol nt)
            {
                BoundExpression def = ConvertImplicit(BindValue(el.Initializer.Value, nt.Underlying), nt.Underlying, el.Initializer.Value);
                value = new BoundCoalesce(value, def, nt.Underlying) { Syntax = el };
            }
            else
            {
                Report(Descriptors.DestructuringDefaultNeedsNullable, el.Initializer.Value);
            }
        }
        if (el.NestedPattern != null)
        {
            LocalSymbol tmp = new("<n>", value.Type!, true, LocalOwner) { IsSynthesized = true };
            stmts.Add(new BoundLocalDeclaration(tmp, value) { Syntax = el });
            stmts.Add(BindDestructuringFrom(el.NestedPattern, new BoundLocal(tmp), value.Type!, isConst, el));
            return;
        }
        if (el.Identifier is not { IsMissing: false } id)
        {
            return;
        }

        LocalSymbol local = new(id.Text, value.Type!, isConst, LocalOwner, el);
        DeclareSymbol(id.Text, local, id);
        if (isConst && value.ConstantValue is { } cv)
        {
            local.ConstantValue = cv;
        }

        stmts.Add(new BoundLocalDeclaration(local, value) { Syntax = el });
    }

    private BoundExpression? ElementAccessor(BoundExpression source, TypeSymbol st, int index, SyntaxNode at, SyntaxNode report)
    {
        BoundLiteral idx = new(new ConstantValue(index), IntType) { Syntax = at };
        switch (st)
        {
            case ArrayTypeSymbol a:
                return new BoundArrayAccess(source, idx, a.ElementType) { Syntax = at };
            case NamedTypeSymbol { IsTuple: true } tup:
                if (index >= tup.TypeArguments.Length)
                {
                    Report(Descriptors.TupleIndexOutOfRange, at, index, tup.ToDisplayString());
                    return null;
                }
                return new BoundFieldAccess(source, tup.GetMembers("Item" + (index + 1)).OfType<FieldSymbol>().First()) { Syntax = at };
        }
        PropertySymbol? indexer = st.GetMembers().OfType<PropertySymbol>().FirstOrDefault(p => p.IsIndexer && p.Parameters.Length == 1 && p.Parameters[0].Type.SpecialType == SpecialType.Int32 && p.GetMethod != null)
                      ?? st.AllInterfaces.SelectMany(i => i.GetMembers().OfType<PropertySymbol>()).FirstOrDefault(p => p.IsIndexer && p.Parameters.Length == 1 && p.Parameters[0].Type.SpecialType == SpecialType.Int32 && p.GetMethod != null);
        if (indexer != null)
        {
            return new BoundPropertyAccess(source, indexer, [idx]) { Syntax = at };
        }

        Report(Descriptors.CannotDestructure, report, st.ToDisplayString());
        return null;
    }

    private BoundExpression? MemberAccessor(BoundExpression source, TypeSymbol st, string name, SyntaxNode at, SyntaxNode _1)
    {
        ImmutableArray<Symbol> members = LookupMembers(st, name);
        if (members.Length == 0)
        {
            members = LookupMembers(st, NameUtilities.FlipFirstCase(name));
        }

        foreach (Symbol m in members)
        {
            if (m is PropertySymbol { IsStatic: false, IsIndexer: false } p && p.GetMethod != null)
            {
                return new BoundPropertyAccess(source, p, []) { Syntax = at };
            }

            if (m is FieldSymbol { IsStatic: false } f)
            {
                return new BoundFieldAccess(source, f) { Syntax = at };
            }
        }
        Report(Descriptors.MemberNotFound, at, name, st.ToDisplayString());
        return null;
    }

    private BoundCall? BuildRest(BoundExpression source, TypeSymbol st, int from, SyntaxNode at)
    {
        TypeSymbol? elem = st is ArrayTypeSymbol a ? a.ElementType : GetIterationType(st);
        if (elem == null) { Report(Descriptors.CannotDestructure, at, st.ToDisplayString()); return null; }
        NamedTypeSymbol? en = Compilation.GetWellKnownType("System.Linq.Enumerable");
        if (en == null) { Report(Descriptors.RuntimeMissing, at, "System.Linq.Enumerable"); return null; }
        NamedTypeSymbol enumerable = Compilation.GetWellKnownType("System.Collections.Generic.IEnumerable`1")!.Construct(elem);
        MethodSymbol skip = en.GetMembers("Skip").OfType<MethodSymbol>().First(m => m.Arity == 1 && m.Parameters.Length == 2).Construct([elem]);
        MethodSymbol toArray = en.GetMembers("ToArray").OfType<MethodSymbol>().First(m => m.Arity == 1 && m.Parameters.Length == 1).Construct([elem]);
        BoundExpression src = ConvertImplicit(source, enumerable, at, report: false);
        BoundCall skipped = new(null, skip, [src, new BoundLiteral(new ConstantValue(from), IntType)]) { Syntax = at };
        return new BoundCall(null, toArray, [skipped]) { Syntax = at };
    }

    // ------------------------------------------------------------------ destructuring assignment

    private BoundExpression BindDestructuringAssignment(AssignmentExpressionSyntax a, ExpressionSyntax leftSyntax)
    {
        BoundExpression rhs = BindValue(a.Right);
        if (rhs is BoundBadExpression)
        {
            return Bad(a, rhs);
        }

        if (rhs is BoundUnbound u && HasNaturalType(u))
        {
            rhs = u.BindToType(null);
        }

        if (rhs.Type == null || rhs.HasErrors)
        {
            return Bad(a, rhs);
        }

        TypeSymbol t = rhs.Type.StripNullable();
        LocalSymbol tmp = new("<da>", rhs.Type, true, LocalOwner) { IsSynthesized = true };
        ImmutableArray<BoundStatement>.Builder stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        stmts.Add(new BoundLocalDeclaration(tmp, rhs) { Syntax = a });
        BoundExpression src = new BoundLocal(tmp);
        if (rhs.Type is NullableTypeSymbol) { Report(Descriptors.PossiblyNull, a.Right, "destructured value"); src = new BoundConversion(src, ConversionKind.FromNullable, t); }

        void AssignTo(ExpressionSyntax targetSyntax, BoundExpression value)
        {
            BoundExpression target = BindAssignmentTarget(targetSyntax);
            if (target is BoundBadExpression)
            {
                return;
            }

            BoundExpression cv = ConvertImplicit(value, target.Type!, targetSyntax);
            stmts.Add(new BoundExpressionStatement(new BoundAssignment(target, cv) { Syntax = a, IsDiscarded = true }) { Syntax = a });
            InvalidateNarrowing(target);
        }

        if (leftSyntax is ArrayLiteralExpressionSyntax arr)
        {
            int idx = 0;
            foreach (ExpressionSyntax el in arr.Elements)
            {
                int i = idx++;
                if (el is MissingExpressionSyntax)
                {
                    continue;
                }

                if (el is SpreadElementSyntax sp)
                {
                    BoundExpression? rest = BuildRest(src, t, i, el);
                    if (rest != null)
                    {
                        AssignTo(sp.Expression, rest);
                    }

                    continue;
                }
                BoundExpression? access = ElementAccessor(src, t, i, el, el);
                if (access != null)
                {
                    AssignTo(el, access);
                }
            }
        }
        else if (leftSyntax is ObjectLiteralExpressionSyntax obj)
        {
            foreach (SyntaxNode m in obj.Members)
            {
                switch (m)
                {
                    case ShorthandPropertyAssignmentSyntax sh:
                        {
                            BoundExpression? access = MemberAccessor(src, t, sh.NameToken.Text, sh, sh);
                            if (access == null)
                            {
                                break;
                            }

                            Symbol? local = LookupLocal(sh.NameToken.Text);
                            BoundExpression? target = local != null ? SymbolToExpressionNoNarrow(local, sh) : null;
                            if (target == null)
                            {
                                BoundExpression r = BindModuleLevelName(sh.NameToken.Text, 0, [], sh, sh.NameToken.Span);
                                target = r is BoundFieldAccess ? CheckAssignable(r, sh, false) : null;
                            }
                            if (target is null or BoundBadExpression)
                            {
                                break;
                            }

                            target = CheckAssignable(target, sh, false);
                            if (target is BoundBadExpression)
                            {
                                break;
                            }

                            stmts.Add(new BoundExpressionStatement(new BoundAssignment(target, ConvertImplicit(access, target.Type!, sh)) { Syntax = a, IsDiscarded = true }) { Syntax = a });
                            InvalidateNarrowing(target);
                            break;
                        }
                    case PropertyAssignmentSyntax pa:
                        {
                            BoundExpression? access = MemberAccessor(src, t, pa.NameToken.ValueText, pa, pa);
                            if (access != null)
                            {
                                AssignTo(pa.Value, access);
                            }

                            break;
                        }
                    default:
                        Report(Descriptors.FeatureNotSupported, m, "Rest in destructuring assignment");
                        break;
                }
            }
        }
        return new BoundSequence([tmp], stmts.ToImmutable(), new BoundLocal(tmp)) { Syntax = a };
    }

    private static BoundExpression SymbolToExpressionNoNarrow(Symbol s, SyntaxNode syntax)
    {
        return s switch
        {
            LocalSymbol l => new BoundLocal(l) { Syntax = syntax },
            ParameterSymbol p => new BoundParameter(p) { Syntax = syntax },
            _ => Bad(syntax),
        };
    }
}
