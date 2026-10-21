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
    public virtual ImmutableArray<NamedTypeSymbol> Interfaces => ImmutableArray<NamedTypeSymbol>.Empty;
    public virtual ImmutableArray<Symbol> GetMembers() => ImmutableArray<Symbol>.Empty;
    public virtual ImmutableArray<Symbol> GetMembers(string name)
    {
        var all = GetMembers();
        ImmutableArray<Symbol>.Builder? b = null;
        foreach (var m in all)
            if (m.Name == name) (b ??= ImmutableArray.CreateBuilder<Symbol>()).Add(m);
        return b?.ToImmutable() ?? ImmutableArray<Symbol>.Empty;
    }

    public bool IsErrorType => TypeKind == TypeKind.Error;
    public bool IsVoid => SpecialType == SpecialType.Void;
    public bool IsNullType => TypeKind == TypeKind.Null;
    public bool IsNeverType => TypeKind == TypeKind.Never;

    private ImmutableArray<NamedTypeSymbol> _allInterfaces;

    /// <summary>All interfaces implemented by this type (including inherited), de-duplicated.</summary>
    public ImmutableArray<NamedTypeSymbol> AllInterfaces
    {
        get
        {
            if (!_allInterfaces.IsDefault) return _allInterfaces;
            var set = new List<NamedTypeSymbol>();
            void Add(NamedTypeSymbol i)
            {
                foreach (var e in set) if (e.Equals(i)) return;
                set.Add(i);
                foreach (var b in i.Interfaces) Add(b);
            }
            for (TypeSymbol? t = this; t != null; t = t.BaseType)
                foreach (var i in t.Interfaces) Add(i);
            if (this is TypeParameterSymbol tp)
                foreach (var c in tp.ConstraintTypes)
                {
                    if (c is NamedTypeSymbol { TypeKind: TypeKind.Interface } ci) Add(ci);
                    else foreach (var i in c.AllInterfaces) Add(i);
                }
            return _allInterfaces = set.ToImmutableArray();
        }
    }

    /// <summary>Full display name using Caladesi type names.</summary>
    public string ToDisplayString() => TypeDisplay.Format(this);
    public virtual string ToMetadataString() => Name;

    public override string ToString() => ToDisplayString();

    public abstract bool Equals(TypeSymbol? other);
    public override bool Equals(object? obj) => obj is TypeSymbol t && Equals(t);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public static bool operator ==(TypeSymbol? a, TypeSymbol? b) => ReferenceEquals(a, b) || (a is not null && b is not null && a.Equals(b));
    public static bool operator !=(TypeSymbol? a, TypeSymbol? b) => !(a == b);

    /// <summary>Strips a nullable annotation wrapper, if any.</summary>
    public TypeSymbol StripNullable() => this is NullableTypeSymbol n ? n.Underlying : this;
    public bool IsNullableWrapper => this is NullableTypeSymbol;
}

/// <summary>Produced for unresolvable types; suppresses cascading diagnostics.</summary>
public sealed class ErrorTypeSymbol : NamedTypeSymbol
{
    public static readonly ErrorTypeSymbol Instance = new("<error>");
    private readonly string _name;
    public ErrorTypeSymbol(string name) => _name = name;
    public override string Name => _name;
    public override TypeKind TypeKind => TypeKind.Error;
    public override bool IsReferenceType => true;
    public override bool IsValueType => false;
    public override SymbolKind Kind => SymbolKind.ErrorType;
    public override ImmutableArray<TypeParameterSymbol> TypeParameters => ImmutableArray<TypeParameterSymbol>.Empty;
    public override ImmutableArray<TypeSymbol> TypeArguments => ImmutableArray<TypeSymbol>.Empty;
    public override bool Equals(TypeSymbol? other) => other is ErrorTypeSymbol;
    public override int GetHashCode() => 17;
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
    public override bool Equals(TypeSymbol? other) => other is NullTypeSymbol;
    public override int GetHashCode() => 3;
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
    public override bool Equals(TypeSymbol? other) => other is NeverTypeSymbol;
    public override int GetHashCode() => 5;
}

