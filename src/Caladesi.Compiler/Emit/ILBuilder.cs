using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Emit;

internal readonly record struct SequencePoint(int Offset, int StartLine, int StartColumn, int EndLine, int EndColumn, bool IsHidden);

internal sealed class LocalSlot(TypeSymbol type, string? name, int index)
{
    public TypeSymbol Type { get; } = type;
    public string? Name { get; } = name;
    public int Index { get; } = index;
}

/// <summary>Thin wrapper over <see cref="InstructionEncoder"/> that tracks stack depth, locals and sequence points.</summary>
internal sealed class ILBuilder
{
    private readonly Dictionary<LabelHandle, int> _labelDepth = new();
    private int _depth;
    private int _max;

    public ILBuilder()
    {
        Code = new BlobBuilder();
        ControlFlow = new ControlFlowBuilder();
        Encoder = new InstructionEncoder(Code, ControlFlow);
    }

    public BlobBuilder Code { get; }
    public ControlFlowBuilder ControlFlow { get; }
    public InstructionEncoder Encoder { get; }
    public List<LocalSlot> Locals { get; } = new();
    public List<SequencePoint> SequencePoints { get; } = new();
    public int MaxStack => _max + 4;
    public int Offset => Encoder.Offset;
    public int CurrentDepth => _depth;

    public LocalSlot NewLocal(TypeSymbol type, string? name = null)
    {
        var l = new LocalSlot(type, name, Locals.Count);
        Locals.Add(l);
        return l;
    }

    private void Adjust(int delta)
    {
        _depth += delta;
        if (_depth < 0) _depth = 0;
        if (_depth > _max) _max = _depth;
    }

    public void Op(ILOpCode op, int delta)
    {
        Encoder.OpCode(op);
        Adjust(delta);
        if (op is ILOpCode.Br or ILOpCode.Ret or ILOpCode.Throw or ILOpCode.Rethrow or ILOpCode.Leave or ILOpCode.Endfinally or ILOpCode.Endfilter)
        {
            if (op == ILOpCode.Ret) _depth = 0;
        }
    }

    public void Token(ILOpCode op, EntityHandle handle, int delta)
    {
        Encoder.OpCode(op);
        Encoder.Token(handle);
        Adjust(delta);
    }

    public void Call(ILOpCode op, EntityHandle method, int pops, int pushes)
    {
        Encoder.OpCode(op);
        Encoder.Token(method);
        Adjust(-pops + pushes);
    }

    public void LdcI4(int v)
    {
        Encoder.LoadConstantI4(v);
        Adjust(1);
    }

    public void LdcI8(long v)
    {
        Encoder.LoadConstantI8(v);
        Adjust(1);
    }

    public void LdcR4(float v)
    {
        Encoder.LoadConstantR4(v);
        Adjust(1);
    }

    public void LdcR8(double v)
    {
        Encoder.LoadConstantR8(v);
        Adjust(1);
    }

    public void LdStr(UserStringHandle h)
    {
        Encoder.LoadString(h);
        Adjust(1);
    }

    public void Ldloc(int i) { Encoder.LoadLocal(i); Adjust(1); }
    public void Ldloca(int i)
    {
        if (i < 256) { Encoder.OpCode(ILOpCode.Ldloca_s); Encoder.CodeBuilder.WriteByte((byte)i); }
        else { Encoder.OpCode(ILOpCode.Ldloca); Encoder.CodeBuilder.WriteUInt16((ushort)i); }
        Adjust(1);
    }
    public void Stloc(int i) { Encoder.StoreLocal(i); Adjust(-1); }
    public void Ldarg(int i) { Encoder.LoadArgument(i); Adjust(1); }
    public void Ldarga(int i)
    {
        if (i < 256) { Encoder.OpCode(ILOpCode.Ldarga_s); Encoder.CodeBuilder.WriteByte((byte)i); }
        else { Encoder.OpCode(ILOpCode.Ldarga); Encoder.CodeBuilder.WriteUInt16((ushort)i); }
        Adjust(1);
    }
    public void Starg(int i) { Encoder.StoreArgument(i); Adjust(-1); }

    public LabelHandle NewLabel() => Encoder.DefineLabel();

    public void Mark(LabelHandle l)
    {
        Encoder.MarkLabel(l);
        if (_labelDepth.TryGetValue(l, out var d) && d > _depth) _depth = d;
    }

    public void Branch(ILOpCode op, LabelHandle target)
    {
        var pops = op switch
        {
            ILOpCode.Br or ILOpCode.Leave => 0,
            ILOpCode.Brtrue or ILOpCode.Brfalse => 1,
            _ => 2,
        };
        Adjust(-pops);
        if (!_labelDepth.TryGetValue(target, out var d) || d < _depth) _labelDepth[target] = _depth;
        Encoder.Branch(op, target);
        if (op is ILOpCode.Br or ILOpCode.Leave) _depth = 0;
    }

    public void ResetDepth(int depth = 0) { _depth = depth; if (_depth > _max) _max = _depth; }

    public void MarkSequencePoint(int startLine, int startCol, int endLine, int endCol) =>
        SequencePoints.Add(new SequencePoint(Offset, startLine, startCol, endLine, endCol, false));

    public void MarkHidden() => SequencePoints.Add(new SequencePoint(Offset, 0xFEEFEE, 0, 0xFEEFEE, 0, true));
}
