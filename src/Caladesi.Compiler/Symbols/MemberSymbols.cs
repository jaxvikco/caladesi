using System.Collections.Immutable;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols;

/// <summary>A compile-time constant. <see cref="Value"/> is null for the null constant.</summary>
public readonly record struct ConstantValue(object? Value)
{
    public static readonly ConstantValue Null = new(null);
    public bool IsNull => Value == null;
    public override string ToString() => Value?.ToString() ?? "null";
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
    public virtual ImmutableArray<TypeParameterSymbol> TypeParameters => ImmutableArray<TypeParameterSymbol>.Empty;
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
    public virtual ImmutableArray<MethodSymbol> ExplicitInterfaceImplementations => ImmutableArray<MethodSymbol>.Empty;
    public bool HasParamsArray => Parameters.Length > 0 && Parameters[^1].IsParams;

    public MethodSymbol Construct(ImmutableArray<TypeSymbol> typeArguments)
    {
        if (typeArguments.Length != Arity) throw new ArgumentException("wrong number of type arguments");
        return new ConstructedMethodSymbol(this, typeArguments);
    }
}

public sealed class ConstructedMethodSymbol : MethodSymbol
{
    private readonly MethodSymbol _definition;
    private readonly ImmutableArray<TypeSymbol> _typeArguments;
    private readonly TypeMap _map;
    private ImmutableArray<ParameterSymbol> _params;

    public ConstructedMethodSymbol(MethodSymbol definition, ImmutableArray<TypeSymbol> typeArguments)
    {
        _definition = definition;
        _typeArguments = typeArguments;
        _map = new TypeMap(definition.TypeParameters, typeArguments);
    }

    public override string Name => _definition.Name;
    public override string MetadataName => _definition.MetadataName;
    public override Symbol? ContainingSymbol => _definition.ContainingSymbol;
    public override MethodKind MethodKind => _definition.MethodKind;
    public override TypeSymbol ReturnType => _map.Substitute(_definition.ReturnType);
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
    public override ImmutableArray<AttributeData> GetAttributes() => _definition.GetAttributes();
    public TypeMap Map => _map;

    public override ImmutableArray<ParameterSymbol> Parameters
    {
        get
        {
            if (_params.IsDefault)
                _params = _definition.Parameters.Select(p => p.WithType(_map.Substitute(p.Type), this)).ToImmutableArray();
            return _params;
        }
    }
}

public sealed class SubstitutedMethodSymbol : MethodSymbol
{
    private readonly ConstructedNamedType _container;
    private readonly MethodSymbol _original;
    private readonly TypeMap _map;
    private ImmutableArray<ParameterSymbol> _params;
    private ImmutableArray<TypeParameterSymbol> _typeParams;
    private TypeMap? _fullMap;
    private Symbol? _associated;

    public SubstitutedMethodSymbol(ConstructedNamedType container, MethodSymbol original, TypeMap map)
    {
        _container = container;
        _original = original;
        _map = map;
    }

    internal void SetAssociated(Symbol s) => _associated = s;

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
    public override ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();
    public override ImmutableArray<SourceLocation> Locations => _original.Locations;

