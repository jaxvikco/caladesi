using System.Collections.Immutable;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Emit;

internal sealed partial class MethodBodyEmitter
{
    // ------------------------------------------------------------------ expressions

    public void EmitExpression(BoundExpression e, bool used)
    {
        // constants
        if (e.ConstantValue is { } cv && e is not (BoundAssignment or BoundSequence or BoundCall) && e.Type != null && !e.Type.IsErrorType)
        {
            if (used)
            {
                EmitConstant(cv.Value, e.Type);
            }

            return;
        }
        switch (e)
        {
            case BoundLiteral lit:
                if (used)
                {
                    EmitConstant(lit.Value.Value, lit.Type!);
                }

                return;
            case BoundIsNull isn:
                {
                    TypeSymbol ot = _m.ClrType(isn.Operand.Type!);
                    if (EmitModule.IsNullableValue(isn.Operand.Type!))
                    {
                        EmitAddress(isn.Operand);
                        _il.Call(ILOpCode.Call, _m.GetMethodEntity(((NamedTypeSymbol)ot).GetMembers("get_HasValue").OfType<MethodSymbol>().First()), 1, 1);
                        _il.LdcI4(0);
                        _il.Op(ILOpCode.Ceq, -1);
                    }
                    else if (EmitModule.IsValueTypeLike(ot))
                    {
                        EmitExpression(isn.Operand, false);
                        _il.LdcI4(0);
                    }
                    else
                    {
                        EmitExpression(isn.Operand, true);
                        if (ot is TypeParameterSymbol)
                        {
                            _il.Token(ILOpCode.Box, _m.GetTypeEntity(ot), 0);
                        }

                        _il.Op(ILOpCode.Ldnull, 1);
                        _il.Op(ILOpCode.Ceq, -1);
                    }
                    if (!used)
                    {
                        _il.Op(ILOpCode.Pop, -1);
                    }

                    return;
                }
            case BoundAddressOf ao:
                EmitAddress(ao.Operand);
                if (!used)
                {
                    _il.Op(ILOpCode.Pop, -1);
                }

                return;
            case BoundLocal l:
                if (used)
                {
                    _il.Ldloc(GetLocal(l.Local).Index);
                }

                return;
            case BoundParameter p:
                if (used)
                {
                    _il.Ldarg(_paramIndex[p.Parameter]);
                }

                return;
            case BoundThis:
                if (used)
                {
                    _il.Ldarg(0);
                    if (_thisIsByRef)
                    {
                        EmitLdobj(_method.ContainingType!);
                    }
                }
                return;
            case BoundBaseReference:
                if (used)
                {
                    _il.Ldarg(0);
                }

                return;
            case BoundFieldAccess f: EmitFieldLoad(f, used); return;
            case BoundPropertyAccess pa: EmitPropertyGet(pa, used); return;
            case BoundCall c: EmitCall(c, used); return;
            case BoundObjectCreation oc: EmitObjectCreation(oc, used); return;
            case BoundDefaultValue d:
                if (used)
                {
                    EmitDefault(d.Type!);
                }

                return;
            case BoundArrayCreation ac: EmitArrayCreation(ac, used); return;
            case BoundArrayAccess aa:
                EmitExpression(aa.Array, true);
                EmitExpression(aa.Index, true);
                EmitLdelem(aa.Type!);
                if (!used)
                {
                    _il.Op(ILOpCode.Pop, -1);
                }

                return;
            case BoundConversion cv2: EmitConversion(cv2, used); return;
            case BoundUnary u: EmitUnary(u, used); return;
            case BoundBinary b: EmitBinary(b, used); return;
            case BoundLogicalBinary lb: EmitLogical(lb, used); return;
            case BoundAssignment a: EmitAssignment(a, used); return;
            case BoundConditional c: EmitConditional(c, used); return;
            case BoundCoalesce co: EmitCoalesce(co, used); return;
            case BoundConditionalAccess ca: EmitConditionalAccess(ca, used); return;
            case BoundConditionalReceiver cr: EmitConditionalReceiver(cr, used); return;
            case BoundTypeTest tt: EmitTypeTest(tt, used); return;
            case BoundDelegateCreation dc: EmitDelegateCreation(dc, used); return;
            case BoundSequence seq:
                foreach (LocalSymbol l in seq.Locals)
                {
                    _ = GetLocal(l);
                }

                foreach (BoundStatement s in seq.SideEffects)
                {
                    EmitStatement(s);
                }

                EmitExpression(seq.Value, used);
                return;
            case BoundTypeOf to:
                {
                    _il.Token(ILOpCode.Ldtoken, _m.GetTypeEntity(to.Operand), 1);
                    MethodSymbol getType = ((NamedTypeSymbol)to.Type!).GetMembers("GetTypeFromHandle").OfType<MethodSymbol>().First();
                    _il.Call(ILOpCode.Call, _m.GetMethodEntity(getType), 1, 1);
                    if (!used)
                    {
                        _il.Op(ILOpCode.Pop, -1);
                    }

                    return;
                }
            case BoundThrowExpression te:
                EmitExpression(te.Operand, true);
                _il.Op(ILOpCode.Throw, -1);
                return;
            case BoundLambda or BoundAwait or BoundUnbound or BoundMethodGroup or BoundTypeExpression or BoundNamespaceExpression or BoundBadExpression:
                throw new InvalidOperationException("Expression must be lowered before emit: " + e.GetType().Name);
        }
        throw new InvalidOperationException("Unhandled expression " + e.GetType().Name);
    }

