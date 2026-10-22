using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols.Source;

public abstract class SourceNamedType(Compilation compilation, SourceFile file, SyntaxNode? syntax, string name, Symbol? container, Accessibility accessibility) : NamedTypeSymbol
{
    private ImmutableArray<TypeParameterSymbol> _typeParams;
    private bool _typeParamsDone;
    private ImmutableArray<Symbol> _members;
    private bool _membersBuilding;
    private Dictionary<string, ImmutableArray<Symbol>>? _byName;
    private readonly string _name = name;
    private readonly Symbol? _container = container;

    public Compilation Compilation { get; } = compilation;
    public SourceFile File { get; } = file;
    public SyntaxNode? DeclaringSyntax { get; } = syntax;
    private Accessibility Accessibility_ { get; } = accessibility;
    public override string Name => _name;
    public override Symbol? ContainingSymbol => _container;
    public override bool IsFromSource => true;
    public override Accessibility DeclaredAccessibility => Accessibility_;
    protected virtual TypeParameterListSyntax? TypeParameterListSyntax => null;
    public virtual SyntaxToken? IdentifierToken => null;
    public virtual ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => [];

    public override ImmutableArray<SourceLocation> Locations =>
        IdentifierToken is { } t ? [new SourceLocation(File.Tree, t.Span)] : [];

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_typeParamsDone)
            {
                return _typeParams;
            }

            _typeParamsDone = true;
            TypeParameterListSyntax? list = TypeParameterListSyntax;
            _typeParams = list == null || list.Parameters.Count == 0
                ? []
                : SourceTypeParameters.Create(Compilation, File, this, null, list);
            return _typeParams;
        }
    }

    public override ImmutableArray<TypeSymbol> TypeArguments => TypeParameters.CastArray<TypeSymbol>();

    protected Binder CreateBinder()
    {
        return Compilation.CreateBinder(File, this, Compilation.DeclarationDiagnostics);
    }

    protected abstract ImmutableArray<Symbol> BuildMembers();

    public override ImmutableArray<Symbol> GetMembers()
    {
        if (!_members.IsDefault)
        {
            return _members;
        }

        if (_membersBuilding)
        {
            return [];
        }

        _membersBuilding = true;
        _members = BuildMembers();
        _membersBuilding = false;
        return _members;
    }

    public override ImmutableArray<Symbol> GetMembers(string name)
    {
        if (_byName == null)
        {
            ImmutableArray<Symbol> all = GetMembers();
            if (_membersBuilding)
            {
                return [];
            }

            Dictionary<string, ImmutableArray<Symbol>> d = new(StringComparer.Ordinal);
            foreach (IGrouping<string, Symbol> g in all.GroupBy(m => m.Name))
            {
                d[g.Key] = [.. g];
            }

            _byName = d;
        }
        return _byName.TryGetValue(name, out ImmutableArray<Symbol> r) ? r : [];
    }

    /// <summary>Invalidates cached members (used when synthesized members are added by later phases).</summary>
    internal void ResetMemberCache()
    {
        _byName = null;
    }
}

// ======================================================================= helpers for building members

internal static class MemberFactory
{
    public static SourceMethodSymbol Accessor(Compilation c, SourceFile f, NamedTypeSymbol container, SourcePropertySymbol prop, bool isGetter,
        Accessibility acc, bool isStatic, bool isAbstract, bool isVirtual, bool isOverride, bool isSealed, SynthesizedBodyKind kind,
        AccessorDeclarationSyntax? declaration = null)
    {
        string name = (isGetter ? "get_" : "set_") + prop.Name;
        SourceMethodSymbol m = new(c, f, container, name, isGetter ? MethodKind.PropertyGet : MethodKind.PropertySet, default,
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
            ExplicitParameters = isGetter ? [] : default,
        };
        return m;
    }
}

// ======================================================================= classes

