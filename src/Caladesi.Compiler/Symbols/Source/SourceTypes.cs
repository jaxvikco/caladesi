using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols.Source;

public abstract class SourceNamedType : NamedTypeSymbol
{
    private ImmutableArray<TypeParameterSymbol> _typeParams;
    private bool _typeParamsDone;
    private ImmutableArray<Symbol> _members;
    private bool _membersBuilding;
    private Dictionary<string, ImmutableArray<Symbol>>? _byName;
    private readonly string _name;
    private readonly Symbol? _container;

    protected SourceNamedType(Compilation compilation, SourceFile file, SyntaxNode? syntax, string name, Symbol? container, Accessibility accessibility)
    {
        Compilation = compilation;
        File = file;
        DeclaringSyntax = syntax;
        _name = name;
        _container = container;
        Accessibility_ = accessibility;
    }

    public Compilation Compilation { get; }
    public SourceFile File { get; }
    public SyntaxNode? DeclaringSyntax { get; }
    private Accessibility Accessibility_ { get; }
    public override string Name => _name;
    public override Symbol? ContainingSymbol => _container;
    public override bool IsFromSource => true;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    protected virtual TypeParameterListSyntax? TypeParameterListSyntax => null;
    public virtual SyntaxToken? IdentifierToken => null;
    public virtual ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => ImmutableArray<DecoratorSyntax>.Empty;

    public override ImmutableArray<SourceLocation> Locations =>
        IdentifierToken is { } t ? [new SourceLocation(File.Tree, t.Span)] : ImmutableArray<SourceLocation>.Empty;

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_typeParamsDone) return _typeParams;
            _typeParamsDone = true;
            var list = TypeParameterListSyntax;
            _typeParams = list == null || list.Parameters.Count == 0
                ? ImmutableArray<TypeParameterSymbol>.Empty
                : SourceTypeParameters.Create(Compilation, File, this, null, list);
            return _typeParams;
        }
    }

    public override ImmutableArray<TypeSymbol> TypeArguments => TypeParameters.CastArray<TypeSymbol>();

    protected Binder CreateBinder() => Compilation.CreateBinder(File, this, Compilation.DeclarationDiagnostics);

    protected abstract ImmutableArray<Symbol> BuildMembers();

    public override ImmutableArray<Symbol> GetMembers()
    {
        if (!_members.IsDefault) return _members;
        if (_membersBuilding) return ImmutableArray<Symbol>.Empty;
        _membersBuilding = true;
        _members = BuildMembers();
        _membersBuilding = false;
        return _members;
    }

    public override ImmutableArray<Symbol> GetMembers(string name)
    {
        if (_byName == null)
        {
            var all = GetMembers();
            if (_membersBuilding) return ImmutableArray<Symbol>.Empty;
            var d = new Dictionary<string, ImmutableArray<Symbol>>(StringComparer.Ordinal);
            foreach (var g in all.GroupBy(m => m.Name)) d[g.Key] = g.ToImmutableArray();
            _byName = d;
        }
        return _byName.TryGetValue(name, out var r) ? r : ImmutableArray<Symbol>.Empty;
    }

    /// <summary>Invalidates cached members (used when synthesized members are added by later phases).</summary>
    internal void ResetMemberCache() => _byName = null;
}

// ======================================================================= helpers for building members

internal static class MemberFactory
{
    public static SourceMethodSymbol Accessor(Compilation c, SourceFile f, NamedTypeSymbol container, SourcePropertySymbol prop, bool isGetter,
        Accessibility acc, bool isStatic, bool isAbstract, bool isVirtual, bool isOverride, bool isSealed, SynthesizedBodyKind kind,
        AccessorDeclarationSyntax? declaration = null)
    {
        var name = (isGetter ? "get_" : "set_") + prop.Name;
        var m = new SourceMethodSymbol(c, f, container, name, isGetter ? MethodKind.PropertyGet : MethodKind.PropertySet, default,
            declaration, acc)
        {
            SynthesizedKind = declaration?.Body != null ? SynthesizedBodyKind.None : kind,
            IsStaticFlag = isStatic,
            IsAbstractFlag = isAbstract,
            IsVirtualFlag = isVirtual,
            IsOverrideFlag = isOverride,
            IsSealedFlag = isSealed,
            AssociatedProperty = prop,
            ParameterList = declaration?.Parameters,
            BodySyntax = declaration?.Body,
            ExplicitReturnType = isGetter ? null : c.GetSpecialType(SpecialType.Void),
            ExplicitParameters = isGetter ? ImmutableArray<ParameterSymbol>.Empty : default,
        };
        return m;
    }
}

