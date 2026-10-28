using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols.Source;

public enum SynthesizedBodyKind
{
    None,
    AutoGetter,
    AutoSetter,
    DefaultConstructor,
    InheritedConstructor,
    EnumValueField,
    RecordConstructor,
    RecordCopyConstructor,
    RecordClone,
    RecordEquals,
    RecordEqualsObject,
    RecordEqualsOperator,
    RecordNotEqualsOperator,
    RecordGetHashCode,
    RecordToString,
    RecordPrintMembers,
    RecordEqualityContract,
    RecordDeconstruct,
    UnionConstructor,
    UnionImplicit,
    UnionToString,
    UnionEquals,
    UnionGetHashCode,
    UnionEqualsObject,
    DelegateConstructor,
    DelegateInvoke,
    StaticConstructor,
    EntryPointBridge,
    Custom,
    UnionEqualsOperator,
    UnionNotEqualsOperator,
}

/// <summary>A method, function, constructor or accessor declared in source or synthesized by the compiler.</summary>
public sealed class SourceMethodSymbol(Compilation compilation, SourceFile file, NamedTypeSymbol container, string name, MethodKind kind,
    ModifierSet mods, SyntaxNode? declaration, Accessibility accessibility) : MethodSymbol
{
    private readonly string _name = name;
    private readonly MethodKind _kind = kind;
    private ImmutableArray<TypeParameterSymbol> _typeParams;
    private bool _typeParamsDone;
    private ImmutableArray<ParameterSymbol> _params;
    private TypeSymbol? _returnType;
    private bool _returnTypeInProgress;
    private BoundBlock? _boundBody;
    private bool _bodyBound;
    private MethodSymbol? _overridden;
    private bool _overriddenDone;
    private string? _emitName;

    public SourceFile File { get; } = file;
    public Compilation Compilation { get; } = compilation;
    public SyntaxNode? Declaration { get; } = declaration;
    private Accessibility Accessibility_ { get; } = accessibility;
    public SynthesizedBodyKind SynthesizedKind { get; init; }
    public bool IsVirtualFlag { get; init; }
    public bool IsAbstractFlag { get; init; }
    public bool IsOverrideFlag { get; init; }
    public bool IsSealedFlag { get; init; }
    public bool IsStaticFlag { get; init; }
    public bool IsEntryPoint { get; set; }
    public PropertySymbol? AssociatedProperty { get; set; }
    public ParameterListSyntax? ParameterList { get; init; }
    public TypeParameterListSyntax? TypeParameterList { get; init; }
    public TypeAnnotationSyntax? ReturnAnnotation { get; init; }
    public BlockSyntax? BodySyntax { get; init; }
    public bool IsGeneratorSyntax { get; init; }
    public bool HasExplicitIlName { get; init; }
    public ImmutableArray<SyntaxNode> DecoratorSyntax { get; init; } = [];
    /// <summary>For synthesized members: explicit parameter list.</summary>
    public ImmutableArray<ParameterSymbol> ExplicitParameters { get; init; }
    public TypeSymbol? ExplicitReturnType { get; init; }
    /// <summary>For constructors with parameter properties: the members to assign.</summary>
    public ImmutableArray<(ParameterSymbol Parameter, Symbol Member)> ParameterProperties { get; set; }
    public Func<SourceMethodSymbol, BoundBlock?>? SyntheticBodyFactory { get; init; }
    /// <summary>Set when a constructor delegates to base/this via super(...) as its first statement.</summary>
    public bool IsExpressionBodyImplicitReturn { get; init; }
    /// <summary>For union constructors / conversions: the case this member wraps.</summary>
    public int UnionCaseIndex { get; init; } = -1;
    /// <summary>For an inherited constructor: the base constructor it forwards its arguments to.</summary>
    public MethodSymbol? InheritedBaseConstructor { get; init; }

    public override string Name => _name;
    public override string MetadataName => _emitName ?? _name;
    internal void SetEmitName(string n)
    {
        _emitName = n;
    }

    public override Symbol? ContainingSymbol => ContainerType;
    public NamedTypeSymbol ContainerType { get; } = container;
    public override MethodKind MethodKind => _kind;
    public override bool IsFromSource => true;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    public override bool IsStatic => IsStaticFlag;
    public override bool IsAbstract => IsAbstractFlag;
    public override bool IsVirtual => IsVirtualFlag;
    public override bool IsOverride => IsOverrideFlag;
    public override bool IsSealed => IsSealedFlag;
    public bool ForceAsync { get; init; }
    public override bool IsAsync => Modifiers.IsAsync || ForceAsync;
    public override bool IsIterator => IsGeneratorSyntax;
    public override bool IsInitOnly => _kind == MethodKind.PropertySet && AssociatedProperty is SourcePropertySymbol { IsInitOnlyProperty: true };
    public override bool IsExtensionMethod => Parameters.Length > 0 && Parameters[0].IsThis && IsStatic;
    public override Symbol? AssociatedSymbol => AssociatedProperty;
    public ModifierSet Modifiers { get; } = mods;

    public override ImmutableArray<SourceLocation> Locations =>
        Declaration == null ? [] : [new SourceLocation(File.Tree, IdentifierSpan)];

    public Text.TextSpan IdentifierSpan => Declaration switch
    {
        FunctionDeclarationSyntax f => f.Identifier.Span,
        MethodDeclarationSyntax m => m.Identifier.Span,
        AccessorDeclarationSyntax a => a.Identifier.Span,
        ConstructorDeclarationSyntax c => c.Keyword.Span,
        PropertyDeclarationSyntax p => p.Identifier.Span,
        { } d => d.Span,
        _ => default,
    };

    public override ImmutableArray<AttributeData> GetAttributes()
    {
        return [];
    }

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_typeParamsDone)
            {
                return _typeParams;
            }

            _typeParamsDone = true;
            if (TypeParameterList == null || TypeParameterList.Parameters.Count == 0)
            {
                _typeParams = [];
                return _typeParams;
            }
            _typeParams = SourceTypeParameters.Create(Compilation, File, this, ContainerType, TypeParameterList);
            return _typeParams;
        }
    }

    private Binder CreateBinder()
    {
        return Compilation.CreateBinder(File, this, Compilation.DeclarationDiagnostics);
    }

    public override ImmutableArray<ParameterSymbol> Parameters
    {
        get
        {
            if (!_params.IsDefault)
            {
                return _params;
            }

            if (!ExplicitParameters.IsDefault)
            {
                return _params = ExplicitParameters;
            }

            if (ParameterList == null)
            {
                return _kind == MethodKind.PropertySet && AssociatedProperty != null
                    ? (_params = [new ParameterSymbol("value", AssociatedProperty.Type, 0, this)])
                    : (_params = []);
            }
            Binder binder = CreateBinder();
            _params = binder.BindParameters(this, ParameterList);
            return _params;
        }
    }

    public override TypeSymbol ReturnType
    {
        get
        {
            if (_returnType != null)
            {
                return _returnType;
            }

            if (ExplicitReturnType != null)
            {
                return _returnType = ExplicitReturnType;
            }

            if (_kind is MethodKind.Constructor or MethodKind.StaticConstructor)
            {
                return _returnType = Compilation.GetSpecialType(SpecialType.Void);
            }

            if (ReturnAnnotation != null)
            {
                Binder binder = CreateBinder();
                TypeSymbol t = binder.BindType(ReturnAnnotation.Type, allowVoid: true);
                return _returnType = binder.AdjustDeclaredReturnType(this, t, ReturnAnnotation.Type);
            }
            if (_kind == MethodKind.PropertySet)
            {
                return _returnType = Compilation.GetSpecialType(SpecialType.Void);
            }

            if (_kind == MethodKind.PropertyGet && AssociatedProperty != null)
            {
                return _returnType = AssociatedProperty.Type;
            }
            // infer from the body
            if (_returnTypeInProgress)
            {
                Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.RecursiveInference, File.FilePath, IdentifierSpan, Name));
                return _returnType = ErrorTypeSymbol.Instance;
            }
            _returnTypeInProgress = true;
            _ = GetBoundBody();
            _returnTypeInProgress = false;
            return _returnType ??= _inferredReturn ?? Compilation.GetSpecialType(SpecialType.Void);
        }
    }

    private TypeSymbol? _inferredReturn;
    internal void SetInferredReturnType(TypeSymbol t)
    {
        _inferredReturn = t;
    }

    internal void SetReturnType(TypeSymbol t)
    {
        _returnType = t;
    }

    /// <summary>The bound (not yet lowered) body, or null for abstract / extern / synthesized-IL members.</summary>
    public BoundBlock? GetBoundBody()
    {
        if (_bodyBound)
        {
            return _boundBody;
        }

        _bodyBound = true;
        if (SyntheticBodyFactory != null) { _boundBody = SyntheticBodyFactory(this); return _boundBody; }
        if (BodySyntax == null && !(_kind == MethodKind.Constructor && SynthesizedKind is SynthesizedBodyKind.DefaultConstructor or SynthesizedBodyKind.InheritedConstructor) && _kind != MethodKind.StaticConstructor)
        {
            return null;
        }

        Binder binder = Compilation.CreateBinder(File, this, Compilation.BodyDiagnostics);
        _boundBody = binder.BindMethodBody(this);
        return _boundBody;
    }

    public bool HasBody => BodySyntax != null || SyntheticBodyFactory != null || (_kind == MethodKind.Constructor && SynthesizedKind is SynthesizedBodyKind.DefaultConstructor or SynthesizedBodyKind.InheritedConstructor);

    public override MethodSymbol? OverriddenMethod
    {
        get
        {
            if (_overriddenDone)
            {
                return _overridden;
            }

            _overriddenDone = true;
            if (!IsOverrideFlag || ContainerType.BaseType == null)
            {
                return null;
            }

            _overridden = OverrideResolver.FindOverriddenMethod(this);
            return _overridden;
        }
    }

    public override string ToString()
    {
        return ContainerType.Name + "." + _name;
    }
}

