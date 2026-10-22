using System.Text;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Semantics;

/// <summary>Human readable descriptions of symbols for hover text and signature help.</summary>
public static class SymbolDisplay
{
    public static string Describe(Symbol symbol)
    {
        switch (symbol.OriginalDefinition)
        {
            case NamespaceSymbol ns: return "namespace " + ns.ToDisplayString();
            case NamedTypeSymbol t: return DescribeType(t);
            case MethodSymbol m: return DescribeMethod(m);
            case PropertySymbol p: return $"(property) {Owner(p)}{p.Name}: {p.Type.ToDisplayString()}" + (p.SetMethod == null ? " (readonly)" : "");
            case FieldSymbol f:
                return (f.IsConst ? "(constant) " : "(field) ") + $"{Owner(f)}{f.Name}: {f.Type.ToDisplayString()}";
            case ParameterSymbol p: return $"(parameter) {p.Name}: {p.Type.ToDisplayString()}";
            case LocalSymbol l: return (l.IsConst ? "(constant) " : "(variable) ") + $"{l.Name}: {l.Type.ToDisplayString()}";
            case TypeParameterSymbol tp: return "(type parameter) " + tp.Name;
            case TypeAliasSymbol a: return $"type {a.Name} = {a.Target.ToDisplayString()}";
            case FileModuleSymbol fm: return "module " + fm.Name;
        }
        return symbol.Kind + " " + symbol.Name;
    }

    private static string Owner(Symbol s) => s.ContainingSymbol is NamedTypeSymbol { IsStaticClass: false } t && s.ContainingSymbol is not SourceModuleClassSymbol ? t.Name + "." : "";

    private static string DescribeType(NamedTypeSymbol t)
    {
        var kind = t.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Struct => t.IsUnionType ? "union" : "struct",
            TypeKind.Delegate => "delegate",
            _ => t.IsRecord ? "type" : t.IsAbstract && !t.IsStatic ? "abstract class" : "class",
        };
        var sb = new StringBuilder();
        sb.Append(kind).Append(' ').Append(t.ToDisplayString());
        if (t.TypeKind == TypeKind.Class && t.BaseType is { } b && b.SpecialType != SpecialType.Object)
            sb.Append(" extends ").Append(b.ToDisplayString());
        if (t.IsUnionType) sb.Append(" = ").Append(string.Join(" | ", t.UnionCases.Select(c => c.ToDisplayString())));
        return sb.ToString();
    }

    public static string DescribeMethod(MethodSymbol m)
    {
        var sb = new StringBuilder();
        switch (m.MethodKind)
        {
            case MethodKind.Constructor:
                sb.Append("constructor ").Append(m.ContainingType?.Name);
                break;
            case MethodKind.PropertyGet or MethodKind.PropertySet:
                sb.Append("(accessor) ").Append(m.Name);
                break;
            default:
                sb.Append(m.ContainingSymbol is SourceModuleClassSymbol || m.ContainingType == null ? "function " : "(method) " + (m.ContainingType.Name + "."));
                sb.Append(m.Name);
                break;
        }
        if (m.TypeParameters.Length > 0) sb.Append('<').Append(string.Join(", ", m.TypeParameters.Select(t => t.Name))).Append('>');
        sb.Append('(').Append(string.Join(", ", Parameters(m))).Append(')');
        if (m.MethodKind != MethodKind.Constructor) sb.Append(": ").Append(m.ReturnType.ToDisplayString());
        return sb.ToString();
    }

    public static IReadOnlyList<string> Parameters(MethodSymbol m)
    {
        var list = new List<string>();
        foreach (var p in m.Parameters)
        {
            var s = (p.IsParams ? "..." : "") + p.Name + (p.IsOptional && !p.IsParams ? "?" : "") + ": " + (p.IsParams && p.Type is ArrayTypeSymbol a ? a.ElementType.ToDisplayString() + "[]" : p.Type.ToDisplayString());
            list.Add(s);
        }
        return list;
    }

    public static string Signature(MethodSymbol m)
    {
        var name = m.MethodKind == MethodKind.Constructor ? "new " + m.ContainingType?.Name : m.Name;
        var ret = m.MethodKind == MethodKind.Constructor ? "" : ": " + m.ReturnType.ToDisplayString();
        return name + "(" + string.Join(", ", Parameters(m)) + ")" + ret;
    }
}
