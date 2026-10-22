using System.Collections.Immutable;
using System.Text;

namespace Caladesi.Compiler.Symbols;

public enum TypeKind { Class, Struct, Interface, Enum, Delegate, Array, TypeParameter, Error, Null, Void, Never, Nullable }

public enum VarianceKind { None, In, Out }

public abstract class TypeSymbol : Symbol
{
    public abstract TypeKind TypeKind { get; }
    public virtual SpecialType SpecialType => SpecialType.None;
    public abstract bool IsReferenceType { get; }
    public abstract bool IsValueType { get; }
    public virtual NamedTypeSymbol? BaseType => null;
    public virtual ImmutableArray<NamedTypeSymbol> Interfaces => [];
    public virtual ImmutableArray<Symbol> GetMembers()
    {
        return [];
    }

    public virtual ImmutableArray<Symbol> GetMembers(string name)
    {
        ImmutableArray<Symbol> all = GetMembers();
        ImmutableArray<Symbol>.Builder? b = null;
        foreach (Symbol m in all)
        {
            if (m.Name == name)
            {
                (b ??= ImmutableArray.CreateBuilder<Symbol>()).Add(m);
            }
        }

        return b?.ToImmutable() ?? [];
    }

    public bool IsErrorType => TypeKind == TypeKind.Error;
    public bool IsVoid => SpecialType == SpecialType.Void;
    public bool IsNullType => TypeKind == TypeKind.Null;
    public bool IsNeverType => TypeKind == TypeKind.Never;

    /// <summary>All interfaces implemented by this type (including inherited), de-duplicated.</summary>
    public ImmutableArray<NamedTypeSymbol> AllInterfaces
    {
        get
        {
            if (!field.IsDefault)
            {
                return field;
            }

            List<NamedTypeSymbol> set = [];
            void Add(NamedTypeSymbol i)
            {
                foreach (NamedTypeSymbol e in set)
                {
                    if (e.Equals(i))
                    {
                        return;
                    }
                }

                set.Add(i);
                foreach (NamedTypeSymbol b in i.Interfaces)
                {
                    Add(b);
                }
            }
            for (TypeSymbol? t = this; t != null; t = t.BaseType)
            {
                foreach (NamedTypeSymbol i in t.Interfaces)
                {
                    Add(i);
                }
            }

            if (this is TypeParameterSymbol tp)
            {
                foreach (TypeSymbol c in tp.ConstraintTypes)
                {
                    if (c is NamedTypeSymbol { TypeKind: TypeKind.Interface } ci)
                    {
                        Add(ci);
                    }
                    else
                    {
                        foreach (NamedTypeSymbol i in c.AllInterfaces)
                        {
                            Add(i);
                        }
                    }
                }
            }

            return field = [.. set];
        }
    }

    /// <summary>Full display name using Caladesi type names.</summary>
    public string ToDisplayString()
    {
        return TypeDisplay.Format(this);
    }

    public virtual string ToMetadataString()
    {
        return Name;
    }

    public override string ToString()
    {
        return ToDisplayString();
    }

    public abstract bool Equals(TypeSymbol? other);
    public override bool Equals(object? obj)
    {
        return obj is TypeSymbol t && Equals(t);
    }

    public override int GetHashCode()
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    public static bool operator ==(TypeSymbol? a, TypeSymbol? b)
    {
        return ReferenceEquals(a, b) || (a is not null && b is not null && a.Equals(b));
    }

    public static bool operator !=(TypeSymbol? a, TypeSymbol? b)
    {
        return !(a == b);
    }

    /// <summary>Strips a nullable annotation wrapper, if any.</summary>
    public TypeSymbol StripNullable()
    {
        return this is NullableTypeSymbol n ? n.Underlying : this;
    }

    public bool IsNullableWrapper => this is NullableTypeSymbol;
}

/// <summary>Produced for unresolvable types; suppresses cascading diagnostics.</summary>
public sealed class ErrorTypeSymbol(string name) : NamedTypeSymbol
{
    public static readonly ErrorTypeSymbol Instance = new("<error>");
    private readonly string _name = name;

    public override string Name => _name;
    public override TypeKind TypeKind => TypeKind.Error;
    public override bool IsReferenceType => true;
    public override bool IsValueType => false;
    public override SymbolKind Kind => SymbolKind.ErrorType;
    public override ImmutableArray<TypeParameterSymbol> TypeParameters => [];
    public override ImmutableArray<TypeSymbol> TypeArguments => [];
    public override bool Equals(TypeSymbol? other)
    {
        return other is ErrorTypeSymbol;
    }

    public override int GetHashCode()
    {
        return 17;
    }

    public override Accessibility DeclaredAccessibility => Accessibility.Public;
}