public static class OverrideResolver
{
    public static MethodSymbol? FindOverriddenMethod(MethodSymbol m)
    {
        ArgumentNullException.ThrowIfNull(m);
        NamedTypeSymbol? c = m.ContainingType;
        if (c == null)
        {
            return null;
        }

        if (m.AssociatedSymbol is SourcePropertySymbol { } sp && m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet)
        {
            PropertySymbol? bp = DeclarationChecker.FindOverriddenProperty(c, sp);
            return m.MethodKind == MethodKind.PropertyGet ? bp?.GetMethod : bp?.SetMethod;
        }

        for (NamedTypeSymbol? b = c.BaseType; b != null; b = b.BaseType)
        {
            foreach (Symbol cand in LookupNames(b, m.Name))
            {
                if (cand is MethodSymbol bm && bm.MethodKind == m.MethodKind && bm.Arity == m.Arity && !bm.IsStatic
                    && (bm.IsVirtual || bm.IsAbstract || bm.IsOverride)
                    && SignaturesMatch(bm, m))
                {
                    return bm;
                }
            }
        }
        return null;
    }

    internal static IEnumerable<Symbol> LookupNames(TypeSymbol t, string name)
    {
        foreach (Symbol s in t.GetMembers(name))
        {
            yield return s;
        }

        string flipped = NameUtilities.FlipFirstCase(name);
        if (flipped != name)
        {
            foreach (Symbol s in t.GetMembers(flipped))
            {
                yield return s;
            }
        }
    }

