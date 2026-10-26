using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Semantics;

public sealed partial class SemanticModel
{
    private static readonly string[] s_keywords =
    [
        "abstract", "as", "async", "await", "break", "case", "catch", "class", "const", "constructor", "continue", "default", "do", "else", "enum",
        "export", "extends", "false", "finally", "for", "function", "get", "if", "implements", "import", "instanceof", "interface", "let",
        "new", "null", "of", "override", "private", "protected", "readonly", "return", "satisfies", "sealed", "set", "static", "struct", "super",
        "switch", "this", "throw", "true", "try", "type", "typeof", "using", "while", "yield",
    ];

    private static bool IsIdentChar(char c)
    {
        return char.IsLetterOrDigit(c) || c is '_' or '$';
    }

    public IReadOnlyList<CompletionEntry> GetCompletions(int position)
    {
        List<CompletionEntry> entries = [];
        HashSet<string> seen = [];
        void Add(string label, CompletionKind kind, string? detail = null, string? insert = null)
        {
            if (label.Length == 0 || label.Contains('<') || label.Contains('$') || label.Contains('.'))
            {
                return;
            }

            if (seen.Add(label))
            {
                entries.Add(new CompletionEntry(label, kind, detail, insert));
            }
        }

        string text = Tree.Text.Text;
        position = Math.Clamp(position, 0, text.Length);
        int prefixStart = position;
        while (prefixStart > 0 && IsIdentChar(text[prefixStart - 1]))
        {
            prefixStart--;
        }

        if (prefixStart > 0 && text[prefixStart - 1] == '.')
        {
            int dotPos = prefixStart - 1;
            MemberCompletions(dotPos, Add);
            return entries;
        }

        // names in scope: locals and parameters, then module-level names, then keywords
        foreach (KeyValuePair<(SyntaxTree, int), Symbol> kv in _declared)
        {
            if (kv.Key.Item1 != Tree || kv.Value is not (LocalSymbol or ParameterSymbol))
            {
                continue;
            }

            if (kv.Key.Item2 >= position)
            {
                continue;
            }

            SyntaxToken? token = Tree.Root.FindToken(kv.Key.Item2);
            if (token == null)
            {
                continue;
            }

            SyntaxNode? scope = ScopeNodeOf(token);
            if (scope == null || scope.Start > position || scope.End < position)
            {
                continue;
            }

            Add(kv.Value.Name, kv.Value is ParameterSymbol ? CompletionKind.Variable : CompletionKind.Variable, SymbolDisplay.Describe(kv.Value));
        }
        SourceFile file = Compilation.GetFile(Tree);
        foreach ((string? name, Symbol? symbol) in file.Scope.VisibleNames())
        {
            Add(name, KindOf(symbol), SymbolDisplay.Describe(symbol));
        }

        foreach (string k in s_keywords)
        {
            Add(k, CompletionKind.Keyword);
        }

        return entries;
    }

    private static SyntaxNode? ScopeNodeOf(SyntaxToken declaration)
    {
        bool isParameter = declaration.Parent is ParameterSyntax;
        for (SyntaxNode? n = declaration.Parent; n != null; n = n.Parent)
        {
            if (isParameter && n is FunctionDeclarationSyntax or MethodDeclarationSyntax or ConstructorDeclarationSyntax or ArrowFunctionSyntax or FunctionExpressionSyntax or AccessorDeclarationSyntax)
            {
                return n;
            }

            if (!isParameter && n is BlockSyntax or ForStatementSyntax or ForOfStatementSyntax or CatchClauseSyntax or SwitchSectionSyntax or CompilationUnitSyntax)
            {
                return n;
            }
        }
        return null;
    }

    private static CompletionKind KindOf(Symbol s)
    {
        return s.OriginalDefinition switch
        {
            NamespaceSymbol or FileModuleSymbol => CompletionKind.Module,
            NamedTypeSymbol { TypeKind: TypeKind.Interface } => CompletionKind.Interface,
            NamedTypeSymbol { TypeKind: TypeKind.Enum } => CompletionKind.Enum,
            NamedTypeSymbol { TypeKind: TypeKind.Struct } => CompletionKind.Struct,
            NamedTypeSymbol => CompletionKind.Class,
            TypeAliasSymbol => CompletionKind.TypeAlias,
            TypeParameterSymbol => CompletionKind.TypeParameter,
            MethodSymbol m => m.ContainingSymbol is SourceModuleClassSymbol || m.ContainingType == null ? CompletionKind.Function : CompletionKind.Method,
            PropertySymbol => CompletionKind.Property,
            FieldSymbol { IsConst: true, ContainingType.TypeKind: TypeKind.Enum } => CompletionKind.EnumMember,
            FieldSymbol { IsConst: true } => CompletionKind.Constant,
            FieldSymbol => CompletionKind.Field,
            _ => CompletionKind.Variable,
        };
    }

