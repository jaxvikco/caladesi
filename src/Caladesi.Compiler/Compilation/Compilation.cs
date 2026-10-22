using System.Collections.Immutable;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed class SourceAssemblySymbol(Compilation compilation) : AssemblySymbol
{
    private readonly Compilation _compilation = compilation;

    public override string Name => _compilation.Options.AssemblyName;
    public override Version Version => _compilation.Options.AssemblyVersion;
    public override NamespaceSymbol GlobalNamespace => _compilation.GlobalNamespace;
    public override bool IsFromSource => true;
}

/// <summary>A set of source files plus references: the unit of semantic analysis and emit.</summary>
public sealed partial class Compilation
{
    private readonly Dictionary<SyntaxTree, SourceFile> _files = [];
    private readonly Dictionary<string, SourceFile> _filesByPath = new(StringComparer.Ordinal);
    private readonly NamedTypeSymbol?[] _specialTypes = new NamedTypeSymbol?[Enum.GetValues<SpecialType>().Length + 1];
    private readonly Dictionary<string, NamedTypeSymbol?> _wellKnown = new(StringComparer.Ordinal);
    private ImmutableArray<Diagnostic> _diagnostics;
    private bool _declared;
    private readonly Lock _lock = new();

    private Compilation(CompilationOptions options, ImmutableArray<SyntaxTree> trees, ReferenceSet references)
    {
        Options = options;
        SyntaxTrees = trees;
        References = references;
        SourceAssembly = new SourceAssemblySymbol(this);
        GlobalNamespace = new NamespaceSymbol("", null, references.GlobalNamespace);
        foreach (SyntaxTree t in trees)
        {
            SourceFile f = new(this, t);
            _files[t] = f;
            _filesByPath[NormalizePath(t.FilePath)] = f;
        }
    }

    public CompilationOptions Options { get; }
    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public ReferenceSet References { get; }
    public SourceAssemblySymbol SourceAssembly { get; }
    public NamespaceSymbol GlobalNamespace { get; }
    internal DiagnosticBag DeclarationDiagnostics { get; } = new();
    internal DiagnosticBag BodyDiagnostics { get; } = new();

    public static Compilation Create(CompilationOptions options, IEnumerable<SyntaxTree> trees, ReferenceSet references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return new(options, [.. trees], references);
    }

    public static Compilation Create(CompilationOptions options, IEnumerable<SyntaxTree> trees, IEnumerable<string> referencePaths)
    {
        return new(options, [.. trees], ReferenceSet.Create(referencePaths));
    }

    public Compilation WithSyntaxTrees(IEnumerable<SyntaxTree> trees)
    {
        return new(Options, [.. trees], References);
    }

    public Compilation WithOptions(CompilationOptions options)
    {
        return new(options, SyntaxTrees, References);
    }

    internal SourceFile GetSourceFile(SyntaxTree tree)
    {
        return _files[tree];
    }

