using System.Collections.Immutable;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols;

public enum SymbolKind
{
    Namespace, NamedType, TypeParameter, ArrayType, NullableType, ErrorType, Method, Field, Property, Parameter, Local,
    Label, Alias, Assembly, FunctionGroup,
}

public enum Accessibility { NotApplicable, Private, ProtectedAndInternal, Protected, Internal, ProtectedOrInternal, Public }

public enum RefKind { None, Ref, Out, In }

/// <summary>A (possibly absent) declaration site in source, used for navigation and diagnostics.</summary>
public readonly record struct SourceLocation(SyntaxTree Tree, Text.TextSpan Span);

/// <summary>Base of all symbols. Symbols are reference-compared except structurally constructed types.</summary>
public abstract class Symbol
{
    public abstract SymbolKind Kind { get; }
    public abstract string Name { get; }
    public virtual Symbol? ContainingSymbol => null;
    public virtual Accessibility DeclaredAccessibility => Accessibility.NotApplicable;
    public virtual bool IsStatic => false;
    public virtual bool IsAbstract => false;
    public virtual bool IsVirtual => false;
    public virtual bool IsOverride => false;
    public virtual bool IsSealed => false;
    public virtual bool IsFromSource => false;
    public virtual Symbol OriginalDefinition => this;
    public virtual ImmutableArray<SourceLocation> Locations => ImmutableArray<SourceLocation>.Empty;
    public virtual ImmutableArray<AttributeData> GetAttributes() => ImmutableArray<AttributeData>.Empty;

    public NamedTypeSymbol? ContainingType
    {
        get
        {
            var s = ContainingSymbol;
            while (s != null && s is not NamedTypeSymbol) s = s.ContainingSymbol;
            return s as NamedTypeSymbol;
        }
    }

    public virtual NamespaceSymbol? ContainingNamespace
    {
        get
        {
            var s = ContainingSymbol;
            while (s != null && s is not NamespaceSymbol) s = s.ContainingSymbol;
            return s as NamespaceSymbol;
        }
    }

    public virtual AssemblySymbol? ContainingAssembly
    {
        get
        {
            var s = ContainingSymbol;
            while (s != null && s is not AssemblySymbol) s = s.ContainingSymbol;
            return s as AssemblySymbol;
        }
    }

    public virtual string MetadataName => Name;

    public bool HasAttribute(string fullName)
    {
        foreach (var a in GetAttributes())
            if (a.AttributeClass?.ToMetadataString() == fullName) return true;
        return false;
    }

    public AttributeData? GetAttribute(string fullName)
    {
        foreach (var a in GetAttributes())
            if (a.AttributeClass?.ToMetadataString() == fullName) return a;
        return null;
    }

    public override string ToString() => Name;
}

/// <summary>A decoded custom attribute (constructor arguments and named arguments).</summary>
public sealed class AttributeData
{
    public AttributeData(NamedTypeSymbol? attributeClass, MethodSymbol? constructor, ImmutableArray<object?> ctorArgs,
        ImmutableArray<KeyValuePair<string, object?>> namedArgs)
    {
        AttributeClass = attributeClass;
        Constructor = constructor;
        ConstructorArguments = ctorArgs;
        NamedArguments = namedArgs;
    }

    public NamedTypeSymbol? AttributeClass { get; }
    public MethodSymbol? Constructor { get; }
    public ImmutableArray<object?> ConstructorArguments { get; }
    public ImmutableArray<KeyValuePair<string, object?>> NamedArguments { get; }
}

public abstract class AssemblySymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Assembly;
    public abstract NamespaceSymbol GlobalNamespace { get; }
    public virtual Version Version => new(0, 0, 0, 0);
    public override AssemblySymbol? ContainingAssembly => this;
}
