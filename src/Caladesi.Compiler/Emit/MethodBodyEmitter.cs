using System.Collections.Immutable;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Emit;

/// <summary>Generates IL for one lowered method body.</summary>
internal sealed partial class MethodBodyEmitter
{
    private readonly EmitModule _m;
    private readonly Compilation _c;
    private readonly MethodSymbol _method;
    private readonly ILBuilder _il;
    private readonly SourceFile? _file;
    private readonly Dictionary<LocalSymbol, LocalSlot> _locals = new();
    private readonly Dictionary<ParameterSymbol, int> _paramIndex = new();
    private readonly Dictionary<LabelSymbol, LabelHandle> _labels = new();
    private readonly Dictionary<LabelSymbol, int> _labelTry = new();
    private readonly Dictionary<int, int> _tryParent = new();
    private readonly Dictionary<BoundConditionalReceiver, (LocalSlot slot, TypeSymbol receiverType, bool isNullableValue)> _condReceivers = new();
    private readonly Stack<int> _tryStack = new();
    private int _nextTryId = 1;
    private readonly record struct TryIds(int Outer, int Body, int[] Catches, int Finally);
    private readonly Dictionary<BoundTry, TryIds> _tryIds = new();
    private int _inFinally;
    private LocalSlot? _returnSlot;
    private LabelHandle _returnLabel;
    private bool _returnLabelUsed;
    private readonly bool _isStatic;
    private readonly bool _hasReturnValue;
    private readonly bool _thisIsByRef;

    public MethodBodyEmitter(EmitModule module, MethodSymbol method, ILBuilder il, SourceFile? file)
    {
        _m = module;
        _c = module.Compilation;
        _method = method;
        _il = il;
        _file = file;
        _isStatic = method.IsStatic;
        _hasReturnValue = !method.ReturnType.IsVoid && method.MethodKind != MethodKind.Constructor && method.MethodKind != MethodKind.StaticConstructor;
        _thisIsByRef = method.ContainingType?.IsValueType == true;
        var offset = _isStatic ? 0 : 1;
        foreach (var p in method.Parameters) _paramIndex[p] = p.Ordinal + offset;
    }

    // ------------------------------------------------------------------ entry

    public void EmitBody(BoundBlock body)
    {
        PreScanLabels(body, 0);
        _returnLabel = _il.NewLabel();
        EmitStatement(body);
        // implicit return at the end of the body
        if (_hasReturnValue)
        {
            // unreachable fall-through (the binder reported missing returns); keep the IL valid
            EmitDefault(_method.ReturnType);
        }
        _il.Op(ILOpCode.Ret, _hasReturnValue ? -1 : 0);
        if (_returnLabelUsed)
        {
            _il.Mark(_returnLabel);
            if (_returnSlot != null) _il.Ldloc(_returnSlot.Index);
            _il.Op(ILOpCode.Ret, _returnSlot != null ? -1 : 0);
        }
    }

    // ------------------------------------------------------------------ labels & try regions

    private void PreScanLabels(BoundNode node, int tryId)
    {
        switch (node)
        {
            case BoundLabel l:
                _labelTry[l.Label] = tryId;
                break;
            case BoundBlock b:
                foreach (var s in b.Statements) PreScanLabels(s, tryId);
                break;
            case BoundStatementList sl:
                foreach (var s in sl.Statements) PreScanLabels(s, tryId);
                break;
            case BoundIf i:
                PreScanLabels(i.Consequence, tryId);
                if (i.Alternative != null) PreScanLabels(i.Alternative, tryId);
                break;
            case BoundWhile w:
                _labelTry[w.BreakLabel] = tryId; _labelTry[w.ContinueLabel] = tryId;
                PreScanLabels(w.Body, tryId);
                break;
            case BoundDoWhile dw:
                _labelTry[dw.BreakLabel] = tryId; _labelTry[dw.ContinueLabel] = tryId;
                PreScanLabels(dw.Body, tryId);
                break;
            case BoundFor f:
                _labelTry[f.BreakLabel] = tryId; _labelTry[f.ContinueLabel] = tryId;
                if (f.Initializer != null) PreScanLabels(f.Initializer, tryId);
                if (f.Increment != null) PreScanLabels(f.Increment, tryId);
                PreScanLabels(f.Body, tryId);
                break;
            case BoundTry t:
            {
                var ids = new TryIds(t.Finally != null ? _nextTryId++ : 0, _nextTryId++, t.Catches.Select(_ => _nextTryId++).ToArray(), t.Finally != null ? _nextTryId++ : 0);
                _tryIds[t] = ids;
                var bodyParent = ids.Outer != 0 ? ids.Outer : tryId;
                if (ids.Outer != 0) _tryParent[ids.Outer] = tryId;
                _tryParent[ids.Body] = bodyParent;
                foreach (var cid in ids.Catches) _tryParent[cid] = bodyParent;
                if (ids.Finally != 0) _tryParent[ids.Finally] = tryId;
                PreScanLabels(t.Body, ids.Body);
                for (var i = 0; i < t.Catches.Length; i++) PreScanLabels(t.Catches[i].Body, ids.Catches[i]);
                if (t.Finally != null) PreScanLabels(t.Finally, ids.Finally);
                break;
            }
            case BoundSequencePoint sp:
                PreScanLabels(sp.Statement, tryId);
                break;
        }
    }