/// <summary>A nullable annotation: <c>T | null</c> / <c>T?</c>. Value types lower to Nullable&lt;T&gt;; reference types erase.</summary>
public sealed class NullableTypeSymbol : TypeSymbol
{
    public NullableTypeSymbol(TypeSymbol underlying)
    {
        Underlying = underlying;
    }

    public TypeSymbol Underlying { get; }
    public override SymbolKind Kind => SymbolKind.NullableType;
    public override string Name => Underlying.Name + "?";
    public override TypeKind TypeKind => TypeKind.Nullable;
    public override bool IsReferenceType => !Underlying.IsValueType;
    public override bool IsValueType => false;
    public override NamedTypeSymbol? BaseType => Underlying.BaseType;
    public override ImmutableArray<NamedTypeSymbol> Interfaces => Underlying.Interfaces;
    public override ImmutableArray<Symbol> GetMembers() => Underlying.GetMembers();
    public override bool Equals(TypeSymbol? other) => other is NullableTypeSymbol n && n.Underlying.Equals(Underlying);
    public override int GetHashCode() => Underlying.GetHashCode() * 31 + 7;
}

public sealed class ArrayTypeSymbol : TypeSymbol
{
    private readonly NamedTypeSymbol? _arrayBase;

    public ArrayTypeSymbol(TypeSymbol elementType, NamedTypeSymbol? arrayBase, int rank = 1)
    {
        ElementType = elementType;
        _arrayBase = arrayBase;
        Rank = rank;
    }

    public TypeSymbol ElementType { get; }
    public int Rank { get; }
    public override SymbolKind Kind => SymbolKind.ArrayType;
    public override string Name => ElementType.Name + "[]";
    public override TypeKind TypeKind => TypeKind.Array;
    public override bool IsReferenceType => true;
    public override bool IsValueType => false;
    public override NamedTypeSymbol? BaseType => _arrayBase;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;
    public override bool Equals(TypeSymbol? other) => other is ArrayTypeSymbol a && a.Rank == Rank && a.ElementType.Equals(ElementType);
    public override int GetHashCode() => ElementType.GetHashCode() * 31 + Rank;
    public ArrayTypeSymbol WithElementType(TypeSymbol e) => new(e, _arrayBase, Rank);
}

public sealed class TypeParameterSymbol : TypeSymbol
{
    private readonly string _name;
    private Func<TypeParameterSymbol, TypeParameterConstraints>? _constraintFactory;
    private TypeParameterConstraints? _constraints;

    public TypeParameterSymbol(string name, int ordinal, Symbol container, VarianceKind variance,
        Func<TypeParameterSymbol, TypeParameterConstraints>? constraints = null)
    {
        _name = name;
        Ordinal = ordinal;
        Container = container;
        Variance = variance;
        _constraintFactory = constraints;
    }

    public int Ordinal { get; }
    public Symbol Container { get; }
    public VarianceKind Variance { get; }
    public bool IsMethodTypeParameter => Container is MethodSymbol;
    public override SymbolKind Kind => SymbolKind.TypeParameter;
    public override string Name => _name;
    public override Symbol? ContainingSymbol => Container;
    public override TypeKind TypeKind => TypeKind.TypeParameter;
    public override bool IsReferenceType => HasReferenceTypeConstraint || (!HasValueTypeConstraint && ConstraintTypes.Any(c => c.IsReferenceType && c.TypeKind != TypeKind.TypeParameter && c.TypeKind != TypeKind.Interface));
    public override bool IsValueType => HasValueTypeConstraint;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;

    private TypeParameterConstraints Constraints => _constraints ??= (_constraintFactory?.Invoke(this) ?? TypeParameterConstraints.None);
    public bool HasReferenceTypeConstraint => Constraints.HasClass;
    public bool HasValueTypeConstraint => Constraints.HasStruct;
    public bool HasConstructorConstraint => Constraints.HasNew || Constraints.HasStruct;
    public bool HasNotNullConstraint => Constraints.HasNotNull;
    public ImmutableArray<TypeSymbol> ConstraintTypes => Constraints.Types;