/// <summary>The type of the <c>null</c> literal.</summary>
public sealed class NullTypeSymbol : TypeSymbol
{
    public static readonly NullTypeSymbol Instance = new();
    public override SymbolKind Kind => SymbolKind.NamedType;
    public override string Name => "null";
    public override TypeKind TypeKind => TypeKind.Null;
    public override SpecialType SpecialType => SpecialType.Null;
    public override bool IsReferenceType => true;
    public override bool IsValueType => false;
    public override bool Equals(TypeSymbol? other)
    {
        return other is NullTypeSymbol;
    }

    public override int GetHashCode()
    {
        return 3;
    }
}

/// <summary>The type of expressions that never produce a value (calls to functions returning <c>never</c>, throw).</summary>
public sealed class NeverTypeSymbol : TypeSymbol
{
    public static readonly NeverTypeSymbol Instance = new();
    public override SymbolKind Kind => SymbolKind.NamedType;
    public override string Name => "never";
    public override TypeKind TypeKind => TypeKind.Never;
    public override SpecialType SpecialType => SpecialType.Never;
    public override bool IsReferenceType => false;
    public override bool IsValueType => false;
    public override bool Equals(TypeSymbol? other)
    {
        return other is NeverTypeSymbol;
    }

    public override int GetHashCode()
    {
        return 5;
    }
}

/// <summary>A nullable annotation: <c>T | null</c> / <c>T?</c>. Value types lower to Nullable&lt;T&gt;; reference types erase.</summary>
public sealed class NullableTypeSymbol(TypeSymbol underlying) : TypeSymbol
{
    public TypeSymbol Underlying { get; } = underlying;
    public override SymbolKind Kind => SymbolKind.NullableType;
    public override string Name => Underlying.Name + "?";
    public override TypeKind TypeKind => TypeKind.Nullable;
    public override bool IsReferenceType => !Underlying.IsValueType;
    public override bool IsValueType => false;
    public override NamedTypeSymbol? BaseType => Underlying.BaseType;
    public override ImmutableArray<NamedTypeSymbol> Interfaces => Underlying.Interfaces;
    public override ImmutableArray<Symbol> GetMembers()
    {
        return Underlying.GetMembers();
    }

    public override bool Equals(TypeSymbol? other)
    {
        return other is NullableTypeSymbol n && n.Underlying.Equals(Underlying);
    }

    public override int GetHashCode()
    {
        return (Underlying.GetHashCode() * 31) + 7;
    }
}

public sealed class ArrayTypeSymbol(TypeSymbol elementType, NamedTypeSymbol? arrayBase, int rank = 1) : TypeSymbol
{
    private readonly NamedTypeSymbol? _arrayBase = arrayBase;

    public TypeSymbol ElementType { get; } = elementType;
    public int Rank { get; } = rank;
    public override SymbolKind Kind => SymbolKind.ArrayType;
    public override string Name => ElementType.Name + "[]";
    public override TypeKind TypeKind => TypeKind.Array;
    public override bool IsReferenceType => true;
    public override bool IsValueType => false;
    public override NamedTypeSymbol? BaseType => _arrayBase;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;
    public override bool Equals(TypeSymbol? other)
    {
        return other is ArrayTypeSymbol a && a.Rank == Rank && a.ElementType.Equals(ElementType);
    }

    public override int GetHashCode()
    {
        return (ElementType.GetHashCode() * 31) + Rank;
    }

    public ArrayTypeSymbol WithElementType(TypeSymbol e)
    {
        return new(e, _arrayBase, Rank);
    }
}

public sealed class TypeParameterSymbol(string name, int ordinal, Symbol container, VarianceKind variance,
    Func<TypeParameterSymbol, TypeParameterConstraints>? constraints = null) : TypeSymbol
{
    private readonly string _name = name;
    private readonly Func<TypeParameterSymbol, TypeParameterConstraints>? _constraintFactory = constraints;

    public int Ordinal { get; } = ordinal;
    public Symbol Container { get; } = container;
    public VarianceKind Variance { get; } = variance;
    public bool IsMethodTypeParameter => Container is MethodSymbol;
    public override SymbolKind Kind => SymbolKind.TypeParameter;
    public override string Name => _name;
    public override Symbol? ContainingSymbol => Container;
    public override TypeKind TypeKind => TypeKind.TypeParameter;
    public override bool IsReferenceType => HasReferenceTypeConstraint || (!HasValueTypeConstraint && ConstraintTypes.Any(c => c.IsReferenceType && c.TypeKind != TypeKind.TypeParameter && c.TypeKind != TypeKind.Interface));
    public override bool IsValueType => HasValueTypeConstraint;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;

    private TypeParameterConstraints Constraints => field ??= (_constraintFactory?.Invoke(this) ?? TypeParameterConstraints.None);
    public bool HasReferenceTypeConstraint => Constraints.HasClass;
    public bool HasValueTypeConstraint => Constraints.HasStruct;
    public bool HasConstructorConstraint => Constraints.HasNew || Constraints.HasStruct;
    public bool HasNotNullConstraint => Constraints.HasNotNull;
    public ImmutableArray<TypeSymbol> ConstraintTypes => Constraints.Types;

    public override NamedTypeSymbol? BaseType
    {
        get
        {
            foreach (TypeSymbol c in ConstraintTypes)
            {
                if (c is NamedTypeSymbol { TypeKind: TypeKind.Class } n)
                {
                    return n;
                }
            }

            return null;
        }
    }

    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            ImmutableArray<NamedTypeSymbol>.Builder b = ImmutableArray.CreateBuilder<NamedTypeSymbol>();
            foreach (TypeSymbol c in ConstraintTypes)
            {
                if (c is NamedTypeSymbol { TypeKind: TypeKind.Interface } n)
                {
                    b.Add(n);
                }
            }

            return b.ToImmutable();
        }
    }

    public override ImmutableArray<Symbol> GetMembers()
    {
        return BaseType?.GetMembers() ?? [];
    }

    public override bool Equals(TypeSymbol? other)
    {
        return ReferenceEquals(this, other);
    }

    public override int GetHashCode()
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
}

