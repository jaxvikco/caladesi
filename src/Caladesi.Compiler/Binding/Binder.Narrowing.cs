using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

/// <summary>An access path (local / parameter / this followed by member accesses) used as the key for flow narrowing.</summary>
internal sealed class NarrowPath : IEquatable<NarrowPath>
{
    public static readonly Symbol ThisMarker = new ThisSymbol();

    private sealed class ThisSymbol : Symbol
    {
        public override SymbolKind Kind => SymbolKind.Parameter;
        public override string Name => "this";
    }

    public NarrowPath(ImmutableArray<Symbol> parts) => Parts = parts;
    public ImmutableArray<Symbol> Parts { get; }
    public Symbol Root => Parts[0];

    public NarrowPath Append(Symbol s) => new(Parts.Add(s));

    public bool StartsWith(NarrowPath prefix)
    {
        if (prefix.Parts.Length > Parts.Length) return false;
        for (var i = 0; i < prefix.Parts.Length; i++)
            if (!SameSymbol(Parts[i], prefix.Parts[i])) return false;
        return true;
    }

    private static bool SameSymbol(Symbol a, Symbol b) =>
        ReferenceEquals(a, b) || ReferenceEquals(a.OriginalDefinition, b.OriginalDefinition);

    public bool Equals(NarrowPath? other)
    {
        if (other is null || other.Parts.Length != Parts.Length) return false;
        for (var i = 0; i < Parts.Length; i++)
            if (!SameSymbol(Parts[i], other.Parts[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is NarrowPath p && Equals(p);

    public override int GetHashCode()
    {
        var h = 17;
        foreach (var p in Parts) h = h * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.OriginalDefinition);
        return h;
    }
}

public sealed partial class Binder
{
    private ImmutableDictionary<NarrowPath, TypeSymbol> _narrow = ImmutableDictionary<NarrowPath, TypeSymbol>.Empty;
    private bool _isStaticContext;
    public bool UsesThis { get => _usesThis; }
#pragma warning disable CS0414
    private bool _usesThis;
#pragma warning restore CS0414

    internal readonly record struct Narrowings(ImmutableArray<(NarrowPath Path, TypeSymbol Type)> Items)
    {
        public static readonly Narrowings Empty = new(ImmutableArray<(NarrowPath, TypeSymbol)>.Empty);
        public Narrowings Concat(Narrowings o) =>
            Items.IsDefaultOrEmpty ? o : o.Items.IsDefaultOrEmpty ? this : new(Items.AddRange(o.Items));
    }

    private static NarrowPath? PathOf(BoundExpression e)
    {
        switch (e)
        {
            case BoundLocal l: return new NarrowPath([l.Local]);
            case BoundParameter p: return new NarrowPath([p.Parameter]);
            case BoundThis: return new NarrowPath([NarrowPath.ThisMarker]);
            case BoundFieldAccess { Receiver: null } f: return new NarrowPath([f.Field]);
            case BoundFieldAccess f:
                return PathOf(f.Receiver!)?.Append(f.Field);
            case BoundPropertyAccess { Arguments.IsDefaultOrEmpty: true, Receiver: null } p: return new NarrowPath([p.Property]);
            case BoundPropertyAccess { Arguments.IsDefaultOrEmpty: true } p:
                return PathOf(p.Receiver!)?.Append(p.Property);
            case BoundConversion { Kind: ConversionKind.FromNullable or ConversionKind.UnionExtract or ConversionKind.ExplicitReference or ConversionKind.TypeOnly or ConversionKind.Unboxing } c:
                return PathOf(c.Operand);
        }
        return null;
    }

    /// <summary>Wraps <paramref name="e"/> in a narrowing conversion when flow analysis knows a more specific type.</summary>
    internal BoundExpression ApplyNarrowing(BoundExpression e)
    {
        if (_narrow.IsEmpty || e.Type == null) return e;
        var path = PathOf(e);
        if (path == null || !_narrow.TryGetValue(path, out var narrowed)) return e;
        if (narrowed.Equals(e.Type)) return e;
        return NarrowExpression(e, narrowed);
    }

    private BoundExpression NarrowExpression(BoundExpression e, TypeSymbol narrowed)
    {
        var declared = e.Type!;
        // nullable removal
        if (declared is NullableTypeSymbol dn)
        {
            if (narrowed.Equals(dn.Underlying))
                return new BoundConversion(e, ConversionKind.FromNullable, narrowed) { Syntax = e.Syntax };
            var unwrapped = new BoundConversion(e, ConversionKind.FromNullable, dn.Underlying) { Syntax = e.Syntax };
            return NarrowExpression(unwrapped, narrowed);
        }
        if (declared is NamedTypeSymbol { IsUnionType: true } u)
        {
            var cases = u.UnionCases;
            for (var i = 0; i < cases.Length; i++)
                if (cases[i].Equals(narrowed))
                    return new BoundConversion(e, ConversionKind.UnionExtract, narrowed) { Syntax = e.Syntax, CaseIndex = i };
        }
        var kind = narrowed.IsValueType && !declared.IsValueType ? ConversionKind.Unboxing : ConversionKind.ExplicitReference;
        return new BoundConversion(e, kind, narrowed) { Syntax = e.Syntax };
    }

    private ImmutableDictionary<NarrowPath, TypeSymbol> ApplyNarrowings(ImmutableDictionary<NarrowPath, TypeSymbol> state, Narrowings n)
    {
        if (n.Items.IsDefaultOrEmpty) return state;
        foreach (var (p, t) in n.Items) state = state.SetItem(p, t);
        return state;
    }

    private ImmutableDictionary<NarrowPath, TypeSymbol> RemovePathsStartingWith(ImmutableDictionary<NarrowPath, TypeSymbol> state, NarrowPath prefix)
    {
        foreach (var k in state.Keys.ToList())
            if (k.StartsWith(prefix)) state = state.Remove(k);
        return state;
    }

    internal void InvalidateNarrowing(BoundExpression target)
    {
        if (_narrow.IsEmpty) return;
        var p = PathOf(target);
        if (p != null) _narrow = RemovePathsStartingWith(_narrow, p);
    }

    /// <summary>Computes narrowings that hold when <paramref name="cond"/> is true / false.</summary>
    internal (Narrowings whenTrue, Narrowings whenFalse) GetNarrowing(BoundExpression cond)
    {
        switch (cond)
        {
            case BoundUnary { Kind: UnaryOperatorKind.Not } u:
            {
                var (t, f) = GetNarrowing(u.Operand);
                return (f, t);
            }
            case BoundLogicalBinary { IsAnd: true } a:
            {
                var (lt, _) = GetNarrowing(a.Left);
                var (rt, _) = GetNarrowing(a.Right);
                return (lt.Concat(rt), Narrowings.Empty);
            }
            case BoundLogicalBinary { IsAnd: false } o:
            {
                var (_, lf) = GetNarrowing(o.Left);
                var (_, rf) = GetNarrowing(o.Right);
                return (Narrowings.Empty, lf.Concat(rf));
            }
            case BoundBinary { Kind: BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual } b:
            {
                BoundExpression? subject = null;
                if (b.Right is BoundLiteral { Value.IsNull: true }) subject = b.Left;
                else if (b.Left is BoundLiteral { Value.IsNull: true }) subject = b.Right;
                if (subject?.Type is NullableTypeSymbol n && PathOf(subject) is { } path)
                {
                    var nn = new Narrowings([(path, n.Underlying)]);
                    return b.Kind == BinaryOperatorKind.NotEqual ? (nn, Narrowings.Empty) : (Narrowings.Empty, nn);
                }
                return (Narrowings.Empty, Narrowings.Empty);
            }
            case BoundTypeTest tt when PathOf(tt.Operand) is { } tp:
            {
                var whenTrue = new Narrowings([(tp, tt.Target)]);
                var whenFalse = Narrowings.Empty;
                var opType = tt.Operand.Type?.StripNullable();
                if (opType is NamedTypeSymbol { IsUnionType: true } u)
                {
                    var rest = u.UnionCases.Where(c => !c.Equals(tt.Target)).ToList();
                    if (rest.Count == 1) whenFalse = new Narrowings([(tp, rest[0])]);
                }
                return (whenTrue, whenFalse);
            }
            case BoundConversion { Kind: ConversionKind.Identity or ConversionKind.TypeOnly } c when c.Operand.Type?.SpecialType == SpecialType.Boolean:
                return GetNarrowing(c.Operand);
        }
        return (Narrowings.Empty, Narrowings.Empty);
    }

    /// <summary>Removes narrowings invalidated by assignments inside <paramref name="syntax"/> (loop bodies, lambdas).</summary>
    private void InvalidateAssignedIn(SyntaxNode syntax)
    {
        if (_narrow.IsEmpty) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in syntax.DescendantNodesAndSelf())
        {
            ExpressionSyntax? target = n switch
            {
                AssignmentExpressionSyntax a => a.Left,
                PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken } p => p.Operand,
                PostfixUnaryExpressionSyntax pf => pf.Operand,
                _ => null,
            };
            while (target is ParenthesizedExpressionSyntax pe) target = pe.Expression;
            switch (target)
            {
                case IdentifierNameSyntax id: names.Add(id.Identifier.Text); break;
                case MemberAccessExpressionSyntax ma: names.Add(ma.Name.Name); break;
            }
        }
        if (names.Count == 0) return;
        foreach (var k in _narrow.Keys.ToList())
            if (k.Parts.Any(part => names.Contains(part.Name))) _narrow = _narrow.Remove(k);
    }

    private void DropMutableNarrowings()
    {
        foreach (var k in _narrow.Keys.ToList())
        {
            var immutable = k.Parts.Length == 1 && (k.Root is LocalSymbol { IsConst: true } || k.Root is ParameterSymbol);
            if (!immutable) _narrow = _narrow.Remove(k);
        }
    }
}