    private LabelHandle GetLabel(LabelSymbol l)
    {
        if (!_labels.TryGetValue(l, out var h)) _labels[l] = h = _il.NewLabel();
        return h;
    }

    private int CurrentTry => _tryStack.Count == 0 ? 0 : _tryStack.Peek();

    // ------------------------------------------------------------------ locals

    private LocalSlot GetLocal(LocalSymbol l)
    {
        if (!_locals.TryGetValue(l, out var slot))
        {
            slot = _il.NewLocal(_m.ClrType(l.Type), l.IsSynthesized ? null : l.Name);
            _locals[l] = slot;
        }
        return slot;
    }

    private LocalSlot TempLocal(TypeSymbol type) => _il.NewLocal(_m.ClrType(type));

    // ------------------------------------------------------------------ statements

    private void EmitStatement(BoundStatement s)
    {
        switch (s)
        {
            case BoundBlock b:
                foreach (var l in b.Locals) GetLocal(l);
                foreach (var st in b.Statements) EmitStatement(st);
                break;
            case BoundStatementList sl:
                foreach (var st in sl.Statements) EmitStatement(st);
                break;
            case BoundNoOp:
                break;
            case BoundSequencePoint sp:
                MarkSequencePoint(sp.SyntaxNodeForSpan);
                EmitStatement(sp.Statement);
                break;
            case BoundExpressionStatement es:
                MarkSequencePoint(es.Syntax);
                EmitExpression(es.Expression, used: false);
                break;
            case BoundLocalDeclaration ld:
                MarkSequencePoint(ld.Syntax);
                if (ld.Initializer != null)
                {
                    EmitExpression(ld.Initializer, true);
                    _il.Stloc(GetLocal(ld.Local).Index);
                }
                break;
            case BoundIf i: EmitIf(i); break;
            case BoundWhile w: EmitWhile(w); break;
            case BoundDoWhile dw: EmitDoWhile(dw); break;
            case BoundFor f: EmitFor(f); break;
            case BoundGoto g: EmitGoto(g.Label); break;
            case BoundConditionalGoto cg:
                if (RequiresLeave(cg.Label))
                {
                    var skip = _il.NewLabel();
                    EmitBranchIf(cg.Condition, skip, !cg.JumpIfTrue);
                    _il.Branch(ILOpCode.Leave, GetLabel(cg.Label));
                    _il.Mark(skip);
                }
                else EmitBranchIf(cg.Condition, GetLabel(cg.Label), cg.JumpIfTrue);
                break;
            case BoundLabel l:
                _il.Mark(GetLabel(l.Label));
                break;
            case BoundReturn r: EmitReturn(r); break;
            case BoundThrow t:
                MarkSequencePoint(t.Syntax);
                if (t.Expression == null) _il.Op(ILOpCode.Rethrow, 0);
                else { EmitExpression(t.Expression, true); _il.Op(ILOpCode.Throw, -1); }
                break;
            case BoundTry t: EmitTry(t); break;
            case BoundYieldReturn or BoundYieldBreak:
                throw new InvalidOperationException("yield statements must be lowered before emit");
            default:
                throw new InvalidOperationException("Unhandled statement " + s.GetType().Name);
        }
    }

    private void MarkSequencePoint(SyntaxNode? node)
    {
        if (node == null || _file == null) return;
        SyntaxNode n = node;
        // use the statement-like span (exclude trailing trivia)
        var span = n.Span;
        if (span.Length == 0) return;
        var lp = _file.Tree.Text.GetLinePositionSpan(span);
        _il.MarkSequencePoint(lp.Start.Line + 1, lp.Start.Character + 1, lp.End.Line + 1, lp.End.Character + 1);
    }

    private void EmitGoto(LabelSymbol label)
    {
        var target = GetLabel(label);
        var op = RequiresLeave(label) ? ILOpCode.Leave : ILOpCode.Br;
        _il.Branch(op, target);
    }

    /// <summary>Jumping out of a protected region needs <c>leave</c>; jumping to the same region or into the first instruction of a nested one is a plain branch.</summary>
    private bool RequiresLeave(LabelSymbol label)
    {
        var target = _labelTry.TryGetValue(label, out var t) ? t : 0;
        var current = CurrentTry;
        if (target == current) return false;
        for (var r = target; r != 0; r = _tryParent.GetValueOrDefault(r))
            if (r == current) return false;
        return current != 0;
    }