// ======================================================================= classes

public sealed class SourceClassSymbol : SourceNamedType
{
    private readonly ClassDeclarationSyntax _syntax;
    private readonly ModifierSet _mods;
    private NamedTypeSymbol? _base;
    private bool _baseDone;
    private ImmutableArray<NamedTypeSymbol> _interfaces;

    public SourceClassSymbol(Compilation compilation, SourceFile file, ClassDeclarationSyntax syntax, Symbol container)
        : base(compilation, file, syntax, syntax.Identifier.Text, container, new ModifierSet(syntax.Modifiers).TypeAccessibility)
    {
        _syntax = syntax;
        _mods = new ModifierSet(syntax.Modifiers);
    }

    public ClassDeclarationSyntax Syntax => _syntax;
    public ModifierSet Modifiers => _mods;
    protected override TypeParameterListSyntax? TypeParameterListSyntax => _syntax.TypeParameters;
    public override SyntaxToken? IdentifierToken => _syntax.Identifier;
    public override ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => _syntax.Decorators.ToImmutableArray();
    public override TypeKind TypeKind => TypeKind.Class;
    public override bool IsAbstract => _mods.IsAbstract;
    public override bool IsSealed => _mods.IsSealed;
    public override bool IsStatic => _mods.IsStatic;