    // ------------------------------------------------------------------ constants

    private void EmitConstant(object? v, TypeSymbol type)
    {
        TypeSymbol t = type;
        if (t is NullableTypeSymbol nt0 && v != null)
        {
            t = nt0.Underlying;
        }

        if (t.TypeKind == TypeKind.Enum)
        {
            t = ((NamedTypeSymbol)t).EnumUnderlyingType!;
        }

        if (v == null)
        {
            EmitDefault(type);
            return;
        }
        switch (t.SpecialType)
        {
            case SpecialType.Boolean: _il.LdcI4((bool)v ? 1 : 0); return;
            case SpecialType.Char: _il.LdcI4(v is char c ? c : Convert.ToInt32(v)); return;
            case SpecialType.SByte or SpecialType.Byte or SpecialType.Int16 or SpecialType.UInt16 or SpecialType.Int32:
                _il.LdcI4(Convert.ToInt32(v is char ch ? (int)ch : v)); return;
            case SpecialType.UInt32: _il.LdcI4(unchecked((int)Convert.ToUInt32(v))); return;
            case SpecialType.Int64: _il.LdcI8(Convert.ToInt64(v)); return;
            case SpecialType.UInt64: _il.LdcI8(unchecked((long)Convert.ToUInt64(v))); return;
            case SpecialType.Single: _il.LdcR4(Convert.ToSingle(v)); return;
            case SpecialType.Double: _il.LdcR8(Convert.ToDouble(v)); return;
            case SpecialType.IntPtr: _il.LdcI4(Convert.ToInt32(v)); _il.Op(ILOpCode.Conv_i, 0); return;
            case SpecialType.UIntPtr: _il.LdcI4(unchecked((int)Convert.ToUInt32(v))); _il.Op(ILOpCode.Conv_u, 0); return;
            case SpecialType.String: _il.LdStr(_m.Metadata.GetOrAddUserString((string)v)); return;
            case SpecialType.Decimal:
                {
                    int[] bits = decimal.GetBits(Convert.ToDecimal(v));
                    MethodSymbol ctor = _c.GetSpecialType(SpecialType.Decimal).InstanceConstructors.First(x => x.Parameters.Length == 5);
                    _il.LdcI4(bits[0]); _il.LdcI4(bits[1]); _il.LdcI4(bits[2]);
                    _il.LdcI4((bits[3] & unchecked((int)0x80000000)) != 0 ? 1 : 0);
                    _il.LdcI4((bits[3] >> 16) & 0xFF);
                    _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(ctor), 5, 1);
                    return;
                }
            case SpecialType.BigInteger:
                {
                    MethodSymbol parse = _c.GetSpecialType(SpecialType.BigInteger).GetMembers("Parse").OfType<MethodSymbol>().First(x => x.IsStatic && x.Parameters.Length == 1 && x.Parameters[0].Type.SpecialType == SpecialType.String);
                    _il.LdStr(_m.Metadata.GetOrAddUserString(((BigInteger)v).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    _il.Call(ILOpCode.Call, _m.GetMethodEntity(parse), 1, 1);
                    return;
                }
            case SpecialType.Object:
                if (v is string s) { _il.LdStr(_m.Metadata.GetOrAddUserString(s)); return; }
                break;
        }
        throw new InvalidOperationException($"Cannot emit constant {v} of type {type.ToDisplayString()}");
    }

    internal void EmitDefault(TypeSymbol type)
    {
        TypeSymbol t = _m.ClrType(type);
        if (t.IsNullType || (!EmitModule.IsValueTypeLike(t) && t is not TypeParameterSymbol))
        {
            _il.Op(ILOpCode.Ldnull, 1);
            return;
        }
        switch (t.SpecialType)
        {
            case SpecialType.Boolean or SpecialType.Char or SpecialType.SByte or SpecialType.Byte or SpecialType.Int16 or SpecialType.UInt16
                or SpecialType.Int32 or SpecialType.UInt32:
                _il.LdcI4(0); return;
            case SpecialType.Int64 or SpecialType.UInt64: _il.LdcI8(0); return;
            case SpecialType.Single: _il.LdcR4(0); return;
            case SpecialType.Double: _il.LdcR8(0); return;
        }
        LocalSlot tmp = TempLocal(t);
        _il.Ldloca(tmp.Index);
        _il.Token(ILOpCode.Initobj, _m.GetTypeEntity(t), -1);
        _il.Ldloc(tmp.Index);
    }

    private void EmitLdobj(TypeSymbol t)
    {
        _il.Token(ILOpCode.Ldobj, _m.GetTypeEntity(t), 0);
    }

    // ------------------------------------------------------------------ addresses and receivers

    /// <summary>Pushes a managed pointer to the storage denoted by <paramref name="e"/> (spilling rvalues to a temp).</summary>
    private void EmitAddress(BoundExpression e)
    {
        switch (e)
        {
            case BoundLocal l when EmitModule.IsValueTypeLike(_m.ClrType(l.Local.Type)) || l.Local.Type is TypeParameterSymbol:
                _il.Ldloca(GetLocal(l.Local).Index);
                return;
            case BoundParameter p when EmitModule.IsValueTypeLike(_m.ClrType(p.Parameter.Type)) || p.Parameter.Type is TypeParameterSymbol:
                _il.Ldarga(_paramIndex[p.Parameter]);
                return;
            case BoundThis when _thisIsByRef:
                _il.Ldarg(0);
                return;
            case BoundFieldAccess f when !f.Field.IsConst:
                if (f.Field.IsStatic)
                {
                    _il.Token(ILOpCode.Ldsflda, _m.GetFieldEntity(f.Field), 1);
                }
                else
                {
                    EmitFieldReceiver(f.Receiver!);
                    _il.Token(ILOpCode.Ldflda, _m.GetFieldEntity(f.Field), 0);
                }
                return;
            case BoundArrayAccess aa:
                EmitExpression(aa.Array, true);
                EmitExpression(aa.Index, true);
                _il.Token(ILOpCode.Ldelema, _m.GetTypeEntity(aa.Type!), -1);
                return;
        }
        EmitExpression(e, true);
        LocalSlot tmp = TempLocal(e.Type!);
        _il.Stloc(tmp.Index);
        _il.Ldloca(tmp.Index);
    }

    private void EmitFieldReceiver(BoundExpression recv)
    {
        TypeSymbol rt = recv.Type!;
        if (EmitModule.IsValueTypeLike(_m.ClrType(rt)) || rt is TypeParameterSymbol)
        {
            EmitAddress(recv);
        }
        else
        {
            EmitExpression(recv, true);
        }
    }

    /// <summary>Pushes the receiver for an instance call and reports whether a constrained prefix is required.</summary>
    private TypeSymbol? EmitCallReceiver(BoundExpression recv, MethodSymbol method, bool isBase)
    {
        if (isBase)
        {
            EmitExpression(recv, true);
            return null;
        }
        TypeSymbol rt = _m.ClrType(recv.Type!);
        if (EmitModule.IsValueTypeLike(rt) || rt is TypeParameterSymbol)
        {
            EmitAddress(recv);
            NamedTypeSymbol? decl = method.ContainingType;
            return rt is TypeParameterSymbol || decl == null || !ReferenceEquals(decl.OriginalDefinition, (rt as NamedTypeSymbol)?.OriginalDefinition)
                ? rt
                : null;
        }
        EmitExpression(recv, true);
        return null;
    }

    private void EmitFieldLoad(BoundFieldAccess f, bool used)
    {
        if (f.Field.IsStatic)
        {
            if (!used)
            {
                return;
            }

            _il.Token(ILOpCode.Ldsfld, _m.GetFieldEntity(f.Field), 1);
            return;
        }
        EmitFieldReceiver(f.Receiver!);
        _il.Token(ILOpCode.Ldfld, _m.GetFieldEntity(f.Field), 0);
        if (!used)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    // ------------------------------------------------------------------ calls

    private void EmitArguments(ImmutableArray<BoundExpression> args)
    {
        foreach (BoundExpression a in args)
        {
            EmitExpression(a, true);
        }
    }

    private void EmitCallCore(MethodSymbol m, BoundExpression? receiver, ImmutableArray<BoundExpression> args, bool isBase, bool used)
    {
        TypeSymbol? constrained = null;
        bool isInstance = !m.IsStatic;
        if (isInstance && receiver != null)
        {
            constrained = EmitCallReceiver(receiver, m, isBase);
        }
        else if (isInstance)
        {
            _il.Ldarg(0);
        }

        EmitArguments(args);
        ILOpCode op;
        if (!isInstance)
        {
            op = ILOpCode.Call;
        }
        else if (m.MethodKind == MethodKind.Constructor || isBase)
        {
            op = ILOpCode.Call;
        }
        else if (constrained != null)
        {
            op = ILOpCode.Callvirt;
        }
        else
        {
            TypeSymbol? rt = receiver != null ? _m.ClrType(receiver.Type!) : null;
            op = rt != null && EmitModule.IsValueTypeLike(rt) ? ILOpCode.Call : ILOpCode.Callvirt;
        }
        if (constrained != null)
        {
            _il.Encoder.OpCode(ILOpCode.Constrained);
            _il.Encoder.Token(_m.GetTypeEntity(constrained));
        }
        int pops = args.Length + (isInstance ? 1 : 0);
        TypeSymbol rtn = m.ReturnType;
        int pushes = rtn.IsVoid ? 0 : 1;
        _il.Call(op, _m.GetMethodEntity(m), pops, pushes);
        if (m.ReturnsByRef && pushes == 1)
        {
            _il.Token(ILOpCode.Ldobj, _m.GetTypeEntity(rtn), 0);
        }

        if (!used && pushes == 1)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    private void EmitCall(BoundCall c, bool used)
    {
        EmitCallCore(c.Method, c.Receiver, c.Arguments, c.IsBaseCall, used);
    }

    private void EmitPropertyGet(BoundPropertyAccess pa, bool used)
    {
        MethodSymbol getter = pa.Property.GetMethod ?? throw new InvalidOperationException("property without getter: " + pa.Property.Name);
        EmitCallCore(getter, pa.Receiver, pa.Arguments, pa.IsBaseAccess, used);
    }

    private void EmitObjectCreation(BoundObjectCreation oc, bool used)
    {
        TypeSymbol type = _m.ClrType(oc.Type!);
        if (oc.Type is TypeParameterSymbol tp)
        {
            MethodSymbol activator = _c.GetWellKnownType("System.Activator")!.GetMembers("CreateInstance").OfType<MethodSymbol>().First(m => m.Arity == 1 && m.Parameters.Length == 0);
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(activator.Construct([tp])), 0, 1);
            if (!used)
            {
                _il.Op(ILOpCode.Pop, -1);
            }

            return;
        }
        bool isValue = EmitModule.IsValueTypeLike(type);
        if (oc.Constructor == null)
        {
            // default(struct)
            LocalSlot tmp0 = TempLocal(type);
            _il.Ldloca(tmp0.Index);
            _il.Token(ILOpCode.Initobj, _m.GetTypeEntity(type), -1);
            if (oc.Initializers.IsDefaultOrEmpty)
            {
                if (used)
                {
                    _il.Ldloc(tmp0.Index);
                }

                return;
            }
            EmitInitializers(oc, tmp0, isValue: true);
            if (used)
            {
                _il.Ldloc(tmp0.Index);
            }

            return;
        }
        EmitArguments(oc.Arguments);
        _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(oc.Constructor), oc.Arguments.Length, 1);
        if (oc.Initializers.IsDefaultOrEmpty)
        {
            if (!used)
            {
                _il.Op(ILOpCode.Pop, -1);
            }

            return;
        }
        LocalSlot tmp = TempLocal(type);
        _il.Stloc(tmp.Index);
        EmitInitializers(oc, tmp, isValue);
        if (used)
        {
            _il.Ldloc(tmp.Index);
        }
    }

    private void EmitInitializers(BoundObjectCreation oc, LocalSlot tmp, bool isValue)
    {
        foreach (BoundMemberInit init in oc.Initializers)
        {
            if (isValue)
            {
                _il.Ldloca(tmp.Index);
            }
            else
            {
                _il.Ldloc(tmp.Index);
            }

            switch (init.Member)
            {
                case PropertySymbol p:
                    EmitExpression(init.Value, true);
                    _il.Call(isValue ? ILOpCode.Call : ILOpCode.Callvirt, _m.GetMethodEntity(p.SetMethod!), 2, 0);
                    break;
                case FieldSymbol f:
                    EmitExpression(init.Value, true);
                    _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(f), -2);
                    break;
            }
        }
    }

