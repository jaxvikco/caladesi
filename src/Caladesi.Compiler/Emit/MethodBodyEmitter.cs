using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Emit;

/// <summary>Generates IL for one lowered method body.</summary>
internal sealed partial class MethodBodyEmitter
{
    private readonly EmitModule _m;
    private readonly Compilation _c;
    private readonly MethodSymbol _method;
    private readonly ILBuilder _il;
    private readonly SourceFile? _file;
    private readonly Dictionary<LocalSymbol, LocalSlot> _locals = [];
    private readonly Dictionary<ParameterSymbol, int> _paramIndex = [];
    private readonly Dictionary<LabelSymbol, LabelHandle> _labels = [];
    private readonly Dictionary<LabelSymbol, int> _labelTry = [];
    private readonly Dictionary<int, int> _tryParent = [];
    private readonly Dictionary<BoundConditionalReceiver, (LocalSlot slot, TypeSymbol receiverType, bool isNullableValue)> _condReceivers = [];
    private readonly Stack<int> _tryStack = new();
    private int _nextTryId = 1;
    private readonly record struct TryIds(int Outer, int Body, int[] Catches, int Finally);
    private readonly Dictionary<BoundTry, TryIds> _tryIds = [];
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
        _hasReturnValue = !method.ReturnType.ReturnsNothing && method.MethodKind != MethodKind.Constructor && method.MethodKind != MethodKind.StaticConstructor;
        _thisIsByRef = method.ContainingType?.IsValueType == true;
        int offset = _isStatic ? 0 : 1;
        foreach (ParameterSymbol p in method.Parameters)
        {
            _paramIndex[p] = p.Ordinal + offset;
        }
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
            if (_returnSlot != null)
            {
                _il.Ldloc(_returnSlot.Index);
            }

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
                foreach (BoundStatement s in b.Statements)
                {
                    PreScanLabels(s, tryId);
                }

                break;
            case BoundStatementList sl:
                foreach (BoundStatement s in sl.Statements)
                {
                    PreScanLabels(s, tryId);
                }

                break;
            case BoundIf i:
                PreScanLabels(i.Consequence, tryId);
                if (i.Alternative != null)
                {
                    PreScanLabels(i.Alternative, tryId);
                }

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
                if (f.Initializer != null)
                {
                    PreScanLabels(f.Initializer, tryId);
                }

                if (f.Increment != null)
                {
                    PreScanLabels(f.Increment, tryId);
                }

