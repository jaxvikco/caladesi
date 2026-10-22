using System.Diagnostics.CodeAnalysis;
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
        MethodDefinition def = md.GetMethodDefinition(handle);
        StringBuilder sb = new();
        _ = sb.AppendLine(md.GetString(md.GetTypeDefinition(def.GetDeclaringType()).Name) + "::" + md.GetString(def.Name));
        if (def.RelativeVirtualAddress == 0)
        {
            return sb.AppendLine("  <no body>").ToString();
        }

        MethodBodyBlock body = pe.GetMethodBody(def.RelativeVirtualAddress);
        byte[] il = body.GetILBytes()!;
        byte[] arr = il;
        int pos = 0;
        while (pos < arr.Length)
        {
            int start = pos;
            int code = arr[pos++];
            if (code == 0xFE)
            {
                code = 0xFE00 | arr[pos++];
            }

            ILOpCode op = (ILOpCode)code;
            string name = op.ToString().ToLowerInvariant().Replace('_', '.');
            _ = sb.Append($"  IL_{start:x4}: {name}");
            switch (OperandKind(op))
            {
                case 'n': break;
                case 'b': _ = sb.Append($" {(sbyte)arr[pos]}"); pos += 1; break;
                case 'B': _ = sb.Append($" IL_{pos + 1 + (sbyte)arr[pos]:x4}"); pos += 1; break;
                case 'w': _ = sb.Append($" {BitConverter.ToUInt16(arr, pos)}"); pos += 2; break;
                case 'i': _ = sb.Append($" {BitConverter.ToInt32(arr, pos)}"); pos += 4; break;
                case 'r': _ = sb.Append($" {BitConverter.ToSingle(arr, pos)}"); pos += 4; break;
                case 'l': _ = sb.Append($" {BitConverter.ToInt64(arr, pos)}"); pos += 8; break;
                case 'd': _ = sb.Append($" {BitConverter.ToDouble(arr, pos)}"); pos += 8; break;
                case 'J': _ = sb.Append($" IL_{pos + 4 + BitConverter.ToInt32(arr, pos):x4}"); pos += 4; break;
                case 't':
                    {
                        int token = BitConverter.ToInt32(arr, pos);
                        pos += 4;
                        _ = sb.Append(' ').Append(Describe(md, token));
                        break;
                    }
                case 's':
                    {
                        int n = BitConverter.ToInt32(arr, pos);
                        pos += 4 + (4 * n);
                        _ = sb.Append($" switch({n})");
                        break;
                    }
            }
            _ = sb.AppendLine();
        }
        return sb.ToString();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort pretty printer for failing test output: any metadata read failure falls back to the raw token.")]
    private static string Describe(MetadataReader md, int token)
    {
        try
        {
            EntityHandle h = MetadataTokens.EntityHandle(token);
            switch (h.Kind)
            {
                case HandleKind.MethodDefinition:
                    {
                        MethodDefinition m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                        return md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name) + "::" + md.GetString(m.Name);
                    }
                case HandleKind.FieldDefinition:
                    {
                        FieldDefinition f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                        return md.GetString(md.GetTypeDefinition(f.GetDeclaringType()).Name) + "::" + md.GetString(f.Name);
                    }
                case HandleKind.TypeDefinition: return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name);
                case HandleKind.TypeReference: return md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name);
                case HandleKind.TypeSpecification: return "typespec#" + MetadataTokens.GetRowNumber(h);
                case HandleKind.MemberReference:
                    {
                        MemberReference mr = md.GetMemberReference((MemberReferenceHandle)h);
                        string parent = mr.Parent.Kind == HandleKind.TypeReference ? md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name) : mr.Parent.Kind.ToString();
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
        return op switch
        {
            ILOpCode.Ldarg_s or ILOpCode.Ldarga_s or ILOpCode.Starg_s or ILOpCode.Ldloc_s or ILOpCode.Ldloca_s or ILOpCode.Stloc_s or ILOpCode.Ldc_i4_s or ILOpCode.Unaligned => 'b',
            ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg or ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Stloc => 'w',
            ILOpCode.Ldc_i4 => 'i',
            ILOpCode.Ldc_i8 => 'l',
            ILOpCode.Ldc_r4 => 'r',
            ILOpCode.Ldc_r8 => 'd',
            ILOpCode.Br_s or ILOpCode.Brfalse_s or ILOpCode.Brtrue_s or ILOpCode.Beq_s or ILOpCode.Bge_s or ILOpCode.Bgt_s or ILOpCode.Ble_s or ILOpCode.Blt_s or ILOpCode.Bne_un_s or ILOpCode.Bge_un_s or ILOpCode.Bgt_un_s or ILOpCode.Ble_un_s or ILOpCode.Blt_un_s or ILOpCode.Leave_s => 'B',
            ILOpCode.Br or ILOpCode.Brfalse or ILOpCode.Brtrue or ILOpCode.Beq or ILOpCode.Bge or ILOpCode.Bgt or ILOpCode.Ble or ILOpCode.Blt or ILOpCode.Bne_un or ILOpCode.Bge_un or ILOpCode.Bgt_un or ILOpCode.Ble_un or ILOpCode.Blt_un or ILOpCode.Leave => 'J',
            ILOpCode.Switch => 's',
            ILOpCode.Jmp or ILOpCode.Call or ILOpCode.Calli or ILOpCode.Callvirt or ILOpCode.Cpobj or ILOpCode.Ldobj or ILOpCode.Ldstr or ILOpCode.Newobj or ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Unbox or ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld or ILOpCode.Stobj or ILOpCode.Box or ILOpCode.Newarr or ILOpCode.Ldelema or ILOpCode.Ldelem or ILOpCode.Stelem or ILOpCode.Unbox_any or ILOpCode.Refanyval or ILOpCode.Mkrefany or ILOpCode.Ldtoken or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Initobj or ILOpCode.Constrained or ILOpCode.Sizeof => 't',
            _ => 'n',
        };
    }
}
