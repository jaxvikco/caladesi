using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>Emits the IL for compiler-synthesized members: auto accessors, record members and union members.</summary>
internal sealed class SynthesizedBodyEmitter(EmitModule module, MethodSymbol method, ILBuilder il)
{
    private readonly EmitModule _m = module;
    private readonly MethodSymbol _method = method;
    private readonly ILBuilder _il = il;
    private readonly SynthesizedBodyKind _kind = method switch
    {
        SourceMethodSymbol s => s.SynthesizedKind,
        SynthesizedMethodSymbol y => y.SynthesizedKind,
        _ => SynthesizedBodyKind.None,
    };
    private readonly NamedTypeSymbol _type = (NamedTypeSymbol)method.ContainingSymbol!;

    private Compilation C => _m.Compilation;
    private bool IsStruct => _type.TypeKind == TypeKind.Struct;
    private NamedTypeSymbol Self => _type.IsGenericType ? _type.Construct(_type.TypeArguments) : _type;
    private ImmutableArray<SourcePropertySymbol> Props => [.. _type.GetMembers().OfType<SourcePropertySymbol>().Where(p => p.BackingField != null)];

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

    private static MethodSymbol Find(NamedTypeSymbol t, string name, int paramCount, bool isStatic)
    {
        return t.GetMembers(name).OfType<MethodSymbol>().First(x => x.Parameters.Length == paramCount && x.IsStatic == isStatic);
    }

