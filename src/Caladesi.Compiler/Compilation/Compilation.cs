using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler;

public sealed class SourceAssemblySymbol : AssemblySymbol
{
    private readonly Compilation _compilation;
    public SourceAssemblySymbol(Compilation compilation) => _compilation = compilation;
    public override string Name => _compilation.Options.AssemblyName;
    public override Version Version => _compilation.Options.AssemblyVersion;
    public override NamespaceSymbol GlobalNamespace => _compilation.GlobalNamespace;
    public override bool IsFromSource => true;
}

/// <summary>A set of source files plus references: the unit of semantic analysis and emit.</summary>
public sealed partial class Compilation
{
    private readonly Dictionary<SyntaxTree, SourceFile> _files = new();
    private readonly Dictionary<string, SourceFile> _filesByPath = new(StringComparer.Ordinal);
    private readonly NamedTypeSymbol?[] _specialTypes = new NamedTypeSymbol?[Enum.GetValues<SpecialType>().Length + 1];
    private readonly Dictionary<string, NamedTypeSymbol?> _wellKnown = new(StringComparer.Ordinal);
    private ImmutableArray<Diagnostic> _diagnostics;
    private bool _declared;
    private readonly object _lock = new();

    private Compilation(CompilationOptions options, ImmutableArray<SyntaxTree> trees, ReferenceSet references)
    {
        Options = options;
        SyntaxTrees = trees;
        References = references;
        SourceAssembly = new SourceAssemblySymbol(this);
        GlobalNamespace = new NamespaceSymbol("", null, references.GlobalNamespace);
        foreach (var t in trees)
        {
            var f = new SourceFile(this, t);
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

    public static Compilation Create(CompilationOptions options, IEnumerable<SyntaxTree> trees, ReferenceSet references) =>
        new(options, trees.ToImmutableArray(), references);

    public static Compilation Create(CompilationOptions options, IEnumerable<SyntaxTree> trees, IEnumerable<string> referencePaths) =>
        new(options, trees.ToImmutableArray(), ReferenceSet.Create(referencePaths));

    public Compilation WithSyntaxTrees(IEnumerable<SyntaxTree> trees) => new(Options, trees.ToImmutableArray(), References);
    public Compilation WithOptions(CompilationOptions options) => new(options, SyntaxTrees, References);

    internal SourceFile GetSourceFile(SyntaxTree tree) => _files[tree];
    public IEnumerable<SourceFile> SourceFiles => SyntaxTrees.Select(t => _files[t]);

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
    }

    internal SourceFile? FindSourceFile(SourceFile from, string spec)
    {
        var dir = Path.GetDirectoryName(NormalizePath(from.FilePath)) ?? "";
        var combined = Path.GetFullPath(Path.Combine(dir, spec));
        foreach (var candidate in new[] { combined, combined + ".cala", Path.Combine(combined, "index.cala") })
            if (_filesByPath.TryGetValue(candidate, out var f)) return f;
        return null;
    }

    internal NamedTypeSymbol? FindTypeByFullName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        if (lastDot < 0) return null;
        var ns = GlobalNamespace.FindNamespace(fullName[..lastDot]);
        var name = fullName[(lastDot + 1)..];
        return ns?.GetTypeMembersByName(name).FirstOrDefault();
    }

    // ------------------------------------------------------------------ well-known types

    public NamedTypeSymbol GetSpecialType(SpecialType t)
    {
        var i = (int)t;
        lock (_lock)
        {
            return _specialTypes[i] ??= References.GetSpecialType(t) ?? ErrorTypeSymbol.Instance;
        }
    }

    public NamedTypeSymbol? GetWellKnownType(string metadataName)
    {
        lock (_lock)
        {
            if (_wellKnown.TryGetValue(metadataName, out var t)) return t;
            t = FindWellKnown(metadataName);
            _wellKnown[metadataName] = t;
            return t;
        }
    }

    private NamedTypeSymbol? FindWellKnown(string metadataName)
    {
        var t = References.FindType(metadataName);
        if (t != null) return t;
        return null;
    }

    public NamedTypeSymbol RequireWellKnownType(string metadataName) =>
        GetWellKnownType(metadataName) ?? new ErrorTypeSymbol(metadataName);

    internal ArrayTypeSymbol CreateArrayType(TypeSymbol element, int rank = 1) =>
        new(element, GetSpecialType(SpecialType.Array), rank);

    internal Binder CreateBinder(SourceFile file, Symbol? containing, DiagnosticBag? diagnostics) =>
        new(this, file, containing, diagnostics);

    // ------------------------------------------------------------------ declarations

