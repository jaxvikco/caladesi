using System.Collections.Immutable;
using System.Reflection.Metadata;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>Emits the IL for compiler-synthesized members: auto accessors, record members and union members.</summary>
internal sealed class SynthesizedBodyEmitter
{
    private readonly EmitModule _m;
    private readonly MethodSymbol _method;
    private readonly ILBuilder _il;
    private readonly SynthesizedBodyKind _kind;
    private readonly NamedTypeSymbol _type;

    public SynthesizedBodyEmitter(EmitModule module, MethodSymbol method, ILBuilder il)
    {
        _m = module;
        _method = method;
        _il = il;
        _type = (NamedTypeSymbol)method.ContainingSymbol!;
        _kind = method switch
        {
            SourceMethodSymbol s => s.SynthesizedKind,
            SynthesizedMethodSymbol y => y.SynthesizedKind,
            _ => SynthesizedBodyKind.None,
        };
    }

    private Compilation C => _m.Compilation;
    private bool IsStruct => _type.TypeKind == TypeKind.Struct;
    private NamedTypeSymbol Self => _type.IsGenericType ? _type.Construct(_type.TypeArguments) : _type;
    private ImmutableArray<SourcePropertySymbol> Props => ((SourceRecordSymbol)_type).Properties;

    public void Emit()
    {
        switch (_kind)
        {
            case SynthesizedBodyKind.AutoGetter: AutoGetter(); break;
            case SynthesizedBodyKind.AutoSetter: AutoSetter(); break;
            case SynthesizedBodyKind.RecordConstructor: RecordCtor(); break;
            case SynthesizedBodyKind.RecordCopyConstructor: RecordCopyCtor(); break;
            case SynthesizedBodyKind.RecordClone: RecordClone(); break;
            case SynthesizedBodyKind.RecordEqualityContract: EqualityContract(); break;
            case SynthesizedBodyKind.RecordEquals: RecordEquals(); break;
            case SynthesizedBodyKind.RecordEqualsObject: RecordEqualsObject(); break;
            case SynthesizedBodyKind.RecordGetHashCode: RecordHash(); break;
            case SynthesizedBodyKind.RecordToString: RecordToString(); break;
            case SynthesizedBodyKind.RecordEqualsOperator: RecordOpEquals(); break;
            case SynthesizedBodyKind.RecordNotEqualsOperator: RecordOpNotEquals(); break;
            case SynthesizedBodyKind.UnionConstructor: UnionCtor(); break;
            case SynthesizedBodyKind.UnionImplicit: UnionImplicit(); break;
            case SynthesizedBodyKind.UnionToString: UnionToString(); break;
            case SynthesizedBodyKind.UnionEqualsObject: UnionEqualsObject(); break;
            case SynthesizedBodyKind.UnionGetHashCode: UnionHash(); break;
            default:
                _il.Op(ILOpCode.Ldnull, 1);
                _il.Op(ILOpCode.Throw, -1);
                break;
        }
    }

    // ------------------------------------------------------------------ helpers

    private MethodSymbol Find(NamedTypeSymbol t, string name, int paramCount, bool isStatic) =>
        t.GetMembers(name).OfType<MethodSymbol>().First(x => x.Parameters.Length == paramCount && x.IsStatic == isStatic);