    public override NamedTypeSymbol? BaseType
    {
        get
        {
            if (_baseDone) return _base;
            _baseDone = true;
            var obj = Compilation.GetSpecialType(SpecialType.Object);
            if (_syntax.ExtendsClause is { Types.Count: > 0 } ext)
            {
                var binder = CreateBinder();
                var t = binder.BindType(ext.Types[0]);
                if (t is NamedTypeSymbol { TypeKind: TypeKind.Class } nt && !nt.IsErrorType)
                {
                    if (nt.IsSealed || nt.SpecialType is SpecialType.String or SpecialType.Array or SpecialType.Enum or SpecialType.ValueType)
                        Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.CannotInheritSealed, File.FilePath, ext.Types[0].Span, nt.ToDisplayString()));
                    _base = nt;
                }
                else if (!t.IsErrorType)
                {
                    Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.BaseMustBeClass, File.FilePath, ext.Types[0].Span, t.ToDisplayString()));
                    _base = obj;
                }
                else _base = obj;
            }
            else _base = obj;
            return _base;
        }
    }

    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            if (!_interfaces.IsDefault) return _interfaces;
            if (_syntax.ImplementsClause == null) return _interfaces = ImmutableArray<NamedTypeSymbol>.Empty;
            var binder = CreateBinder();
            var b = ImmutableArray.CreateBuilder<NamedTypeSymbol>();
            foreach (var ts in _syntax.ImplementsClause.Types)
            {
                var t = binder.BindType(ts);
                if (t is NamedTypeSymbol { TypeKind: TypeKind.Interface } i) b.Add(i);
                else if (!t.IsErrorType)
                    Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.ImplementsMustBeInterface, File.FilePath, ts.Span, t.ToDisplayString()));
            }
            return _interfaces = b.ToImmutable();
        }
    }

    /// <summary>Instance members with initializers, in declaration order.</summary>
    public ImmutableArray<(Symbol Member, ExpressionSyntax Initializer)> InstanceInitializers { get; private set; } = ImmutableArray<(Symbol, ExpressionSyntax)>.Empty;
    public ImmutableArray<(Symbol Member, ExpressionSyntax Initializer)> StaticInitializers { get; private set; } = ImmutableArray<(Symbol, ExpressionSyntax)>.Empty;

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        var b = ImmutableArray.CreateBuilder<Symbol>();
        var inst = ImmutableArray.CreateBuilder<(Symbol, ExpressionSyntax)>();
        var stat = ImmutableArray.CreateBuilder<(Symbol, ExpressionSyntax)>();
        var accessorGroups = new Dictionary<(string, bool), List<AccessorDeclarationSyntax>>();
        var hasCtor = false;

        foreach (var m in _syntax.Members)
        {
            switch (m)
            {
                case PropertyDeclarationSyntax p:
                    AddProperty(b, inst, stat, p);
                    break;
                case MethodDeclarationSyntax md:
                    b.Add(CreateMethod(md));
                    break;
                case ConstructorDeclarationSyntax cd:
                    hasCtor = true;
                    b.Add(CreateConstructor(cd));
                    AddParameterProperties(b, cd);
                    break;
                case AccessorDeclarationSyntax ad:
                {
                    var isStatic = new ModifierSet(ad.Modifiers).IsStatic;
                    var key = (ad.Identifier.Text, isStatic);
                    if (!accessorGroups.TryGetValue(key, out var l)) accessorGroups[key] = l = new List<AccessorDeclarationSyntax>();
                    l.Add(ad);
                    break;
                }
            }
        }

        foreach (var g in accessorGroups.Values) b.AddRange(CreateAccessorProperty(g));

        if (!hasCtor && !IsStatic)
        {
            b.Add(new SourceMethodSymbol(Compilation, File, this, ".ctor", MethodKind.Constructor, default, null,
                _mods.IsAbstract ? Accessibility.Protected : Accessibility.Public)
            {
                SynthesizedKind = SynthesizedBodyKind.DefaultConstructor,
                ExplicitParameters = ImmutableArray<ParameterSymbol>.Empty,
            });
        }
        InstanceInitializers = inst.ToImmutable();
        StaticInitializers = stat.ToImmutable();
        if (StaticInitializers.Length > 0)
        {
            b.Add(new SourceMethodSymbol(Compilation, File, this, ".cctor", MethodKind.StaticConstructor, default, null, Accessibility.Private)
            {
                SynthesizedKind = SynthesizedBodyKind.StaticConstructor,
                IsStaticFlag = true,
                ExplicitParameters = ImmutableArray<ParameterSymbol>.Empty,
            });
        }
        return b.ToImmutable();
    }

    private void AddProperty(ImmutableArray<Symbol>.Builder b, ImmutableArray<(Symbol, ExpressionSyntax)>.Builder inst,
        ImmutableArray<(Symbol, ExpressionSyntax)>.Builder stat, PropertyDeclarationSyntax p)
    {
        var mods = new ModifierSet(p.Modifiers);
        var name = p.Identifier.Text;
        var isStatic = mods.IsStatic;
        var target = isStatic ? stat : inst;
        Func<TypeSymbol> typeOf = () => SourceMemberTypes.BindMemberType(this, p.TypeAnnotation, p.QuestionToken != null, p.Initializer?.Value);

        if (mods.IsPrivate && !mods.IsAbstract)
        {
            var f = new SourceFieldSymbol(Compilation, File, this, name, Accessibility.Private, isStatic, mods.IsReadonly, false, p, null, _ => typeOf())
            { InitializerSyntax = p.Initializer?.Value };
            b.Add(f);
            if (p.Initializer != null) target.Add((f, p.Initializer.Value));
            return;
        }

        var acc = mods.MemberAccessibility;
        var isAbstract = mods.IsAbstract;
        var isOverride = mods.IsOverride;
        var isVirtual = !isStatic && !isAbstract && !isOverride && !IsSealed && !mods.IsSealed && acc != Accessibility.Private;
        var prop = new SourcePropertySymbol(Compilation, File, this, name, acc, isStatic, p, _ => typeOf())
        {
            IsAbstractFlag = isAbstract,
            IsVirtualFlag = isVirtual,
            IsOverrideFlag = isOverride,
            IsSealedFlag = mods.IsSealed && isOverride,
            IsAutoProperty = true,
            InitializerSyntax = p.Initializer?.Value,
        };
        var hasSetter = !mods.IsReadonly;
        prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, acc, isStatic, isAbstract, isVirtual, isOverride, mods.IsSealed && isOverride, SynthesizedBodyKind.AutoGetter);
        if (hasSetter)
            prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, acc, isStatic, isAbstract, isVirtual, isOverride, mods.IsSealed && isOverride, SynthesizedBodyKind.AutoSetter);
        if (!isAbstract)
        {
            var backing = new SourceFieldSymbol(Compilation, File, this, "<" + name + ">k__BackingField", Accessibility.Private, isStatic, !hasSetter, false, p, null, _ => prop.Type)
            { IsBackingField = true };
            prop.BackingField = backing;
            b.Add(backing);
            if (p.Initializer != null) target.Add((prop, p.Initializer.Value));
        }
        b.Add(prop);
        b.Add(prop.Getter);
        if (prop.Setter != null) b.Add(prop.Setter);
    }

    private IEnumerable<Symbol> CreateAccessorProperty(List<AccessorDeclarationSyntax> group)
    {
        var first = group[0];
        var mods = new ModifierSet(first.Modifiers);
        var acc = mods.MemberAccessibility;
        var isStatic = mods.IsStatic;
        var getter = group.FirstOrDefault(a => a.IsGetter);
        var setter = group.FirstOrDefault(a => !a.IsGetter);
        var isAbstract = mods.IsAbstract;
        var isOverride = mods.IsOverride;
        var isVirtual = !isStatic && !isAbstract && !isOverride && !IsSealed && acc != Accessibility.Private;
        var prop = new SourcePropertySymbol(Compilation, File, this, first.Identifier.Text, acc, isStatic, first, p =>
        {
            var binder = CreateBinder();
            if (getter?.ReturnType != null) return binder.BindType(getter.ReturnType.Type);
            if (setter != null && setter.Parameters.Parameters.Count > 0 && setter.Parameters.Parameters[0].TypeAnnotation is { } ta)
                return binder.BindType(ta.Type);
            return Compilation.GetSpecialType(SpecialType.Object);
        })
        { IsAbstractFlag = isAbstract, IsVirtualFlag = isVirtual, IsOverrideFlag = isOverride };
        var result = new List<Symbol> { prop };
        if (getter != null)
        {
            prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, acc, isStatic, isAbstract, isVirtual, isOverride, false, SynthesizedBodyKind.None, getter);
            result.Add(prop.Getter);
        }
        if (setter != null)
        {
            prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, acc, isStatic, isAbstract, isVirtual, isOverride, false, SynthesizedBodyKind.None, setter);
            result.Add(prop.Setter);
        }
        return result;
    }

    private SourceMethodSymbol CreateMethod(MethodDeclarationSyntax md)
    {
        var mods = new ModifierSet(md.Modifiers);
        var isStatic = mods.IsStatic;
        var acc = mods.MemberAccessibility;
        var isAbstract = mods.IsAbstract;
        var isOverride = mods.IsOverride;
        var isVirtual = !isStatic && !isAbstract && !isOverride && !IsSealed && acc != Accessibility.Private;
        return new SourceMethodSymbol(Compilation, File, this, md.Identifier.Text, MethodKind.Ordinary, mods, md, acc)
        {
            IsStaticFlag = isStatic,
            IsAbstractFlag = isAbstract,
            IsVirtualFlag = isVirtual,
            IsOverrideFlag = isOverride,
            IsSealedFlag = isOverride && mods.IsSealed,
            ParameterList = md.Parameters,
            TypeParameterList = md.TypeParameters,
            ReturnAnnotation = md.ReturnType,
            BodySyntax = md.Body,
            IsGeneratorSyntax = md.AsteriskToken != null,
        };
    }

    private SourceMethodSymbol CreateConstructor(ConstructorDeclarationSyntax cd)
    {
        var mods = new ModifierSet(cd.Modifiers);
        return new SourceMethodSymbol(Compilation, File, this, ".ctor", MethodKind.Constructor, mods, cd, mods.MemberAccessibility)
        {
            ParameterList = cd.Parameters,
            BodySyntax = cd.Body,
        };
    }

    private void AddParameterProperties(ImmutableArray<Symbol>.Builder b, ConstructorDeclarationSyntax cd)
    {
        foreach (var p in cd.Parameters.Parameters)
        {
            var pm = new ModifierSet(p.Modifiers);
            if (!(pm.IsPublic || pm.IsPrivate || pm.IsProtected || pm.IsInternal || pm.IsReadonly)) continue;
            if (p.Identifier == null) continue;
            var name = p.Identifier.Text;
            var ps = p;
            Func<TypeSymbol> typeOf = () => SourceMemberTypes.BindMemberType(this, ps.TypeAnnotation, ps.QuestionToken != null, ps.Initializer?.Value);
            var acc = pm.IsPrivate ? Accessibility.Private : pm.IsProtected ? Accessibility.Protected : pm.IsInternal ? Accessibility.Internal : Accessibility.Public;
            if (pm.IsPrivate)
            {
                b.Add(new SourceFieldSymbol(Compilation, File, this, name, Accessibility.Private, false, pm.IsReadonly, false, p, null, _ => typeOf()));
                continue;
            }
            var prop = new SourcePropertySymbol(Compilation, File, this, name, acc, false, p, _ => typeOf())
            {
                IsVirtualFlag = !IsSealed && !_mods.IsSealed,
                IsAutoProperty = true,
            };
            prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, acc, false, false, prop.IsVirtualFlag, false, false, SynthesizedBodyKind.AutoGetter);
            if (!pm.IsReadonly)
                prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, acc, false, false, prop.IsVirtualFlag, false, false, SynthesizedBodyKind.AutoSetter);
            var backing = new SourceFieldSymbol(Compilation, File, this, "<" + name + ">k__BackingField", Accessibility.Private, false, pm.IsReadonly, false, p, null, _ => prop.Type) { IsBackingField = true };
            prop.BackingField = backing;
            b.Add(backing);
            b.Add(prop);
            b.Add(prop.Getter);
            if (prop.Setter != null) b.Add(prop.Setter);
        }
    }
}