public sealed class SourceClassSymbol(Compilation compilation, SourceFile file, ClassDeclarationSyntax syntax, Symbol container) : SourceNamedType(compilation, file, syntax, syntax.Identifier.Text, container, new ModifierSet(syntax.Modifiers).TypeAccessibility)
{
    private NamedTypeSymbol? _base;
    private bool _baseDone;
    private ImmutableArray<NamedTypeSymbol> _interfaces;

    public ClassDeclarationSyntax Syntax { get; } = syntax;
    public ModifierSet Modifiers { get; } = new ModifierSet(syntax.Modifiers);
    protected override TypeParameterListSyntax? TypeParameterListSyntax => Syntax.TypeParameters;
    public override SyntaxToken? IdentifierToken => Syntax.Identifier;
    public override ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => [.. Syntax.Decorators];
    public override TypeKind TypeKind => TypeKind.Class;
    public override bool IsAbstract => Modifiers.IsAbstract;
    public override bool IsSealed => Modifiers.IsSealed;
    public override bool IsStatic => Modifiers.IsStatic;

    public override NamedTypeSymbol? BaseType
    {
        get
        {
            if (_baseDone)
            {
                return _base;
            }

            _baseDone = true;
            NamedTypeSymbol obj = Compilation.GetSpecialType(SpecialType.Object);
            if (Syntax.ExtendsClause is { Types.Count: > 0 } ext)
            {
                Binder binder = CreateBinder();
                TypeSymbol t = binder.BindType(ext.Types[0]);
                if (t is NamedTypeSymbol { TypeKind: TypeKind.Class } nt && !nt.IsErrorType)
                {
                    if (nt.IsSealed || nt.SpecialType is SpecialType.String or SpecialType.Array or SpecialType.Enum or SpecialType.ValueType)
                    {
                        Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.CannotInheritSealed, File.FilePath, ext.Types[0].Span, nt.ToDisplayString()));
                    }

                    _base = nt;
                    for (NamedTypeSymbol? x = nt; x != null; x = x.BaseType)
                    {
                        if (ReferenceEquals(x.OriginalDefinition, this))
                        {
                            Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.CyclicInheritance, File.FilePath, ext.Types[0].Span, Name));
                            _base = obj;
                            break;
                        }
                    }
                }
                else if (!t.IsErrorType)
                {
                    Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.BaseMustBeClass, File.FilePath, ext.Types[0].Span, t.ToDisplayString()));
                    _base = obj;
                }
                else
                {
                    _base = obj;
                }
            }
            else
            {
                _base = obj;
            }

            return _base;
        }
    }

    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            if (!_interfaces.IsDefault)
            {
                return _interfaces;
            }

            if (Syntax.ImplementsClause == null)
            {
                return _interfaces = [];
            }

            Binder binder = CreateBinder();
            ImmutableArray<NamedTypeSymbol>.Builder b = ImmutableArray.CreateBuilder<NamedTypeSymbol>();
            foreach (TypeSyntax ts in Syntax.ImplementsClause.Types)
            {
                TypeSymbol t = binder.BindType(ts);
                if (t is NamedTypeSymbol { TypeKind: TypeKind.Interface } i)
                {
                    b.Add(i);
                }
                else if (!t.IsErrorType)
                {
                    Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.ImplementsMustBeInterface, File.FilePath, ts.Span, t.ToDisplayString()));
                }
            }
            return _interfaces = b.ToImmutable();
        }
    }

    /// <summary>Instance members with initializers, in declaration order.</summary>
    public ImmutableArray<(Symbol Member, ExpressionSyntax Initializer)> InstanceInitializers { get; private set; } = [];
    public ImmutableArray<(Symbol Member, ExpressionSyntax Initializer)> StaticInitializers { get; private set; } = [];

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        ImmutableArray<Symbol>.Builder b = ImmutableArray.CreateBuilder<Symbol>();
        ImmutableArray<(Symbol, ExpressionSyntax)>.Builder inst = ImmutableArray.CreateBuilder<(Symbol, ExpressionSyntax)>();
        ImmutableArray<(Symbol, ExpressionSyntax)>.Builder stat = ImmutableArray.CreateBuilder<(Symbol, ExpressionSyntax)>();
        Dictionary<(string, bool), List<AccessorDeclarationSyntax>> accessorGroups = [];
        bool hasCtor = false;

        foreach (MemberDeclarationSyntax m in Syntax.Members)
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
                        bool isStatic = new ModifierSet(ad.Modifiers).IsStatic;
                        (string Text, bool isStatic) key = (ad.Identifier.Text, isStatic);
                        if (!accessorGroups.TryGetValue(key, out List<AccessorDeclarationSyntax>? l))
                        {
                            accessorGroups[key] = l = [];
                        }

                        l.Add(ad);
                        break;
                    }
            }
        }

        foreach (List<AccessorDeclarationSyntax> g in accessorGroups.Values)
        {
            b.AddRange(CreateAccessorProperty(g));
        }

        ImmutableArray<MethodSymbol> inherited = !hasCtor && !IsStatic && TypeKind == TypeKind.Class ? InheritedConstructors() : [];
        foreach (MethodSymbol bc in inherited)
        {
            b.Add(new SourceMethodSymbol(Compilation, File, this, ".ctor", MethodKind.Constructor, default, null,
                Modifiers.IsAbstract ? Accessibility.Protected : bc.DeclaredAccessibility == Accessibility.Protected ? Accessibility.Protected : Accessibility.Public)
            {
                SynthesizedKind = SynthesizedBodyKind.InheritedConstructor,
                InheritedBaseConstructor = bc,
                ExplicitParameters = [.. bc.Parameters.Select(p => p.WithType(p.Type, null!))],
            });
        }
        if (!hasCtor && !IsStatic && inherited.IsEmpty)
        {
            b.Add(new SourceMethodSymbol(Compilation, File, this, ".ctor", MethodKind.Constructor, default, null,
                Modifiers.IsAbstract ? Accessibility.Protected : Accessibility.Public)
            {
                SynthesizedKind = SynthesizedBodyKind.DefaultConstructor,
                ExplicitParameters = [],
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
                ExplicitParameters = [],
            });
        }
        return b.ToImmutable();
    }

    /// <summary>TypeScript semantics: a derived class without its own constructor inherits the base class constructors.</summary>
    private ImmutableArray<MethodSymbol> InheritedConstructors()
    {
        NamedTypeSymbol? baseType = BaseType;
        if (baseType == null || baseType.SpecialType is SpecialType.Object or SpecialType.ValueType or SpecialType.Enum)
        {
            return [];
        }

        ImmutableArray<MethodSymbol> ctors = [.. baseType.InstanceConstructors.Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
            || (c.DeclaredAccessibility == Accessibility.Internal && baseType.IsFromSource))];
        return ctors.All(c => c.Parameters.Length == 0) ? [] : ctors;
    }

    private void AddProperty(ImmutableArray<Symbol>.Builder b, ImmutableArray<(Symbol, ExpressionSyntax)>.Builder inst,
        ImmutableArray<(Symbol, ExpressionSyntax)>.Builder stat, PropertyDeclarationSyntax p)
    {
        ModifierSet mods = new(p.Modifiers);
        string name = p.Identifier.Text;
        bool isStatic = mods.IsStatic;
        ImmutableArray<(Symbol, ExpressionSyntax)>.Builder target = isStatic ? stat : inst;
        TypeSymbol typeOf()
        {
            return SourceMemberTypes.BindMemberType(this, p.TypeAnnotation, p.QuestionToken != null, p.Initializer?.Value);
        }

        if (mods.IsPrivate && !mods.IsAbstract)
        {
            SourceFieldSymbol f = new(Compilation, File, this, name, Accessibility.Private, isStatic, mods.IsReadonly, false, p, null, _ => typeOf())
            { InitializerSyntax = p.Initializer?.Value };
            b.Add(f);
            if (p.Initializer != null)
            {
                target.Add((f, p.Initializer.Value));
            }

            return;
        }

        Accessibility acc = mods.MemberAccessibility;
        bool isAbstract = mods.IsAbstract;
        bool isOverride = mods.IsOverride;
        bool isVirtual = !isStatic && !isAbstract && !isOverride && !IsSealed && !mods.IsSealed && acc != Accessibility.Private;
        SourcePropertySymbol prop = new(File, this, name, acc, isStatic, p, _ => typeOf())
        {
            IsAbstractFlag = isAbstract,
            IsVirtualFlag = isVirtual,
            IsOverrideFlag = isOverride,
            IsSealedFlag = mods.IsSealed && isOverride,
            IsAutoProperty = true,
            InitializerSyntax = p.Initializer?.Value,
        };
        bool hasSetter = !mods.IsReadonly;
        prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, acc, isStatic, isAbstract, isVirtual, isOverride, mods.IsSealed && isOverride, SynthesizedBodyKind.AutoGetter);
        if (hasSetter)
        {
            prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, acc, isStatic, isAbstract, isVirtual, isOverride, mods.IsSealed && isOverride, SynthesizedBodyKind.AutoSetter);
        }

        if (!isAbstract)
        {
            SourceFieldSymbol backing = new(Compilation, File, this, "<" + name + ">k__BackingField", Accessibility.Private, isStatic, !hasSetter, false, p, null, _ => prop.Type)
            { IsBackingField = true };
            prop.BackingField = backing;
            b.Add(backing);
            if (p.Initializer != null)
            {
                target.Add((prop, p.Initializer.Value));
            }
        }
        b.Add(prop);
        b.Add(prop.Getter);
        if (prop.Setter != null)
        {
            b.Add(prop.Setter);
        }
    }

    private List<Symbol> CreateAccessorProperty(List<AccessorDeclarationSyntax> group)
    {
        AccessorDeclarationSyntax first = group[0];
        ModifierSet mods = new(first.Modifiers);
        Accessibility acc = mods.MemberAccessibility;
        bool isStatic = mods.IsStatic;
        AccessorDeclarationSyntax? getter = group.FirstOrDefault(a => a.IsGetter);
        AccessorDeclarationSyntax? setter = group.FirstOrDefault(a => !a.IsGetter);
        bool isAbstract = mods.IsAbstract;
        bool isOverride = mods.IsOverride;
        bool isSealed = mods.IsSealed && isOverride;
        bool isVirtual = !isStatic && !isAbstract && !isOverride && !IsSealed && acc != Accessibility.Private;
        SourcePropertySymbol prop = new(File, this, first.Identifier.Text, acc, isStatic, first, p =>
        {
            Binder binder = CreateBinder();
            return getter?.ReturnType != null
                ? binder.BindType(getter.ReturnType.Type)
                : setter != null && setter.Parameters.Parameters.Count > 0 && setter.Parameters.Parameters[0].TypeAnnotation is { } ta
                ? binder.BindType(ta.Type)
                : Compilation.GetSpecialType(SpecialType.Object);
        })
        { IsAbstractFlag = isAbstract, IsVirtualFlag = isVirtual, IsOverrideFlag = isOverride, IsSealedFlag = isSealed };
        List<Symbol> result = [prop];
        if (getter != null)
        {
            prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, acc, isStatic, isAbstract, isVirtual, isOverride, isSealed, SynthesizedBodyKind.None, getter);
            result.Add(prop.Getter);
        }
        if (setter != null)
        {
            prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, acc, isStatic, isAbstract, isVirtual, isOverride, isSealed, SynthesizedBodyKind.None, setter);
            result.Add(prop.Setter);
        }
        return result;
    }

    private SourceMethodSymbol CreateMethod(MethodDeclarationSyntax md)
    {
        ModifierSet mods = new(md.Modifiers);
        bool isStatic = mods.IsStatic;
        Accessibility acc = mods.MemberAccessibility;
        bool isAbstract = mods.IsAbstract;
        bool isOverride = mods.IsOverride;
        bool isVirtual = !isStatic && !isAbstract && !isOverride && !IsSealed && acc != Accessibility.Private;
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
        ModifierSet mods = new(cd.Modifiers);
        return new SourceMethodSymbol(Compilation, File, this, ".ctor", MethodKind.Constructor, mods, cd, mods.MemberAccessibility)
        {
            ParameterList = cd.Parameters,
            BodySyntax = cd.Body,
        };
    }

    private void AddParameterProperties(ImmutableArray<Symbol>.Builder b, ConstructorDeclarationSyntax cd)
    {
        foreach (ParameterSyntax p in cd.Parameters.Parameters)
        {
            ModifierSet pm = new(p.Modifiers);
            if (!(pm.IsPublic || pm.IsPrivate || pm.IsProtected || pm.IsInternal || pm.IsReadonly))
            {
                continue;
            }

            if (p.Identifier == null)
            {
                continue;
            }

            string name = p.Identifier.Text;
            ParameterSyntax ps = p;
            TypeSymbol typeOf()
            {
                return SourceMemberTypes.BindMemberType(this, ps.TypeAnnotation, ps.QuestionToken != null, ps.Initializer?.Value);
            }

            Accessibility acc = pm.IsPrivate ? Accessibility.Private : pm.IsProtected ? Accessibility.Protected : pm.IsInternal ? Accessibility.Internal : Accessibility.Public;
            if (pm.IsPrivate)
            {
                b.Add(new SourceFieldSymbol(Compilation, File, this, name, Accessibility.Private, false, pm.IsReadonly, false, p, null, _ => typeOf()));
                continue;
            }
            SourcePropertySymbol prop = new(File, this, name, acc, false, p, _ => typeOf())
            {
                IsVirtualFlag = !IsSealed && !Modifiers.IsSealed,
                IsAutoProperty = true,
            };
            prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, acc, false, false, prop.IsVirtualFlag, false, false, SynthesizedBodyKind.AutoGetter);
            if (!pm.IsReadonly)
            {
                prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, acc, false, false, prop.IsVirtualFlag, false, false, SynthesizedBodyKind.AutoSetter);
            }

            SourceFieldSymbol backing = new(Compilation, File, this, "<" + name + ">k__BackingField", Accessibility.Private, false, pm.IsReadonly, false, p, null, _ => prop.Type) { IsBackingField = true };
            prop.BackingField = backing;
            b.Add(backing);
            b.Add(prop);
            b.Add(prop.Getter);
            if (prop.Setter != null)
            {
                b.Add(prop.Setter);
            }
        }
    }
}