    private void EmitReturn(BoundReturn r)
    {
        MarkSequencePoint(r.Syntax);
        if (r.Expression != null) EmitExpression(r.Expression, true);
        if (_tryStack.Count > 0)
        {
            if (r.Expression != null)
            {
                _returnSlot ??= TempLocal(_method.ReturnType);
                _il.Stloc(_returnSlot.Index);
            }
            _returnLabelUsed = true;
            _il.Branch(ILOpCode.Leave, _returnLabel);
        }
        else _il.Op(ILOpCode.Ret, r.Expression != null ? -1 : 0);
    }

    private void EmitIf(BoundIf i)
    {
        MarkSequencePoint(i.Condition.Syntax);
        var elseLabel = _il.NewLabel();
        EmitBranchIf(i.Condition, elseLabel, jumpIfTrue: false);
        EmitStatement(i.Consequence);
        if (i.Alternative != null)
        {
            var end = _il.NewLabel();
            _il.Branch(ILOpCode.Br, end);
            _il.Mark(elseLabel);
            EmitStatement(i.Alternative);
            _il.Mark(end);
        }
        else _il.Mark(elseLabel);
    }

    private void EmitWhile(BoundWhile w)
    {
        var body = _il.NewLabel();
        var cont = GetLabel(w.ContinueLabel);
        var brk = GetLabel(w.BreakLabel);
        _il.Branch(ILOpCode.Br, cont);
        _il.Mark(body);
        EmitStatement(w.Body);
        _il.Mark(cont);
        MarkSequencePoint(w.Condition.Syntax);
        EmitBranchIf(w.Condition, body, jumpIfTrue: true);
        _il.Mark(brk);
    }

    private void EmitDoWhile(BoundDoWhile w)
    {
        var body = _il.NewLabel();
        _il.Mark(body);
        EmitStatement(w.Body);
        _il.Mark(GetLabel(w.ContinueLabel));
        EmitBranchIf(w.Condition, body, jumpIfTrue: true);
        _il.Mark(GetLabel(w.BreakLabel));
    }

    private void EmitFor(BoundFor f)
    {
        foreach (var l in f.Locals) GetLocal(l);
        if (f.Initializer != null) EmitStatement(f.Initializer);
        var body = _il.NewLabel();
        var cont = GetLabel(f.ContinueLabel);
        var cond = _il.NewLabel();
        _il.Branch(ILOpCode.Br, cond);
        _il.Mark(body);
        EmitStatement(f.Body);
        _il.Mark(cont);
        if (f.Increment != null) EmitStatement(f.Increment);
        _il.Mark(cond);
        if (f.Condition != null) EmitBranchIf(f.Condition, body, jumpIfTrue: true);
        else _il.Branch(ILOpCode.Br, body);
        _il.Mark(GetLabel(f.BreakLabel));
    }

    private void EmitTry(BoundTry t)
    {
        var ids = _tryIds[t];
        var cfb = _il.ControlFlow;
        var tryStart = _il.NewLabel();
        var tryEnd = _il.NewLabel();
        var end = _il.NewLabel();
        var hasFinally = t.Finally != null;

        LabelHandle outerStart = default, outerEnd = default;
        if (hasFinally)
        {
            outerStart = _il.NewLabel();
            outerEnd = _il.NewLabel();
            _il.Mark(outerStart);
            _tryStack.Push(ids.Outer);
        }

        _il.Mark(tryStart);
        _tryStack.Push(ids.Body);
        EmitStatement(t.Body);
        _il.Branch(ILOpCode.Leave, end);
        _tryStack.Pop();
        _il.Mark(tryEnd);

        for (var i = 0; i < t.Catches.Length; i++)
        {
            var c = t.Catches[i];
            var hs = _il.NewLabel();
            var he = _il.NewLabel();
            _il.Mark(hs);
            _tryStack.Push(ids.Catches[i]);
            _il.ResetDepth(1);
            if (c.Local != null) _il.Stloc(GetLocal(c.Local).Index);
            else _il.Op(ILOpCode.Pop, -1);
            EmitStatement(c.Body);
            _il.Branch(ILOpCode.Leave, end);
            _tryStack.Pop();
            _il.Mark(he);
            cfb.AddCatchRegion(tryStart, tryEnd, hs, he, _m.GetTypeEntity(c.ExceptionType));
        }

        if (hasFinally)
        {
            _il.Mark(end);
            _il.Branch(ILOpCode.Leave, outerEnd);
            _tryStack.Pop(); // outer
            var protectedEnd = _il.NewLabel();
            var fs = _il.NewLabel();
            var fe = _il.NewLabel();
            _il.Mark(protectedEnd);
            _il.Mark(fs);
            _tryStack.Push(ids.Finally);
            _inFinally++;
            _il.ResetDepth(0);
            EmitStatement(t.Finally!);
            _il.Op(ILOpCode.Endfinally, 0);
            _inFinally--;
            _tryStack.Pop();
            _il.Mark(fe);
            cfb.AddFinallyRegion(outerStart, protectedEnd, fs, fe);
            _il.Mark(outerEnd);
        }
        else _il.Mark(end);
        _il.ResetDepth(0);
    }
}