internal static class SourceMemberTypes
{
    /// <summary>Binds a member's type from its annotation, or infers it from the initializer.</summary>
    public static TypeSymbol BindMemberType(SourceNamedType container, TypeAnnotationSyntax? annotation, bool optional, ExpressionSyntax? initializer)
    {
        var c = container.Compilation;
        var binder = c.CreateBinder(container.File, container, c.DeclarationDiagnostics);
        TypeSymbol t;
        if (annotation != null) t = binder.BindType(annotation.Type);
        else if (initializer != null) t = binder.InferVariableType(initializer);
        else
        {
            c.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.TypeAnnotationRequired, container.File.FilePath, annotation?.Span ?? initializer?.Span ?? default));
            t = ErrorTypeSymbol.Instance;
        }
        if (optional && t is not NullableTypeSymbol && !t.IsErrorType) t = new NullableTypeSymbol(t);
        return t;
    }
}

// ======================================================================= interfaces

public sealed class SourceInterfaceSymbol : SourceNamedType
{
    private readonly InterfaceDeclarationSyntax _syntax;
    private ImmutableArray<NamedTypeSymbol> _interfaces;

    public SourceInterfaceSymbol(Compilation compilation, SourceFile file, InterfaceDeclarationSyntax syntax, Symbol container)
        : base(compilation, file, syntax, syntax.Identifier.Text, container, new ModifierSet(syntax.Modifiers).TypeAccessibility)
    {
        _syntax = syntax;
    }

