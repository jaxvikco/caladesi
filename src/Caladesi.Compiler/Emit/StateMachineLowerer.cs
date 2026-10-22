using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Emit.Lowering;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>
/// Turns an async method or an iterator into a state machine class: locals become fields, awaits and yields become
/// suspension points with a resume label and a state number, and <c>MoveNext</c> dispatches on the state.
/// The original method keeps its signature and just creates and starts the machine.
/// </summary>
internal sealed partial class StateMachineLowerer
{
    private enum Kind { Async, Iterator, AsyncIterator }

    private readonly Compilation _c;
    private readonly LoweredProgram _program;
    private readonly MethodSymbol _method;
    private readonly NamedTypeSymbol _container;
    private readonly int _ordinal;
    private readonly Kind _kind;
    private int _tempCounter;

    private SynthesizedNamedType _cls = null!;
    private NamedTypeSymbol _closed = null!;

    // declared members (definition symbols; use Closed* helpers for the instantiated view)
    private SynthesizedFieldSymbol _stateField = null!;
    private SynthesizedMethodSymbol _moveNext = null!;
    private readonly Dictionary<Symbol, SynthesizedFieldSymbol> _varFields = new(ReferenceEqualityComparer.Instance);
    private SynthesizedFieldSymbol? _thisField;
    private TypeSymbol? _thisType;
    private readonly Dictionary<string, SynthesizedFieldSymbol> _awaiterFields = [];

    private TypeSymbol Void => _c.GetSpecialType(SpecialType.Void);
    private TypeSymbol Bool => _c.GetSpecialType(SpecialType.Boolean);
    private TypeSymbol Int => _c.GetSpecialType(SpecialType.Int32);
    private NamedTypeSymbol Wk(string name)
    {
        return _c.GetWellKnownType(name) ?? throw new InvalidOperationException("missing well-known type " + name);
    }

    private StateMachineLowerer(Compilation c, LoweredProgram program, MethodSymbol method, NamedTypeSymbol container, int ordinal)
    {
        _c = c;
        _program = program;
        _method = method;
        _container = container;
        _ordinal = ordinal;
        _kind = method.IsIterator ? (method.IsAsync ? Kind.AsyncIterator : Kind.Iterator) : Kind.Async;
    }

    public static void Lower(Compilation c, LoweredProgram program, MethodSymbol method, NamedTypeSymbol container, int ordinal, BoundBlock body)
    {
        new StateMachineLowerer(c, program, method, container, ordinal).Run(body);
    }

    private LocalSymbol NewTemp(TypeSymbol type)
    {
        return new("<>s__" + _tempCounter++, type, false, _method) { IsSynthesized = true };
    }

    private BoundLiteral IntLit(int v)
    {
        return new(new ConstantValue(v), Int);
    }

    private static BoundExpressionStatement Assign(BoundExpression target, BoundExpression value)
    {
        return new BoundExpressionStatement(new BoundAssignment(target, value) { IsDiscarded = true });
    }

    private static BoundBlock Block(IEnumerable<BoundStatement> s)
    {
        return new([], [.. s]);
    }

    // ------------------------------------------------------------------ pipeline

    private TypeSymbol? _payload;          // async: the value type of the result (null for void results); iterator: element type
    private NamedTypeSymbol? _builderType;
    private LocalSymbol? _disposingPlaceholder;
    private readonly LabelSymbol _endLabel = new("<end>");
    private readonly LabelSymbol _suspendLabel = new("<suspend>");

