using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

/// <summary>An access path (local / parameter / this followed by member accesses) used as the key for flow narrowing.</summary>
internal sealed class NarrowPath(ImmutableArray<Symbol> parts) : IEquatable<NarrowPath>
{
    public static readonly Symbol ThisMarker = new ThisSymbol();

    private sealed class ThisSymbol : Symbol
    {
        public override SymbolKind Kind => SymbolKind.Parameter;
        public override string Name => "this";
    }

    public ImmutableArray<Symbol> Parts { get; } = parts;
    public Symbol Root => Parts[0];

    public NarrowPath Append(Symbol s)
    {
        return new(Parts.Add(s));
    }

    public bool StartsWith(NarrowPath prefix)
    {
        if (prefix.Parts.Length > Parts.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Parts.Length; i++)
        {
            if (!SameSymbol(Parts[i], prefix.Parts[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameSymbol(Symbol a, Symbol b)
    {
        return ReferenceEquals(a, b) || ReferenceEquals(a.OriginalDefinition, b.OriginalDefinition);
    }

    public bool Equals(NarrowPath? other)
    {
        if (other is null || other.Parts.Length != Parts.Length)
        {
            return false;
        }

        for (int i = 0; i < Parts.Length; i++)
        {
            if (!SameSymbol(Parts[i], other.Parts[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is NarrowPath p && Equals(p);
    }

    public override int GetHashCode()
    {
        int h = 17;
        foreach (Symbol p in Parts)
        {
            h = (h * 31) + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.OriginalDefinition);
        }

        return h;
    }
}

public sealed partial class Binder
{
    private ImmutableDictionary<NarrowPath, TypeSymbol> _narrow = [];
    private bool _isStaticContext;
    public bool UsesThis { get; private set; }
#pragma warning disable CS0414
#pragma warning restore CS0414

    internal readonly record struct Narrowings(ImmutableArray<(NarrowPath Path, TypeSymbol Type)> Items)
    {
        public static readonly Narrowings Empty = new([]);
        public Narrowings Concat(Narrowings o)
        {
            return Items.IsDefaultOrEmpty ? o : o.Items.IsDefaultOrEmpty ? this : new(Items.AddRange(o.Items));
        }
    }

    private static NarrowPath? PathOf(BoundExpression e)
    {
        return e switch
        {
            BoundLocal l => new NarrowPath([l.Local]),
            BoundParameter p => new NarrowPath([p.Parameter]),
            BoundThis => new NarrowPath([NarrowPath.ThisMarker]),
            BoundFieldAccess { Receiver: null } f => new NarrowPath([f.Field]),
            BoundFieldAccess f => PathOf(f.Receiver)?.Append(f.Field),
            BoundPropertyAccess { Arguments.IsDefaultOrEmpty: true, Receiver: null } p => new NarrowPath([p.Property]),
            BoundPropertyAccess { Arguments.IsDefaultOrEmpty: true } p => PathOf(p.Receiver)?.Append(p.Property),
            BoundConversion { Kind: ConversionKind.FromNullable or ConversionKind.UnionExtract or ConversionKind.ExplicitReference or ConversionKind.TypeOnly or ConversionKind.Unboxing } c => PathOf(c.Operand),
            _ => null,
        };
    }

    /// <summary>Wraps <paramref name="e"/> in a narrowing conversion when flow analysis knows a more specific type.</summary>
    internal BoundExpression ApplyNarrowing(BoundExpression e)
    {
        if (_narrow.IsEmpty || e.Type == null)
        {
            return e;
        }

        NarrowPath? path = PathOf(e);
        return path == null || !_narrow.TryGetValue(path, out TypeSymbol? narrowed)
            ? e
            : narrowed.Equals(e.Type) ? e : NarrowExpression(e, narrowed);
    }

    private static BoundExpression NarrowExpression(BoundExpression e, TypeSymbol narrowed)
    {
        TypeSymbol declared = e.Type!;
        // nullable removal
        if (declared is NullableTypeSymbol dn)
        {
            if (narrowed.Equals(dn.Underlying))
            {
                return new BoundConversion(e, ConversionKind.FromNullable, narrowed) { Syntax = e.Syntax };
            }

            BoundConversion unwrapped = new(e, ConversionKind.FromNullable, dn.Underlying) { Syntax = e.Syntax };
            return NarrowExpression(unwrapped, narrowed);
        }
        if (declared is NamedTypeSymbol { IsUnionType: true } u)
        {
            ImmutableArray<TypeSymbol> cases = u.UnionCases;
            for (int i = 0; i < cases.Length; i++)
            {
                if (cases[i].Equals(narrowed))
                {
                    return new BoundConversion(e, ConversionKind.UnionExtract, narrowed) { Syntax = e.Syntax, CaseIndex = i };
                }
            }
        }
        ConversionKind kind = narrowed.IsValueType && !declared.IsValueType ? ConversionKind.Unboxing : ConversionKind.ExplicitReference;
        return new BoundConversion(e, kind, narrowed) { Syntax = e.Syntax };
    }

    private static ImmutableDictionary<NarrowPath, TypeSymbol> ApplyNarrowings(ImmutableDictionary<NarrowPath, TypeSymbol> state, Narrowings n)
    {
        if (n.Items.IsDefaultOrEmpty)
        {
            return state;
        }

        foreach ((NarrowPath? p, TypeSymbol? t) in n.Items)
        {
            state = state.SetItem(p, t);
        }

        return state;
    }

    private static ImmutableDictionary<NarrowPath, TypeSymbol> RemovePathsStartingWith(ImmutableDictionary<NarrowPath, TypeSymbol> state, NarrowPath prefix)
    {
        foreach (NarrowPath? k in state.Keys.ToList())
        {
            if (k.StartsWith(prefix))
            {
                state = state.Remove(k);
            }
        }

        return state;
    }

    internal void InvalidateNarrowing(BoundExpression target)
    {
        if (_narrow.IsEmpty)
        {
            return;
        }

        NarrowPath? p = PathOf(target);
        if (p != null)
        {
            _narrow = RemovePathsStartingWith(_narrow, p);
        }
    }

    /// <summary>Computes narrowings that hold when <paramref name="cond"/> is true / false.</summary>
    internal static (Narrowings whenTrue, Narrowings whenFalse) GetNarrowing(BoundExpression cond)
    {
        switch (cond)
        {
            case BoundUnary { Kind: UnaryOperatorKind.Not } u:
                {
                    (Narrowings t, Narrowings f) = GetNarrowing(u.Operand);
                    return (f, t);
                }
            case BoundLogicalBinary { IsAnd: true } a:
                {
                    (Narrowings lt, Narrowings _) = GetNarrowing(a.Left);
                    (Narrowings rt, Narrowings _) = GetNarrowing(a.Right);
                    return (lt.Concat(rt), Narrowings.Empty);
                }
            case BoundLogicalBinary { IsAnd: false } o:
                {
                    (Narrowings _, Narrowings lf) = GetNarrowing(o.Left);
                    (Narrowings _, Narrowings rf) = GetNarrowing(o.Right);
                    return (Narrowings.Empty, lf.Concat(rf));
                }
            case BoundBinary { Kind: BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual } b:
                {
                    BoundExpression? subject = null;
                    if (b.Right is BoundLiteral { Value.IsNull: true })
                    {
                        subject = b.Left;
                    }
                    else if (b.Left is BoundLiteral { Value.IsNull: true })
                    {
                        subject = b.Right;
                    }

                    if (subject?.Type is NullableTypeSymbol n && PathOf(subject) is { } path)
                    {
                        Narrowings nn = new([(path, n.Underlying)]);
                        return b.Kind == BinaryOperatorKind.NotEqual ? (nn, Narrowings.Empty) : (Narrowings.Empty, nn);
                    }
                    return (Narrowings.Empty, Narrowings.Empty);
                }
            case BoundTypeTest tt when PathOf(tt.Operand) is { } tp:
                {
                    Narrowings whenTrue = new([(tp, tt.Target)]);
                    Narrowings whenFalse = Narrowings.Empty;
                    TypeSymbol? opType = tt.Operand.Type?.StripNullable();
                    // for `A | B | null` the false branch may still be null, so only a non-nullable union narrows to its remaining case
                    if (opType is NamedTypeSymbol { IsUnionType: true } u && !tt.OperandWasNullable)
                    {
                        List<TypeSymbol> rest = [.. u.UnionCases.Where(c => !c.Equals(tt.Target))];
                        if (rest.Count == 1)
                        {
                            whenFalse = new Narrowings([(tp, rest[0])]);
                        }
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
        if (_narrow.IsEmpty)
        {
            return;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (SyntaxNode n in syntax.DescendantNodesAndSelf())
        {
            ExpressionSyntax? target = n switch
            {
                AssignmentExpressionSyntax a => a.Left,
                PrefixUnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken } p => p.Operand,
                PostfixUnaryExpressionSyntax pf => pf.Operand,
                _ => null,
            };
            while (target is ParenthesizedExpressionSyntax pe)
            {
                target = pe.Expression;
            }

            switch (target)
            {
                case IdentifierNameSyntax id: _ = names.Add(id.Identifier.Text); break;
                case MemberAccessExpressionSyntax ma: _ = names.Add(ma.Name.Name); break;
            }
        }
        if (names.Count == 0)
        {
            return;
        }

        foreach (NarrowPath? k in _narrow.Keys.ToList())
        {
            if (k.Parts.Any(part => names.Contains(part.Name)))
            {
                _narrow = _narrow.Remove(k);
            }
        }
    }

    private void DropMutableNarrowings()
    {
        foreach (NarrowPath? k in _narrow.Keys.ToList())
        {
            bool immutable = k.Parts.Length == 1 && (k.Root is LocalSymbol { IsConst: true } || k.Root is ParameterSymbol);
            if (!immutable)
            {
                _narrow = _narrow.Remove(k);
            }
        }
    }
}