    public InterfaceDeclarationSyntax Syntax => _syntax;
    protected override TypeParameterListSyntax? TypeParameterListSyntax => _syntax.TypeParameters;
    public override SyntaxToken? IdentifierToken => _syntax.Identifier;
    public override ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => _syntax.Decorators.ToImmutableArray();
    public override TypeKind TypeKind => TypeKind.Interface;
    public override bool IsAbstract => true;

    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            if (!_interfaces.IsDefault) return _interfaces;
            if (_syntax.ExtendsClause == null) return _interfaces = ImmutableArray<NamedTypeSymbol>.Empty;
            var binder = CreateBinder();
            var b = ImmutableArray.CreateBuilder<NamedTypeSymbol>();
            foreach (var ts in _syntax.ExtendsClause.Types)
            {
                var t = binder.BindType(ts);
                if (t is NamedTypeSymbol { TypeKind: TypeKind.Interface } i) b.Add(i);
                else if (!t.IsErrorType)
                    Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.ImplementsMustBeInterface, File.FilePath, ts.Span, t.ToDisplayString()));
            }
            return _interfaces = b.ToImmutable();
        }
    }

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        var b = ImmutableArray.CreateBuilder<Symbol>();
        var groups = new Dictionary<string, List<AccessorDeclarationSyntax>>();
        foreach (var m in _syntax.Members)
        {
            switch (m)
            {
                case PropertyDeclarationSyntax p:
                {
                    var mods = new ModifierSet(p.Modifiers);
                    var ps = p;
                    var prop = new SourcePropertySymbol(Compilation, File, this, p.Identifier.Text, Accessibility.Public, mods.IsStatic, p,
                        _ => SourceMemberTypes.BindMemberType(this, ps.TypeAnnotation, ps.QuestionToken != null, null))
                    { IsAbstractFlag = true };
                    prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, Accessibility.Public, false, true, true, false, false, SynthesizedBodyKind.None);
                    if (!mods.IsReadonly)
                        prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, Accessibility.Public, false, true, true, false, false, SynthesizedBodyKind.None);
                    b.Add(prop);
                    b.Add(prop.Getter);
                    if (prop.Setter != null) b.Add(prop.Setter);
                    break;
                }
                case MethodDeclarationSyntax md:
                {
                    var mods = new ModifierSet(md.Modifiers);
                    var hasBody = md.Body != null;
                    b.Add(new SourceMethodSymbol(Compilation, File, this, md.Identifier.Text, MethodKind.Ordinary, mods, md, Accessibility.Public)
                    {
                        IsAbstractFlag = !hasBody,
                        IsVirtualFlag = hasBody,
                        IsStaticFlag = mods.IsStatic,
                        ParameterList = md.Parameters,
                        TypeParameterList = md.TypeParameters,
                        ReturnAnnotation = md.ReturnType,
                        BodySyntax = md.Body,
                    });
                    break;
                }
                case AccessorDeclarationSyntax ad:
                {
                    if (!groups.TryGetValue(ad.Identifier.Text, out var l)) groups[ad.Identifier.Text] = l = new List<AccessorDeclarationSyntax>();
                    l.Add(ad);
                    break;
                }
            }
        }
        foreach (var g in groups.Values)
        {
            var getter = g.FirstOrDefault(a => a.IsGetter);
            var setter = g.FirstOrDefault(a => !a.IsGetter);
            var first = g[0];
            var prop = new SourcePropertySymbol(Compilation, File, this, first.Identifier.Text, Accessibility.Public, false, first, _ =>
            {
                var binder = CreateBinder();
                if (getter?.ReturnType != null) return binder.BindType(getter.ReturnType.Type);
                if (setter != null && setter.Parameters.Parameters.Count > 0 && setter.Parameters.Parameters[0].TypeAnnotation is { } ta)
                    return binder.BindType(ta.Type);
                return Compilation.GetSpecialType(SpecialType.Object);
            })
            { IsAbstractFlag = true };
            if (getter != null) { prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, Accessibility.Public, false, true, true, false, false, SynthesizedBodyKind.None); b.Add(prop.Getter); }
            if (setter != null) { prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, Accessibility.Public, false, true, true, false, false, SynthesizedBodyKind.None); b.Add(prop.Setter); }
            b.Add(prop);
        }
        return b.ToImmutable();
    }
}

