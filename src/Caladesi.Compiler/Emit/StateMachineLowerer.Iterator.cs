using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

internal sealed partial class StateMachineLowerer
{
    private SynthesizedFieldSymbol? _currentField;
    private SynthesizedFieldSymbol? _disposingField;
    private bool _isEnumerable;
    private readonly Dictionary<ParameterSymbol, SynthesizedFieldSymbol> _initialParams = [];
    private SynthesizedMethodSymbol? _getEnumerator;

    private void DeclareIteratorMembers()
    {
        NamedTypeSymbol rt = (NamedTypeSymbol)_method.ReturnType.StripNullable();
        _isEnumerable = rt.OriginalDefinition.MetadataName is "IEnumerable`1" or "IEnumerable" or "IAsyncEnumerable`1";
        TypeSymbol t = _payload!;
        NamedTypeSymbol enumeratorT = Wk("System.Collections.Generic.IEnumerator`1").Construct(t);
        NamedTypeSymbol enumeratorObj = Wk("System.Collections.IEnumerator");
        NamedTypeSymbol objType = _c.GetSpecialType(SpecialType.Object);
        if (_isEnumerable)
        {
            _cls.AddInterface(Wk("System.Collections.Generic.IEnumerable`1").Construct(t));
            _cls.AddInterface(Wk("System.Collections.IEnumerable"));
        }
        _cls.AddInterface(enumeratorT);
        _cls.AddInterface(enumeratorObj);
        _cls.AddInterface(Wk("System.IDisposable"));

        _currentField = NewField("<>2__current", t);
        _disposingField = NewField("<>__disposing", Bool);
        _ = NewMethod("get_Current", t);
        SynthesizedMethodSymbol explicitCurrent = new(_cls, "System.Collections.IEnumerator.get_Current", MethodKind.Ordinary, Accessibility.Private, false, objType,
            [])
        {
            SynthesizedKind = SynthesizedBodyKind.Custom,
            Overrides = [enumeratorObj.GetMembers("get_Current").OfType<MethodSymbol>().First()],
        };
        _cls.AddMember(explicitCurrent);
        _ = NewMethod("Reset", Void);
        _ = NewMethod("Dispose", Void);
        if (_isEnumerable)
        {
            _getEnumerator = NewMethod("GetEnumerator", enumeratorT);
            SynthesizedMethodSymbol explicitGet = new(_cls, "System.Collections.IEnumerable.GetEnumerator", MethodKind.Ordinary, Accessibility.Private, false, enumeratorObj,
                [])
            {
                SynthesizedKind = SynthesizedBodyKind.Custom,
                Overrides = [Wk("System.Collections.IEnumerable").GetMembers("GetEnumerator").OfType<MethodSymbol>().First()],
            };
            _cls.AddMember(explicitGet);
        }
    }

    private void DeclareIteratorFields()
    {
        if (!_isEnumerable)
        {
            return;
        }

        foreach (ParameterSymbol p in _method.Parameters)
        {
            _initialParams[p] = NewField("<>3__" + p.Name, p.Type);
        }
    }

    private BoundBlock LowerYieldReturn(BoundYieldReturn n, Func<LabelSymbol, int> newState, LabelSymbol? disposeTarget)
    {
        LabelSymbol resume = new("<resume>");
        int k = newState(resume);
        return _kind == Kind.AsyncIterator
            ? Block([
                Assign(Field(_currentField!), n.Expression),
                Assign(State(), IntLit(k)),
                CallStmt(Builder(), BuilderMethod("SetResult", 1), new BoundLiteral(new ConstantValue(true), Bool)),
                new BoundGoto(_suspendLabel),
                new BoundLabel(resume),
                Assign(State(), IntLit(-1)),
                new BoundIf(Field(_disposingField!), new BoundGoto(disposeTarget ?? _exitLabel), null),
            ])
            : Block([
            Assign(Field(_currentField!), n.Expression),
            Assign(State(), IntLit(k)),
            new BoundReturn(new BoundLiteral(new ConstantValue(true), Bool)),
            new BoundLabel(resume),
            Assign(State(), IntLit(-1)),
            new BoundIf(Field(_disposingField!), new BoundGoto(disposeTarget ?? _endLabel), null),
        ]);
    }