    public static bool SignaturesMatch(MethodSymbol a, MethodSymbol b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Parameters.Length != b.Parameters.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Parameters.Length; i++)
        {
            TypeSymbol ta = a.Parameters[i].Type;
            TypeSymbol tb = b.Parameters[i].Type;
            if (a.IsGenericMethod)
            {
                // compare generic method parameter positions
                if (ta is TypeParameterSymbol { IsMethodTypeParameter: true } pa && tb is TypeParameterSymbol { IsMethodTypeParameter: true } pb)
                {
                    if (pa.Ordinal != pb.Ordinal)
                    {
                        return false;
                    }

                    continue;
                }
            }
            if (!ta.StripNullable().Equals(tb.StripNullable()))
            {
                return false;
            }

            if (a.Parameters[i].RefKind != b.Parameters[i].RefKind)
            {
                return false;
            }
        }
        return true;
    }
}

public static class SourceTypeParameters
{
    public static ImmutableArray<TypeParameterSymbol> Create(Compilation compilation, SourceFile file, Symbol owner, NamedTypeSymbol? _1,
        TypeParameterListSyntax list)
    {
        ArgumentNullException.ThrowIfNull(list);
        ImmutableArray<TypeParameterSymbol>.Builder b = ImmutableArray.CreateBuilder<TypeParameterSymbol>(list.Parameters.Count);
        int ordinalBase = owner is NamedTypeSymbol nt ? nt.ContainingType?.Arity ?? 0 : 0;
        ImmutableArray<TypeParameterSymbol> result = [];
        for (int i = 0; i < list.Parameters.Count; i++)
        {
            TypeParameterSyntax syntax = list.Parameters[i];
            VarianceKind variance = syntax.VarianceKeyword?.Kind switch
            {
                SyntaxKind.InKeyword => VarianceKind.In,
                SyntaxKind.OutKeyword => VarianceKind.Out,
                _ => VarianceKind.None,
            };
            TypeParameterSyntax s = syntax;
            b.Add(new TypeParameterSymbol(syntax.Identifier.Text, ordinalBase + i, owner, variance, tp =>
            {
                Binder binder = compilation.CreateBinder(file, owner, compilation.DeclarationDiagnostics);
                return binder.BindConstraints(s, tp);
            }));
        }
        result = b.MoveToImmutable();
        return result;
    }
}