    private void EnsureDeclarations()
    {
        lock (_lock)
        {
            if (_declared) return;
            _declared = true;
            var allTypeNames = new Dictionary<(string ns, string name), SourceNamedType>();
            foreach (var tree in SyntaxTrees)
            {
                var file = _files[tree];
                file.Namespace = GlobalNamespace.GetOrAddNamespace(file.NamespaceName);
                CollectFile(file, allTypeNames);
            }

            // module classes: avoid clashes with declared types
            foreach (var tree in SyntaxTrees)
            {
                var file = _files[tree];
                if (!FileNeedsModuleClass(file)) continue;
                var name = file.ModuleClassBaseName;
                if (allTypeNames.ContainsKey((file.NamespaceName, name))
                    || SyntaxTrees.Any(t => t != tree && _files[t].NamespaceName == file.NamespaceName && FileNeedsModuleClass(_files[t]) && _files[t].ModuleClassBaseName == name && string.CompareOrdinal(t.FilePath, tree.FilePath) < 0))
                    name += "Module";
                var module = new SourceModuleClassSymbol(this, file, name);
                file.ModuleClass = module;
                file.Namespace.AddType(module);
            }
        }
    }

    private static bool FileNeedsModuleClass(SourceFile file)
    {
        foreach (var s in file.Tree.Root.Statements)
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
        var types = ImmutableArray.CreateBuilder<SourceNamedType>();
        var aliases = ImmutableArray.CreateBuilder<TypeAliasSymbol>();
        var imports = ImmutableArray.CreateBuilder<ImportDeclarationSyntax>();
        var statements = ImmutableArray.CreateBuilder<StatementSyntax>();

        void Register(SourceNamedType t, SyntaxToken id)
        {
            var key = (file.NamespaceName, t.Name);
            if (allTypeNames.ContainsKey(key))
            {
                DeclarationDiagnostics.Add(Diagnostic.Create(Descriptors.DuplicateType, file.FilePath, id.Span, t.Name, file.NamespaceName));
                return;
            }
            allTypeNames[key] = t;
            types.Add(t);
            file.Namespace.AddType(t);
        }

        foreach (var s in file.Tree.Root.Statements)
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
                    var created = TypeAliasDeclarations.Create(this, file, a);
                    if (created.Type != null) Register(created.Type, a.Identifier);
                    if (created.Alias != null) aliases.Add(created.Alias);
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
            foreach (var f in SourceFiles)
            {
                foreach (var t in f.Types) yield return t;
                if (f.ModuleClass != null) yield return f.ModuleClass;
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
            if (!_diagnostics.IsDefault) return _diagnostics;
            EnsureDeclarations();
            var all = new List<Diagnostic>();
            foreach (var t in SyntaxTrees) all.AddRange(t.Diagnostics);
            foreach (var f in SourceFiles) all.AddRange(f.Scope.GetImportDiagnostics());

            var checker = new DeclarationChecker(this);
            checker.CheckAll();
            all.AddRange(DeclarationDiagnostics.Items);
            all.AddRange(BodyDiagnostics.Items);
            all.AddRange(checker.Diagnostics);

            var entry = new EntryPointChecker(this);
            all.AddRange(entry.Check());

            _diagnostics = ApplyOptions(all);
            return _diagnostics;
        }
    }

    private ImmutableArray<Diagnostic> ApplyOptions(List<Diagnostic> all)
    {
        var result = new List<Diagnostic>();
        var seen = new HashSet<(string?, int, int, string)>();
        foreach (var d in all)
        {
            if (!seen.Add((d.FilePath, d.Span.Start, d.Span.Length, d.Id + d.Message))) continue;
            if (Options.NoWarn.Contains(d.Id, StringComparer.OrdinalIgnoreCase) && d.EffectiveSeverity == DiagnosticSeverity.Warning) continue;
            var dd = d;
            if (Options.WarningsAsErrors && d.EffectiveSeverity == DiagnosticSeverity.Warning) dd = d.WithSeverity(DiagnosticSeverity.Error);
            result.Add(dd);
        }
        result.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.FilePath, b.FilePath);
            if (c != 0) return c;
            c = a.Span.Start.CompareTo(b.Span.Start);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        return result.ToImmutableArray();
    }

    /// <summary>Emits the assembly (and optionally a portable PDB). Fails without writing when there are errors.</summary>
    public Emit.EmitResult Emit(Stream peStream, Stream? pdbStream = null, string? pdbFileName = null) =>
        new Emit.EmitModule(this).Emit(peStream, pdbStream, pdbFileName);

    public bool HasErrors => GetDiagnostics().Any(d => d.EffectiveSeverity == DiagnosticSeverity.Error);
}
