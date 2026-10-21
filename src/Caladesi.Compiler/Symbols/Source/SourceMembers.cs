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
}

/// <summary>A method, function, constructor or accessor declared in source or synthesized by the compiler.</summary>
public sealed class SourceMethodSymbol : MethodSymbol
{
    private readonly Compilation _compilation;
    private readonly SourceFile _file;
    private readonly NamedTypeSymbol _container;
    private readonly string _name;
    private readonly MethodKind _kind;
    private readonly ModifierSet _mods;
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

    public SourceMethodSymbol(Compilation compilation, SourceFile file, NamedTypeSymbol container, string name, MethodKind kind,
        ModifierSet mods, SyntaxNode? declaration, Accessibility accessibility)
    {
        _compilation = compilation;
        _file = file;
        _container = container;
        _name = name;
        _kind = kind;
        _mods = mods;
        Declaration = declaration;
        Accessibility_ = accessibility;
    }

    public SourceFile File => _file;
    public Compilation Compilation => _compilation;
    public SyntaxNode? Declaration { get; }
    private Accessibility Accessibility_ { get; }
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
    public ImmutableArray<SyntaxNode> DecoratorSyntax { get; init; } = ImmutableArray<SyntaxNode>.Empty;
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

    public override string Name => _name;
    public override string MetadataName => _emitName ?? _name;
    internal void SetEmitName(string n) => _emitName = n;
    public override Symbol? ContainingSymbol => _container;
    public NamedTypeSymbol ContainerType => _container;
    public override MethodKind MethodKind => _kind;
    public override bool IsFromSource => true;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    public override bool IsStatic => IsStaticFlag;
    public override bool IsAbstract => IsAbstractFlag;
    public override bool IsVirtual => IsVirtualFlag;
    public override bool IsOverride => IsOverrideFlag;
    public override bool IsSealed => IsSealedFlag;
    public bool ForceAsync { get; init; }
    public override bool IsAsync => _mods.IsAsync || ForceAsync;
    public override bool IsIterator => IsGeneratorSyntax;
    public override bool IsInitOnly => _kind == MethodKind.PropertySet && AssociatedProperty is SourcePropertySymbol { IsInitOnlyProperty: true };
    public override bool IsExtensionMethod => Parameters.Length > 0 && Parameters[0].IsThis && IsStatic;
    public override Symbol? AssociatedSymbol => AssociatedProperty;
    public ModifierSet Modifiers => _mods;

    public override ImmutableArray<SourceLocation> Locations =>
        Declaration == null ? ImmutableArray<SourceLocation>.Empty : [new SourceLocation(_file.Tree, IdentifierSpan)];

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

    public override ImmutableArray<AttributeData> GetAttributes() => ImmutableArray<AttributeData>.Empty;

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_typeParamsDone) return _typeParams;
            _typeParamsDone = true;
            if (TypeParameterList == null || TypeParameterList.Parameters.Count == 0)
            {
                _typeParams = ImmutableArray<TypeParameterSymbol>.Empty;
                return _typeParams;
            }
            _typeParams = SourceTypeParameters.Create(_compilation, _file, this, _container, TypeParameterList);
            return _typeParams;
        }
    }

    private Binder CreateBinder() => _compilation.CreateBinder(_file, this, _compilation.DeclarationDiagnostics);

    public override ImmutableArray<ParameterSymbol> Parameters
    {
        get
        {
            if (!_params.IsDefault) return _params;
            if (!ExplicitParameters.IsDefault) return _params = ExplicitParameters;
            if (ParameterList == null) return _params = ImmutableArray<ParameterSymbol>.Empty;
            var binder = CreateBinder();
            _params = binder.BindParameters(this, ParameterList);
            return _params;
        }
    }

    public override TypeSymbol ReturnType
    {
        get
        {
            if (_returnType != null) return _returnType;
            if (ExplicitReturnType != null) return _returnType = ExplicitReturnType;
            if (_kind is MethodKind.Constructor or MethodKind.StaticConstructor) return _returnType = _compilation.GetSpecialType(SpecialType.Void);
            if (ReturnAnnotation != null)
            {
                var binder = CreateBinder();
                var t = binder.BindType(ReturnAnnotation.Type, allowVoid: true);
                return _returnType = binder.AdjustDeclaredReturnType(this, t, ReturnAnnotation.Type);
            }
            if (_kind == MethodKind.PropertySet) return _returnType = _compilation.GetSpecialType(SpecialType.Void);
            if (_kind == MethodKind.PropertyGet && AssociatedProperty != null) return _returnType = AssociatedProperty.Type;
            // infer from the body
            if (_returnTypeInProgress)
            {
                _compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.RecursiveInference, _file.FilePath, IdentifierSpan, Name));
                return _returnType = ErrorTypeSymbol.Instance;
            }
            _returnTypeInProgress = true;
            var body = GetBoundBody();
            _returnTypeInProgress = false;
            return _returnType ??= _inferredReturn ?? _compilation.GetSpecialType(SpecialType.Void);
        }
    }

    private TypeSymbol? _inferredReturn;
    internal void SetInferredReturnType(TypeSymbol t) => _inferredReturn = t;
    internal void SetReturnType(TypeSymbol t) => _returnType = t;

    /// <summary>The bound (not yet lowered) body, or null for abstract / extern / synthesized-IL members.</summary>
    public BoundBlock? GetBoundBody()
    {
        if (_bodyBound) return _boundBody;
        _bodyBound = true;
        if (SyntheticBodyFactory != null) { _boundBody = SyntheticBodyFactory(this); return _boundBody; }
        if (BodySyntax == null && !(_kind == MethodKind.Constructor && SynthesizedKind == SynthesizedBodyKind.DefaultConstructor)) return null;
        var binder = _compilation.CreateBinder(_file, this, _compilation.BodyDiagnostics);
        _boundBody = binder.BindMethodBody(this);
        return _boundBody;
    }

    public bool HasBody => BodySyntax != null || SyntheticBodyFactory != null || _kind == MethodKind.Constructor && SynthesizedKind == SynthesizedBodyKind.DefaultConstructor;

    public override MethodSymbol? OverriddenMethod
    {
        get
        {
            if (_overriddenDone) return _overridden;
            _overriddenDone = true;
            if (!IsOverrideFlag || _container.BaseType == null) return null;
            _overridden = OverrideResolver.FindOverriddenMethod(this);
            return _overridden;
        }
    }

    public override string ToString() => _container.Name + "." + _name;
}

