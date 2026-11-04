extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Caladesi.Compiler.Tests.Emit;
using H = SelfHost::Caladesi.Compiler.Emit;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HD = SelfHost::Caladesi.Compiler.Diagnostics;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using HSS = SelfHost::Caladesi.Compiler.Symbols.Source;
using HX = SelfHost::Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class SynthesizedBodyExecutionTests
{
    [TestMethod]
    public void RecordConstructorAndAutoAccessors_InitializeAndUpdateBothFields()
    {
        new Fixture().Execute(type =>
        {
            var value = Create(type, 17, "initial");
            var first = type.GetProperty("First")!;
            var second = type.GetProperty("Second")!;
            Assert.AreEqual(17, first.GetValue(value));
            Assert.AreEqual("initial", second.GetValue(value));
            first.SetValue(value, -4);
            second.SetValue(value, "updated");
            Assert.AreEqual(-4, first.GetValue(value));
            Assert.AreEqual("updated", second.GetValue(value));
        });
    }

    [TestMethod]
    public void RecordEquals_ComparesEveryFieldAndRejectsNullAndUnrelatedObjects()
    {
        new Fixture().Execute(type =>
        {
            var value = Create(type, 5, "same");
            var equals = type.GetMethod("Equals", [type])!;
            Assert.IsTrue((bool)equals.Invoke(value, [Create(type, 5, "same")])!);
            Assert.IsFalse((bool)equals.Invoke(value, [Create(type, 6, "same")])!);
            Assert.IsFalse((bool)equals.Invoke(value, [Create(type, 5, "different")])!);
            Assert.IsFalse((bool)equals.Invoke(value, [null])!);
            Assert.IsTrue(value.Equals(Create(type, 5, "same")));
            Assert.IsFalse(value.Equals(null));
            Assert.IsFalse(value.Equals("unrelated"));
            Assert.IsTrue(Create(type, 5, null).Equals(Create(type, 5, null)));
            Assert.IsFalse(value.Equals(Create(type, 5, null)));
        });
    }

    [TestMethod]
    public void RecordGetHashCode_IsConsistentWithEqualityIncludingNullFields()
    {
        new Fixture().Execute(type =>
        {
            foreach (var text in new[] { "same", null })
            {
                var left = Create(type, 23, text);
                var right = Create(type, 23, text);
                Assert.IsTrue(left.Equals(right));
                Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
                var expected = unchecked((23 * -1521134295) + (text?.GetHashCode() ?? 0));
                Assert.AreEqual(expected, left.GetHashCode());
            }
        });
    }

    [TestMethod]
    public void RecordToString_PreservesPropertyOrderAndBoxesValueFields()
    {
        new Fixture().Execute(type =>
        {
            Assert.AreEqual("RuntimeRecord { First = 42, Second = text }",
                Create(type, 42, "text").ToString());
            Assert.AreEqual("RuntimeRecord { First = -7, Second =  }",
                Create(type, -7, null).ToString());
        });
    }

    private static object Create(Type type, int first, string? second)
    {
        return Activator.CreateInstance(type, [first, second])!;
    }

    private sealed class Fixture : HSS.SourceSymbolContext<string>
    {
        private static readonly Lazy<HM.ReferenceSet> s_references =
            new(() => HM.ReferenceSet.Create(TestReferences.Paths));
        private readonly HM.ReferenceSet references = s_references.Value;
        private readonly H.MetadataEmitter metadata;
        private readonly HSS.SynthesizedNamedType owner =
            new("RuntimeRecord", null, HS.TypeKind.Class, HS.Accessibility.Public);
        private readonly List<HSS.SourcePropertySymbol<string>> properties = [];
        private readonly List<HSS.SourceMethodSymbol<string>> methods = [];

        public Fixture()
        {
            metadata = new(GetSpecialType, name => references.FindType(name));
            owner.SetBase(GetSpecialType(HS.SpecialType.Object));
            AddProperty("First", GetSpecialType(HS.SpecialType.Int32));
            AddProperty("Second", GetSpecialType(HS.SpecialType.String));
            _ = AddMethod(".ctor", HS.MethodKind.Constructor, HSS.SynthesizedBodyKind.RecordConstructor,
                GetSpecialType(HS.SpecialType.Void),
                [("first", properties[0].Type), ("second", properties[1].Type)]);
            _ = AddMethod("Equals", HS.MethodKind.Ordinary, HSS.SynthesizedBodyKind.RecordEquals,
                GetSpecialType(HS.SpecialType.Boolean), [("other", owner)]);
            _ = AddMethod("Equals", HS.MethodKind.Ordinary, HSS.SynthesizedBodyKind.RecordEqualsObject,
                GetSpecialType(HS.SpecialType.Boolean), [("other", GetSpecialType(HS.SpecialType.Object))],
                isOverride: true);
            _ = AddMethod("GetHashCode", HS.MethodKind.Ordinary, HSS.SynthesizedBodyKind.RecordGetHashCode,
                GetSpecialType(HS.SpecialType.Int32), [], isOverride: true);
            _ = AddMethod("ToString", HS.MethodKind.Ordinary, HSS.SynthesizedBodyKind.RecordToString,
                GetSpecialType(HS.SpecialType.String), [], isOverride: true);
        }

        private void AddProperty(string name, HS.TypeSymbol type)
        {
            HSS.SourceFieldSymbol<string> field = new(this, "", owner, "<" + name + ">k__BackingField",
                HS.Accessibility.Private, false, false, false, null, type)
            { IsBackingField = true };
            HSS.SourcePropertySymbol<string> property = new(this, "", owner, name,
                HS.Accessibility.Public, false, null, null, type)
            {
                IsAutoProperty = true,
                BackingField = field,
                Getter = AddMethod("get_" + name, HS.MethodKind.PropertyGet,
                    HSS.SynthesizedBodyKind.AutoGetter, type, []),
                Setter = AddMethod("set_" + name, HS.MethodKind.PropertySet,
                    HSS.SynthesizedBodyKind.AutoSetter, GetSpecialType(HS.SpecialType.Void), [("value", type)])
            };
            property.Getter.AssociatedProperty = property;
            property.Setter.AssociatedProperty = property;
            owner.AddMember(field);
            owner.AddMember(property);
            properties.Add(property);
        }

        private HSS.SourceMethodSymbol<string> AddMethod(string name, HS.MethodKind kind,
            HSS.SynthesizedBodyKind bodyKind, HS.TypeSymbol returnType,
            (string Name, HS.TypeSymbol Type)[] parameters, bool isOverride = false)
        {
            HSS.SourceMethodSymbol<string> method = new(this, "", owner, name, kind, default, null,
                HS.Accessibility.Public)
            {
                SynthesizedKind = bodyKind,
                ExplicitReturnType = returnType,
                IsOverrideFlag = isOverride,
                IsVirtualFlag = isOverride,
                IsSealedFlag = isOverride
            };
            method.ExplicitParameters = [.. parameters.Select((p, index) =>
                new HS.ParameterSymbol(p.Name, p.Type, index, method))];
            owner.AddMember(method);
            methods.Add(method);
            return method;
        }

        public void Execute(Action<Type> assertions)
        {
            var assemblyName = "SynthesizedBodyExecution_" + Guid.NewGuid().ToString("N");
            var md = metadata.Metadata;
            _ = md.AddModule(0, md.GetOrAddString(assemblyName + ".dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
            _ = md.AddAssembly(md.GetOrAddString(assemblyName), new Version(1, 0, 0, 0),
                default, default, 0, AssemblyHashAlgorithm.None);
            _ = md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            var type = md.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed,
                default, md.GetOrAddString(owner.Name), metadata.GetTypeEntity(owner.BaseType!),
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            metadata.RegisterTypeDefinition(owner, type);
            for (var i = 0; i < properties.Count; i++)
            {
                metadata.RegisterField(properties[i].BackingField!, MetadataTokens.FieldDefinitionHandle(i + 1));
            }

            for (var i = 0; i < methods.Count; i++)
            {
                metadata.RegisterMethod(methods[i], MetadataTokens.MethodDefinitionHandle(i + 1));
            }

            for (var i = 0; i < properties.Count; i++)
            {
                metadata.RegisterProperty(properties[i], MetadataTokens.PropertyDefinitionHandle(i + 1));
            }

            foreach (var property in properties)
            {
                HS.FieldSymbol field = property.BackingField!;
                _ = md.AddFieldDefinition(FieldAttributes.Private, md.GetOrAddString(field.Name),
                    metadata.EncodeFieldSignature(field.Type));
            }

            BlobBuilder methodBodies = new();
            MethodBodyStreamEncoder bodies = new(methodBodies);
            var parameterRow = 1;
            foreach (var method in methods)
            {
                H.ILBuilder il = new();
                new H.SynthesizedBodyEmitter<string>(metadata, method, il, GetSpecialType,
                    name => references.FindType(name)).Emit();
                StandaloneSignatureHandle locals = default;
                if (il.Locals.Count > 0)
                {
                    BlobBuilder signature = new();
                    var encoder = new BlobEncoder(signature).LocalVariableSignature(il.Locals.Count);
                    foreach (var slot in il.Locals)
                    {
                        metadata.EncodeType(encoder.AddVariable().Type(), slot.Type);
                    }

                    locals = md.AddStandaloneSignature(md.GetOrAddBlob(signature));
                }
                var offset = bodies.AddMethodBody(il.Encoder, il.MaxStack, locals, MethodBodyAttributes.InitLocals);
                var firstParameter = MetadataTokens.ParameterHandle(parameterRow);
                foreach (var parameter in method.Parameters)
                {
                    _ = md.AddParameter(ParameterAttributes.None, md.GetOrAddString(parameter.Name), parameter.Ordinal + 1);
                    parameterRow++;
                }
                var attributes = MethodAttributes.Public | MethodAttributes.HideBySig;
                if (method.MethodKind == HS.MethodKind.Constructor)
                {
                    attributes |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                }

                if (method.MethodKind is HS.MethodKind.PropertyGet or HS.MethodKind.PropertySet)
                {
                    attributes |= MethodAttributes.SpecialName;
                }

                if (method.IsOverride)
                {
                    attributes |= MethodAttributes.Virtual | MethodAttributes.Final;
                }

                _ = md.AddMethodDefinition(attributes, MethodImplAttributes.IL | MethodImplAttributes.Managed,
                    md.GetOrAddString(method.MetadataName), metadata.EncodeMethodSignature(method), offset, firstParameter);
            }

            md.AddPropertyMap(type, MetadataTokens.PropertyDefinitionHandle(1));
            foreach (var property in properties)
            {
                var handle = md.AddProperty(PropertyAttributes.None,
                    md.GetOrAddString(property.Name), metadata.EncodePropertySignature(property));
                md.AddMethodSemantics(handle, MethodSemanticsAttributes.Getter,
                    MetadataTokens.MethodDefinitionHandle(methods.IndexOf(property.Getter!) + 1));
                md.AddMethodSemantics(handle, MethodSemanticsAttributes.Setter,
                    MetadataTokens.MethodDefinitionHandle(methods.IndexOf(property.Setter!) + 1));
            }
            ManagedPEBuilder pe = new(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(md),
                methodBodies, flags: CorFlags.ILOnly);
            BlobBuilder image = new();
            _ = pe.Serialize(image);
            var bytes = image.ToArray();
            Assert.IsEmpty(ILVerifier.Verify(assemblyName, bytes));

            AssemblyLoadContext context = new(assemblyName, isCollectible: true);
            try
            {
                using MemoryStream stream = new(bytes);
                var assembly = context.LoadFromStream(stream);
                assertions(assembly.GetType(owner.Name, throwOnError: true)!);
            }
            finally
            {
                context.Unload();
            }
        }

        public override HS.NamedTypeSymbol GetSpecialType(HS.SpecialType type)
        {
            return references.GetSpecialType(type)!;
        }

        public override bool IsSourceNamedType(HS.NamedTypeSymbol type)
        {
            return ReferenceEquals(type, owner);
        }

        public override string GetFilePath(string file)
        {
            return file;
        }

        public override HD.DiagnosticBag DeclarationDiagnostics { get; } = new();

        // All signatures are explicit; any accidental request for binding must fail the test.
        public override HX.SyntaxTree GetTree(string file)
        {
            throw new NotSupportedException();
        }

        public override ImmutableArray<HS.ParameterSymbol> BindParameters(string file,
                    HSS.SourceMethodSymbol<string> method, HX.ParameterListSyntax syntax)
        {
            throw new NotSupportedException();
        }

        public override HS.TypeSymbol BindType(string file, HS.Symbol owner, HX.TypeSyntax syntax,
                    bool allowVoid)
        {
            throw new NotSupportedException();
        }

        public override HS.TypeSymbol AdjustDeclaredReturnType(string file, HSS.SourceMethodSymbol<string> method,
                    HS.TypeSymbol type, HX.TypeSyntax syntax)
        {
            throw new NotSupportedException();
        }

        public override HS.TypeParameterConstraints BindConstraints(string file, HS.Symbol owner,
                    HX.TypeParameterSyntax syntax, HS.TypeParameterSymbol parameter)
        {
            throw new NotSupportedException();
        }

        public override HB.BoundBlock? BindMethodBody(string file,
                    HSS.SourceMethodSymbol<string> method)
        {
            throw new NotSupportedException();
        }
    }
}