    public override NamedTypeSymbol? BaseType
    {
        get
        {
            foreach (var c in ConstraintTypes)
                if (c is NamedTypeSymbol { TypeKind: TypeKind.Class } n) return n;
            return null;
        }
    }

    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            var b = ImmutableArray.CreateBuilder<NamedTypeSymbol>();
            foreach (var c in ConstraintTypes)
                if (c is NamedTypeSymbol { TypeKind: TypeKind.Interface } n) b.Add(n);
            return b.ToImmutable();
        }
    }

    public override ImmutableArray<Symbol> GetMembers() => BaseType?.GetMembers() ?? ImmutableArray<Symbol>.Empty;

    public override bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

public sealed class TypeParameterConstraints
{
    public static readonly TypeParameterConstraints None = new(false, false, false, false, ImmutableArray<TypeSymbol>.Empty);

    public TypeParameterConstraints(bool hasClass, bool hasStruct, bool hasNew, bool hasNotNull, ImmutableArray<TypeSymbol> types)
    {
        HasClass = hasClass;
        HasStruct = hasStruct;
        HasNew = hasNew;
        HasNotNull = hasNotNull;
        Types = types;
    }

    public bool HasClass { get; }
    public bool HasStruct { get; }
    public bool HasNew { get; }
    public bool HasNotNull { get; }
    public ImmutableArray<TypeSymbol> Types { get; }
}

/// <summary>Caladesi-style rendering of types for diagnostics and hover.</summary>
public static class TypeDisplay
{
    public static string Format(TypeSymbol t)
    {
        var sb = new StringBuilder();
        Write(sb, t);
        return sb.ToString();
    }

    public static void Write(StringBuilder sb, TypeSymbol t)
    {
        switch (t)
        {
            case ArrayTypeSymbol a:
                Write(sb, a.ElementType);
                sb.Append('[').Append(',', a.Rank - 1).Append(']');
                return;
            case NullableTypeSymbol n:
                if (n.Underlying is NamedTypeSymbol { IsUnionType: true, IsAnonymousUnion: true } or ArrayTypeSymbol or NullableTypeSymbol) sb.Append('(');
                Write(sb, n.Underlying);
                if (n.Underlying is NamedTypeSymbol { IsUnionType: true, IsAnonymousUnion: true } or ArrayTypeSymbol or NullableTypeSymbol) sb.Append(')');
                sb.Append(" | null");
                return;
            case NamedTypeSymbol nt:
                WriteNamed(sb, nt);
                return;
            default:
                sb.Append(t.Name);
                return;
        }
    }

    private static void WriteNamed(StringBuilder sb, NamedTypeSymbol nt)
    {
        var kw = nt.SpecialType.Keyword();
        if (kw != null && nt.SpecialType != SpecialType.None) { sb.Append(kw); return; }
        if (nt.IsAnonymousUnion)
        {
            for (var i = 0; i < nt.TypeArguments.Length; i++)
            {
                if (i > 0) sb.Append(" | ");
                Write(sb, nt.TypeArguments[i]);
            }
            return;
        }
        if (nt.IsTuple)
        {
            sb.Append('[');
            for (var i = 0; i < nt.TypeArguments.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var names = nt.TupleElementNames;
                if (!names.IsDefault && i < names.Length && names[i] != null) sb.Append(names[i]).Append(": ");
                Write(sb, nt.TypeArguments[i]);
            }
            sb.Append(']');
            return;
        }
        if (nt.ContainingType != null && nt.SpecialType == SpecialType.None)
        {
            WriteNamed(sb, nt.ContainingType.OriginalDefinition is { } od && !nt.ContainingType.IsGenericType ? nt.ContainingType : nt.ContainingType);
            sb.Append('.');
        }
        sb.Append(nt.Name);
        var own = nt.OwnTypeArguments;
        if (own.Length > 0)
        {
            sb.Append('<');
            for (var i = 0; i < own.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                Write(sb, own[i]);
            }
            sb.Append('>');
        }
    }
}