    private Symbol? SymbolOfExpression(SyntaxNode expr)
    {
        Dictionary<SyntaxNode, Symbol>? recorded = Compilation.RecordedSymbols;
        if (recorded == null)
        {
            return null;
        }

        SyntaxNode key = expr switch { MemberAccessExpressionSyntax ma => ma.Name, QualifiedNameSyntax q => q, _ => expr };
        return recorded.TryGetValue(key, out Symbol? s)
            ? s
            : expr is QualifiedNameSyntax q2 && recorded.TryGetValue(q2.Right, out s) ? s : null;
    }

    private void MemberCompletions(int dotPosition, Action<string, CompletionKind, string?, string?> add)
    {
        SyntaxToken? dot = Tree.Root.FindToken(dotPosition);
        if (dot == null)
        {
            return;
        }

        SyntaxNode? receiver = dot.Parent switch
        {
            MemberAccessExpressionSyntax ma when ma.OperatorToken.Start == dot.Start => ma.Expression,
            QualifiedNameSyntax q when q.DotToken.Start == dot.Start => q.Left,
            _ => null,
        };
        if (receiver == null)
        {
            return;
        }

        Symbol? symbol = SymbolOfExpression(receiver);
        TypeSymbol? type = null;
        if (Compilation.RecordedTypes != null)
        {
            _ = Compilation.RecordedTypes.TryGetValue(receiver, out type);
        }

        switch (symbol)
        {
            case NamespaceSymbol ns:
                foreach (NamespaceSymbol child in ns.GetNamespaceMembers())
                {
                    add(child.Name, CompletionKind.Module, null, null);
                }

                foreach (NamedTypeSymbol? t in ns.GetTypeMembers().Where(t => t.DeclaredAccessibility == Accessibility.Public))
                {
                    add(t.Name, KindOf(t), null, null);
                }

                return;
            case FileModuleSymbol fm:
                foreach (Symbol export in fm.File.Scope.AllExports())
                {
                    add(export.Name, KindOf(export), SymbolDisplay.Describe(export), null);
                }

                return;
            case NamedTypeSymbol typeRef:
                AddMembers(typeRef, staticOnly: true, add);
                return;
        }
        if (type == null)
        {
            type = symbol switch { LocalSymbol l => l.Type, ParameterSymbol p => p.Type, FieldSymbol f => f.Type, PropertySymbol p => p.Type, _ => null };
        }

        if (type == null || type.IsErrorType)
        {
            return;
        }

        type = type.StripNullable();
        AddMembers(type, staticOnly: false, add);
        AddExtensionMethods(type, add);
    }

    private static bool IsUserVisible(Symbol m)
    {
        return m.Name.Length != 0 && m.Name[0] != '<' && !m.Name.Contains('$') && !m.Name.Contains('.') && (m.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
            || (m.DeclaredAccessibility == Accessibility.Internal && m.IsFromSource));
    }

