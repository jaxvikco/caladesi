using System.Text;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Semantics;

/// <summary>Human readable descriptions of symbols for hover text and signature help.</summary>
public static class SymbolDisplay
{
    public static string Describe(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol.OriginalDefinition switch
        {
            NamespaceSymbol ns => "namespace " + ns.ToDisplayString(),
            NamedTypeSymbol t => DescribeType(t),
            MethodSymbol m => DescribeMethod(m),
            PropertySymbol p => $"(property) {Owner(p)}{p.Name}: {p.Type.ToDisplayString()}" + (p.SetMethod == null ? " (readonly)" : ""),
            FieldSymbol f => (f.IsConst ? "(constant) " : "(field) ") + $"{Owner(f)}{f.Name}: {f.Type.ToDisplayString()}",
            ParameterSymbol p => $"(parameter) {p.Name}: {p.Type.ToDisplayString()}",
            LocalSymbol l => (l.IsConst ? "(constant) " : "(variable) ") + $"{l.Name}: {l.Type.ToDisplayString()}",
            TypeParameterSymbol tp => "(type parameter) " + tp.Name,
            TypeAliasSymbol a => $"type {a.Name} = {a.Target.ToDisplayString()}",
            FileModuleSymbol fm => "module " + fm.Name,
            _ => symbol.Kind + " " + symbol.Name,
        };
    }

    private static string Owner(Symbol s)
    {
        return s.ContainingSymbol is NamedTypeSymbol { IsStaticClass: false } t and not SourceModuleClassSymbol ? t.Name + "." : "";
    }

    private static string DescribeType(NamedTypeSymbol t)
    {
        string kind = t.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Struct => t.IsUnionType ? "union" : "struct",
            TypeKind.Delegate => "delegate",
            _ => t.IsRecord ? "type" : t.IsAbstract && !t.IsStatic ? "abstract class" : "class",
        };
        StringBuilder sb = new();
        _ = sb.Append(kind).Append(' ').Append(t.ToDisplayString());
        if (t.TypeKind == TypeKind.Class && t.BaseType is { } b && b.SpecialType != SpecialType.Object)
        {
            _ = sb.Append(" extends ").Append(b.ToDisplayString());
        }

        if (t.IsUnionType)
        {
            _ = sb.Append(" = ").Append(string.Join(" | ", t.UnionCases.Select(c => c.ToDisplayString())));
        }

        return sb.ToString();
    }

    public static string DescribeMethod(MethodSymbol m)
    {
        ArgumentNullException.ThrowIfNull(m);
        StringBuilder sb = new();
        switch (m.MethodKind)
        {
            case MethodKind.Constructor:
                _ = sb.Append("constructor ").Append(m.ContainingType?.Name);
                break;
            case MethodKind.PropertyGet or MethodKind.PropertySet:
                _ = sb.Append("(accessor) ").Append(m.Name);
                break;
            default:
                _ = sb.Append(m.ContainingSymbol is SourceModuleClassSymbol || m.ContainingType == null ? "function " : "(method) " + m.ContainingType.Name + ".");
                _ = sb.Append(m.Name);
                break;
        }
        if (m.TypeParameters.Length > 0)
        {
            _ = sb.Append('<').Append(string.Join(", ", m.TypeParameters.Select(t => t.Name))).Append('>');
        }

        _ = sb.Append('(').Append(string.Join(", ", Parameters(m))).Append(')');
        if (m.MethodKind != MethodKind.Constructor)
        {
            _ = sb.Append(": ").Append(m.ReturnType.ToDisplayString());
        }

        return sb.ToString();
    }

    public static IReadOnlyList<string> Parameters(MethodSymbol m)
    {
        ArgumentNullException.ThrowIfNull(m);
        List<string> list = [];
        foreach (ParameterSymbol p in m.Parameters)
        {
            string s = (p.IsParams ? "..." : "") + p.Name + (p.IsOptional && !p.IsParams ? "?" : "") + ": " + (p.IsParams && p.Type is ArrayTypeSymbol a ? a.ElementType.ToDisplayString() + "[]" : p.Type.ToDisplayString());
            list.Add(s);
        }
        return list;
    }

    public static string Signature(MethodSymbol m)
    {
        ArgumentNullException.ThrowIfNull(m);
        string name = m.MethodKind == MethodKind.Constructor ? "new " + m.ContainingType?.Name : m.Name;
        string ret = m.MethodKind == MethodKind.Constructor ? "" : ": " + m.ReturnType.ToDisplayString();
        return name + "(" + string.Join(", ", Parameters(m)) + ")" + ret;
    }
}