// ======================================================================= enums

public sealed class SourceEnumSymbol : SourceNamedType
{
    private readonly EnumDeclarationSyntax _syntax;

    public SourceEnumSymbol(Compilation compilation, SourceFile file, EnumDeclarationSyntax syntax, Symbol container)
        : base(compilation, file, syntax, syntax.Identifier.Text, container, new ModifierSet(syntax.Modifiers).TypeAccessibility)
    {
        _syntax = syntax;
    }

    public EnumDeclarationSyntax Syntax => _syntax;
    public override SyntaxToken? IdentifierToken => _syntax.Identifier;
    public override ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => _syntax.Decorators.ToImmutableArray();
    public override TypeKind TypeKind => TypeKind.Enum;
    public override bool IsSealed => true;
    public override NamedTypeSymbol? BaseType => Compilation.GetSpecialType(SpecialType.Enum);
    public override NamedTypeSymbol? EnumUnderlyingType => Compilation.GetSpecialType(SpecialType.Int32);

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        var b = ImmutableArray.CreateBuilder<Symbol>();
        // value__ is implicit at emit; members are constants
        long next = 0;
        var members = _syntax.Members;
        var previous = new List<SourceFieldSymbol>();
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var idx = i;
            var prev = previous.ToArray();
            var syntax = m;
            var field = new SourceFieldSymbol(Compilation, File, this, m.Identifier.ValueText, Accessibility.Public, true, true, true, m, this)
            {
                IsEnumMember = true,
                ConstantFactory = f =>
                {
                    if (syntax.Initializer != null)
                    {
                        var binder = CreateBinder();
                        var v = binder.EvaluateEnumInitializer(this, syntax.Initializer.Value, prev);
                        if (v is { } cv && cv.Value is int iv) { return new ConstantValue(iv); }
                        return new ConstantValue(0);
                    }
                    // auto-increment from the previous member
                    if (prev.Length == 0) return new ConstantValue(0);
                    var pv = prev[^1].ConstantValue;
                    return new ConstantValue(pv is { Value: int p } ? p + 1 : 0);
                },
            };
            _ = next;
            previous.Add(field);
            b.Add(field);
        }
        return b.ToImmutable();
    }
}

