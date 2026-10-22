using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed partial class Binder
{
    // ------------------------------------------------------------------ predefined names

    internal TypeSymbol? PredefinedType(string name)
    {
        switch (name)
        {
            case "number" or "double": return Special(SpecialType.Double);
            case "string": return Special(SpecialType.String);
            case "boolean": return Special(SpecialType.Boolean);
            case "object": return Special(SpecialType.Object);
            case "unknown": return new NullableTypeSymbol(Special(SpecialType.Object));
            case "never": return NeverTypeSymbol.Instance;
            case "bigint": return Special(SpecialType.BigInteger);
            case "int": return Special(SpecialType.Int32);
            case "long": return Special(SpecialType.Int64);
            case "short": return Special(SpecialType.Int16);
            case "byte": return Special(SpecialType.Byte);
            case "sbyte": return Special(SpecialType.SByte);
            case "uint": return Special(SpecialType.UInt32);
            case "ulong": return Special(SpecialType.UInt64);
            case "ushort": return Special(SpecialType.UInt16);
            case "float": return Special(SpecialType.Single);
            case "decimal": return Special(SpecialType.Decimal);
            case "nint": return Special(SpecialType.IntPtr);
            case "nuint": return Special(SpecialType.UIntPtr);
            case "char": return Special(SpecialType.Char);
        }
        return null;
    }

    private TypeSymbol? PredefinedGenericType(string name, ImmutableArray<TypeSymbol> args, SyntaxNode syntax)
    {
        NamedTypeSymbol? Construct(string metadataName, int arity)
        {
            if (args.Length != arity) return null;
            var t = _c.GetWellKnownType(metadataName);
            return t == null ? null : t.Construct(args);
        }

        switch (name)
        {
            case "Record": return Construct("System.Collections.Generic.Dictionary`2", 2);
            case "ReadonlyArray": return Construct("System.Collections.Generic.IReadOnlyList`1", 1);
            case "Iterable" or "IterableIterator" or "Generator": return Construct("System.Collections.Generic.IEnumerable`1", 1);
            case "AsyncIterable" or "AsyncIterableIterator" or "AsyncGenerator": return Construct("System.Collections.Generic.IAsyncEnumerable`1", 1);
            case "Array" when args.Length == 1: return _c.CreateArrayType(args[0]);
        }
        return null;
    }

    private static readonly HashSet<string> s_predefinedGenericNames =
        ["Record", "ReadonlyArray", "Iterable", "IterableIterator", "Generator", "AsyncIterable", "AsyncIterableIterator", "AsyncGenerator", "Array"];

    // ------------------------------------------------------------------ BindType

    public TypeSymbol BindType(TypeSyntax syntax, bool allowVoid = false)
    {
        var t = BindTypeCore(syntax, allowVoid);
        if (TypeMap != null) TypeMap[syntax] = t;
        return t;
    }

    private TypeSymbol BindTypeCore(TypeSyntax syntax, bool allowVoid)
    {
        switch (syntax)
        {
            case IdentifierNameSyntax id:
            {
                var text = id.Identifier.Text;
                if (id.Identifier.Kind == SyntaxKind.VoidKeyword)
                {
                    if (!allowVoid) { Report(Descriptors.VoidNotAllowed, syntax); return ErrorType; }
                    return VoidType;
                }
                if (id.Identifier.IsMissing) return ErrorType;
                return BindSimpleTypeName(text, ImmutableArray<TypeSymbol>.Empty, syntax, id.Identifier.Span);
            }
            case GenericNameSyntax g:
            {
                var args = BindTypeArguments(g.TypeArguments);
                return BindSimpleTypeName(g.Name, args, syntax, g.Identifier.Span);
            }
            case QualifiedNameSyntax q:
                return BindQualifiedTypeName(q);
            case ArrayTypeSyntax a:
            {
                var e = BindType(a.ElementType);
                if (e.IsVoid) { Report(Descriptors.VoidNotAllowed, a.ElementType); return ErrorType; }
                return _c.CreateArrayType(e);
            }
            case ReadonlyArrayTypeSyntax ra:
            {
                var inner = BindType(ra.Type);
                if (inner is ArrayTypeSymbol at)
                {
                    var rol = _c.GetWellKnownType("System.Collections.Generic.IReadOnlyList`1");
                    return rol == null ? ErrorType : rol.Construct(at.ElementType);
                }
                if (!inner.IsErrorType) Report(Descriptors.ReadonlyOnlyArrays, syntax);
                return inner;
            }
            case TupleTypeSyntax tt:
                return BindTupleType(tt);
            case UnionTypeSyntax u:
                return BindUnionType(u);
            case NullableTypeSyntax n:
            {
                var e = BindType(n.ElementType);
                return MakeNullable(e, n);
            }
            case FunctionTypeSyntax f:
                return BindFunctionType(f);
            case ObjectTypeSyntax:
                Report(Descriptors.ObjectTypeLiteralNotAllowed, syntax);
                return ErrorType;
            case LiteralTypeSyntax l:
                return BindLiteralType(l);
            case ParenthesizedTypeSyntax p:
                return BindType(p.Type, allowVoid);
            case IntersectionTypeSyntax:
                Report(Descriptors.IntersectionNotAllowed, syntax);
                return ErrorType;
            case SpecialConstraintTypeSyntax or NewConstraintTypeSyntax:
                Report(Descriptors.ConstraintNotAType, syntax);
                return ErrorType;
            case MissingTypeSyntax:
                return ErrorType;
        }
        return ErrorType;
    }

    private TypeSymbol MakeNullable(TypeSymbol e, SyntaxNode? at)
    {
        if (e.IsErrorType || e is NullableTypeSymbol) return e;
        if (e.IsVoid || e.IsNullType || e.IsNeverType)
        {
            Report(Descriptors.CannotBeNullable, at, e.ToDisplayString());
            return ErrorType;
        }
        return new NullableTypeSymbol(e);
    }

    private ImmutableArray<TypeSymbol> BindTypeArguments(TypeArgumentListSyntax list, bool allowVoid = false)
    {
        var b = ImmutableArray.CreateBuilder<TypeSymbol>(list.Arguments.Count);
        foreach (var a in list.Arguments) b.Add(BindType(a, allowVoid));
        return b.MoveToImmutable();
    }

    private TypeSymbol BindLiteralType(LiteralTypeSyntax l)
    {
        switch (l.Literal.Kind)
        {
            case SyntaxKind.NullLiteralExpression: return NullTypeSymbol.Instance;
            case SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression: return BoolType;
            case SyntaxKind.NumericLiteralExpression: return DoubleType;
            case SyntaxKind.StringLiteralExpression:
                Report(Descriptors.StringLiteralTypeNotAllowed, l);
                return ErrorType;
        }
        return ErrorType;
    }

    private TypeSymbol BindFunctionType(FunctionTypeSyntax f)
    {
        if (f.TypeParameters != null)
        {
            Report(Descriptors.FeatureNotSupported, f.TypeParameters, "Generic function types");
            return ErrorType;
        }
        var ps = f.Parameters.Parameters;
        var args = ImmutableArray.CreateBuilder<TypeSymbol>();
        foreach (var p in ps)
        {
            if (p.TypeAnnotation == null)
            {
                Report(Descriptors.TypeAnnotationRequired, p);
                args.Add(ErrorType);
            }
            else
            {
                var pt = BindType(p.TypeAnnotation.Type);
                if (p.QuestionToken != null) pt = MakeNullable(pt, p);
                args.Add(pt);
            }
        }
        var ret = BindType(f.ReturnType, allowVoid: true);
        return MakeDelegateType(args.ToImmutable(), ret, f);
    }

    internal TypeSymbol MakeDelegateType(ImmutableArray<TypeSymbol> parameters, TypeSymbol returnType, SyntaxNode? at)
    {
        if (parameters.Length > 16)
        {
            Report(Descriptors.TooManyParameters, at);
            return ErrorType;
        }
        if (returnType.IsVoid)
        {
            if (parameters.Length == 0) return _c.GetWellKnownType("System.Action") ?? ErrorType;
            var a = _c.GetWellKnownType("System.Action`" + parameters.Length);
            return a == null ? ErrorType : a.Construct(parameters);
        }
        var f = _c.GetWellKnownType("System.Func`" + (parameters.Length + 1));
        return f == null ? ErrorType : f.Construct(parameters.Add(returnType));
    }

    private TypeSymbol BindTupleType(TupleTypeSyntax tt)
    {
        var args = ImmutableArray.CreateBuilder<TypeSymbol>();
        var names = ImmutableArray.CreateBuilder<string?>();
        var any = false;
        foreach (var e in tt.Elements)
        {
            args.Add(BindType(e.Type));
            var n = e.NameToken?.Text;
            names.Add(n);
            if (n != null) any = true;
        }
        return MakeTuple(args.ToImmutable(), any ? names.ToImmutable() : default, tt);
    }

    internal TypeSymbol MakeTuple(ImmutableArray<TypeSymbol> elements, ImmutableArray<string?> names, SyntaxNode? at)
    {
        if (elements.Length is 0 or > 7)
        {
            Report(Descriptors.TupleSize, at);
            return ErrorType;
        }
        var vt = _c.GetWellKnownType("System.ValueTuple`" + elements.Length);
        if (vt == null) return ErrorType;
        return new ConstructedNamedType(vt, elements, names);
    }

    private TypeSymbol BindUnionType(UnionTypeSyntax u)
    {
        var cases = new List<TypeSymbol>();
        var hasNull = false;
        var allStringLiterals = u.Types.Count > 0;
        foreach (var t in u.Types)
        {
            if (t is not LiteralTypeSyntax { Literal.Kind: SyntaxKind.StringLiteralExpression }) allStringLiterals = false;
        }
        if (allStringLiterals)
        {
            Report(Descriptors.StringLiteralUnionNeedsAlias, u);
            return ErrorType;
        }
        void Add(TypeSymbol t)
        {
            if (t.IsNullType) { hasNull = true; return; }
            if (t is NullableTypeSymbol n) { hasNull = true; Add(n.Underlying); return; }
            if (t is NamedTypeSymbol { IsUnionType: true } un && !t.IsErrorType)
            {
                foreach (var c in un.UnionCases) Add(c);
                return;
            }
            foreach (var e in cases) if (e.Equals(t)) return;
            cases.Add(t);
        }
        foreach (var t in u.Types)
        {
            var bound = BindType(t);
            if (bound.IsErrorType) return ErrorType;
            if (bound.IsVoid) { Report(Descriptors.VoidNotAllowed, t); return ErrorType; }
            Add(bound);
        }
        if (cases.Count == 0) return NullTypeSymbol.Instance;
        TypeSymbol result = cases.Count == 1 ? cases[0] : MakeAnonymousUnion(cases, u);
        return hasNull ? MakeNullable(result, u) : result;
    }

    /// <summary>Creates (canonicalizing the case order) an inline union type <c>Caladesi.Runtime.Union&lt;...&gt;</c>.</summary>
    internal TypeSymbol MakeAnonymousUnion(List<TypeSymbol> cases, SyntaxNode? at)
    {
        if (cases.Count > 8)
        {
            Report(Descriptors.UnionTooLarge, at);
            return ErrorType;
        }
        cases.Sort((a, b) => string.CompareOrdinal(a.ToMetadataKey(), b.ToMetadataKey()));
        var def = _c.GetWellKnownType("Caladesi.Runtime.Union`" + cases.Count);
        if (def == null)
        {
            Report(Descriptors.RuntimeMissing, at, "Caladesi.Runtime.Union`" + cases.Count);
            return ErrorType;
        }
        return def.Construct(cases.ToImmutableArray());
    }

    // ------------------------------------------------------------------ names

    private TypeSymbol BindSimpleTypeName(string name, ImmutableArray<TypeSymbol> args, SyntaxNode syntax, Text.TextSpan span)
    {
        if (args.IsDefault) args = ImmutableArray<TypeSymbol>.Empty;
        if (args.Length == 0 && _typeParams.TryGetValue(name, out var tp)) { Record(syntax, tp); return tp; }

        var candidates = _file.Scope.Lookup(name);
        if (candidates.Length > 0)
        {
            TypeSymbol? found = null;
            var sawWrongArity = false;
            var sawNonType = false;
            foreach (var c in candidates)
            {
                switch (c)
                {
                    case NamedTypeSymbol nt when nt.OwnArity == args.Length:
                        found = args.Length == 0 ? NamedOrNullable(nt) : ConstructNamed(nt, args, syntax);
                        break;
                    case NamedTypeSymbol:
                        sawWrongArity = true;
                        break;
                    case TypeAliasSymbol alias when alias.TypeParameters.Length == args.Length:
                        found = ExpandAlias(alias, args);
                        break;
                    case TypeAliasSymbol:
                        sawWrongArity = true;
                        break;
                    default:
                        sawNonType = true;
                        break;
                }
                if (found != null) break;
            }
            if (found != null)
            {
                Record(syntax, (found as NullableTypeSymbol)?.Underlying ?? found);
                return found;
            }
            if (sawWrongArity)
            {
                var generic = candidates.OfType<NamedTypeSymbol>().FirstOrDefault();
                if (args.Length == 0) Report(Descriptors.TypeRequiresArguments, span, name, generic?.OwnArity ?? 1);
                else Report(Descriptors.TypeNotGeneric, span, name, args.Length);
                return ErrorType;
            }
            if (sawNonType)
            {
                Report(Descriptors.NotAType, span, name);
                return ErrorType;
            }
        }

        if (args.Length == 0)
        {
            var p = PredefinedType(name);
            if (p != null) return p;
            if (name == "any") { Report(Descriptors.AnyNotAllowed, span); return ErrorType; }
            if (name == "undefined") { Report(Descriptors.UndefinedNotAllowed, span); return ErrorType; }
        }
        else if (s_predefinedGenericNames.Contains(name))
        {
            var pg = PredefinedGenericType(name, args, syntax);
            if (pg != null) return pg;
        }
        Report(Descriptors.NameNotFound, span, name);
        return ErrorType;
    }

    /// <summary>A named union that includes null (<c>type T = A | B | null</c>) is referenced as its nullable form.</summary>
    private TypeSymbol NamedOrNullable(NamedTypeSymbol nt) =>
        nt is SourceUnionSymbol { IncludesNull: true } ? new NullableTypeSymbol(nt) : nt;

    private TypeSymbol ConstructNamed(NamedTypeSymbol def, ImmutableArray<TypeSymbol> args, SyntaxNode syntax)
    {
        foreach (var a in args)
            if (a.IsErrorType) return ErrorType;
        // nested generic types' arguments include the outer type's; only top-level generic types are constructed from syntax
        return def.Construct(args);
    }

    private TypeSymbol ExpandAlias(TypeAliasSymbol alias, ImmutableArray<TypeSymbol> args)
    {
        var target = alias.Target;
        if (args.Length == 0 || target.IsErrorType) return target;
        var map = new TypeMap(alias.TypeParameters, args);
        return map.Substitute(target);
    }

    private TypeSymbol BindQualifiedTypeName(QualifiedNameSyntax q)
    {
        var left = ResolveNamespaceOrType(q.Left);
        if (left == null) return ErrorType;
        var right = q.Right;
        var arity = right is GenericNameSyntax gn ? gn.TypeArguments.Arguments.Count : 0;
        var args = right is GenericNameSyntax g2 ? BindTypeArguments(g2.TypeArguments) : ImmutableArray<TypeSymbol>.Empty;
        NamedTypeSymbol? found = null;
        switch (left)
        {
            case NamespaceSymbol ns:
                found = ns.GetTypeMember(right.Name, arity);
                break;
            case NamedTypeSymbol nt:
                found = nt.GetMembers(right.Name).OfType<NamedTypeSymbol>().FirstOrDefault(x => x.OwnArity == arity);
                break;
            case FileModuleSymbol fm:
            {
                var t = fm.File.Scope.GetExports(right.Name).OfType<NamedTypeSymbol>().FirstOrDefault(x => x.OwnArity == arity);
                found = t;
                if (found == null)
                {
                    var al = fm.File.Scope.GetExports(right.Name).OfType<TypeAliasSymbol>().FirstOrDefault(x => x.TypeParameters.Length == arity);
                    if (al != null) return ExpandAlias(al, args);
                }
                break;
            }
        }
        if (found == null)
        {
            Report(Descriptors.NameNotFound, right.Span, q.Left.ToFullString().Trim() + "." + right.Name);
            return ErrorType;
        }
        Record(q, found);
        return arity == 0 ? NamedOrNullable(found) : ConstructNamed(found, args, q);
    }

    /// <summary>Resolves a name used as the qualifier of a type name: a namespace, a type, or an imported module alias.</summary>
    internal Symbol? ResolveNamespaceOrType(NameSyntax name)
    {
        switch (name)
        {
            case IdentifierNameSyntax id:
            {
                var text = id.Identifier.Text;
                if (_typeParams.TryGetValue(text, out var tp)) return tp;
                var cands = _file.Scope.Lookup(text);
                foreach (var c in cands)
                    if (c is NamespaceSymbol or FileModuleSymbol) { Record(name, c); return c; }
                foreach (var c in cands)
                    if (c is NamedTypeSymbol nt && nt.OwnArity == 0) { Record(name, c); return c; }
                Report(Descriptors.NameNotFound, id.Identifier.Span, text);
                return null;
            }
            case QualifiedNameSyntax q:
            {
                var left = ResolveNamespaceOrType(q.Left);
                if (left == null) return null;
                var rightName = q.Right.Name;
                switch (left)
                {
                    case NamespaceSymbol ns:
                    {
                        var child = ns.GetNamespaceMember(rightName);
                        if (child != null) { Record(name, child); return child; }
                        var t = ns.GetTypeMember(rightName, 0);
                        if (t != null) { Record(name, t); return t; }
                        break;
                    }
                    case NamedTypeSymbol nt:
                    {
                        var t = nt.GetMembers(rightName).OfType<NamedTypeSymbol>().FirstOrDefault();
                        if (t != null) { Record(name, t); return t; }
                        break;
                    }
                }
                Report(Descriptors.NameNotFound, q.Right.Span, rightName);
                return null;
            }
            case GenericNameSyntax g:
            {
                var t = BindType(g);
                return t as NamedTypeSymbol;
            }
        }
        return null;
    }

    private void Record(SyntaxNode syntax, Symbol symbol)
    {
        if (SymbolMap != null) SymbolMap[syntax] = symbol;
    }

    // ------------------------------------------------------------------ constraints

    internal TypeParameterConstraints BindConstraints(TypeParameterSyntax syntax, TypeParameterSymbol symbol)
    {
        if (syntax.Constraint == null) return TypeParameterConstraints.None;
        bool cls = false, st = false, nw = false, notnull = false;
        var types = ImmutableArray.CreateBuilder<TypeSymbol>();

        void Visit(TypeSyntax t)
        {
            switch (t)
            {
                case IntersectionTypeSyntax inter:
                    foreach (var x in inter.Types) Visit(x);
                    break;
                case ParenthesizedTypeSyntax p:
                    Visit(p.Type);
                    break;
                case SpecialConstraintTypeSyntax sc:
                    if (sc.Keyword.Kind == SyntaxKind.ClassKeyword) cls = true; else st = true;
                    break;
                case NewConstraintTypeSyntax:
                    nw = true;
                    break;
                case IdentifierNameSyntax { Name: "notnull" }:
                    notnull = true;
                    break;
                default:
                {
                    var bound = BindType(t);
                    if (!bound.IsErrorType) types.Add(bound);
                    break;
                }
            }
        }
        Visit(syntax.Constraint);
        return new TypeParameterConstraints(cls, st, nw, notnull, types.ToImmutable());
    }

    // ------------------------------------------------------------------ parameters

    internal ImmutableArray<ParameterSymbol> BindParameters(Symbol owner, ParameterListSyntax list)
    {
        var b = ImmutableArray.CreateBuilder<ParameterSymbol>(list.Parameters.Count);
        var names = new HashSet<string>();
        var seenOptional = false;
        for (var i = 0; i < list.Parameters.Count; i++)
        {
            var p = list.Parameters[i];
            var name = p.Identifier?.Text ?? ("<p" + i + ">");
            if (p.Identifier != null && !names.Add(name)) Report(Descriptors.DuplicateParameter, p.Identifier, name);
            var isThis = p.Modifiers.Any(m => m.Kind == SyntaxKind.ThisKeyword);
            if (isThis && i != 0) Report(Descriptors.ThisParameterPosition, p);
            var isParams = p.DotDotDotToken != null;
            TypeSymbol type;
            ConstantValue? def = null;
            var optional = false;
            if (p.TypeAnnotation != null)
            {
                type = BindType(p.TypeAnnotation.Type);
            }
            else if (p.Initializer != null)
            {
                type = InferVariableType(p.Initializer.Value);
            }
            else
            {
                Report(Descriptors.ParameterTypeRequired, p, name);
                type = ErrorType;
            }
            if (p.QuestionToken != null)
            {
                type = MakeNullable(type, p);
                optional = true;
                def = ConstantValue.Null;
            }
            if (p.Initializer != null)
            {
                optional = true;
                def = TryEvaluateConstant(p.Initializer.Value, type);
                if (def == null)
                {
                    Report(Descriptors.DefaultMustBeConstant, p.Initializer.Value);
                    def = type.IsValueType ? new ConstantValue(null) : ConstantValue.Null;
                }
            }
            if (isParams)
            {
                if (type is not ArrayTypeSymbol && !type.IsErrorType)
                {
                    Report(Descriptors.RestMustBeArray, p);
                    type = ErrorType;
                }
                if (i != list.Parameters.Count - 1) Report(Descriptors.RestMustBeLast, p);
            }
            else if (optional) seenOptional = true;
            else if (seenOptional && !isParams) Report(Descriptors.RequiredAfterOptional, p, name);

            var ps = new ParameterSymbol(name, type, i, owner, RefKind.None, isParams, optional ? def : null, optional, isThis)
            {
                DeclaringSyntax = p,
                IsFromSourceParam = true,
            };
            b.Add(ps);
        }
        return b.MoveToImmutable();
    }

    internal TypeSymbol AdjustDeclaredReturnType(SourceMethodSymbol method, TypeSymbol t, TypeSyntax syntax)
    {
        if (t.IsErrorType) return t;
        if (method.IsAsync && !method.IsIterator)
        {
            if (!IsTaskLike(t)) { Report(Descriptors.MustReturnTask, syntax); return ErrorType; }
        }
        else if (method.IsIterator)
        {
            if (!IsIteratorReturnType(t, method.IsAsync)) { Report(Descriptors.MustReturnEnumerable, syntax); return ErrorType; }
        }
        return t;
    }

    internal bool IsTaskLike(TypeSymbol t) =>
        t is NamedTypeSymbol n && n.OriginalDefinition.SpecialType is SpecialType.Task or SpecialType.TaskT or SpecialType.ValueTask or SpecialType.ValueTaskT;

    internal bool IsIteratorReturnType(TypeSymbol t, bool isAsync)
    {
        if (t is not NamedTypeSymbol n) return false;
        var def = n.OriginalDefinition.ToMetadataString();
        return isAsync
            ? def is "System.Collections.Generic.IAsyncEnumerable`1" or "System.Collections.Generic.IAsyncEnumerator`1"
            : def is "System.Collections.Generic.IEnumerable`1" or "System.Collections.Generic.IEnumerator`1" or "System.Collections.IEnumerable" or "System.Collections.IEnumerator";
    }

    // ------------------------------------------------------------------ inference of declared types from initializers

    public TypeSymbol InferVariableType(ExpressionSyntax initializer)
    {
        var inferred = Speculatively(() =>
        {
            var bound = BindExpression(initializer);
            if (bound is BoundUnbound u && HasNaturalType(u)) bound = u.BindToType(null);
            return WidenInferredType(bound, initializer, report: false);
        });
        if (inferred.IsErrorType)
        {
            // the speculative pass hides diagnostics; bind again so the user learns why the type could not be inferred
            var bound = BindExpression(initializer);
            if (bound is BoundUnbound u && HasNaturalType(u)) bound = u.BindToType(null);
            WidenInferredType(bound, initializer, report: true);
        }
        return inferred;
    }

    /// <summary>The type a variable gets when declared without annotation (after binding the initializer).</summary>
    internal TypeSymbol WidenInferredType(BoundExpression e, SyntaxNode at, bool report)
    {
        if (e is BoundUnbound u)
        {
            if (u.Type != null) return u.Type;
            if (report) Report(Descriptors.CannotInferType, at);
            return ErrorType;
        }
        var t = e.Type;
        if (t == null || e is BoundMethodGroup or BoundTypeExpression or BoundNamespaceExpression or BoundModuleExpression)
        {
            if (report) Report(Descriptors.CannotInferType, at);
            return ErrorType;
        }
        if (t.IsNullType)
        {
            if (report) Report(Descriptors.CannotInferNull, at);
            return ErrorType;
        }
        if (t.IsVoid)
        {
            if (report) Report(Descriptors.VoidNotAllowed, at);
            return ErrorType;
        }
        if (t.IsNeverType) return ObjectType;
        return t;
    }

    internal ConstantValue? EvaluateEnumInitializer(SourceEnumSymbol enumType, ExpressionSyntax expr, SourceFieldSymbol[] previous)
    {
        PushScope(ScopeKind.Block);
        try
        {
            foreach (var p in previous) _scope.Symbols[p.Name] = p;
            return TryEvaluateConstant(expr, IntType);
        }
        finally { PopScope(); }
    }

    /// <summary>Binds <paramref name="expr"/> against <paramref name="target"/> and returns its constant value, if any.</summary>
    public ConstantValue? TryEvaluateConstant(ExpressionSyntax expr, TypeSymbol target)
    {
        return Speculatively(() =>
        {
            var bound = BindExpression(expr, target);
            if (bound.HasErrors) return (ConstantValue?)null;
            var converted = ConvertImplicit(bound, target, expr, report: false);
            if (converted.HasErrors) return null;
            return converted.ConstantValue;
        });
    }
}

internal static class TypeKeyExtensions
{
    /// <summary>A stable ordering key for canonicalizing union case order.</summary>
    public static string ToMetadataKey(this TypeSymbol t) => t.ToDisplayString();
}
