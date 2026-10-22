using System.Collections.Immutable;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols;

/// <summary>A compile-time constant. <see cref="Value"/> is null for the null constant.</summary>
public readonly record struct ConstantValue(object? Value)
{
    public static readonly ConstantValue Null = new(null);
    public bool IsNull => Value == null;
    public override string ToString()
    {
        return Value?.ToString() ?? "null";
    }
}

public enum MethodKind
{
    Ordinary, Constructor, StaticConstructor, PropertyGet, PropertySet, UserDefinedOperator, Conversion, DelegateInvoke,
    LambdaMethod, LocalFunction, Destructor, EventAdd, EventRemove,
}

public abstract class MethodSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Method;
    public abstract MethodKind MethodKind { get; }
    public abstract TypeSymbol ReturnType { get; }
    public abstract ImmutableArray<ParameterSymbol> Parameters { get; }
    public virtual ImmutableArray<TypeParameterSymbol> TypeParameters => [];
    public virtual ImmutableArray<TypeSymbol> TypeArguments => TypeParameters.CastArray<TypeSymbol>();
    public int Arity => TypeParameters.Length;
    public bool IsGenericMethod => Arity > 0;
    public bool ReturnsVoid => ReturnType.IsVoid;
    public virtual bool IsExtensionMethod => false;
    public virtual bool IsAsync => false;
    public virtual bool IsIterator => false;
    public virtual bool IsInitOnly => false;
    public virtual bool ReturnsByRef => false;
    public virtual bool IsExtern => false;
    public virtual Symbol? AssociatedSymbol => null;
    public virtual MethodSymbol? OverriddenMethod => null;
    public new virtual MethodSymbol OriginalDefinition => this;
    public virtual MethodSymbol ConstructedFrom => this;
    public bool IsDefinition => ReferenceEquals(OriginalDefinition, this);
    public virtual ImmutableArray<MethodSymbol> ExplicitInterfaceImplementations => [];
    public bool HasParamsArray => Parameters.Length > 0 && Parameters[^1].IsParams;

    public MethodSymbol Construct(ImmutableArray<TypeSymbol> typeArguments)
    {
        return typeArguments.Length != Arity
            ? throw new ArgumentException("wrong number of type arguments")
            : (MethodSymbol)new ConstructedMethodSymbol(this, typeArguments);
    }
}

public sealed class ConstructedMethodSymbol(MethodSymbol definition, ImmutableArray<TypeSymbol> typeArguments) : MethodSymbol
{
    private readonly MethodSymbol _definition = definition;
    private readonly ImmutableArray<TypeSymbol> _typeArguments = typeArguments;
    private ImmutableArray<ParameterSymbol> _params;

    public override string Name => _definition.Name;
    public override string MetadataName => _definition.MetadataName;
    public override Symbol? ContainingSymbol => _definition.ContainingSymbol;
    public override MethodKind MethodKind => _definition.MethodKind;
    public override TypeSymbol ReturnType => Map.Substitute(_definition.ReturnType);
    public override ImmutableArray<TypeSymbol> TypeArguments => _typeArguments;
    public override ImmutableArray<TypeParameterSymbol> TypeParameters => _definition.TypeParameters;
    public override MethodSymbol OriginalDefinition => _definition.OriginalDefinition;
    public override MethodSymbol ConstructedFrom => _definition;
    public override Accessibility DeclaredAccessibility => _definition.DeclaredAccessibility;
    public override bool IsStatic => _definition.IsStatic;
    public override bool IsAbstract => _definition.IsAbstract;
    public override bool IsVirtual => _definition.IsVirtual;
    public override bool IsOverride => _definition.IsOverride;
    public override bool IsSealed => _definition.IsSealed;
    public override bool IsExtensionMethod => _definition.IsExtensionMethod;
    public override bool IsAsync => _definition.IsAsync;
    public override bool IsFromSource => _definition.IsFromSource;
    public override Symbol? AssociatedSymbol => _definition.AssociatedSymbol;
    public override ImmutableArray<SourceLocation> Locations => _definition.Locations;
    public override ImmutableArray<AttributeData> GetAttributes()
    {
        return _definition.GetAttributes();
    }

    public TypeMap Map { get; } = new TypeMap(definition.TypeParameters, typeArguments);

    public override ImmutableArray<ParameterSymbol> Parameters
    {
        get
        {
            if (_params.IsDefault)
            {
                _params = [.. _definition.Parameters.Select(p => p.WithType(Map.Substitute(p.Type), this))];
            }

            return _params;
        }
    }
}

public sealed class SubstitutedMethodSymbol(ConstructedNamedType container, MethodSymbol original, TypeMap map) : MethodSymbol
{
    private readonly ConstructedNamedType _container = container;
    private readonly MethodSymbol _original = original;
    private readonly TypeMap _map = map;
    private ImmutableArray<ParameterSymbol> _params;
    private ImmutableArray<TypeParameterSymbol> _typeParams;
    private Symbol? _associated;

    internal void SetAssociated(Symbol s)
    {
        _associated = s;
    }

