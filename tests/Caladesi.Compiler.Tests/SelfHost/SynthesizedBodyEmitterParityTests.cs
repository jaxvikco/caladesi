extern alias SelfHost;

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using H = SelfHost::Caladesi.Compiler.Emit;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using HSS = SelfHost::Caladesi.Compiler.Symbols.Source;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using HX = SelfHost::Caladesi.Compiler.Syntax;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HD = SelfHost::Caladesi.Compiler.Diagnostics;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class SynthesizedBodyEmitterParityTests
{
    public static IEnumerable<object[]> DispatchCases()
    {
        foreach (var kind in Enum.GetValues<SynthesizedBodyKind>())
        {
            foreach (var valueType in new[] { false, true })
            {
                foreach (var shape in new[] { 0, 1, 2 })
                {
                    yield return [kind, valueType, shape];
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(DispatchCases))]
    public void EveryDispatchKind_PreservesILMetadataLocalsAndBranches(SynthesizedBodyKind kind, bool valueType, int shape)
    {
        Fixture fixture = new(valueType, shape);
        fixture.Compare(fixture.Method(kind, unionCase: shape));
    }

    [TestMethod]
    public void StaticAndInstanceAccessors_UseBackingFieldFlagsRatherThanMethodFlags()
    {
        foreach (var fieldStatic in new[] { false, true })
        {
            foreach (var methodStatic in new[] { false, true })
            {
                foreach (var kind in new[] { SynthesizedBodyKind.AutoGetter, SynthesizedBodyKind.AutoSetter })
                {
                    Fixture fixture = new(false, 0);
                    (var Expected, var Actual) = fixture.Property("Access", fixture.Special(SpecialType.Int32), fieldStatic);
                    var method = fixture.Method(kind);
                    method.Expected.AssociatedProperty = Expected;
                    method.Actual.AssociatedProperty = Actual;
                    typeof(SourceMethodSymbol).GetProperty(nameof(SourceMethodSymbol.IsStaticFlag))!.SetValue(method.Expected, methodStatic);
                    method.Actual.IsStaticFlag = methodStatic;
                    fixture.Compare(method);
                }
            }
        }
    }

    [TestMethod]
    public void SynthesizedAndUnrecognizedMethods_PreserveKindDetectionAndDefaultThrow()
    {
        foreach (var kind in new[] { SynthesizedBodyKind.RecordConstructor,
            SynthesizedBodyKind.RecordToString, SynthesizedBodyKind.None, SynthesizedBodyKind.RecordPrintMembers })
        {
            Fixture f = new(true, 1);
            SynthesizedMethodSymbol original = new(f.Owner, "M", MethodKind.Ordinary, Accessibility.Public,
                false, f.Compilation.GetSpecialType(SpecialType.Void), [])
            { SynthesizedKind = kind };
            HSS.SynthesizedMethodSymbol actual = new(f.SelfOwner, "M", HS.MethodKind.Ordinary, HS.Accessibility.Public,
                false, f.GetSpecialType(HS.SpecialType.Void), [])
            {
                SynthesizedKind = (HSS.SynthesizedBodyKind)(int)kind
            };
            f.Compare((original, actual));
        }
        Fixture plain = new(false, 0);
        plain.Compare((plain.Compilation.GetSpecialType(SpecialType.Object).GetMembers("ToString").OfType<MethodSymbol>().First(),
            plain.GetSpecialType(HS.SpecialType.Object).GetMembers("ToString").OfType<HS.MethodSymbol>().First()));
    }

    [TestMethod]
    public void MissingConstructorsAndTypedEquals_PreserveOriginalFailuresAndPartialEmission()
    {
        foreach (var kind in new[] { SynthesizedBodyKind.RecordClone, SynthesizedBodyKind.UnionImplicit,
            SynthesizedBodyKind.RecordEqualsObject, SynthesizedBodyKind.RecordEqualsOperator, SynthesizedBodyKind.RecordNotEqualsOperator })
        {
            Fixture f = new(true, 0, members: false);
            f.CompareFailure(f.Method(kind));
        }
        Fixture missingBaseCtor = new(false, 0);
        missingBaseCtor.Owner.SetBase(new SynthesizedNamedType("Base", null, TypeKind.Class));
        missingBaseCtor.SelfOwner.SetBase(new HSS.SynthesizedNamedType("Base", null, HS.TypeKind.Class));
        missingBaseCtor.CompareFailure(missingBaseCtor.Method(SynthesizedBodyKind.RecordConstructor));
        Fixture missingTag = new(true, 0, members: false);
        missingTag.CompareFailure(missingTag.Method(SynthesizedBodyKind.UnionConstructor));
    }

    [TestMethod]
    public void ConstructorLookup_SkipsSynthesizedDecoysAndPreservesFirstMatchingSourceCase()
    {
        Fixture f = new(true, 0, members: false);
        f.Owner.AddMember(new SynthesizedMethodSymbol(f.Owner, ".ctor", MethodKind.Constructor, Accessibility.Public,
            false, f.Compilation.GetSpecialType(SpecialType.Void), [])
        { SynthesizedKind = SynthesizedBodyKind.RecordCopyConstructor });
        f.SelfOwner.AddMember(new HSS.SynthesizedMethodSymbol(f.SelfOwner, ".ctor", HS.MethodKind.Constructor,
            HS.Accessibility.Public, false, f.GetSpecialType(HS.SpecialType.Void), []));
        _ = f.Method(SynthesizedBodyKind.RecordCopyConstructor);
        f.Compare(f.Method(SynthesizedBodyKind.RecordClone));
        Fixture union = new(true, 0, members: false);
        _ = union.Method(SynthesizedBodyKind.UnionConstructor, unionCase: 2);
        _ = union.Method(SynthesizedBodyKind.UnionConstructor, unionCase: 2);
        union.Compare(union.Method(SynthesizedBodyKind.UnionImplicit, unionCase: 2));
    }

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly OriginalAssembly = typeof(Compilation).Assembly;
    private static readonly Type ModuleType = OriginalAssembly.GetType("Caladesi.Compiler.Emit.EmitModule", true)!;
    private static readonly Type OriginalILType = OriginalAssembly.GetType("Caladesi.Compiler.Emit.ILBuilder", true)!;
    private static readonly Type OriginalEmitterType = OriginalAssembly.GetType("Caladesi.Compiler.Emit.SynthesizedBodyEmitter", true)!;
    private static readonly Lazy<HM.ReferenceSet> References = new(() => HM.ReferenceSet.Create(TestReferences.Paths));
    private static object? Invoke(object target, string name, params object?[] args)
    {
        return target.GetType().GetMethod(name, Instance)!.Invoke(target, args);
    }

    private static T Get<T>(object target, string name)
    {
        return (T)target.GetType().GetProperty(name, Instance)!.GetValue(target)!;
    }

    // A real source file owns explicitly declared source symbols. Only binding services are adapted;
    // metadata, generic construction, member discovery and IL generation run in each compiler.
    private sealed class Fixture : HSS.SourceSymbolContext<SourceFile>
    {
        private readonly SourceFile file;
        private readonly HX.SyntaxTree tree = HX.SyntaxTree.ParseText("", "synthesized.cala");
        private readonly object module;
        private readonly object originalIL = Activator.CreateInstance(OriginalILType)!;
        private readonly H.MetadataEmitter metadata;
        private readonly H.ILBuilder il = new();
        private readonly List<(SourcePropertySymbol Expected, HSS.SourcePropertySymbol<SourceFile> Actual)> properties = [];
        public Compilation Compilation { get; } = TestCompiler.Create("", OutputKind.DynamicallyLinkedLibrary);
        public SynthesizedNamedType Owner { get; }
        public HSS.SynthesizedNamedType SelfOwner { get; }
        public override HD.DiagnosticBag DeclarationDiagnostics { get; } = new();

        public Fixture(bool valueType, int shape, bool members = true)
        {
            file = Compilation.GetFile(Compilation.SyntaxTrees.Single());
            module = Activator.CreateInstance(ModuleType, Compilation)!;
            metadata = new(GetSpecialType, References.Value.FindType);
            Owner = new("Generated", null, valueType ? TypeKind.Struct : TypeKind.Class, Accessibility.Public,
                Compilation.GetSpecialType(valueType ? SpecialType.ValueType : SpecialType.Object));
            SelfOwner = new("Generated", null, valueType ? HS.TypeKind.Struct : HS.TypeKind.Class, HS.Accessibility.Public,
                GetSpecialType(valueType ? HS.SpecialType.ValueType : HS.SpecialType.Object));
            if (shape == 2)
            {
                Owner.SetTypeParameters([new TypeParameterSymbol("T", 0, Owner, VarianceKind.None)]);
                SelfOwner.SetTypeParameters([new HS.TypeParameterSymbol("T", 0, SelfOwner, HS.VarianceKind.None)]);
            }
            _ = Invoke(module, "RegisterTypeDefinition", Owner, MetadataTokens.TypeDefinitionHandle(1));
            metadata.RegisterTypeDefinition(SelfOwner, MetadataTokens.TypeDefinitionHandle(1));
            foreach (var builder in new[] { Get<MetadataBuilder>(module, "Metadata"), metadata.Metadata })
            {
                _ = builder.AddModule(0, builder.GetOrAddString("SynthesizedParity"),
                    builder.GetOrAddGuid(new Guid("3e2124db-748f-4d55-ae33-29c1767db285")), default, default);
            }

            if (!members)
            {
                return;
            }
            // Non-source properties and source properties without storage are intentionally ignored.
            Owner.AddMember(new SourcePropertySymbol(file, Owner, "NoStorage", Accessibility.Public, false, null, null,
                Compilation.GetSpecialType(SpecialType.Int64)));
            SelfOwner.AddMember(new HSS.SourcePropertySymbol<SourceFile>(this, file, SelfOwner, "NoStorage",
                HS.Accessibility.Public, false, null, null, GetSpecialType(HS.SpecialType.Int64)));
            Owner.AddMember(Compilation.GetSpecialType(SpecialType.String).GetMembers("Length").First());
            SelfOwner.AddMember(GetSpecialType(HS.SpecialType.String).GetMembers("Length")[0]);
            if (shape > 0)
            {
                _ = Property("ZFirst", Special(SpecialType.Int32));
                _ = Property("ASecond", Special(SpecialType.String));
                _ = Property("NullableValue", (new NullableTypeSymbol(Compilation.GetSpecialType(SpecialType.Int32)),
                    new HS.NullableTypeSymbol(GetSpecialType(HS.SpecialType.Int32))));
                if (shape == 2)
                {
                    _ = Property("Generic", (Owner.TypeParameters[0], SelfOwner.TypeParameters[0]));
                }
            }
            _ = Field("Tag", Special(SpecialType.Int32));
            _ = Field("Value", Special(SpecialType.Object));
            _ = Method(SynthesizedBodyKind.RecordCopyConstructor);
            _ = Method(SynthesizedBodyKind.RecordEquals);
            _ = Method(SynthesizedBodyKind.RecordEqualsOperator);
            for (var i = 0; i < 3; i++)
            {
                _ = Method(SynthesizedBodyKind.UnionConstructor, i);
            }
        }

        public (TypeSymbol Expected, HS.TypeSymbol Actual) Special(SpecialType special)
        {
            return (Compilation.GetSpecialType(special), GetSpecialType((HS.SpecialType)(int)special));
        }

        private (SourceFieldSymbol Expected, HSS.SourceFieldSymbol<SourceFile> Actual) Field(string name,
            (TypeSymbol Expected, HS.TypeSymbol Actual) type, bool isStatic = false)
        {
            SourceFieldSymbol expected = new(Compilation, file, Owner, name, Accessibility.Public, isStatic, false, false, null, type.Expected);
            HSS.SourceFieldSymbol<SourceFile> actual = new(this, file, SelfOwner, name, HS.Accessibility.Public,
                isStatic, false, false, null, type.Actual);
            Owner.AddMember(expected); SelfOwner.AddMember(actual);
            return (expected, actual);
        }

        public (SourcePropertySymbol Expected, HSS.SourcePropertySymbol<SourceFile> Actual) Property(string name,
            (TypeSymbol Expected, HS.TypeSymbol Actual) type, bool isStatic = false)
        {
            (var Expected, var Actual) = Field("<" + name + ">k__BackingField", type, isStatic);
            SourcePropertySymbol expected = new(file, Owner, name, Accessibility.Public, isStatic, null, null, type.Expected)
            { BackingField = Expected, IsAutoProperty = true };
            HSS.SourcePropertySymbol<SourceFile> actual = new(this, file, SelfOwner, name, HS.Accessibility.Public,
                isStatic, null, null, type.Actual)
            {
                BackingField = Actual,
                IsAutoProperty = true
            };
            Owner.AddMember(expected); SelfOwner.AddMember(actual);
            properties.Add((expected, actual));
            return (expected, actual);
        }

        public (SourceMethodSymbol Expected, HSS.SourceMethodSymbol<SourceFile> Actual) Method(SynthesizedBodyKind kind, int unionCase = 0)
        {
            var name = kind switch
            {
                SynthesizedBodyKind.RecordCopyConstructor or SynthesizedBodyKind.RecordConstructor or SynthesizedBodyKind.UnionConstructor => ".ctor",
                SynthesizedBodyKind.RecordEquals or SynthesizedBodyKind.UnionEquals => "Equals",
                SynthesizedBodyKind.RecordEqualsOperator or SynthesizedBodyKind.UnionEqualsOperator => "op_Equality",
                _ => kind.ToString()
            };
            var methodKind = name == ".ctor" ? MethodKind.Constructor : MethodKind.Ordinary;
            SourceMethodSymbol expected = new(Compilation, file, Owner, name, methodKind, default, null, Accessibility.Public)
            { SynthesizedKind = kind, UnionCaseIndex = unionCase, ExplicitReturnType = Compilation.GetSpecialType(SpecialType.Boolean) };
            HSS.SourceMethodSymbol<SourceFile> actual = new(this, file, SelfOwner, name, (HS.MethodKind)(int)methodKind,
                default, null, HS.Accessibility.Public)
            {
                SynthesizedKind = (HSS.SynthesizedBodyKind)(int)kind,
                UnionCaseIndex = unionCase,
                ExplicitReturnType = GetSpecialType(HS.SpecialType.Boolean)
            };
            var argument = unionCase switch
            {
                1 => Special(SpecialType.String),
                2 when Owner.IsGenericType => (Owner.TypeParameters[0], SelfOwner.TypeParameters[0]),
                2 => (new NullableTypeSymbol(Compilation.GetSpecialType(SpecialType.Int32)), new HS.NullableTypeSymbol(GetSpecialType(HS.SpecialType.Int32))),
                _ => Special(SpecialType.Int32)
            };
            typeof(SourceMethodSymbol).GetProperty(nameof(SourceMethodSymbol.ExplicitParameters))!.SetValue(expected,
                ImmutableArray.Create(new ParameterSymbol("arg", argument.Item1, 0, expected)));
            actual.ExplicitParameters = [new HS.ParameterSymbol("arg", argument.Item2, 0, actual)];
            if (kind is SynthesizedBodyKind.AutoGetter or SynthesizedBodyKind.AutoSetter)
            {
                (var Expected, var Actual) = properties.Count == 0 ? Property("Access", Special(SpecialType.Int32)) : properties[0];
                expected.AssociatedProperty = Expected; actual.AssociatedProperty = Actual;
            }
            Owner.AddMember(expected); SelfOwner.AddMember(actual);
            return (expected, actual);
        }

        public void Compare((MethodSymbol Expected, HS.MethodSymbol Actual) method)
        {
            var original = Activator.CreateInstance(OriginalEmitterType, Instance, null, [module, method.Expected, originalIL], null)!;
            _ = Invoke(original, "Emit");
            new H.SynthesizedBodyEmitter<SourceFile>(metadata, method.Actual, il, GetSpecialType, References.Value.FindType).Emit();
            CompareState();
            BlobBuilder left = new(), right = new();
            _ = new MethodBodyStreamEncoder(left).AddMethodBody(Get<InstructionEncoder>(originalIL, "Encoder"), Get<int>(originalIL, "MaxStack"));
            _ = new MethodBodyStreamEncoder(right).AddMethodBody(il.Encoder, il.MaxStack);
            CollectionAssert.AreEqual(left.ToArray(), right.ToArray(), "Fixed branches and body headers");
        }

        public void CompareFailure((MethodSymbol Expected, HS.MethodSymbol Actual) method)
        {
            var original = Activator.CreateInstance(OriginalEmitterType, Instance, null, [module, method.Expected, originalIL], null)!;
            var expected = Assert.ThrowsExactly<TargetInvocationException>(() => Invoke(original, "Emit"));
            Exception? actual = null;
            try { new H.SynthesizedBodyEmitter<SourceFile>(metadata, method.Actual, il, GetSpecialType, References.Value.FindType).Emit(); }
            catch (InvalidOperationException exception) { actual = exception; }
            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.InnerException!.GetType(), actual.GetType());
            CompareState();
        }

        private void CompareState()
        {
            CollectionAssert.AreEqual(Get<BlobBuilder>(originalIL, "Code").ToArray(), il.Code.ToArray(), "Raw IL and tokens");
            Assert.AreEqual(Get<int>(originalIL, "MaxStack"), il.MaxStack);
            Assert.AreEqual(Get<int>(originalIL, "CurrentDepth"), il.CurrentDepth);
            var locals = Get<IList>(originalIL, "Locals");
            Assert.HasCount(locals.Count, il.Locals);
            for (var i = 0; i < locals.Count; i++)
            {
                Assert.AreEqual(Get<int>(locals[i]!, "Index"), il.Locals[i].Index);
                Assert.AreEqual(Get<string?>(locals[i]!, "Name"), il.Locals[i].Name);
                var originalType = Get<TypeSymbol>(locals[i]!, "Type");
                Assert.AreEqual(originalType.ToDisplayString(), il.Locals[i].Type.ToDisplayString());
                BlobBuilder left = new(), right = new();
                _ = Invoke(module, "EncodeType", new SignatureTypeEncoder(left), originalType);
                metadata.EncodeType(new SignatureTypeEncoder(right), il.Locals[i].Type);
                CollectionAssert.AreEqual(left.ToArray(), right.ToArray(), "Local signature blobs");
            }
            CollectionAssert.AreEqual(MetadataBytes(Get<MetadataBuilder>(module, "Metadata")), MetadataBytes(metadata.Metadata),
                "Metadata tables, member/type specifications, signatures, strings and scopes");
        }
        private static byte[] MetadataBytes(MetadataBuilder builder)
        {
            BlobBuilder blob = new(); new MetadataRootBuilder(builder).Serialize(blob, 0, 0); return blob.ToArray();
        }
        public override HX.SyntaxTree GetTree(SourceFile source)
        {
            return tree;
        }

        public override string GetFilePath(SourceFile source)
        {
            return source.FilePath;
        }

        public override HS.NamedTypeSymbol GetSpecialType(HS.SpecialType special)
        {
            return References.Value.GetSpecialType(special)!;
        }

        public override bool IsSourceNamedType(HS.NamedTypeSymbol type)
        {
            return false;
        }

        public override ImmutableArray<HS.ParameterSymbol> BindParameters(SourceFile source, HSS.SourceMethodSymbol<SourceFile> method,
                    HX.ParameterListSyntax syntax)
        {
            throw new InvalidOperationException("Explicit parameters required");
        }

        public override HS.TypeSymbol BindType(SourceFile source, HS.Symbol owner, HX.TypeSyntax syntax, bool allowVoid)
        {
            throw new InvalidOperationException("Explicit types required");
        }

        public override HS.TypeSymbol AdjustDeclaredReturnType(SourceFile source, HSS.SourceMethodSymbol<SourceFile> method,
                    HS.TypeSymbol type, HX.TypeSyntax syntax)
        {
            return type;
        }

        public override HS.TypeParameterConstraints BindConstraints(SourceFile source, HS.Symbol owner, HX.TypeParameterSyntax syntax,
                    HS.TypeParameterSymbol parameter)
        {
            throw new InvalidOperationException("No source constraints");
        }

        public override HB.BoundBlock? BindMethodBody(SourceFile source, HSS.SourceMethodSymbol<SourceFile> method)
        {
            return null;
        }
    }
}
