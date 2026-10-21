using System.Reflection.Metadata;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Emit;

internal sealed partial class MethodBodyEmitter
{
    private void EmitConversion(BoundConversion c, bool used)
    {
        var from = c.Operand.Type!;
        var to = c.Type!;
        switch (c.Kind)
        {
            case ConversionKind.Identity or ConversionKind.TypeOnly or ConversionKind.ImplicitReference:
                EmitExpression(c.Operand, used);
                return;
            case ConversionKind.NullLiteral:
                if (used) EmitDefault(to);
                return;
            case ConversionKind.ImplicitNumeric or ConversionKind.ExplicitNumeric or ConversionKind.EnumNumeric:
                EmitExpression(c.Operand, true);
                EmitNumericConversion(from, to);
                break;
            case ConversionKind.ToNullable:
                EmitExpression(c.Operand, true);
                if (_m.IsNullableValue(to))
                {
                    var ctor = ((NamedTypeSymbol)_m.ClrType(to)).InstanceConstructors.First(x => x.Parameters.Length == 1);
                    _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(ctor), 1, 1);
                }
                break;
            case ConversionKind.FromNullable:
                if (_m.IsNullableValue(from))
                {
                    EmitAddress(c.Operand);
                    var nullable = (NamedTypeSymbol)_m.ClrType(from);
                    var getValue = nullable.GetMembers("get_Value").OfType<MethodSymbol>().First();
                    _il.Call(ILOpCode.Call, _m.GetMethodEntity(getValue), 1, 1);
                }
                else EmitExpression(c.Operand, true);
                break;
            case ConversionKind.Boxing:
                EmitExpression(c.Operand, true);
                if (EmitModule.IsValueTypeLike(_m.ClrType(from)) || from is TypeParameterSymbol || _m.IsNullableValue(from))
                    _il.Token(ILOpCode.Box, _m.GetTypeEntity(from), 0);
                break;
            case ConversionKind.Unboxing:
                EmitExpression(c.Operand, true);
                _il.Token(ILOpCode.Unbox_any, _m.GetTypeEntity(to), 0);
                break;
            case ConversionKind.ExplicitReference:
                EmitExpression(c.Operand, true);
                if (from is TypeParameterSymbol) _il.Token(ILOpCode.Box, _m.GetTypeEntity(from), 0);
                if (to is TypeParameterSymbol || EmitModule.IsValueTypeLike(_m.ClrType(to))) _il.Token(ILOpCode.Unbox_any, _m.GetTypeEntity(to), 0);
                else _il.Token(ILOpCode.Castclass, _m.GetTypeEntity(to), 0);
                break;
            case ConversionKind.TypeParameter:
                EmitExpression(c.Operand, true);
                break;
            case ConversionKind.UserDefined:
                EmitExpression(c.Operand, true);
                _il.Call(ILOpCode.Call, _m.GetMethodEntity(c.Method!), 1, 1);
                break;
            case ConversionKind.UnionWrap:
                EmitUnionWrap(c);
                break;
            case ConversionKind.UnionExtract:
            {
                var ut = (NamedTypeSymbol)_m.ClrType(from.StripNullable());
                EmitFieldReceiver(c.Operand);
                var value = ut.GetMembers("Value").OfType<FieldSymbol>().First();
                _il.Token(ILOpCode.Ldfld, _m.GetFieldEntity(value), 0);
                var target = _m.ClrType(to);
                if (EmitModule.IsValueTypeLike(target) || target is TypeParameterSymbol) _il.Token(ILOpCode.Unbox_any, _m.GetTypeEntity(target), 0);
                else _il.Token(ILOpCode.Castclass, _m.GetTypeEntity(target), 0);
                break;
            }
            case ConversionKind.LiftedNumeric:
                EmitLiftedNumeric(c);
                break;
            default:
                throw new InvalidOperationException("Unhandled conversion " + c.Kind);
        }
        if (!used) _il.Op(ILOpCode.Pop, -1);
    }

    private void EmitUnionWrap(BoundConversion c)
    {
        var unionType = (NamedTypeSymbol)_m.ClrType(c.Type!.StripNullable());
        var from = c.Operand.Type!;
        if (c.CaseIndex == -3 || (from.StripNullable() is NamedTypeSymbol { IsUnionType: true } fu && !fu.Equals(unionType)))
        {
            EmitUnionToUnion(c, unionType);
            return;
        }
        EmitExpression(c.Operand, true);
        var cases = unionType.UnionCases;
        var caseType = c.CaseIndex >= 0 && c.CaseIndex < cases.Length ? cases[c.CaseIndex] : from;
        // convert to the case type first when needed (e.g. int -> double case)
        var ctor = unionType.InstanceConstructors.First(k => k.Parameters.Length == 1 && k.Parameters[0].Type.Equals(caseType));
        _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(ctor), 1, 1);
    }

    /// <summary>Re-wraps each case of the source union into the corresponding case of a wider union (dispatching on the tag).</summary>
    private void EmitUnionToUnion(BoundConversion c, NamedTypeSymbol target)
    {
        var from = c.Operand.Type!;
        var source = (NamedTypeSymbol)_m.ClrType(from.StripNullable());
        var tmp = TempLocal(source);
        EmitExpression(c.Operand, true);
        _il.Stloc(tmp.Index);
        var end = _il.NewLabel();
        var tagField = source.GetMembers("Tag").OfType<FieldSymbol>().First();
        var valueField = source.GetMembers("Value").OfType<FieldSymbol>().First();
        var srcCases = source.UnionCases;
        var tgtCases = target.UnionCases;
        for (var i = 0; i < srcCases.Length; i++)
        {
            var next = _il.NewLabel();
            _il.Ldloca(tmp.Index);
            _il.Token(ILOpCode.Ldfld, _m.GetFieldEntity(tagField), 0);
            _il.LdcI4(i + 1);
            _il.Branch(ILOpCode.Bne_un, next);
            // find the target case this source case maps to
            var j = -1;
            for (var k = 0; k < tgtCases.Length; k++)
                if (tgtCases[k].Equals(srcCases[i])) { j = k; break; }
            if (j < 0)
                for (var k = 0; k < tgtCases.Length; k++)
                    if (ImplicitRef(srcCases[i], tgtCases[k])) { j = k; break; }
            if (j < 0) j = 0;
            _il.Ldloca(tmp.Index);
            _il.Token(ILOpCode.Ldfld, _m.GetFieldEntity(valueField), 0);
            var st = _m.ClrType(srcCases[i]);
            if (EmitModule.IsValueTypeLike(st) || st is TypeParameterSymbol) _il.Token(ILOpCode.Unbox_any, _m.GetTypeEntity(st), 0);
            else _il.Token(ILOpCode.Castclass, _m.GetTypeEntity(st), 0);
            var ctor = target.InstanceConstructors.First(k => k.Parameters.Length == 1 && k.Parameters[0].Type.Equals(tgtCases[j]));
            _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(ctor), 1, 1);
            _il.Branch(ILOpCode.Br, end);
            _il.Mark(next);
        }
        // default(source): produce default(target)
        EmitDefault(target);
        _il.Mark(end);
    }

    private static bool ImplicitRef(TypeSymbol a, TypeSymbol b)
    {
        for (var t = a.BaseType; t != null; t = t.BaseType)
            if (t.Equals(b)) return true;
        return a.AllInterfaces.Any(i => i.Equals(b)) || b.SpecialType == SpecialType.Object;
    }

    private void EmitLiftedNumeric(BoundConversion c)
    {
        var from = (NullableTypeSymbol)c.Operand.Type!;
        var to = (NullableTypeSymbol)c.Type!;
        var srcNullable = (NamedTypeSymbol)_m.ClrType(from);
        var tmp = TempLocal(from);
        EmitExpression(c.Operand, true);
        _il.Stloc(tmp.Index);
        var isNull = _il.NewLabel();
        var end = _il.NewLabel();
        _il.Ldloca(tmp.Index);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(srcNullable.GetMembers("get_HasValue").OfType<MethodSymbol>().First()), 1, 1);
        _il.Branch(ILOpCode.Brfalse, isNull);
        _il.Ldloca(tmp.Index);
        _il.Call(ILOpCode.Call, _m.GetMethodEntity(srcNullable.GetMembers("GetValueOrDefault").OfType<MethodSymbol>().First(m => m.Parameters.Length == 0)), 1, 1);
        EmitNumericConversion(from.Underlying, to.Underlying);
        var ctor = ((NamedTypeSymbol)_m.ClrType(to)).InstanceConstructors.First(k => k.Parameters.Length == 1);
        _il.Call(ILOpCode.Newobj, _m.GetMethodEntity(ctor), 1, 1);
        _il.Branch(ILOpCode.Br, end);
        _il.Mark(isNull);
        EmitDefault(to);
        _il.Mark(end);
    }

    // ------------------------------------------------------------------ numeric conversions

    private static SpecialType Underlying(TypeSymbol t)
    {
        if (t.TypeKind == TypeKind.Enum) return ((NamedTypeSymbol)t).EnumUnderlyingType!.SpecialType;
        return t.SpecialType;
    }

    private void EmitNumericConversion(TypeSymbol fromType, TypeSymbol toType)
    {
        var from = Underlying(fromType);
        var to = Underlying(toType);
        if (from == to) return;
        if (from == SpecialType.Decimal || to == SpecialType.Decimal)
        {
            var dec = _c.GetSpecialType(SpecialType.Decimal);
            var fromT = _c.GetSpecialType(from);
            var toT = _c.GetSpecialType(to);
            var name = from == SpecialType.Decimal ? "op_Explicit" : "op_Implicit";
            MethodSymbol? m = null;
            foreach (var cand in dec.GetMembers(name).OfType<MethodSymbol>())
                if (cand.Parameters.Length == 1 && cand.Parameters[0].Type.SpecialType == from && cand.ReturnType.SpecialType == to) { m = cand; break; }
            if (m == null)
            {
                // go through an intermediate type (e.g. char/byte/short -> decimal via int)
                var mid = from == SpecialType.Decimal ? SpecialType.Int32 : SpecialType.Int32;
                if (from == SpecialType.Decimal)
                {
                    EmitNumericConversion(fromType, _c.GetSpecialType(SpecialType.Int32));
                    EmitNumericConversion(_c.GetSpecialType(SpecialType.Int32), toType);
                }
                else
                {
                    EmitNumericConversion(fromType, _c.GetSpecialType(mid));
                    EmitNumericConversion(_c.GetSpecialType(mid), toType);
                }
                return;
            }
            _ = fromT; _ = toT;
            _il.Call(ILOpCode.Call, _m.GetMethodEntity(m), 1, 1);
            return;
        }
        var unsignedFrom = from is SpecialType.Byte or SpecialType.UInt16 or SpecialType.Char or SpecialType.UInt32 or SpecialType.UInt64 or SpecialType.UIntPtr;
        switch (to)
        {
            case SpecialType.SByte: _il.Op(ILOpCode.Conv_i1, 0); break;
            case SpecialType.Byte: _il.Op(ILOpCode.Conv_u1, 0); break;
            case SpecialType.Int16: _il.Op(ILOpCode.Conv_i2, 0); break;
            case SpecialType.UInt16 or SpecialType.Char: _il.Op(ILOpCode.Conv_u2, 0); break;
            case SpecialType.Int32:
                if (from is SpecialType.Int64 or SpecialType.UInt64 or SpecialType.Single or SpecialType.Double or SpecialType.UInt32 or SpecialType.IntPtr or SpecialType.UIntPtr) _il.Op(ILOpCode.Conv_i4, 0);
                break;
            case SpecialType.UInt32:
                if (from is not (SpecialType.Byte or SpecialType.UInt16 or SpecialType.Char)) _il.Op(ILOpCode.Conv_u4, 0);
                break;
            case SpecialType.Int64:
                _il.Op(from is SpecialType.UInt32 or SpecialType.UInt16 or SpecialType.Byte or SpecialType.Char or SpecialType.UIntPtr ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8, 0);
                break;
            case SpecialType.UInt64:
                _il.Op(unsignedFrom ? ILOpCode.Conv_u8 : ILOpCode.Conv_i8, 0);
                break;
            case SpecialType.Single:
                if (unsignedFrom) _il.Op(ILOpCode.Conv_r_un, 0);
                _il.Op(ILOpCode.Conv_r4, 0);
                break;
            case SpecialType.Double:
                if (unsignedFrom) _il.Op(ILOpCode.Conv_r_un, 0);
                _il.Op(ILOpCode.Conv_r8, 0);
                break;
            case SpecialType.IntPtr: _il.Op(unsignedFrom ? ILOpCode.Conv_u : ILOpCode.Conv_i, 0); break;
            case SpecialType.UIntPtr: _il.Op(ILOpCode.Conv_u, 0); break;
        }
    }
}
