extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HS = SelfHost::Caladesi.Compiler.Symbols.Source;
using Factory = SelfHost::Caladesi.Compiler.Emit.SynthesizedClassFactory;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class SynthesizedClassFactoryParityTests
{
    private static readonly Type s_factory = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.SynthesizedClassFactory", true)!;

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(2, 0)]
    [DataRow(0, 2)]
    [DataRow(2, 2)]
    public void Create_GenericClosureAndOriginalArgumentsMatch(int classArity, int methodArity)
    {
        Fixtures f = new(classArity, methodArity);
        var expected = Create(f.Compilation, f.Container, f.Method);
        var actual = Factory.Create(f.ObjectType, f.HostContainer, f.HostMethod, "Closure");
        Compare(expected, actual);
        Assert.AreSame(f.HostContainer, actual.Item1.ContainingSymbol);
        Assert.AreSame(f.ObjectType, actual.Item1.BaseType);
        Assert.AreEqual(H.Accessibility.Private, actual.Item1.DeclaredAccessibility);
        Assert.AreEqual(H.TypeKind.Class, actual.Item1.TypeKind);
        Assert.IsTrue(actual.Item1.IsSealed);
        H.TypeParameterSymbol[] originals = [.. f.HostContainer.TypeParameters, .. f.HostMethod.TypeParameters];
        for (var i = 0; i < originals.Length; i++)
        {
            Assert.AreSame(originals[i], actual.Item2.TypeArguments[i]);
            Assert.AreNotSame(originals[i], actual.Item1.TypeParameters[i]);
            Assert.AreSame(actual.Item1, actual.Item1.TypeParameters[i].ContainingSymbol);
        }
        if (originals.Length == 0)
        {
            Assert.AreSame(actual.Item1, actual.Item2);
            Assert.IsNull(actual.Item1.MethodTypeParameterMap);
            Assert.AreEqual(-1, actual.Item1.OwnArityValue);
        }
        else
        {
            Assert.AreEqual(methodArity, actual.Item1.OwnArity);
            Assert.HasCount(methodArity, actual.Item1.MethodTypeParameterMap!);
            for (var i = 0; i < methodArity; i++)
            {
                Assert.AreEqual(classArity + i, actual.Item1.MethodTypeParameterMap![f.HostMethod.TypeParameters[i]]);
            }
        }
    }

    [TestMethod]
    public void Create_ExplicitBaseAndNullDefaultMatch()
    {
        Fixtures f = new(0, 0);
        SynthesizedNamedType baseType = new("Base", null, TypeKind.Class);
        HS.SynthesizedNamedType hostBase = new("Base", null, H.TypeKind.Class);
        var expected = Create(f.Compilation, f.Container, f.Method, baseType);
        var actual = Factory.Create(f.ObjectType, f.HostContainer, f.HostMethod, "Closure", hostBase);
        Compare(expected, actual);
        Assert.AreSame(hostBase, actual.Item1.BaseType);
        var fallback = Factory.Create(f.ObjectType, f.HostContainer, f.HostMethod, "Closure", null);
        Compare(Create(f.Compilation, f.Container, f.Method), fallback);
        Assert.AreSame(f.ObjectType, fallback.Item1.BaseType);
    }

    [TestMethod]
    public void Create_CopiesConstraintsLazilyWithoutVarianceOrStructNewConstraint()
    {
        Fixtures f = new(0, 0);
        var originalCalls = 0;
        var hostCalls = 0;
        var originalConstraints = TypeParameterConstraints.None;
        var hostConstraints = H.TypeParameterConstraints.None;
        TypeParameterSymbol original = new("T", 0, f.Container, VarianceKind.Out,
            _ => { originalCalls++; return originalConstraints; });
        H.TypeParameterSymbol host = new("T", 0, f.HostContainer, H.VarianceKind.Out,
            _ => { hostCalls++; return hostConstraints; });
        f.Container.SetTypeParameters([original]);
        f.HostContainer.SetTypeParameters([host]);
        var expected = Create(f.Compilation, f.Container, f.Method);
        var actual = Factory.Create(f.ObjectType, f.HostContainer, f.HostMethod, "Closure");
        Assert.AreEqual(0, originalCalls);
        Assert.AreEqual(0, hostCalls);
        originalConstraints = new(false, true, true, true, [f.Container]);
        hostConstraints = new(false, true, true, true, [f.HostContainer]);
        Compare(expected, actual);
        var copy = actual.Item1.TypeParameters[0];
        Assert.AreEqual(H.VarianceKind.None, copy.Variance);
        Assert.IsTrue(copy.HasValueTypeConstraint);
        Assert.IsTrue(copy.HasConstructorConstraint);
        Assert.IsTrue(copy.HasNotNullConstraint);
        Assert.AreSame(f.HostContainer, copy.ConstraintTypes.Single());
        Assert.IsFalse(RawNewConstraint(copy));
        Assert.IsFalse(RawNewConstraint(expected.Item1.TypeParameters[0]));
        Assert.AreEqual(1, originalCalls);
        Assert.AreEqual(1, hostCalls);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void Create_ReferenceAndConstructorConstraintsMatch(bool reference, bool constructor)
    {
        Fixtures f = new(0, 0);
        f.Container.SetTypeParameters([new("T", 0, f.Container, VarianceKind.In,
            _ => new(reference, false, constructor, true, [f.Container]))]);
        f.HostContainer.SetTypeParameters([new("T", 0, f.HostContainer, H.VarianceKind.In,
            _ => new(reference, false, constructor, true, [f.HostContainer]))]);
        var expected = Create(f.Compilation, f.Container, f.Method);
        var actual = Factory.Create(f.ObjectType, f.HostContainer, f.HostMethod, "Closure");
        Compare(expected, actual);
        Assert.AreEqual(reference, actual.Item1.TypeParameters[0].HasReferenceTypeConstraint);
        Assert.AreEqual(constructor, RawNewConstraint(actual.Item1.TypeParameters[0]));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void Create_NestedClosureReusesOriginalMethodMapAndDeclaresNoOwnParameters(int methodArity)
    {
        Fixtures f = new(1, methodArity);
        var outer = Create(f.Compilation, f.Container, f.Method);
        var hostOuter = Factory.Create(f.ObjectType, f.HostContainer, f.HostMethod, "Closure");
        MethodFixture innerMethod = new(outer.Item1, "Inner");
        HostMethodFixture hostInnerMethod = new(hostOuter.Item1, "Inner");
        innerMethod.TypeParametersValue = [new("Ignored", 0, innerMethod, VarianceKind.None)];
        hostInnerMethod.TypeParametersValue = [new("Ignored", 0, hostInnerMethod, H.VarianceKind.None)];
        var expected = Create(f.Compilation, outer.Item1, innerMethod);
        var actual = Factory.Create(f.ObjectType, hostOuter.Item1, hostInnerMethod, "Closure");
        Compare(expected, actual);
        Assert.AreEqual(0, actual.Item1.OwnArity);
        Assert.AreEqual(1 + methodArity, actual.Item1.Arity);
        Assert.AreSame(hostOuter.Item1.TypeParameters[0], actual.Item2.TypeArguments[0]);
        Assert.AreNotSame(hostOuter.Item1.MethodTypeParameterMap, actual.Item1.MethodTypeParameterMap);
        for (var i = 0; i < methodArity; i++)
        {
            Assert.AreSame(f.HostMethod.TypeParameters[i], actual.Item2.TypeArguments[i + 1]);
            Assert.AreEqual(i + 1, actual.Item1.MethodTypeParameterMap![f.HostMethod.TypeParameters[i]]);
        }
        var deeper = Factory.Create(f.ObjectType, actual.Item1, hostInnerMethod, "Deep");
        Assert.AreEqual(0, deeper.Item1.OwnArity);
        for (var i = 0; i < methodArity; i++)
        {
            Assert.AreSame(f.HostMethod.TypeParameters[i], deeper.Item2.TypeArguments[i + 1]);
        }
    }

    [TestMethod]
    public void ClosedMembers_PreserveIdentityAndResolveByNameKindAndParameterCount()
    {
        Fixtures f = new(1, 0);
        var expected = Create(f.Compilation, f.Container, f.Method);
        var actual = Factory.Create(f.ObjectType, f.HostContainer, f.HostMethod, "Closure");
        TypeSymbol originalType = expected.Item1.TypeParameters[0];
        H.TypeSymbol hostType = actual.Item1.TypeParameters[0];
        SynthesizedFieldSymbol field = new(expected.Item1, "Value", originalType);
        HS.SynthesizedFieldSymbol hostField = new(actual.Item1, "Value", hostType);
        expected.Item1.AddMember(new MethodFixture(expected.Item1, "Value"));
        actual.Item1.AddMember(new HostMethodFixture(actual.Item1, "Value"));
        expected.Item1.AddMember(field);
        actual.Item1.AddMember(hostField);
        Assert.AreSame(hostField, Factory.ClosedField(actual.Item1, hostField));
        Assert.AreSame(field, Invoke<FieldSymbol>("ClosedField", expected.Item1, field));
        var closedField = Invoke<FieldSymbol>("ClosedField", expected.Item2, field);
        var hostClosedField = Factory.ClosedField(actual.Item2, hostField);
        Assert.AreEqual(closedField.Type.Name, hostClosedField.Type.Name);
        Assert.AreSame(f.HostContainer.TypeParameters[0], hostClosedField.Type);
        Assert.AreSame(actual.Item2, hostClosedField.ContainingSymbol);

        // Use a fresh construction because constructed symbols cache their members.
        var closed = expected.Item1.Construct(new TypeSymbol[] { f.Container.TypeParameters[0] });
        var hostClosed = actual.Item1.Construct(new H.TypeSymbol[] { f.HostContainer.TypeParameters[0] });
        expected.Item1.AddMember(new SynthesizedFieldSymbol(expected.Item1, "Run", originalType));
        actual.Item1.AddMember(new HS.SynthesizedFieldSymbol(actual.Item1, "Run", hostType));
        MethodFixture zero = new(expected.Item1, "Run", originalType);
        HostMethodFixture hostZero = new(actual.Item1, "Run", hostType);
        MethodFixture one = new(expected.Item1, "Run", originalType);
        HostMethodFixture hostOne = new(actual.Item1, "Run", hostType);
        one.ParametersValue = [new("x", originalType, 0, one)];
        hostOne.ParametersValue = [new("x", hostType, 0, hostOne)];
        expected.Item1.AddMember(zero);
        actual.Item1.AddMember(hostZero);
        expected.Item1.AddMember(one);
        actual.Item1.AddMember(hostOne);
        foreach (var pair in new[] { (zero, hostZero), (one, hostOne) })
        {
            Assert.AreSame(pair.Item2, Factory.ClosedMethod(actual.Item1, pair.Item2));
            var method = Invoke<MethodSymbol>("ClosedMethod", closed, pair.Item1);
            var hostMethod = Factory.ClosedMethod(hostClosed, pair.Item2);
            Assert.HasCount(method.Parameters.Length, hostMethod.Parameters);
            Assert.AreSame(f.HostContainer.TypeParameters[0], hostMethod.ReturnType);
            Assert.AreSame(hostClosed, hostMethod.ContainingSymbol);
            if (hostMethod.Parameters.Length != 0)
            {
                Assert.AreSame(f.HostContainer.TypeParameters[0], hostMethod.Parameters[0].Type);
            }
        }
        HostMethodFixture duplicate = new(actual.Item1, "Run", H.NullTypeSymbol.Instance)
        { ParametersValue = hostOne.ParametersValue };
        Assert.AreSame(Factory.ClosedMethod(hostClosed, hostOne), Factory.ClosedMethod(hostClosed, duplicate));
    }

    [TestMethod]
    public void ClosedMembers_MissingNameOrMatchingArityThrowsLikeBootstrap()
    {
        Fixtures f = new(1, 0);
        var expected = Create(f.Compilation, f.Container, f.Method);
        var actual = Factory.Create(f.ObjectType, f.HostContainer, f.HostMethod, "Closure");
        SynthesizedFieldSymbol field = new(expected.Item1, "Missing", NullTypeSymbol.Instance);
        HS.SynthesizedFieldSymbol hostField = new(actual.Item1, "Missing", H.NullTypeSymbol.Instance);
        _ = Assert.IsInstanceOfType<InvalidOperationException>(
            Assert.Throws<TargetInvocationException>(() => Invoke<FieldSymbol>("ClosedField", expected.Item2, field)).InnerException);
        _ = Assert.Throws<InvalidOperationException>(() => Factory.ClosedField(actual.Item2, hostField));
        MethodFixture method = new(expected.Item1, "Missing");
        HostMethodFixture hostMethod = new(actual.Item1, "Missing");
        _ = Assert.IsInstanceOfType<InvalidOperationException>(
            Assert.Throws<TargetInvocationException>(() => Invoke<MethodSymbol>("ClosedMethod", expected.Item2, method)).InnerException);
        _ = Assert.Throws<InvalidOperationException>(() => Factory.ClosedMethod(actual.Item2, hostMethod));

        SynthesizedNamedType other = new("Other", null, TypeKind.Class);
        HS.SynthesizedNamedType hostOther = new("Other", null, H.TypeKind.Class);
        MethodFixture present = new(other, "Run");
        HostMethodFixture hostPresent = new(hostOther, "Run");
        other.AddMember(present);
        hostOther.AddMember(hostPresent);
        MethodFixture wrongArity = new(expected.Item1, "Run");
        HostMethodFixture hostWrongArity = new(actual.Item1, "Run");
        wrongArity.ParametersValue = [new("x", NullTypeSymbol.Instance, 0, wrongArity)];
        hostWrongArity.ParametersValue = [new("x", H.NullTypeSymbol.Instance, 0, hostWrongArity)];
        _ = Assert.IsInstanceOfType<InvalidOperationException>(
            Assert.Throws<TargetInvocationException>(() => Invoke<MethodSymbol>("ClosedMethod", other, wrongArity)).InnerException);
        _ = Assert.Throws<InvalidOperationException>(() => Factory.ClosedMethod(hostOther, hostWrongArity));
    }

    private static (SynthesizedNamedType, NamedTypeSymbol) Create(Compilation compilation,
        NamedTypeSymbol container, MethodSymbol method, NamedTypeSymbol? baseType = null)
    {
        object?[] args = [compilation, container, method, "Closure", baseType, null];
        var definition = (SynthesizedNamedType)s_factory.GetMethod("Create")!.Invoke(null, args)!;
        return (definition, (NamedTypeSymbol)args[5]!);
    }

    private static T Invoke<T>(string name, params object[] args)
    {
        return (T)s_factory.GetMethod(name)!.Invoke(null, args)!;
    }

    private static bool RawNewConstraint(object parameter)
    {
        var constraints = parameter.GetType().GetProperty("Constraints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parameter)!;
        return (bool)constraints.GetType().GetProperty("HasNew")!.GetValue(constraints)!;
    }

    private static void Compare((SynthesizedNamedType, NamedTypeSymbol) expected,
        (HS.SynthesizedNamedType, H.NamedTypeSymbol) actual)
    {
        Assert.AreEqual(expected.Item1.Name, actual.Item1.Name);
        Assert.AreEqual(expected.Item1.MetadataName, actual.Item1.MetadataName);
        Assert.AreEqual(expected.Item1.BaseType?.Name, actual.Item1.BaseType?.Name);
        Assert.AreEqual(expected.Item1.OwnArityValue, actual.Item1.OwnArityValue);
        Assert.AreEqual(expected.Item1.OwnArity, actual.Item1.OwnArity);
        Assert.HasCount(expected.Item1.TypeParameters.Length, actual.Item1.TypeParameters);
        Assert.AreEqual(expected.Item1.MethodTypeParameterMap?.Count, actual.Item1.MethodTypeParameterMap?.Count);
        for (var i = 0; i < expected.Item1.TypeParameters.Length; i++)
        {
            var left = expected.Item1.TypeParameters[i];
            var right = actual.Item1.TypeParameters[i];
            Assert.AreEqual(left.Name, right.Name);
            Assert.AreEqual(left.Ordinal, right.Ordinal);
            Assert.AreEqual((int)left.Variance, (int)right.Variance);
            Assert.AreEqual(left.HasReferenceTypeConstraint, right.HasReferenceTypeConstraint);
            Assert.AreEqual(left.HasValueTypeConstraint, right.HasValueTypeConstraint);
            Assert.AreEqual(left.HasConstructorConstraint, right.HasConstructorConstraint);
            Assert.AreEqual(left.HasNotNullConstraint, right.HasNotNullConstraint);
            CollectionAssert.AreEqual(left.ConstraintTypes.Select(t => t.Name).ToArray(), right.ConstraintTypes.Select(t => t.Name).ToArray());
            Assert.AreEqual(expected.Item2.TypeArguments[i].Name, actual.Item2.TypeArguments[i].Name);
        }
    }

    private sealed class Fixtures
    {
        public Compilation Compilation { get; } = TestCompiler.Create("", OutputKind.DynamicallyLinkedLibrary);
        public SynthesizedNamedType Container { get; } = new("Container", null, TypeKind.Class);
        public HS.SynthesizedNamedType HostContainer { get; } = new("Container", null, H.TypeKind.Class);
        public HS.SynthesizedNamedType ObjectType { get; } = new("Object", null, H.TypeKind.Class);
        public MethodFixture Method { get; }
        public HostMethodFixture HostMethod { get; }

        public Fixtures(int classArity, int methodArity)
        {
            Method = new(Container, "Method");
            HostMethod = new(HostContainer, "Method");
            Container.SetTypeParameters([.. Enumerable.Range(0, classArity).Select(i =>
                new TypeParameterSymbol("C" + i, i, Container, VarianceKind.Out))]);
            HostContainer.SetTypeParameters([.. Enumerable.Range(0, classArity).Select(i =>
                new H.TypeParameterSymbol("C" + i, i, HostContainer, H.VarianceKind.Out))]);
            Method.TypeParametersValue = [.. Enumerable.Range(0, methodArity).Select(i =>
                new TypeParameterSymbol("M" + i, i, Method, VarianceKind.None))];
            HostMethod.TypeParametersValue = [.. Enumerable.Range(0, methodArity).Select(i =>
                new H.TypeParameterSymbol("M" + i, i, HostMethod, H.VarianceKind.None))];
        }
    }

    private sealed class MethodFixture(Symbol container, string name, TypeSymbol? returnType = null) : MethodSymbol
    {
        public ImmutableArray<TypeParameterSymbol> TypeParametersValue { get; set; } = [];
        public ImmutableArray<ParameterSymbol> ParametersValue { get; set; } = [];
        public override string Name => name;
        public override Symbol ContainingSymbol => container;
        public override MethodKind MethodKind => MethodKind.Ordinary;
        public override TypeSymbol ReturnType => returnType ?? NullTypeSymbol.Instance;
        public override ImmutableArray<ParameterSymbol> Parameters => ParametersValue;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => TypeParametersValue;
    }

    private sealed class HostMethodFixture(H.Symbol container, string name, H.TypeSymbol? returnType = null) : H.MethodSymbol
    {
        public ImmutableArray<H.TypeParameterSymbol> TypeParametersValue { get; set; } = [];
        public ImmutableArray<H.ParameterSymbol> ParametersValue { get; set; } = [];
        public override string Name => name;
        public override H.Symbol ContainingSymbol => container;
        public override H.MethodKind MethodKind => H.MethodKind.Ordinary;
        public override H.TypeSymbol ReturnType => returnType ?? H.NullTypeSymbol.Instance;
        public override ImmutableArray<H.ParameterSymbol> Parameters => ParametersValue;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => TypeParametersValue;
    }
}