public sealed class SourceFieldSymbol(Compilation compilation, SourceFile file, Symbol container, string name, Accessibility accessibility,
    bool isStatic, bool isReadOnly, bool isConst, SyntaxNode? declaration, TypeSymbol? type, Func<SourceFieldSymbol, TypeSymbol>? typeFactory = null) : FieldSymbol
{
    private readonly Compilation _compilation = compilation;
    private readonly Symbol _container = container;
    private readonly string _name = name;
    private TypeSymbol? _type = type;
    private bool _typeInProgress;
    private ConstantValue? _const;
    private bool _constDone;
    private readonly Func<SourceFieldSymbol, TypeSymbol>? _typeFactory = typeFactory;

    private Accessibility Accessibility_ { get; } = accessibility;
    public bool IsStaticFlag { get; } = isStatic;
    public bool IsReadOnlyFlag { get; set; } = isReadOnly;
    public bool IsConstFlag { get; } = isConst;
    public SyntaxNode? Declaration { get; } = declaration;
    /// <summary>True for top-level <c>let</c>/<c>const</c> declarations in a module.</summary>
    public bool IsModuleVariable { get; init; }
    public bool IsBackingField { get; init; }
    public VariableDeclaratorSyntax? Declarator { get; init; }
    public bool IsConstDeclaration { get; init; }
    public SourceFile File { get; } = file;
    public bool IsEnumMember { get; init; }
    /// <summary>For members of string-literal-union enums: the original string literal.</summary>
    public string? LiteralText { get; init; }
    public Func<SourceFieldSymbol, ConstantValue?>? ConstantFactory { get; init; }
    public ExpressionSyntax? InitializerSyntax { get; init; }

    public override string Name => _name;
    public override Symbol? ContainingSymbol => _container;
    public override bool IsFromSource => true;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    public override bool IsStatic => IsStaticFlag;
    public override bool IsReadOnly => IsReadOnlyFlag;
    public override bool IsConst => IsConstFlag || (IsConstDeclaration && ConstantValue != null);
    public override ImmutableArray<SourceLocation> Locations =>
        Declaration == null ? [] : [new SourceLocation(File.Tree, Declaration.Span)];

    public override TypeSymbol Type
    {
        get
        {
            if (_type != null)
            {
                return _type;
            }

            if (_typeInProgress)
            {
                _compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.RecursiveInference, File.FilePath, Declaration?.Span ?? default, Name));
                return _type = ErrorTypeSymbol.Instance;
            }
            _typeInProgress = true;
            _type = _typeFactory!(this);
            _typeInProgress = false;
            return _type;
        }
    }

    public override ConstantValue? ConstantValue
    {
        get
        {
            if (!IsConstFlag && !IsEnumMember && !IsConstDeclaration)
            {
                return null;
            }

            if (!_constDone)
            {
                _constDone = true;
                _const = ConstantFactory?.Invoke(this);
            }
            return _const;
        }
    }

    internal void SetType(TypeSymbol t)
    {
        _type = t;
    }

    public override string ToString()
    {
        return _container.Name + "." + _name;
    }
}