    private void CallBaseCtor()
    {
        if (IsStruct) return;
        _il.Ldarg(0);
        var b = _type.BaseType!;
        var ctor = b.InstanceConstructors.First(c => c.Parameters.Length == 0);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(ctor), 1, 0);
    }

    private FieldSymbol Backing(SourcePropertySymbol p) => p.BackingField!;

    private void LoadField(FieldSymbol f) => _il.Token(ILOpCode.Ldfld, _m.GetFieldEntity(f), 0);

    private TypeSymbol FieldClr(SourcePropertySymbol p) => _m.ClrType(p.Type);

    private NamedTypeSymbol EqComparer(TypeSymbol t) =>
        C.GetWellKnownType("System.Collections.Generic.EqualityComparer`1")!.Construct(t);

    /// <summary>Stack: a, b -> bool, using EqualityComparer&lt;T&gt;.Default.Equals.</summary>
    private void EmitEqualsOfType(TypeSymbol t, LocalSlot a, LocalSlot b)
    {
        var cmp = EqComparer(t);
        var getDefault = cmp.GetMembers("get_Default").OfType<MethodSymbol>().First();
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(getDefault), 0, 1);
        _il.Ldloc(a.Index);
        _il.Ldloc(b.Index);
        var eq = cmp.GetMembers("Equals").OfType<MethodSymbol>().First(x => x.Parameters.Length == 2);
        _il.Call(ILOpCode.Callvirt, _m.GetMethodEntity(eq), 3, 1);
    }

    // ------------------------------------------------------------------ accessors

    private SourcePropertySymbol? AssocProp => (_method as SourceMethodSymbol)?.AssociatedProperty as SourcePropertySymbol;

    private void AutoGetter()
    {
        var f = Backing(AssocProp!);
        if (f.IsStatic) _il.Token(ILOpCode.Ldsfld, _m.GetFieldEntity(f), 1);
        else { _il.Ldarg(0); LoadField(f); }
        _il.Op(ILOpCode.Ret, 0);
    }

    private void AutoSetter()
    {
        var f = Backing(AssocProp!);
        if (f.IsStatic) { _il.Ldarg(0); _il.Token(ILOpCode.Stsfld, _m.GetFieldEntity(f), -1); }
        else { _il.Ldarg(0); _il.Ldarg(1); _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(f), -2); }
        _il.Op(ILOpCode.Ret, 0);
    }

    // ------------------------------------------------------------------ records

    private void RecordCtor()
    {
        CallBaseCtor();
        var props = Props;
        for (var i = 0; i < props.Length; i++)
        {
            _il.Ldarg(0);
            _il.Ldarg(i + 1);
            _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(Backing(props[i])), -2);
        }
        _il.Op(ILOpCode.Ret, 0);
    }

    private void RecordCopyCtor()
    {
        CallBaseCtor();
        foreach (var p in Props)
        {
            _il.Ldarg(0);
            _il.Ldarg(1);
            LoadField(Backing(p));
            _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(Backing(p)), -2);
        }
        _il.Op(ILOpCode.Ret, 0);
    }

    private void RecordClone()
    {
        _il.Ldarg(0);
        var copy = _type.InstanceConstructors.OfType<SourceMethodSymbol>().First(c => c.SynthesizedKind == SynthesizedBodyKind.RecordCopyConstructor);
        _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(copy), 1, 1);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void EqualityContract()
    {
        _il.Token(ILOpCode.Ldtoken, _m.GetTypeEntity(Self), 1);
        var typeT = C.GetSpecialType(SpecialType.Type);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(Find(typeT, "GetTypeFromHandle", 1, true)), 1, 1);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void RecordEquals()
    {
        var props = Props;
        var f = _il.NewLabel();
        if (!IsStruct)
        {
            _il.Ldarg(1);
            _il.Branch(ILOpCode.Brfalse, f);
        }
        var la = _il.NewLocal(_m.ClrType(C.GetSpecialType(SpecialType.Object)));
        foreach (var p in props)
        {
            var t = FieldClr(p);
            var a = _il.NewLocal(t);
            var b = _il.NewLocal(t);
            _il.Ldarg(0); LoadField(Backing(p)); _il.Stloc(a.Index);
            _il.Ldarg(1); LoadField(Backing(p)); _il.Stloc(b.Index);
            EmitEqualsOfType(t, a, b);
            _il.Branch(ILOpCode.Brfalse, f);
        }
        _ = la;
        _il.LdcI4(1);
        _il.Op(ILOpCode.Ret, -1);
        _il.Mark(f);
        _il.ResetDepth();
        _il.LdcI4(0);
        _il.Op(ILOpCode.Ret, -1);
    }

    private MethodSymbol TypedEquals() =>
        _type.GetMembers("Equals").OfType<SourceMethodSymbol>().First(x => x.SynthesizedKind == SynthesizedBodyKind.RecordEquals);

    private void RecordEqualsObject()
    {
        var f = _il.NewLabel();
        var self = _m.GetTypeEntity(Self);
        if (IsStruct)
        {
            _il.Ldarg(1);
            _il.Token(ILOpCode.Isinst, self, 0);
            _il.Branch(ILOpCode.Brfalse, f);
            _il.Ldarg(0);
            _il.Ldarg(1);
            _il.Token(ILOpCode.Unbox_any, self, 0);
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(TypedEquals()), 2, 1);
        }
        else
        {
            _il.Ldarg(0);
            _il.Ldarg(1);
            _il.Token(ILOpCode.Isinst, self, 0);
            _il.Call(ILOpCode.Callvirt, _m.GetMethodEntity(TypedEquals()), 2, 1);
        }
        _il.Op(ILOpCode.Ret, 0);
        _il.Mark(f);
        _il.ResetDepth();
        _il.LdcI4(0);
        _il.Op(ILOpCode.Ret, -1);
    }

    private void RecordHash()
    {
        var h = _il.NewLocal(C.GetSpecialType(SpecialType.Int32));
        _il.LdcI4(0);
        _il.Stloc(h.Index);
        foreach (var p in Props)
        {
            var t = FieldClr(p);
            var cmp = EqComparer(t);
            _il.Ldloc(h.Index);
            _il.LdcI4(-1521134295);
            _il.Op(ILOpCode.Mul, -1);
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(cmp.GetMembers("get_Default").OfType<MethodSymbol>().First()), 0, 1);
            _il.Ldarg(0);
            LoadField(Backing(p));
            var gh = cmp.GetMembers("GetHashCode").OfType<MethodSymbol>().First(x => x.Parameters.Length == 1);
            _il.Call(ILOpCode.Callvirt, _m.GetMethodEntity(gh), 2, 1);
            _il.Op(ILOpCode.Add, -1);
            _il.Stloc(h.Index);
        }
        _il.Ldloc(h.Index);
        _il.Op(ILOpCode.Ret, -1);
    }

    private void RecordToString()
    {
        var props = Props;
        var strT = C.GetSpecialType(SpecialType.String);
        if (props.Length == 0)
        {
            _il.LdStr(_m.Metadata.GetOrAddUserString(_type.Name + " { }"));
            _il.Op(ILOpCode.Ret, -1);
            return;
        }
        var objT = C.GetSpecialType(SpecialType.Object);
        var n = props.Length * 2 + 1;
        _il.LdcI4(n);
        _il.Token(ILOpCode.Newarr, _m.GetTypeEntity(objT), 0);
        var idx = 0;
        void Str(string s)
        {
            _il.Op(ILOpCode.Dup, 1);
            _il.LdcI4(idx++);
            _il.LdStr(_m.Metadata.GetOrAddUserString(s));
            _il.Op(ILOpCode.Stelem_ref, -3);
        }
        for (var i = 0; i < props.Length; i++)
        {
            Str((i == 0 ? _type.Name + " { " : ", ") + props[i].Name + " = ");
            _il.Op(ILOpCode.Dup, 1);
            _il.LdcI4(idx++);
            _il.Ldarg(0);
            LoadField(Backing(props[i]));
            var t = FieldClr(props[i]);
            if (!(t.IsReferenceType && t is not TypeParameterSymbol)) _il.Token(ILOpCode.Box, _m.GetTypeEntity(t), 0);
            _il.Op(ILOpCode.Stelem_ref, -3);
        }
        Str(" }");
        var concat = strT.GetMembers("Concat").OfType<MethodSymbol>().First(x => x.IsStatic && x.Parameters.Length == 1 && x.Parameters[0].Type is ArrayTypeSymbol);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(concat), 1, 1);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void RecordOpEquals()
    {
        var t = Self;
        if (IsStruct)
        {
            _il.Ldarga(0);
            _il.Ldarg(1);
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(TypedEquals()), 2, 1);
            _il.Op(ILOpCode.Ret, 0);
            return;
        }
        var yes = _il.NewLabel();
        var no = _il.NewLabel();
        _il.Ldarg(0);
        _il.Ldarg(1);
        _il.Branch(ILOpCode.Beq, yes);
        _il.Ldarg(0);
        _il.Branch(ILOpCode.Brfalse, no);
        _il.Ldarg(0);
        _il.Ldarg(1);
        _il.Call(ILOpCode.Callvirt, _m.GetMethodEntity(TypedEquals()), 2, 1);
        _il.Op(ILOpCode.Ret, 0);
        _il.Mark(yes);
        _il.ResetDepth();
        _il.LdcI4(1);
        _il.Op(ILOpCode.Ret, -1);
        _il.Mark(no);
        _il.ResetDepth();
        _il.LdcI4(0);
        _il.Op(ILOpCode.Ret, -1);
        _ = t;
    }

    private void RecordOpNotEquals()
    {
        var eq = _type.GetMembers("op_Equality").OfType<MethodSymbol>().First();
        _il.Ldarg(0);
        _il.Ldarg(1);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(eq), 2, 1);
        _il.LdcI4(0);
        _il.Op(ILOpCode.Ceq, -1);
        _il.Op(ILOpCode.Ret, 0);
    }

    // ------------------------------------------------------------------ unions

    private FieldSymbol Tag => _type.GetMembers("Tag").OfType<FieldSymbol>().First();
    private FieldSymbol Value => _type.GetMembers("Value").OfType<FieldSymbol>().First();

    private void UnionCtor()
    {
        var idx = ((SourceMethodSymbol)_method).UnionCaseIndex;
        _il.Ldarg(0);
        _il.LdcI4(idx + 1);
        _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(Tag), -2);
        _il.Ldarg(0);
        _il.Ldarg(1);
        var t = _m.ClrType(_method.Parameters[0].Type);
        if (!(t.IsReferenceType && t is not TypeParameterSymbol)) _il.Token(ILOpCode.Box, _m.GetTypeEntity(t), 0);
        _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(Value), -2);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void UnionImplicit()
    {
        var ctor = _type.InstanceConstructors.OfType<SourceMethodSymbol>().First(c => c.UnionCaseIndex == ((SourceMethodSymbol)_method).UnionCaseIndex);
        _il.Ldarg(0);
        _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(ctor), 1, 1);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void UnionToString()
    {
        var objT = C.GetSpecialType(SpecialType.Object);
        var notNull = _il.NewLabel();
        _il.Ldarg(0);
        LoadField(Value);
        _il.Op(ILOpCode.Dup, 1);
        _il.Branch(ILOpCode.Brtrue, notNull);
        _il.Op(ILOpCode.Pop, -1);
        _il.LdStr(_m.Metadata.GetOrAddUserString(""));
        _il.Op(ILOpCode.Ret, -1);
        _il.Mark(notNull);
        _il.ResetDepth(1);
        _il.Call(ILOpCode.Callvirt, _m.GetMethodEntity(Find(objT, "ToString", 0, false)), 1, 1);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void UnionEqualsObject()
    {
        var no = _il.NewLabel();
        var objT = C.GetSpecialType(SpecialType.Object);
        var self = _m.GetTypeEntity(Self);
        var o = _il.NewLocal(Self);
        _il.Ldarg(1);
        _il.Token(ILOpCode.Isinst, self, 0);
        _il.Branch(ILOpCode.Brfalse, no);
        _il.Ldarg(1);
        _il.Token(ILOpCode.Unbox_any, self, 0);
        _il.Stloc(o.Index);
        _il.Ldarg(0);
        LoadField(Tag);
        _il.Ldloca(o.Index);
        LoadField(Tag);
        _il.Branch(ILOpCode.Bne_un, no);
        _il.Ldarg(0);
        LoadField(Value);
        _il.Ldloca(o.Index);
        LoadField(Value);
        var eq = objT.GetMembers("Equals").OfType<MethodSymbol>().First(x => x.IsStatic && x.Parameters.Length == 2);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(eq), 2, 1);
        _il.Op(ILOpCode.Ret, 0);
        _il.Mark(no);
        _il.ResetDepth();
        _il.LdcI4(0);
        _il.Op(ILOpCode.Ret, -1);
    }

    private void UnionHash()
    {
        var objT = C.GetSpecialType(SpecialType.Object);
        var isNull = _il.NewLabel();
        _il.Ldarg(0);
        LoadField(Tag);
        _il.Ldarg(0);
        LoadField(Value);
        _il.Op(ILOpCode.Dup, 1);
        _il.Branch(ILOpCode.Brfalse, isNull);
        _il.Call(ILOpCode.Callvirt, _m.GetMethodEntity(Find(objT, "GetHashCode", 0, false)), 1, 1);
        _il.Op(ILOpCode.Xor, -1);
        _il.Op(ILOpCode.Ret, 0);
        _il.Mark(isNull);
        _il.ResetDepth(3);
        _il.Op(ILOpCode.Pop, -1);
        _il.Op(ILOpCode.Ret, -1);
    }
}
