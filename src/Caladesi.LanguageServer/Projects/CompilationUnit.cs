using Caladesi.Compiler;
using Caladesi.Compiler.Semantics;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Syntax;

namespace Caladesi.LanguageServer.Projects;

/// <summary>
/// One <see cref="Compilation"/> worth of state: either an <c>.calaproj</c> or a single loose file. The reference set is created
/// once; the compilation is rebuilt whenever a source text changes.
/// </summary>
internal sealed class CompilationUnit
{
    private readonly Dictionary<string, (string Text, SyntaxTree Tree)> _trees = new(StringComparer.Ordinal);
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();
    private readonly SyntaxTree? _generated;
    private Compilation? _compilation;
    private bool _filesChanged = true;

    public CompilationUnit(string key, string? projectPath, CompilationOptions options, ReferenceSet references, IEnumerable<string> files, SyntaxTree? generated = null)
    {
        Key = key;
        ProjectPath = projectPath;
        Options = options;
        References = references;
        ProjectFiles = [.. files];
        _generated = generated;
    }

    public string Key { get; }
    public string? ProjectPath { get; }
    public bool IsProject => ProjectPath != null;
    public CompilationOptions Options { get; }
    public ReferenceSet References { get; }
    public List<string> ProjectFiles { get; }
    public HashSet<string> OpenFiles { get; } = new(StringComparer.Ordinal);

    public bool IsMember(string path) => OpenFiles.Contains(path) || ProjectFiles.Contains(path);

    public void MarkChanged() => _filesChanged = true;

    public IEnumerable<string> AllPaths()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in ProjectFiles) if (seen.Add(p)) yield return p;
        foreach (var p in OpenFiles.OrderBy(p => p, StringComparer.Ordinal)) if (seen.Add(p)) yield return p;
    }

    public Compilation GetCompilation(Workspace workspace)
    {
        var changed = _compilation == null || _filesChanged;
        var paths = AllPaths().ToList();
        var trees = new List<SyntaxTree>(paths.Count + 1);
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var text = workspace.GetText(path);
            if (text == null) continue;
            live.Add(path);
            if (_trees.TryGetValue(path, out var cached) && string.Equals(cached.Text, text, StringComparison.Ordinal))
            {
                trees.Add(cached.Tree);
                continue;
            }
            var tree = SyntaxTree.ParseText(text, path);
            _trees[path] = (text, tree);
            trees.Add(tree);
            changed = true;
        }
        foreach (var stale in _trees.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _trees.Remove(stale);
            changed = true;
        }
        if (_generated != null) trees.Add(_generated);

        if (changed)
        {
            _compilation = Compilation.Create(Options, trees, References);
            _models.Clear();
            _filesChanged = false;
        }
        return _compilation!;
    }

    public bool HasTree(string path) => _trees.ContainsKey(path);

    public SyntaxTree? GetTree(Workspace workspace, string path)
    {
        var compilation = GetCompilation(workspace);
        return compilation.SyntaxTrees.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.Ordinal));
    }

    public SemanticModel GetModel(Compilation compilation, SyntaxTree tree)
    {
        if (!_models.TryGetValue(tree, out var model))
        {
            model = compilation.GetSemanticModel(tree);
            _models[tree] = model;
        }
        return model;
    }
}