                PreScanLabels(f.Body, tryId);
                break;
            case BoundTry t:
                {
                    TryIds ids = new(t.Finally != null ? _nextTryId++ : 0, _nextTryId++, [.. t.Catches.Select(_ => _nextTryId++)], t.Finally != null ? _nextTryId++ : 0);
                    _tryIds[t] = ids;
                    int bodyParent = ids.Outer != 0 ? ids.Outer : tryId;
                    if (ids.Outer != 0)
                    {
                        _tryParent[ids.Outer] = tryId;
                    }

                    _tryParent[ids.Body] = bodyParent;
                    foreach (int cid in ids.Catches)
                    {
                        _tryParent[cid] = bodyParent;
                    }

                    if (ids.Finally != 0)
                    {
                        _tryParent[ids.Finally] = tryId;
                    }

                    PreScanLabels(t.Body, ids.Body);
                    for (int i = 0; i < t.Catches.Length; i++)
                    {
                        PreScanLabels(t.Catches[i].Body, ids.Catches[i]);
                    }

                    if (t.Finally != null)
                    {
                        PreScanLabels(t.Finally, ids.Finally);
                    }

                    break;
                }
            case BoundSequencePoint sp:
                PreScanLabels(sp.Statement, tryId);
                break;
        }
    }

    private LabelHandle GetLabel(LabelSymbol l)
    {
        if (!_labels.TryGetValue(l, out LabelHandle h))
        {
            _labels[l] = h = _il.NewLabel();
        }

        return h;
    }

    private int CurrentTry => _tryStack.Count == 0 ? 0 : _tryStack.Peek();

    // ------------------------------------------------------------------ locals

    private LocalSlot GetLocal(LocalSymbol l)
    {
        if (!_locals.TryGetValue(l, out LocalSlot? slot))
        {
            slot = _il.NewLocal(_m.ClrType(l.Type), l.IsSynthesized ? null : l.Name);
            _locals[l] = slot;
        }
        return slot;
    }

    private LocalSlot TempLocal(TypeSymbol type)
    {
        return _il.NewLocal(_m.ClrType(type));
    }

    // ------------------------------------------------------------------ statements

    private void EmitStatement(BoundStatement s)
    {
        switch (s)
        {
            case BoundBlock b:
                foreach (LocalSymbol l in b.Locals)
                {
                    _ = GetLocal(l);
                }

                foreach (BoundStatement st in b.Statements)
                {
                    EmitStatement(st);
                }

                break;
            case BoundStatementList sl:
                foreach (BoundStatement st in sl.Statements)
                {
                    EmitStatement(st);
                }

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
                    LabelHandle skip = _il.NewLabel();
                    EmitBranchIf(cg.Condition, skip, !cg.JumpIfTrue);
                    _il.Branch(ILOpCode.Leave, GetLabel(cg.Label));
                    _il.Mark(skip);
                }
                else
                {
                    EmitBranchIf(cg.Condition, GetLabel(cg.Label), cg.JumpIfTrue);
                }

                break;
            case BoundLabel l:
                _il.Mark(GetLabel(l.Label));
                break;
            case BoundReturn r: EmitReturn(r); break;
            case BoundThrow t:
                MarkSequencePoint(t.Syntax);
                if (t.Expression == null)
                {
                    _il.Op(ILOpCode.Rethrow, 0);
                }
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
        if (node == null || _file == null)
        {
            return;
        }

        SyntaxNode n = node;
        // use the statement-like span (exclude trailing trivia)
        TextSpan span = n.Span;
        if (span.Length == 0)
        {
            return;
        }

        LinePositionSpan lp = _file.Tree.Text.GetLinePositionSpan(span);
        _il.MarkSequencePoint(lp.Start.Line + 1, lp.Start.Character + 1, lp.End.Line + 1, lp.End.Character + 1);
    }

    private void EmitGoto(LabelSymbol label)
    {
        LabelHandle target = GetLabel(label);
        ILOpCode op = RequiresLeave(label) ? ILOpCode.Leave : ILOpCode.Br;
        _il.Branch(op, target);
    }

    /// <summary>Jumping out of a protected region needs <c>leave</c>; jumping to the same region or into the first instruction of a nested one is a plain branch.</summary>
    private bool RequiresLeave(LabelSymbol label)
    {
        int target = _labelTry.TryGetValue(label, out int t) ? t : 0;
        int current = CurrentTry;
        if (target == current)
        {
            return false;
        }

        for (int r = target; r != 0; r = _tryParent.GetValueOrDefault(r))
        {
            if (r == current)
            {
                return false;
            }
        }

        return current != 0;
    }

    private void EmitReturn(BoundReturn r)
    {
        MarkSequencePoint(r.Syntax);
        if (r.Expression != null)
        {
            EmitExpression(r.Expression, true);
        }

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
        else
        {
            _il.Op(ILOpCode.Ret, r.Expression != null ? -1 : 0);
        }
    }

    private void EmitIf(BoundIf i)
    {
        MarkSequencePoint(i.Condition.Syntax);
        LabelHandle elseLabel = _il.NewLabel();
        EmitBranchIf(i.Condition, elseLabel, jumpIfTrue: false);
        EmitStatement(i.Consequence);
        if (i.Alternative != null)
        {
            LabelHandle end = _il.NewLabel();
            _il.Branch(ILOpCode.Br, end);
            _il.Mark(elseLabel);
            EmitStatement(i.Alternative);
            _il.Mark(end);
        }
        else
        {
            _il.Mark(elseLabel);
        }
    }

    private void EmitWhile(BoundWhile w)
    {
        LabelHandle body = _il.NewLabel();
        LabelHandle cont = GetLabel(w.ContinueLabel);
        LabelHandle brk = GetLabel(w.BreakLabel);
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
        LabelHandle body = _il.NewLabel();
        _il.Mark(body);
        EmitStatement(w.Body);
        _il.Mark(GetLabel(w.ContinueLabel));
        EmitBranchIf(w.Condition, body, jumpIfTrue: true);
        _il.Mark(GetLabel(w.BreakLabel));
    }

    private void EmitFor(BoundFor f)
    {
        foreach (LocalSymbol l in f.Locals)
        {
            _ = GetLocal(l);
        }

        if (f.Initializer != null)
        {
            EmitStatement(f.Initializer);
        }

        LabelHandle body = _il.NewLabel();
        LabelHandle cont = GetLabel(f.ContinueLabel);
        LabelHandle cond = _il.NewLabel();
        _il.Branch(ILOpCode.Br, cond);
        _il.Mark(body);
        EmitStatement(f.Body);
        _il.Mark(cont);
        if (f.Increment != null)
        {
            EmitStatement(f.Increment);
        }

        _il.Mark(cond);
        if (f.Condition != null)
        {
            EmitBranchIf(f.Condition, body, jumpIfTrue: true);
        }
        else
        {
            _il.Branch(ILOpCode.Br, body);
        }

        _il.Mark(GetLabel(f.BreakLabel));
    }

    private void EmitTry(BoundTry t)
    {
        TryIds ids = _tryIds[t];
        ControlFlowBuilder cfb = _il.ControlFlow;
        LabelHandle tryStart = _il.NewLabel();
        LabelHandle tryEnd = _il.NewLabel();
        LabelHandle end = _il.NewLabel();
        bool hasFinally = t.Finally != null;

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
        _ = _tryStack.Pop();
        _il.Mark(tryEnd);

        for (int i = 0; i < t.Catches.Length; i++)
        {
            BoundCatch c = t.Catches[i];
            LabelHandle hs = _il.NewLabel();
            LabelHandle he = _il.NewLabel();
            _il.Mark(hs);
            _tryStack.Push(ids.Catches[i]);
            _il.ResetDepth(1);
            if (c.Local != null)
            {
                _il.Stloc(GetLocal(c.Local).Index);
            }
            else
            {
                _il.Op(ILOpCode.Pop, -1);
            }

            EmitStatement(c.Body);
            _il.Branch(ILOpCode.Leave, end);
            _ = _tryStack.Pop();
            _il.Mark(he);
            cfb.AddCatchRegion(tryStart, tryEnd, hs, he, _m.GetTypeEntity(c.ExceptionType));
        }

        if (hasFinally)
        {
            _il.Mark(end);
            _il.Branch(ILOpCode.Leave, outerEnd);
            _ = _tryStack.Pop(); // outer
            LabelHandle protectedEnd = _il.NewLabel();
            LabelHandle fs = _il.NewLabel();
            LabelHandle fe = _il.NewLabel();
            _il.Mark(protectedEnd);
            _il.Mark(fs);
            _tryStack.Push(ids.Finally);
            _il.ResetDepth(0);
            EmitStatement(t.Finally!);
            _il.Op(ILOpCode.Endfinally, 0);
            _ = _tryStack.Pop();
            _il.Mark(fe);
            cfb.AddFinallyRegion(outerStart, protectedEnd, fs, fe);
            _il.Mark(outerEnd);
        }
        else
        {
            _il.Mark(end);
        }

        _il.ResetDepth(0);
    }
}
