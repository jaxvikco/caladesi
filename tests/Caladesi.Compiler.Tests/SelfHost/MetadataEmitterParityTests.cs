extern alias SelfHost;

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using S = Caladesi.Compiler.Symbols;
using SM = Caladesi.Compiler.Symbols.Metadata;
using SS = Caladesi.Compiler.Symbols.Source;
using H = SelfHost::Caladesi.Compiler.Emit;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using HSS = SelfHost::Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class MetadataEmitterParityTests
{
    [TestMethod]
    public void PrimitivesAndInvalidTypes_MatchOriginalSignatureBytesAndExceptions()
    {
        Fixture f = new();
        foreach (S.SpecialType special in new[] {
            S.SpecialType.Boolean, S.SpecialType.Char, S.SpecialType.SByte, S.SpecialType.Byte,
            S.SpecialType.Int16, S.SpecialType.UInt16, S.SpecialType.Int32, S.SpecialType.UInt32,
            S.SpecialType.Int64, S.SpecialType.UInt64, S.SpecialType.Single, S.SpecialType.Double,
            S.SpecialType.String, S.SpecialType.Object, S.SpecialType.IntPtr, S.SpecialType.UIntPtr })
        {
            Pair type = f.Special(special);
            f.Encode(type);
            f.Signature("EncodeFieldSignature", type);
            f.Token("GetTypeEntity", type);
            f.AssertCached("GetTypeEntity", type);
        }
        foreach (Pair invalid in new[] {
            new Pair(S.NullTypeSymbol.Instance, HS.NullTypeSymbol.Instance),
            new Pair(S.NeverTypeSymbol.Instance, HS.NeverTypeSymbol.Instance),
            new Pair(new S.ErrorTypeSymbol("missing"), new HS.ErrorTypeSymbol("missing")),
            new Pair(new SM.UnsupportedTypeSymbol("pointer"), new HM.UnsupportedTypeSymbol("pointer")) })
        {
            f.AssertFailure("EncodeTypeBlob", invalid);
            f.AssertFailure("GetTypeEntity", invalid);
        }
        f.AssertFailure("GetTypeEntity", Fixture.Generated("Unregistered"));
    }

    [TestMethod]
    public void NullableMapping_ErasesReferencesAndInstantiatesValuesIncludingConstrainedParameters()
    {
        Fixture f = new();
        Pair generic = Fixture.Member(Fixture.Type(typeof(RuntimeFixture<>)), nameof(RuntimeFixture<int>.Struct));
        Pair constrained = new(((S.MethodSymbol)generic.Expected).TypeParameters[0],
            ((HS.MethodSymbol)generic.Actual).TypeParameters[0]);
        Pair plain = Fixture.Parameter("T", 0, Fixture.Generated("Owner"));
        foreach (Pair underlying in new[] { f.Special(S.SpecialType.Int32), f.Special(S.SpecialType.String), constrained, plain })
        {
            Pair nullable = Nullable(underlying);
            Pair mapped = f.Map(nullable);
            Assert.AreEqual(((S.TypeSymbol)mapped.Expected).ToDisplayString(), ((HS.TypeSymbol)mapped.Actual).ToDisplayString());
            Assert.AreEqual(InvokeStatic("IsNullableValue", nullable.Expected), H.MetadataEmitter.IsNullableValue((HS.TypeSymbol)nullable.Actual));
            Assert.AreEqual(InvokeStatic("IsValueTypeLike", underlying.Expected), H.MetadataEmitter.IsValueTypeLike((HS.TypeSymbol)underlying.Actual));
            f.Encode(nullable);
            f.Token("GetTypeEntity", nullable);
            f.AssertCached("GetTypeEntity", nullable);
        }
        Pair reference = f.Special(S.SpecialType.String);
        Assert.AreSame(reference.Actual, f.Map(Nullable(reference)).Actual);
        f.Encode(Nullable(Nullable(f.Special(S.SpecialType.Int32))));
    }

    [TestMethod]
    public void GenericNestedAndArrayTypes_PreserveScopesInstantiationRanksAndAssemblyIdentity()
    {
        Fixture f = new();
        Pair integer = f.Special(S.SpecialType.Int32);
        Pair text = f.Special(S.SpecialType.String);
        Pair dictionary = Fixture.Type(typeof(Dictionary<,>));
        Pair nested = Fixture.Type(typeof(Dictionary<,>.KeyCollection));
        foreach (Pair type in new[] { dictionary, Construct(dictionary, integer, text), nested, Construct(nested, integer, text) })
        {
            f.Encode(type);
            f.AssertCached("GetTypeEntity", type);
        }
        foreach (int rank in new[] { 1, 2, 3, 32 })
        {
            Pair array = ArrayOf(Construct(dictionary, integer, text), rank);
            f.Encode(array);
            f.AssertCached("GetTypeEntity", array);
            f.Encode(ArrayOf(array, 1));
        }
        f.Token("GetTypeEntity", Fixture.Type(typeof(RuntimeFixture<>)));
        using MetadataReaderProvider image = f.Image();
        MetadataReader reader = image.GetMetadataReader();
        foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
        {
            AssemblyReference reference = reader.GetAssemblyReference(handle);
            string name = reader.GetString(reference.Name);
            Assembly assembly = name == typeof(object).Assembly.GetName().Name ? typeof(object).Assembly : typeof(RuntimeFixture<>).Assembly;
            CollectionAssert.AreEqual(assembly.GetName().GetPublicKeyToken() ?? [], reader.GetBlobBytes(reference.PublicKeyOrToken), name);
            Assert.AreEqual(assembly.GetName().Version, reference.Version);
            Assert.AreEqual(0, (int)reference.Flags);
            Assert.IsTrue(reference.HashValue.IsNil);
        }
        Assert.IsTrue(reader.TypeReferences.Any(handle => reader.GetTypeReference(handle).ResolutionScope.Kind == HandleKind.TypeReference));
        Assert.IsTrue(reader.AssemblyReferences.Any(handle => reader.GetBlobBytes(reader.GetAssemblyReference(handle).PublicKeyOrToken).Length == 8));
        Assert.IsTrue(reader.AssemblyReferences.Any(handle => reader.GetBlobBytes(reader.GetAssemblyReference(handle).PublicKeyOrToken).Length == 0));
    }

    [TestMethod]
    public void ParameterMaps_PartitionCachesByPresenceAndPreserveOwnGenericInstantiation()
    {
        Fixture f = new();
        Pair owner = Fixture.Generated("Box");
        Pair method = Fixture.SynthesizedMethod(owner, "M", f.Special(S.SpecialType.Int32));
        Pair methodParameter = Fixture.Parameter("T", 0, method);
        Pair classParameter = Fixture.Parameter("T", 0, owner);
        ((SS.SynthesizedNamedType)owner.Expected).SetTypeParameters([(S.TypeParameterSymbol)classParameter.Expected]);
        ((HSS.SynthesizedNamedType)owner.Actual).SetTypeParameters([(HS.TypeParameterSymbol)classParameter.Actual]);
        f.Register("RegisterTypeDefinition", owner, MetadataTokens.TypeDefinitionHandle(1));
        Pair field = Fixture.Field(owner, "Value", methodParameter);
        EntityHandle original = f.Token("GetTypeEntity", methodParameter);
        EntityHandle own = f.Token("GetTypeEntity", owner);
        EntityHandle originalMethod = f.Token("GetMethodEntity", method);
        EntityHandle originalField = f.Token("GetFieldEntity", field);
        CollectionAssert.AreEqual(new byte[] { 0x1e, 0 }, f.Encode(methodParameter));
        CollectionAssert.AreEqual(new byte[] { 0x13, 0 }, f.Encode(classParameter));
        f.SetMap(methodParameter, 2);
        CollectionAssert.AreEqual(new byte[] { 0x13, 2 }, f.Encode(methodParameter));
        EntityHandle mapped = f.Token("GetTypeEntity", methodParameter);
        Assert.AreNotEqual(original, mapped);
        Assert.AreNotEqual(own, f.Token("GetTypeEntity", owner));
        EntityHandle mappedMethod = f.Token("GetMethodEntity", method);
        EntityHandle mappedField = f.Token("GetFieldEntity", field);
        Assert.AreNotEqual(originalMethod, mappedMethod);
        Assert.AreNotEqual(originalField, mappedField);
        f.SetMap(methodParameter, 9);
        CollectionAssert.AreEqual(new byte[] { 0x13, 9 }, f.Encode(methodParameter));
        Assert.AreEqual(mapped, f.Token("GetTypeEntity", methodParameter));
        Assert.AreEqual(mappedMethod, f.Token("GetMethodEntity", method));
        Assert.AreEqual(mappedField, f.Token("GetFieldEntity", field));
        f.SetMap(null);
        Assert.AreEqual(original, f.Token("GetTypeEntity", methodParameter));
        Assert.AreEqual(originalMethod, f.Token("GetMethodEntity", method));
        Assert.AreEqual(originalField, f.Token("GetFieldEntity", field));
        f.SetMap(null, empty: true);
        Assert.AreEqual(mapped, f.Token("GetTypeEntity", methodParameter));
    }

    [TestMethod]
    public void RuntimeMembers_EncodeFieldsPropertiesByRefInitOnlyAndGenericSignatures()
    {
        Fixture f = new();
        Pair definition = Fixture.Type(typeof(RuntimeFixture<>));
        Pair constructed = Construct(definition, f.Special(S.SpecialType.Int32));
        foreach (Pair owner in new[] { definition, constructed })
        {
            foreach (string name in new[] { nameof(RuntimeFixture<int>.Generic), nameof(RuntimeFixture<int>.Refs),
                nameof(RuntimeFixture<int>.RefReturn), "set_Value", "get_Item" })
            {
                Pair method = Fixture.Member(owner, name);
                f.Signature("EncodeMethodSignature", method);
                f.AssertCached("GetMethodEntity", method);
            }
            foreach (string name in new[] { nameof(RuntimeFixture<int>.Field), nameof(RuntimeFixture<int>.StaticField) })
            {
                Pair field = Fixture.Member(owner, name);
                f.Signature("EncodeFieldSignature", new Pair(((S.FieldSymbol)field.Expected).Type, ((HS.FieldSymbol)field.Actual).Type));
                f.AssertCached("GetFieldEntity", field);
            }
            foreach (string name in new[] { nameof(RuntimeFixture<int>.Value), "Item" })
                f.Signature("EncodePropertySignature", Fixture.Member(owner, name));
        }
        Pair setter = Fixture.Member(definition, "set_Value");
        Assert.IsTrue(((HS.MethodSymbol)setter.Actual).IsInitOnly);
        byte[] initSignature = f.Signature("EncodeMethodSignature", setter);
        Assert.AreEqual(0x1f, initSignature[2]);
        Pair byRef = Fixture.Member(definition, nameof(RuntimeFixture<int>.RefReturn));
        Assert.IsTrue(((HS.MethodSymbol)byRef.Actual).ReturnsByRef);
        Assert.AreEqual(0x10, f.Signature("EncodeMethodSignature", byRef)[2]);
        Pair refs = Fixture.Member(definition, nameof(RuntimeFixture<int>.Refs));
        Assert.IsTrue(((HS.MethodSymbol)refs.Actual).Parameters.All(parameter => parameter.RefKind != HS.RefKind.None));
        Assert.HasCount(3, f.Signature("EncodeMethodSignature", refs).Where(value => value == 0x10));
        Pair never = Fixture.SynthesizedMethod(Fixture.Generated("NeverOwner"), "Never",
            new Pair(S.NeverTypeSymbol.Instance, HS.NeverTypeSymbol.Instance));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 1 }, f.Signature("EncodeMethodSignature", never));
        Fixture withoutInit = new(missingWellKnownTypes: true);
        Pair unmodifiedSetter = Fixture.Member(Fixture.Type(typeof(RuntimeFixture<>)), "set_Value");
        Assert.AreEqual(1, withoutInit.Signature("EncodeMethodSignature", unmodifiedSetter)[2]);
    }

    [TestMethod]
    public void MethodSpecifications_CacheExactBlobsAndDistinguishClassFromMethodParameters()
    {
        Fixture f = new();
        Pair owner = Fixture.Type(typeof(RuntimeFixture<>));
        Pair generic = Fixture.Member(owner, nameof(RuntimeFixture<int>.Generic));
        Pair methodParameter = new(((S.MethodSymbol)generic.Expected).TypeParameters[0],
            ((HS.MethodSymbol)generic.Actual).TypeParameters[0]);
        Pair classParameter = new(((S.NamedTypeSymbol)owner.Expected).TypeParameters[0],
            ((HS.NamedTypeSymbol)owner.Actual).TypeParameters[0]);
        Pair methodArgument = ConstructMethod(generic, methodParameter);
        Pair classArgument = ConstructMethod(generic, classParameter);
        EntityHandle methodSpec = f.Token("GetMethodEntity", methodArgument);
        EntityHandle classSpec = f.Token("GetMethodEntity", classArgument);
        Assert.AreEqual(HandleKind.MethodSpecification, methodSpec.Kind);
        Assert.AreNotEqual(methodSpec, classSpec);
        Assert.AreEqual(methodSpec, f.Token("GetMethodEntity", ConstructMethod(generic, methodParameter)));
        f.SetMap(methodParameter, 0);
        EntityHandle mapped = f.Token("GetMethodEntity", methodArgument);
        Assert.AreNotEqual(methodSpec, mapped);
        Assert.AreEqual(mapped, f.Token("GetMethodEntity", classArgument));
        f.SetMap(null);
        Assert.AreEqual(methodSpec, f.Token("GetMethodEntity", methodArgument));
        f.AssertCached("GetMethodEntity", ConstructMethod(generic, f.Special(S.SpecialType.String)));
        using MetadataReaderProvider image = f.Image();
        MetadataReader reader = image.GetMetadataReader();
        CollectionAssert.AreEqual(new byte[] { 0x0a, 1, 0x1e, 0 },
            reader.GetBlobBytes(reader.GetMethodSpecification((MethodSpecificationHandle)methodSpec).Signature));
        CollectionAssert.AreEqual(new byte[] { 0x0a, 1, 0x13, 0 },
            reader.GetBlobBytes(reader.GetMethodSpecification((MethodSpecificationHandle)classSpec).Signature));
    }

    [TestMethod]
    public void DefinitionRegistration_UsesDefinitionsExceptOnGenericContainersAndOverwritesHandles()
    {
        Fixture f = new();
        Pair owner = Fixture.Generated("Registered");
        Pair method = Fixture.SynthesizedMethod(owner, "M", f.Special(S.SpecialType.Int32));
        Pair field = Fixture.Field(owner, "F", f.Special(S.SpecialType.Int32));
        Pair property = Fixture.Member(Fixture.Type(typeof(RuntimeFixture<>)), nameof(RuntimeFixture<int>.Value));
        f.Register("RegisterTypeDefinition", owner, MetadataTokens.TypeDefinitionHandle(7));
        f.Register("RegisterMethod", method, MetadataTokens.MethodDefinitionHandle(8));
        f.Register("RegisterField", field, MetadataTokens.FieldDefinitionHandle(9));
        f.Register("RegisterProperty", property, MetadataTokens.PropertyDefinitionHandle(10));
        Assert.AreEqual((EntityHandle)MetadataTokens.TypeDefinitionHandle(7), f.Token("GetTypeEntity", owner));
        Assert.AreEqual((EntityHandle)MetadataTokens.MethodDefinitionHandle(8), f.Token("GetMethodEntity", method));
        Assert.AreEqual((EntityHandle)MetadataTokens.FieldDefinitionHandle(9), f.Token("GetFieldEntity", field));
        f.Register("RegisterMethod", method, MetadataTokens.MethodDefinitionHandle(11));
        Assert.AreEqual((EntityHandle)MetadataTokens.MethodDefinitionHandle(11), f.Token("GetMethodEntity", method));
        Assert.AreEqual(MetadataTokens.PropertyDefinitionHandle(10), ((IDictionary)Field(f.Actual, "propertyDefs"))[property.Actual]);
        Pair methodParameter = Fixture.Parameter("U", 0, method);
        typeof(SS.SynthesizedMethodSymbol).GetMethod("SetTypeParameters", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(method.Expected, [ImmutableArray.Create((S.TypeParameterSymbol)methodParameter.Expected)]);
        ((HSS.SynthesizedMethodSymbol)method.Actual).SetTypeParameters([(HS.TypeParameterSymbol)methodParameter.Actual]);
        EntityHandle specification = f.Token("GetMethodEntity", ConstructMethod(method, f.Special(S.SpecialType.Int32)));
        using (MetadataReaderProvider image = f.Image())
            Assert.AreEqual((EntityHandle)MetadataTokens.MethodDefinitionHandle(11),
                image.GetMetadataReader().GetMethodSpecification((MethodSpecificationHandle)specification).Method);
        Pair genericOwner = Fixture.Generated("GenericRegistered");
        Pair parameter = Fixture.Parameter("T", 0, genericOwner);
        ((SS.SynthesizedNamedType)genericOwner.Expected).SetTypeParameters([(S.TypeParameterSymbol)parameter.Expected]);
        ((HSS.SynthesizedNamedType)genericOwner.Actual).SetTypeParameters([(HS.TypeParameterSymbol)parameter.Actual]);
        Pair genericMethod = Fixture.SynthesizedMethod(genericOwner, "M", f.Special(S.SpecialType.Int32));
        Pair genericField = Fixture.Field(genericOwner, "F", parameter);
        f.Register("RegisterTypeDefinition", genericOwner, MetadataTokens.TypeDefinitionHandle(12));
        f.Register("RegisterMethod", genericMethod, MetadataTokens.MethodDefinitionHandle(13));
        f.Register("RegisterField", genericField, MetadataTokens.FieldDefinitionHandle(14));
        Assert.AreEqual(HandleKind.MemberReference, f.Token("GetMethodEntity", genericMethod).Kind);
        Assert.AreEqual(HandleKind.MemberReference, f.Token("GetFieldEntity", genericField).Kind);
        Pair constructed = Construct(genericOwner, f.Special(S.SpecialType.Int32));
        f.Token("GetTypeDefOrRef", constructed);
        f.Register("RegisterTypeDefinition", genericOwner, MetadataTokens.TypeDefinitionHandle(15));
        Assert.AreEqual((EntityHandle)MetadataTokens.TypeDefinitionHandle(15), f.Token("GetTypeDefOrRef", constructed));
    }

    [TestMethod]
    public void SynthesizedContainerMaps_EncodeDefinitionSignaturesWithoutLeakingCallSiteMaps()
    {
        Fixture f = new();
        Pair container = Fixture.Generated("Closure");
        Pair method = Fixture.SynthesizedMethod(container, "M", f.Special(S.SpecialType.Int32));
        Pair parameter = Fixture.Parameter("Captured", 3, method);
        ((SS.SynthesizedNamedType)container.Expected).MethodTypeParameterMap = new() { [(S.TypeParameterSymbol)parameter.Expected] = 1 };
        ((HSS.SynthesizedNamedType)container.Actual).MethodTypeParameterMap = new() { [(HS.TypeParameterSymbol)parameter.Actual] = 1 };
        typeof(SS.SynthesizedMethodSymbol).GetMethod("SetReturnType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(method.Expected, [parameter.Expected]);
        ((HSS.SynthesizedMethodSymbol)method.Actual).SetReturnType((HS.TypeSymbol)parameter.Actual);
        f.Register("RegisterTypeDefinition", container, MetadataTokens.TypeDefinitionHandle(1));
        Pair field = Fixture.Field(container, "Captured", parameter);
        f.SetMap(parameter, 8);
        EntityHandle methodHandle = f.Token("GetMethodEntity", method);
        EntityHandle fieldHandle = f.Token("GetFieldEntity", field);
        CollectionAssert.AreEqual(new byte[] { 0x13, 8 }, f.Encode(parameter));
        using MetadataReaderProvider image = f.Image();
        MetadataReader reader = image.GetMetadataReader();
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0x13, 1 },
            reader.GetBlobBytes(reader.GetMemberReference((MemberReferenceHandle)methodHandle).Signature));
        CollectionAssert.AreEqual(new byte[] { 6, 0x13, 1 },
            reader.GetBlobBytes(reader.GetMemberReference((MemberReferenceHandle)fieldHandle).Signature));
    }

    private static readonly Type OracleType = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.EmitModule", true)!;
    private const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed record Pair(object Expected, object Actual);

    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, Methods)!.Invoke(target, arguments);

    private static object? InvokeStatic(string name, object argument) =>
        OracleType.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [argument]);

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Methods)!.GetValue(target)!;

    private static Pair Nullable(Pair type) =>
        new(new S.NullableTypeSymbol((S.TypeSymbol)type.Expected), new HS.NullableTypeSymbol((HS.TypeSymbol)type.Actual));

    private static Pair ArrayOf(Pair type, int rank) =>
        new(new S.ArrayTypeSymbol((S.TypeSymbol)type.Expected, null, rank), new HS.ArrayTypeSymbol((HS.TypeSymbol)type.Actual, null, rank));

    private static Pair Construct(Pair definition, params Pair[] arguments) => new(
        ((S.NamedTypeSymbol)definition.Expected).Construct(arguments.Select(argument => (S.TypeSymbol)argument.Expected).ToImmutableArray()),
        ((HS.NamedTypeSymbol)definition.Actual).Construct(arguments.Select(argument => (HS.TypeSymbol)argument.Actual).ToImmutableArray()));

    private static Pair ConstructMethod(Pair definition, Pair argument) => new(
        ((S.MethodSymbol)definition.Expected).Construct([(S.TypeSymbol)argument.Expected]),
        ((HS.MethodSymbol)definition.Actual).Construct([(HS.TypeSymbol)argument.Actual]));

    private sealed class Fixture
    {
        private static readonly string[] Paths = [typeof(object).Assembly.Location, typeof(RuntimeFixture<>).Assembly.Location];
        private static readonly Lazy<SM.ReferenceSet> OriginalReferences = new(() => SM.ReferenceSet.Create(Paths));
        private static readonly Lazy<HM.ReferenceSet> SelfReferences = new(() => HM.ReferenceSet.Create(Paths));
        private readonly Compilation compilation;
        private readonly object expected;
        public H.MetadataEmitter Actual { get; }
        private MetadataBuilder ExpectedMetadata => (MetadataBuilder)OracleType.GetProperty("Metadata")!.GetValue(expected)!;

        public Fixture(bool missingWellKnownTypes = false)
        {
            compilation = Compilation.Create(new CompilationOptions(), [],
                missingWellKnownTypes ? SM.ReferenceSet.Empty : OriginalReferences.Value);
            expected = Activator.CreateInstance(OracleType, compilation)!;
            Actual = new H.MetadataEmitter(special => SelfReferences.Value.GetSpecialType(special)!,
                name => missingWellKnownTypes ? null : SelfReferences.Value.FindType(name));
            foreach (MetadataBuilder metadata in new[] { ExpectedMetadata, Actual.Metadata })
                metadata.AddModule(0, metadata.GetOrAddString("MetadataEmitterParity"),
                    metadata.GetOrAddGuid(new Guid("62735412-a66c-43e9-86ad-32f643cd522b")), default, default);
        }

        public Pair Special(S.SpecialType type) =>
            new(compilation.GetSpecialType(type), SelfReferences.Value.GetSpecialType((HS.SpecialType)(int)type)!);

        public static Pair Type(Type type) => new(OriginalReferences.Value.FindType(type.FullName!)!, SelfReferences.Value.FindType(type.FullName!)!);

        public static Pair Generated(string name) =>
            new(new SS.SynthesizedNamedType(name, null, S.TypeKind.Class), new HSS.SynthesizedNamedType(name, null, HS.TypeKind.Class));

        public static Pair Parameter(string name, int ordinal, Pair owner) => new(
            new S.TypeParameterSymbol(name, ordinal, (S.Symbol)owner.Expected, S.VarianceKind.None),
            new HS.TypeParameterSymbol(name, ordinal, (HS.Symbol)owner.Actual, HS.VarianceKind.None));

        public static Pair SynthesizedMethod(Pair owner, string name, Pair result) => new(
            new SS.SynthesizedMethodSymbol((S.Symbol)owner.Expected, name, S.MethodKind.Ordinary, S.Accessibility.Public, true, (S.TypeSymbol)result.Expected, []),
            new HSS.SynthesizedMethodSymbol((HS.Symbol)owner.Actual, name, HS.MethodKind.Ordinary, HS.Accessibility.Public, true, (HS.TypeSymbol)result.Actual, []));

        public static Pair Field(Pair owner, string name, Pair type) => new(
            new SS.SynthesizedFieldSymbol((S.Symbol)owner.Expected, name, (S.TypeSymbol)type.Expected),
            new HSS.SynthesizedFieldSymbol((HS.Symbol)owner.Actual, name, (HS.TypeSymbol)type.Actual));

        public static Pair Member(Pair owner, string name) => new(
            ((S.NamedTypeSymbol)owner.Expected).GetMembers(name).Single(), ((HS.NamedTypeSymbol)owner.Actual).GetMembers(name).Single());

        public Pair Map(Pair type) => new(Invoke(expected, "ClrType", type.Expected)!, Actual.ClrType((HS.TypeSymbol)type.Actual));

        public void SetMap(Pair? parameter, int ordinal = 0, bool empty = false)
        {
            Dictionary<S.TypeParameterSymbol, int>? original = parameter == null
                ? (empty ? [] : null) : new() { [(S.TypeParameterSymbol)parameter.Expected] = ordinal };
            Dictionary<HS.TypeParameterSymbol, int>? actual = parameter == null
                ? (empty ? [] : null) : new() { [(HS.TypeParameterSymbol)parameter.Actual] = ordinal };
            Invoke(expected, "SetClassTypeParameterMap", original);
            Actual.SetClassTypeParameterMap(actual);
        }

        public void Register(string method, Pair symbol, object handle)
        {
            Invoke(expected, method, symbol.Expected, handle);
            Invoke(Actual, method, symbol.Actual, handle);
            Check();
        }

        public EntityHandle Token(string method, Pair symbol)
        {
            EntityHandle left = (EntityHandle)Invoke(expected, method, symbol.Expected)!;
            EntityHandle right = (EntityHandle)Invoke(Actual, method, symbol.Actual)!;
            Assert.AreEqual(left, right, method);
            Check();
            return right;
        }

        public void AssertCached(string method, Pair symbol)
        {
            EntityHandle first = Token(method, symbol);
            byte[] before = Bytes(Actual.Metadata);
            Assert.AreEqual(first, Token(method, symbol));
            CollectionAssert.AreEqual(before, Bytes(Actual.Metadata), method + " cache hit");
        }

        public byte[] Encode(Pair type)
        {
            BlobBuilder left = new();
            BlobBuilder right = new();
            Invoke(expected, "EncodeType", new SignatureTypeEncoder(left), type.Expected);
            Actual.EncodeType(new SignatureTypeEncoder(right), (HS.TypeSymbol)type.Actual);
            CollectionAssert.AreEqual(left.ToArray(), right.ToArray());
            Check();
            return right.ToArray();
        }

        public byte[] Signature(string method, Pair symbol)
        {
            BlobHandle left = (BlobHandle)Invoke(expected, method, symbol.Expected)!;
            BlobHandle right = (BlobHandle)Invoke(Actual, method, symbol.Actual)!;
            Assert.AreEqual(left, right, method);
            Check();
            using MetadataReaderProvider image = Image();
            return image.GetMetadataReader().GetBlobBytes(right);
        }

        public void AssertFailure(string method, Pair symbol)
        {
            TargetInvocationException left = Assert.ThrowsExactly<TargetInvocationException>(() => Invoke(expected, method, symbol.Expected));
            TargetInvocationException right = Assert.ThrowsExactly<TargetInvocationException>(() => Invoke(Actual, method, symbol.Actual));
            Assert.AreEqual(left.InnerException!.GetType(), right.InnerException!.GetType());
            Assert.AreEqual(left.InnerException.Message, right.InnerException.Message);
            Check();
        }

        public MetadataReaderProvider Image() => MetadataReaderProvider.FromMetadataImage(ImmutableArray.CreateRange(Bytes(Actual.Metadata)));

        private void Check()
        {
            CollectionAssert.AreEqual(Bytes(ExpectedMetadata), Bytes(Actual.Metadata));
            foreach ((string original, string actual) in new[] {
                ("_assemblyRefs", "assemblyRefs"), ("_typeHandles", "typeHandles"), ("_typeSpecs", "typeSpecs"),
                ("_memberRefs", "memberRefs"), ("_methodSpecs", "methodSpecs"),
                ("_methodDefs", "methodDefs"), ("_fieldDefs", "fieldDefs"), ("_propertyDefs", "propertyDefs") })
                Assert.HasCount(((IDictionary)MetadataEmitterParityTests.Field(expected, original)).Count,
                    (IDictionary)MetadataEmitterParityTests.Field(Actual, actual), actual);
        }

        private static byte[] Bytes(MetadataBuilder metadata)
        {
            // Serialization links the GUID and user-string builders into its output.
            // Clone those buffers so intermediate oracle snapshots remain non-destructive.
            MetadataBuilder snapshot = (MetadataBuilder)typeof(object)
                .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(metadata, null)!;
            foreach (string name in new[] { "_userStringBuilder", "_guidBuilder" })
            {
                FieldInfo field = typeof(MetadataBuilder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
                BlobBuilder original = (BlobBuilder)field.GetValue(metadata)!;
                BlobBuilder copy = (BlobBuilder)Activator.CreateInstance(original.GetType(), original.Count)!;
                copy.WriteBytes(original.ToArray());
                field.SetValue(snapshot, copy);
            }
            BlobBuilder bytes = new();
            new MetadataRootBuilder(snapshot).Serialize(bytes, 0, 0);
            return bytes.ToArray();
        }
    }

#pragma warning disable CA1000, CA1034, CA1045, CA1051, CA1715, CA1822, CA2211 // Public runtime metadata fixture, not a production API.
    public sealed class RuntimeFixture<T>
    {
        public T Field = default!;
        public static int StaticField = 1;
        public T Value { get; init; } = default!;
        public T this[int index, string name] => Field;
        private int storage;
        public ref int RefReturn(ref int value) { storage = value; return ref storage; }
        public static U Generic<U>(U value) => value;
        public static U Struct<U>(U value) where U : struct => value;
        public static void Refs(ref int value, in long read, out byte result) { value += (int)read; result = 0; }
    }
#pragma warning restore CA1000, CA1034, CA1045, CA1051, CA1715, CA1822, CA2211
}