    public IEnumerable<SourceFile> SourceFiles => SyntaxTrees.Select(t => _files[t]);

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
    }

    internal SourceFile? FindSourceFile(SourceFile from, string spec)
    {
        string dir = Path.GetDirectoryName(NormalizePath(from.FilePath)) ?? "";
        string combined = Path.GetFullPath(Path.Combine(dir, spec));
        foreach (string? candidate in new[] { combined, combined + ".cala", Path.Combine(combined, "index.cala") })
        {
            if (_filesByPath.TryGetValue(candidate, out SourceFile? f))
            {
                return f;
            }
        }

        return null;
    }

    internal NamedTypeSymbol? FindTypeByFullName(string fullName)
    {
        int lastDot = fullName.LastIndexOf('.');
        if (lastDot < 0)
        {
            return null;
        }

        NamespaceSymbol? ns = GlobalNamespace.FindNamespace(fullName[..lastDot]);
        string name = fullName[(lastDot + 1)..];
        return ns?.GetTypeMembersByName(name).FirstOrDefault();
    }

    // ------------------------------------------------------------------ well-known types

    public NamedTypeSymbol GetSpecialType(SpecialType t)
    {
        int i = (int)t;
        lock (_lock)
        {
            return _specialTypes[i] ??= References.GetSpecialType(t) ?? ErrorTypeSymbol.Instance;
        }
    }

    public NamedTypeSymbol? GetWellKnownType(string metadataName)
    {
        lock (_lock)
        {
            if (_wellKnown.TryGetValue(metadataName, out NamedTypeSymbol? t))
            {
                return t;
            }

            t = FindWellKnown(metadataName);
            _wellKnown[metadataName] = t;
            return t;
        }
    }

    private NamedTypeSymbol? FindWellKnown(string metadataName)
    {
        NamedTypeSymbol? t = References.FindType(metadataName);
        return t;
    }

    public NamedTypeSymbol RequireWellKnownType(string metadataName)
    {
        return GetWellKnownType(metadataName) ?? new ErrorTypeSymbol(metadataName);
    }

    internal ArrayTypeSymbol CreateArrayType(TypeSymbol element, int rank = 1)
    {
        return new(element, GetSpecialType(SpecialType.Array), rank);
    }

    internal Dictionary<SyntaxNode, Symbol>? RecordedSymbols { get; private set; }
    internal Dictionary<SyntaxNode, TypeSymbol>? RecordedTypes { get; private set; }
    /// <summary>Declared locals, parameters and local functions by (tree, identifier start).</summary>
    internal Dictionary<(SyntaxTree, int), Symbol>? RecordedDeclarations { get; private set; }

    internal void EnsureRecording()
    {
        RecordedSymbols ??= new(ReferenceEqualityComparer.Instance);
        RecordedTypes ??= new(ReferenceEqualityComparer.Instance);
        RecordedDeclarations ??= [];
    }

    internal Binder CreateBinder(SourceFile file, Symbol? containing, DiagnosticBag? diagnostics)
    {
        Binder binder = new(this, file, containing, diagnostics);
        if (Options.RecordSemanticInfo)
        {
            EnsureRecording();
            binder.SymbolMap = RecordedSymbols;
            binder.TypeMap = RecordedTypes;
            binder.DeclarationMap = RecordedDeclarations;
        }
        return binder;
    }

    // ------------------------------------------------------------------ declarations

    private void EnsureDeclarations()
    {
        lock (_lock)
        {
            if (_declared)
            {
                return;
            }

            _declared = true;
            Dictionary<(string ns, string name), SourceNamedType> allTypeNames = [];
            foreach (SyntaxTree tree in SyntaxTrees)
            {
                SourceFile file = _files[tree];
                file.Namespace = GlobalNamespace.GetOrAddNamespace(file.NamespaceName);
                CollectFile(file, allTypeNames);
            }

            // module classes: avoid clashes with declared types
            foreach (SyntaxTree tree in SyntaxTrees)
            {
                SourceFile file = _files[tree];
                if (!FileNeedsModuleClass(file))
                {
                    continue;
                }

                string name = file.ModuleClassBaseName;
                if (allTypeNames.ContainsKey((file.NamespaceName, name))
                    || SyntaxTrees.Any(t => t != tree && _files[t].NamespaceName == file.NamespaceName && FileNeedsModuleClass(_files[t]) && _files[t].ModuleClassBaseName == name && string.CompareOrdinal(t.FilePath, tree.FilePath) < 0))
                {
                    name += "Module";
                }

                SourceModuleClassSymbol module = new(this, file, name);
                file.ModuleClass = module;
                file.Namespace.AddType(module);
            }
        }
    }

    private static bool FileNeedsModuleClass(SourceFile file)
    {
        foreach (StatementSyntax s in file.Tree.Root.Statements)
        {
            switch (s)
            {
                case ImportDeclarationSyntax or TypeAliasDeclarationSyntax or ClassDeclarationSyntax or InterfaceDeclarationSyntax
                    or EnumDeclarationSyntax or EmptyStatementSyntax:
                    break;
                default:
                    return true;
            }
        }
        return false;
    }

    private void CollectFile(SourceFile file, Dictionary<(string, string), SourceNamedType> allTypeNames)
    {
        ImmutableArray<SourceNamedType>.Builder types = ImmutableArray.CreateBuilder<SourceNamedType>();
        ImmutableArray<TypeAliasSymbol>.Builder aliases = ImmutableArray.CreateBuilder<TypeAliasSymbol>();
        ImmutableArray<ImportDeclarationSyntax>.Builder imports = ImmutableArray.CreateBuilder<ImportDeclarationSyntax>();
        ImmutableArray<StatementSyntax>.Builder statements = ImmutableArray.CreateBuilder<StatementSyntax>();

        void Register(SourceNamedType t, SyntaxToken id)
        {
            (string NamespaceName, string Name) key = (file.NamespaceName, t.Name);
            if (allTypeNames.ContainsKey(key))
            {
                DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.DuplicateType, file.FilePath, id.Span, t.Name, file.NamespaceName));
                return;
            }
            allTypeNames[key] = t;
            types.Add(t);
            file.Namespace.AddType(t);
        }

        foreach (StatementSyntax s in file.Tree.Root.Statements)
        {
            switch (s)
            {
                case ImportDeclarationSyntax imp:
                    imports.Add(imp);
                    break;
                case ClassDeclarationSyntax c when !c.Identifier.IsMissing:
                    Register(new SourceClassSymbol(this, file, c, file.Namespace), c.Identifier);
                    break;
                case InterfaceDeclarationSyntax i when !i.Identifier.IsMissing:
                    Register(new SourceInterfaceSymbol(this, file, i, file.Namespace), i.Identifier);
                    break;
                case EnumDeclarationSyntax e when !e.Identifier.IsMissing:
                    Register(new SourceEnumSymbol(this, file, e, file.Namespace), e.Identifier);
                    break;
                case TypeAliasDeclarationSyntax a when !a.Identifier.IsMissing:
                    {
                        AliasCreation created = TypeAliasDeclarations.Create(this, file, a);
                        if (created.Type != null)
                        {
                            Register(created.Type, a.Identifier);
                        }

                        if (created.Alias != null)
                        {
                            aliases.Add(created.Alias);
                        }

                        break;
                    }
                case FunctionDeclarationSyntax or VariableStatementSyntax or EmptyStatementSyntax:
                    break;
                default:
                    statements.Add(s);
                    break;
            }
        }
        file.Types = types.ToImmutable();
        file.Aliases = aliases.ToImmutable();
        file.Imports = imports.ToImmutable();
        file.TopLevelStatements = statements.ToImmutable();
    }

    internal IEnumerable<SourceNamedType> AllSourceTypes
    {
        get
        {
            EnsureDeclarations();
            foreach (SourceFile f in SourceFiles)
            {
                foreach (SourceNamedType t in f.Types)
                {
                    yield return t;
                }

                if (f.ModuleClass != null)
                {
                    yield return f.ModuleClass;
                }
            }
        }
    }

    /// <summary>Gets (declaring if needed) the source file model for a tree.</summary>
    public SourceFile GetFile(SyntaxTree tree)
    {
        EnsureDeclarations();
        return _files[tree];
    }

    // ------------------------------------------------------------------ diagnostics

    public ImmutableArray<Diagnostic> GetDiagnostics()
    {
        lock (_lock)
        {
            if (!_diagnostics.IsDefault)
            {
                return _diagnostics;
            }

            EnsureDeclarations();
            List<Diagnostic> all = [];
            foreach (SyntaxTree t in SyntaxTrees)
            {
                all.AddRange(t.Diagnostics);
            }

            foreach (SourceFile f in SourceFiles)
            {
                all.AddRange(f.Scope.GetImportDiagnostics());
            }

            DeclarationChecker checker = new(this);
            checker.CheckAll();
            all.AddRange(DeclarationDiagnostics.Items);
            all.AddRange(BodyDiagnostics.Items);
            all.AddRange(checker.Diagnostics);

            EntryPointChecker entry = new(this);
            all.AddRange(entry.Check());

            _diagnostics = ApplyOptions(all);
            return _diagnostics;
        }
    }

    private ImmutableArray<Diagnostic> ApplyOptions(List<Diagnostic> all)
    {
        List<Diagnostic> result = [];
        HashSet<(string?, int, int, string)> seen = [];
        foreach (Diagnostic d in all)
        {
            if (!seen.Add((d.FilePath, d.Span.Start, d.Span.Length, d.Id + d.Message)))
            {
                continue;
            }

            if (Options.NoWarn.Contains(d.Id, StringComparer.OrdinalIgnoreCase) && d.EffectiveSeverity == DiagnosticSeverity.Warning)
            {
                continue;
            }

            Diagnostic dd = d;
            if (Options.WarningsAsErrors && d.EffectiveSeverity == DiagnosticSeverity.Warning)
            {
                dd = d.WithSeverity(DiagnosticSeverity.Error);
            }

            result.Add(dd);
        }
        result.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.FilePath, b.FilePath);
            if (c != 0)
            {
                return c;
            }

            c = a.Span.Start.CompareTo(b.Span.Start);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        return [.. result];
    }

    /// <summary>Emits the assembly (and optionally a portable PDB). Fails without writing when there are errors.</summary>
    public Emit.EmitResult Emit(Stream peStream, Stream? pdbStream = null, string? pdbFileName = null)
    {
        return new Emit.EmitModule(this).Emit(peStream, pdbStream, pdbFileName);
    }

    private Dictionary<(SyntaxTree, int), Symbol>? _declarationIndex;

    /// <summary>Every declared source symbol (types, members, locals, parameters) by the start of its identifier.</summary>
    internal Dictionary<(SyntaxTree, int), Symbol> GetDeclarationIndex()
    {
        if (_declarationIndex != null)
        {
            return _declarationIndex;
        }

        Dictionary<(SyntaxTree, int), Symbol> index = [];
        if (RecordedDeclarations != null)
        {
            foreach (KeyValuePair<(SyntaxTree, int), Symbol> kv in RecordedDeclarations)
            {
                index[kv.Key] = kv.Value;
            }
        }

        void Add(SyntaxTree tree, SyntaxToken? token, Symbol symbol)
        {
            if (token != null && !token.IsMissing)
            {
                _ = index.TryAdd((tree, token.Start), symbol);
            }
        }
        foreach (SourceNamedType? type in AllSourceTypes.ToList())
        {
            if (type is SourceModuleClassSymbol) { }
            else
            {
                Add(type.File.Tree, type.IdentifierToken, type);
            }

            foreach (Symbol m in type.GetMembers())
            {
                switch (m)
                {
                    case SourceMethodSymbol sm when sm.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet):
                        Add(type.File.Tree, DeclarationIdentifier(sm.Declaration), sm);
                        break;
                    case SourcePropertySymbol sp:
                        Add(type.File.Tree, DeclarationIdentifier(sp.Declaration), sp);
                        break;
                    case SourceFieldSymbol sf when !sf.IsBackingField:
                        Add(type.File.Tree, DeclarationIdentifier(sf.Declaration), sf);
                        break;
                }
            }
        }
        foreach (SourceFile file in SourceFiles)
        {
            foreach (TypeAliasSymbol alias in file.Aliases)
            {
                Add(file.Tree, alias.Syntax.Identifier, alias);
            }
        }

        return _declarationIndex = index;
    }

    internal static SyntaxToken? DeclarationIdentifier(SyntaxNode? decl)
    {
        return decl switch
        {
            FunctionDeclarationSyntax f => f.Identifier,
            MethodDeclarationSyntax m => m.Identifier,
            PropertyDeclarationSyntax p => p.Identifier,
            AccessorDeclarationSyntax a => a.Identifier,
            ClassDeclarationSyntax c => c.Identifier,
            InterfaceDeclarationSyntax i => i.Identifier,
            EnumDeclarationSyntax e => e.Identifier,
            EnumMemberSyntax em => em.Identifier,
            TypeAliasDeclarationSyntax t => t.Identifier,
            VariableDeclaratorSyntax v => v.Identifier,
            ParameterSyntax p => p.Identifier,
            _ => null,
        };
    }

    public Semantics.SemanticModel GetSemanticModel(SyntaxTree tree)
    {
        return new(this, tree);
    }

    private readonly Dictionary<DecoratorSyntax, BoundAttribute?> _boundAttributes = new(ReferenceEqualityComparer.Instance);

    /// <summary>Binds a decorator once; diagnostics land in the body diagnostics.</summary>
    internal BoundAttribute? GetBoundAttribute(SourceFile file, DecoratorSyntax decorator, Symbol? context)
    {
        lock (_lock)
        {
            if (_boundAttributes.TryGetValue(decorator, out BoundAttribute? existing))
            {
                return existing;
            }

            Binder binder = CreateBinder(file, context, BodyDiagnostics);
            BoundAttribute? bound = binder.BindDecorator(decorator);
            _boundAttributes[decorator] = bound;
            return bound;
        }
    }

    internal string? GetAssemblyNameOf(NamedTypeSymbol type)
    {
        return type.IsFromSource ? Options.AssemblyName : type.ContainingAssembly?.Name;
    }

    public bool HasErrors => GetDiagnostics().Any(d => d.EffectiveSeverity == DiagnosticSeverity.Error);
}