// ======================================================================= type aliases

public sealed class TypeAliasSymbol : Symbol
{
    private readonly Compilation _compilation;
    private readonly SourceFile _file;
    private readonly TypeAliasDeclarationSyntax _syntax;
    private ImmutableArray<TypeParameterSymbol> _typeParams;
    private bool _typeParamsDone;
    private TypeSymbol? _target;
    private bool _inProgress;

    public TypeAliasSymbol(Compilation compilation, SourceFile file, TypeAliasDeclarationSyntax syntax)
    {
        _compilation = compilation;
        _file = file;
        _syntax = syntax;
        IsExported = new ModifierSet(syntax.Modifiers).IsExport;
    }

    public override SymbolKind Kind => SymbolKind.Alias;
    public override string Name => _syntax.Identifier.Text;
    public SourceFile File => _file;
    public TypeAliasDeclarationSyntax Syntax => _syntax;
    public bool IsExported { get; }
    public override bool IsFromSource => true;
    public override Accessibility DeclaredAccessibility => IsExported ? Accessibility.Public : Accessibility.Internal;
    public override ImmutableArray<SourceLocation> Locations => [new SourceLocation(_file.Tree, _syntax.Identifier.Span)];

    public ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_typeParamsDone) return _typeParams;
            _typeParamsDone = true;
            var list = _syntax.TypeParameters;
            _typeParams = list == null || list.Parameters.Count == 0
                ? ImmutableArray<TypeParameterSymbol>.Empty
                : SourceTypeParameters.Create(_compilation, _file, this, null, list);
            return _typeParams;
        }
    }

    /// <summary>The aliased type (in terms of this alias's type parameters).</summary>
    public TypeSymbol Target
    {
        get
        {
            if (_target != null) return _target;
            if (_inProgress)
            {
                _compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.CircularAlias, _file.FilePath, _syntax.Identifier.Span, Name));
                return _target = ErrorTypeSymbol.Instance;
            }
            _inProgress = true;
            var binder = _compilation.CreateBinder(_file, this, _compilation.DeclarationDiagnostics);
            _target = binder.BindType(_syntax.Type);
            _inProgress = false;
            return _target;
        }
    }
}