    private void EmitArrayCreation(BoundArrayCreation ac, bool used)
    {
        TypeSymbol elem = ac.Type.ElementType;
        if (ac.Length != null)
        {
            EmitExpression(ac.Length, true);
        }
        else
        {
            _il.LdcI4(ac.Initializers.Length);
        }

        _il.Token(ILOpCode.Newarr, _m.GetTypeEntity(elem), 0);
        if (!ac.Initializers.IsDefaultOrEmpty)
        {
            for (int i = 0; i < ac.Initializers.Length; i++)
            {
                _il.Op(ILOpCode.Dup, 1);
                _il.LdcI4(i);
                EmitExpression(ac.Initializers[i], true);
                EmitStelem(elem);
            }
        }
        if (!used)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    private void EmitLdelem(TypeSymbol elem)
    {
        TypeSymbol t = _m.ClrType(elem);
        ILOpCode op = t.SpecialType switch
        {
            SpecialType.SByte => ILOpCode.Ldelem_i1,
            SpecialType.Byte or SpecialType.Boolean => ILOpCode.Ldelem_u1,
            SpecialType.Int16 => ILOpCode.Ldelem_i2,
            SpecialType.UInt16 or SpecialType.Char => ILOpCode.Ldelem_u2,
            SpecialType.Int32 => ILOpCode.Ldelem_i4,
            SpecialType.UInt32 => ILOpCode.Ldelem_u4,
            SpecialType.Int64 or SpecialType.UInt64 => ILOpCode.Ldelem_i8,
            SpecialType.Single => ILOpCode.Ldelem_r4,
            SpecialType.Double => ILOpCode.Ldelem_r8,
            SpecialType.IntPtr or SpecialType.UIntPtr => ILOpCode.Ldelem_i,
            _ => ILOpCode.Nop,
        };
        if (op != ILOpCode.Nop && t.TypeKind != TypeKind.Enum) { _il.Op(op, -1); return; }
        if (!EmitModule.IsValueTypeLike(t) && t is not TypeParameterSymbol) { _il.Op(ILOpCode.Ldelem_ref, -1); return; }
        _il.Token(ILOpCode.Ldelem, _m.GetTypeEntity(t), -1);
    }

    private void EmitStelem(TypeSymbol elem)
    {
        TypeSymbol t = _m.ClrType(elem);
        ILOpCode op = t.SpecialType switch
        {
            SpecialType.SByte or SpecialType.Byte or SpecialType.Boolean => ILOpCode.Stelem_i1,
            SpecialType.Int16 or SpecialType.UInt16 or SpecialType.Char => ILOpCode.Stelem_i2,
            SpecialType.Int32 or SpecialType.UInt32 => ILOpCode.Stelem_i4,
            SpecialType.Int64 or SpecialType.UInt64 => ILOpCode.Stelem_i8,
            SpecialType.Single => ILOpCode.Stelem_r4,
            SpecialType.Double => ILOpCode.Stelem_r8,
            SpecialType.IntPtr or SpecialType.UIntPtr => ILOpCode.Stelem_i,
            _ => ILOpCode.Nop,
        };
        if (op != ILOpCode.Nop && t.TypeKind != TypeKind.Enum) { _il.Op(op, -3); return; }
        if (!EmitModule.IsValueTypeLike(t) && t is not TypeParameterSymbol) { _il.Op(ILOpCode.Stelem_ref, -3); return; }
        _il.Token(ILOpCode.Stelem, _m.GetTypeEntity(t), -3);
    }

    // ------------------------------------------------------------------ assignment

    private void EmitAssignment(BoundAssignment a, bool used)
    {
        used = used && !a.IsDiscarded;
        switch (a.Target)
        {
            case BoundLocal l:
                EmitExpression(a.Value, true);
                if (used)
                {
                    _il.Op(ILOpCode.Dup, 1);
                }

                _il.Stloc(GetLocal(l.Local).Index);
                return;
            case BoundParameter p:
                EmitExpression(a.Value, true);
                if (used)
                {
                    _il.Op(ILOpCode.Dup, 1);
                }

                _il.Starg(_paramIndex[p.Parameter]);
                return;
            case BoundFieldAccess f:
                {
                    if (f.Field.IsStatic)
                    {
                        EmitExpression(a.Value, true);
                        if (used)
                        {
                            _il.Op(ILOpCode.Dup, 1);
                        }

                        _il.Token(ILOpCode.Stsfld, _m.GetFieldEntity(f.Field), -1);
                        return;
                    }
                    EmitFieldReceiver(f.Receiver!);
                    EmitExpression(a.Value, true);
                    LocalSlot? tmp = null;
                    if (used)
                    {
                        _il.Op(ILOpCode.Dup, 1);
                        tmp = TempLocal(a.Value.Type!);
                        _il.Stloc(tmp.Index);
                    }
                    _il.Token(ILOpCode.Stfld, _m.GetFieldEntity(f.Field), -2);
                    if (tmp != null)
                    {
                        _il.Ldloc(tmp.Index);
                    }

                    return;
                }
            case BoundPropertyAccess pa:
                {
                    MethodSymbol setter = pa.Property.SetMethod ?? throw new InvalidOperationException("property without setter: " + pa.Property.Name);
                    TypeSymbol? constrained = null;
                    if (!setter.IsStatic)
                    {
                        constrained = pa.Receiver != null ? EmitCallReceiver(pa.Receiver, setter, pa.IsBaseAccess) : null;
                    }

                    EmitArguments(pa.Arguments);
                    EmitExpression(a.Value, true);
                    LocalSlot? tmp = null;
                    if (used)
                    {
                        _il.Op(ILOpCode.Dup, 1);
                        tmp = TempLocal(a.Value.Type!);
                        _il.Stloc(tmp.Index);
                    }
                    if (constrained != null) { _il.Encoder.OpCode(ILOpCode.Constrained); _il.Encoder.Token(_m.GetTypeEntity(constrained)); }
                    ILOpCode op = setter.IsStatic || pa.IsBaseAccess
                        ? ILOpCode.Call
                        : constrained != null
                        ? ILOpCode.Callvirt
                        : pa.Receiver != null && EmitModule.IsValueTypeLike(_m.ClrType(pa.Receiver.Type!)) ? ILOpCode.Call : ILOpCode.Callvirt;
                    _il.Call(op, _m.GetMethodEntity(setter), pa.Arguments.Length + 1 + (setter.IsStatic ? 0 : 1), 0);
                    if (tmp != null)
                    {
                        _il.Ldloc(tmp.Index);
                    }

                    return;
                }
            case BoundArrayAccess aa:
                {
                    EmitExpression(aa.Array, true);
                    EmitExpression(aa.Index, true);
                    EmitExpression(a.Value, true);
                    LocalSlot? tmp = null;
                    if (used)
                    {
                        _il.Op(ILOpCode.Dup, 1);
                        tmp = TempLocal(a.Value.Type!);
                        _il.Stloc(tmp.Index);
                    }
                    EmitStelem(aa.Type!);
                    if (tmp != null)
                    {
                        _il.Ldloc(tmp.Index);
                    }

                    return;
                }
        }
        throw new InvalidOperationException("Unsupported assignment target " + a.Target.GetType().Name);
    }

    // ------------------------------------------------------------------ operators

    private void EmitUnary(BoundUnary u, bool used)
    {
        EmitExpression(u.Operand, true);
        switch (u.Kind)
        {
            case UnaryOperatorKind.Minus: _il.Op(ILOpCode.Neg, 0); break;
            case UnaryOperatorKind.BitwiseNot: _il.Op(ILOpCode.Not, 0); break;
            case UnaryOperatorKind.Not: _il.LdcI4(0); _il.Op(ILOpCode.Ceq, -1); break;
            case UnaryOperatorKind.Plus: break;
        }
        if (!used)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    private static bool IsUnsignedType(TypeSymbol t)
    {
        return t.SpecialType is SpecialType.UInt32 or SpecialType.UInt64 or SpecialType.UIntPtr or SpecialType.Byte or SpecialType.UInt16 or SpecialType.Char;
    }

    private static bool IsFloatType(TypeSymbol t)
    {
        return t.SpecialType is SpecialType.Single or SpecialType.Double;
    }

    private void EmitBinary(BoundBinary b, bool used)
    {
        // comparisons with null
        if (b.Kind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual && (b.Right is BoundLiteral { Value.IsNull: true } || b.Left is BoundLiteral { Value.IsNull: true }))
        {
            BoundExpression subject = b.Right is BoundLiteral { Value.IsNull: true } ? b.Left : b.Right;
            EmitNullCheck(subject);
            if (b.Kind == BinaryOperatorKind.NotEqual) { _il.LdcI4(0); _il.Op(ILOpCode.Ceq, -1); }
            if (!used)
            {
                _il.Op(ILOpCode.Pop, -1);
            }

            return;
        }
        EmitExpression(b.Left, true);
        EmitExpression(b.Right, true);
        TypeSymbol ot = b.OperandType;
        bool unsigned = IsUnsignedType(ot);
        bool isFloat = IsFloatType(ot);
        switch (b.Kind)
        {
            case BinaryOperatorKind.Add: _il.Op(ILOpCode.Add, -1); break;
            case BinaryOperatorKind.Subtract: _il.Op(ILOpCode.Sub, -1); break;
            case BinaryOperatorKind.Multiply: _il.Op(ILOpCode.Mul, -1); break;
            case BinaryOperatorKind.Divide: _il.Op(unsigned ? ILOpCode.Div_un : ILOpCode.Div, -1); break;
            case BinaryOperatorKind.Remainder: _il.Op(unsigned ? ILOpCode.Rem_un : ILOpCode.Rem, -1); break;
            case BinaryOperatorKind.And: _il.Op(ILOpCode.And, -1); break;
            case BinaryOperatorKind.Or: _il.Op(ILOpCode.Or, -1); break;
            case BinaryOperatorKind.Xor: _il.Op(ILOpCode.Xor, -1); break;
            case BinaryOperatorKind.ShiftLeft or BinaryOperatorKind.ShiftRight or BinaryOperatorKind.UnsignedShiftRight:
                {
                    if (b.Right.ConstantValue == null)
                    {
                        _il.LdcI4(ot.SpecialType is SpecialType.Int64 or SpecialType.UInt64 ? 63 : 31);
                        _il.Op(ILOpCode.And, -1);
                    }
                    else
                    {
                        // constant counts are masked at compile time: replace the pushed count
                        _il.Op(ILOpCode.Pop, -1);
                        int raw = Convert.ToInt32(b.Right.ConstantValue.Value.Value);
                        _il.LdcI4(raw & (ot.SpecialType is SpecialType.Int64 or SpecialType.UInt64 ? 63 : 31));
                    }
                    ILOpCode op = b.Kind switch
                    {
                        BinaryOperatorKind.ShiftLeft => ILOpCode.Shl,
                        BinaryOperatorKind.UnsignedShiftRight => ILOpCode.Shr_un,
                        _ => unsigned ? ILOpCode.Shr_un : ILOpCode.Shr,
                    };
                    _il.Op(op, -1);
                    break;
                }
            case BinaryOperatorKind.Equal: _il.Op(ILOpCode.Ceq, -1); break;
            case BinaryOperatorKind.NotEqual: _il.Op(ILOpCode.Ceq, -1); _il.LdcI4(0); _il.Op(ILOpCode.Ceq, -1); break;
            case BinaryOperatorKind.LessThan: _il.Op(unsigned ? ILOpCode.Clt_un : ILOpCode.Clt, -1); break;
            case BinaryOperatorKind.GreaterThan: _il.Op(unsigned ? ILOpCode.Cgt_un : ILOpCode.Cgt, -1); break;
            case BinaryOperatorKind.LessThanOrEqual:
                _il.Op(unsigned || isFloat ? ILOpCode.Cgt_un : ILOpCode.Cgt, -1); _il.LdcI4(0); _il.Op(ILOpCode.Ceq, -1); break;
            case BinaryOperatorKind.GreaterThanOrEqual:
                _il.Op(unsigned || isFloat ? ILOpCode.Clt_un : ILOpCode.Clt, -1); _il.LdcI4(0); _il.Op(ILOpCode.Ceq, -1); break;
            default:
                throw new InvalidOperationException("Unhandled binary operator " + b.Kind);
        }
        if (!used)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    /// <summary>Pushes 1 if the expression is null (or an empty Nullable&lt;T&gt;), otherwise 0.</summary>
    private void EmitNullCheck(BoundExpression subject)
    {
        TypeSymbol t = subject.Type!;
        if (EmitModule.IsNullableValue(t))
        {
            EmitAddress(subject);
            NamedTypeSymbol nullable = (NamedTypeSymbol)_m.ClrType(t);
            MethodSymbol hasValue = nullable.GetMembers("get_HasValue").OfType<MethodSymbol>().First();
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(hasValue), 1, 1);
            _il.LdcI4(0);
            _il.Op(ILOpCode.Ceq, -1);
            return;
        }
        EmitExpression(subject, true);
        if (EmitModule.IsValueTypeLike(_m.ClrType(t)) || t.StripNullable() is TypeParameterSymbol)
        {
            _il.Token(ILOpCode.Box, _m.GetTypeEntity(t.StripNullable()), 0);
        }
        _il.Op(ILOpCode.Ldnull, 1);
        _il.Op(ILOpCode.Ceq, -1);
    }

    private void EmitLogical(BoundLogicalBinary lb, bool used)
    {
        LabelHandle end = _il.NewLabel();
        LabelHandle shortCircuit = _il.NewLabel();
        EmitExpression(lb.Left, true);
        _il.Branch(lb.IsAnd ? ILOpCode.Brfalse : ILOpCode.Brtrue, shortCircuit);
        EmitExpression(lb.Right, true);
        _il.Branch(ILOpCode.Br, end);
        _il.Mark(shortCircuit);
        _il.LdcI4(lb.IsAnd ? 0 : 1);
        _il.Mark(end);
        if (!used)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    private void EmitConditional(BoundConditional c, bool used)
    {
        LabelHandle elseL = _il.NewLabel();
        LabelHandle end = _il.NewLabel();
        EmitBranchIf(c.Condition, elseL, jumpIfTrue: false);
        EmitExpression(c.WhenTrue, used);
        _il.Branch(ILOpCode.Br, end);
        _il.Mark(elseL);
        EmitExpression(c.WhenFalse, used);
        _il.Mark(end);
    }

    private void EmitCoalesce(BoundCoalesce co, bool used)
    {
        TypeSymbol lt = co.Left.Type!;
        LabelHandle end = _il.NewLabel();
        if (EmitModule.IsNullableValue(lt))
        {
            NamedTypeSymbol nullable = (NamedTypeSymbol)_m.ClrType(lt);
            LocalSlot tmp = TempLocal(lt);
            EmitExpression(co.Left, true);
            _il.Stloc(tmp.Index);
            LabelHandle useRight = _il.NewLabel();
            _il.Ldloca(tmp.Index);
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(nullable.GetMembers("get_HasValue").OfType<MethodSymbol>().First()), 1, 1);
            _il.Branch(ILOpCode.Brfalse, useRight);
            if (EmitModule.IsNullableValue(co.Type!))
            {
                _il.Ldloc(tmp.Index);
            }
            else
            {
                _il.Ldloca(tmp.Index);
                _il.Call(ILOpCode.Call, _m.GetMethodEntity(nullable.GetMembers("GetValueOrDefault").OfType<MethodSymbol>().First(m => m.Parameters.Length == 0)), 1, 1);
            }
            _il.Branch(ILOpCode.Br, end);
            _il.Mark(useRight);
            EmitExpression(co.Right, true);
            _il.Mark(end);
        }
        else
        {
            EmitExpression(co.Left, true);
            _il.Op(ILOpCode.Dup, 1);
            if (lt.StripNullable() is TypeParameterSymbol)
            {
                _il.Token(ILOpCode.Box, _m.GetTypeEntity(lt.StripNullable()), 0);
            }

            _il.Branch(ILOpCode.Brtrue, end);
            _il.Op(ILOpCode.Pop, -1);
            EmitExpression(co.Right, true);
            _il.Mark(end);
        }
        if (!used)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    private void EmitConditionalAccess(BoundConditionalAccess ca, bool used)
    {
        TypeSymbol rt = ca.Receiver.Type!;
        bool isValueNullable = EmitModule.IsNullableValue(rt);
        LocalSlot slot = TempLocal(rt);
        EmitExpression(ca.Receiver, true);
        _il.Stloc(slot.Index);
        _condReceivers[ca.Placeholder] = (slot, rt, isValueNullable);
        LabelHandle isNull = _il.NewLabel();
        LabelHandle end = _il.NewLabel();
        if (isValueNullable)
        {
            NamedTypeSymbol nullable = (NamedTypeSymbol)_m.ClrType(rt);
            _il.Ldloca(slot.Index);
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(nullable.GetMembers("get_HasValue").OfType<MethodSymbol>().First()), 1, 1);
            _il.Branch(ILOpCode.Brfalse, isNull);
        }
        else
        {
            _il.Ldloc(slot.Index);
            _il.Branch(ILOpCode.Brfalse, isNull);
        }
        TypeSymbol? resultType = ca.Type;
        bool voidResult = resultType == null || resultType.IsVoid;
        BoundExpression inner = ca.WhenNotNull;
        if (voidResult)
        {
            EmitExpression(inner, false);
            _il.Branch(ILOpCode.Br, end);
            _il.Mark(isNull);
            _il.Mark(end);
            return;
        }
        EmitExpression(inner, true);
        // wrap the value into the nullable result type when needed
        TypeSymbol innerType = inner.Type!;
        if (EmitModule.IsNullableValue(resultType!) && !EmitModule.IsNullableValue(innerType))
        {
            MethodSymbol ctor = ((NamedTypeSymbol)_m.ClrType(resultType!)).InstanceConstructors.First(c => c.Parameters.Length == 1);
            _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(ctor), 1, 1);
        }
        _il.Branch(ILOpCode.Br, end);
        _il.Mark(isNull);
        EmitDefault(resultType!);
        _il.Mark(end);
        if (!used)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    private void EmitConditionalReceiver(BoundConditionalReceiver cr, bool used)
    {
        (LocalSlot? slot, TypeSymbol? rt, bool isNullableValue) = _condReceivers[cr];
        if (!used)
        {
            return;
        }

        if (isNullableValue)
        {
            NamedTypeSymbol nullable = (NamedTypeSymbol)_m.ClrType(rt);
            _il.Ldloca(slot.Index);
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(nullable.GetMembers("GetValueOrDefault").OfType<MethodSymbol>().First(m => m.Parameters.Length == 0)), 1, 1);
        }
        else
        {
            _il.Ldloc(slot.Index);
        }
    }

    private void EmitTypeTest(BoundTypeTest tt, bool used)
    {
        if (tt.Kind == TypeTestKind.UnionCase)
        {
            NamedTypeSymbol ut = (NamedTypeSymbol)_m.ClrType(tt.Operand.Type!);
            EmitFieldReceiver(tt.Operand);
            FieldSymbol tag = ut.GetMembers("Tag").OfType<FieldSymbol>().First();
            _il.Token(ILOpCode.Ldfld, _m.GetFieldEntity(tag), 0);
            _il.LdcI4(tt.CaseIndex + 1);
            _il.Op(ILOpCode.Ceq, -1);
        }
        else
        {
            EmitExpression(tt.Operand, true);
            TypeSymbol ot = _m.ClrType(tt.Operand.Type!);
            if (EmitModule.IsValueTypeLike(ot) || ot is TypeParameterSymbol)
            {
                _il.Token(ILOpCode.Box, _m.GetTypeEntity(ot), 0);
            }

            _il.Token(ILOpCode.Isinst, _m.GetTypeEntity(tt.Target), 0);
            _il.Op(ILOpCode.Ldnull, 1);
            _il.Op(ILOpCode.Cgt_un, -1);
        }
        if (!used)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    private void EmitDelegateCreation(BoundDelegateCreation dc, bool used)
    {
        MethodSymbol m = dc.Method;
        // an extension method group is a delegate closed over its first argument
        if (dc.Receiver != null && (!m.IsStatic || m.IsExtensionMethod))
        {
            TypeSymbol rt = dc.Receiver.Type!;
            EmitExpression(dc.Receiver, true);
            if (!m.IsStatic && EmitModule.IsValueTypeLike(_m.ClrType(rt)))
            {
                _il.Token(ILOpCode.Box, _m.GetTypeEntity(rt), 0);
            }
        }
        else
        {
            _il.Op(ILOpCode.Ldnull, 1);
        }

        bool virt = !m.IsStatic && (m.IsVirtual || m.IsAbstract || m.IsOverride) && dc.Receiver != null && !m.IsSealed;
        if (virt)
        {
            _il.Op(ILOpCode.Dup, 1);
            _il.Encoder.OpCode(ILOpCode.Ldvirtftn);
            _il.Encoder.Token(_m.GetMethodEntity(m));
        }
        else
        {
            _il.Encoder.OpCode(ILOpCode.Ldftn);
            _il.Encoder.Token(_m.GetMethodEntity(m));
            _il.ResetDepth(_il.CurrentDepth + 1);
        }
        NamedTypeSymbol delegateType = (NamedTypeSymbol)_m.ClrType(dc.Type!);
        MethodSymbol ctor = delegateType.InstanceConstructors.First(c => c.Parameters.Length == 2);
        _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(ctor), 2, 1);
        if (!used)
        {
            _il.Op(ILOpCode.Pop, -1);
        }
    }

    // ------------------------------------------------------------------ branching on conditions

    private void EmitBranchIf(BoundExpression cond, LabelHandle target, bool jumpIfTrue)
    {
        if (cond.ConstantValue is { Value: bool cb })
        {
            if (cb == jumpIfTrue)
            {
                _il.Branch(ILOpCode.Br, target);
            }

            return;
        }
        switch (cond)
        {
            case BoundUnary { Kind: UnaryOperatorKind.Not } u:
                EmitBranchIf(u.Operand, target, !jumpIfTrue);
                return;
            case BoundLogicalBinary lb:
                {
                    if (lb.IsAnd == jumpIfTrue)
                    {
                        // (a && b) jump-if-true  /  (a || b) jump-if-false : both operands must agree
                        LabelHandle skip = _il.NewLabel();
                        EmitBranchIf(lb.Left, skip, !jumpIfTrue);
                        EmitBranchIf(lb.Right, target, jumpIfTrue);
                        _il.Mark(skip);
                    }
                    else
                    {
                        EmitBranchIf(lb.Left, target, jumpIfTrue);
                        EmitBranchIf(lb.Right, target, jumpIfTrue);
                    }
                    return;
                }
            case BoundBinary b when IsDirectBranchable(b):
                {
                    EmitExpression(b.Left, true);
                    EmitExpression(b.Right, true);
                    bool unsigned = IsUnsignedType(b.OperandType);
                    ILOpCode op = jumpIfTrue
                        ? b.Kind switch
                        {
                            BinaryOperatorKind.Equal => ILOpCode.Beq,
                            BinaryOperatorKind.NotEqual => ILOpCode.Bne_un,
                            BinaryOperatorKind.LessThan => unsigned ? ILOpCode.Blt_un : ILOpCode.Blt,
                            BinaryOperatorKind.LessThanOrEqual => unsigned ? ILOpCode.Ble_un : ILOpCode.Ble,
                            BinaryOperatorKind.GreaterThan => unsigned ? ILOpCode.Bgt_un : ILOpCode.Bgt,
                            _ => unsigned ? ILOpCode.Bge_un : ILOpCode.Bge,
                        }
                        : b.Kind switch
                        {
                            BinaryOperatorKind.Equal => ILOpCode.Bne_un,
                            BinaryOperatorKind.NotEqual => ILOpCode.Beq,
                            BinaryOperatorKind.LessThan => unsigned ? ILOpCode.Bge_un : ILOpCode.Bge,
                            BinaryOperatorKind.LessThanOrEqual => unsigned ? ILOpCode.Bgt_un : ILOpCode.Bgt,
                            BinaryOperatorKind.GreaterThan => unsigned ? ILOpCode.Ble_un : ILOpCode.Ble,
                            _ => unsigned ? ILOpCode.Blt_un : ILOpCode.Blt,
                        };
                    _il.Branch(op, target);
                    return;
                }
        }
        EmitExpression(cond, true);
        _il.Branch(jumpIfTrue ? ILOpCode.Brtrue : ILOpCode.Brfalse, target);
    }

    private static bool IsDirectBranchable(BoundBinary b)
    {
        if (b.Kind is not (BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
            or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual))
        {
            return false;
        }

        if (b.Right is BoundLiteral { Value.IsNull: true } || b.Left is BoundLiteral { Value.IsNull: true })
        {
            return false;
        }

        TypeSymbol ot = b.OperandType;
        return !IsFloatType(ot) && ot.SpecialType is SpecialType.Int32 or SpecialType.Int64 or SpecialType.UInt32 or SpecialType.UInt64 or SpecialType.Char or SpecialType.Boolean
            or SpecialType.Int16 or SpecialType.UInt16 or SpecialType.Byte or SpecialType.SByte;
    }
}
