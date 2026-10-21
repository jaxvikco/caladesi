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
    private enum Kind { Async, Iterator }

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
    private SynthesizedMethodSymbol _ctor = null!;
    private SynthesizedMethodSymbol _moveNext = null!;
    private readonly Dictionary<Symbol, SynthesizedFieldSymbol> _varFields = new(ReferenceEqualityComparer.Instance);
    private SynthesizedFieldSymbol? _thisField;
    private TypeSymbol? _thisType;
    private readonly Dictionary<string, SynthesizedFieldSymbol> _awaiterFields = new();

    private TypeSymbol Void => _c.GetSpecialType(SpecialType.Void);
    private TypeSymbol Bool => _c.GetSpecialType(SpecialType.Boolean);
    private TypeSymbol Int => _c.GetSpecialType(SpecialType.Int32);
    private NamedTypeSymbol Wk(string name) => _c.GetWellKnownType(name) ?? throw new InvalidOperationException("missing well-known type " + name);

    private StateMachineLowerer(Compilation c, LoweredProgram program, MethodSymbol method, NamedTypeSymbol container, int ordinal)
    {
        _c = c;
        _program = program;
        _method = method;
        _container = container;
        _ordinal = ordinal;
        _kind = method.IsIterator ? Kind.Iterator : Kind.Async;
    }

    public static void Lower(Compilation c, LoweredProgram program, MethodSymbol method, NamedTypeSymbol container, int ordinal, BoundBlock body)
    {
        if (method.IsIterator && method.IsAsync)
            throw new NotSupportedException("async iterators are not supported yet");
        new StateMachineLowerer(c, program, method, container, ordinal).Run(body);
    }

    private LocalSymbol NewTemp(TypeSymbol type) => new("<>s__" + _tempCounter++, type, false, _method) { IsSynthesized = true };

    private BoundLiteral IntLit(int v) => new(new ConstantValue(v), Int);

    private static BoundStatement Assign(BoundExpression target, BoundExpression value) =>
        new BoundExpressionStatement(new BoundAssignment(target, value) { IsDiscarded = true });

    private static BoundBlock Block(IEnumerable<BoundStatement> s) => new(ImmutableArray<LocalSymbol>.Empty, s.ToImmutableArray());

    // ------------------------------------------------------------------ pipeline

    private TypeSymbol? _payload;          // async: the value type of the result (null for void results); iterator: element type
    private NamedTypeSymbol? _builderType;
    private LocalSymbol? _disposingPlaceholder;
    private readonly LabelSymbol _endLabel = new("<end>");
    private readonly LabelSymbol _suspendLabel = new("<suspend>");

    private void Run(BoundBlock body)
    {
        AnalyzeSignature();
        var finder = new SuspensionFinder();
        var spiller = new SuspensionSpiller(finder, NewTemp, Bool);
        body = spiller.Spill(body);

        if (_kind == Kind.Iterator) _disposingPlaceholder = NewTemp(Bool);
        var tryRewriter = new TryRewriter(_c, finder, NewTemp, _kind == Kind.Async ? _payload : null,
            _kind == Kind.Iterator ? () => new BoundLocal(_disposingPlaceholder!) : null, _endLabel);
        body = (BoundBlock)tryRewriter.Visit(body)!;

        var collected = new Collector();
        collected.Visit(body);
        DeclareMembers(collected);

        body = (BoundBlock)new Hoister(this).Visit(body)!;
        var states = new StateRewriter(this, tryRewriter.DisposeTargets);
        body = (BoundBlock)states.Visit(body)!;

        var moveNextBody = _kind == Kind.Async ? AssembleAsyncMoveNext(body, states) : AssembleIteratorMoveNext(body, states);
        _program.Bodies[_moveNext] = moveNextBody;
        if (_kind == Kind.Async) _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "SetStateMachine")] = Block([]);
        _program.Types.Add(_cls);

        if (_kind == Kind.Iterator) BuildIteratorSupportBodies();
        var stub = _kind == Kind.Async ? BuildAsyncStub() : BuildIteratorStub();
        _program.Bodies[_method] = stub;
    }

    private void AnalyzeSignature()
    {
        var rt = _method.ReturnType.StripNullable();
        if (_kind == Kind.Iterator)
        {
            _payload = rt is NamedTypeSymbol { IsGenericType: true } n ? n.TypeArguments[0] : _c.GetSpecialType(SpecialType.Object);
            return;
        }
        string builder;
        TypeSymbol? payload = null;
        if (rt.IsVoid) builder = "System.Runtime.CompilerServices.AsyncVoidMethodBuilder";
        else
        {
            var n = (NamedTypeSymbol)rt;
            switch (n.OriginalDefinition.SpecialType)
            {
                case SpecialType.Task: builder = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder"; break;
                case SpecialType.TaskT: builder = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1"; payload = n.TypeArguments[0]; break;
                case SpecialType.ValueTask: builder = "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder"; break;
                case SpecialType.ValueTaskT: builder = "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1"; payload = n.TypeArguments[0]; break;
                default: throw new NotSupportedException("unsupported async return type " + rt.ToDisplayString());
            }
        }
        var b = Wk(builder);
        _builderType = payload != null ? b.Construct(payload) : b;
        _payload = payload;
    }

    // ------------------------------------------------------------------ collecting what must become a field

    private sealed class Collector : BoundTreeRewriter
    {
        public readonly List<LocalSymbol> Locals = new();
        public readonly HashSet<LocalSymbol> CatchLocals = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<LocalSymbol> Seen = new(ReferenceEqualityComparer.Instance);
        public readonly List<TypeSymbol> AwaiterTypes = new();
        public TypeSymbol? ThisType;

        private void Add(LocalSymbol l)
        {
            if (Seen.Add(l)) Locals.Add(l);
        }

        protected override BoundNode VisitLocal(BoundLocal n) { Add(n.Local); return n; }
        protected override BoundNode VisitLocalDeclaration(BoundLocalDeclaration n) { Add(n.Local); return base.VisitLocalDeclaration(n); }
        protected override BoundNode VisitThis(BoundThis n) { ThisType ??= n.Type; return n; }
        protected override BoundNode VisitBlock(BoundBlock n) { foreach (var l in n.Locals) Add(l); return base.VisitBlock(n); }
        protected override BoundNode VisitFor(BoundFor n) { foreach (var l in n.Locals) Add(l); return base.VisitFor(n); }
        protected override BoundNode VisitSequence(BoundSequence n) { foreach (var l in n.Locals) Add(l); return base.VisitSequence(n); }
        protected override BoundNode VisitAwait(BoundAwait n) { AwaiterTypes.Add(n.Info.AwaiterType); return base.VisitAwait(n); }

        protected override BoundNode VisitTry(BoundTry n)
        {
            Visit(n.Body);
            foreach (var c in n.Catches)
            {
                if (c.Local != null) CatchLocals.Add(c.Local);
                Visit(c.Body);
            }
            if (n.Finally != null) Visit(n.Finally);
            return n;
        }
    }

    // ------------------------------------------------------------------ declaring the class

    private SynthesizedMethodSymbol NewMethod(string name, TypeSymbol ret, bool isPublic = true, params (string Name, TypeSymbol Type)[] ps)
    {
        var parameters = ps.Select((p, i) => new ParameterSymbol(p.Name, p.Type, i, null)).ToImmutableArray();
        var m = new SynthesizedMethodSymbol(_cls, name, name == ".ctor" ? MethodKind.Constructor : MethodKind.Ordinary,
            isPublic ? Accessibility.Public : Accessibility.Private, false, ret, parameters)
        { SynthesizedKind = SynthesizedBodyKind.Custom, SourceMethod = _method };
        _cls.AddMember(m);
        return m;
    }

    private SynthesizedFieldSymbol NewField(string name, TypeSymbol type)
    {
        var f = new SynthesizedFieldSymbol(_cls, name, type);
        _cls.AddMember(f);
        return f;
    }

    private void DeclareMembers(Collector col)
    {
        var name = "<" + _method.Name + ">d__" + _ordinal;
        _cls = SynthesizedClassFactory.Create(_c, _container, _method, name, null, out _closed);
        _ctor = NewMethod(".ctor", Void);
        _moveNext = NewMethod("MoveNext", _kind == Kind.Async ? Void : Bool);
        _stateField = NewField("<>1__state", Int);
        _smLocal = new LocalSymbol("<sm>", _closed, false, _method) { IsSynthesized = true };

        if (_kind == Kind.Async) DeclareAsyncMembers(col);
        else DeclareIteratorMembersEntry();

        if (!_method.IsStatic)
        {
            _thisType = col.ThisType ?? (_container.IsGenericType ? _container.Construct(_container.TypeArguments) : _container);
            _thisField = NewField("<>4__this", _thisType);
        }
        foreach (var p in _method.Parameters) _varFields[p] = NewField(p.Name, p.Type);
        var n = 0;
        foreach (var l in col.Locals)
        {
            if (col.CatchLocals.Contains(l) || l == _disposingPlaceholder) continue;
            _varFields[l] = NewField("<" + l.Name + ">5__" + n++, l.Type);
        }
        foreach (var t in col.AwaiterTypes)
        {
            var key = t.ToDisplayString();
            if (!_awaiterFields.ContainsKey(key)) _awaiterFields[key] = NewField("<>u__" + (_awaiterFields.Count + 1), t);
        }
        DeclareKindSpecificFields();
    }

    // closed (instantiated) views -------------------------------------------------

    private FieldSymbol CF(FieldSymbol def) => SynthesizedClassFactory.ClosedField(_closed, def);
    private BoundThis SelfThis() => new(_closed);
    private BoundExpression Field(SynthesizedFieldSymbol f) => new BoundFieldAccess(SelfThis(), CF(f));
    private BoundExpression FieldOn(BoundExpression receiver, SynthesizedFieldSymbol f) => new BoundFieldAccess(receiver, CF(f));
    private BoundExpression State() => Field(_stateField);

    // ------------------------------------------------------------------ hoisting

    private sealed class Hoister(StateMachineLowerer o) : BoundTreeRewriter
    {
        private BoundExpression? Hoisted(Symbol s) => o._varFields.TryGetValue(s, out var f) ? o.Field(f) : null;

        protected override BoundNode VisitLocal(BoundLocal n)
        {
            if (n.Local == o._disposingPlaceholder) return o.Field(o._disposingField!);
            return Hoisted(n.Local) ?? n;
        }

        protected override BoundNode VisitParameter(BoundParameter n) => Hoisted(n.Parameter) ?? n;

        protected override BoundNode VisitThis(BoundThis n) =>
            o._thisField != null && !ReferenceEquals(n.Type, o._closed) ? o.Field(o._thisField) : n;

        protected override BoundNode VisitLocalDeclaration(BoundLocalDeclaration n)
        {
            if (!o._varFields.TryGetValue(n.Local, out var f)) return base.VisitLocalDeclaration(n);
            if (n.Initializer == null) return new BoundNoOp { Syntax = n.Syntax };
            return new BoundExpressionStatement(new BoundAssignment(o.Field(f), VisitExpr(n.Initializer)) { Syntax = n.Syntax, IsDiscarded = true }) { Syntax = n.Syntax };
        }

        private ImmutableArray<LocalSymbol> Keep(ImmutableArray<LocalSymbol> locals) =>
            locals.IsDefaultOrEmpty ? locals : locals.Where(l => !o._varFields.ContainsKey(l)).ToImmutableArray();

        protected override BoundNode VisitBlock(BoundBlock n)
        {
            var s = VisitStmts(n.Statements);
            return new BoundBlock(Keep(n.Locals), s) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitFor(BoundFor n)
        {
            var i = VisitStmtOpt(n.Initializer);
            var c = VisitExprOpt(n.Condition);
            var inc = VisitStmtOpt(n.Increment);
            var b = VisitStmt(n.Body);
            return new BoundFor(Keep(n.Locals), i, c, inc, b, n.BreakLabel, n.ContinueLabel) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitSequence(BoundSequence n)
        {
            var s = VisitStmts(n.SideEffects);
            var v = VisitExpr(n.Value);
            return new BoundSequence(Keep(n.Locals), s, v) { Syntax = n.Syntax };
        }
    }

    // ------------------------------------------------------------------ suspension points

    private sealed class TryInfo(LabelSymbol start)
    {
        public LabelSymbol Start { get; } = start;
        public List<(int State, LabelSymbol Target)> Entries { get; } = new();
        public void Register(int state, LabelSymbol target)
        {
            if (!Entries.Any(e => e.State == state)) Entries.Add((state, target));
        }
    }

    private int _stateCount;

    private sealed class StateRewriter : BoundTreeRewriter
    {
        private readonly StateMachineLowerer _o;
        private readonly Dictionary<BoundYieldReturn, LabelSymbol?> _disposeTargets;
        private readonly Stack<TryInfo> _tries = new();
        public TryInfo Root { get; } = new(new LabelSymbol("<root>"));

        public StateRewriter(StateMachineLowerer o, Dictionary<BoundYieldReturn, LabelSymbol?> disposeTargets)
        {
            _o = o;
            _disposeTargets = disposeTargets;
            _tries.Push(Root);
        }

        private int NewState(LabelSymbol label)
        {
            var k = _o._stateCount++;
            _tries.Peek().Register(k, label);
            return k;
        }

        protected override BoundNode VisitTry(BoundTry n)
        {
            var info = new TryInfo(new LabelSymbol("<try>"));
            _tries.Push(info);
            var body = (BoundBlock)Visit(n.Body)!;
            var catches = ImmutableArray.CreateBuilder<BoundCatch>(n.Catches.Length);
            foreach (var c in n.Catches) catches.Add(new BoundCatch(c.Local, c.ExceptionType, (BoundBlock)Visit(c.Body)!));
            var fin = n.Finally == null ? null : (BoundBlock)Visit(n.Finally)!;
            _tries.Pop();
            if (info.Entries.Count > 0)
            {
                var stmts = new List<BoundStatement> { new BoundLabel(info.Start) };
                stmts.AddRange(_o.DispatchStatements(info));
                stmts.AddRange(body.Statements);
                body = new BoundBlock(body.Locals, stmts.ToImmutableArray()) { Syntax = body.Syntax };
                foreach (var (state, _) in info.Entries) _tries.Peek().Register(state, info.Start);
            }
            return new BoundTry(body, catches.MoveToImmutable(), fin) { Syntax = n.Syntax };
        }

        protected override BoundNode VisitExpressionStatement(BoundExpressionStatement n)
        {
            switch (n.Expression)
            {
                case BoundAwait aw:
                    return _o.LowerAwait(aw, null, NewState);
                case BoundAssignment { Value: BoundAwait aw2 } asg:
                    return _o.LowerAwait(aw2, asg.Target, NewState);
            }
            return base.VisitExpressionStatement(n);
        }

        protected override BoundNode VisitReturn(BoundReturn n) => _o.LowerReturn(n);

        protected override BoundNode VisitYieldReturn(BoundYieldReturn n) =>
            _o.LowerYieldReturn(n, NewState, _disposeTargets.GetValueOrDefault(n));

        protected override BoundNode VisitAwait(BoundAwait n) => throw new InvalidOperationException("await survived spilling");
    }

    private IEnumerable<BoundStatement> DispatchStatements(TryInfo info)
    {
        foreach (var (state, target) in info.Entries)
            yield return new BoundConditionalGoto(
                new BoundBinary(BinaryOperatorKind.Equal, State(), IntLit(state), Bool, Int), target, jumpIfTrue: true);
    }

    // ------------------------------------------------------------------ async pieces

    private SynthesizedFieldSymbol? _builderField;
    private SynthesizedFieldSymbol? _retvalField;

    private void DeclareIteratorMembersEntry() => DeclareIteratorMembers();

    private void DeclareAsyncMembers(Collector col)
    {
        _cls.AddInterface(Wk("System.Runtime.CompilerServices.IAsyncStateMachine"));
        _builderField = NewField("<>t__builder", _builderType!);
        if (_payload != null) _retvalField = NewField("<>s__retval", _payload);
        NewMethod("SetStateMachine", Void, true, ("stateMachine", Wk("System.Runtime.CompilerServices.IAsyncStateMachine")));
        _ = col;
    }

    private void DeclareKindSpecificFields()
    {
        if (_kind == Kind.Iterator) DeclareIteratorFields();
    }

    private BoundExpression Builder() => Field(_builderField!);

    private MethodSymbol BuilderMethod(string name, int paramCount, int arity = 0) =>
        _builderType!.GetMembers(name).OfType<MethodSymbol>().First(m => m.Parameters.Length == paramCount && m.Arity == arity);

    private BoundStatement CallStmt(BoundExpression receiver, MethodSymbol m, params BoundExpression[] args) =>
        new BoundExpressionStatement(new BoundCall(receiver, m, args.ToImmutableArray()));

    private BoundStatement LowerAwait(BoundAwait aw, BoundExpression? target, Func<LabelSymbol, int> newState)
    {
        var info = aw.Info;
        var awaiterDef = _awaiterFields[info.AwaiterType.ToDisplayString()];
        BoundExpression Awaiter() => Field(awaiterDef);
        var resume = new LabelSymbol("<resume>");
        var k = newState(resume);

        var getAwaiter = info.GetAwaiter.IsStatic
            ? new BoundCall(null, info.GetAwaiter, [aw.Operand])
            : new BoundCall(aw.Operand, info.GetAwaiter, ImmutableArray<BoundExpression>.Empty);
        var isCompleted = info.IsCompletedGetter ?? info.IsCompleted!.GetMethod!;
        var critical = info.AwaiterType.AllInterfaces.Any(i => i.OriginalDefinition.MetadataName == "ICriticalNotifyCompletion");
        var awaitOn = _builderType!.GetMembers(critical ? "AwaitUnsafeOnCompleted" : "AwaitOnCompleted").OfType<MethodSymbol>().First(m => m.Arity == 2)
            .Construct([info.AwaiterType, _closed]);

        var stmts = new List<BoundStatement>
        {
            Assign(Awaiter(), getAwaiter),
            new BoundIf(
                new BoundUnary(UnaryOperatorKind.Not, new BoundCall(Awaiter(), isCompleted, ImmutableArray<BoundExpression>.Empty), Bool),
                Block([
                    Assign(State(), IntLit(k)),
                    CallStmt(Builder(), awaitOn, new BoundAddressOf(Awaiter()), new BoundAddressOf(new BoundLocal(_smLocal))),
                    new BoundGoto(_suspendLabel),
                ]), null),
            new BoundLabel(resume),
            Assign(State(), IntLit(-1)),
        };
        var result = new BoundCall(Awaiter(), info.GetResult, ImmutableArray<BoundExpression>.Empty);
        if (target != null) stmts.Add(Assign(target, result));
        else stmts.Add(new BoundExpressionStatement(result));
        stmts.Add(Assign(Awaiter(), new BoundDefaultValue(info.AwaiterType)));
        return Block(stmts);
    }

    private readonly LabelSymbol _exitLabel = new("<exit>");
    private LocalSymbol _smLocal = null!;

    private BoundStatement LowerReturn(BoundReturn r)
    {
        if (_kind == Kind.Iterator) return new BoundGoto(_endLabel);
        var stmts = new List<BoundStatement>();
        if (r.Expression != null && _retvalField != null) stmts.Add(Assign(Field(_retvalField), r.Expression));
        else if (r.Expression != null) stmts.Add(new BoundExpressionStatement(r.Expression));
        stmts.Add(new BoundGoto(_exitLabel));
        return Block(stmts);
    }

    private BoundBlock AssembleAsyncMoveNext(BoundBlock body, StateRewriter states)
    {
        var exType = Wk("System.Exception");
        var ex = new LocalSymbol("ex", exType, false, _method) { IsSynthesized = true };
        var tryBody = new List<BoundStatement>(DispatchStatements(states.Root)) { body };
        var catchBody = Block([
            Assign(State(), IntLit(-2)),
            CallStmt(Builder(), BuilderMethod("SetException", 1), new BoundLocal(ex)),
            new BoundReturn(null),
        ]);
        var setResult = _payload != null
            ? CallStmt(Builder(), BuilderMethod("SetResult", 1), Field(_retvalField!))
            : CallStmt(Builder(), BuilderMethod("SetResult", 0));
        var stmts = new List<BoundStatement>
        {
            Assign(new BoundLocal(_smLocal), SelfThis()),
            new BoundTry(Block(tryBody), [new BoundCatch(ex, exType, catchBody)], null),
            new BoundLabel(_exitLabel),
            Assign(State(), IntLit(-2)),
            setResult,
            new BoundLabel(_suspendLabel),
        };
        return new BoundBlock([_smLocal], stmts.ToImmutableArray());
    }

    private BoundBlock BuildAsyncStub()
    {
        var sm = new LocalSymbol("sm", _closed, false, _method) { IsSynthesized = true };
        BoundExpression Sm() => new BoundLocal(sm);
        var ctor = _closed.InstanceConstructors.First();
        var stmts = new List<BoundStatement>
        {
            Assign(Sm(), new BoundObjectCreation(ctor, _closed, ImmutableArray<BoundExpression>.Empty, ImmutableArray<BoundMemberInit>.Empty)),
            Assign(FieldOn(Sm(), _builderField!), new BoundCall(null, BuilderMethod("Create", 0), ImmutableArray<BoundExpression>.Empty)),
        };
        if (_thisField != null) stmts.Add(Assign(FieldOn(Sm(), _thisField), new BoundThis(_thisType!)));
        foreach (var p in _method.Parameters) stmts.Add(Assign(FieldOn(Sm(), (SynthesizedFieldSymbol)_varFields[p]), new BoundParameter(p)));
        stmts.Add(Assign(FieldOn(Sm(), _stateField), IntLit(-1)));
        var start = _builderType!.GetMembers("Start").OfType<MethodSymbol>().First(m => m.Arity == 1 && m.Parameters.Length == 1).Construct([_closed]);
        stmts.Add(CallStmt(FieldOn(Sm(), _builderField!), start, new BoundAddressOf(Sm())));
        var taskProp = _builderType.GetMembers("get_Task").OfType<MethodSymbol>().FirstOrDefault();
        if (taskProp != null) stmts.Add(new BoundReturn(new BoundCall(FieldOn(Sm(), _builderField!), taskProp, ImmutableArray<BoundExpression>.Empty)));
        else stmts.Add(new BoundReturn(null));
        return new BoundBlock([sm], stmts.ToImmutableArray());
    }
}
