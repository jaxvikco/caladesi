using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>
/// Async iterators reuse the async machinery: every <c>MoveNextAsync</c> starts a fresh <c>AsyncTaskMethodBuilder&lt;bool&gt;</c>
/// and resumes the state machine; <c>yield</c> completes that builder with <c>true</c>, the end of the body with <c>false</c>.
/// </summary>
internal sealed partial class StateMachineLowerer
{
#pragma warning disable IDE0370 // the null-forgiving operator is required: removing it gives CS8625
    private SynthesizedMethodSymbol _moveNextAsync = default!;
#pragma warning restore IDE0370
#pragma warning disable IDE0370 // the null-forgiving operator is required: removing it gives CS8625
    private SynthesizedMethodSymbol _disposeAsync = default!;
#pragma warning restore IDE0370

    private void DeclareAsyncIteratorMembers()
    {
        NamedTypeSymbol rt = (NamedTypeSymbol)_method.ReturnType.StripNullable();
        _isEnumerable = rt.OriginalDefinition.MetadataName is "IAsyncEnumerable`1";
        TypeSymbol t = _payload!;
        NamedTypeSymbol enumeratorT = Wk("System.Collections.Generic.IAsyncEnumerator`1").Construct(t);
        NamedTypeSymbol valueTaskBool = Wk("System.Threading.Tasks.ValueTask`1").Construct(Bool);
        NamedTypeSymbol valueTask = Wk("System.Threading.Tasks.ValueTask");
        _cls.AddInterface(Wk("System.Runtime.CompilerServices.IAsyncStateMachine"));
        if (_isEnumerable)
        {
            _cls.AddInterface(Wk("System.Collections.Generic.IAsyncEnumerable`1").Construct(t));
        }

        _cls.AddInterface(enumeratorT);

        _builderField = NewField("<>t__builder", _builderType!);
        _currentField = NewField("<>2__current", t);
        _disposingField = NewField("<>__disposing", Bool);
        _ = NewMethod("SetStateMachine", Void, true, ("stateMachine", Wk("System.Runtime.CompilerServices.IAsyncStateMachine")));
        _ = NewMethod("get_Current", t);
        _moveNextAsync = NewMethod("MoveNextAsync", valueTaskBool);
        _disposeAsync = NewMethod("DisposeAsync", valueTask);
        if (_isEnumerable)
        {
            _getEnumerator = NewMethod("GetAsyncEnumerator", enumeratorT, true, ("cancellationToken", Wk("System.Threading.CancellationToken")));
        }
    }

    private void BuildAsyncIteratorSupportBodies()
    {
        _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "get_Current")] =
            Block([new BoundReturn(Field(_currentField!))]);

        NamedTypeSymbol valueTaskBool = (NamedTypeSymbol)_moveNextAsync.ReturnType;
        NamedTypeSymbol valueTask = (NamedTypeSymbol)_disposeAsync.ReturnType;
        BoundLiteral boolTrueFalse(bool b)
        {
            return new BoundLiteral(new ConstantValue(b), Bool);
        }

        LocalSymbol sm = _smLocal;

        // MoveNextAsync: a finished machine yields false; otherwise run it until the next yield or the end
        MethodSymbol startM = _builderType!.GetMembers("Start").OfType<MethodSymbol>().First(m => m.Arity == 1 && m.Parameters.Length == 1).Construct([_closed]);
        MethodSymbol vtbCtorValue = valueTaskBool.InstanceConstructors.First(c => c.Parameters.Length == 1 && c.Parameters[0].Type.SpecialType == SpecialType.Boolean);
        MethodSymbol vtbCtorTask = valueTaskBool.InstanceConstructors.First(c => c.Parameters.Length == 1 && c.Parameters[0].Type.SpecialType != SpecialType.Boolean);
        MethodSymbol taskGetter = _builderType.GetMembers("get_Task").OfType<MethodSymbol>().First();
        _program.Bodies[_moveNextAsync] = new BoundBlock([sm], [
            new BoundIf(new BoundBinary(BinaryOperatorKind.Equal, State(), IntLit(-2), Bool, Int),
                new BoundReturn(new BoundObjectCreation(vtbCtorValue, valueTaskBool, [boolTrueFalse(false)], [])), null),
            Assign(Field(_builderField!), new BoundCall(null, BuilderMethod("Create", 0), [])),
            Assign(new BoundLocal(sm), SelfThis()),
            CallStmt(Builder(), startM, new BoundAddressOf(new BoundLocal(sm))),
            new BoundReturn(new BoundObjectCreation(vtbCtorTask, valueTaskBool,
                [new BoundCall(Builder(), taskGetter, [])], [])),
        ]);

        // DisposeAsync: let a suspended machine run its finally blocks
        MethodSymbol vtCtorTask = valueTask.InstanceConstructors.First(c => c.Parameters.Length == 1 && c.Parameters[0].Type.SpecialType != SpecialType.Boolean && c.Parameters[0].Type.Name == "Task");
        MethodSymbol asTask = valueTaskBool.GetMembers("AsTask").OfType<MethodSymbol>().First(m => m.Parameters.Length == 0);
        NamedTypeSymbol task = Wk("System.Threading.Tasks.Task");
        _program.Bodies[_disposeAsync] = Block([
            new BoundIf(new BoundBinary(BinaryOperatorKind.GreaterThanOrEqual, State(), IntLit(0), Bool, Int),
                Block([
                    Assign(Field(_disposingField!), boolTrueFalse(true)),
                    new BoundReturn(new BoundObjectCreation(vtCtorTask, valueTask,
                        [new BoundConversion(
                            new BoundCall(new BoundCall(SelfThis(), CF2(_moveNextAsync), []), asTask, []),
                            ConversionKind.ImplicitReference, task)],
                        [])),
                ]), null),
            Assign(State(), IntLit(-2)),
            new BoundReturn(new BoundDefaultValue(valueTask)),
        ]);

        if (!_isEnumerable)
        {
            return;
        }

        LocalSymbol r = new("r", _closed, false, _method) { IsSynthesized = true };
        BoundExpression R()
        {
            return new BoundLocal(r);
        }

        MethodSymbol ctor = _closed.InstanceConstructors.First();
        List<BoundStatement> fresh =
        [
            Assign(R(), new BoundObjectCreation(ctor, _closed, [], [])),
            Assign(FieldOn(R(), _stateField), IntLit(-1)),
        ];
        if (_thisField != null)
        {
            fresh.Add(Assign(FieldOn(R(), _thisField), Field(_thisField)));
        }

        List<BoundStatement> body =
        [
            new BoundIf(new BoundBinary(BinaryOperatorKind.Equal, State(), IntLit(-3), Bool, Int),
                Block([Assign(State(), IntLit(-1)), Assign(R(), SelfThis())]),
                Block(fresh)),
        ];
        foreach (ParameterSymbol p in _method.Parameters)
        {
            body.Add(Assign(FieldOn(R(), _varFields[p]), Field(_initialParams[p])));
        }

        body.Add(new BoundReturn(new BoundConversion(R(), ConversionKind.ImplicitReference, _getEnumerator!.ReturnType)));
        _program.Bodies[_getEnumerator!] = new BoundBlock([r], [.. body]);
    }
}