    private void CallBaseCtor()
    {
        if (IsStruct)
        {
            return;
        }

        _il.Ldarg(0);
        NamedTypeSymbol b = _type.BaseType!;
        MethodSymbol ctor = b.InstanceConstructors.First(c => c.Parameters.Length == 0);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(ctor), 1, 0);
    }

    private static SourceFieldSymbol Backing(SourcePropertySymbol p)
    {
        return p.BackingField!;
    }

    private void LoadField(FieldSymbol f)
    {
        _il.Token(ILOpCode.Ldfld, _m.GetFieldEntity(f), 0);
    }

    private TypeSymbol FieldClr(SourcePropertySymbol p)
    {
        return _m.ClrType(p.Type);
    }

    private NamedTypeSymbol EqComparer(TypeSymbol t)
    {
        return C.GetWellKnownType("System.Collections.Generic.EqualityComparer`1")!.Construct(t);
    }

    /// <summary>Stack: a, b -> bool, using EqualityComparer&lt;T&gt;.Default.Equals.</summary>
    private void EmitEqualsOfType(TypeSymbol t, LocalSlot a, LocalSlot b)
    {
        NamedTypeSymbol cmp = EqComparer(t);
        MethodSymbol getDefault = cmp.GetMembers("get_Default").OfType<MethodSymbol>().First();
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(getDefault), 0, 1);
        _il.Ldloc(a.Index);
        _il.Ldloc(b.Index);
        MethodSymbol eq = cmp.GetMembers("Equals").OfType<MethodSymbol>().First(x => x.Parameters.Length == 2);
        _il.Call(ILOpCode.Callvirt, _m.GetMethodEntity(eq), 3, 1);
    }

    // ------------------------------------------------------------------ accessors

    private SourcePropertySymbol? AssocProp => (_method as SourceMethodSymbol)?.AssociatedProperty as SourcePropertySymbol;

    private void AutoGetter()
    {
        FieldSymbol f = Backing(AssocProp!);
        if (f.IsStatic)
        {
            _il.Token(ILOpCode.Ldsfld, _m.GetFieldEntity(f), 1);
        }
        else { _il.Ldarg(0); LoadField(f); }
        _il.Op(ILOpCode.Ret, 0);
    }

    private void AutoSetter()
    {
        FieldSymbol f = Backing(AssocProp!);
        if (f.IsStatic) { _il.Ldarg(0); _il.Token(ILOpCode.Stsfld, _m.GetFieldEntity(f), -1); }
        else { _il.Ldarg(0); _il.Ldarg(1); _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(f), -2); }
        _il.Op(ILOpCode.Ret, 0);
    }

    // ------------------------------------------------------------------ records

    private void RecordCtor()
    {
        CallBaseCtor();
        ImmutableArray<SourcePropertySymbol> props = Props;
        for (int i = 0; i < props.Length; i++)
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
        foreach (SourcePropertySymbol p in Props)
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
        SourceMethodSymbol copy = _type.InstanceConstructors.OfType<SourceMethodSymbol>().First(c => c.SynthesizedKind == SynthesizedBodyKind.RecordCopyConstructor);
        _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(copy), 1, 1);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void EqualityContract()
    {
        _il.Token(ILOpCode.Ldtoken, _m.GetTypeEntity(Self), 1);
        NamedTypeSymbol typeT = C.GetSpecialType(SpecialType.Type);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(Find(typeT, "GetTypeFromHandle", 1, true)), 1, 1);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void RecordEquals()
    {
        ImmutableArray<SourcePropertySymbol> props = Props;
        LabelHandle f = _il.NewLabel();
        if (!IsStruct)
        {
            _il.Ldarg(1);
            _il.Branch(ILOpCode.Brfalse, f);
        }
        LocalSlot la = _il.NewLocal(_m.ClrType(C.GetSpecialType(SpecialType.Object)));
        foreach (SourcePropertySymbol p in props)
        {
            TypeSymbol t = FieldClr(p);
            LocalSlot a = _il.NewLocal(t);
            LocalSlot b = _il.NewLocal(t);
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

    private SourceMethodSymbol TypedEquals()
    {
        return _type.GetMembers("Equals").OfType<SourceMethodSymbol>().First(x => x.SynthesizedKind == SynthesizedBodyKind.RecordEquals);
    }

    private void RecordEqualsObject()
    {
        LabelHandle f = _il.NewLabel();
        EntityHandle self = _m.GetTypeEntity(Self);
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
        LocalSlot h = _il.NewLocal(C.GetSpecialType(SpecialType.Int32));
        _il.LdcI4(0);
        _il.Stloc(h.Index);
        foreach (SourcePropertySymbol p in Props)
        {
            TypeSymbol t = FieldClr(p);
            NamedTypeSymbol cmp = EqComparer(t);
            _il.Ldloc(h.Index);
            _il.LdcI4(-1521134295);
            _il.Op(ILOpCode.Mul, -1);
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(cmp.GetMembers("get_Default").OfType<MethodSymbol>().First()), 0, 1);
            _il.Ldarg(0);
            LoadField(Backing(p));
            MethodSymbol gh = cmp.GetMembers("GetHashCode").OfType<MethodSymbol>().First(x => x.Parameters.Length == 1);
            _il.Call(ILOpCode.Callvirt, _m.GetMethodEntity(gh), 2, 1);
            _il.Op(ILOpCode.Add, -1);
            _il.Stloc(h.Index);
        }
        _il.Ldloc(h.Index);
        _il.Op(ILOpCode.Ret, -1);
    }

    private void RecordToString()
    {
        ImmutableArray<SourcePropertySymbol> props = Props;
        NamedTypeSymbol strT = C.GetSpecialType(SpecialType.String);
        if (props.Length == 0)
        {
            _il.LdStr(_m.Metadata.GetOrAddUserString(_type.Name + " { }"));
            _il.Op(ILOpCode.Ret, -1);
            return;
        }
        NamedTypeSymbol objT = C.GetSpecialType(SpecialType.Object);
        int n = (props.Length * 2) + 1;
        _il.LdcI4(n);
        _il.Token(ILOpCode.Newarr, _m.GetTypeEntity(objT), 0);
        int idx = 0;
        void Str(string s)
        {
            _il.Op(ILOpCode.Dup, 1);
            _il.LdcI4(idx++);
            _il.LdStr(_m.Metadata.GetOrAddUserString(s));
            _il.Op(ILOpCode.Stelem_ref, -3);
        }
        for (int i = 0; i < props.Length; i++)
        {
            Str((i == 0 ? _type.Name + " { " : ", ") + props[i].Name + " = ");
            _il.Op(ILOpCode.Dup, 1);
            _il.LdcI4(idx++);
            _il.Ldarg(0);
            LoadField(Backing(props[i]));
            TypeSymbol t = FieldClr(props[i]);
            if (!(t.IsReferenceType && t is not TypeParameterSymbol))
            {
                _il.Token(ILOpCode.Box, _m.GetTypeEntity(t), 0);
            }

            _il.Op(ILOpCode.Stelem_ref, -3);
        }
        Str(" }");
        MethodSymbol concat = strT.GetMembers("Concat").OfType<MethodSymbol>().First(x => x.IsStatic && x.Parameters.Length == 1 && x.Parameters[0].Type is ArrayTypeSymbol);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(concat), 1, 1);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void RecordOpEquals()
    {
        NamedTypeSymbol t = Self;
        if (IsStruct)
        {
            _il.Ldarga(0);
            _il.Ldarg(1);
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(TypedEquals()), 2, 1);
            _il.Op(ILOpCode.Ret, 0);
            return;
        }
        LabelHandle yes = _il.NewLabel();
        LabelHandle no = _il.NewLabel();
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
        MethodSymbol eq = _type.GetMembers("op_Equality").OfType<MethodSymbol>().First();
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
        int idx = ((SourceMethodSymbol)_method).UnionCaseIndex;
        _il.Ldarg(0);
        _il.LdcI4(idx + 1);
        _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(Tag), -2);
        _il.Ldarg(0);
        _il.Ldarg(1);
        TypeSymbol t = _m.ClrType(_method.Parameters[0].Type);
        if (!(t.IsReferenceType && t is not TypeParameterSymbol))
        {
            _il.Token(ILOpCode.Box, _m.GetTypeEntity(t), 0);
        }

        _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(Value), -2);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void UnionImplicit()
    {
        SourceMethodSymbol ctor = _type.InstanceConstructors.OfType<SourceMethodSymbol>().First(c => c.UnionCaseIndex == ((SourceMethodSymbol)_method).UnionCaseIndex);
        _il.Ldarg(0);
        _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(ctor), 1, 1);
        _il.Op(ILOpCode.Ret, 0);
    }

    private void UnionToString()
    {
        NamedTypeSymbol objT = C.GetSpecialType(SpecialType.Object);
        LabelHandle notNull = _il.NewLabel();
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
        LabelHandle no = _il.NewLabel();
        NamedTypeSymbol objT = C.GetSpecialType(SpecialType.Object);
        EntityHandle self = _m.GetTypeEntity(Self);
        LocalSlot o = _il.NewLocal(Self);
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
        MethodSymbol eq = objT.GetMembers("Equals").OfType<MethodSymbol>().First(x => x.IsStatic && x.Parameters.Length == 2);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(eq), 2, 1);
        _il.Op(ILOpCode.Ret, 0);
        _il.Mark(no);
        _il.ResetDepth();
        _il.LdcI4(0);
        _il.Op(ILOpCode.Ret, -1);
    }

    private void UnionHash()
    {
        NamedTypeSymbol objT = C.GetSpecialType(SpecialType.Object);
        LabelHandle isNull = _il.NewLabel();
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
