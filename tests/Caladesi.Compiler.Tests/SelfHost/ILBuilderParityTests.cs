extern alias SelfHost;

using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using S = Caladesi.Compiler.Symbols;
using H = SelfHost::Caladesi.Compiler.Emit;
using HS = SelfHost::Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class ILBuilderParityTests
{
    [TestMethod]
    public void ConstantsTokensAndCalls_EmitIdenticalBytesAndTrackDepth()
    {
        Pair pair = new();
        foreach (int value in new[] { int.MinValue, -129, -128, -2, -1, 0, 1, 8, 9, 127, 128, int.MaxValue })
            pair.Run("LdcI4", value);
        foreach (long value in new[] { long.MinValue, -1L, 0L, long.MaxValue })
            pair.Run("LdcI8", value);
        foreach (float value in new[] { float.MinValue, -0.0f, 0.0f, float.MaxValue, float.NaN, float.PositiveInfinity })
            pair.Run("LdcR4", value);
        foreach (double value in new[] { double.MinValue, -0.0, 0.0, double.MaxValue, double.NaN, double.NegativeInfinity })
            pair.Run("LdcR8", value);
        pair.Run("LdStr", MetadataTokens.UserStringHandle(17));
        pair.Run("Token", ILOpCode.Ldtoken, (EntityHandle)MetadataTokens.TypeReferenceHandle(3), 1);
        pair.Run("Token", ILOpCode.Ldfld, (EntityHandle)MetadataTokens.FieldDefinitionHandle(4), -2);
        pair.Run("Call", ILOpCode.Call, (EntityHandle)MetadataTokens.MethodDefinitionHandle(7), 3, 1);
        pair.Run("Call", ILOpCode.Callvirt, (EntityHandle)MetadataTokens.MemberReferenceHandle(9), -3, -1);
        pair.Run("Call", ILOpCode.Newobj, (EntityHandle)MetadataTokens.MemberReferenceHandle(10), 1000, 1);
        pair.Run("Op", ILOpCode.Nop, 0);
        pair.Run("Op", ILOpCode.Dup, 2);
        foreach (ILOpCode op in new[] { ILOpCode.Br, ILOpCode.Throw, ILOpCode.Rethrow, ILOpCode.Leave,
                     ILOpCode.Endfinally, ILOpCode.Endfilter, ILOpCode.Ret })
        {
            pair.Run("ResetDepth", 5);
            pair.Run("Op", op, -1);
        }
        pair.Run("ResetDepth", -7);
        pair.Run("Op", ILOpCode.Pop, -1);
        Assert.AreEqual(0, pair.Actual.CurrentDepth);
        pair.Run("ResetDepth", 100);
        pair.Run("ResetDepth", 0);
        Assert.AreEqual(104, pair.Actual.MaxStack);
        pair.Run("ResetDepth", int.MaxValue);
        pair.Run("Op", ILOpCode.Nop, 1);
        Assert.AreEqual(0, pair.Actual.CurrentDepth);
        pair.Run("Call", ILOpCode.Call, (EntityHandle)MetadataTokens.MethodDefinitionHandle(1), int.MinValue, -1);
    }

    [TestMethod]
    public void ArgumentsAndLocals_PreserveCompactWideAndUncheckedAddressPaths()
    {
        foreach (string method in new[] { "Ldloc", "Stloc", "Ldarg", "Starg", "Ldloca", "Ldarga" })
        {
            Pair pair = new();
            foreach (int index in new[] { -65537, -257, -1, 0, 1, 2, 3, 4, 255, 256, 65535, 65536, int.MaxValue })
                pair.Run(method, index);
        }
    }

    [TestMethod]
    public void Labels_PreserveMergesFallthroughBackwardEdgesAndBranchBodyFixups()
    {
        Pair pair = new();
        LabelHandle target = (LabelHandle)pair.Run("NewLabel")!;
        LabelHandle backward = (LabelHandle)pair.Run("NewLabel")!;
        Assert.AreNotEqual(target, backward);
        pair.Run("Mark", backward);
        pair.Run("ResetDepth", 7);
        pair.Run("Branch", ILOpCode.Brtrue, target);
        pair.Run("ResetDepth", 3);
        pair.Run("Branch", ILOpCode.Brfalse, target);
        pair.Run("ResetDepth", 1);
        pair.Run("Mark", target);
        Assert.AreEqual(6, pair.Actual.CurrentDepth);
        pair.Run("Branch", ILOpCode.Beq, backward);
        pair.Run("Branch", ILOpCode.Br, backward);
        Assert.AreEqual(0, pair.Actual.CurrentDepth);
        CollectionAssert.AreEqual(Body(pair.Expected), Body(pair.Actual));

        foreach (ILOpCode op in new[] { ILOpCode.Br, ILOpCode.Leave, ILOpCode.Brtrue, ILOpCode.Brfalse,
                     ILOpCode.Beq, ILOpCode.Bne_un, ILOpCode.Bge, ILOpCode.Bge_un, ILOpCode.Bgt,
                     ILOpCode.Bgt_un, ILOpCode.Ble, ILOpCode.Ble_un, ILOpCode.Blt, ILOpCode.Blt_un,
                     ILOpCode.Br_s, ILOpCode.Leave_s, ILOpCode.Brtrue_s, ILOpCode.Brfalse_s, ILOpCode.Beq_s })
        {
            Pair branch = new();
            LabelHandle label = (LabelHandle)branch.Run("NewLabel")!;
            branch.Run("ResetDepth", 5);
            branch.Run("Branch", op, label);
            branch.Run("ResetDepth", 9);
            branch.Run("Mark", label);
            Assert.AreEqual(9, branch.Actual.CurrentDepth);
            CollectionAssert.AreEqual(Body(branch.Expected), Body(branch.Actual), op.ToString());
        }
    }

    [TestMethod]
    public void Labels_InvalidOperationsPreserveExceptionsAndMutationOrdering()
    {
        Pair pair = new();
        pair.Run("ResetDepth", 5);
        pair.Run("Branch", ILOpCode.Nop, default(LabelHandle));
        pair.Run("Branch", ILOpCode.Brtrue, default(LabelHandle));
        pair.Run("Mark", default(LabelHandle));
        LabelHandle label = (LabelHandle)pair.Run("NewLabel")!;
        pair.Run("Mark", label);
        pair.Run("Mark", label);
    }

    [TestMethod]
    public void LocalsAndSequencePoints_PreserveIdentityOffsetsAndHiddenSentinel()
    {
        Pair pair = new();
        Assert.AreEqual(4, pair.Actual.MaxStack);
        Assert.AreSame(pair.Actual.Code, pair.Actual.Encoder.CodeBuilder);
        Assert.AreSame(pair.Actual.ControlFlow, pair.Actual.Encoder.ControlFlowBuilder);
        Assert.IsTrue(typeof(H.SequencePoint).IsValueType);
        Assert.IsTrue(typeof(H.SequencePoint).IsDefined(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute)));
        Assert.IsTrue(typeof(H.LocalSlot).IsSealed);
        Assert.IsTrue(typeof(H.ILBuilder).IsSealed);
        foreach (string? name in new[] { null, "", "local" })
        {
            object expected = Invoke(pair.Expected, "NewLocal", S.NullTypeSymbol.Instance, name)!;
            H.LocalSlot actual = name == null
                ? pair.Actual.NewLocal(HS.NullTypeSymbol.Instance)
                : pair.Actual.NewLocal(HS.NullTypeSymbol.Instance, name);
            Assert.AreSame(HS.NullTypeSymbol.Instance, actual.Type);
            Assert.AreSame(pair.Actual.Locals[actual.Index], actual);
            Assert.AreEqual(Get<int>(expected, "Index"), actual.Index);
            Assert.AreEqual(Get<string?>(expected, "Name"), actual.Name);
        }
        pair.Check();
        pair.Run("MarkHidden");
        pair.Run("MarkSequencePoint", 1, 2, 3, 4);
        pair.Run("LdcI4", 128);
        pair.Run("MarkSequencePoint", -1, 0, -2, int.MaxValue);
        pair.Run("MarkHidden");
        Assert.AreEqual(0xFEEFEE, pair.Actual.SequencePoints[^1].StartLine);
        Assert.AreEqual(pair.Actual.Offset, pair.Actual.SequencePoints[^1].Offset);
        pair.Actual.Encoder.LoadConstantI4(7);
        Get<InstructionEncoder>(pair.Expected, "Encoder").LoadConstantI4(7);
        pair.Check();
        Assert.AreEqual(1, pair.Actual.CurrentDepth);
        Assert.AreEqual(0, typeof(H.ILBuilder).GetMethod("ResetDepth")!.GetParameters()[0].DefaultValue);
        Assert.IsNotNull(typeof(H.ILBuilder).GetMethod("NewLocal", [typeof(HS.TypeSymbol)]));
    }

    private static byte[] Body(object builder)
    {
        BlobBuilder bytes = new();
        MethodBodyStreamEncoder bodies = new(bytes);
        bodies.AddMethodBody(Get<InstructionEncoder>(builder, "Encoder"), Get<int>(builder, "MaxStack"));
        return bytes.ToArray();
    }

    private static T Get<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;

    private static object? Invoke(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name)!.Invoke(instance, arguments);

    private sealed class Pair
    {
        public object Expected { get; } = Activator.CreateInstance(
            typeof(Caladesi.Compiler.Compilation).Assembly.GetType("Caladesi.Compiler.Emit.ILBuilder", true)!)!;
        public H.ILBuilder Actual { get; } = new();

        public object? Run(string method, params object?[] arguments)
        {
            object? expected = null;
            object? actual = null;
            Exception? left = null;
            Exception? right = null;
            try { expected = Invoke(Expected, method, arguments); }
            catch (TargetInvocationException error) { left = error.InnerException; }
            try { actual = Invoke(Actual, method, arguments); }
            catch (TargetInvocationException error) { right = error.InnerException; }
            Assert.AreEqual(left?.GetType(), right?.GetType(), method);
            Assert.AreEqual(left?.Message, right?.Message, method);
            Assert.AreEqual(expected, actual, method);
            Check();
            return actual;
        }

        public void Check()
        {
            CollectionAssert.AreEqual(Get<BlobBuilder>(Expected, "Code").ToArray(), Actual.Code.ToArray());
            Assert.AreEqual(Get<int>(Expected, "Offset"), Actual.Offset);
            Assert.AreEqual(Get<int>(Expected, "CurrentDepth"), Actual.CurrentDepth);
            Assert.AreEqual(Get<int>(Expected, "MaxStack"), Actual.MaxStack);
            Assert.HasCount(Get<IList>(Expected, "Locals").Count, Actual.Locals);
            IList points = Get<IList>(Expected, "SequencePoints");
            Assert.HasCount(points.Count, Actual.SequencePoints);
            for (int i = 0; i < points.Count; i++)
                foreach (string property in new[] { "Offset", "StartLine", "StartColumn", "EndLine", "EndColumn", "IsHidden" })
                    Assert.AreEqual(Get<object>(points[i]!, property), Get<object>(Actual.SequencePoints[i], property), property);
        }
    }
}