internal static class SourceMemberTypes
{
    /// <summary>Binds a member's type from its annotation, or infers it from the initializer.</summary>
    public static TypeSymbol BindMemberType(SourceNamedType container, TypeAnnotationSyntax? annotation, bool optional, ExpressionSyntax? initializer)
    {
        Compilation c = container.Compilation;
        Binder binder = c.CreateBinder(container.File, container, c.DeclarationDiagnostics);
        TypeSymbol t;
        if (annotation != null)
        {
            t = binder.BindType(annotation.Type);
        }
        else if (initializer != null)
        {
            t = binder.InferVariableType(initializer);
        }
        else
        {
            c.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.TypeAnnotationRequired, container.File.FilePath, default));
            t = ErrorTypeSymbol.Instance;
        }
        if (optional && t is not NullableTypeSymbol && !t.IsErrorType)
        {
            t = new NullableTypeSymbol(t);
        }

        return t;
    }
}

// ======================================================================= interfaces

public sealed class SourceInterfaceSymbol(Compilation compilation, SourceFile file, InterfaceDeclarationSyntax syntax, Symbol container) : SourceNamedType(compilation, file, syntax, syntax.Identifier.Text, container, new ModifierSet(syntax.Modifiers).TypeAccessibility)
{
    private ImmutableArray<NamedTypeSymbol> _interfaces;

