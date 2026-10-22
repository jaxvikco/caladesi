using Caladesi.Compiler;
using Caladesi.Compiler.Semantics;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Syntax;

namespace Caladesi.LanguageServer.Projects;

/// <summary>
/// One <see cref="Compilation"/> worth of state: either an <c>.calaproj</c> or a single loose file. The reference set is created
/// once; the compilation is rebuilt whenever a source text changes.
/// </summary>
internal sealed class CompilationUnit(string key, string? projectPath, CompilationOptions options, ReferenceSet references, IEnumerable<string> files, SyntaxTree? generated = null)
{
    private readonly Dictionary<string, (string Text, SyntaxTree Tree)> _trees = new(StringComparer.Ordinal);
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = [];
    private readonly SyntaxTree? _generated = generated;
    private Compilation? _compilation;
    private bool _filesChanged = true;

    public string Key { get; } = key;
    public string? ProjectPath { get; } = projectPath;
    public bool IsProject => ProjectPath != null;
    public CompilationOptions Options { get; } = options;
    public ReferenceSet References { get; } = references;
    public List<string> ProjectFiles { get; } = [.. files];
    public HashSet<string> OpenFiles { get; } = new(StringComparer.Ordinal);

    public bool IsMember(string path)
    {
        return OpenFiles.Contains(path) || ProjectFiles.Contains(path);
    }

    public void MarkChanged()
    {
        _filesChanged = true;
    }

    public IEnumerable<string> AllPaths()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string p in ProjectFiles)
        {
            if (seen.Add(p))
            {
                yield return p;
            }
        }

        foreach (string? p in OpenFiles.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (seen.Add(p))
            {
                yield return p;
            }
        }
    }

    public Compilation GetCompilation(Workspace workspace)
    {
        bool changed = _compilation == null || _filesChanged;
        List<string> paths = [.. AllPaths()];
        List<SyntaxTree> trees = new(paths.Count + 1);
        HashSet<string> live = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string? text = workspace.GetText(path);
            if (text == null)
            {
                continue;
            }

            _ = live.Add(path);
            if (_trees.TryGetValue(path, out (string Text, SyntaxTree Tree) cached) && string.Equals(cached.Text, text, StringComparison.Ordinal))
            {
                trees.Add(cached.Tree);
                continue;
            }
            SyntaxTree tree = SyntaxTree.ParseText(text, path);
            _trees[path] = (text, tree);
            trees.Add(tree);
            changed = true;
        }
        foreach (string? stale in _trees.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _ = _trees.Remove(stale);
            changed = true;
        }
        if (_generated != null)
        {
            trees.Add(_generated);
        }

        if (changed)
        {
            _compilation = Compilation.Create(Options, trees, References);
            _models.Clear();
            _filesChanged = false;
        }
        return _compilation!;
    }

    public bool HasTree(string path)
    {
        return _trees.ContainsKey(path);
    }

    public SyntaxTree? GetTree(Workspace workspace, string path)
    {
        Compilation compilation = GetCompilation(workspace);
        return compilation.SyntaxTrees.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.Ordinal));
    }

    public SemanticModel GetModel(Compilation compilation, SyntaxTree tree)
    {
        if (!_models.TryGetValue(tree, out SemanticModel? model))
        {
            model = compilation.GetSemanticModel(tree);
            _models[tree] = model;
        }
        return model;
    }
}
