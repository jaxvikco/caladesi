using System.Collections.Immutable;
using System.Text;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols.Source;

internal readonly record struct AliasCreation(SourceNamedType? Type, TypeAliasSymbol? Alias);

internal static class TypeAliasDeclarations
{
    /// <summary>Chooses the symbol kind for a <c>type X = ...</c> declaration based on the right-hand side.</summary>
    public static AliasCreation Create(Compilation c, SourceFile file, TypeAliasDeclarationSyntax a)
    {
        TypeSyntax rhs = a.Type;
        while (rhs is ParenthesizedTypeSyntax p)
        {
            rhs = p.Type;
        }

        ModifierSet mods = new(a.Modifiers);
        switch (rhs)
        {
            case ObjectTypeSyntax obj:
                return new AliasCreation(new SourceRecordSymbol(c, file, a, obj, mods.IsStruct, file.Namespace), null);
            case UnionTypeSyntax u:
                {
                    List<TypeSyntax> members = [.. u.Types];
                    if (members.Count > 0 && members.All(m => m is LiteralTypeSyntax { Literal.Kind: SyntaxKind.StringLiteralExpression }))
                    {
                        return new AliasCreation(new SourceLiteralEnumSymbol(c, file, a, u, file.Namespace), null);
                    }

                    List<TypeSyntax> nonNull = [.. members.Where(m => m is not LiteralTypeSyntax { Literal.Kind: SyntaxKind.NullLiteralExpression })];
                    bool hasNull = nonNull.Count != members.Count;
                    if (nonNull.Count >= 2)
                    {
                        return new AliasCreation(new SourceUnionSymbol(c, file, a, nonNull, hasNull, file.Namespace), null);
                    }

                    break;
                }
        }
        if (mods.IsStruct)
        {
            c.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.StructOnlyForObjectTypes, file.FilePath, a.Identifier.Span));
        }

        return new AliasCreation(null, new TypeAliasSymbol(c, file, a));
    }
}

/// <summary><c>type Point = { x: number; y: number }</c> : a sealed record class (or readonly record struct).</summary>
public sealed class SourceRecordSymbol(Compilation compilation, SourceFile file, TypeAliasDeclarationSyntax alias, ObjectTypeSyntax syntax, bool isStruct, Symbol container) : SourceNamedType(compilation, file, alias, alias.Identifier.Text, container, new ModifierSet(alias.Modifiers).TypeAccessibility)
{
    private readonly ObjectTypeSyntax _syntax = syntax;
    private readonly bool _isStruct = isStruct;
    private ImmutableArray<NamedTypeSymbol> _interfaces;

    public TypeAliasDeclarationSyntax Alias { get; } = alias;
    protected override TypeParameterListSyntax? TypeParameterListSyntax => Alias.TypeParameters;
    public override SyntaxToken? IdentifierToken => Alias.Identifier;
    public override ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => [.. Alias.Decorators];
    public override TypeKind TypeKind => _isStruct ? TypeKind.Struct : TypeKind.Class;
    public override bool IsRecord => true;
    public override bool IsSealed => true;
    public override bool IsReadOnlyStruct => _isStruct;
    public override NamedTypeSymbol? BaseType => Compilation.GetSpecialType(_isStruct ? SpecialType.ValueType : SpecialType.Object);

    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            if (!_interfaces.IsDefault)
            {
                return _interfaces;
            }

