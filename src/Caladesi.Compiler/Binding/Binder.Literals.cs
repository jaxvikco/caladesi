using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    // ======================================================================= iteration helpers

    /// <summary>The element type produced by iterating a value of type <paramref name="t"/> (foreach / spread).</summary>
    internal TypeSymbol? GetIterationType(TypeSymbol t, bool isAsync = false)
    {
        t = t.StripNullable();
        if (t is ArrayTypeSymbol a)
        {
            return a.ElementType;
        }

        if (t.SpecialType == SpecialType.String)
        {
            return Special(SpecialType.Char);
        }

        string ifaceName = isAsync ? "System.Collections.Generic.IAsyncEnumerable`1" : "System.Collections.Generic.IEnumerable`1";
        NamedTypeSymbol? found = null;
        List<NamedTypeSymbol> candidates = [];
        if (t is NamedTypeSymbol self)
        {
            candidates.Add(self);
        }

        candidates.AddRange(t.AllInterfaces);
        foreach (NamedTypeSymbol i in candidates)
        {
            if (i.OriginalDefinition.ToMetadataString() == ifaceName)
            {
                if (found == null)
                {
                    found = i;
                }
                else if (!found.Equals(i))
                {
                    return null;
                }
            }
        }

        if (found != null)
        {
            return found.TypeArguments[0];
        }
        // GetEnumerator pattern
        MethodSymbol? ge = LookupMembers(t, isAsync ? "GetAsyncEnumerator" : "GetEnumerator").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == (isAsync ? 0 : 0) || m.Parameters.All(p => p.IsOptional));
        if (ge != null)
        {
            PropertySymbol? cur = LookupMembers(ge.ReturnType, "Current").OfType<PropertySymbol>().FirstOrDefault();
            if (cur != null)
            {
                return cur.Type;
            }
        }
        return !isAsync && ImplementsInterface(t, Special(SpecialType.IEnumerable)) ? ObjectType : null;
    }

    // ======================================================================= collection expressions

    private BoundUnbound BindCollectionExpression(ArrayLiteralExpressionSyntax arr, TypeSymbol? target)
    {
        ImmutableArray<ExpressionSyntax> elems = [.. arr.Elements];
        bool hasSpread = elems.Any(e => e is SpreadElementSyntax);
        Scope capturedScope = _scope;
        ImmutableDictionary<NarrowPath, TypeSymbol> capturedNarrow = _narrow;
        TypeSymbol? naturalElement = null;
        if (target == null)
        {
            naturalElement = Speculatively(() =>
            {
                List<BoundExpression> types = [];
                foreach (ExpressionSyntax? e in elems)
                {
                    if (e is SpreadElementSyntax sp)
                    {
                        BoundExpression s = BindValue(sp.Expression);
                        TypeSymbol? it = s.Type != null ? GetIterationType(s.Type) : null;
                        if (it == null)
                        {
                            return null;
                        }

                        types.Add(new BoundDefaultValue(it));
                    }
                    else
                    {
                        BoundExpression b = BindValue(e);
                        if (b is BoundUnbound { Type: null } || b.Type == null)
                        {
                            return null;
                        }

                        types.Add(b);
                    }
                }
                return types.Count == 0 ? null : BestCommonType(types, arr);
            });
        }
        CollectionInfo info = new() { ElementCount = elems.Length, HasSpread = hasSpread, NaturalElementType = naturalElement };
        ArrayTypeSymbol? natural = naturalElement != null && !naturalElement.IsErrorType ? Compilation.CreateArrayType(naturalElement) : null;
        return new BoundUnbound(arr, t => WithCapturedState(capturedScope, capturedNarrow, () => BindCollectionToType(arr, elems, t)), natural)
        { IsCollection = true, Collection = info };
    }

    private enum CollectionKind { Array, Span, ReadOnlySpan, Builder, ReadOnlyInterface, ListInterface, Add }

    private BoundExpression BindCollectionToType(ArrayLiteralExpressionSyntax syntax, ImmutableArray<ExpressionSyntax> elems, TypeSymbol? target)
    {
        TypeSymbol? elementType = null;
        CollectionKind kind;
        MethodSymbol? builder = null;
        NamedTypeSymbol? namedTarget = null;
        bool wrapNullable = target is NullableTypeSymbol;
        TypeSymbol? tt = target?.StripNullable();

        if (tt == null)
        {
            // natural type: best common type array
            List<BoundExpression> parts = [];
            foreach (ExpressionSyntax e in elems)
            {
                if (e is SpreadElementSyntax sp)
                {
                    BoundExpression s = BindValue(sp.Expression);
                    TypeSymbol? it = s.Type != null ? GetIterationType(s.Type) : null;
                    if (it == null) { Report(Descriptors.NotIterable, sp.Expression, s.Type?.ToDisplayString() ?? "?"); return Bad(syntax); }
                    parts.Add(new BoundDefaultValue(it));
                }
                else
                {
                    parts.Add(BindValue(e));
                }
            }
            if (parts.Count == 0)
            {
                Report(Descriptors.CannotInferEmptyCollection, syntax);
                return Bad(syntax);
            }
            TypeSymbol? common = BestCommonType(parts, syntax);
            if (common == null)
            {
                Report(Descriptors.NoBestCommonTypeCollection, syntax);
                return Bad(syntax);
            }
            tt = Compilation.CreateArrayType(common);
        }

        if (tt.IsErrorType) { foreach (ExpressionSyntax e in elems) { if (e is not SpreadElementSyntax) { _ = BindValue(e); } } return Bad(syntax); }

        if (tt is NamedTypeSymbol { IsTuple: true } tuple && target != null)
        {
            ImmutableArray<TypeSymbol> targs = tuple.TypeArguments;
            if (elems.Any(e => e is SpreadElementSyntax) || targs.Length != elems.Length || targs.Length > 7)
            {
                Report(Descriptors.CollectionNotConvertible, syntax, target.ToDisplayString());
                foreach (ExpressionSyntax e in elems)
                {
                    if (e is not SpreadElementSyntax)
                    {
                        _ = BindValue(e);
                    }
                }

                return Bad(syntax);
            }
            ImmutableArray<BoundExpression>.Builder items = ImmutableArray.CreateBuilder<BoundExpression>(targs.Length);
            for (int i = 0; i < targs.Length; i++)
            {
                BoundExpression item = BindToType(elems[i], targs[i]);
                if (item.HasErrors)
                {
                    return Bad(syntax);
                }

                items.Add(item);
            }
            MethodSymbol tctor = tuple.InstanceConstructors.First(c => c.Parameters.Length == targs.Length);
            BoundExpression created = new BoundObjectCreation(tctor, tuple, items.ToImmutable(), []) { Syntax = syntax };
            if (wrapNullable)
            {
                created = new BoundConversion(created, ConversionKind.ToNullable, target) { Syntax = syntax };
            }

            return created;
        }

        if (tt is ArrayTypeSymbol at)
        {
            kind = CollectionKind.Array;
            elementType = at.ElementType;
        }
        else if (tt is NamedTypeSymbol nt)
        {
            namedTarget = nt;
            string def = nt.OriginalDefinition.ToMetadataString();
            if (def == "System.Span`1") { kind = CollectionKind.Span; elementType = nt.TypeArguments[0]; }
            else if (def == "System.ReadOnlySpan`1") { kind = CollectionKind.ReadOnlySpan; elementType = nt.TypeArguments[0]; }
            else if (FindCollectionBuilder(nt, out builder, out TypeSymbol? be)) { kind = CollectionKind.Builder; elementType = be; }
            else if (def is "System.Collections.Generic.IEnumerable`1" or "System.Collections.Generic.IReadOnlyCollection`1" or "System.Collections.Generic.IReadOnlyList`1")
            { kind = CollectionKind.ReadOnlyInterface; elementType = nt.TypeArguments[0]; }
            else if (def is "System.Collections.Generic.ICollection`1" or "System.Collections.Generic.IList`1")
            { kind = CollectionKind.ListInterface; elementType = nt.TypeArguments[0]; }
            else if (nt.TypeKind is TypeKind.Class or TypeKind.Struct && !nt.IsAbstract && GetAddElementType(nt) is { } ae && HasDefaultConstructor(nt))
            { kind = CollectionKind.Add; elementType = ae; }
            else
            {
                Report(Descriptors.CollectionNotConvertible, syntax, target!.ToDisplayString());
                foreach (ExpressionSyntax e in elems)
                {
                    if (e is not SpreadElementSyntax)
                    {
                        _ = BindValue(e);
                    }
                }

                return Bad(syntax);
            }
        }
        else
        {
            Report(Descriptors.CollectionNotConvertible, syntax, target!.ToDisplayString());
            return Bad(syntax);
        }

        TypeSymbol elementTypeFinal = elementType!;
        bool hasSpread = elems.Any(e => e is SpreadElementSyntax);
        // bind elements
        List<(BoundExpression Expr, bool Spread)> bound = [];
        foreach (ExpressionSyntax e in elems)
        {
            if (e is SpreadElementSyntax sp)
            {
                BoundExpression s = BindValue(sp.Expression);
                if (s is BoundBadExpression)
                {
                    return Bad(syntax);
                }

                TypeSymbol? it = s.Type != null ? GetIterationType(s.Type) : null;
                if (it == null) { Report(Descriptors.NotIterable, sp.Expression, s.Type?.ToDisplayString() ?? "?"); return Bad(syntax); }
                if (!ClassifyImplicit(null, it, elementTypeFinal).Exists)
                {
                    Report(Descriptors.NotAssignable, sp.Expression, it.ToDisplayString(), elementTypeFinal.ToDisplayString());
                    return Bad(syntax);
                }
                bound.Add((s, true));
            }
            else
            {
                BoundExpression v = BindValue(e, elementTypeFinal);
                if (v is BoundBadExpression)
                {
                    return Bad(syntax);
                }

                BoundExpression c = ConvertImplicit(v, elementTypeFinal, e);
                if (c.HasErrors)
                {
                    return Bad(syntax);
                }

                bound.Add((c, false));
            }
        }

        BoundExpression result;
        if (kind == CollectionKind.Add)
        {
            result = BuildAddCollection(syntax, namedTarget!, elementTypeFinal, bound);
        }
        else
        {
            BoundExpression array = BuildArrayFromElements(syntax, elementTypeFinal, bound);
            ArrayTypeSymbol arrType = (ArrayTypeSymbol)array.Type!;
            switch (kind)
            {
                case CollectionKind.Array:
                    result = array;
                    break;
                case CollectionKind.Span or CollectionKind.ReadOnlySpan:
                    {
                        MethodSymbol? conv = namedTarget!.GetMembers("op_Implicit").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1 && m.Parameters[0].Type.StripNullable().Equals(arrType));
                        if (conv == null) { Report(Descriptors.CollectionNotConvertible, syntax, target!.ToDisplayString()); return Bad(syntax); }
                        result = new BoundConversion(array, ConversionKind.UserDefined, namedTarget, conv) { Syntax = syntax };
                        break;
                    }
                case CollectionKind.Builder:
                    {
                        TypeSymbol spanT = builder!.Parameters[0].Type;
                        MethodSymbol? rosConv = ((NamedTypeSymbol)spanT).GetMembers("op_Implicit").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1 && m.Parameters[0].Type.StripNullable().Equals(arrType));
                        if (rosConv == null) { Report(Descriptors.CollectionNotConvertible, syntax, target!.ToDisplayString()); return Bad(syntax); }
                        BoundConversion spanArg = new(array, ConversionKind.UserDefined, spanT, rosConv) { Syntax = syntax };
                        result = new BoundCall(null, builder, [spanArg]) { Syntax = syntax };
                        break;
                    }
                case CollectionKind.ReadOnlyInterface:
                    result = new BoundConversion(array, ConversionKind.ImplicitReference, namedTarget!) { Syntax = syntax };
                    break;
                default: // ListInterface
                    {
                        NamedTypeSymbol? listDef = Compilation.GetWellKnownType("System.Collections.Generic.List`1");
                        NamedTypeSymbol list = listDef!.Construct(elementTypeFinal);
                        NamedTypeSymbol enumT = Compilation.GetWellKnownType("System.Collections.Generic.IEnumerable`1")!.Construct(elementTypeFinal);
                        MethodSymbol ctor = list.InstanceConstructors.First(c => c.Parameters.Length == 1 && c.Parameters[0].Type.Equals(enumT));
                        BoundObjectCreation create = new(ctor, list, [new BoundConversion(array, ConversionKind.ImplicitReference, enumT) { Syntax = syntax }], []) { Syntax = syntax };
                        result = new BoundConversion(create, ConversionKind.ImplicitReference, namedTarget!) { Syntax = syntax };
                        break;
                    }
            }
        }
        if (wrapNullable && target != null)
        {
            result = new BoundConversion(result, ConversionKind.ToNullable, target) { Syntax = syntax };
        }

        return result;
    }

    private bool HasDefaultConstructor(NamedTypeSymbol t)
    {
        return t.TypeKind == TypeKind.Struct || t.InstanceConstructors.Any(c => c.Parameters.All(p => p.IsOptional) && IsAccessible(c));
    }

    private TypeSymbol? GetAddElementType(NamedTypeSymbol t)
    {
        if (!ImplementsInterface(t, Special(SpecialType.IEnumerable)))
        {
            return null;
        }

        List<MethodSymbol> adds = [.. LookupMembers(t, "Add").OfType<MethodSymbol>().Where(m => m.Parameters.Length == 1 && !m.IsStatic)];
        if (adds.Count == 0)
        {
            return null;
        }
        // prefer the iteration element type when an Add overload accepts it
        TypeSymbol? it = GetIterationType(t);
        return it != null && adds.Any(a => a.Parameters[0].Type.Equals(it)) ? it : adds[0].Parameters[0].Type;
    }

    private static bool FindCollectionBuilder(NamedTypeSymbol target, out MethodSymbol? method, out TypeSymbol? elementType)
    {
        method = null; elementType = null;
        AttributeData? attr = target.OriginalDefinition.GetAttribute("System.Runtime.CompilerServices.CollectionBuilderAttribute");
        if (attr == null || attr.ConstructorArguments.Length < 2)
        {
            return false;
        }

        if (attr.ConstructorArguments[0] is not NamedTypeSymbol builderType || attr.ConstructorArguments[1] is not string name)
        {
            return false;
        }

        foreach (MethodSymbol m in builderType.GetMembers(name).OfType<MethodSymbol>())
        {
            if (!m.IsStatic || m.Parameters.Length != 1)
            {
                continue;
            }

            MethodSymbol cm = m;
            if (m.IsGenericMethod)
            {
                if (m.Arity != target.Arity)
                {
                    continue;
                }

                cm = m.Construct(target.TypeArguments);
            }
            if (cm.Parameters[0].Type is NamedTypeSymbol { IsGenericType: true } p && p.OriginalDefinition.ToMetadataString() == "System.ReadOnlySpan`1")
            {
                method = cm;
                elementType = p.TypeArguments[0];
                return true;
            }
        }
        return false;
    }

    private BoundExpression BuildArrayFromElements(SyntaxNode syntax, TypeSymbol elementType, List<(BoundExpression Expr, bool Spread)> elems)
    {
        ArrayTypeSymbol arrType = Compilation.CreateArrayType(elementType);
        if (elems.All(e => !e.Spread))
        {
            return new BoundArrayCreation(arrType, new BoundLiteral(new ConstantValue(elems.Count), IntType), [.. elems.Select(e => e.Expr)]) { Syntax = syntax };
        }
        // with spreads: accumulate into a List<T> and convert
        NamedTypeSymbol listDef = Compilation.GetWellKnownType("System.Collections.Generic.List`1")!;
        NamedTypeSymbol list = listDef.Construct(elementType);
        LocalSymbol tmp = new("<list>", list, false, LocalOwner) { IsSynthesized = true };
        MethodSymbol ctor = list.InstanceConstructors.First(c => c.Parameters.Length == 0);
        ImmutableArray<BoundStatement>.Builder stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        stmts.Add(new BoundLocalDeclaration(tmp, new BoundObjectCreation(ctor, list, [], []) { Syntax = syntax }) { Syntax = syntax });
        AddElementsTo(stmts, tmp, list, elementType, elems, syntax);
        MethodSymbol toArray = list.GetMembers("ToArray").OfType<MethodSymbol>().First(m => m.Parameters.Length == 0);
        BoundCall value = new(new BoundLocal(tmp), toArray, []) { Syntax = syntax };
        return new BoundSequence([tmp], stmts.ToImmutable(), value) { Syntax = syntax };
    }

    private void AddElementsTo(ImmutableArray<BoundStatement>.Builder stmts, LocalSymbol tmp, NamedTypeSymbol collectionType, TypeSymbol elementType,
        List<(BoundExpression Expr, bool Spread)> elems, SyntaxNode syntax)
    {
        NamedTypeSymbol enumT = Compilation.GetWellKnownType("System.Collections.Generic.IEnumerable`1")!.Construct(elementType);
        foreach ((BoundExpression? expr, bool spread) in elems)
        {
            if (!spread)
            {
                MethodSymbol? add = FindAddMethod(collectionType, elementType, expr);
                if (add == null)
                {
                    continue;
                }

                BoundExpression arg = ConvertImplicit(expr, add.Parameters[0].Type, syntax);
                stmts.Add(new BoundExpressionStatement(new BoundCall(new BoundLocal(tmp), add, [arg]) { Syntax = syntax }) { Syntax = syntax });
                continue;
            }
            MethodSymbol? addRange = collectionType.GetMembers("AddRange").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1 && !m.IsStatic && ClassifyImplicit(expr, expr.Type!, m.Parameters[0].Type).Exists);
            if (addRange != null)
            {
                stmts.Add(new BoundExpressionStatement(new BoundCall(new BoundLocal(tmp), addRange, [ConvertImplicit(expr, addRange.Parameters[0].Type, syntax)]) { Syntax = syntax }) { Syntax = syntax });
                continue;
            }
            MethodSymbol? addM = FindAddMethod(collectionType, elementType, null);
            if (addM == null)
            {
                continue;
            }

            TypeSymbol it = GetIterationType(expr.Type!)!;
            stmts.Add(BuildForEachLoop(expr, it, syntax, current =>
            {
                BoundExpression arg = ConvertImplicit(new BoundLocal(current), addM.Parameters[0].Type, syntax);
                return new BoundExpressionStatement(new BoundCall(new BoundLocal(tmp), addM, [arg]) { Syntax = syntax }) { Syntax = syntax };
            }));
        }
    }

    private MethodSymbol? FindAddMethod(NamedTypeSymbol collectionType, TypeSymbol elementType, BoundExpression? sample)
    {
        List<MethodSymbol> adds = [.. LookupMembers(collectionType, "Add").OfType<MethodSymbol>().Where(m => m.Parameters.Length == 1 && !m.IsStatic)];
        if (sample != null)
        {
            ResolvedCall? r = ResolveOverloads(adds, [sample], [], sample.Syntax!, "Add", report: false);
            if (r != null)
            {
                return r.Method;
            }
        }
        return adds.FirstOrDefault(a => a.Parameters[0].Type.Equals(elementType)) ?? adds.FirstOrDefault();
    }

    private BoundSequence BuildAddCollection(SyntaxNode syntax, NamedTypeSymbol type, TypeSymbol elementType, List<(BoundExpression Expr, bool Spread)> elems)
    {
        LocalSymbol tmp = new("<coll>", type, false, LocalOwner) { IsSynthesized = true };
        MethodSymbol? ctor = type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        BoundObjectCreation create = new(ctor, type, [], []) { Syntax = syntax };
        ImmutableArray<BoundStatement>.Builder stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        stmts.Add(new BoundLocalDeclaration(tmp, create) { Syntax = syntax });
        AddElementsTo(stmts, tmp, type, elementType, elems, syntax);
        return new BoundSequence([tmp], stmts.ToImmutable(), new BoundLocal(tmp)) { Syntax = syntax };
    }

    // ======================================================================= object literals

    private sealed record LiteralMember(string Name, SyntaxNode NameNode, ExpressionSyntax? Value, bool IsSpread);

    private BoundUnbound BindObjectLiteral(ObjectLiteralExpressionSyntax obj, TypeSymbol? _1)
    {
        List<LiteralMember> members = [];
        foreach (SyntaxNode m in obj.Members)
        {
            switch (m)
            {
                case PropertyAssignmentSyntax pa:
                    members.Add(new LiteralMember(pa.NameToken.ValueText, pa.NameToken.Parent ?? pa, pa.Value, false));
                    break;
                case ShorthandPropertyAssignmentSyntax sh:
                    members.Add(new LiteralMember(sh.NameToken.Text, sh, null, false));
                    break;
                case SpreadAssignmentSyntax sp:
                    members.Add(new LiteralMember("...", sp, sp.Expression, true));
                    break;
            }
        }
        Scope capturedScope = _scope;
        ImmutableDictionary<NarrowPath, TypeSymbol> capturedNarrow = _narrow;
        ObjectLiteralInfo info = new() { PropertyNames = [.. members.Where(m => !m.IsSpread).Select(m => m.Name)], HasSpread = members.Any(m => m.IsSpread) };
        return new BoundUnbound(obj, t => WithCapturedState(capturedScope, capturedNarrow, () => BindObjectLiteralToType(obj, members, t)))
        { IsObjectLiteral = true, ObjectLiteral = info };
    }

    private BoundExpression BindLiteralMemberValue(LiteralMember m, TypeSymbol? target)
    {
        if (m.Value != null)
        {
            return BindValue(m.Value, target);
        }
        // shorthand: the member name is an identifier expression
        SyntaxToken token = ((ShorthandPropertyAssignmentSyntax)m.NameNode).NameToken;
        IdentifierNameSyntax ident = new(new SyntaxToken(SyntaxKind.IdentifierToken, token.Text, token.FullStart, token.LeadingTrivia, token.TrailingTrivia));
        // keep diagnostics positioned at the original token
        Symbol? local = LookupLocal(token.Text);
        if (local != null)
        {
            return SymbolToExpression(local, m.NameNode);
        }

        BoundExpression r = BindModuleLevelName(token.Text, 0, [], m.NameNode, token.Span);
        _ = ident;
        return r;
    }

    private BoundExpression BindObjectLiteralToType(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members, TypeSymbol? target)
    {
        if (target == null)
        {
            return BindAnonymousObject(syntax, members);
        }

        bool wrapNullable = target is NullableTypeSymbol;
        TypeSymbol tt = target.StripNullable();
        if (tt.IsErrorType)
        {
            foreach (LiteralMember m in members)
            {
                if (!m.IsSpread)
                {
                    _ = BindLiteralMemberValue(m, null);
                }
            }

            return Bad(syntax);
        }

        // duplicate keys
        HashSet<string> seen = [];
        bool hasDuplicate = false;
        foreach (LiteralMember m in members)
        {
            if (!m.IsSpread && !seen.Add(m.Name))
            {
                Report(Descriptors.DuplicateObjectLiteralKey, m.NameNode, m.Name);
                hasDuplicate = true;
            }
        }

        if (hasDuplicate)
        {
            foreach (LiteralMember m in members)
            {
                if (!m.IsSpread)
                {
                    _ = BindLiteralMemberValue(m, null);
                }
            }

            return Bad(syntax);
        }

        BoundExpression result;
        if (members.Any(m => m.IsSpread))
        {
            result = BindSpreadObject(syntax, members, tt);
        }
        else if (tt is NamedTypeSymbol dict && IsDictionaryLike(dict, out TypeSymbol? keyT, out TypeSymbol? valT, out PropertySymbol? setIndexer))
        {
            result = BindDictionaryLiteral(syntax, members, dict, keyT!, valT!);
        }
        else if (tt is NamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } nt && !nt.IsAbstract)
        {
            result = BindClassLiteral(syntax, members, nt);
        }
        else
        {
            Report(Descriptors.ObjectLiteralNotConvertible, syntax, target.ToDisplayString());
            foreach (LiteralMember m in members)
            {
                if (!m.IsSpread)
                {
                    _ = BindLiteralMemberValue(m, null);
                }
            }

            return Bad(syntax);
        }
        if (result.HasErrors)
        {
            return result;
        }

        if (wrapNullable)
        {
            result = new BoundConversion(result, ConversionKind.ToNullable, target) { Syntax = syntax };
        }

        return result;
    }

    private static bool IsDictionaryLike(NamedTypeSymbol t, out TypeSymbol? key, out TypeSymbol? value, out PropertySymbol? indexer)
    {
        key = null; value = null; indexer = null;
        string def = t.OriginalDefinition.ToMetadataString();
        if (def is not ("System.Collections.Generic.Dictionary`2" or "System.Collections.Generic.IDictionary`2" or "System.Collections.Generic.SortedDictionary`2"
            or "System.Collections.Concurrent.ConcurrentDictionary`2"))
        {
            return false;
        }

        key = t.TypeArguments[0];
        value = t.TypeArguments[1];
        indexer = t.GetMembers().OfType<PropertySymbol>().FirstOrDefault(p => p.IsIndexer && p.SetMethod != null);
        return indexer != null;
    }

    private BoundExpression BindDictionaryLiteral(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members, NamedTypeSymbol dict, TypeSymbol keyT, TypeSymbol valT)
    {
        if (!ClassifyImplicit(new BoundLiteral(new ConstantValue(""), StringType), StringType, keyT).Exists)
        {
            Report(Descriptors.ObjectLiteralNotConvertible, syntax, dict.ToDisplayString());
            return Bad(syntax);
        }
        NamedTypeSymbol concrete = dict;
        if (dict.TypeKind == TypeKind.Interface)
        {
            NamedTypeSymbol d = Compilation.GetWellKnownType("System.Collections.Generic.Dictionary`2")!;
            concrete = d.Construct(keyT, valT);
        }
        LocalSymbol tmp = new("<dict>", concrete, false, LocalOwner) { IsSynthesized = true };
        MethodSymbol ctor = concrete.InstanceConstructors.First(c => c.Parameters.Length == 0);
        ImmutableArray<BoundStatement>.Builder stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        stmts.Add(new BoundLocalDeclaration(tmp, new BoundObjectCreation(ctor, concrete, [], []) { Syntax = syntax }) { Syntax = syntax });
        PropertySymbol indexer = concrete.GetMembers().OfType<PropertySymbol>().First(p => p.IsIndexer && p.SetMethod != null);
        foreach (LiteralMember m in members)
        {
            BoundExpression v = BindLiteralMemberValue(m, valT);
            if (v is BoundBadExpression)
            {
                return Bad(syntax);
            }

            BoundExpression cv = ConvertImplicit(v, valT, m.Value ?? m.NameNode);
            BoundExpression key = ConvertImplicit(new BoundLiteral(new ConstantValue(m.Name), StringType) { Syntax = m.NameNode }, keyT, m.NameNode);
            BoundPropertyAccess target = new(new BoundLocal(tmp), indexer, [key]) { Syntax = syntax };
            stmts.Add(new BoundExpressionStatement(new BoundAssignment(target, cv) { Syntax = syntax, IsDiscarded = true }) { Syntax = syntax });
        }
        BoundExpression value = new BoundLocal(tmp);
        if (!concrete.Equals(dict))
        {
            value = new BoundConversion(value, ConversionKind.ImplicitReference, dict) { Syntax = syntax };
        }

        return new BoundSequence([tmp], stmts.ToImmutable(), value) { Syntax = syntax };
    }

    private (Symbol? member, TypeSymbol? type, bool settable) FindLiteralMember(NamedTypeSymbol nt, string name, bool _1)
    {
        ImmutableArray<Symbol> candidates = LookupMembers(nt, name);
        if (candidates.Length == 0)
        {
            candidates = LookupMembers(nt, NameUtilities.FlipFirstCase(name));
        }

        foreach (Symbol c in candidates)
        {
            if (c is PropertySymbol { IsStatic: false, IsIndexer: false } p)
            {
                return (p, p.Type, p.SetMethod != null && IsAccessible(p.SetMethod));
            }

            if (c is FieldSymbol { IsStatic: false, IsConst: false } f)
            {
                return (f, f.Type, !f.IsReadOnly);
            }
        }
        return (null, null, false);
    }

    private BoundExpression BindClassLiteral(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members, NamedTypeSymbol nt)
    {
        ImmutableArray<MethodSymbol> ctors = LookupConstructors(nt);
        Dictionary<string, LiteralMember> byName = members.ToDictionary(m => m.Name, StringComparer.Ordinal);

        // choose the best constructor
        MethodSymbol? best = null;
        int bestScore = -1;
        foreach (MethodSymbol c in ctors)
        {
            bool ok = true;
            int matched = 0;
            foreach (ParameterSymbol p in c.Parameters)
            {
                bool hasMember = byName.ContainsKey(p.Name) || byName.ContainsKey(NameUtilities.FlipFirstCase(p.Name));
                if (hasMember)
                {
                    matched++;
                }
                else if (!p.IsOptional && !p.IsParams) { ok = false; break; }
            }
            if (!ok)
            {
                continue;
            }
            // the remaining members must be settable
            foreach (LiteralMember m in members)
            {
                bool isParam = c.Parameters.Any(p => p.Name == m.Name || NameUtilities.FlipFirstCase(p.Name) == m.Name);
                if (isParam)
                {
                    continue;
                }

                (Symbol? mem, TypeSymbol? _, bool settable) = FindLiteralMember(nt, m.Name, true);
                if (mem == null || !settable) { ok = false; break; }
            }
            if (!ok)
            {
                continue;
            }

            int score = (matched * 100) - c.Parameters.Length;
            if (score > bestScore) { best = c; bestScore = score; }
        }

        if (best == null && nt.TypeKind == TypeKind.Struct && ctors.All(c => c.Parameters.Length > 0 || true))
        {
            // struct without suitable constructor: default value + member initializers
            ImmutableArray<BoundMemberInit>.Builder inits0 = ImmutableArray.CreateBuilder<BoundMemberInit>();
            foreach (LiteralMember m in members)
            {
                (Symbol? mem, TypeSymbol? mt, bool settable) = FindLiteralMember(nt, m.Name, true);
                if (mem == null) { ReportUnknownLiteralMember(m, nt); _ = BindLiteralMemberValue(m, null); continue; }
                if (!settable) { Report(Descriptors.CannotAssignReadonlyProperty, m.NameNode, m.Name); _ = BindLiteralMemberValue(m, null); continue; }
                BoundExpression v = BindLiteralMemberValue(m, mt);
                inits0.Add(new BoundMemberInit(mem, ConvertImplicit(v, mt!, m.Value ?? m.NameNode)));
            }
            return _diag != null && members.Any(m => FindLiteralMember(nt, m.Name, true).member == null)
                ? Bad(syntax)
                : new BoundObjectCreation(null, nt, ImmutableArray<BoundExpression>.Empty, inits0.ToImmutable()) { Syntax = syntax };
        }

        if (best == null)
        {
            // produce the most helpful error: unknown members first, then missing required ones
            bool reported = false;
            foreach (LiteralMember m in members)
            {
                (Symbol? mem, TypeSymbol? _, bool settable) = FindLiteralMember(nt, m.Name, true);
                bool inCtor = ctors.Any(c => c.Parameters.Any(p => p.Name == m.Name || NameUtilities.FlipFirstCase(p.Name) == m.Name));
                if (mem == null && !inCtor) { ReportUnknownLiteralMember(m, nt); reported = true; }
                else if (mem != null && !settable && !inCtor) { Report(Descriptors.CannotAssignReadonlyProperty, m.NameNode, m.Name); reported = true; }
            }
            if (!reported)
            {
                MethodSymbol? ctor = ctors.OrderBy(c => c.Parameters.Length).FirstOrDefault();
                ParameterSymbol? missing = ctor?.Parameters.FirstOrDefault(p => !p.IsOptional && !byName.ContainsKey(p.Name) && !byName.ContainsKey(NameUtilities.FlipFirstCase(p.Name)));
                if (missing != null)
                {
                    Report(Descriptors.MissingLiteralMember, syntax, missing.Name, nt.ToDisplayString());
                }
                else
                {
                    Report(Descriptors.NoAccessibleConstructor, syntax, nt.ToDisplayString());
                }
            }
            foreach (LiteralMember m in members)
            {
                _ = BindLiteralMemberValue(m, null);
            }

            return Bad(syntax);
        }

        ImmutableArray<BoundExpression>.Builder args = ImmutableArray.CreateBuilder<BoundExpression>();
        HashSet<string> used = [];
        foreach (ParameterSymbol p in best.Parameters)
        {
            string key = byName.ContainsKey(p.Name) ? p.Name : NameUtilities.FlipFirstCase(p.Name);
            if (byName.TryGetValue(key, out LiteralMember? m))
            {
                _ = used.Add(m.Name);
                BoundExpression v = BindLiteralMemberValue(m, p.Type);
                if (v is BoundBadExpression)
                {
                    return Bad(syntax);
                }

                BoundExpression cv = ConvertImplicit(v, p.Type, m.Value ?? m.NameNode);
                args.Add(cv);
            }
            else if (p.IsParams)
            {
                args.Add(new BoundArrayCreation((ArrayTypeSymbol)p.Type, new BoundLiteral(new ConstantValue(0), IntType), []) { Syntax = syntax });
            }
            else
            {
                args.Add(DefaultArgument(p, syntax));
            }
        }
        ImmutableArray<BoundMemberInit>.Builder inits = ImmutableArray.CreateBuilder<BoundMemberInit>();
        foreach (LiteralMember m in members)
        {
            if (used.Contains(m.Name))
            {
                continue;
            }

            (Symbol? mem, TypeSymbol? mt, bool _) = FindLiteralMember(nt, m.Name, true);
            BoundExpression v = BindLiteralMemberValue(m, mt);
            if (v is BoundBadExpression)
            {
                return Bad(syntax);
            }

            inits.Add(new BoundMemberInit(mem!, ConvertImplicit(v, mt!, m.Value ?? m.NameNode)));
        }
        return new BoundObjectCreation(best, nt, args.ToImmutable(), inits.ToImmutable()) { Syntax = syntax };
    }

    private void ReportUnknownLiteralMember(LiteralMember m, NamedTypeSymbol nt)
    {
        Report(Descriptors.ExcessLiteralProperty, m.NameNode, m.Name, nt.ToDisplayString());
    }

    private BoundExpression BindSpreadObject(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members, TypeSymbol target)
    {
        if (!members[0].IsSpread || members.Count(m => m.IsSpread) > 1)
        {
            Report(Descriptors.FeatureNotSupported, syntax, "Object spread other than a single leading '...expr'");
            return Bad(syntax);
        }
        BoundExpression source = BindValue(members[0].Value!);
        if (source is BoundBadExpression)
        {
            return Bad(syntax);
        }

        if (target is not NamedTypeSymbol nt || source.Type == null || !source.Type.StripNullable().Equals(nt))
        {
            Report(Descriptors.SpreadTypeMismatch, members[0].NameNode, source.Type?.ToDisplayString() ?? "?", target.ToDisplayString());
            return Bad(syntax);
        }
        if (source.Type is NullableTypeSymbol) { Report(Descriptors.PossiblyNull, members[0].NameNode, "spread operand"); return Bad(syntax); }

        if (nt.OriginalDefinition is SynthesizedAnonymousRecord anonymous)
        {
            return BindAnonymousSpread(syntax, members, nt, anonymous, source);
        }

        LocalSymbol tmp = new("<with>", nt, false, LocalOwner) { IsSynthesized = true };
        ImmutableArray<BoundStatement>.Builder stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        BoundExpression init;
        if (nt.TypeKind == TypeKind.Struct)
        {
            init = source;
        }
        else
        {
            MethodSymbol? clone = nt.GetMembers("<Clone>$").OfType<MethodSymbol>().FirstOrDefault();
            if (clone == null) { Report(Descriptors.SpreadTypeMismatch, members[0].NameNode, nt.ToDisplayString(), nt.ToDisplayString()); return Bad(syntax); }
            init = new BoundCall(source, clone, []) { Syntax = syntax };
            if (!clone.ReturnType.Equals(nt))
            {
                init = new BoundConversion(init, ConversionKind.ExplicitReference, nt) { Syntax = syntax };
            }
        }
        stmts.Add(new BoundLocalDeclaration(tmp, init) { Syntax = syntax });
        for (int i = 1; i < members.Count; i++)
        {
            LiteralMember m = members[i];
            (Symbol? mem, TypeSymbol? mt, bool settable) = FindLiteralMember(nt, m.Name, true);
            if (mem == null) { ReportUnknownLiteralMember(m, nt); _ = BindLiteralMemberValue(m, null); continue; }
            BoundExpression v = BindLiteralMemberValue(m, mt);
            if (v is BoundBadExpression)
            {
                return Bad(syntax);
            }

            BoundExpression cv = ConvertImplicit(v, mt!, m.Value ?? m.NameNode);
            BoundExpression targetExpr = mem is PropertySymbol p
                ? new BoundPropertyAccess(new BoundLocal(tmp), p, ImmutableArray<BoundExpression>.Empty) { Syntax = syntax }
                : new BoundFieldAccess(new BoundLocal(tmp), (FieldSymbol)mem) { Syntax = syntax };
            stmts.Add(new BoundExpressionStatement(new BoundAssignment(targetExpr, cv) { Syntax = syntax, IsDiscarded = true }) { Syntax = syntax });
        }
        return new BoundSequence([tmp], stmts.ToImmutable(), new BoundLocal(tmp)) { Syntax = syntax };
    }

    /// <summary>
    /// `{ ...anon, age: 4 }` where `anon` is an anonymous record: its properties are get-only and it has no clone method, so the result is a
    /// new instance built from the overrides and the remaining properties of the spread value.
    /// </summary>
    private BoundExpression BindAnonymousSpread(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members, NamedTypeSymbol nt, SynthesizedAnonymousRecord anonymous, BoundExpression source)
    {
        LocalSymbol tmp = new("<spread>", nt, false, LocalOwner) { IsSynthesized = true };
        MethodSymbol ctor = nt.InstanceConstructors.First(c => c.Parameters.Length == anonymous.PropertyNames.Length);
        Dictionary<string, BoundExpression> overrides = new(StringComparer.Ordinal);
        for (int i = 1; i < members.Count; i++)
        {
            LiteralMember m = members[i];
            PropertySymbol? prop = nt.GetMembers(m.Name).OfType<PropertySymbol>().FirstOrDefault();
            if (prop == null) { ReportUnknownLiteralMember(m, nt); _ = BindLiteralMemberValue(m, null); continue; }
            BoundExpression v = BindLiteralMemberValue(m, prop.Type);
            if (v is BoundBadExpression)
            {
                return Bad(syntax);
            }

            overrides[m.Name] = ConvertImplicit(v, prop.Type, m.Value ?? m.NameNode);
        }

        ImmutableArray<BoundExpression>.Builder args = ImmutableArray.CreateBuilder<BoundExpression>();
        foreach (string name in anonymous.PropertyNames)
        {
            args.Add(overrides.TryGetValue(name, out BoundExpression? replaced)
                ? replaced
                : new BoundPropertyAccess(new BoundLocal(tmp), nt.GetMembers(name).OfType<PropertySymbol>().First(), []) { Syntax = syntax });
        }

        BoundObjectCreation created = new(ctor, nt, args.ToImmutable(), []) { Syntax = syntax };
        return new BoundSequence([tmp], [new BoundLocalDeclaration(tmp, source) { Syntax = syntax }], created) { Syntax = syntax };
    }

    private BoundExpression BindAnonymousObject(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members)
    {
        if (members.Any(m => m.IsSpread))
        {
            // `{ ...p, x: 3 }` without a target: the result has the type of the spread operand
            if (members[0].IsSpread && members.Count(m => m.IsSpread) == 1)
            {
                BoundExpression spreadSource = Speculatively(() => BindValue(members[0].Value!));
                if (spreadSource.Type?.StripNullable() is NamedTypeSymbol spreadType && spreadSource is not BoundBadExpression)
                {
                    return BindSpreadObject(syntax, members, spreadType);
                }
            }
            Report(Descriptors.FeatureNotSupported, syntax, "Spread in an object literal without a target type (only a single leading '...expr' of a record type is supported)");
            return Bad(syntax);
        }
        List<string> names = [];
        List<BoundExpression> values = [];
        foreach (LiteralMember m in members)
        {
            BoundExpression v = BindLiteralMemberValue(m, null);
            if (v is BoundBadExpression)
            {
                return Bad(syntax);
            }

            BoundExpression w = v is BoundUnbound ? ConvertImplicitForInference(v, m) : v;
            if (w.HasErrors)
            {
                return Bad(syntax);
            }

            TypeSymbol t = WidenInferredType(w, m.NameNode, report: true);
            if (t.IsErrorType)
            {
                return Bad(syntax);
            }

            names.Add(m.Name);
            values.Add(ConvertImplicit(w, t, m.Value ?? m.NameNode));
        }
        NamedTypeSymbol anon = Compilation.GetAnonymousRecord(names, [.. values.Select(v => v.Type!)]);
        MethodSymbol ctor = anon.InstanceConstructors.First(c => c.Parameters.Length == names.Count);
        return new BoundObjectCreation(ctor, anon, [.. values], []) { Syntax = syntax };
    }

    private BoundExpression ConvertImplicitForInference(BoundExpression v, LiteralMember m)
    {
        if (v is BoundUnbound u && HasNaturalType(u))
        {
            return u.BindToType(null);
        }

        Report(Descriptors.CannotInferType, m.NameNode);
        return Bad(m.NameNode);
    }
}