public static class OverrideResolver
{
    public static MethodSymbol? FindOverriddenMethod(MethodSymbol m)
    {
        var c = m.ContainingType;
        if (c == null) return null;
        for (var b = c.BaseType; b != null; b = b.BaseType)
        {
            foreach (var cand in LookupNames(b, m.Name))
            {
                if (cand is MethodSymbol bm && bm.MethodKind == m.MethodKind && bm.Arity == m.Arity && !bm.IsStatic
                    && (bm.IsVirtual || bm.IsAbstract || bm.IsOverride)
                    && SignaturesMatch(bm, m))
                    return bm;
            }
        }
        return null;
    }

    internal static IEnumerable<Symbol> LookupNames(TypeSymbol t, string name)
    {
        foreach (var s in t.GetMembers(name)) yield return s;
        var flipped = NameUtilities.FlipFirstCase(name);
        if (flipped != name)
            foreach (var s in t.GetMembers(flipped)) yield return s;
    }

    public static bool SignaturesMatch(MethodSymbol a, MethodSymbol b)
    {
        if (a.Parameters.Length != b.Parameters.Length) return false;
        for (var i = 0; i < a.Parameters.Length; i++)
        {
            var ta = a.Parameters[i].Type;
            var tb = b.Parameters[i].Type;
            if (a.IsGenericMethod)
            {
                // compare generic method parameter positions
                if (ta is TypeParameterSymbol { IsMethodTypeParameter: true } pa && tb is TypeParameterSymbol { IsMethodTypeParameter: true } pb)
                {
                    if (pa.Ordinal != pb.Ordinal) return false;
                    continue;
                }
            }
            if (!ta.StripNullable().Equals(tb.StripNullable())) return false;
            if (a.Parameters[i].RefKind != b.Parameters[i].RefKind) return false;
        }
        return true;
    }
}

public static class SourceTypeParameters
{
    public static ImmutableArray<TypeParameterSymbol> Create(Compilation compilation, SourceFile file, Symbol owner, NamedTypeSymbol? containerForScope,
        TypeParameterListSyntax list)
    {
        var b = ImmutableArray.CreateBuilder<TypeParameterSymbol>(list.Parameters.Count);
        var ordinalBase = owner is NamedTypeSymbol nt ? nt.ContainingType?.Arity ?? 0 : 0;
        var result = ImmutableArray<TypeParameterSymbol>.Empty;
        for (var i = 0; i < list.Parameters.Count; i++)
        {
            var syntax = list.Parameters[i];
            var variance = syntax.VarianceKeyword?.Kind switch
            {
                SyntaxKind.InKeyword => VarianceKind.In,
                SyntaxKind.OutKeyword => VarianceKind.Out,
                _ => VarianceKind.None,
            };
            var s = syntax;
            b.Add(new TypeParameterSymbol(syntax.Identifier.Text, ordinalBase + i, owner, variance, tp =>
            {
                var binder = compilation.CreateBinder(file, owner, compilation.DeclarationDiagnostics);
                return binder.BindConstraints(s, tp);
            }));
        }
        result = b.MoveToImmutable();
        return result;
    }
}