            NamedTypeSymbol? eq = Compilation.GetWellKnownType("System.IEquatable`1");
            NamedTypeSymbol self = IsGenericType ? Construct(TypeParameters.CastArray<TypeSymbol>()) : this;
            return _interfaces = eq == null ? [] : [eq.Construct(self)];
        }
    }

    /// <summary>The record's properties in declaration order.</summary>
    public ImmutableArray<SourcePropertySymbol> Properties => [.. GetMembers().OfType<SourcePropertySymbol>().Where(p => p.BackingField != null)];

    public SourceMethodSymbol? PrimaryConstructor => GetMembers().OfType<SourceMethodSymbol>().FirstOrDefault(m => m.MethodKind == MethodKind.Constructor && m.SynthesizedKind == SynthesizedBodyKind.RecordConstructor);

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        ImmutableArray<Symbol>.Builder b = ImmutableArray.CreateBuilder<Symbol>();
        List<SourcePropertySymbol> props = [];
        NamedTypeSymbol self = IsGenericType ? Construct(TypeParameters.CastArray<TypeSymbol>()) : this;

        foreach (MemberDeclarationSyntax m in _syntax.Members)
        {
            if (m is not PropertyDeclarationSyntax p)
            {
                Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.ObjectTypeMemberNotProperty, File.FilePath, m.Span));
                continue;
            }
            if (p.Identifier.IsMissing)
            {
                continue;
            }

            PropertyDeclarationSyntax ps = p;
            SourcePropertySymbol prop = new(File, this, p.Identifier.Text, Accessibility.Public, false, p,
                _ => SourceMemberTypes.BindMemberType(this, ps.TypeAnnotation, ps.QuestionToken != null, null))
            {
                IsAutoProperty = true,
                IsInitOnlyProperty = true,
                IsOptionalMember = p.QuestionToken != null,
            };
            prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, Accessibility.Public, false, false, false, false, false, SynthesizedBodyKind.AutoGetter);
            prop.Setter = MemberFactory.Accessor(Compilation, File, this, prop, false, Accessibility.Public, false, false, false, false, false, SynthesizedBodyKind.AutoSetter);
            SourceFieldSymbol backing = new(Compilation, File, this, "<" + prop.Name + ">k__BackingField", Accessibility.Private, false, true, false, p, null, _ => prop.Type) { IsBackingField = true };
            prop.BackingField = backing;
            if (props.Any(x => x.Name == prop.Name))
            {
                Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.DuplicateMember, File.FilePath, p.Identifier.Span, prop.Name, Name));
                continue;
            }
            props.Add(prop);
            b.Add(backing);
            b.Add(prop);
            b.Add(prop.Getter);
            b.Add(prop.Setter);
        }

        NamedTypeSymbol voidT = Compilation.GetSpecialType(SpecialType.Void);
        NamedTypeSymbol boolT = Compilation.GetSpecialType(SpecialType.Boolean);
        NamedTypeSymbol objT = Compilation.GetSpecialType(SpecialType.Object);
        NamedTypeSymbol intT = Compilation.GetSpecialType(SpecialType.Int32);
        NamedTypeSymbol strT = Compilation.GetSpecialType(SpecialType.String);

        // primary constructor
        ImmutableArray<ParameterSymbol>.Builder ctorParams = ImmutableArray.CreateBuilder<ParameterSymbol>();
        for (int i = 0; i < props.Count; i++)
        {
            SourcePropertySymbol prop = props[i];
            ctorParams.Add(new ParameterSymbol(prop.Name, prop.Type, i, null, RefKind.None, false,
                prop.IsOptionalMember ? ConstantValue.Null : null, prop.IsOptionalMember));
        }
        SourceMethodSymbol ctor = new(Compilation, File, this, ".ctor", MethodKind.Constructor, default, null, Accessibility.Public)
        {
            SynthesizedKind = SynthesizedBodyKind.RecordConstructor,
            ExplicitParameters = [.. ctorParams.ToImmutable().Select(p => p.WithType(p.Type, null!))],
        };
        b.Add(ctor);

        if (!_isStruct)
        {
            // protected copy constructor used by <Clone>$
            b.Add(new SourceMethodSymbol(Compilation, File, this, ".ctor", MethodKind.Constructor, default, null, Accessibility.Protected)
            {
                SynthesizedKind = SynthesizedBodyKind.RecordCopyConstructor,
                ExplicitParameters = [new ParameterSymbol("original", self, 0, null)],
            });
            b.Add(new SourceMethodSymbol(Compilation, File, this, "<Clone>$", MethodKind.Ordinary, default, null, Accessibility.Public)
            {
                SynthesizedKind = SynthesizedBodyKind.RecordClone,
                IsVirtualFlag = true,
                ExplicitParameters = [],
                ExplicitReturnType = self,
            });
            NamedTypeSymbol typeT = Compilation.GetSpecialType(SpecialType.Type);
            SourcePropertySymbol eqContract = new(File, this, "EqualityContract", Accessibility.Protected, false, null, null, typeT) { IsVirtualFlag = true };
            eqContract.Getter = new SourceMethodSymbol(Compilation, File, this, "get_EqualityContract", MethodKind.PropertyGet, default, null, Accessibility.Protected)
            {
                SynthesizedKind = SynthesizedBodyKind.RecordEqualityContract,
                IsVirtualFlag = true,
                AssociatedProperty = eqContract,
                ExplicitParameters = [],
                ExplicitReturnType = typeT,
            };
            b.Add(eqContract);
            b.Add(eqContract.Getter);
        }

        SourceMethodSymbol M(string name, SynthesizedBodyKind k, TypeSymbol ret, ImmutableArray<ParameterSymbol> ps, bool isStatic = false, bool isOverride = false, bool isVirtual = false,
            MethodKind kind = MethodKind.Ordinary, Accessibility acc = Accessibility.Public)
        {
            return new(Compilation, File, this, name, kind, default, null, acc)
            {
                SynthesizedKind = k,
                ExplicitParameters = ps,
                ExplicitReturnType = ret,
                IsStaticFlag = isStatic,
                IsOverrideFlag = isOverride,
                IsVirtualFlag = isVirtual,
            };
        }

        b.Add(M("Equals", SynthesizedBodyKind.RecordEquals, boolT, [new ParameterSymbol("other", _isStruct ? self : new NullableTypeSymbol(self), 0, null)], isVirtual: !_isStruct));
        b.Add(M("Equals", SynthesizedBodyKind.RecordEqualsObject, boolT, [new ParameterSymbol("obj", new NullableTypeSymbol(objT), 0, null)], isOverride: true));
        b.Add(M("GetHashCode", SynthesizedBodyKind.RecordGetHashCode, intT, [], isOverride: true));
        b.Add(M("ToString", SynthesizedBodyKind.RecordToString, strT, [], isOverride: true));
        b.Add(M("op_Equality", SynthesizedBodyKind.RecordEqualsOperator, boolT,
            [new ParameterSymbol("left", _isStruct ? self : new NullableTypeSymbol(self), 0, null), new ParameterSymbol("right", _isStruct ? self : new NullableTypeSymbol(self), 1, null)],
            isStatic: true, kind: MethodKind.UserDefinedOperator));
        b.Add(M("op_Inequality", SynthesizedBodyKind.RecordNotEqualsOperator, boolT,
            [new ParameterSymbol("left", _isStruct ? self : new NullableTypeSymbol(self), 0, null), new ParameterSymbol("right", _isStruct ? self : new NullableTypeSymbol(self), 1, null)],
            isStatic: true, kind: MethodKind.UserDefinedOperator));
        _ = voidT;
        return b.ToImmutable();
    }
}

