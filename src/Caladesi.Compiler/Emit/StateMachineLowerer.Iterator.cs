using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

internal sealed partial class StateMachineLowerer
{
    private SynthesizedFieldSymbol? _currentField;
    private SynthesizedFieldSymbol? _disposingField;
    private bool _isEnumerable;
    private readonly Dictionary<ParameterSymbol, SynthesizedFieldSymbol> _initialParams = new();
    private SynthesizedMethodSymbol? _getEnumerator;

    private void DeclareIteratorMembers()
    {
        var rt = (NamedTypeSymbol)_method.ReturnType.StripNullable();
        _isEnumerable = rt.OriginalDefinition.MetadataName is "IEnumerable`1" or "IEnumerable" or "IAsyncEnumerable`1";
        var t = _payload!;
        var enumeratorT = Wk("System.Collections.Generic.IEnumerator`1").Construct(t);
        var enumeratorObj = Wk("System.Collections.IEnumerator");
        var objType = _c.GetSpecialType(SpecialType.Object);
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
        NewMethod("get_Current", t);
        var explicitCurrent = new SynthesizedMethodSymbol(_cls, "System.Collections.IEnumerator.get_Current", MethodKind.Ordinary, Accessibility.Private, false, objType,
            ImmutableArray<ParameterSymbol>.Empty)
        {
            SynthesizedKind = SynthesizedBodyKind.Custom,
            Overrides = [enumeratorObj.GetMembers("get_Current").OfType<MethodSymbol>().First()],
        };
        _cls.AddMember(explicitCurrent);
        NewMethod("Reset", Void);
        NewMethod("Dispose", Void);
        if (_isEnumerable)
        {
            _getEnumerator = NewMethod("GetEnumerator", enumeratorT);
            var explicitGet = new SynthesizedMethodSymbol(_cls, "System.Collections.IEnumerable.GetEnumerator", MethodKind.Ordinary, Accessibility.Private, false, enumeratorObj,
                ImmutableArray<ParameterSymbol>.Empty)
            {
                SynthesizedKind = SynthesizedBodyKind.Custom,
                Overrides = [Wk("System.Collections.IEnumerable").GetMembers("GetEnumerator").OfType<MethodSymbol>().First()],
            };
            _cls.AddMember(explicitGet);
        }
    }

    private void DeclareIteratorFields()
    {
        if (!_isEnumerable) return;
        foreach (var p in _method.Parameters) _initialParams[p] = NewField("<>3__" + p.Name, p.Type);
    }

    private MethodSymbol CMethod(string name) =>
        _closed.GetMembers(name).OfType<MethodSymbol>().First();