    public override string Name => _original.Name;
    public override string MetadataName => _original.MetadataName;
    public override Symbol? ContainingSymbol => _container;
    public override MethodKind MethodKind => _original.MethodKind;
    public override MethodSymbol OriginalDefinition => _original.OriginalDefinition;
    public override Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public override bool IsStatic => _original.IsStatic;
    public override bool IsAbstract => _original.IsAbstract;
    public override bool IsVirtual => _original.IsVirtual;
    public override bool IsOverride => _original.IsOverride;
    public override bool IsSealed => _original.IsSealed;
    public override bool IsExtensionMethod => _original.IsExtensionMethod;
    public override bool IsInitOnly => _original.IsInitOnly;
    public override bool IsFromSource => _original.IsFromSource;
    public override Symbol? AssociatedSymbol => _associated ?? _original.AssociatedSymbol;
    public override ImmutableArray<AttributeData> GetAttributes()
    {
        return _original.GetAttributes();
    }

    public override ImmutableArray<SourceLocation> Locations => _original.Locations;

    private TypeMap FullMap
    {
        get
        {
            if (field != null)
            {
                return field;
            }

            if (_original.Arity == 0)
            {
                return field = _map;
            }
            // method type parameters are re-created so constructed generic methods substitute correctly
            ImmutableArray<TypeParameterSymbol> tps = TypeParameters;
            ImmutableArray<TypeParameterSymbol> from = _original.TypeParameters;
            ImmutableArray<TypeSymbol> to = tps.CastArray<TypeSymbol>();
            return field = _map.Extend(from, to);
        }
    }

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_original.Arity == 0)
            {
                return [];
            }

            if (_typeParams.IsDefault)
            {
                ImmutableArray<TypeParameterSymbol>.Builder b = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
                foreach (TypeParameterSymbol tp in _original.TypeParameters)
                {
                    TypeParameterSymbol o = tp;
                    b.Add(new TypeParameterSymbol(o.Name, o.Ordinal, this, o.Variance, self =>
                    {
                        List<TypeSymbol> t = [];
                        foreach (TypeSymbol c in o.ConstraintTypes)
                        {
                            t.Add(FullMap.Substitute(c));
                        }

                        return new TypeParameterConstraints(o.HasReferenceTypeConstraint, o.HasValueTypeConstraint, o.HasConstructorConstraint && !o.HasValueTypeConstraint, o.HasNotNullConstraint, [.. t]);
                    }));
                }
                _typeParams = b.ToImmutable();
            }
            return _typeParams;
        }
    }

    public override TypeSymbol ReturnType => FullMap.Substitute(_original.ReturnType);

    public override ImmutableArray<ParameterSymbol> Parameters
    {
        get
        {
            if (_params.IsDefault)
            {
                _params = [.. _original.Parameters.Select(p => p.WithType(FullMap.Substitute(p.Type), this))];
            }

            return _params;
        }
    }

    public override MethodSymbol? OverriddenMethod => _original.OverriddenMethod;
}

public class ParameterSymbol(string name, TypeSymbol type, int ordinal, Symbol? container, RefKind refKind = RefKind.None,
    bool isParams = false, ConstantValue? defaultValue = null, bool isOptional = false, bool isThis = false) : Symbol
{
    private string Name_ { get; } = name;
    public override string Name => Name_;
    public override SymbolKind Kind => SymbolKind.Parameter;
    public TypeSymbol Type { get; internal set; } = type;
    public int Ordinal { get; } = ordinal;
    public Symbol? Container { get; } = container;
    public override Symbol? ContainingSymbol => Container;
    public RefKind RefKind { get; } = refKind;
    public bool IsParams { get; } = isParams;
    public ConstantValue? DefaultValue { get; } = defaultValue;
    public bool IsOptional { get; } = isOptional || defaultValue.HasValue;
    /// <summary>True for the <c>this</c> parameter of an extension method.</summary>
    public bool IsThis { get; } = isThis;
    public ParameterSymbol? OriginalParameter { get; internal set; }
    public SyntaxNode? DeclaringSyntax { get; init; }
    /// <summary>Set by closure analysis when the parameter is captured by a lambda.</summary>
    public bool IsCaptured { get; set; }
    public bool IsReassigned { get; set; }
    public bool IsFromSourceParam { get; init; }
    public override bool IsFromSource => IsFromSourceParam;

    public virtual ParameterSymbol WithType(TypeSymbol type, Symbol container)
    {
        return new(Name, type, Ordinal, container, RefKind, IsParams, DefaultValue, IsOptional, IsThis)
        { OriginalParameter = OriginalParameter ?? this, DeclaringSyntax = DeclaringSyntax, IsFromSourceParam = IsFromSourceParam };
    }

    public override string ToString()
    {
        return Name + ": " + Type.ToDisplayString();
    }
}

public abstract class FieldSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Field;
    public abstract TypeSymbol Type { get; }
    public virtual bool IsConst => false;
    public virtual ConstantValue? ConstantValue => null;
    public virtual bool IsReadOnly => false;
    public new virtual FieldSymbol OriginalDefinition => this;
    public virtual Symbol? AssociatedSymbol => null;
}

