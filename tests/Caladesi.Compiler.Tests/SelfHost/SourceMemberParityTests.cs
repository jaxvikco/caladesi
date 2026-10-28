extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Tests.Emit;
using H = SelfHost::Caladesi.Compiler.Symbols;
using HS = SelfHost::Caladesi.Compiler.Symbols.Source;
using HX = SelfHost::Caladesi.Compiler.Syntax;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HD = SelfHost::Caladesi.Compiler.Diagnostics;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class SourceMemberParityTests
{
    [TestMethod]
    public void RealDeclarations_AnnotationsInferenceFlagsAccessorsAndLocationsMatch()
    {
        Fixture f = new("""
            export class C {
              private static readonly field: int = 3;
              value: string = "hello";
              constructor(public item: int) {}
              get label(): string { return this.value; }
              set label(value: string) { this.value = value; }
              async task(value: int): Task<int> { return value; }
              inferred(value: int) { return value; }
              generic<T extends object>(value: T): T { return value; }
            }
            export function top(value: int): int { return value; }
            """);
        SourceNamedType type = f.File.Types.Single(t => t.Name == "C");
        foreach (SourceMethodSymbol expected in type.GetMembers().OfType<SourceMethodSymbol>()
            .Concat(f.File.ModuleClass!.GetMembers().OfType<SourceMethodSymbol>()))
        {
            HS.SourceMethodSymbol<SourceFile> actual = f.Method(expected);
            AssertMethod(expected, actual);
            Assert.AreEqual(expected.ReturnType.ToDisplayString(), actual.ReturnType.ToDisplayString(), expected.Name);
            Assert.HasCount(expected.Parameters.Length, actual.Parameters, expected.Name);
            for (int i = 0; i < expected.Parameters.Length; i++)
            {
                Assert.AreEqual(expected.Parameters[i].Name, actual.Parameters[i].Name);
                Assert.AreEqual(expected.Parameters[i].Type.ToDisplayString(), actual.Parameters[i].Type.ToDisplayString());
            }
            Assert.HasCount(expected.TypeParameters.Length, actual.TypeParameters);
            Assert.IsTrue(actual.Parameters == actual.Parameters);
            Assert.IsTrue(actual.TypeParameters == actual.TypeParameters);
            Assert.AreEqual(expected.GetBoundBody() is null, actual.GetBoundBody() is null);
            Assert.AreSame(actual.GetBoundBody(), actual.GetBoundBody());
        }
        foreach (SourceFieldSymbol expected in type.GetMembers().OfType<SourceFieldSymbol>())
        {
            HS.SourceFieldSymbol<SourceFile> actual = f.Field(expected);
            AssertSymbol(expected, actual);
            Assert.AreEqual(expected.Type.ToDisplayString(), actual.Type.ToDisplayString());
            Assert.AreEqual(expected.IsReadOnly, actual.IsReadOnly);
            Assert.AreEqual(expected.IsConst, actual.IsConst);
        }
        foreach (SourcePropertySymbol expected in type.GetMembers().OfType<SourcePropertySymbol>())
        {
            HS.SourcePropertySymbol<SourceFile> actual = f.Property(expected);
            AssertSymbol(expected, actual);
            Assert.AreEqual(expected.Type.ToDisplayString(), actual.Type.ToDisplayString());
            Assert.AreEqual(expected.GetMethod?.Name, actual.GetMethod?.Name);
            Assert.AreEqual(expected.SetMethod?.Name, actual.SetMethod?.Name);
            Assert.IsEmpty(actual.Parameters);
            if (actual.Getter is { } getter) Assert.IsTrue(ReferenceEquals(actual, getter.AssociatedSymbol));
            if (actual.Setter is { } setter) Assert.IsTrue(ReferenceEquals(actual, setter.AssociatedSymbol));
        }
        Assert.IsGreaterThan(0, f.ParametersBound);
        Assert.IsGreaterThan(0, f.TypesBound);
        Assert.IsGreaterThan(0, f.ReturnsAdjusted);
    }

    [TestMethod]
    public void Bodies_AllKindsAndSynthesisDecisionsPreserveStaticConstructorOddity()
    {
        Fixture f = new("class C {}");
        SourceNamedType container = f.File.Types.Single();
        foreach (MethodKind kind in Enum.GetValues<MethodKind>())
        foreach (SynthesizedBodyKind synthesis in Enum.GetValues<SynthesizedBodyKind>())
        {
            SourceMethodSymbol expected = new(f.Compilation, f.File, container, "body", kind, default, null, Accessibility.Public)
            { SynthesizedKind = synthesis };
            HS.SourceMethodSymbol<SourceFile> actual = f.Method(expected);
            Assert.AreEqual(expected.HasBody, actual.HasBody, $"{kind}/{synthesis}");
            int calls = f.BodiesBound;
            bool binds = kind == MethodKind.StaticConstructor || kind == MethodKind.Constructor &&
                synthesis is SynthesizedBodyKind.DefaultConstructor or SynthesizedBodyKind.InheritedConstructor;
            f.Body = _ => new HB.BoundBlock([], []);
            Assert.AreEqual(!binds, actual.GetBoundBody() is null, $"{kind}/{synthesis}");
            Assert.AreSame(actual.GetBoundBody(), actual.GetBoundBody());
            Assert.AreEqual(calls + (binds ? 1 : 0), f.BodiesBound);
            Assert.AreEqual(expected.ReturnType.ToDisplayString(), actual.ReturnType.ToDisplayString());
        }
    }

    [TestMethod]
    public void SyntheticBodies_ReturnInferenceAndBodyReentryAreCachedBeforeCallbacks()
    {
        Fixture f = new("class C { method() {} }");
        SourceNamedType container = f.File.Types.Single();
        int originalCalls = 0, selfCalls = 0;
        SourceMethodSymbol expected = new(f.Compilation, f.File, container, "recursive", MethodKind.Ordinary,
            default, null, Accessibility.Public)
        {
            SyntheticBodyFactory = method =>
            {
                originalCalls++;
                Assert.IsNull(method.GetBoundBody());
                _ = method.ReturnType;
                return new BoundBlock([], []);
            }
        };
        HS.SourceMethodSymbol<SourceFile> actual = f.Method(expected);
        actual.SyntheticBodyFactory = method =>
        {
            selfCalls++;
            Assert.IsNull(method.GetBoundBody());
            _ = method.ReturnType;
            return new HB.BoundBlock([], []);
        };
        Assert.AreEqual(expected.ReturnType.IsErrorType, actual.ReturnType.IsErrorType);
        Assert.IsTrue(actual.ReturnType.IsErrorType);
        Assert.AreEqual(originalCalls, selfCalls);
        Assert.AreEqual(1, selfCalls);
        Assert.AreSame(actual.GetBoundBody(), actual.GetBoundBody());
        Diagnostic diagnostic = OriginalDiagnostics(f.Compilation).Items[^1];
        Assert.AreEqual(diagnostic.Id, f.DeclarationDiagnostics.Items.Single().Id);
        Assert.AreEqual(diagnostic.Message, f.DeclarationDiagnostics.Items.Single().Message);
        Assert.AreEqual(diagnostic.FilePath, f.DeclarationDiagnostics.Items.Single().FilePath);
        Assert.AreEqual(diagnostic.Span.Start, f.DeclarationDiagnostics.Items.Single().Span.Start);
        HS.SourceMethodSymbol<SourceFile> inferred = f.Method(container.GetMembers("method").OfType<SourceMethodSymbol>().Single());
        f.Body = method =>
        {
            Assert.IsNull(method.GetBoundBody());
            method.SetInferredReturnType(f.GetSpecialType(H.SpecialType.Int32));
            return new HB.BoundBlock([], []);
        };
        Assert.AreEqual(H.SpecialType.Int32, inferred.ReturnType.SpecialType);
        Assert.AreSame(inferred.ReturnType, inferred.ReturnType);
        inferred.SetReturnType(H.NullTypeSymbol.Instance);
        Assert.AreSame(H.NullTypeSymbol.Instance, inferred.ReturnType);
        inferred.SetEmitName("renamed");
        Assert.AreEqual("renamed", inferred.MetadataName);
        Assert.AreEqual("method", inferred.Name);
    }

    [TestMethod]
    public void FieldsAndProperties_RecursiveTypesConstantReentryAndMutationMatch()
    {
        Fixture f = new("class C { private field: int = 1; }");
        SourceNamedType container = f.File.Types.Single();
        SyntaxNode declaration = container.GetMembers("field").OfType<SourceFieldSymbol>().Single().Declaration!;
        int expectedCalls = 0, actualCalls = 0;
        SourceFieldSymbol expected = new(f.Compilation, f.File, container, "cycle", Accessibility.Private,
            true, true, false, declaration, null, field => { expectedCalls++; return field.Type; });
        HS.SourceFieldSymbol<SourceFile> actual = new(f, f.File, f.Container(container), "cycle", H.Accessibility.Private,
            true, true, false, f.Node(declaration), null, field => { actualCalls++; return field.Type; });
        Assert.IsTrue(expected.Type.IsErrorType);
        Assert.IsTrue(actual.Type.IsErrorType);
        Assert.AreEqual(expectedCalls, actualCalls);
        Assert.AreEqual(1, actualCalls);
        Assert.AreSame(actual.Type, actual.Type);
        AssertSymbol(expected, actual);
        Assert.AreEqual(OriginalDiagnostics(f.Compilation).Items[^1].Message, f.DeclarationDiagnostics.Items[^1].Message);
        actual.SetType(H.NullTypeSymbol.Instance);
        actual.IsReadOnlyFlag = false;
        Assert.AreSame(H.NullTypeSymbol.Instance, actual.Type);
        Assert.IsFalse(actual.IsReadOnly);
        for (int flags = 0; flags < 8; flags++)
        {
            int left = 0, right = 0;
            SourceFieldSymbol constant = new(f.Compilation, f.File, container, "constant", Accessibility.Public,
                false, false, (flags & 1) != 0, null, ErrorTypeSymbol.Instance)
            {
                IsEnumMember = (flags & 2) != 0, IsConstDeclaration = (flags & 4) != 0,
                ConstantFactory = field => { left++; Assert.IsNull(field.ConstantValue); return new ConstantValue(42); }
            };
            HS.SourceFieldSymbol<SourceFile> selfConstant = f.Field(constant);
            selfConstant.ConstantFactory = field => { right++; Assert.IsNull(field.ConstantValue); return new H.ConstantValue(42); };
            Assert.AreEqual(constant.ConstantValue?.Value, selfConstant.ConstantValue?.Value);
            Assert.AreEqual(constant.IsConst, selfConstant.IsConst);
            Assert.AreEqual(left, right);
            Assert.AreSame(selfConstant.ConstantValue, selfConstant.ConstantValue);
        }
        foreach (bool sourceOwner in new[] { false, true })
        {
            NamedTypeSymbol owner = sourceOwner ? container : f.Compilation.GetSpecialType(SpecialType.Object);
            SourcePropertySymbol property = new(f.File, owner, "cycle", Accessibility.Public, false, declaration, p => p.Type);
            HS.SourcePropertySymbol<SourceFile> selfProperty = new(f, f.File, (H.NamedTypeSymbol)f.Type(owner),
                "cycle", H.Accessibility.Public, false, f.Node(declaration), p => p.Type, null);
            int before = f.DeclarationDiagnostics.Count;
            Assert.AreEqual(property.Type.IsErrorType, selfProperty.Type.IsErrorType);
            Assert.AreEqual(before + (sourceOwner ? 1 : 0), f.DeclarationDiagnostics.Count);
            selfProperty.SetType(H.NeverTypeSymbol.Instance);
            Assert.AreSame(H.NeverTypeSymbol.Instance, selfProperty.Type);
        }
    }

    [TestMethod]
    public void Overrides_RealMetadataCaseFlipAccessorsAndCachedMissesMatch()
    {
        Fixture f = new("""
            class C {
              override toString(): string { return "C"; }
              override equals(other: object): bool { return false; }
              override getHashCode(): int { return 1; }
            }
            class B { get value(): int { return 1; } set value(v: int) {} }
            class D extends B { override get value(): int { return 2; } override set value(v: int) {} }
            """);
        foreach (SourceNamedType type in f.File.Types)
        foreach (SourceMethodSymbol expected in type.GetMembers().OfType<SourceMethodSymbol>().Where(m => m.IsOverride))
        {
            HS.SourceMethodSymbol<SourceFile> actual = f.Method(expected);
            Assert.AreEqual(expected.OverriddenMethod?.Name, actual.OverriddenMethod?.Name, expected.ToString());
            Assert.IsNotNull(actual.OverriddenMethod, expected.ToString());
            Assert.AreSame(actual.OverriddenMethod, actual.OverriddenMethod);
        }
        SourceNamedType c = f.File.Types.Single(t => t.Name == "C");
        HS.SourceMethodSymbol<SourceFile> miss = f.Method(new SourceMethodSymbol(f.Compilation, f.File, c,
            "absent", MethodKind.Ordinary, default, null, Accessibility.Public) { IsOverrideFlag = true });
        Assert.IsNull(miss.OverriddenMethod);
        f.Container(c).SetBase(new HS.SynthesizedNamedType("NewBase", null, H.TypeKind.Class));
        Assert.IsNull(miss.OverriddenMethod);
        ArgumentNullException expectedNull = Assert.ThrowsExactly<ArgumentNullException>(() => OverrideResolver.FindOverriddenMethod(null!));
        ArgumentNullException actualNull = Assert.ThrowsExactly<ArgumentNullException>(() =>
            HS.OverrideResolver<SourceFile>.FindOverriddenMethod(null!, f));
        Assert.AreEqual(expectedNull.ParamName, actualNull.ParamName);
    }

    [TestMethod]
    public void Signatures_NullableRefKindsAndGenericParameterPositionOddityMatch()
    {
        Fixture f = new("class C { a<T>(x: T): void {} b<U>(x: U): void {} }");
        SourceMethodSymbol a = f.File.Types.Single().GetMembers("a").OfType<SourceMethodSymbol>().Single();
        SourceMethodSymbol b = f.File.Types.Single().GetMembers("b").OfType<SourceMethodSymbol>().Single();
        HS.SourceMethodSymbol<SourceFile> ha = f.Method(a), hb = f.Method(b);
        foreach (RefKind left in Enum.GetValues<RefKind>())
        foreach (RefKind right in Enum.GetValues<RefKind>())
        {
            SourceMethodSymbol x = new(f.Compilation, f.File, a.ContainerType, "x", MethodKind.Ordinary, default, null, Accessibility.Public)
            { ExplicitParameters = [new ParameterSymbol("x", a.TypeParameters[0], 0, a, left)], TypeParameterList = a.TypeParameterList };
            SourceMethodSymbol y = new(f.Compilation, f.File, a.ContainerType, "y", MethodKind.Ordinary, default, null, Accessibility.Public)
            { ExplicitParameters = [new ParameterSymbol("x", b.TypeParameters[0], 0, b, right)], TypeParameterList = b.TypeParameterList };
            HS.SourceMethodSymbol<SourceFile> hx = f.Method(x), hy = f.Method(y);
            Assert.IsTrue(OverrideResolver.SignaturesMatch(x, y));
            Assert.AreEqual(OverrideResolver.SignaturesMatch(x, y), HS.OverrideResolver<SourceFile>.SignaturesMatch(hx, hy));
            x = new(f.Compilation, f.File, a.ContainerType, "x", MethodKind.Ordinary, default, null, Accessibility.Public)
            { ExplicitParameters = [new ParameterSymbol("x", new NullableTypeSymbol(f.Compilation.GetSpecialType(SpecialType.String)), 0, a, left)] };
            y = new(f.Compilation, f.File, a.ContainerType, "y", MethodKind.Ordinary, default, null, Accessibility.Public)
            { ExplicitParameters = [new ParameterSymbol("y", f.Compilation.GetSpecialType(SpecialType.String), 0, b, right)] };
            Assert.AreEqual(left == right, HS.OverrideResolver<SourceFile>.SignaturesMatch(f.Method(x), f.Method(y)));
            Assert.AreEqual(OverrideResolver.SignaturesMatch(x, y),
                HS.OverrideResolver<SourceFile>.SignaturesMatch(f.Method(x), f.Method(y)));
        }
        hb.ExplicitParameters = [new H.ParameterSymbol("x", new H.TypeParameterSymbol("V", 1, hb, H.VarianceKind.None), 0, hb)];
        Assert.IsFalse(HS.OverrideResolver<SourceFile>.SignaturesMatch(ha, hb));
        Assert.ThrowsExactly<ArgumentNullException>(() => HS.OverrideResolver<SourceFile>.SignaturesMatch(null!, hb));
    }

    [TestMethod]
    public void TypeParameters_RealConstraintsAreLazyCachedAndCaptureEachSyntax()
    {
        Fixture f = new("class C { method<T extends object, U extends struct>(t: T, u: U): void {} }");
        SourceMethodSymbol expected = f.File.Types.Single().GetMembers("method").OfType<SourceMethodSymbol>().Single();
        HS.SourceMethodSymbol<SourceFile> actual = f.Method(expected);
        Assert.AreEqual(0, f.ConstraintsBound);
        ImmutableArray<H.TypeParameterSymbol> parameters = actual.TypeParameters;
        Assert.AreEqual(0, f.ConstraintsBound);
        Assert.HasCount(2, parameters);
        for (int i = 0; i < parameters.Length; i++)
        {
            TypeParameterSymbol left = expected.TypeParameters[i];
            H.TypeParameterSymbol right = parameters[i];
            Assert.AreEqual(left.Name, right.Name);
            Assert.AreEqual(left.Ordinal, right.Ordinal);
            Assert.AreEqual(left.IsMethodTypeParameter, right.IsMethodTypeParameter);
            Assert.AreEqual(left.HasReferenceTypeConstraint, right.HasReferenceTypeConstraint);
            Assert.AreEqual(left.HasValueTypeConstraint, right.HasValueTypeConstraint);
            Assert.AreEqual(left.HasConstructorConstraint, right.HasConstructorConstraint);
            Assert.AreEqual(left.HasNotNullConstraint, right.HasNotNullConstraint);
            CollectionAssert.AreEqual(left.ConstraintTypes.Select(t => t.ToDisplayString()).ToArray(),
                right.ConstraintTypes.Select(t => t.ToDisplayString()).ToArray());
            Assert.AreEqual(i + 1, f.ConstraintsBound);
            Assert.IsTrue(ReferenceEquals(actual, right.ContainingSymbol));
        }
        Assert.IsTrue(parameters == actual.TypeParameters);
        Assert.AreEqual(2, f.ConstraintsBound);
    }

    [TestMethod]
    public void Flags_ExplicitSignaturesInitAccessorsAndParameterCachesMatch()
    {
        Fixture f = new("class C { get value(): int { return 1; } set value(v: int) {} method(x: int): int { return x; } }");
        SourceNamedType container = f.File.Types.Single();
        SourcePropertySymbol property = container.GetMembers("value").OfType<SourcePropertySymbol>().Single();
        HS.SourcePropertySymbol<SourceFile> selfProperty = f.Property(property);
        property.IsInitOnlyProperty = selfProperty.IsInitOnlyProperty = true;
        property.IsAutoProperty = selfProperty.IsAutoProperty = true;
        Assert.IsTrue(selfProperty.Setter!.IsInitOnly);
        AssertMethod(property.Setter!, selfProperty.Setter);
        Assert.IsFalse(selfProperty.Getter!.IsInitOnly);
        HS.SourceMethodSymbol<SourceFile> implicitSetter = f.Method(new SourceMethodSymbol(f.Compilation, f.File, container,
            "set_implicit", MethodKind.PropertySet, default, null, Accessibility.Private) { AssociatedProperty = property });
        implicitSetter.AssociatedProperty = selfProperty;
        Assert.HasCount(1, implicitSetter.Parameters);
        Assert.AreEqual("value", implicitSetter.Parameters[0].Name);
        Assert.AreEqual(H.SpecialType.Int32, implicitSetter.Parameters[0].Type.SpecialType);
        Assert.IsTrue(ReferenceEquals(implicitSetter, implicitSetter.Parameters[0].ContainingSymbol));
        selfProperty.IndexerParameters = implicitSetter.Parameters;
        Assert.IsTrue(selfProperty.Parameters == implicitSetter.Parameters);

        SourceMethodSymbol method = container.GetMembers("method").OfType<SourceMethodSymbol>().Single();
        HS.SourceMethodSymbol<SourceFile> selfMethod = f.Method(method);
        int before = f.ParametersBound;
        _ = selfMethod.Parameters;
        _ = selfMethod.Parameters;
        Assert.AreEqual(before + 1, f.ParametersBound);
        before = f.TypesBound;
        _ = selfMethod.ReturnType;
        _ = selfMethod.ReturnType;
        Assert.AreEqual(before + 1, f.TypesBound);

        foreach (Accessibility accessibility in Enum.GetValues<Accessibility>())
        for (int flags = 0; flags < 32; flags++)
        {
            SourceMethodSymbol original = new(f.Compilation, f.File, container, "explicit", MethodKind.Ordinary,
                default, null, accessibility)
            {
                IsStaticFlag = (flags & 1) != 0, IsAbstractFlag = (flags & 2) != 0,
                IsVirtualFlag = (flags & 4) != 0, IsOverrideFlag = (flags & 8) != 0, IsSealedFlag = (flags & 16) != 0,
                ExplicitReturnType = f.Compilation.GetSpecialType(SpecialType.String), ForceAsync = true, IsGeneratorSyntax = true,
                ExplicitParameters = [new ParameterSymbol("self", f.Compilation.GetSpecialType(SpecialType.Int32), 0, null,
                    isThis: true)]
            };
            HS.SourceMethodSymbol<SourceFile> migrated = f.Method(original);
            AssertMethod(original, migrated);
            Assert.AreEqual(original.ReturnType.SpecialType, (SpecialType)(int)migrated.ReturnType.SpecialType);
            Assert.AreEqual(original.IsStatic, migrated.IsExtensionMethod);
            Assert.IsTrue(migrated.ExplicitParameters == migrated.Parameters);
        }
    }

    [TestMethod]
    public void TypeParameterCreation_NestedOrdinalsVarianceAndNullValidationMatch()
    {
        Fixture f = new("class Outer<A> {} class Inner<in T, out U> {}");
        SourceNamedType outer = f.File.Types.Single(t => t.Name == "Outer");
        SourceClassSymbol inner = (SourceClassSymbol)f.File.Types.Single(t => t.Name == "Inner");
        HS.SynthesizedNamedType selfOuter = f.Container(outer);
        selfOuter.SetTypeParameters([new H.TypeParameterSymbol("A", 0, selfOuter, H.VarianceKind.None)]);
        SynthesizedNamedType nested = new("Nested", outer, TypeKind.Class);
        HS.SynthesizedNamedType selfInner = new("Nested", selfOuter, H.TypeKind.Class);
        TypeParameterListSyntax list = inner.Syntax.TypeParameters!;
        ImmutableArray<TypeParameterSymbol> expected = SourceTypeParameters.Create(f.Compilation, f.File, nested, outer, list);
        ImmutableArray<H.TypeParameterSymbol> actual = HS.SourceTypeParameters<SourceFile>.Create(f, f.File, selfInner,
            selfOuter, (HX.TypeParameterListSyntax)f.Node(list)!);
        Assert.HasCount(expected.Length, actual);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].Name, actual[i].Name);
            Assert.AreEqual(expected[i].Ordinal, actual[i].Ordinal);
            Assert.AreEqual((int)expected[i].Variance, (int)actual[i].Variance);
            Assert.IsFalse(actual[i].IsMethodTypeParameter);
            Assert.IsTrue(ReferenceEquals(selfInner, actual[i].ContainingSymbol));
        }
        Assert.AreEqual(0, f.ConstraintsBound);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            HS.SourceTypeParameters<SourceFile>.Create(f, f.File, selfInner, selfOuter, null!));
    }

    [TestMethod]
    public void OverrideLookup_IsLazyAndDoesNotInspectFlippedNamesAfterAnExactMatch()
    {
        Fixture f = new("class C { go(): void {} }");
        SourceMethodSymbol member = f.File.Types.Single().GetMembers("go").OfType<SourceMethodSymbol>().Single();
        TrackingType original = new(member);
        SelfTrackingType migrated = new(f.Method(member));
        IEnumerable<Symbol> expected = (IEnumerable<Symbol>)typeof(OverrideResolver)
            .GetMethod("LookupNames", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [original, "go"])!;
        IEnumerable<H.Symbol> actual = HS.OverrideResolver<SourceFile>.LookupNames(migrated, "go");
        Assert.IsEmpty(original.Lookups);
        Assert.IsEmpty(migrated.Lookups);
        using (IEnumerator<Symbol> left = expected.GetEnumerator())
        using (IEnumerator<H.Symbol> right = actual.GetEnumerator())
        {
            Assert.IsTrue(left.MoveNext());
            Assert.IsTrue(right.MoveNext());
            Assert.AreEqual(left.Current.Name, right.Current.Name);
        }
        CollectionAssert.AreEqual(original.Lookups, migrated.Lookups);
        string[] firstLookup = ["go"];
        CollectionAssert.AreEqual(firstLookup, migrated.Lookups);
        CollectionAssert.AreEqual(expected.Select(s => s.Name).ToArray(), actual.Select(s => s.Name).ToArray());
        CollectionAssert.AreEqual(original.Lookups, migrated.Lookups);
        string[] allLookups = ["go", "go", "Go"];
        CollectionAssert.AreEqual(allLookups, migrated.Lookups);
    }

    [TestMethod]
    public void SelfHostLibrary_PassesWholeLibraryILVerificationWithoutBootstrapReference()
    {
        Assembly assembly = typeof(HS.SourceMethodSymbol<>).Assembly;
        Assert.IsFalse(assembly.GetReferencedAssemblies().Any(a => a.Name == typeof(Compilation).Assembly.GetName().Name));
        Assert.IsEmpty(ILVerifier.Verify(assembly.GetName().Name!, File.ReadAllBytes(assembly.Location)));
    }

    private static DiagnosticBag OriginalDiagnostics(Compilation compilation) =>
        (DiagnosticBag)typeof(Compilation).GetProperty("DeclarationDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compilation)!;

    private static void AssertSymbol(Symbol expected, H.Symbol actual)
    {
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.MetadataName, actual.MetadataName);
        Assert.AreEqual(expected.ToString(), actual.ToString());
        Assert.AreEqual((int)expected.DeclaredAccessibility, (int)actual.DeclaredAccessibility);
        Assert.AreEqual(expected.IsFromSource, actual.IsFromSource);
        Assert.AreEqual(expected.IsStatic, actual.IsStatic);
        Assert.AreEqual(expected.IsAbstract, actual.IsAbstract);
        Assert.AreEqual(expected.IsVirtual, actual.IsVirtual);
        Assert.AreEqual(expected.IsOverride, actual.IsOverride);
        Assert.AreEqual(expected.IsSealed, actual.IsSealed);
        Assert.HasCount(expected.Locations.Length, actual.Locations);
        for (int i = 0; i < expected.Locations.Length; i++)
        {
            Assert.AreEqual(expected.Locations[i].Tree.FilePath, actual.Locations[i].Tree.FilePath);
            Assert.AreEqual(expected.Locations[i].Span.Start, actual.Locations[i].Span.Start);
            Assert.AreEqual(expected.Locations[i].Span.Length, actual.Locations[i].Span.Length);
        }
    }

    private static void AssertMethod(SourceMethodSymbol expected, HS.SourceMethodSymbol<SourceFile> actual)
    {
        AssertSymbol(expected, actual);
        Assert.AreEqual((int)expected.MethodKind, (int)actual.MethodKind);
        Assert.AreEqual(expected.IdentifierSpan.Start, actual.IdentifierSpan.Start);
        Assert.AreEqual(expected.IdentifierSpan.Length, actual.IdentifierSpan.Length);
        Assert.AreEqual(expected.IsAsync, actual.IsAsync);
        Assert.AreEqual(expected.IsIterator, actual.IsIterator);
        Assert.AreEqual(expected.IsInitOnly, actual.IsInitOnly);
        Assert.AreEqual(expected.IsExtensionMethod, actual.IsExtensionMethod);
        Assert.AreEqual(expected.HasBody, actual.HasBody);
        Assert.AreEqual(expected.UnionCaseIndex, actual.UnionCaseIndex);
        Assert.IsEmpty(actual.GetAttributes());
    }

    private sealed class TrackingType(Symbol member) : NamedTypeSymbol
    {
        public List<string> Lookups { get; } = [];
        public override string Name => "Tracking";
        public override TypeKind TypeKind => TypeKind.Class;
        public override ImmutableArray<TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<TypeSymbol> TypeArguments => [];
        public override ImmutableArray<Symbol> GetMembers(string name)
        {
            Lookups.Add(name);
            return name == member.Name ? [member] : [];
        }
    }

    private sealed class SelfTrackingType(H.Symbol member) : H.NamedTypeSymbol
    {
        public List<string> Lookups { get; } = [];
        public override string Name => "Tracking";
        public override H.TypeKind TypeKind => H.TypeKind.Class;
        public override ImmutableArray<H.TypeParameterSymbol> TypeParameters => [];
        public override ImmutableArray<H.TypeSymbol> TypeArguments => [];
        public override ImmutableArray<H.Symbol> GetMembers(string name)
        {
            Lookups.Add(name);
            return name == member.Name ? [member] : [];
        }
    }

    // The test adapter supplies real bootstrap binding results and real selfhost metadata.
    // All lazy state, recursion, body decisions and override traversal remain in the Caladesi symbols.
    private sealed class Fixture : HS.SourceSymbolContext<SourceFile>
    {
        private readonly Dictionary<Symbol, H.Symbol> symbols = [];
        private readonly Dictionary<H.Symbol, Symbol> originals = [];
        private readonly HX.SyntaxTree tree;
        private readonly HM.ReferenceSet references = HM.ReferenceSet.Create(TestReferences.Paths);
        public Compilation Compilation { get; }
        public SourceFile File { get; }
        public int ParametersBound { get; private set; }
        public int TypesBound { get; private set; }
        public int ReturnsAdjusted { get; private set; }
        public int ConstraintsBound { get; private set; }
        public int BodiesBound { get; private set; }
        public Func<HS.SourceMethodSymbol<SourceFile>, HB.BoundBlock?>? Body { get; set; }
        public override HD.DiagnosticBag DeclarationDiagnostics { get; } = new();

        public Fixture(string source)
        {
            Compilation = TestCompiler.Create(source, OutputKind.DynamicallyLinkedLibrary);
            Assert.IsEmpty(Compilation.SyntaxTrees.Single().Diagnostics, source);
            // Force declarations, not bodies/constraints.
            File = Compilation.GetFile(Compilation.SyntaxTrees.Single());
            tree = HX.SyntaxTree.ParseText(source, File.FilePath);
        }

        public override HX.SyntaxTree GetTree(SourceFile file) => tree;
        public override string GetFilePath(SourceFile file) => file.FilePath;
        public override H.NamedTypeSymbol GetSpecialType(H.SpecialType type) => references.GetSpecialType(type)!;
        public override bool IsSourceNamedType(H.NamedTypeSymbol type) => originals.TryGetValue(type, out Symbol? original) && original is SourceNamedType;

        public HS.SynthesizedNamedType Container(NamedTypeSymbol original)
        {
            if (symbols.TryGetValue(original, out H.Symbol? cached)) return (HS.SynthesizedNamedType)cached;
            HS.SynthesizedNamedType result = new(original.Name, original.ContainingType is { } outer ? Container(outer) : null,
                (H.TypeKind)(int)original.TypeKind, (H.Accessibility)(int)original.DeclaredAccessibility);
            Register(original, result);
            if (original.BaseType is { } parent) result.SetBase((H.NamedTypeSymbol)Type(parent));
            foreach (SourceMethodSymbol method in original.GetMembers().OfType<SourceMethodSymbol>()) result.AddMember(Method(method));
            foreach (SourcePropertySymbol property in original.GetMembers().OfType<SourcePropertySymbol>()) result.AddMember(Property(property));
            return result;
        }

        public H.TypeSymbol Type(TypeSymbol type)
        {
            if (symbols.TryGetValue(type, out H.Symbol? cached)) return (H.TypeSymbol)cached;
            if (type is TypeParameterSymbol parameter)
            {
                if (parameter.Container is SourceMethodSymbol method) return Method(method).TypeParameters[parameter.Ordinal];
                H.TypeParameterSymbol result = new(parameter.Name, parameter.Ordinal, Container((NamedTypeSymbol)parameter.Container),
                    (H.VarianceKind)(int)parameter.Variance);
                Register(parameter, result);
                return result;
            }
            if (type is NullableTypeSymbol nullable) return new H.NullableTypeSymbol(Type(nullable.Underlying));
            if (type is ArrayTypeSymbol array) return new H.ArrayTypeSymbol(Type(array.ElementType), null, array.Rank);
            if (type is ErrorTypeSymbol) return H.ErrorTypeSymbol.Instance;
            if (type is NullTypeSymbol) return H.NullTypeSymbol.Instance;
            if (type is NeverTypeSymbol) return H.NeverTypeSymbol.Instance;
            NamedTypeSymbol named = (NamedTypeSymbol)type;
            if (named is SourceNamedType or SourceModuleClassSymbol) return Container(named);
            H.NamedTypeSymbol definition = references.FindType(named.ToMetadataString())!;
            Assert.IsNotNull(definition, named.ToMetadataString());
            H.NamedTypeSymbol mapped = named.IsDefinition ? definition : definition.Construct(named.TypeArguments.Select(Type).ToArray());
            Register(type, mapped);
            return mapped;
        }

        private void Register(Symbol original, H.Symbol mapped)
        {
            symbols[original] = mapped;
            originals[mapped] = original;
        }

        public HX.SyntaxNode? Node(SyntaxNode? original) => original is null ? null : Walk(tree.Root).Single(n =>
            n.Kind == (HX.SyntaxKind)(int)original.Kind && n.Span.Start == original.Span.Start && n.Span.Length == original.Span.Length);

        private static IEnumerable<HX.SyntaxNode> Walk(HX.SyntaxNode node)
        {
            yield return node;
            for (int i = 0; i < node.SlotCount; i++)
                if (node.Slot(i) is HX.SyntaxNode child)
                    foreach (HX.SyntaxNode descendant in Walk(child)) yield return descendant;
        }

        public HS.SourceMethodSymbol<SourceFile> Method(SourceMethodSymbol original)
        {
            if (symbols.TryGetValue(original, out H.Symbol? cached)) return (HS.SourceMethodSymbol<SourceFile>)cached;
            H.NamedTypeSymbol container = Container(original.ContainerType);
            if (symbols.TryGetValue(original, out cached)) return (HS.SourceMethodSymbol<SourceFile>)cached;
            ModifierSet m = original.Modifiers;
            HS.SourceMethodSymbol<SourceFile> result = new(this, File, container, original.Name, (H.MethodKind)(int)original.MethodKind,
                new HS.ModifierSet(m.IsExport, m.IsStatic, m.IsAbstract, m.IsAsync, m.IsOverride, m.IsPrivate,
                    m.IsProtected, m.IsInternal, m.IsPublic, m.IsReadonly, m.IsSealed, m.IsStruct),
                Node(original.Declaration), (H.Accessibility)(int)original.DeclaredAccessibility);
            Register(original, result);
            result.IsStaticFlag = original.IsStaticFlag;
            result.IsVirtualFlag = original.IsVirtualFlag;
            result.IsAbstractFlag = original.IsAbstractFlag;
            result.IsOverrideFlag = original.IsOverrideFlag;
            result.IsSealedFlag = original.IsSealedFlag;
            result.ForceAsync = original.ForceAsync;
            result.IsGeneratorSyntax = original.IsGeneratorSyntax;
            result.SynthesizedKind = (HS.SynthesizedBodyKind)(int)original.SynthesizedKind;
            result.ParameterList = (HX.ParameterListSyntax?)Node(original.ParameterList);
            result.TypeParameterList = (HX.TypeParameterListSyntax?)Node(original.TypeParameterList);
            result.ReturnAnnotation = (HX.TypeAnnotationSyntax?)Node(original.ReturnAnnotation);
            result.BodySyntax = (HX.BlockSyntax?)Node(original.BodySyntax);
            if (!original.ExplicitParameters.IsDefault) result.ExplicitParameters = [.. original.ExplicitParameters.Select(p => Parameter(p, result))];
            if (original.ExplicitReturnType is { } type) result.ExplicitReturnType = Type(type);
            return result;
        }

        public HS.SourceFieldSymbol<SourceFile> Field(SourceFieldSymbol original)
        {
            if (symbols.TryGetValue(original, out H.Symbol? cached)) return (HS.SourceFieldSymbol<SourceFile>)cached;
            HS.SourceFieldSymbol<SourceFile> result = new(this, File, Container(original.ContainingType!), original.Name,
                (H.Accessibility)(int)original.DeclaredAccessibility, original.IsStatic, original.IsReadOnly, original.IsConstFlag,
                Node(original.Declaration), null, _ => Type(original.Type));
            Register(original, result);
            result.IsConstDeclaration = original.IsConstDeclaration;
            result.IsEnumMember = original.IsEnumMember;
            result.ConstantFactory = _ => original.ConstantValue is { } constant ? new H.ConstantValue(constant.Value) : null;
            return result;
        }

        public HS.SourcePropertySymbol<SourceFile> Property(SourcePropertySymbol original)
        {
            if (symbols.TryGetValue(original, out H.Symbol? cached)) return (HS.SourcePropertySymbol<SourceFile>)cached;
            HS.SourcePropertySymbol<SourceFile> result = new(this, File, Container(original.ContainingType!), original.Name,
                (H.Accessibility)(int)original.DeclaredAccessibility, original.IsStatic, Node(original.Declaration), _ => Type(original.Type), null);
            Register(original, result);
            result.IsVirtualFlag = original.IsVirtualFlag;
            result.IsAbstractFlag = original.IsAbstractFlag;
            result.IsOverrideFlag = original.IsOverrideFlag;
            result.IsSealedFlag = original.IsSealedFlag;
            result.IsInitOnlyProperty = original.IsInitOnlyProperty;
            if (original.Getter is { } getter) { result.Getter = Method(getter); result.Getter.AssociatedProperty = result; }
            if (original.Setter is { } setter) { result.Setter = Method(setter); result.Setter.AssociatedProperty = result; }
            return result;
        }

        private H.ParameterSymbol Parameter(ParameterSymbol parameter, H.Symbol owner) =>
            new(parameter.Name, Type(parameter.Type), parameter.Ordinal, owner, (H.RefKind)(int)parameter.RefKind, parameter.IsParams,
                parameter.DefaultValue is { } value ? new H.ConstantValue(value.Value) : null, parameter.IsOptional, parameter.IsThis);

        public override ImmutableArray<H.ParameterSymbol> BindParameters(SourceFile file, HS.SourceMethodSymbol<SourceFile> method, HX.ParameterListSyntax syntax)
        {
            ParametersBound++;
            SourceMethodSymbol original = (SourceMethodSymbol)originals[method];
            return [.. original.Parameters.Select(p => Parameter(p, method))];
        }

        public override H.TypeSymbol BindType(SourceFile file, H.Symbol owner, HX.TypeSyntax syntax, bool allowVoid)
        {
            TypesBound++;
            SourceMethodSymbol original = (SourceMethodSymbol)originals[owner];
            Binder binder = Binder(file, original);
            return Type(binder.BindType(original.ReturnAnnotation!.Type, allowVoid));
        }

        public override H.TypeSymbol AdjustDeclaredReturnType(SourceFile file, HS.SourceMethodSymbol<SourceFile> method, H.TypeSymbol type, HX.TypeSyntax syntax)
        {
            ReturnsAdjusted++;
            return Type(((SourceMethodSymbol)originals[method]).ReturnType);
        }

        public override H.TypeParameterConstraints BindConstraints(SourceFile file, H.Symbol owner, HX.TypeParameterSyntax syntax, H.TypeParameterSymbol parameter)
        {
            ConstraintsBound++;
            SourceMethodSymbol method = (SourceMethodSymbol)originals[owner];
            TypeParameterConstraints constraints = (TypeParameterConstraints)typeof(Binder).GetMethod("BindConstraints",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Binder(file, method),
                [method.TypeParameterList!.Parameters[parameter.Ordinal], method.TypeParameters[parameter.Ordinal]])!;
            return new(constraints.HasClass, constraints.HasStruct, constraints.HasNew, constraints.HasNotNull, [.. constraints.Types.Select(Type)]);
        }

        private Binder Binder(SourceFile file, Symbol owner) => (Binder)typeof(Compilation).GetMethod("CreateBinder",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Compilation, [file, owner, OriginalDiagnostics(Compilation)])!;

        public override HB.BoundBlock? BindMethodBody(SourceFile file, HS.SourceMethodSymbol<SourceFile> method)
        {
            BodiesBound++;
            if (Body is { } body) return body(method);
            SourceMethodSymbol original = (SourceMethodSymbol)originals[method];
            BoundBlock? bound = original.GetBoundBody();
            method.SetInferredReturnType(Type(original.ReturnType));
            return bound is null ? null : new HB.BoundBlock([], []);
        }

        public override H.PropertySymbol? FindOverriddenProperty(H.NamedTypeSymbol container, HS.SourcePropertySymbol<SourceFile> property)
        {
            SourcePropertySymbol original = (SourcePropertySymbol)originals[property];
            PropertySymbol? overridden = (PropertySymbol?)typeof(Compilation).Assembly.GetType("Caladesi.Compiler.DeclarationChecker")!
                .GetMethod("FindOverriddenProperty", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [original.ContainingType, original]);
            if (overridden is SourcePropertySymbol source) return Property(source);
            return overridden is null ? null : ((H.NamedTypeSymbol)Type(overridden.ContainingType!)).GetMembers(overridden.Name).OfType<H.PropertySymbol>().Single();
        }
    }
}