public sealed class SourcePropertySymbol(SourceFile file, NamedTypeSymbol container, string name, Accessibility accessibility,
    bool isStatic, SyntaxNode? declaration, Func<SourcePropertySymbol, TypeSymbol>? typeFactory, TypeSymbol? type = null) : PropertySymbol
{
    private readonly NamedTypeSymbol _container = container;
    private readonly string _name = name;
    private TypeSymbol? _type = type;
    private readonly Func<SourcePropertySymbol, TypeSymbol>? _typeFactory = typeFactory;

    private Accessibility Accessibility_ { get; } = accessibility;
    public bool IsStaticFlag { get; } = isStatic;
    public SyntaxNode? Declaration { get; } = declaration;
    public bool IsAbstractFlag { get; init; }
    public bool IsVirtualFlag { get; init; }
    public bool IsOverrideFlag { get; init; }
    public bool IsSealedFlag { get; init; }
    public bool IsInitOnlyProperty { get; set; }
    public bool IsAutoProperty { get; set; }
    public SourceFieldSymbol? BackingField { get; set; }
    public SourceMethodSymbol? Getter { get; set; }
    public SourceMethodSymbol? Setter { get; set; }
    public bool IsOptionalMember { get; init; }
    public SourceFile File { get; } = file;
    public ExpressionSyntax? InitializerSyntax { get; init; }
    public ImmutableArray<ParameterSymbol> IndexerParameters { get; init; }

    public override string Name => _name;
    public override Symbol? ContainingSymbol => _container;
    public override bool IsFromSource => true;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    public override bool IsStatic => IsStaticFlag;
    public override bool IsAbstract => IsAbstractFlag;
    public override bool IsVirtual => IsVirtualFlag;
    public override bool IsOverride => IsOverrideFlag;
    public override bool IsSealed => IsSealedFlag;
    public override MethodSymbol? GetMethod => Getter;
    public override MethodSymbol? SetMethod => Setter;
    public override ImmutableArray<ParameterSymbol> Parameters => IndexerParameters.IsDefault ? [] : IndexerParameters;
    public override ImmutableArray<SourceLocation> Locations =>
        Declaration == null ? [] : [new SourceLocation(File.Tree, Declaration switch
        {
            PropertyDeclarationSyntax p => p.Identifier.Span,
            AccessorDeclarationSyntax a => a.Identifier.Span,
            _ => Declaration.Span,
        })];

    private bool _typeInProgress;

    public override TypeSymbol Type
    {
        get
        {
            if (_type != null)
            {
                return _type;
            }

            if (_typeInProgress)
            {
                // an inferred type that depends on itself (`static a = C.a`), as for fields
                if (_container is SourceNamedType owner)
                {
                    owner.Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.RecursiveInference, File.FilePath, Declaration?.Span ?? default, Name));
                }

                return _type = ErrorTypeSymbol.Instance;
            }

            _typeInProgress = true;
            _type = _typeFactory!(this);
            _typeInProgress = false;
            return _type;
        }
    }

    internal void SetType(TypeSymbol t)
    {
        _type = t;
    }

    public override string ToString()
    {
        return _container.Name + "." + _name;
    }
}