    public InterfaceDeclarationSyntax Syntax { get; } = syntax;
    protected override TypeParameterListSyntax? TypeParameterListSyntax => Syntax.TypeParameters;
    public override SyntaxToken? IdentifierToken => Syntax.Identifier;
    public override ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => [.. Syntax.Decorators];
    public override TypeKind TypeKind => TypeKind.Interface;
    public override bool IsAbstract => true;

    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            if (!_interfaces.IsDefault)
            {
                return _interfaces;
            }

            if (Syntax.ExtendsClause == null)
            {
                return _interfaces = [];
            }

            _interfaces = []; // while binding, a cycle sees no base interfaces
            Binder binder = CreateBinder();
            ImmutableArray<NamedTypeSymbol>.Builder b = ImmutableArray.CreateBuilder<NamedTypeSymbol>();
            foreach (TypeSyntax ts in Syntax.ExtendsClause.Types)
            {
                TypeSymbol t = binder.BindType(ts);
                if (t is NamedTypeSymbol { TypeKind: TypeKind.Interface } i && ReachesSelf(i, new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance)))
                {
                    Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.CyclicInheritance, File.FilePath, ts.Span, Name));
                }
                else if (t is NamedTypeSymbol { TypeKind: TypeKind.Interface } i2)
                {
                    b.Add(i2);
                }
                else if (!t.IsErrorType)
                {
                    Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.ImplementsMustBeInterface, File.FilePath, ts.Span, t.ToDisplayString()));
                }
            }
            return _interfaces = b.ToImmutable();
        }
    }

    private bool ReachesSelf(NamedTypeSymbol i, HashSet<NamedTypeSymbol> seen)
    {
        if (ReferenceEquals(i.OriginalDefinition, this))
        {
            return true;
        }

        if (!seen.Add(i.OriginalDefinition))
        {
            return false;
        }

        foreach (NamedTypeSymbol next in i.Interfaces)
        {
            if (ReachesSelf(next, seen))
            {
                return true;
            }
        }

        return false;
    }

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        ImmutableArray<Symbol>.Builder b = ImmutableArray.CreateBuilder<Symbol>();
        Dictionary<string, List<AccessorDeclarationSyntax>> groups = [];
        foreach (MemberDeclarationSyntax m in Syntax.Members)
        {
            switch (m)
            {
                case PropertyDeclarationSyntax p:
                    {
                        ModifierSet mods = new(p.Modifiers);
                        PropertyDeclarationSyntax ps = p;
                        SourcePropertySymbol prop = new(File, this, p.Identifier.Text, Accessibility.Public, mods.IsStatic, p,
                            _ => SourceMemberTypes.BindMemberType(this, ps.TypeAnnotation, ps.QuestionToken != null, null))
                        { IsAbstractFlag = true };
                        prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, Accessibility.Public, false, true, true, false, false, SynthesizedBodyKind.None);
                        if (!mods.IsReadonly)
                        {
                            prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, Accessibility.Public, false, true, true, false, false, SynthesizedBodyKind.None);
                        }

                        b.Add(prop);
                        b.Add(prop.Getter);
                        if (prop.Setter != null)
                        {
                            b.Add(prop.Setter);
                        }

                        break;
                    }
                case MethodDeclarationSyntax md:
                    {
                        ModifierSet mods = new(md.Modifiers);
                        bool hasBody = md.Body != null;
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
                        if (!groups.TryGetValue(ad.Identifier.Text, out List<AccessorDeclarationSyntax>? l))
                        {
                            groups[ad.Identifier.Text] = l = [];
                        }

                        l.Add(ad);
                        break;
                    }
            }
        }
        foreach (List<AccessorDeclarationSyntax> g in groups.Values)
        {
            AccessorDeclarationSyntax? getter = g.FirstOrDefault(a => a.IsGetter);
            AccessorDeclarationSyntax? setter = g.FirstOrDefault(a => !a.IsGetter);
            AccessorDeclarationSyntax first = g[0];
            SourcePropertySymbol prop = new(File, this, first.Identifier.Text, Accessibility.Public, false, first, _ =>
            {
                Binder binder = CreateBinder();
                return getter?.ReturnType != null
                    ? binder.BindType(getter.ReturnType.Type)
                    : setter != null && setter.Parameters.Parameters.Count > 0 && setter.Parameters.Parameters[0].TypeAnnotation is { } ta
                    ? binder.BindType(ta.Type)
                    : Compilation.GetSpecialType(SpecialType.Object);
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

public sealed class SourceEnumSymbol(Compilation compilation, SourceFile file, EnumDeclarationSyntax syntax, Symbol container) : SourceNamedType(compilation, file, syntax, syntax.Identifier.Text, container, new ModifierSet(syntax.Modifiers).TypeAccessibility)
{
    public EnumDeclarationSyntax Syntax { get; } = syntax;
    public override SyntaxToken? IdentifierToken => Syntax.Identifier;
    public override ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => [.. Syntax.Decorators];
    public override TypeKind TypeKind => TypeKind.Enum;
    public override bool IsSealed => true;
    public override NamedTypeSymbol? BaseType => Compilation.GetSpecialType(SpecialType.Enum);
    public override NamedTypeSymbol? EnumUnderlyingType => Compilation.GetSpecialType(SpecialType.Int32);

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        ImmutableArray<Symbol>.Builder b = ImmutableArray.CreateBuilder<Symbol>();
        // value__ is implicit at emit; members are constants
        long next = 0;
        SeparatedSyntaxList<EnumMemberSyntax> members = Syntax.Members;
        List<SourceFieldSymbol> previous = [];
        for (int i = 0; i < members.Count; i++)
        {
            EnumMemberSyntax m = members[i];
            int idx = i;
            SourceFieldSymbol[] prev = [.. previous];
            EnumMemberSyntax syntax = m;
            SourceFieldSymbol field = new(Compilation, File, this, m.Identifier.ValueText, Accessibility.Public, true, true, true, m, this)
            {
                IsEnumMember = true,
                ConstantFactory = f =>
                {
                    if (syntax.Initializer != null)
                    {
                        Binder binder = CreateBinder();
                        ConstantValue? v = binder.EvaluateEnumInitializer(this, syntax.Initializer.Value, prev);
                        return v is { } cv && cv.Value is int iv ? new ConstantValue(iv) : new ConstantValue(0);
                    }
                    // auto-increment from the previous member
                    if (prev.Length == 0)
                    {
                        return new ConstantValue(0);
                    }

                    ConstantValue? pv = prev[^1].ConstantValue;
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

public sealed class TypeAliasSymbol(Compilation compilation, SourceFile file, TypeAliasDeclarationSyntax syntax) : Symbol
{
    private readonly Compilation _compilation = compilation;
    private bool _typeParamsDone;
    private bool _inProgress;

    public override SymbolKind Kind => SymbolKind.Alias;
    public override string Name => Syntax.Identifier.Text;
    public SourceFile File { get; } = file;
    public TypeAliasDeclarationSyntax Syntax { get; } = syntax;
    public bool IsExported { get; } = new ModifierSet(syntax.Modifiers).IsExport;
    public override bool IsFromSource => true;
    public override Accessibility DeclaredAccessibility => IsExported ? Accessibility.Public : Accessibility.Internal;
    public override ImmutableArray<SourceLocation> Locations => [new SourceLocation(File.Tree, Syntax.Identifier.Span)];

    public ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_typeParamsDone)
            {
                return field;
            }

            _typeParamsDone = true;
            TypeParameterListSyntax? list = Syntax.TypeParameters;
            field = list == null || list.Parameters.Count == 0
                ? []
                : SourceTypeParameters.Create(_compilation, File, this, null, list);
            return field;
        }
    }

    /// <summary>The aliased type (in terms of this alias's type parameters).</summary>
    public TypeSymbol Target
    {
        get
        {
            if (field != null)
            {
                return field;
            }

            if (_inProgress)
            {
                _compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.CircularAlias, File.FilePath, Syntax.Identifier.Span, Name));
                return field = ErrorTypeSymbol.Instance;
            }
            _inProgress = true;
            Binder binder = _compilation.CreateBinder(File, this, _compilation.DeclarationDiagnostics);
            field = binder.BindType(Syntax.Type);
            _inProgress = false;
            return field;
        }
    }
}