    private BoundBlock AssembleIteratorMoveNext(BoundBlock body, StateRewriter states)
    {
        NamedTypeSymbol exType = Wk("System.Exception");
        LocalSymbol ex = new("ex", exType, false, _method) { IsSynthesized = true };
        List<BoundStatement> tryBody = [.. DispatchStatements(states.Root), body];
        BoundLiteral falseLit = new(new ConstantValue(false), Bool);
        List<BoundStatement> stmts =
        [
            new BoundIf(new BoundBinary(BinaryOperatorKind.Equal, State(), IntLit(-2), Bool, Int), new BoundReturn(falseLit), null),
            new BoundTry(Block(tryBody), [new BoundCatch(ex, exType, Block([Assign(State(), IntLit(-2)), new BoundThrow(null)]))], null),
            new BoundLabel(_endLabel),
            Assign(State(), IntLit(-2)),
            new BoundReturn(falseLit),
        ];
        return Block(stmts);
    }

    private BoundBlock BuildIteratorStub()
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
            Assign(FieldOn(Sm(), _stateField), IntLit(_isEnumerable ? -3 : -1)),
        ];
        if (_thisField != null)
        {
            stmts.Add(Assign(FieldOn(Sm(), _thisField), new BoundThis(_thisType!)));
        }

        foreach (ParameterSymbol p in _method.Parameters)
        {
            SynthesizedFieldSymbol target = _isEnumerable ? _initialParams[p] : _varFields[p];
            stmts.Add(Assign(FieldOn(Sm(), target), new BoundParameter(p)));
        }
        stmts.Add(new BoundReturn(new BoundConversion(Sm(), ConversionKind.ImplicitReference, _method.ReturnType.StripNullable())));
        return new BoundBlock([sm], [.. stmts]);
    }

    /// <summary>Bodies of the IEnumerator/IEnumerable plumbing members.</summary>
    private void BuildIteratorSupportBodies()
    {
        NamedTypeSymbol objType = _c.GetSpecialType(SpecialType.Object);
        _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "get_Current")] =
            Block([new BoundReturn(Field(_currentField!))]);
        BoundExpression asObject = _payload!.IsValueType || _payload is TypeParameterSymbol
            ? new BoundConversion(Field(_currentField!), ConversionKind.Boxing, objType)
            : new BoundConversion(Field(_currentField!), ConversionKind.ImplicitReference, objType);
        _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "System.Collections.IEnumerator.get_Current")] =
            Block([new BoundReturn(asObject)]);
        NamedTypeSymbol nse = Wk("System.NotSupportedException");
        _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "Reset")] =
            Block([new BoundThrow(new BoundObjectCreation(nse.InstanceConstructors.First(c => c.Parameters.Length == 0), nse, [], []))]);
        _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "Dispose")] = Block([
            new BoundIf(new BoundBinary(BinaryOperatorKind.GreaterThanOrEqual, State(), IntLit(0), Bool, Int),
                Block([
                    Assign(Field(_disposingField!), new BoundLiteral(new ConstantValue(true), Bool)),
                    new BoundExpressionStatement(new BoundCall(SelfThis(), CF2(_moveNext), [])),
                ]), null),
            Assign(State(), IntLit(-2)),
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
        SynthesizedMethodSymbol explicitGet = _cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "System.Collections.IEnumerable.GetEnumerator");
        _program.Bodies[explicitGet] = Block([new BoundReturn(new BoundConversion(
            new BoundCall(SelfThis(), CF2(_getEnumerator!), []), ConversionKind.ImplicitReference, explicitGet.ReturnType))]);
    }

    private MethodSymbol CF2(MethodSymbol def)
    {
        return SynthesizedClassFactory.ClosedMethod(_closed, def);
    }
}
