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
        if (t is ArrayTypeSymbol a) return a.ElementType;
        if (t.SpecialType == SpecialType.String) return Special(SpecialType.Char);
        var ifaceName = isAsync ? "System.Collections.Generic.IAsyncEnumerable`1" : "System.Collections.Generic.IEnumerable`1";
        NamedTypeSymbol? found = null;
        var candidates = new List<NamedTypeSymbol>();
        if (t is NamedTypeSymbol self) candidates.Add(self);
        candidates.AddRange(t.AllInterfaces);
        foreach (var i in candidates)
            if (i.OriginalDefinition.ToMetadataString() == ifaceName)
            {
                if (found == null) found = i;
                else if (!found.Equals(i)) return null;
            }
        if (found != null) return found.TypeArguments[0];
        // GetEnumerator pattern
        var ge = LookupMembers(t, isAsync ? "GetAsyncEnumerator" : "GetEnumerator").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == (isAsync ? 0 : 0) || m.Parameters.All(p => p.IsOptional));
        if (ge != null)
        {
            var cur = LookupMembers(ge.ReturnType, "Current").OfType<PropertySymbol>().FirstOrDefault();
            if (cur != null) return cur.Type;
        }
        if (!isAsync && ImplementsInterface(t, (NamedTypeSymbol)Special(SpecialType.IEnumerable))) return ObjectType;
        return null;
    }

    // ======================================================================= collection expressions

    private BoundExpression BindCollectionExpression(ArrayLiteralExpressionSyntax arr, TypeSymbol? target)
    {
        var elems = arr.Elements.ToImmutableArray();
        var hasSpread = elems.Any(e => e is SpreadElementSyntax);
        var capturedScope = _scope;
        var capturedNarrow = _narrow;
        TypeSymbol? naturalElement = null;
        if (target == null)
        {
            naturalElement = Speculatively(() =>
            {
                var types = new List<BoundExpression>();
                foreach (var e in elems)
                {
                    if (e is SpreadElementSyntax sp)
                    {
                        var s = BindValue(sp.Expression);
                        var it = s.Type != null ? GetIterationType(s.Type) : null;
                        if (it == null) return null;
                        types.Add(new BoundDefaultValue(it));
                    }
                    else
                    {
                        var b = BindValue(e);
                        if (b is BoundUnbound { Type: null } || b.Type == null) return null;
                        types.Add(b);
                    }
                }
                return types.Count == 0 ? null : BestCommonType(types, arr);
            });
        }
        var info = new CollectionInfo { ElementCount = elems.Length, HasSpread = hasSpread, NaturalElementType = naturalElement };
        var natural = naturalElement != null && !naturalElement.IsErrorType ? _c.CreateArrayType(naturalElement) : null;
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
        var wrapNullable = target is NullableTypeSymbol;
        var tt = target?.StripNullable();

        if (tt == null)
        {
            // natural type: best common type array
            var parts = new List<BoundExpression>();
            foreach (var e in elems)
            {
                if (e is SpreadElementSyntax sp)
                {
                    var s = BindValue(sp.Expression);
                    var it = s.Type != null ? GetIterationType(s.Type) : null;
                    if (it == null) { Report(Descriptors.NotIterable, sp.Expression, s.Type?.ToDisplayString() ?? "?"); return Bad(syntax); }
                    parts.Add(new BoundDefaultValue(it));
                }
                else parts.Add(BindValue(e));
            }
            if (parts.Count == 0)
            {
                Report(Descriptors.CannotInferEmptyCollection, syntax);
                return Bad(syntax);
            }
            var common = BestCommonType(parts, syntax);
            if (common == null)
            {
                Report(Descriptors.NoBestCommonTypeCollection, syntax);
                return Bad(syntax);
            }
            tt = _c.CreateArrayType(common);
        }

        if (tt.IsErrorType) { foreach (var e in elems) if (e is not SpreadElementSyntax) BindValue(e); return Bad(syntax); }

        if (tt is NamedTypeSymbol { IsTuple: true } tuple && target != null)
        {
            var targs = tuple.TypeArguments;
            if (elems.Any(e => e is SpreadElementSyntax) || targs.Length != elems.Length || targs.Length > 7)
            {
                Report(Descriptors.CollectionNotConvertible, syntax, target.ToDisplayString());
                foreach (var e in elems) if (e is not SpreadElementSyntax) BindValue(e);
                return Bad(syntax);
            }
            var items = ImmutableArray.CreateBuilder<BoundExpression>(targs.Length);
            for (var i = 0; i < targs.Length; i++)
            {
                var item = BindToType(elems[i], targs[i]);
                if (item.HasErrors) return Bad(syntax);
                items.Add(item);
            }
            var tctor = tuple.InstanceConstructors.First(c => c.Parameters.Length == targs.Length);
            BoundExpression created = new BoundObjectCreation(tctor, tuple, items.ToImmutable(), ImmutableArray<BoundMemberInit>.Empty) { Syntax = syntax };
            if (wrapNullable) created = new BoundConversion(created, ConversionKind.ToNullable, target) { Syntax = syntax };
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
            var def = nt.OriginalDefinition.ToMetadataString();
            if (def == "System.Span`1") { kind = CollectionKind.Span; elementType = nt.TypeArguments[0]; }
            else if (def == "System.ReadOnlySpan`1") { kind = CollectionKind.ReadOnlySpan; elementType = nt.TypeArguments[0]; }
            else if (FindCollectionBuilder(nt, out builder, out var be)) { kind = CollectionKind.Builder; elementType = be; }
            else if (def is "System.Collections.Generic.IEnumerable`1" or "System.Collections.Generic.IReadOnlyCollection`1" or "System.Collections.Generic.IReadOnlyList`1")
            { kind = CollectionKind.ReadOnlyInterface; elementType = nt.TypeArguments[0]; }
            else if (def is "System.Collections.Generic.ICollection`1" or "System.Collections.Generic.IList`1")
            { kind = CollectionKind.ListInterface; elementType = nt.TypeArguments[0]; }
            else if (nt.TypeKind is TypeKind.Class or TypeKind.Struct && !nt.IsAbstract && GetAddElementType(nt) is { } ae && HasDefaultConstructor(nt))
            { kind = CollectionKind.Add; elementType = ae; }
            else
            {
                Report(Descriptors.CollectionNotConvertible, syntax, target!.ToDisplayString());
                foreach (var e in elems) if (e is not SpreadElementSyntax) BindValue(e);
                return Bad(syntax);
            }
        }
        else
        {
            Report(Descriptors.CollectionNotConvertible, syntax, target!.ToDisplayString());
            return Bad(syntax);
        }

        var elementTypeFinal = elementType!;
        var hasSpread = elems.Any(e => e is SpreadElementSyntax);
        // bind elements
        var bound = new List<(BoundExpression Expr, bool Spread)>();
        foreach (var e in elems)
        {
            if (e is SpreadElementSyntax sp)
            {
                var s = BindValue(sp.Expression);
                if (s is BoundBadExpression) return Bad(syntax);
                var it = s.Type != null ? GetIterationType(s.Type) : null;
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
                var v = BindValue(e, elementTypeFinal);
                if (v is BoundBadExpression) return Bad(syntax);
                var c = ConvertImplicit(v, elementTypeFinal, e);
                if (c.HasErrors) return Bad(syntax);
                bound.Add((c, false));
            }
        }

        BoundExpression result;
        if (kind == CollectionKind.Add)
            result = BuildAddCollection(syntax, namedTarget!, elementTypeFinal, bound);
        else
        {
            var array = BuildArrayFromElements(syntax, elementTypeFinal, bound);
            var arrType = (ArrayTypeSymbol)array.Type!;
            switch (kind)
            {
                case CollectionKind.Array:
                    result = array;
                    break;
                case CollectionKind.Span or CollectionKind.ReadOnlySpan:
                {
                    var conv = namedTarget!.GetMembers("op_Implicit").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1 && m.Parameters[0].Type.Equals(arrType));
                    if (conv == null) { Report(Descriptors.CollectionNotConvertible, syntax, target!.ToDisplayString()); return Bad(syntax); }
                    result = new BoundConversion(array, ConversionKind.UserDefined, namedTarget, conv) { Syntax = syntax };
                    break;
                }
                case CollectionKind.Builder:
                {
                    var spanT = builder!.Parameters[0].Type;
                    var rosConv = ((NamedTypeSymbol)spanT).GetMembers("op_Implicit").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1 && m.Parameters[0].Type.Equals(arrType));
                    if (rosConv == null) { Report(Descriptors.CollectionNotConvertible, syntax, target!.ToDisplayString()); return Bad(syntax); }
                    var spanArg = new BoundConversion(array, ConversionKind.UserDefined, spanT, rosConv) { Syntax = syntax };
                    result = new BoundCall(null, builder, [spanArg]) { Syntax = syntax };
                    break;
                }
                case CollectionKind.ReadOnlyInterface:
                    result = new BoundConversion(array, ConversionKind.ImplicitReference, namedTarget!) { Syntax = syntax };
                    break;
                default: // ListInterface
                {
                    var listDef = _c.GetWellKnownType("System.Collections.Generic.List`1");
                    var list = listDef!.Construct(elementTypeFinal);
                    var enumT = _c.GetWellKnownType("System.Collections.Generic.IEnumerable`1")!.Construct(elementTypeFinal);
                    var ctor = list.InstanceConstructors.First(c => c.Parameters.Length == 1 && c.Parameters[0].Type.Equals(enumT));
                    var create = new BoundObjectCreation(ctor, list, [new BoundConversion(array, ConversionKind.ImplicitReference, enumT) { Syntax = syntax }], ImmutableArray<BoundMemberInit>.Empty) { Syntax = syntax };
                    result = new BoundConversion(create, ConversionKind.ImplicitReference, namedTarget!) { Syntax = syntax };
                    break;
                }
            }
        }
        if (wrapNullable && target != null) result = new BoundConversion(result, ConversionKind.ToNullable, target) { Syntax = syntax };
        return result;
    }

    private bool HasDefaultConstructor(NamedTypeSymbol t) =>
        t.TypeKind == TypeKind.Struct || t.InstanceConstructors.Any(c => c.Parameters.All(p => p.IsOptional) && IsAccessible(c));

    private TypeSymbol? GetAddElementType(NamedTypeSymbol t)
    {
        if (!ImplementsInterface(t, (NamedTypeSymbol)Special(SpecialType.IEnumerable))) return null;
        var adds = LookupMembers(t, "Add").OfType<MethodSymbol>().Where(m => m.Parameters.Length == 1 && !m.IsStatic).ToList();
        if (adds.Count == 0) return null;
        // prefer the iteration element type when an Add overload accepts it
        var it = GetIterationType(t);
        if (it != null && adds.Any(a => a.Parameters[0].Type.Equals(it))) return it;
        return adds[0].Parameters[0].Type;
    }

    private bool FindCollectionBuilder(NamedTypeSymbol target, out MethodSymbol? method, out TypeSymbol? elementType)
    {
        method = null; elementType = null;
        var attr = target.OriginalDefinition.GetAttribute("System.Runtime.CompilerServices.CollectionBuilderAttribute");
        if (attr == null || attr.ConstructorArguments.Length < 2) return false;
        if (attr.ConstructorArguments[0] is not NamedTypeSymbol builderType || attr.ConstructorArguments[1] is not string name) return false;
        foreach (var m in builderType.GetMembers(name).OfType<MethodSymbol>())
        {
            if (!m.IsStatic || m.Parameters.Length != 1) continue;
            MethodSymbol cm = m;
            if (m.IsGenericMethod)
            {
                if (m.Arity != target.Arity) continue;
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
        var arrType = _c.CreateArrayType(elementType);
        if (elems.All(e => !e.Spread))
        {
            return new BoundArrayCreation(arrType, new BoundLiteral(new ConstantValue(elems.Count), IntType), elems.Select(e => e.Expr).ToImmutableArray()) { Syntax = syntax };
        }
        // with spreads: accumulate into a List<T> and convert
        var listDef = _c.GetWellKnownType("System.Collections.Generic.List`1")!;
        var list = listDef.Construct(elementType);
        var tmp = new LocalSymbol("<list>", list, false, LocalOwner) { IsSynthesized = true };
        var ctor = list.InstanceConstructors.First(c => c.Parameters.Length == 0);
        var stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        stmts.Add(new BoundLocalDeclaration(tmp, new BoundObjectCreation(ctor, list, ImmutableArray<BoundExpression>.Empty, ImmutableArray<BoundMemberInit>.Empty) { Syntax = syntax }) { Syntax = syntax });
        AddElementsTo(stmts, tmp, list, elementType, elems, syntax);
        var toArray = list.GetMembers("ToArray").OfType<MethodSymbol>().First(m => m.Parameters.Length == 0);
        var value = new BoundCall(new BoundLocal(tmp), toArray, ImmutableArray<BoundExpression>.Empty) { Syntax = syntax };
        return new BoundSequence([tmp], stmts.ToImmutable(), value) { Syntax = syntax };
    }

    private void AddElementsTo(ImmutableArray<BoundStatement>.Builder stmts, LocalSymbol tmp, NamedTypeSymbol collectionType, TypeSymbol elementType,
        List<(BoundExpression Expr, bool Spread)> elems, SyntaxNode syntax)
    {
        var enumT = _c.GetWellKnownType("System.Collections.Generic.IEnumerable`1")!.Construct(elementType);
        foreach (var (expr, spread) in elems)
        {
            if (!spread)
            {
                var add = FindAddMethod(collectionType, elementType, expr);
                if (add == null) continue;
                var arg = ConvertImplicit(expr, add.Parameters[0].Type, syntax);
                stmts.Add(new BoundExpressionStatement(new BoundCall(new BoundLocal(tmp), add, [arg]) { Syntax = syntax }) { Syntax = syntax });
                continue;
            }
            var addRange = collectionType.GetMembers("AddRange").OfType<MethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1 && !m.IsStatic && ClassifyImplicit(expr, expr.Type!, m.Parameters[0].Type).Exists);
            if (addRange != null)
            {
                stmts.Add(new BoundExpressionStatement(new BoundCall(new BoundLocal(tmp), addRange, [ConvertImplicit(expr, addRange.Parameters[0].Type, syntax)]) { Syntax = syntax }) { Syntax = syntax });
                continue;
            }
            var addM = FindAddMethod(collectionType, elementType, null);
            if (addM == null) continue;
            var it = GetIterationType(expr.Type!)!;
            stmts.Add(BuildForEachLoop(expr, it, syntax, current =>
            {
                var arg = ConvertImplicit(new BoundLocal(current), addM.Parameters[0].Type, syntax);
                return new BoundExpressionStatement(new BoundCall(new BoundLocal(tmp), addM, [arg]) { Syntax = syntax }) { Syntax = syntax };
            }));
        }
    }

    private MethodSymbol? FindAddMethod(NamedTypeSymbol collectionType, TypeSymbol elementType, BoundExpression? sample)
    {
        var adds = LookupMembers(collectionType, "Add").OfType<MethodSymbol>().Where(m => m.Parameters.Length == 1 && !m.IsStatic).ToList();
        if (sample != null)
        {
            var r = ResolveOverloads(adds, [sample], ImmutableArray<TypeSymbol>.Empty, sample.Syntax!, "Add", report: false);
            if (r != null) return r.Method;
        }
        return adds.FirstOrDefault(a => a.Parameters[0].Type.Equals(elementType)) ?? adds.FirstOrDefault();
    }

    private BoundExpression BuildAddCollection(SyntaxNode syntax, NamedTypeSymbol type, TypeSymbol elementType, List<(BoundExpression Expr, bool Spread)> elems)
    {
        var tmp = new LocalSymbol("<coll>", type, false, LocalOwner) { IsSynthesized = true };
        var ctor = type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        var create = new BoundObjectCreation(ctor, type, ImmutableArray<BoundExpression>.Empty, ImmutableArray<BoundMemberInit>.Empty) { Syntax = syntax };
        var stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        stmts.Add(new BoundLocalDeclaration(tmp, create) { Syntax = syntax });
        AddElementsTo(stmts, tmp, type, elementType, elems, syntax);
        return new BoundSequence([tmp], stmts.ToImmutable(), new BoundLocal(tmp)) { Syntax = syntax };
    }

    // ======================================================================= object literals

    private sealed record LiteralMember(string Name, SyntaxNode NameNode, ExpressionSyntax? Value, bool IsSpread);

    private BoundExpression BindObjectLiteral(ObjectLiteralExpressionSyntax obj, TypeSymbol? target)
    {
        var members = new List<LiteralMember>();
        foreach (var m in obj.Members)
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
        var capturedScope = _scope;
        var capturedNarrow = _narrow;
        var info = new ObjectLiteralInfo { PropertyNames = members.Where(m => !m.IsSpread).Select(m => m.Name).ToImmutableArray(), HasSpread = members.Any(m => m.IsSpread) };
        return new BoundUnbound(obj, t => WithCapturedState(capturedScope, capturedNarrow, () => BindObjectLiteralToType(obj, members, t)))
        { IsObjectLiteral = true, ObjectLiteral = info };
    }

    private BoundExpression BindLiteralMemberValue(LiteralMember m, TypeSymbol? target)
    {
        if (m.Value != null) return BindValue(m.Value, target);
        // shorthand: the member name is an identifier expression
        var token = ((ShorthandPropertyAssignmentSyntax)m.NameNode).NameToken;
        var ident = new IdentifierNameSyntax(new SyntaxToken(SyntaxKind.IdentifierToken, token.Text, token.FullStart, token.LeadingTrivia, token.TrailingTrivia));
        // keep diagnostics positioned at the original token
        var local = LookupLocal(token.Text);
        if (local != null) return SymbolToExpression(local, m.NameNode);
        var r = BindModuleLevelName(token.Text, 0, ImmutableArray<TypeSymbol>.Empty, m.NameNode, token.Span);
        _ = ident;
        return r;
    }

    private BoundExpression BindObjectLiteralToType(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members, TypeSymbol? target)
    {
        if (target == null) return BindAnonymousObject(syntax, members);
        var wrapNullable = target is NullableTypeSymbol;
        var tt = target.StripNullable();
        if (tt.IsErrorType)
        {
            foreach (var m in members) if (!m.IsSpread) BindLiteralMemberValue(m, null);
            return Bad(syntax);
        }

        // duplicate keys
        var seen = new HashSet<string>();
        foreach (var m in members)
            if (!m.IsSpread && !seen.Add(m.Name)) Report(Descriptors.DuplicateObjectLiteralKey, m.NameNode, m.Name);

        BoundExpression result;
        if (members.Any(m => m.IsSpread)) result = BindSpreadObject(syntax, members, tt);
        else if (tt is NamedTypeSymbol dict && IsDictionaryLike(dict, out var keyT, out var valT, out var setIndexer))
            result = BindDictionaryLiteral(syntax, members, dict, keyT!, valT!);
        else if (tt is NamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } nt && !nt.IsAbstract)
            result = BindClassLiteral(syntax, members, nt);
        else
        {
            Report(Descriptors.ObjectLiteralNotConvertible, syntax, target.ToDisplayString());
            foreach (var m in members) if (!m.IsSpread) BindLiteralMemberValue(m, null);
            return Bad(syntax);
        }
        if (result.HasErrors) return result;
        if (wrapNullable) result = new BoundConversion(result, ConversionKind.ToNullable, target) { Syntax = syntax };
        return result;
    }

    private bool IsDictionaryLike(NamedTypeSymbol t, out TypeSymbol? key, out TypeSymbol? value, out PropertySymbol? indexer)
    {
        key = null; value = null; indexer = null;
        var def = t.OriginalDefinition.ToMetadataString();
        if (def is not ("System.Collections.Generic.Dictionary`2" or "System.Collections.Generic.IDictionary`2" or "System.Collections.Generic.SortedDictionary`2"
            or "System.Collections.Concurrent.ConcurrentDictionary`2")) return false;
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
        var concrete = dict;
        if (dict.TypeKind == TypeKind.Interface)
        {
            var d = _c.GetWellKnownType("System.Collections.Generic.Dictionary`2")!;
            concrete = d.Construct(keyT, valT);
        }
        var tmp = new LocalSymbol("<dict>", concrete, false, LocalOwner) { IsSynthesized = true };
        var ctor = concrete.InstanceConstructors.First(c => c.Parameters.Length == 0);
        var stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        stmts.Add(new BoundLocalDeclaration(tmp, new BoundObjectCreation(ctor, concrete, ImmutableArray<BoundExpression>.Empty, ImmutableArray<BoundMemberInit>.Empty) { Syntax = syntax }) { Syntax = syntax });
        var indexer = concrete.GetMembers().OfType<PropertySymbol>().First(p => p.IsIndexer && p.SetMethod != null);
        foreach (var m in members)
        {
            var v = BindLiteralMemberValue(m, valT);
            if (v is BoundBadExpression) return Bad(syntax);
            var cv = ConvertImplicit(v, valT, m.Value ?? m.NameNode);
            var key = ConvertImplicit(new BoundLiteral(new ConstantValue(m.Name), StringType) { Syntax = m.NameNode }, keyT, m.NameNode);
            var target = new BoundPropertyAccess(new BoundLocal(tmp), indexer, [key]) { Syntax = syntax };
            stmts.Add(new BoundExpressionStatement(new BoundAssignment(target, cv) { Syntax = syntax, IsDiscarded = true }) { Syntax = syntax });
        }
        BoundExpression value = new BoundLocal(tmp);
        if (!concrete.Equals(dict)) value = new BoundConversion(value, ConversionKind.ImplicitReference, dict) { Syntax = syntax };
        return new BoundSequence([tmp], stmts.ToImmutable(), value) { Syntax = syntax };
    }

    private (Symbol? member, TypeSymbol? type, bool settable) FindLiteralMember(NamedTypeSymbol nt, string name, bool allowInit)
    {
        var candidates = LookupMembers(nt, name);
        if (candidates.Length == 0) candidates = LookupMembers(nt, NameUtilities.FlipFirstCase(name));
        foreach (var c in candidates)
        {
            if (c is PropertySymbol { IsStatic: false, IsIndexer: false } p) return (p, p.Type, p.SetMethod != null && IsAccessible(p.SetMethod));
            if (c is FieldSymbol { IsStatic: false, IsConst: false } f) return (f, f.Type, !f.IsReadOnly);
        }
        return (null, null, false);
    }

    private BoundExpression BindClassLiteral(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members, NamedTypeSymbol nt)
    {
        var ctors = LookupConstructors(nt);
        var byName = members.ToDictionary(m => m.Name, StringComparer.Ordinal);

        // choose the best constructor
        MethodSymbol? best = null;
        var bestScore = -1;
        foreach (var c in ctors)
        {
            var ok = true;
            var matched = 0;
            foreach (var p in c.Parameters)
            {
                var hasMember = byName.ContainsKey(p.Name) || byName.ContainsKey(NameUtilities.FlipFirstCase(p.Name));
                if (hasMember) matched++;
                else if (!p.IsOptional && !p.IsParams) { ok = false; break; }
            }
            if (!ok) continue;
            // the remaining members must be settable
            foreach (var m in members)
            {
                var isParam = c.Parameters.Any(p => p.Name == m.Name || NameUtilities.FlipFirstCase(p.Name) == m.Name);
                if (isParam) continue;
                var (mem, _, settable) = FindLiteralMember(nt, m.Name, true);
                if (mem == null || !settable) { ok = false; break; }
            }
            if (!ok) continue;
            var score = matched * 100 - c.Parameters.Length;
            if (score > bestScore) { best = c; bestScore = score; }
        }

        if (best == null && nt.TypeKind == TypeKind.Struct && ctors.All(c => c.Parameters.Length > 0 || true))
        {
            // struct without suitable constructor: default value + member initializers
            var inits0 = ImmutableArray.CreateBuilder<BoundMemberInit>();
            foreach (var m in members)
            {
                var (mem, mt, settable) = FindLiteralMember(nt, m.Name, true);
                if (mem == null) { ReportUnknownLiteralMember(m, nt); BindLiteralMemberValue(m, null); continue; }
                if (!settable) { Report(Descriptors.CannotAssignReadonlyProperty, m.NameNode, m.Name); BindLiteralMemberValue(m, null); continue; }
                var v = BindLiteralMemberValue(m, mt);
                inits0.Add(new BoundMemberInit(mem, ConvertImplicit(v, mt!, m.Value ?? m.NameNode)));
            }
            if (_diag != null && members.Any(m => FindLiteralMember(nt, m.Name, true).member == null)) return Bad(syntax);
            return new BoundObjectCreation(null, nt, ImmutableArray<BoundExpression>.Empty, inits0.ToImmutable()) { Syntax = syntax };
        }

        if (best == null)
        {
            // produce the most helpful error: unknown members first, then missing required ones
            var reported = false;
            foreach (var m in members)
            {
                var (mem, _, settable) = FindLiteralMember(nt, m.Name, true);
                var inCtor = ctors.Any(c => c.Parameters.Any(p => p.Name == m.Name || NameUtilities.FlipFirstCase(p.Name) == m.Name));
                if (mem == null && !inCtor) { ReportUnknownLiteralMember(m, nt); reported = true; }
                else if (mem != null && !settable && !inCtor) { Report(Descriptors.CannotAssignReadonlyProperty, m.NameNode, m.Name); reported = true; }
            }
            if (!reported)
            {
                var ctor = ctors.OrderBy(c => c.Parameters.Length).FirstOrDefault();
                var missing = ctor?.Parameters.FirstOrDefault(p => !p.IsOptional && !byName.ContainsKey(p.Name) && !byName.ContainsKey(NameUtilities.FlipFirstCase(p.Name)));
                if (missing != null) Report(Descriptors.MissingLiteralMember, syntax, missing.Name, nt.ToDisplayString());
                else Report(Descriptors.NoAccessibleConstructor, syntax, nt.ToDisplayString());
            }
            foreach (var m in members) BindLiteralMemberValue(m, null);
            return Bad(syntax);
        }

        var args = ImmutableArray.CreateBuilder<BoundExpression>();
        var used = new HashSet<string>();
        foreach (var p in best.Parameters)
        {
            var key = byName.ContainsKey(p.Name) ? p.Name : NameUtilities.FlipFirstCase(p.Name);
            if (byName.TryGetValue(key, out var m))
            {
                used.Add(m.Name);
                var v = BindLiteralMemberValue(m, p.Type);
                if (v is BoundBadExpression) return Bad(syntax);
                var cv = ConvertImplicit(v, p.Type, m.Value ?? m.NameNode);
                args.Add(cv);
            }
            else if (p.IsParams)
                args.Add(new BoundArrayCreation((ArrayTypeSymbol)p.Type, new BoundLiteral(new ConstantValue(0), IntType), ImmutableArray<BoundExpression>.Empty) { Syntax = syntax });
            else args.Add(DefaultArgument(p, syntax));
        }
        var inits = ImmutableArray.CreateBuilder<BoundMemberInit>();
        foreach (var m in members)
        {
            if (used.Contains(m.Name)) continue;
            var (mem, mt, _) = FindLiteralMember(nt, m.Name, true);
            var v = BindLiteralMemberValue(m, mt);
            if (v is BoundBadExpression) return Bad(syntax);
            inits.Add(new BoundMemberInit(mem!, ConvertImplicit(v, mt!, m.Value ?? m.NameNode)));
        }
        return new BoundObjectCreation(best, nt, args.ToImmutable(), inits.ToImmutable()) { Syntax = syntax };
    }

    private void ReportUnknownLiteralMember(LiteralMember m, NamedTypeSymbol nt) =>
        Report(Descriptors.ExcessLiteralProperty, m.NameNode, m.Name, nt.ToDisplayString());

    private BoundExpression BindSpreadObject(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members, TypeSymbol target)
    {
        if (members[0].IsSpread == false || members.Count(m => m.IsSpread) > 1)
        {
            Report(Descriptors.FeatureNotSupported, syntax, "Object spread other than a single leading '...expr'");
            return Bad(syntax);
        }
        var source = BindValue(members[0].Value!);
        if (source is BoundBadExpression) return Bad(syntax);
        if (target is not NamedTypeSymbol nt || source.Type == null || !source.Type.StripNullable().Equals(nt))
        {
            Report(Descriptors.SpreadTypeMismatch, members[0].NameNode, source.Type?.ToDisplayString() ?? "?", target.ToDisplayString());
            return Bad(syntax);
        }
        if (source.Type is NullableTypeSymbol) { Report(Descriptors.PossiblyNull, members[0].NameNode, "spread operand"); return Bad(syntax); }

        var tmp = new LocalSymbol("<with>", nt, false, LocalOwner) { IsSynthesized = true };
        var stmts = ImmutableArray.CreateBuilder<BoundStatement>();
        BoundExpression init;
        if (nt.TypeKind == TypeKind.Struct) init = source;
        else
        {
            var clone = nt.GetMembers("<Clone>$").OfType<MethodSymbol>().FirstOrDefault();
            if (clone == null) { Report(Descriptors.SpreadTypeMismatch, members[0].NameNode, nt.ToDisplayString(), nt.ToDisplayString()); return Bad(syntax); }
            init = new BoundCall(source, clone, ImmutableArray<BoundExpression>.Empty) { Syntax = syntax };
            if (!clone.ReturnType.Equals(nt)) init = new BoundConversion(init, ConversionKind.ExplicitReference, nt) { Syntax = syntax };
        }
        stmts.Add(new BoundLocalDeclaration(tmp, init) { Syntax = syntax });
        for (var i = 1; i < members.Count; i++)
        {
            var m = members[i];
            var (mem, mt, settable) = FindLiteralMember(nt, m.Name, true);
            if (mem == null) { ReportUnknownLiteralMember(m, nt); BindLiteralMemberValue(m, null); continue; }
            var v = BindLiteralMemberValue(m, mt);
            if (v is BoundBadExpression) return Bad(syntax);
            var cv = ConvertImplicit(v, mt!, m.Value ?? m.NameNode);
            BoundExpression targetExpr = mem is PropertySymbol p
                ? new BoundPropertyAccess(new BoundLocal(tmp), p, ImmutableArray<BoundExpression>.Empty) { Syntax = syntax }
                : new BoundFieldAccess(new BoundLocal(tmp), (FieldSymbol)mem) { Syntax = syntax };
            stmts.Add(new BoundExpressionStatement(new BoundAssignment(targetExpr, cv) { Syntax = syntax, IsDiscarded = true }) { Syntax = syntax });
        }
        return new BoundSequence([tmp], stmts.ToImmutable(), new BoundLocal(tmp)) { Syntax = syntax };
    }

    private BoundExpression BindAnonymousObject(ObjectLiteralExpressionSyntax syntax, List<LiteralMember> members)
    {
        if (members.Any(m => m.IsSpread))
        {
            // `{ ...p, x: 3 }` without a target: the result has the type of the spread operand
            if (members[0].IsSpread && members.Count(m => m.IsSpread) == 1)
            {
                var spreadSource = Speculatively(() => BindValue(members[0].Value!));
                if (spreadSource.Type?.StripNullable() is NamedTypeSymbol spreadType && spreadSource is not BoundBadExpression)
                    return BindSpreadObject(syntax, members, spreadType);
            }
            Report(Descriptors.FeatureNotSupported, syntax, "Spread in an object literal without a target type (only a single leading '...expr' of a record type is supported)");
            return Bad(syntax);
        }
        var names = new List<string>();
        var values = new List<BoundExpression>();
        foreach (var m in members)
        {
            var v = BindLiteralMemberValue(m, null);
            if (v is BoundBadExpression) return Bad(syntax);
            var w = v is BoundUnbound ? ConvertImplicitForInference(v, m) : v;
            if (w.HasErrors) return Bad(syntax);
            var t = WidenInferredType(w, m.NameNode, report: true);
            if (t.IsErrorType) return Bad(syntax);
            names.Add(m.Name);
            values.Add(ConvertImplicit(w, t, m.Value ?? m.NameNode));
        }
        var anon = _c.GetAnonymousRecord(names, values.Select(v => v.Type!).ToList());
        var ctor = anon.InstanceConstructors.First(c => c.Parameters.Length == names.Count);
        return new BoundObjectCreation(ctor, anon, values.ToImmutableArray(), ImmutableArray<BoundMemberInit>.Empty) { Syntax = syntax };
    }

    private BoundExpression ConvertImplicitForInference(BoundExpression v, LiteralMember m)
    {
        if (v is BoundUnbound u && !u.IsLambda) return u.BindToType(null);
        Report(Descriptors.CannotInferType, m.NameNode);
        return Bad(m.NameNode);
    }
}