public sealed class TypeParameterConstraints(bool hasClass, bool hasStruct, bool hasNew, bool hasNotNull, ImmutableArray<TypeSymbol> types)
{
    public static readonly TypeParameterConstraints None = new(false, false, false, false, []);

    public bool HasClass { get; } = hasClass;
    public bool HasStruct { get; } = hasStruct;
    public bool HasNew { get; } = hasNew;
    public bool HasNotNull { get; } = hasNotNull;
    public ImmutableArray<TypeSymbol> Types { get; } = types;
}

/// <summary>Caladesi-style rendering of types for diagnostics and hover.</summary>
public static class TypeDisplay
{
    public static string Format(TypeSymbol t)
    {
        StringBuilder sb = new();
        Write(sb, t);
        return sb.ToString();
    }

    public static void Write(StringBuilder sb, TypeSymbol t)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(t);
        switch (t)
        {
            case ArrayTypeSymbol a:
                Write(sb, a.ElementType);
                _ = sb.Append('[').Append(',', a.Rank - 1).Append(']');
                return;
            case NullableTypeSymbol n:
                if (n.Underlying is NamedTypeSymbol { IsUnionType: true, IsAnonymousUnion: true } or ArrayTypeSymbol or NullableTypeSymbol)
                {
                    _ = sb.Append('(');
                }

                Write(sb, n.Underlying);
                if (n.Underlying is NamedTypeSymbol { IsUnionType: true, IsAnonymousUnion: true } or ArrayTypeSymbol or NullableTypeSymbol)
                {
                    _ = sb.Append(')');
                }

                _ = sb.Append(" | null");
                return;
            case NamedTypeSymbol nt:
                WriteNamed(sb, nt);
                return;
            default:
                _ = sb.Append(t.Name);
                return;
        }
    }

    private static void WriteNamed(StringBuilder sb, NamedTypeSymbol nt)
    {
        string? kw = nt.SpecialType.Keyword();
        if (kw != null && nt.SpecialType != SpecialType.None) { _ = sb.Append(kw); return; }
        if (nt.IsAnonymousUnion)
        {
            for (int i = 0; i < nt.TypeArguments.Length; i++)
            {
                if (i > 0)
                {
                    _ = sb.Append(" | ");
                }

                Write(sb, nt.TypeArguments[i]);
            }
            return;
        }
        if (nt.IsTuple)
        {
            _ = sb.Append('[');
            for (int i = 0; i < nt.TypeArguments.Length; i++)
            {
                if (i > 0)
                {
                    _ = sb.Append(", ");
                }

                ImmutableArray<string?> names = nt.TupleElementNames;
                if (!names.IsDefault && i < names.Length && names[i] != null)
                {
                    _ = sb.Append(names[i]).Append(": ");
                }

                Write(sb, nt.TypeArguments[i]);
            }
            _ = sb.Append(']');
            return;
        }
        if (nt.ContainingType != null && nt.SpecialType == SpecialType.None)
        {
            WriteNamed(sb, nt.ContainingType.OriginalDefinition is { } && !nt.ContainingType.IsGenericType ? nt.ContainingType : nt.ContainingType);
            _ = sb.Append('.');
        }
        _ = sb.Append(nt.Name);
        ImmutableArray<TypeSymbol> own = nt.OwnTypeArguments;
        if (own.Length > 0)
        {
            _ = sb.Append('<');
            for (int i = 0; i < own.Length; i++)
            {
                if (i > 0)
                {
                    _ = sb.Append(", ");
                }

                Write(sb, own[i]);
            }
            _ = sb.Append('>');
        }
    }
}