/// <summary><c>type Dir = "up" | "down"</c> : an enum whose members are the string literals.</summary>
public sealed class SourceLiteralEnumSymbol(Compilation compilation, SourceFile file, TypeAliasDeclarationSyntax alias, UnionTypeSyntax syntax, Symbol container) : SourceNamedType(compilation, file, alias, alias.Identifier.Text, container, new ModifierSet(alias.Modifiers).TypeAccessibility)
{
    private readonly TypeAliasDeclarationSyntax _alias = alias;
    private readonly UnionTypeSyntax _syntax = syntax;

    public override SyntaxToken? IdentifierToken => _alias.Identifier;
    public override ImmutableArray<DecoratorSyntax> DecoratorSyntaxes => [.. _alias.Decorators];
    public override TypeKind TypeKind => TypeKind.Enum;
    public override bool IsSealed => true;
    public override NamedTypeSymbol? BaseType => Compilation.GetSpecialType(SpecialType.Enum);
    public override NamedTypeSymbol? EnumUnderlyingType => Compilation.GetSpecialType(SpecialType.Int32);
#pragma warning disable CA1822 // marker flag read through instances by callers; making it static would break `symbol.Flag` access
    public bool IsLiteralEnum => true;
#pragma warning restore CA1822

    public SourceFieldSymbol? FindMember(string literal)
    {
        foreach (SourceFieldSymbol m in GetMembers().OfType<SourceFieldSymbol>())
        {
            if (m.LiteralText == literal)
            {
                return m;
            }
        }

        return null;
    }