public sealed class SourceFieldSymbol : FieldSymbol
{
    private readonly Compilation _compilation;
    private readonly SourceFile _file;
    private readonly Symbol _container;
    private readonly string _name;
    private TypeSymbol? _type;
    private bool _typeInProgress;
    private ConstantValue? _const;
    private bool _constDone;
    private Func<SourceFieldSymbol, TypeSymbol>? _typeFactory;

    public SourceFieldSymbol(Compilation compilation, SourceFile file, Symbol container, string name, Accessibility accessibility,
        bool isStatic, bool isReadOnly, bool isConst, SyntaxNode? declaration, TypeSymbol? type, Func<SourceFieldSymbol, TypeSymbol>? typeFactory = null)
    {
        _compilation = compilation;
        _file = file;
        _container = container;
        _name = name;
        Accessibility_ = accessibility;
        IsStaticFlag = isStatic;
        IsReadOnlyFlag = isReadOnly;
        IsConstFlag = isConst;
        Declaration = declaration;
        _type = type;
        _typeFactory = typeFactory;
    }

    private Accessibility Accessibility_ { get; }
    public bool IsStaticFlag { get; }
    public bool IsReadOnlyFlag { get; set; }
    public bool IsConstFlag { get; }
    public SyntaxNode? Declaration { get; }
    /// <summary>True for top-level <c>let</c>/<c>const</c> declarations in a module.</summary>
    public bool IsModuleVariable { get; init; }
    public bool IsBackingField { get; init; }
    public VariableDeclaratorSyntax? Declarator { get; init; }
    public bool IsConstDeclaration { get; init; }
    public SourceFile File => _file;
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
        Declaration == null ? ImmutableArray<SourceLocation>.Empty : [new SourceLocation(_file.Tree, Declaration.Span)];

    public override TypeSymbol Type
    {
        get
        {
            if (_type != null) return _type;
            if (_typeInProgress)
            {
                _compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.RecursiveInference, _file.FilePath, Declaration?.Span ?? default, Name));
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
            if (!IsConstFlag && !IsEnumMember && !IsConstDeclaration) return null;
            if (!_constDone)
            {
                _constDone = true;
                _const = ConstantFactory?.Invoke(this);
            }
            return _const;
        }
    }

    internal void SetType(TypeSymbol t) => _type = t;
    public override string ToString() => _container.Name + "." + _name;
}

public sealed class SourcePropertySymbol : PropertySymbol
{
    private readonly Compilation _compilation;
    private readonly SourceFile _file;
    private readonly NamedTypeSymbol _container;
    private readonly string _name;
    private TypeSymbol? _type;
    private readonly Func<SourcePropertySymbol, TypeSymbol>? _typeFactory;

    public SourcePropertySymbol(Compilation compilation, SourceFile file, NamedTypeSymbol container, string name, Accessibility accessibility,
        bool isStatic, SyntaxNode? declaration, Func<SourcePropertySymbol, TypeSymbol>? typeFactory, TypeSymbol? type = null)
    {
        _compilation = compilation;
        _file = file;
        _container = container;
        _name = name;
        Accessibility_ = accessibility;
        IsStaticFlag = isStatic;
        Declaration = declaration;
        _typeFactory = typeFactory;
        _type = type;
    }

    private Accessibility Accessibility_ { get; }
    public bool IsStaticFlag { get; }
    public SyntaxNode? Declaration { get; }
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
    public SourceFile File => _file;
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
    public override ImmutableArray<ParameterSymbol> Parameters => IndexerParameters.IsDefault ? ImmutableArray<ParameterSymbol>.Empty : IndexerParameters;
    public override ImmutableArray<SourceLocation> Locations =>
        Declaration == null ? ImmutableArray<SourceLocation>.Empty : [new SourceLocation(_file.Tree, Declaration switch
        {
            PropertyDeclarationSyntax p => p.Identifier.Span,
            AccessorDeclarationSyntax a => a.Identifier.Span,
            _ => Declaration.Span,
        })];

    public override TypeSymbol Type => _type ??= _typeFactory!(this);
    internal void SetType(TypeSymbol t) => _type = t;
    public override string ToString() => _container.Name + "." + _name;
}