    private void AddMembers(TypeSymbol type, bool staticOnly, Action<string, CompletionKind, string?, string?> add)
    {
        MemberLookup.Walk(type, Compilation.GetSpecialType(SpecialType.Object), t =>
        {
            foreach (Symbol m in t.GetMembers())
            {
                if (!IsUserVisible(m))
                {
                    continue;
                }

                switch (m)
                {
                    case MethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction } method when method.IsStatic == staticOnly:
                        add(m.Name, CompletionKind.Method, SymbolDisplay.Signature(method), null);
                        break;
                    case PropertySymbol { IsIndexer: false } p when p.IsStatic == staticOnly:
                        add(m.Name, CompletionKind.Property, p.Type.ToDisplayString(), null);
                        break;
                    case FieldSymbol f when f.IsStatic == staticOnly || (staticOnly && f.IsConst):
                        add(m.Name, KindOf(f), f.Type.ToDisplayString(), null);
                        break;
                    case NamedTypeSymbol nt when staticOnly:
                        add(m.Name, KindOf(nt), null, null);
                        break;
                }
            }
            return !(staticOnly && t is NamedTypeSymbol { TypeKind: TypeKind.Enum });
        });
        if (!staticOnly && type is ArrayTypeSymbol) { add("Length", CompletionKind.Property, "int", null); }
    }

    private void AddExtensionMethods(TypeSymbol receiver, Action<string, CompletionKind, string?, string?> add)
    {
        SourceFile file = Compilation.GetFile(Tree);
        List<NamedTypeSymbol> containers = [];
        foreach (Symbol w in file.Scope.WildcardImports)
        {
            switch (w)
            {
                case NamespaceSymbol ns:
                    containers.AddRange(ns.GetTypeMembers().Where(t => t.IsStaticClass));
                    break;
                case NamedTypeSymbol nt:
                    containers.Add(nt);
                    break;
                case FileModuleSymbol fm when fm.File.ModuleClass != null:
                    containers.Add(fm.File.ModuleClass);
                    break;
            }
        }
        foreach (Symbol s in file.Scope.NamedImportSymbols)
        {
            if (s is MethodSymbol { IsExtensionMethod: true } m && m.ContainingType != null)
            {
                containers.Add(m.ContainingType);
            }
        }

        if (file.ModuleClass != null)
        {
            containers.Add(file.ModuleClass);
        }

        foreach (NamedTypeSymbol? c in containers.Distinct())
        {
            foreach (MethodSymbol m in c.GetMembers().OfType<MethodSymbol>())
            {
                if (!m.IsExtensionMethod || !m.IsStatic || m.Parameters.Length == 0 || !IsUserVisible(m))
                {
                    continue;
                }

                if (!ExtensionApplies(receiver, m.Parameters[0].Type))
                {
                    continue;
                }

                add(m.Name, CompletionKind.Method, SymbolDisplay.Signature(m), null);
            }
        }
    }

    private static bool ExtensionApplies(TypeSymbol receiver, TypeSymbol parameter)
    {
        parameter = parameter.StripNullable();
        if (parameter is TypeParameterSymbol)
        {
            return true;
        }

        if (receiver.Equals(parameter))
        {
            return true;
        }

        if (parameter is ArrayTypeSymbol pa)
        {
            return receiver is ArrayTypeSymbol ra && (pa.ElementType is TypeParameterSymbol || pa.ElementType.Equals(ra.ElementType));
        }

        NamedTypeSymbol? target = (parameter as NamedTypeSymbol)?.OriginalDefinition;
        if (target == null)
        {
            return false;
        }

        if (receiver is ArrayTypeSymbol)
        {
            return target.SpecialType != SpecialType.None || target.MetadataName.StartsWith("IEnumerable", StringComparison.Ordinal) || target.MetadataName.StartsWith("IList", StringComparison.Ordinal);
        }

        for (TypeSymbol? t = receiver; t != null; t = t.BaseType)
        {
            if ((t as NamedTypeSymbol)?.OriginalDefinition.Equals(target) == true)
            {
                return true;
            }

            foreach (NamedTypeSymbol i in t.AllInterfaces)
            {
                if (i.OriginalDefinition.Equals(target))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ signature help

    public SignatureHelpInfo? GetSignatureHelp(int position)
    {
        SyntaxToken? token = Tree.Root.FindToken(Math.Max(0, position - 1)) ?? Tree.Root.FindToken(position);
        for (SyntaxNode? n = token?.Parent; n != null; n = n.Parent)
        {
            ArgumentListSyntax? args = n switch { InvocationExpressionSyntax i => i.Arguments, NewExpressionSyntax ne => ne.Arguments, _ => null };
            if (args == null || position <= args.Start || position > args.End)
            {
                continue;
            }

            if (args.GetLastToken() is { IsMissing: false } close && close.Kind == SyntaxKind.CloseParenToken && position > close.Start)
            {
                continue;
            }

            List<MethodSymbol> methods = [];
            if (n is InvocationExpressionSyntax inv)
            {
                SyntaxNode calleeName = inv.Expression is MemberAccessExpressionSyntax ma ? (SyntaxNode)ma.Name : inv.Expression;
                Symbol? sym = Compilation.RecordedSymbols != null && Compilation.RecordedSymbols.TryGetValue(calleeName, out Symbol? s) ? s : null;
                if (sym is MethodSymbol method)
                {
                    CollectOverloads(method, methods);
                }
            }
            else if (n is NewExpressionSyntax ne && Compilation.RecordedSymbols != null
                     && Compilation.RecordedSymbols.TryGetValue(ne.Type, out Symbol? ts) && ts is NamedTypeSymbol nt)
            {
                methods.AddRange(nt.InstanceConstructors.Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected || c.IsFromSource));
            }

            if (methods.Count == 0)
            {
                continue;
            }

            int active = 0;
            for (int i = 0; i < args.Arguments.SeparatorCount; i++)
            {
                if (args.Arguments.GetSeparator(i).End <= position)
                {
                    active++;
                }
            }

            List<MethodSymbol> ordered = [.. methods.OrderBy(m => m.Parameters.Length)];
            int activeSig = Math.Max(0, ordered.FindIndex(m => m.Parameters.Length > active || (m.Parameters.Length > 0 && m.Parameters[^1].IsParams)));
            return new SignatureHelpInfo([.. ordered.Select(m => new SignatureEntry(SymbolDisplay.Signature(m), SymbolDisplay.Parameters(m)))], activeSig, active);
        }
        return null;
    }

    private static void CollectOverloads(MethodSymbol method, List<MethodSymbol> into)
    {
        Symbol? owner = method.ContainingSymbol;
        if (owner is NamedTypeSymbol type)
        {
            for (NamedTypeSymbol? t = type; t != null; t = t.BaseType)
            {
                foreach (MethodSymbol m in t.GetMembers(method.Name).OfType<MethodSymbol>())
                {
                    if (IsUserVisible(m) && !into.Any(x => x.Parameters.Length == m.Parameters.Length && SymbolDisplay.Signature(x) == SymbolDisplay.Signature(m)))
                    {
                        into.Add(m);
                    }
                }

                if (t is SourceModuleClassSymbol)
                {
                    break;
                }
            }
        }
        if (into.Count == 0)
        {
            into.Add(method);
        }
    }
}