    public static string SanitizeName(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        StringBuilder sb = new();
        foreach (char ch in literal)
        {
            _ = sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            _ = sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        ImmutableArray<Symbol>.Builder b = ImmutableArray.CreateBuilder<Symbol>();
        HashSet<string> seen = [];
        int value = 0;
        foreach (TypeSyntax t in _syntax.Types)
        {
            LiteralTypeSyntax lit = (LiteralTypeSyntax)t;
            string text = lit.Literal is LiteralExpressionSyntax l ? (l.Token.Value as string ?? "") : "";
            string name = SanitizeName(text);
            if (!seen.Add(name))
            {
                Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.DuplicateMember, File.FilePath, t.Span, name, Name));
                continue;
            }
            int v = value++;
            b.Add(new SourceFieldSymbol(Compilation, File, this, name, Accessibility.Public, true, true, true, t, this)
            {
                IsEnumMember = true,
                LiteralText = text,
                ConstantFactory = _ => new ConstantValue(v),
            });
        }
        return b.ToImmutable();
    }
}

/// <summary><c>type Shape = Circle | Square</c> : a readonly struct carrying a tag and the case value.</summary>
#pragma warning disable CA1002 // case list is built by the caller and read by index
public sealed class SourceUnionSymbol(Compilation compilation, SourceFile file, TypeAliasDeclarationSyntax alias, List<TypeSyntax> cases, bool includesNull, Symbol container) : SourceNamedType(compilation, file, alias, alias.Identifier.Text, container, new ModifierSet(alias.Modifiers).TypeAccessibility)
#pragma warning restore CA1002
{
    private readonly TypeAliasDeclarationSyntax _alias = alias;
    private readonly List<TypeSyntax> _caseSyntax = cases;
    private ImmutableArray<TypeSymbol> _cases;
    private bool _casesBuilding;

    public bool IncludesNull { get; } = includesNull;
    protected override TypeParameterListSyntax? TypeParameterListSyntax => _alias.TypeParameters;
    public override SyntaxToken? IdentifierToken => _alias.Identifier;
    public override TypeKind TypeKind => TypeKind.Struct;
    public override bool IsSealed => true;
    public override bool IsReadOnlyStruct => true;
    public override bool IsUnionType => true;
    public override NamedTypeSymbol? BaseType => Compilation.GetSpecialType(SpecialType.ValueType);
    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            NamedTypeSymbol? eq = Compilation.GetWellKnownType("System.IEquatable`1");
            NamedTypeSymbol self = IsGenericType ? Construct(TypeParameters.CastArray<TypeSymbol>()) : this;
            return eq == null ? [] : [eq.Construct(self)];
        }
    }

    public override ImmutableArray<TypeSymbol> UnionCases
    {
        get
        {
            if (!_cases.IsDefault)
            {
                return _cases;
            }

            if (_casesBuilding)
            {
                return [];
            }

            _casesBuilding = true;
            Binder binder = CreateBinder();
            ImmutableArray<TypeSymbol>.Builder b = ImmutableArray.CreateBuilder<TypeSymbol>();
            foreach (TypeSyntax s in _caseSyntax)
            {
                TypeSymbol t = binder.BindType(s);
                if (t.IsErrorType)
                {
                    continue;
                }

                if (t.IsVoid)
                {
                    continue;
                }
                // a union member that is itself a union contributes its cases
                foreach (TypeSymbol c in t is NamedTypeSymbol { IsUnionType: true } inner && !ReferenceEquals(inner, this) ? inner.UnionCases : [t])
                {
                    if (b.Any(x => x.Equals(c)))
                    {
                        Compilation.DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.DuplicateUnionCase, File.FilePath, s.Span, c.ToDisplayString()));
                        continue;
                    }
                    b.Add(c);
                }
            }
            _casesBuilding = false;
            _cases = b.ToImmutable();
            return _cases;
        }
    }

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        ImmutableArray<Symbol>.Builder b = ImmutableArray.CreateBuilder<Symbol>();
        NamedTypeSymbol self = IsGenericType ? Construct(TypeParameters.CastArray<TypeSymbol>()) : this;
        NamedTypeSymbol intT = Compilation.GetSpecialType(SpecialType.Int32);
        NamedTypeSymbol objT = Compilation.GetSpecialType(SpecialType.Object);
        NamedTypeSymbol boolT = Compilation.GetSpecialType(SpecialType.Boolean);
        NamedTypeSymbol strT = Compilation.GetSpecialType(SpecialType.String);
        b.Add(new SourceFieldSymbol(Compilation, File, this, "Tag", Accessibility.Public, false, true, false, null, intT));
        b.Add(new SourceFieldSymbol(Compilation, File, this, "Value", Accessibility.Public, false, true, false, null, new NullableTypeSymbol(objT)));
        ImmutableArray<TypeSymbol> cases = UnionCases;
        for (int i = 0; i < cases.Length; i++)
        {
            TypeSymbol c = cases[i];
            int idx = i;
            b.Add(new SourceMethodSymbol(Compilation, File, this, ".ctor", MethodKind.Constructor, default, null, Accessibility.Public)
            {
                SynthesizedKind = SynthesizedBodyKind.UnionConstructor,
                ExplicitParameters = [new ParameterSymbol("value", c, 0, null)],
                UnionCaseIndex = idx,
            });
            b.Add(new SourceMethodSymbol(Compilation, File, this, "op_Implicit", MethodKind.Conversion, default, null, Accessibility.Public)
            {
                SynthesizedKind = SynthesizedBodyKind.UnionImplicit,
                IsStaticFlag = true,
                ExplicitParameters = [new ParameterSymbol("value", c, 0, null)],
                ExplicitReturnType = self,
                UnionCaseIndex = idx,
            });
        }
        b.Add(new SourceMethodSymbol(Compilation, File, this, "ToString", MethodKind.Ordinary, default, null, Accessibility.Public)
        { SynthesizedKind = SynthesizedBodyKind.UnionToString, IsOverrideFlag = true, ExplicitParameters = [], ExplicitReturnType = strT });
        b.Add(new SourceMethodSymbol(Compilation, File, this, "Equals", MethodKind.Ordinary, default, null, Accessibility.Public)
        { SynthesizedKind = SynthesizedBodyKind.UnionEqualsObject, IsOverrideFlag = true, ExplicitParameters = [new ParameterSymbol("obj", new NullableTypeSymbol(objT), 0, null)], ExplicitReturnType = boolT });
        b.Add(new SourceMethodSymbol(Compilation, File, this, "Equals", MethodKind.Ordinary, default, null, Accessibility.Public)
        { SynthesizedKind = SynthesizedBodyKind.UnionEquals, ExplicitParameters = [new ParameterSymbol("other", self, 0, null)], ExplicitReturnType = boolT });
        b.Add(new SourceMethodSymbol(Compilation, File, this, "op_Equality", MethodKind.UserDefinedOperator, default, null, Accessibility.Public)
        { SynthesizedKind = SynthesizedBodyKind.UnionEqualsOperator, IsStaticFlag = true, ExplicitParameters = [new ParameterSymbol("left", self, 0, null), new ParameterSymbol("right", self, 1, null)], ExplicitReturnType = boolT });
        b.Add(new SourceMethodSymbol(Compilation, File, this, "op_Inequality", MethodKind.UserDefinedOperator, default, null, Accessibility.Public)
        { SynthesizedKind = SynthesizedBodyKind.UnionNotEqualsOperator, IsStaticFlag = true, ExplicitParameters = [new ParameterSymbol("left", self, 0, null), new ParameterSymbol("right", self, 1, null)], ExplicitReturnType = boolT });
        b.Add(new SourceMethodSymbol(Compilation, File, this, "GetHashCode", MethodKind.Ordinary, default, null, Accessibility.Public)
        { SynthesizedKind = SynthesizedBodyKind.UnionGetHashCode, IsOverrideFlag = true, ExplicitParameters = [], ExplicitReturnType = intT });
        return b.ToImmutable();
    }
}
