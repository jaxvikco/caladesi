using System.Collections.Immutable;

namespace Caladesi.Compiler.Symbols.Source;

/// <summary>
/// The compiler-generated record for an object literal without a target type (like a C# anonymous type):
/// a sealed generic class <c>&lt;&gt;f__AnonymousTypeN&lt;T0, T1, ...&gt;</c> with get-only properties and value equality.
/// </summary>
public sealed class SynthesizedAnonymousRecord(Compilation compilation, SourceFile file, string name, ImmutableArray<string> propertyNames) : SourceNamedType(compilation, file, null, name, file.Namespace, Accessibility.Internal)
{
    public ImmutableArray<string> PropertyNames { get; } = propertyNames;
    public override TypeKind TypeKind => TypeKind.Class;
    public override bool IsRecord => true;
    public override bool IsSealed => true;
#pragma warning disable CA1822 // marker flag read through instances by callers; making it static would break `symbol.Flag` access
    public bool IsCompilerGenerated => true;
#pragma warning restore CA1822
    public override NamedTypeSymbol? BaseType => Compilation.GetSpecialType(SpecialType.Object);

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_tps.IsDefault)
            {
                ImmutableArray<TypeParameterSymbol>.Builder b = ImmutableArray.CreateBuilder<TypeParameterSymbol>(PropertyNames.Length);
                for (int i = 0; i < PropertyNames.Length; i++)
                {
                    b.Add(new TypeParameterSymbol("T" + i, i, this, VarianceKind.None));
                }

                _tps = b.MoveToImmutable();
            }
            return _tps;
        }
    }

    private ImmutableArray<TypeParameterSymbol> _tps;
    private ImmutableArray<NamedTypeSymbol> _interfaces;

    public override ImmutableArray<NamedTypeSymbol> Interfaces
    {
        get
        {
            if (!_interfaces.IsDefault)
            {
                return _interfaces;
            }

            NamedTypeSymbol? eq = Compilation.GetWellKnownType("System.IEquatable`1");
            NamedTypeSymbol self = Construct(TypeParameters.CastArray<TypeSymbol>());
            return _interfaces = eq == null ? [] : [eq.Construct(self)];
        }
    }

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        ImmutableArray<Symbol>.Builder b = ImmutableArray.CreateBuilder<Symbol>();
        NamedTypeSymbol self = Construct(TypeParameters.CastArray<TypeSymbol>());
        ImmutableArray<ParameterSymbol>.Builder ctorParams = ImmutableArray.CreateBuilder<ParameterSymbol>();
        for (int i = 0; i < PropertyNames.Length; i++)
        {
            TypeParameterSymbol tp = TypeParameters[i];
            string name = PropertyNames[i];
            SourcePropertySymbol prop = new(File, this, name, Accessibility.Public, false, null, null, tp) { IsAutoProperty = true };
            prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, Accessibility.Public, false, false, false, false, false, SynthesizedBodyKind.AutoGetter);
            SourceFieldSymbol backing = new(Compilation, File, this, "<" + name + ">i__Field", Accessibility.Private, false, true, false, null, tp) { IsBackingField = true };
            prop.BackingField = backing;
            b.Add(backing);
            b.Add(prop);
            b.Add(prop.Getter);
            ctorParams.Add(new ParameterSymbol(name, tp, i, null));
        }
        NamedTypeSymbol voidT = Compilation.GetSpecialType(SpecialType.Void);
        NamedTypeSymbol boolT = Compilation.GetSpecialType(SpecialType.Boolean);
        NamedTypeSymbol objT = Compilation.GetSpecialType(SpecialType.Object);
        NamedTypeSymbol intT = Compilation.GetSpecialType(SpecialType.Int32);
        NamedTypeSymbol strT = Compilation.GetSpecialType(SpecialType.String);
        _ = voidT;
        b.Add(new SourceMethodSymbol(Compilation, File, this, ".ctor", MethodKind.Constructor, default, null, Accessibility.Public)
        {
            SynthesizedKind = SynthesizedBodyKind.RecordConstructor,
            ExplicitParameters = [.. ctorParams.ToImmutable().Select(p => p.WithType(p.Type, null!))],
        });
        SourceMethodSymbol M(string name, SynthesizedBodyKind k, TypeSymbol ret, ImmutableArray<ParameterSymbol> ps, bool isOverride = true, bool isVirtual = false)
        {
            return new(Compilation, File, this, name, MethodKind.Ordinary, default, null, Accessibility.Public)
            { SynthesizedKind = k, ExplicitParameters = ps, ExplicitReturnType = ret, IsOverrideFlag = isOverride, IsVirtualFlag = isVirtual };
        }

        b.Add(M("Equals", SynthesizedBodyKind.RecordEquals, boolT, [new ParameterSymbol("other", new NullableTypeSymbol(self), 0, null)], isOverride: false, isVirtual: true));
        b.Add(M("Equals", SynthesizedBodyKind.RecordEqualsObject, boolT, [new ParameterSymbol("obj", new NullableTypeSymbol(objT), 0, null)]));
        b.Add(M("GetHashCode", SynthesizedBodyKind.RecordGetHashCode, intT, []));
        b.Add(M("ToString", SynthesizedBodyKind.RecordToString, strT, []));
        return b.ToImmutable();
    }
}