    private BoundStatement LowerYieldReturn(BoundYieldReturn n, Func<LabelSymbol, int> newState, LabelSymbol? disposeTarget)
    {
        var resume = new LabelSymbol("<resume>");
        var k = newState(resume);
        if (_kind == Kind.AsyncIterator)
            return Block([
                Assign(Field(_currentField!), n.Expression),
                Assign(State(), IntLit(k)),
                CallStmt(Builder(), BuilderMethod("SetResult", 1), new BoundLiteral(new ConstantValue(true), Bool)),
                new BoundGoto(_suspendLabel),
                new BoundLabel(resume),
                Assign(State(), IntLit(-1)),
                new BoundIf(Field(_disposingField!), new BoundGoto(disposeTarget ?? _exitLabel), null),
            ]);
        return Block([
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
        var exType = Wk("System.Exception");
        var ex = new LocalSymbol("ex", exType, false, _method) { IsSynthesized = true };
        var tryBody = new List<BoundStatement>(DispatchStatements(states.Root)) { body };
        var falseLit = new BoundLiteral(new ConstantValue(false), Bool);
        var stmts = new List<BoundStatement>
        {
            new BoundIf(new BoundBinary(BinaryOperatorKind.Equal, State(), IntLit(-2), Bool, Int), new BoundReturn(falseLit), null),
            new BoundTry(Block(tryBody), [new BoundCatch(ex, exType, Block([Assign(State(), IntLit(-2)), new BoundThrow(null)]))], null),
            new BoundLabel(_endLabel),
            Assign(State(), IntLit(-2)),
            new BoundReturn(falseLit),
        };
        return Block(stmts);
    }

    private BoundBlock BuildIteratorStub()
    {
        var sm = new LocalSymbol("sm", _closed, false, _method) { IsSynthesized = true };
        BoundExpression Sm() => new BoundLocal(sm);
        var ctor = _closed.InstanceConstructors.First();
        var stmts = new List<BoundStatement>
        {
            Assign(Sm(), new BoundObjectCreation(ctor, _closed, ImmutableArray<BoundExpression>.Empty, ImmutableArray<BoundMemberInit>.Empty)),
            Assign(FieldOn(Sm(), _stateField), IntLit(_isEnumerable ? -3 : -1)),
        };
        if (_thisField != null) stmts.Add(Assign(FieldOn(Sm(), _thisField), new BoundThis(_thisType!)));
        foreach (var p in _method.Parameters)
        {
            var target = _isEnumerable ? _initialParams[p] : (SynthesizedFieldSymbol)_varFields[p];
            stmts.Add(Assign(FieldOn(Sm(), target), new BoundParameter(p)));
        }
        stmts.Add(new BoundReturn(new BoundConversion(Sm(), ConversionKind.ImplicitReference, _method.ReturnType.StripNullable())));
        return new BoundBlock([sm], stmts.ToImmutableArray());
    }

    /// <summary>Bodies of the IEnumerator/IEnumerable plumbing members.</summary>
    private void BuildIteratorSupportBodies()
    {
        var objType = _c.GetSpecialType(SpecialType.Object);
        _program.Bodies[(SynthesizedMethodSymbol)_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "get_Current")] =
            Block([new BoundReturn(Field(_currentField!))]);
        BoundExpression asObject = _payload!.IsValueType || _payload is TypeParameterSymbol
            ? new BoundConversion(Field(_currentField!), ConversionKind.Boxing, objType)
            : new BoundConversion(Field(_currentField!), ConversionKind.ImplicitReference, objType);
        _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "System.Collections.IEnumerator.get_Current")] =
            Block([new BoundReturn(asObject)]);
        var nse = Wk("System.NotSupportedException");
        _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "Reset")] =
            Block([new BoundThrow(new BoundObjectCreation(nse.InstanceConstructors.First(c => c.Parameters.Length == 0), nse, ImmutableArray<BoundExpression>.Empty, ImmutableArray<BoundMemberInit>.Empty))]);
        _program.Bodies[_cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "Dispose")] = Block([
            new BoundIf(new BoundBinary(BinaryOperatorKind.GreaterThanOrEqual, State(), IntLit(0), Bool, Int),
                Block([
                    Assign(Field(_disposingField!), new BoundLiteral(new ConstantValue(true), Bool)),
                    new BoundExpressionStatement(new BoundCall(SelfThis(), CF2(_moveNext), ImmutableArray<BoundExpression>.Empty)),
                ]), null),
            Assign(State(), IntLit(-2)),
        ]);
        if (!_isEnumerable) return;

        var r = new LocalSymbol("r", _closed, false, _method) { IsSynthesized = true };
        BoundExpression R() => new BoundLocal(r);
        var ctor = _closed.InstanceConstructors.First();
        var fresh = new List<BoundStatement>
        {
            Assign(R(), new BoundObjectCreation(ctor, _closed, ImmutableArray<BoundExpression>.Empty, ImmutableArray<BoundMemberInit>.Empty)),
            Assign(FieldOn(R(), _stateField), IntLit(-1)),
        };
        if (_thisField != null) fresh.Add(Assign(FieldOn(R(), _thisField), Field(_thisField)));
        var body = new List<BoundStatement>
        {
            new BoundIf(new BoundBinary(BinaryOperatorKind.Equal, State(), IntLit(-3), Bool, Int),
                Block([Assign(State(), IntLit(-1)), Assign(R(), SelfThis())]),
                Block(fresh)),
        };
        foreach (var p in _method.Parameters) body.Add(Assign(FieldOn(R(), (SynthesizedFieldSymbol)_varFields[p]), Field(_initialParams[p])));
        body.Add(new BoundReturn(new BoundConversion(R(), ConversionKind.ImplicitReference, _getEnumerator!.ReturnType)));
        _program.Bodies[_getEnumerator!] = new BoundBlock([r], body.ToImmutableArray());
        var explicitGet = _cls.Members.OfType<SynthesizedMethodSymbol>().First(m => m.Name == "System.Collections.IEnumerable.GetEnumerator");
        _program.Bodies[explicitGet] = Block([new BoundReturn(new BoundConversion(
            new BoundCall(SelfThis(), CF2(_getEnumerator!), ImmutableArray<BoundExpression>.Empty), ConversionKind.ImplicitReference, explicitGet.ReturnType))]);
    }

    private MethodSymbol CF2(MethodSymbol def) => SynthesizedClassFactory.ClosedMethod(_closed, def);
}
