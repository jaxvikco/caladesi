extern alias SelfHost;

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Caladesi.Compiler.Tests.Emit;
using H = SelfHost::Caladesi.Compiler.Emit;
using HB = SelfHost::Caladesi.Compiler.Bound;
using HM = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using HS = SelfHost::Caladesi.Compiler.Symbols;
using HSS = SelfHost::Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class MethodBodyExecutionTests
{
    [TestMethod]
    [DataRow(-4, 7)]
    [DataRow(0, 3)]
    [DataRow(7, 17)]
    public void ArithmeticAndConditional_ExecuteBothBranches(int input, int expected)
    {
        Fixture f = new();
        HB.BoundConditional value = new(
            f.Binary(HB.BinaryOperatorKind.GreaterThanOrEqual, f.Argument, f.Integer(0), comparison: true),
            f.Binary(HB.BinaryOperatorKind.Add,
                f.Binary(HB.BinaryOperatorKind.Multiply, f.Argument, f.Integer(2)), f.Integer(3)),
            f.Binary(HB.BinaryOperatorKind.Subtract, f.Integer(3), f.Argument),
            f.Int32);

        Assert.AreEqual(expected, f.Execute(new([], [new HB.BoundReturn(value)]), input));
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(6, 21)]
    public void WhileAndAssignments_ExecuteBackwardEdgesAndLocalStores(int input, int expected)
    {
        Fixture f = new();
        HS.LocalSymbol index = new("index", f.Int32, false, f.Method);
        HS.LocalSymbol sum = new("sum", f.Int32, false, f.Method);
        HB.BoundBlock loopBody = new([], [
            Assign(sum, f.Binary(HB.BinaryOperatorKind.Add, new HB.BoundLocal(sum), new HB.BoundLocal(index))),
            Assign(index, f.Binary(HB.BinaryOperatorKind.Add, new HB.BoundLocal(index), f.Integer(1)))
        ]);
        HB.BoundBlock body = new([index, sum], [
            new HB.BoundLocalDeclaration(index, f.Integer(1)),
            new HB.BoundLocalDeclaration(sum, f.Integer(0)),
            new HB.BoundWhile(
                f.Binary(HB.BinaryOperatorKind.LessThanOrEqual, new HB.BoundLocal(index), f.Argument, comparison: true),
                loopBody, new HS.LabelSymbol("break"), new HS.LabelSymbol("continue")),
            new HB.BoundReturn(new HB.BoundLocal(sum))
        ]);

        Assert.AreEqual(expected, f.Execute(body, input));
    }

    [TestMethod]
    [DataRow(true, 12, 12)]
    [DataRow(false, 12, 99)]
    public void Finally_ExecutesAndPreservesAnAlreadyEvaluatedReturn(bool returnInsideTry, int input, int expected)
    {
        Fixture f = new();
        HS.LocalSymbol local = new("value", f.Int32, false, f.Method);
        HB.BoundBlock protectedBody = returnInsideTry
            ? new([], [new HB.BoundReturn(new HB.BoundLocal(local))])
            : new([], [new HB.BoundNoOp()]);
        HB.BoundBlock body = new([local], [
            new HB.BoundLocalDeclaration(local, f.Argument),
            new HB.BoundTry(protectedBody, [], new([], [Assign(local, f.Integer(99))])),
            new HB.BoundReturn(new HB.BoundLocal(local))
        ]);

        Assert.AreEqual(expected, f.Execute(body, input));
    }

    [TestMethod]
    [DataRow(-1, 255)]
    [DataRow(256, 0)]
    [DataRow(511, 255)]
    public void NumericAndBoxingConversions_ExecuteWithRealTypeTokens(int input, int expected)
    {
        Fixture f = new();
        HS.NamedTypeSymbol byteType = f.Special(HS.SpecialType.Byte);
        HB.BoundConversion narrowed = new(f.Argument, HB.ConversionKind.ExplicitNumeric, byteType);
        HB.BoundConversion boxed = new(narrowed, HB.ConversionKind.Boxing, f.Special(HS.SpecialType.Object));
        HB.BoundConversion unboxed = new(boxed, HB.ConversionKind.Unboxing, byteType);
        HB.BoundConversion result = new(unboxed, HB.ConversionKind.ImplicitNumeric, f.Int32);

        Assert.AreEqual(expected, f.Execute(new([], [new HB.BoundReturn(result)]), input));
    }

    private static HB.BoundExpressionStatement Assign(HS.LocalSymbol local, HB.BoundExpression value) =>
        new(new HB.BoundAssignment(new HB.BoundLocal(local), value) { IsDiscarded = true });

    private sealed class Fixture
    {
        private static readonly Lazy<HM.ReferenceSet> s_references = new(() => HM.ReferenceSet.Create(TestReferences.Paths));
        private readonly HM.ReferenceSet references = s_references.Value;
        private readonly H.MetadataEmitter metadata;
        private readonly HSS.SynthesizedNamedType owner = new("RuntimeMethods", null, HS.TypeKind.Class);

        public HS.NamedTypeSymbol Int32 => Special(HS.SpecialType.Int32);
        public HSS.SynthesizedMethodSymbol Method { get; }
        public HB.BoundParameter Argument { get; }

        public Fixture()
        {
            metadata = new(Special, name => references.FindType(name));
            Method = new(owner, "Evaluate", HS.MethodKind.Ordinary, HS.Accessibility.Public, true, Int32, []);
            HS.ParameterSymbol parameter = new("value", Int32, 0, Method);
            Method.SetParameters([parameter]);
            Argument = new(parameter);
        }

        public HS.NamedTypeSymbol Special(HS.SpecialType special) => references.GetSpecialType(special)!;

        public HB.BoundLiteral Integer(int value) => new(new HS.ConstantValue(value), Int32);

        public HB.BoundBinary Binary(HB.BinaryOperatorKind kind, HB.BoundExpression left,
            HB.BoundExpression right, bool comparison = false) =>
            new(kind, left, right, comparison ? Special(HS.SpecialType.Boolean) : Int32, Int32);

        public int Execute(HB.BoundBlock body, int input)
        {
            string assemblyName = "MethodBodyExecution_" + Guid.NewGuid().ToString("N");
            MetadataBuilder md = metadata.Metadata;
            md.AddModule(0, md.GetOrAddString(assemblyName + ".dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
            md.AddAssembly(md.GetOrAddString(assemblyName), new Version(1, 0, 0, 0),
                default, default, 0, AssemblyHashAlgorithm.None);
            md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            TypeDefinitionHandle type = md.AddTypeDefinition(
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
                default, md.GetOrAddString(owner.Name), metadata.GetTypeEntity(Special(HS.SpecialType.Object)),
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            metadata.RegisterTypeDefinition(owner, type);
            metadata.RegisterMethod(Method, MetadataTokens.MethodDefinitionHandle(1));

            H.ILBuilder il = new();
            new H.MethodBodyEmitter(metadata, Method, il, Special, name => references.FindType(name), null).EmitBody(body);
            StandaloneSignatureHandle locals = default;
            if (il.Locals.Count > 0)
            {
                BlobBuilder signature = new();
                LocalVariablesEncoder encoder = new BlobEncoder(signature).LocalVariableSignature(il.Locals.Count);
                foreach (H.LocalSlot slot in il.Locals)
                    metadata.EncodeType(encoder.AddVariable().Type(), slot.Type);
                locals = md.AddStandaloneSignature(md.GetOrAddBlob(signature));
            }

            BlobBuilder methodBodies = new();
            int offset = new MethodBodyStreamEncoder(methodBodies).AddMethodBody(
                il.Encoder, il.MaxStack, locals, MethodBodyAttributes.InitLocals);
            ParameterHandle parameter = md.AddParameter(ParameterAttributes.None, md.GetOrAddString("value"), 1);
            md.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodImplAttributes.IL | MethodImplAttributes.Managed, md.GetOrAddString(Method.Name),
                metadata.EncodeMethodSignature(Method), offset, parameter);
            ManagedPEBuilder pe = new(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(md),
                methodBodies, flags: CorFlags.ILOnly);
            BlobBuilder image = new();
            pe.Serialize(image);
            byte[] bytes = image.ToArray();
            Assert.IsEmpty(ILVerifier.Verify(assemblyName, bytes));

            AssemblyLoadContext context = new(assemblyName, isCollectible: true);
            try
            {
                using MemoryStream stream = new(bytes);
                Assembly assembly = context.LoadFromStream(stream);
                Func<int, int> evaluate = assembly.GetType(owner.Name, throwOnError: true)!
                    .GetMethod(Method.Name)!.CreateDelegate<Func<int, int>>();
                return evaluate(input);
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
