using System.Collections.Immutable;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Symbols.Source;

/// <summary>
/// The compiler-generated record for an object literal without a target type (like a C# anonymous type):
/// a sealed generic class <c>&lt;&gt;f__AnonymousTypeN&lt;T0, T1, ...&gt;</c> with get-only properties and value equality.
/// </summary>
public sealed class SynthesizedAnonymousRecord : SourceNamedType
{
    private readonly ImmutableArray<string> _propertyNames;

    public SynthesizedAnonymousRecord(Compilation compilation, SourceFile file, string name, ImmutableArray<string> propertyNames)
        : base(compilation, file, null, name, file.Namespace, Accessibility.Internal)
    {
        _propertyNames = propertyNames;
    }

    public ImmutableArray<string> PropertyNames => _propertyNames;
    public override TypeKind TypeKind => TypeKind.Class;
    public override bool IsRecord => true;
    public override bool IsSealed => true;
    public bool IsCompilerGenerated => true;
    public override NamedTypeSymbol? BaseType => Compilation.GetSpecialType(SpecialType.Object);

    public override ImmutableArray<TypeParameterSymbol> TypeParameters
    {
        get
        {
            if (_tps.IsDefault)
            {
                var b = ImmutableArray.CreateBuilder<TypeParameterSymbol>(_propertyNames.Length);
                for (var i = 0; i < _propertyNames.Length; i++)
                    b.Add(new TypeParameterSymbol("T" + i, i, this, VarianceKind.None));
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
            if (!_interfaces.IsDefault) return _interfaces;
            var eq = Compilation.GetWellKnownType("System.IEquatable`1");
            var self = (NamedTypeSymbol)Construct(TypeParameters.CastArray<TypeSymbol>());
            return _interfaces = eq == null ? ImmutableArray<NamedTypeSymbol>.Empty : [eq.Construct(self)];
        }
    }

    protected override ImmutableArray<Symbol> BuildMembers()
    {
        var b = ImmutableArray.CreateBuilder<Symbol>();
        var self = (NamedTypeSymbol)Construct(TypeParameters.CastArray<TypeSymbol>());
        var ctorParams = ImmutableArray.CreateBuilder<ParameterSymbol>();
        for (var i = 0; i < _propertyNames.Length; i++)
        {
            var tp = TypeParameters[i];
            var name = _propertyNames[i];
            var prop = new SourcePropertySymbol(Compilation, File, this, name, Accessibility.Public, false, null, null, tp) { IsAutoProperty = true };
            prop.Getter = MemberFactory.Accessor(Compilation, File, this, prop, true, Accessibility.Public, false, false, false, false, false, SynthesizedBodyKind.AutoGetter);
            var backing = new SourceFieldSymbol(Compilation, File, this, "<" + name + ">i__Field", Accessibility.Private, false, true, false, null, tp) { IsBackingField = true };
            prop.BackingField = backing;
            b.Add(backing);
            b.Add(prop);
            b.Add(prop.Getter);
            ctorParams.Add(new ParameterSymbol(name, tp, i, null));
        }
        var voidT = Compilation.GetSpecialType(SpecialType.Void);
        var boolT = Compilation.GetSpecialType(SpecialType.Boolean);
        var objT = Compilation.GetSpecialType(SpecialType.Object);
        var intT = Compilation.GetSpecialType(SpecialType.Int32);
        var strT = Compilation.GetSpecialType(SpecialType.String);
        _ = voidT;
        b.Add(new SourceMethodSymbol(Compilation, File, this, ".ctor", MethodKind.Constructor, default, null, Accessibility.Public)
        {
            SynthesizedKind = SynthesizedBodyKind.RecordConstructor,
            ExplicitParameters = ctorParams.ToImmutable().Select(p => p.WithType(p.Type, null!)).ToImmutableArray(),
        });
        SourceMethodSymbol M(string name, SynthesizedBodyKind k, TypeSymbol ret, ImmutableArray<ParameterSymbol> ps, bool isOverride = true, bool isVirtual = false) =>
            new(Compilation, File, this, name, MethodKind.Ordinary, default, null, Accessibility.Public)
            { SynthesizedKind = k, ExplicitParameters = ps, ExplicitReturnType = ret, IsOverrideFlag = isOverride, IsVirtualFlag = isVirtual };
        b.Add(M("Equals", SynthesizedBodyKind.RecordEquals, boolT, [new ParameterSymbol("other", new NullableTypeSymbol(self), 0, null)], isOverride: false, isVirtual: true));
        b.Add(M("Equals", SynthesizedBodyKind.RecordEqualsObject, boolT, [new ParameterSymbol("obj", new NullableTypeSymbol(objT), 0, null)]));
        b.Add(M("GetHashCode", SynthesizedBodyKind.RecordGetHashCode, intT, ImmutableArray<ParameterSymbol>.Empty));
        b.Add(M("ToString", SynthesizedBodyKind.RecordToString, strT, ImmutableArray<ParameterSymbol>.Empty));
        return b.ToImmutable();
    }
}
