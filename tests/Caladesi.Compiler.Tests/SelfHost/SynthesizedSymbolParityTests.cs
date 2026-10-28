extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using S = SelfHost::Caladesi.Compiler.Symbols;
using SS = SelfHost::Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class SynthesizedSymbolParityTests
{
    [TestMethod]
    public void BodyKinds_NamesAndOrdinalsMatchBootstrap()
    {
        CollectionAssert.AreEqual(Enum.GetNames<SynthesizedBodyKind>(), Enum.GetNames<SS.SynthesizedBodyKind>());
        CollectionAssert.AreEqual(Enum.GetValues<SynthesizedBodyKind>().Select(kind => (int)kind).ToArray(),
            Enum.GetValues<SS.SynthesizedBodyKind>().Select(kind => (int)kind).ToArray());
    }

    [TestMethod]
    public void Fields_DefaultsFlagsAndTypeMutationMatchBootstrap()
    {
        AssertField(new SynthesizedFieldSymbol(ErrorTypeSymbol.Instance, "f", NullTypeSymbol.Instance),
            new SS.SynthesizedFieldSymbol(S.ErrorTypeSymbol.Instance, "f", S.NullTypeSymbol.Instance));
        foreach (Accessibility accessibility in Enum.GetValues<Accessibility>())
        {
            for (int flags = 0; flags < 4; flags++)
            {
                SynthesizedFieldSymbol expected = new(ErrorTypeSymbol.Instance, "field", NullTypeSymbol.Instance,
                    (flags & 1) != 0, accessibility, (flags & 2) != 0);
                SS.SynthesizedFieldSymbol actual = new(S.ErrorTypeSymbol.Instance, "field", S.NullTypeSymbol.Instance,
                    (flags & 1) != 0, (S.Accessibility)(int)accessibility, (flags & 2) != 0);
                AssertField(expected, actual);
                Mutate(expected, "SetType", NeverTypeSymbol.Instance);
                actual.SetType(S.NeverTypeSymbol.Instance);
                AssertField(expected, actual);
                Assert.AreSame(S.NeverTypeSymbol.Instance, actual.Type);
                Assert.AreSame(S.ErrorTypeSymbol.Instance, actual.ContainingSymbol);
            }
        }
    }

    [TestMethod]
    public void Methods_KindsFlagsDefaultsAndMetadataMatchBootstrap()
    {
        foreach (MethodKind kind in Enum.GetValues<MethodKind>())
        {
            for (int flags = 0; flags < 32; flags++)
            {
                SynthesizedMethodSymbol expected = new(ErrorTypeSymbol.Instance, "method", kind, Accessibility.Protected,
                    (flags & 1) != 0, NullTypeSymbol.Instance, [], (flags & 2) != 0, (flags & 4) != 0,
                    (flags & 8) != 0, (flags & 16) != 0);
                SS.SynthesizedMethodSymbol actual = new(S.ErrorTypeSymbol.Instance, "method", (S.MethodKind)(int)kind,
                    S.Accessibility.Protected, (flags & 1) != 0, S.NullTypeSymbol.Instance, [],
                    (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0);
                AssertMethod(expected, actual);
            }
        }

        LambdaSymbol origin = new(ErrorTypeSymbol.Instance, "lambda", false, false);
        SelfHost::Caladesi.Compiler.Bound.LambdaSymbol selfOrigin = new(S.ErrorTypeSymbol.Instance, "lambda", false, false);
        SynthesizedMethodSymbol withMetadata = new(ErrorTypeSymbol.Instance, "generated", MethodKind.LocalFunction,
            Accessibility.Private, false, ErrorTypeSymbol.Instance, [])
        {
            SourceMethod = origin, LambdaOrigin = origin, SynthesizedKind = SynthesizedBodyKind.Custom,
            IsAsyncMethod = true, IsIteratorMethod = true, Associated = origin, Overrides = [origin]
        };
        SS.SynthesizedMethodSymbol selfMetadata = new(S.ErrorTypeSymbol.Instance, "generated", S.MethodKind.LocalFunction,
            S.Accessibility.Private, false, S.ErrorTypeSymbol.Instance, []);
        selfMetadata.SourceMethod = selfOrigin;
        selfMetadata.LambdaOrigin = selfOrigin;
        selfMetadata.SynthesizedKind = SS.SynthesizedBodyKind.Custom;
        selfMetadata.IsAsyncMethod = true;
        selfMetadata.IsIteratorMethod = true;
        selfMetadata.Associated = selfOrigin;
        selfMetadata.Overrides = [selfOrigin];
        AssertMethod(withMetadata, selfMetadata);
        Assert.AreSame(selfOrigin, selfMetadata.SourceMethod);
        Assert.AreSame(selfOrigin, selfMetadata.LambdaOrigin);
        Assert.AreSame(selfOrigin, selfMetadata.AssociatedSymbol);
        Assert.AreSame(selfOrigin, selfMetadata.ExplicitInterfaceImplementations[0]);
    }

    [TestMethod]
    public void Methods_MutationAndGenericConstructionMatchBootstrap()
    {
        SynthesizedMethodSymbol expected = new(ErrorTypeSymbol.Instance, "generic", MethodKind.Ordinary,
            Accessibility.Public, false, ErrorTypeSymbol.Instance, default);
        SS.SynthesizedMethodSymbol actual = new(S.ErrorTypeSymbol.Instance, "generic", S.MethodKind.Ordinary,
            S.Accessibility.Public, false, S.ErrorTypeSymbol.Instance, default);
        Assert.AreEqual(expected.Parameters.IsDefault, actual.Parameters.IsDefault);
        TypeParameterSymbol parameter = new("T", 0, expected, VarianceKind.None);
        S.TypeParameterSymbol selfParameter = new("T", 0, actual, S.VarianceKind.None);
        Mutate(expected, "SetTypeParameters", ImmutableArray.Create(parameter));
        actual.SetTypeParameters([selfParameter]);
        Mutate(expected, "SetReturnType", parameter);
        actual.SetReturnType(selfParameter);
        Mutate(expected, "SetParameters", ImmutableArray.Create(new ParameterSymbol("value", parameter, 0, expected)));
        actual.SetParameters([new S.ParameterSymbol("value", selfParameter, 0, actual)]);
        AssertMethod(expected, actual);
        Assert.AreSame(selfParameter, actual.TypeArguments[0]);
        MethodSymbol constructed = expected.Construct([NeverTypeSymbol.Instance]);
        S.MethodSymbol selfConstructed = actual.Construct([S.NeverTypeSymbol.Instance]);
        Assert.AreEqual(constructed.ReturnType.Name, selfConstructed.ReturnType.Name);
        Assert.AreEqual(constructed.Parameters[0].Type.Name, selfConstructed.Parameters[0].Type.Name);
        Assert.AreSame(selfConstructed, selfConstructed.Parameters[0].ContainingSymbol);
        Assert.IsTrue(ReferenceEquals(actual, selfConstructed.OriginalDefinition));
    }

    [TestMethod]
    public void NamedTypes_DefaultsKindsFlagsAndNullableConstructorsMatchBootstrap()
    {
        foreach (TypeKind kind in Enum.GetValues<TypeKind>())
        {
            SynthesizedNamedType expected = new("Generated", null, kind);
            SS.SynthesizedNamedType actual = new("Generated", null, (S.TypeKind)(int)kind);
            AssertType(expected, actual);
            Assert.AreEqual(expected.IsCompilerGenerated, actual.IsCompilerGenerated);
            Assert.IsNull(actual.MethodTypeParameterMap);
            for (int flags = 0; flags < 8; flags++)
            {
                SynthesizedNamedType flagged = new("Generated", null, kind, Accessibility.Public)
                {
                    IsSealedFlag = (flags & 1) != 0, IsAbstractFlag = (flags & 2) != 0, IsStaticFlag = (flags & 4) != 0
                };
                actual.IsSealedFlag = (flags & 1) != 0;
                actual.IsAbstractFlag = (flags & 2) != 0;
                actual.IsStaticFlag = (flags & 4) != 0;
                SS.SynthesizedNamedType selfFlagged = new("Generated", null, (S.TypeKind)(int)kind, S.Accessibility.Public);
                selfFlagged.IsSealedFlag = actual.IsSealedFlag;
                selfFlagged.IsAbstractFlag = actual.IsAbstractFlag;
                selfFlagged.IsStaticFlag = actual.IsStaticFlag;
                AssertType(flagged, selfFlagged);
            }
        }
        AssertType(new SynthesizedNamedType("Empty", null, TypeKind.Class, Accessibility.Private, null, default),
            new SS.SynthesizedNamedType("Empty", null, S.TypeKind.Class, S.Accessibility.Private, null, default));
        AssertType(new SynthesizedNamedType("Derived", null, TypeKind.Class, Accessibility.Internal, ErrorTypeSymbol.Instance),
            new SS.SynthesizedNamedType("Derived", null, S.TypeKind.Class, S.Accessibility.Internal, S.ErrorTypeSymbol.Instance));
    }

    [TestMethod]
    public void NamedTypes_LiveMembersSnapshotsInterfacesAndGenericMapsMatchBootstrap()
    {
        SynthesizedNamedType expected = new("Closure", ErrorTypeSymbol.Instance, TypeKind.Class);
        SS.SynthesizedNamedType actual = new("Closure", S.ErrorTypeSymbol.Instance, S.TypeKind.Class);
        IReadOnlyList<Symbol> liveMembers = expected.Members;
        IReadOnlyList<S.Symbol> selfLiveMembers = actual.Members;
        ImmutableArray<Symbol> snapshot = expected.GetMembers();
        ImmutableArray<S.Symbol> selfSnapshot = actual.GetMembers();
        TypeParameterSymbol outer = new("Outer", 0, expected, VarianceKind.None);
        TypeParameterSymbol own = new("T", 1, expected, VarianceKind.None);
        S.TypeParameterSymbol selfOuter = new("Outer", 0, actual, S.VarianceKind.None);
        S.TypeParameterSymbol selfOwn = new("T", 1, actual, S.VarianceKind.None);
        expected.SetTypeParameters([outer, own]);
        actual.SetTypeParameters([selfOuter, selfOwn]);
        AssertType(expected, actual);
        foreach (int ownArity in new[] { -3, -1, 0, 1, 2 })
        {
            expected.OwnArityValue = ownArity;
            actual.OwnArityValue = ownArity;
            AssertType(expected, actual);
            CollectionAssert.AreEqual(expected.OwnTypeArguments.Select(type => type.Name).ToArray(),
                actual.OwnTypeArguments.Select(type => type.Name).ToArray());
        }
        expected.OwnArityValue = 1;
        actual.OwnArityValue = 1;
        expected.MethodTypeParameterMap = new Dictionary<TypeParameterSymbol, int> { [own] = 0 };
        actual.MethodTypeParameterMap = new Dictionary<S.TypeParameterSymbol, int> { [selfOwn] = 0 };
        Assert.AreEqual(expected.MethodTypeParameterMap[own], actual.MethodTypeParameterMap[selfOwn]);
        actual.MethodTypeParameterMap[selfOwn] = 7;
        Assert.AreEqual(7, actual.MethodTypeParameterMap[selfOwn]);
        SynthesizedFieldSymbol field = new(expected, "value", own);
        SS.SynthesizedFieldSymbol selfField = new(actual, "value", selfOwn);
        expected.AddMember(field);
        actual.AddMember(selfField);
        expected.AddMember(field);
        actual.AddMember(selfField);
        Assert.HasCount(liveMembers.Count, selfLiveMembers);
        Assert.HasCount(snapshot.Length, selfSnapshot);
        Assert.IsEmpty(selfSnapshot);
        Assert.AreSame(selfField, actual.GetMembers()[0]);
        Assert.HasCount(expected.GetMembers("value").Length, actual.GetMembers("value"));
        ImmutableArray<S.Symbol> laterSnapshot = actual.GetMembers();
        actual.AddMember(selfOwn);
        expected.AddMember(own);
        Assert.HasCount(2, laterSnapshot);
        expected.SetBase(ErrorTypeSymbol.Instance);
        actual.SetBase(S.ErrorTypeSymbol.Instance);
        ImmutableArray<S.NamedTypeSymbol> interfaceSnapshot = actual.Interfaces;
        expected.AddInterface(ErrorTypeSymbol.Instance);
        actual.AddInterface(S.ErrorTypeSymbol.Instance);
        expected.AddInterface(ErrorTypeSymbol.Instance);
        actual.AddInterface(S.ErrorTypeSymbol.Instance);
        Assert.IsEmpty(interfaceSnapshot);
        Assert.HasCount(expected.Interfaces.Length, actual.Interfaces);
        AssertType(expected, actual);
        NamedTypeSymbol constructed = expected.Construct(ImmutableArray.Create<TypeSymbol>(NullTypeSymbol.Instance, NeverTypeSymbol.Instance));
        S.NamedTypeSymbol selfConstructed = actual.Construct(ImmutableArray.Create<S.TypeSymbol>(S.NullTypeSymbol.Instance, S.NeverTypeSymbol.Instance));
        Assert.AreEqual(((FieldSymbol)constructed.GetMembers()[0]).Type.Name,
            ((S.FieldSymbol)selfConstructed.GetMembers()[0]).Type.Name);
        Assert.IsTrue(ReferenceEquals(actual, actual.Construct(ImmutableArray.Create<S.TypeSymbol>(selfOuter, selfOwn))));
    }

    private static void Mutate(object symbol, string method, object value)
    {
        MethodInfo mutator = symbol.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        _ = mutator.Invoke(symbol, [value]);
    }

    private static void AssertSymbol(Symbol expected, S.Symbol actual)
    {
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.MetadataName, actual.MetadataName);
        Assert.AreEqual((int)expected.Kind, (int)actual.Kind);
        Assert.AreEqual(expected.ContainingSymbol?.Name, actual.ContainingSymbol?.Name);
        Assert.AreEqual((int)expected.DeclaredAccessibility, (int)actual.DeclaredAccessibility);
        Assert.AreEqual(expected.IsStatic, actual.IsStatic);
        Assert.AreEqual(expected.IsVirtual, actual.IsVirtual);
        Assert.AreEqual(expected.IsOverride, actual.IsOverride);
        Assert.AreEqual(expected.IsSealed, actual.IsSealed);
        Assert.AreEqual(expected.IsAbstract, actual.IsAbstract);
        Assert.AreEqual(expected.IsFromSource, actual.IsFromSource);
    }

    private static void AssertField(SynthesizedFieldSymbol expected, SS.SynthesizedFieldSymbol actual)
    {
        AssertSymbol(expected, actual);
        Assert.AreEqual(expected.Type.Name, actual.Type.Name);
        Assert.AreEqual(expected.IsReadOnly, actual.IsReadOnly);
        Assert.AreEqual(expected.IsConst, actual.IsConst);
    }

    private static void AssertMethod(SynthesizedMethodSymbol expected, SS.SynthesizedMethodSymbol actual)
    {
        AssertSymbol(expected, actual);
        Assert.AreEqual((int)expected.MethodKind, (int)actual.MethodKind);
        Assert.AreEqual(expected.ReturnType.Name, actual.ReturnType.Name);
        Assert.AreEqual(expected.Arity, actual.Arity);
        Assert.AreEqual(expected.IsGenericMethod, actual.IsGenericMethod);
        Assert.AreEqual(expected.IsAsync, actual.IsAsync);
        Assert.AreEqual(expected.IsIterator, actual.IsIterator);
        Assert.AreEqual((int)expected.SynthesizedKind, (int)actual.SynthesizedKind);
        Assert.AreEqual(expected.AssociatedSymbol?.Name, actual.AssociatedSymbol?.Name);
        Assert.HasCount(expected.Parameters.Length, actual.Parameters);
        Assert.HasCount(expected.ExplicitInterfaceImplementations.Length, actual.ExplicitInterfaceImplementations);
        Assert.AreEqual(expected.SourceMethod?.Name, actual.SourceMethod?.Name);
        Assert.AreEqual(expected.LambdaOrigin?.Name, actual.LambdaOrigin?.Name);
    }

    private static void AssertType(SynthesizedNamedType expected, SS.SynthesizedNamedType actual)
    {
        AssertSymbol(expected, actual);
        Assert.AreEqual((int)expected.TypeKind, (int)actual.TypeKind);
        Assert.AreEqual(expected.IsReferenceType, actual.IsReferenceType);
        Assert.AreEqual(expected.IsValueType, actual.IsValueType);
        Assert.AreEqual(expected.Arity, actual.Arity);
        Assert.AreEqual(expected.OwnArity, actual.OwnArity);
        Assert.AreEqual(expected.IsStaticClass, actual.IsStaticClass);
        Assert.AreEqual(expected.BaseType?.Name, actual.BaseType?.Name);
        CollectionAssert.AreEqual(expected.TypeArguments.Select(type => type.Name).ToArray(),
            actual.TypeArguments.Select(type => type.Name).ToArray());
    }
}