    private TypeMap FullMap
    {
        get
        {
            if (_fullMap != null) return _fullMap;
            if (_original.Arity == 0) return _fullMap = _map;
            // method type parameters are re-created so constructed generic methods substitute correctly
            var tps = TypeParameters;
            var from = _original.TypeParameters;
            var to = tps.CastArray<TypeSymbol>();
            return _fullMap = _map.Extend(from, to);
        }
    }

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_original.Arity == 0) return ImmutableArray<TypeParameterSymbol>.Empty;
            if (_typeParams.IsDefault)
            {
                var b = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
                foreach (var tp in _original.TypeParameters)
                {
                    var o = tp;
                    b.Add(new TypeParameterSymbol(o.Name, o.Ordinal, this, o.Variance, self =>
                    {
                        var t = new List<TypeSymbol>();
                        foreach (var c in o.ConstraintTypes) t.Add(FullMap.Substitute(c));
                        return new TypeParameterConstraints(o.HasReferenceTypeConstraint, o.HasValueTypeConstraint, o.HasConstructorConstraint && !o.HasValueTypeConstraint, o.HasNotNullConstraint, t.ToImmutableArray());
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
                _params = _original.Parameters.Select(p => p.WithType(FullMap.Substitute(p.Type), this)).ToImmutableArray();
            return _params;
        }
    }

    public override MethodSymbol? OverriddenMethod => _original.OverriddenMethod;
}

public class ParameterSymbol : Symbol
{
    public ParameterSymbol(string name, TypeSymbol type, int ordinal, Symbol? container, RefKind refKind = RefKind.None,
        bool isParams = false, ConstantValue? defaultValue = null, bool isOptional = false, bool isThis = false)
    {
        Name_ = name;
        Type = type;
        Ordinal = ordinal;
        Container = container;
        RefKind = refKind;
        IsParams = isParams;
        DefaultValue = defaultValue;
        IsOptional = isOptional || defaultValue.HasValue;
        IsThis = isThis;
    }

    private string Name_ { get; }
    public override string Name => Name_;
    public override SymbolKind Kind => SymbolKind.Parameter;
    public TypeSymbol Type { get; internal set; }
    public int Ordinal { get; }
    public Symbol? Container { get; }
    public override Symbol? ContainingSymbol => Container;
    public RefKind RefKind { get; }
    public bool IsParams { get; }
    public ConstantValue? DefaultValue { get; }
    public bool IsOptional { get; }
    /// <summary>True for the <c>this</c> parameter of an extension method.</summary>
    public bool IsThis { get; }
    public ParameterSymbol? OriginalParameter { get; internal set; }
    public SyntaxNode? DeclaringSyntax { get; init; }
    /// <summary>Set by closure analysis when the parameter is captured by a lambda.</summary>
    public bool IsCaptured { get; set; }
    public bool IsReassigned { get; set; }
    public bool IsFromSourceParam { get; init; }
    public override bool IsFromSource => IsFromSourceParam;

    public virtual ParameterSymbol WithType(TypeSymbol type, Symbol container) =>
        new(Name, type, Ordinal, container, RefKind, IsParams, DefaultValue, IsOptional, IsThis)
        { OriginalParameter = OriginalParameter ?? this, DeclaringSyntax = DeclaringSyntax, IsFromSourceParam = IsFromSourceParam };

    public override string ToString() => Name + ": " + Type.ToDisplayString();
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

public sealed class SubstitutedFieldSymbol : FieldSymbol
{
    private readonly ConstructedNamedType _container;
    private readonly FieldSymbol _original;
    private readonly TypeMap _map;

    public SubstitutedFieldSymbol(ConstructedNamedType container, FieldSymbol original, TypeMap map)
    {
        _container = container;
        _original = original;
        _map = map;
    }

    public override string Name => _original.Name;
    public override Symbol? ContainingSymbol => _container;
    public override TypeSymbol Type => _map.Substitute(_original.Type);
    public override FieldSymbol OriginalDefinition => _original.OriginalDefinition;
    public override bool IsStatic => _original.IsStatic;
    public override bool IsConst => _original.IsConst;
    public override ConstantValue? ConstantValue => _original.ConstantValue;
    public override bool IsReadOnly => _original.IsReadOnly;
    public override Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public override ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();
}

public abstract class PropertySymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Property;
    public abstract TypeSymbol Type { get; }
    public abstract MethodSymbol? GetMethod { get; }
    public abstract MethodSymbol? SetMethod { get; }
    public virtual ImmutableArray<ParameterSymbol> Parameters => ImmutableArray<ParameterSymbol>.Empty;
    public bool IsIndexer => Parameters.Length > 0;
    public new virtual PropertySymbol OriginalDefinition => this;
    public virtual PropertySymbol? OverriddenProperty => null;
    public bool IsReadOnlyProperty => SetMethod == null;
    public bool IsInitOnly => SetMethod?.IsInitOnly == true;
}

public sealed class SubstitutedPropertySymbol : PropertySymbol
{
    private readonly ConstructedNamedType _container;
    private readonly PropertySymbol _original;
    private readonly TypeMap _map;
    private MethodSymbol? _get, _set;
    private ImmutableArray<ParameterSymbol> _params;

    public SubstitutedPropertySymbol(ConstructedNamedType container, PropertySymbol original, TypeMap map)
    {
        _container = container;
        _original = original;
        _map = map;
    }

    internal void Link(ImmutableArray<Symbol> members)
    {
        foreach (var m in members)
        {
            if (m is not SubstitutedMethodSymbol sm) continue;
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
    public override ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();

    public override ImmutableArray<ParameterSymbol> Parameters
    {
        get
        {
            if (_params.IsDefault)
                _params = _original.Parameters.Select(p => p.WithType(_map.Substitute(p.Type), this)).ToImmutableArray();
            return _params;
        }
    }
}

public sealed class LocalSymbol : Symbol
{
    public LocalSymbol(string name, TypeSymbol type, bool isConst, Symbol? container, SyntaxNode? declaration = null)
    {
        Name_ = name;
        Type = type;
        IsConst = isConst;
        Container = container;
        DeclaringSyntax = declaration;
    }

    private string Name_ { get; }
    public override string Name => Name_;
    public override SymbolKind Kind => SymbolKind.Local;
    public TypeSymbol Type { get; set; }
    public bool IsConst { get; }
    public Symbol? Container { get; set; }
    public override Symbol? ContainingSymbol => Container;
    public SyntaxNode? DeclaringSyntax { get; }
    public ConstantValue? ConstantValue { get; set; }
    public bool IsCaptured { get; set; }
    public bool IsReassigned { get; set; }
    public bool IsUsingVariable { get; set; }
    public bool IsSynthesized { get; init; }
    public bool IsFixedUp { get; set; }
    public override string ToString() => Name + ": " + Type.ToDisplayString();
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
        var b = ImmutableArray.CreateBuilder<TBase>(a.Length);
        foreach (var x in a) b.Add((TBase)(object)x);
        return b.MoveToImmutable();
    }
}