public sealed class SubstitutedFieldSymbol(ConstructedNamedType container, FieldSymbol original, TypeMap map) : FieldSymbol
{
    private readonly ConstructedNamedType _container = container;
    private readonly FieldSymbol _original = original;
    private readonly TypeMap _map = map;

    public override string Name => _original.Name;
    public override Symbol? ContainingSymbol => _container;
    public override TypeSymbol Type => _map.Substitute(_original.Type);
    public override FieldSymbol OriginalDefinition => _original.OriginalDefinition;
    public override bool IsStatic => _original.IsStatic;
    public override bool IsConst => _original.IsConst;
    public override ConstantValue? ConstantValue => _original.ConstantValue;
    public override bool IsReadOnly => _original.IsReadOnly;
    public override Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public override ImmutableArray<AttributeData> GetAttributes()
    {
        return _original.GetAttributes();
    }
}

public abstract class PropertySymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Property;
    public abstract TypeSymbol Type { get; }
    public abstract MethodSymbol? GetMethod { get; }
    public abstract MethodSymbol? SetMethod { get; }
    public virtual ImmutableArray<ParameterSymbol> Parameters => [];
    public bool IsIndexer => Parameters.Length > 0;
    public new virtual PropertySymbol OriginalDefinition => this;
    public virtual PropertySymbol? OverriddenProperty => null;
    public bool IsReadOnlyProperty => SetMethod == null;
    public bool IsInitOnly => SetMethod?.IsInitOnly == true;
}

public sealed class SubstitutedPropertySymbol(ConstructedNamedType container, PropertySymbol original, TypeMap map) : PropertySymbol
{
    private readonly ConstructedNamedType _container = container;
    private readonly PropertySymbol _original = original;
    private readonly TypeMap _map = map;
    private MethodSymbol? _get, _set;
    private ImmutableArray<ParameterSymbol> _params;

    internal void Link(ImmutableArray<Symbol> members)
    {
        foreach (Symbol m in members)
        {
            if (m is not SubstitutedMethodSymbol sm)
            {
                continue;
            }

            if (ReferenceEquals(sm.OriginalDefinition, _original.GetMethod?.OriginalDefinition)) { _get = sm; sm.SetAssociated(this); }
            else if (ReferenceEquals(sm.OriginalDefinition, _original.SetMethod?.OriginalDefinition)) { _set = sm; sm.SetAssociated(this); }
        }
    }

    public override string Name => _original.Name;
    public override Symbol? ContainingSymbol => _container;
    public override TypeSymbol Type => _map.Substitute(_original.Type);
    public override MethodSymbol? GetMethod => _get;
    public override MethodSymbol? SetMethod => _set;
    public override bool IsStatic => _original.IsStatic;
    public override bool IsAbstract => _original.IsAbstract;
    public override bool IsVirtual => _original.IsVirtual;
    public override bool IsOverride => _original.IsOverride;
    public override Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public override PropertySymbol OriginalDefinition => _original.OriginalDefinition;
    public override ImmutableArray<AttributeData> GetAttributes()
    {
        return _original.GetAttributes();
    }

    public override ImmutableArray<ParameterSymbol> Parameters
    {
        get
        {
            if (_params.IsDefault)
            {
                _params = [.. _original.Parameters.Select(p => p.WithType(_map.Substitute(p.Type), this))];
            }

            return _params;
        }
    }
}

public sealed class LocalSymbol(string name, TypeSymbol type, bool isConst, Symbol? container, SyntaxNode? declaration = null) : Symbol
{
    private string Name_ { get; } = name;
    public override string Name => Name_;
    public override SymbolKind Kind => SymbolKind.Local;
    public TypeSymbol Type { get; set; } = type;
    public bool IsConst { get; } = isConst;
    public Symbol? Container { get; set; } = container;
    public override Symbol? ContainingSymbol => Container;
    public SyntaxNode? DeclaringSyntax { get; } = declaration;
    public ConstantValue? ConstantValue { get; set; }
    public bool IsCaptured { get; set; }
    public bool IsReassigned { get; set; }
    public bool IsUsingVariable { get; set; }
    public bool IsSynthesized { get; init; }
    public bool IsFixedUp { get; set; }
    public override string ToString()
    {
        return Name + ": " + Type.ToDisplayString();
    }
}

public sealed class LabelSymbol(string name) : Symbol
{
    public override SymbolKind Kind => SymbolKind.Label;
    public override string Name => name;
}

internal static class ImmutableArrayExtensions
{
    public static ImmutableArray<TBase> CastArray<TBase>(this ImmutableArray<TypeParameterSymbol> a) where TBase : class
    {
        ImmutableArray<TBase>.Builder b = ImmutableArray.CreateBuilder<TBase>(a.Length);
        foreach (TypeParameterSymbol x in a)
        {
            b.Add((TBase)(object)x);
        }

        return b.MoveToImmutable();
    }
}
