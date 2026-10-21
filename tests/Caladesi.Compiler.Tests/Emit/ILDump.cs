using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Caladesi.Compiler.Tests.Emit;

/// <summary>A tiny IL disassembler used to show failing method bodies in test output.</summary>
internal static class ILDump
{
    public static string Method(PEReader pe, MetadataReader md, MethodDefinitionHandle handle)
    {
        var def = md.GetMethodDefinition(handle);
        var sb = new StringBuilder();
        sb.AppendLine(md.GetString(md.GetTypeDefinition(def.GetDeclaringType()).Name) + "::" + md.GetString(def.Name));
        if (def.RelativeVirtualAddress == 0) return sb.AppendLine("  <no body>").ToString();
        var body = pe.GetMethodBody(def.RelativeVirtualAddress);
        var il = body.GetILBytes()!;
        var arr = il;
        var pos = 0;
        while (pos < arr.Length)
        {
            var start = pos;
            int code = arr[pos++];
            if (code == 0xFE) code = 0xFE00 | arr[pos++];
            var op = (ILOpCode)code;
            var name = op.ToString().ToLowerInvariant().Replace('_', '.');
            sb.Append($"  IL_{start:x4}: {name}");
            switch (OperandKind(op))
            {
                case 'n': break;
                case 'b': sb.Append($" {(sbyte)arr[pos]}"); pos += 1; break;
                case 'B': sb.Append($" IL_{pos + 1 + (sbyte)arr[pos]:x4}"); pos += 1; break;
                case 'w': sb.Append($" {BitConverter.ToUInt16(arr, pos)}"); pos += 2; break;
                case 'i': sb.Append($" {BitConverter.ToInt32(arr, pos)}"); pos += 4; break;
                case 'r': sb.Append($" {BitConverter.ToSingle(arr, pos)}"); pos += 4; break;
                case 'l': sb.Append($" {BitConverter.ToInt64(arr, pos)}"); pos += 8; break;
                case 'd': sb.Append($" {BitConverter.ToDouble(arr, pos)}"); pos += 8; break;
                case 'J': sb.Append($" IL_{pos + 4 + BitConverter.ToInt32(arr, pos):x4}"); pos += 4; break;
                case 't':
                {
                    var token = BitConverter.ToInt32(arr, pos);
                    pos += 4;
                    sb.Append(' ').Append(Describe(md, token));
                    break;
                }
                case 's':
                {
                    var n = BitConverter.ToInt32(arr, pos);
                    pos += 4 + 4 * n;
                    sb.Append($" switch({n})");
                    break;
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Describe(MetadataReader md, int token)
    {
        try
        {
            var h = MetadataTokens.EntityHandle(token);
            switch (h.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                    return md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name) + "::" + md.GetString(m.Name);
                }
                case HandleKind.FieldDefinition:
                {
                    var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                    return md.GetString(md.GetTypeDefinition(f.GetDeclaringType()).Name) + "::" + md.GetString(f.Name);
                }
                case HandleKind.TypeDefinition: return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name);
                case HandleKind.TypeReference: return md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name);
                case HandleKind.TypeSpecification: return "typespec#" + MetadataTokens.GetRowNumber(h);
                case HandleKind.MemberReference:
                {
                    var mr = md.GetMemberReference((MemberReferenceHandle)h);
                    var parent = mr.Parent.Kind == HandleKind.TypeReference ? md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name) : mr.Parent.Kind.ToString();
                    return parent + "::" + md.GetString(mr.Name);
                }
                case HandleKind.MethodSpecification: return "methodspec#" + MetadataTokens.GetRowNumber(h);
                case HandleKind.StandaloneSignature: return "sig#" + MetadataTokens.GetRowNumber(h);
                case HandleKind.UserString: return "\"" + md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF)) + "\"";
            }
        }
        catch (Exception) { /* fall through */ }
        return "0x" + token.ToString("x8");
    }

    private static char OperandKind(ILOpCode op)
    {
        switch (op)
        {
            case ILOpCode.Ldarg_s: case ILOpCode.Ldarga_s: case ILOpCode.Starg_s: case ILOpCode.Ldloc_s: case ILOpCode.Ldloca_s: case ILOpCode.Stloc_s:
            case ILOpCode.Ldc_i4_s: case ILOpCode.Unaligned: return 'b';
            case ILOpCode.Ldarg: case ILOpCode.Ldarga: case ILOpCode.Starg: case ILOpCode.Ldloc: case ILOpCode.Ldloca: case ILOpCode.Stloc: return 'w';
            case ILOpCode.Ldc_i4: return 'i';
            case ILOpCode.Ldc_i8: return 'l';
            case ILOpCode.Ldc_r4: return 'r';
            case ILOpCode.Ldc_r8: return 'd';
            case ILOpCode.Br_s: case ILOpCode.Brfalse_s: case ILOpCode.Brtrue_s: case ILOpCode.Beq_s: case ILOpCode.Bge_s: case ILOpCode.Bgt_s: case ILOpCode.Ble_s:
            case ILOpCode.Blt_s: case ILOpCode.Bne_un_s: case ILOpCode.Bge_un_s: case ILOpCode.Bgt_un_s: case ILOpCode.Ble_un_s: case ILOpCode.Blt_un_s: case ILOpCode.Leave_s:
                return 'B';
            case ILOpCode.Br: case ILOpCode.Brfalse: case ILOpCode.Brtrue: case ILOpCode.Beq: case ILOpCode.Bge: case ILOpCode.Bgt: case ILOpCode.Ble: case ILOpCode.Blt:
            case ILOpCode.Bne_un: case ILOpCode.Bge_un: case ILOpCode.Bgt_un: case ILOpCode.Ble_un: case ILOpCode.Blt_un: case ILOpCode.Leave:
                return 'J';
            case ILOpCode.Switch: return 's';
            case ILOpCode.Jmp: case ILOpCode.Call: case ILOpCode.Calli: case ILOpCode.Callvirt: case ILOpCode.Cpobj: case ILOpCode.Ldobj: case ILOpCode.Ldstr:
            case ILOpCode.Newobj: case ILOpCode.Castclass: case ILOpCode.Isinst: case ILOpCode.Unbox: case ILOpCode.Ldfld: case ILOpCode.Ldflda: case ILOpCode.Stfld:
            case ILOpCode.Ldsfld: case ILOpCode.Ldsflda: case ILOpCode.Stsfld: case ILOpCode.Stobj: case ILOpCode.Box: case ILOpCode.Newarr: case ILOpCode.Ldelema:
            case ILOpCode.Ldelem: case ILOpCode.Stelem: case ILOpCode.Unbox_any: case ILOpCode.Refanyval: case ILOpCode.Mkrefany: case ILOpCode.Ldtoken:
            case ILOpCode.Ldftn: case ILOpCode.Ldvirtftn: case ILOpCode.Initobj: case ILOpCode.Constrained: case ILOpCode.Sizeof:
                return 't';
            default: return 'n';
        }
    }
}