    private void Run(BoundBlock body)
    {
        AnalyzeSignature();
        SuspensionFinder finder = new();
        SuspensionSpiller spiller = new(finder, NewTemp, Bool);
        body = spiller.Spill(body);

        if (_kind != Kind.Async)
        {
            _disposingPlaceholder = NewTemp(Bool);
        }

        TryRewriter tryRewriter = new(_c, finder, NewTemp, _kind == Kind.Async ? _payload : null,
            _kind != Kind.Async ? () => new BoundLocal(_disposingPlaceholder!) : null, _kind == Kind.AsyncIterator ? _exitLabel : _endLabel);
        body = (BoundBlock)tryRewriter.Visit(body)!;

        Collector collected = new();
        _ = collected.Visit(body);
        DeclareMembers(collected);

        body = (BoundBlock)new Hoister(this).Visit(body)!;
        StateRewriter states = new(this);
        body = (BoundBlock)states.Visit(body)!;

        BoundBlock moveNextBody = _kind == Kind.Iterator ? AssembleIteratorMoveNext(body, states) : AssembleAsyncMoveNext(body, states);
        _program.Bodies[_moveNext] = moveNextBody;
        if (_kind != Kind.Iterator)
        {
            _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "SetStateMachine")] = Block([]);
        }

        _program.Types.Add(_cls);

        if (_kind == Kind.Iterator)
        {
            BuildIteratorSupportBodies();
        }
        else if (_kind == Kind.AsyncIterator)
        {
            BuildAsyncIteratorSupportBodies();
        }

        BoundBlock stub = _kind == Kind.Async ? BuildAsyncStub() : BuildIteratorStub();
        _program.Bodies[_method] = stub;
    }

    private void AnalyzeSignature()
    {
        TypeSymbol rt = _method.ReturnType.StripNullable();
        if (_kind != Kind.Async)
        {
            _payload = rt is NamedTypeSymbol { IsGenericType: true } n ? n.TypeArguments[0] : _c.GetSpecialType(SpecialType.Object);
            if (_kind == Kind.AsyncIterator)
            {
                _builderType = Wk("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1").Construct(Bool);
            }

            return;
        }
        string builder;
        TypeSymbol? payload = null;
        if (rt.IsVoid)
        {
            builder = "System.Runtime.CompilerServices.AsyncVoidMethodBuilder";
        }
        else
        {
            NamedTypeSymbol n = (NamedTypeSymbol)rt;
            switch (n.OriginalDefinition.SpecialType)
            {
                case SpecialType.Task: builder = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder"; break;
                case SpecialType.TaskT: builder = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1"; payload = n.TypeArguments[0]; break;
                case SpecialType.ValueTask: builder = "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder"; break;
                case SpecialType.ValueTaskT: builder = "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1"; payload = n.TypeArguments[0]; break;
                default: throw new NotSupportedException("unsupported async return type " + rt.ToDisplayString());
            }
        }
        NamedTypeSymbol b = Wk(builder);
        _builderType = payload != null ? b.Construct(payload) : b;
        _payload = payload;
    }

    // ------------------------------------------------------------------ collecting what must become a field

    private sealed class Collector : BoundTreeRewriter
    {
        public readonly List<LocalSymbol> Locals = [];
        public readonly HashSet<LocalSymbol> CatchLocals = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<LocalSymbol> Seen = new(ReferenceEqualityComparer.Instance);
        public readonly List<TypeSymbol> AwaiterTypes = [];
        public TypeSymbol? ThisType;

        private void Add(LocalSymbol l)
        {
            if (Seen.Add(l))
            {
                Locals.Add(l);
            }
        }

        protected override BoundNode VisitLocal(BoundLocal n) { Add(n.Local); return n; }
        protected override BoundNode VisitLocalDeclaration(BoundLocalDeclaration n) { Add(n.Local); return base.VisitLocalDeclaration(n); }
        protected override BoundNode VisitThis(BoundThis n) { ThisType ??= n.Type; return n; }
        protected override BoundNode VisitBlock(BoundBlock n) { foreach (LocalSymbol l in n.Locals) { Add(l); } return base.VisitBlock(n); }
        protected override BoundNode VisitFor(BoundFor n) { foreach (LocalSymbol l in n.Locals) { Add(l); } return base.VisitFor(n); }
        protected override BoundNode VisitSequence(BoundSequence n) { foreach (LocalSymbol l in n.Locals) { Add(l); } return base.VisitSequence(n); }
        protected override BoundNode VisitAwait(BoundAwait n) { AwaiterTypes.Add(n.Info.AwaiterType); return base.VisitAwait(n); }

        protected override BoundNode VisitTry(BoundTry n)
        {
            _ = Visit(n.Body);
            foreach (BoundCatch c in n.Catches)
            {
                if (c.Local != null)
                {
                    _ = CatchLocals.Add(c.Local);
                }

                _ = Visit(c.Body);
            }
            if (n.Finally != null)
            {
                _ = Visit(n.Finally);
            }

            return n;
        }
    }

    // ------------------------------------------------------------------ declaring the class

    private SynthesizedMethodSymbol NewMethod(string name, TypeSymbol ret, bool isPublic = true, params (string Name, TypeSymbol Type)[] ps)
    {
        ImmutableArray<ParameterSymbol> parameters = [.. ps.Select((p, i) => new ParameterSymbol(p.Name, p.Type, i, null))];
        SynthesizedMethodSymbol m = new(_cls, name, name == ".ctor" ? MethodKind.Constructor : MethodKind.Ordinary,
            isPublic ? Accessibility.Public : Accessibility.Private, false, ret, parameters)
        { SynthesizedKind = SynthesizedBodyKind.Custom, SourceMethod = _method };
        _cls.AddMember(m);
        return m;
    }

    private SynthesizedFieldSymbol NewField(string name, TypeSymbol type)
    {
        SynthesizedFieldSymbol f = new(_cls, name, type);
        _cls.AddMember(f);
        return f;
    }

    private void DeclareMembers(Collector col)
    {
        string name = "<" + _method.Name + ">d__" + _ordinal;
        _cls = SynthesizedClassFactory.Create(_c, _container, _method, name, null, out _closed);
        _ = NewMethod(".ctor", Void);
        _moveNext = NewMethod("MoveNext", _kind == Kind.Iterator ? Bool : Void);
        _stateField = NewField("<>1__state", Int);
        _smLocal = new LocalSymbol("<sm>", _closed, false, _method) { IsSynthesized = true };

        if (_kind == Kind.Async)
        {
            DeclareAsyncMembers(col);
        }
        else if (_kind == Kind.Iterator)
        {
            DeclareIteratorMembersEntry();
        }
        else
        {
            DeclareAsyncIteratorMembers();
        }

        if (!_method.IsStatic)
        {
            _thisType = col.ThisType ?? (_container.IsGenericType ? _container.Construct(_container.TypeArguments) : _container);
            _thisField = NewField("<>4__this", _thisType);
        }
        foreach (ParameterSymbol p in _method.Parameters)
        {
            _varFields[p] = NewField(p.Name, p.Type);
        }

        int n = 0;
        foreach (LocalSymbol l in col.Locals)
        {
            if (col.CatchLocals.Contains(l) || l == _disposingPlaceholder)
            {
                continue;
            }

            _varFields[l] = NewField("<" + l.Name + ">5__" + n++, l.Type);
        }
        foreach (TypeSymbol t in col.AwaiterTypes)
        {
            string key = t.ToDisplayString();
            if (!_awaiterFields.ContainsKey(key))
            {
                _awaiterFields[key] = NewField("<>u__" + (_awaiterFields.Count + 1), t);
            }
        }
        DeclareKindSpecificFields();
    }

    // closed (instantiated) views -------------------------------------------------

    private FieldSymbol CF(FieldSymbol def)
    {
        return SynthesizedClassFactory.ClosedField(_closed, def);
    }

    private BoundThis SelfThis()
    {
        return new(_closed);
    }

    private BoundFieldAccess Field(SynthesizedFieldSymbol f)
    {
        return new BoundFieldAccess(SelfThis(), CF(f));
    }

    private BoundFieldAccess FieldOn(BoundExpression receiver, SynthesizedFieldSymbol f)
    {
        return new BoundFieldAccess(receiver, CF(f));
    }

    private BoundFieldAccess State()
    {
        return Field(_stateField);
    }

    // ------------------------------------------------------------------ hoisting

    private sealed class Hoister(StateMachineLowerer o) : BoundTreeRewriter
    {
        private BoundFieldAccess? Hoisted(Symbol s)
        {
            return o._varFields.TryGetValue(s, out SynthesizedFieldSymbol? f) ? o.Field(f) : null;
        }

        protected override BoundNode VisitLocal(BoundLocal n)
        {
            return n.Local == o._disposingPlaceholder ? o.Field(o._disposingField!) : Hoisted(n.Local) ?? (BoundExpression)n;
        }

        protected override BoundNode VisitParameter(BoundParameter n)
        {
            return Hoisted(n.Parameter) ?? (BoundExpression)n;
        }

        protected override BoundNode VisitThis(BoundThis n)
        {
            return o._thisField != null && !ReferenceEquals(n.Type, o._closed) ? o.Field(o._thisField) : n;
        }

        protected override BoundNode VisitLocalDeclaration(BoundLocalDeclaration n)
        {
            return !o._varFields.TryGetValue(n.Local, out SynthesizedFieldSymbol? f)
                ? base.VisitLocalDeclaration(n)
                : n.Initializer == null
                ? new BoundNoOp { Syntax = n.Syntax }
                : new BoundExpressionStatement(new BoundAssignment(o.Field(f), VisitExpr(n.Initializer)) { Syntax = n.Syntax, IsDiscarded = true }) { Syntax = n.Syntax };
        }

        private ImmutableArray<LocalSymbol> Keep(ImmutableArray<LocalSymbol> locals)
        {
            return locals.IsDefaultOrEmpty ? locals : [.. locals.Where(l => !o._varFields.ContainsKey(l))];
        }

        protected override BoundNode VisitBlock(BoundBlock n)
        {
            ImmutableArray<BoundStatement> s = VisitStmts(n.Statements);
            return new BoundBlock(Keep(n.Locals), s) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitFor(BoundFor n)
        {
            BoundStatement? i = VisitStmtOpt(n.Initializer);
            BoundExpression? c = VisitExprOpt(n.Condition);
            BoundStatement? inc = VisitStmtOpt(n.Increment);
            BoundStatement b = VisitStmt(n.Body);
            return new BoundFor(Keep(n.Locals), i, c, inc, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitSequence(BoundSequence n)
        {
            ImmutableArray<BoundStatement> s = VisitStmts(n.SideEffects);
            BoundExpression v = VisitExpr(n.Value);
            return new BoundSequence(Keep(n.Locals), s, v) { Syntax = n.Syntax };
        }
    }

    // ------------------------------------------------------------------ suspension points

    private sealed class TryInfo(LabelSymbol start)
    {
        public LabelSymbol Start { get; } = start;
        public List<(int State, LabelSymbol Target)> Entries { get; } = [];
        public void Register(int state, LabelSymbol target)
        {
            if (!Entries.Any(e => e.State == state))
            {
                Entries.Add((state, target));
            }
        }
    }

    private int _stateCount;

    private sealed class StateRewriter : BoundTreeRewriter
    {
        private readonly StateMachineLowerer _o;
        private readonly Stack<TryInfo> _tries = new();
        public TryInfo Root { get; } = new(new LabelSymbol("<root>"));

        public StateRewriter(StateMachineLowerer o)
        {
            _o = o;
            _tries.Push(Root);
        }

        private int NewState(LabelSymbol label)
        {
            int k = _o._stateCount++;
            _tries.Peek().Register(k, label);
            return k;
        }

        protected override BoundNode VisitTry(BoundTry n)
        {
            TryInfo info = new(new LabelSymbol("<try>"));
            _tries.Push(info);
            BoundBlock body = (BoundBlock)Visit(n.Body)!;
            ImmutableArray<BoundCatch>.Builder catches = ImmutableArray.CreateBuilder<BoundCatch>(n.Catches.Length);
            foreach (BoundCatch c in n.Catches)
            {
                catches.Add(new BoundCatch(c.Local, c.ExceptionType, (BoundBlock)Visit(c.Body)!));
            }

            BoundBlock? fin = n.Finally == null ? null : (BoundBlock)Visit(n.Finally)!;
            _ = _tries.Pop();
            if (info.Entries.Count > 0)
            {
                List<BoundStatement> stmts = [new BoundLabel(info.Start), .. _o.DispatchStatements(info), .. body.Statements];
                body = new BoundBlock(body.Locals, [.. stmts]) { Syntax = body.Syntax };
                foreach ((int state, LabelSymbol _) in info.Entries)
                {
                    _tries.Peek().Register(state, info.Start);
                }
            }
            return new BoundTry(body, catches.MoveToImmutable(), fin) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitExpressionStatement(BoundExpressionStatement n)
        {
            return n.Expression switch
            {
                BoundAwait aw => _o.LowerAwait(aw, null, NewState),
                BoundAssignment { Value: BoundAwait aw2 } asg => _o.LowerAwait(aw2, asg.Target, NewState),
                _ => base.VisitExpressionStatement(n),
            };
        }

        protected override BoundNode VisitReturn(BoundReturn n)
        {
            return _o.LowerReturn(n);
        }

        protected override BoundNode VisitYieldBreak(BoundYieldBreak n)
        {
            return new BoundGoto(_o._kind == Kind.AsyncIterator ? _o._exitLabel : _o._endLabel);
        }

        protected override BoundNode VisitYieldReturn(BoundYieldReturn n)
        {
            return _o.LowerYieldReturn(n, NewState, n.DisposeTarget);
        }

        protected override BoundNode VisitAwait(BoundAwait n)
        {
            throw new InvalidOperationException("await survived spilling");
        }
    }

    private IEnumerable<BoundStatement> DispatchStatements(TryInfo info)
    {
        foreach ((int state, LabelSymbol? target) in info.Entries)
        {
            yield return new BoundConditionalGoto(
                new BoundBinary(BinaryOperatorKind.Equal, State(), IntLit(state), Bool, Int), target, jumpIfTrue: true);
        }
    }

    // ------------------------------------------------------------------ async pieces

    private SynthesizedFieldSymbol? _builderField;
    private SynthesizedFieldSymbol? _retvalField;

    private void DeclareIteratorMembersEntry()
    {
        DeclareIteratorMembers();
    }

    private void DeclareAsyncMembers(Collector col)
    {
        _cls.AddInterface(Wk("System.Runtime.CompilerServices.IAsyncStateMachine"));
        _builderField = NewField("<>t__builder", _builderType!);
        if (_payload != null && _kind == Kind.Async)
        {
            _retvalField = NewField("<>s__retval", _payload);
        }

        _ = NewMethod("SetStateMachine", Void, true, ("stateMachine", Wk("System.Runtime.CompilerServices.IAsyncStateMachine")));
        _ = col;
    }

    private void DeclareKindSpecificFields()
    {
        if (_kind != Kind.Async)
        {
            DeclareIteratorFields();
        }
    }

    private BoundFieldAccess Builder()
    {
        return Field(_builderField!);
    }

    private MethodSymbol BuilderMethod(string name, int paramCount, int arity = 0)
    {
        return _builderType!.GetMembers(name).OfType<MethodSymbol>().First(m => m.Parameters.Length == paramCount && m.Arity == arity);
    }

    private static BoundExpressionStatement CallStmt(BoundExpression receiver, MethodSymbol m, params BoundExpression[] args)
    {
        return new BoundExpressionStatement(new BoundCall(receiver, m, [.. args]));
    }

    private BoundBlock LowerAwait(BoundAwait aw, BoundExpression? target, Func<LabelSymbol, int> newState)
    {
        AwaitInfo info = aw.Info;
        SynthesizedFieldSymbol awaiterDef = _awaiterFields[info.AwaiterType.ToDisplayString()];
        BoundExpression Awaiter()
        {
            return Field(awaiterDef);
        }

        LabelSymbol resume = new("<resume>");
        int k = newState(resume);

        BoundCall getAwaiter = info.GetAwaiter.IsStatic
            ? new BoundCall(null, info.GetAwaiter, [aw.Operand])
            : new BoundCall(aw.Operand, info.GetAwaiter, []);
        MethodSymbol isCompleted = info.IsCompletedGetter ?? info.IsCompleted!.GetMethod!;
        bool critical = info.AwaiterType.AllInterfaces.Any(i => i.OriginalDefinition.MetadataName == "ICriticalNotifyCompletion");
        MethodSymbol awaitOn = _builderType!.GetMembers(critical ? "AwaitUnsafeOnCompleted" : "AwaitOnCompleted").OfType<MethodSymbol>().First(m => m.Arity == 2)
            .Construct([info.AwaiterType, _closed]);

        List<BoundStatement> stmts =
        [
            Assign(Awaiter(), getAwaiter),
            new BoundIf(
                new BoundUnary(UnaryOperatorKind.Not, new BoundCall(Awaiter(), isCompleted, []), Bool),
                Block([
                    Assign(State(), IntLit(k)),
                    CallStmt(Builder(), awaitOn, new BoundAddressOf(Awaiter()), new BoundAddressOf(new BoundLocal(_smLocal))),
                    new BoundGoto(_suspendLabel),
                ]), null),
            new BoundLabel(resume),
            Assign(State(), IntLit(-1)),
        ];
        BoundCall result = new(Awaiter(), info.GetResult, []);
        if (target != null)
        {
            stmts.Add(Assign(target, result));
        }
        else
        {
            stmts.Add(new BoundExpressionStatement(result));
        }

        stmts.Add(Assign(Awaiter(), new BoundDefaultValue(info.AwaiterType)));
        return Block(stmts);
    }

    private readonly LabelSymbol _exitLabel = new("<exit>");
    private LocalSymbol _smLocal = null!;

    private BoundStatement LowerReturn(BoundReturn r)
    {
        if (_kind == Kind.Iterator)
        {
            return new BoundGoto(_endLabel);
        }

        List<BoundStatement> stmts = [];
        if (r.Expression != null && _retvalField != null)
        {
            stmts.Add(Assign(Field(_retvalField), r.Expression));
        }
        else if (r.Expression != null)
        {
            stmts.Add(new BoundExpressionStatement(r.Expression));
        }

        stmts.Add(new BoundGoto(_exitLabel));
        return Block(stmts);
    }

    private BoundBlock AssembleAsyncMoveNext(BoundBlock body, StateRewriter states)
    {
        NamedTypeSymbol exType = Wk("System.Exception");
        LocalSymbol ex = new("ex", exType, false, _method) { IsSynthesized = true };
        List<BoundStatement> tryBody = [.. DispatchStatements(states.Root), body];
        BoundBlock catchBody = Block([
            Assign(State(), IntLit(-2)),
            CallStmt(Builder(), BuilderMethod("SetException", 1), new BoundLocal(ex)),
            new BoundReturn(null),
        ]);
        BoundStatement setResult = _kind == Kind.AsyncIterator
            ? CallStmt(Builder(), BuilderMethod("SetResult", 1), new BoundLiteral(new ConstantValue(false), Bool))
            : _payload != null
                ? CallStmt(Builder(), BuilderMethod("SetResult", 1), Field(_retvalField!))
                : CallStmt(Builder(), BuilderMethod("SetResult", 0));
        List<BoundStatement> stmts =
        [
            Assign(new BoundLocal(_smLocal), SelfThis()),
            new BoundTry(Block(tryBody), [new BoundCatch(ex, exType, catchBody)], null),
            new BoundLabel(_exitLabel),
            Assign(State(), IntLit(-2)),
            setResult,
            new BoundLabel(_suspendLabel),
        ];
        return new BoundBlock([_smLocal], [.. stmts]);
    }

    private BoundBlock BuildAsyncStub()
    {
        LocalSymbol sm = new("sm", _closed, false, _method) { IsSynthesized = true };
        BoundExpression Sm()
        {
            return new BoundLocal(sm);
        }

        MethodSymbol ctor = _closed.InstanceConstructors.First();
        List<BoundStatement> stmts =
        [
            Assign(Sm(), new BoundObjectCreation(ctor, _closed, [], [])),
            Assign(FieldOn(Sm(), _builderField!), new BoundCall(null, BuilderMethod("Create", 0), [])),
        ];
        if (_thisField != null)
        {
            stmts.Add(Assign(FieldOn(Sm(), _thisField), new BoundThis(_thisType!)));
        }

        foreach (ParameterSymbol p in _method.Parameters)
        {
            stmts.Add(Assign(FieldOn(Sm(), _varFields[p]), new BoundParameter(p)));
        }

        stmts.Add(Assign(FieldOn(Sm(), _stateField), IntLit(-1)));
        MethodSymbol start = _builderType!.GetMembers("Start").OfType<MethodSymbol>().First(m => m.Arity == 1 && m.Parameters.Length == 1).Construct([_closed]);
        stmts.Add(CallStmt(FieldOn(Sm(), _builderField!), start, new BoundAddressOf(Sm())));
        MethodSymbol? taskProp = _builderType.GetMembers("get_Task").OfType<MethodSymbol>().FirstOrDefault();
        if (taskProp != null)
        {
            stmts.Add(new BoundReturn(new BoundCall(FieldOn(Sm(), _builderField!), taskProp, [])));
        }
        else
        {
            stmts.Add(new BoundReturn(null));
        }

        return new BoundBlock([sm], [.. stmts]);
    }
}
