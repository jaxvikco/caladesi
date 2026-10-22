using Caladesi.Compiler;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Syntax;
using Caladesi.LanguageServer.Projects;
using Caladesi.LanguageServer.Protocol;

namespace Caladesi.LanguageServer;

internal sealed class OpenDocument
{
    public OpenDocument(string uri, string path, string text, int version)
    {
        Uri = uri;
        Path = path;
        Text = text;
        Version = version;
    }

    public string Uri { get; }
    public string Path { get; }
    public string Text { get; set; }
    public int Version { get; set; }
}

/// <summary>Open documents, project discovery and the compilation units. Only touched from the worker thread.</summary>
internal sealed class Workspace
{
    private readonly ServerOptions _options;
    private readonly ProjectLoader _loader;
    private readonly Action<string> _showMessage;
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _diskCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationUnit> _projects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failedProjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationUnit> _standalone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationUnit> _unitByDocument = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _projectByDirectory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationUnit> _publishedOwner = new(StringComparer.Ordinal);
    private ReferenceSet? _standaloneReferences;

    public Workspace(ServerOptions options, Action<string> showMessage)
    {
        _options = options;
        _loader = new ProjectLoader(options);
        _showMessage = showMessage;
    }

    public List<string> Roots { get; } = [];
    public HashSet<CompilationUnit> Dirty { get; } = [];

    public IEnumerable<OpenDocument> OpenDocuments => _documents.Values;

    // ------------------------------------------------------------------ paths

    public static string UriToPath(string uri)
    {
        if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return uri;
        try { return Path.GetFullPath(new Uri(uri).LocalPath); }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or PathTooLongException) { return uri; }
    }

    public string PathToUri(string path)
    {
        if (_documents.TryGetValue(path, out var doc)) return doc.Uri;
        return RawPathToUri(path);
    }

    public static string RawPathToUri(string path)
    {
        if (!Path.IsPathRooted(path)) return path;
        try { return new Uri(path).AbsoluteUri; }
        catch (UriFormatException) { return path; }
    }

    // ------------------------------------------------------------------ documents

    public bool IsWorkspaceFile(string path) => _documents.ContainsKey(path) || File.Exists(path);

    public OpenDocument? GetDocument(string uri) => _documents.GetValueOrDefault(UriToPath(uri));

    public string? GetText(string path)
    {
        if (_documents.TryGetValue(path, out var doc)) return doc.Text;
        if (_diskCache.TryGetValue(path, out var cached)) return cached;
        string? text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { text = null; }
        _diskCache[path] = text;
        return text;
    }

    public void Open(string uri, string text, int version)
    {
        var path = UriToPath(uri);
        _documents[path] = new OpenDocument(uri, path, text, version);
        Assign(path);
    }

    public void Change(OpenDocument document, string text, int version)
    {
        document.Text = text;
        document.Version = version;
        if (_unitByDocument.TryGetValue(document.Path, out var unit)) Dirty.Add(unit);
    }

    public void Close(string uri)
    {
        var path = UriToPath(uri);
        if (!_documents.ContainsKey(path)) return;
        if (_unitByDocument.Remove(path, out var unit))
        {
            unit.OpenFiles.Remove(path);
            unit.MarkChanged();
            if (!unit.IsProject)
            {
                _standalone.Remove(path);
                Dirty.Remove(unit);
                RetireDiagnostics(unit);
            }
            else Dirty.Add(unit);
        }
        _documents.Remove(path);
        _diskCache.Remove(path);
    }

    // ------------------------------------------------------------------ units

    public CompilationUnit? GetUnit(string uri) => _unitByDocument.GetValueOrDefault(UriToPath(uri));

    private void Assign(string path)
    {
        if (_unitByDocument.Remove(path, out var old)) old.OpenFiles.Remove(path);
        var unit = ResolveUnit(path);
        unit.OpenFiles.Add(path);
        unit.MarkChanged();
        _unitByDocument[path] = unit;
        Dirty.Add(unit);
    }

    private CompilationUnit ResolveUnit(string path)
    {
        if (FindProjectFile(path) is { } projectPath && !_failedProjects.ContainsKey(projectPath))
        {
            if (_projects.TryGetValue(projectPath, out var existing)) return existing;
            var loaded = LoadProject(projectPath);
            if (loaded != null)
            {
                _projects[projectPath] = loaded;
                return loaded;
            }
        }
        if (_standalone.TryGetValue(path, out var unit)) return unit;
        unit = CreateStandalone(path);
        _standalone[path] = unit;
        return unit;
    }

    private CompilationUnit CreateStandalone(string path)
    {
        if (_standaloneReferences == null)
        {
            var paths = StandaloneReferences.Find(_options, out var warning);
            if (warning != null) _showMessage(warning);
            _standaloneReferences = ReferenceSet.Create(paths);
        }
        var directory = Path.IsPathRooted(path) ? Path.GetDirectoryName(path) : null;
        var options = new CompilationOptions
        {
            AssemblyName = Path.GetFileNameWithoutExtension(path) is { Length: > 0 } n ? n : "Caladesi",
            RootNamespace = "",
            ProjectDirectory = directory,
            RecordSemanticInfo = true,
        };
        return new CompilationUnit(path, null, options, _standaloneReferences, []);
    }

    private CompilationUnit? LoadProject(string projectPath)
    {
        var result = _loader.Load(projectPath);
        if (result.Data == null)
        {
            var message = "Caladesi: could not load " + Path.GetFileName(projectPath) + ", using standalone mode. " + result.Error;
            Log.Write(message);
            _failedProjects[projectPath] = result.Error ?? "";
            _showMessage(message);
            return null;
        }
        var data = result.Data;
        var options = data.ToCompilationOptions(projectPath);
        var references = ReferenceSet.Create(data.References);
        SyntaxTree? generated = null;
        if (data.Property("CaladesiGenerateTestEntryPoint").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            try { generated = TestEntryPoint.Create(options, data.TestHooks); }
            catch (Exception ex) { Log.Write("test entry point generation failed: " + ex.Message); }
        }
        return new CompilationUnit(projectPath, projectPath, options, references, data.Compile.Select(Path.GetFullPath), generated);
    }

    public string? FindProjectFile(string path)
    {
        if (!Path.IsPathRooted(path)) return null;
        var directory = Path.GetDirectoryName(path);
        var root = Roots.Where(r => IsUnder(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        var visited = new List<string>();
        string? found = null;
        while (directory != null)
        {
            if (_projectByDirectory.TryGetValue(directory, out var cached))
            {
                found = cached;
                break;
            }
            visited.Add(directory);
            found = FirstProjectIn(directory);
            if (found != null) break;
            if (root == null || string.Equals(directory, root, StringComparison.Ordinal)) break;
            directory = Path.GetDirectoryName(directory);
        }
        foreach (var d in visited) _projectByDirectory[d] = found;
        return found;
    }

    private static string? FirstProjectIn(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.calaproj").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsUnder(string path, string root)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar);
        return path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal) || string.Equals(path, r, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ file system changes

    /// <summary>Applies <c>workspace/didChangeWatchedFiles</c>; returns true when anything relevant changed.</summary>
    public bool FilesChanged(IEnumerable<(string Path, int Type)> changes)
    {
        var reloadProjects = false;
        var any = false;
        foreach (var (path, type) in changes)
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".calaproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith("project.assets.json", StringComparison.Ordinal))
            {
                reloadProjects = true;
                any = true;
            }
            else if (extension.Equals(".cala", StringComparison.OrdinalIgnoreCase))
            {
                any = true;
                _diskCache.Remove(path);
                if (type != 2) reloadProjects = true;
                foreach (var unit in _unitByDocument.Values.Distinct()) if (unit.IsMember(path)) unit.MarkChanged();
                foreach (var unit in _projects.Values) if (unit.IsMember(path)) { unit.MarkChanged(); Dirty.Add(unit); }
            }
        }
        if (reloadProjects) ReloadAll();
        foreach (var unit in _unitByDocument.Values.Distinct()) Dirty.Add(unit);
        return any;
    }

    private void ReloadAll()
    {
        var oldUnits = _projects.Values.Concat(_standalone.Values).Distinct().ToList();
        _projects.Clear();
        _failedProjects.Clear();
        _projectByDirectory.Clear();
        _standalone.Clear();
        foreach (var unit in oldUnits)
        {
            Dirty.Remove(unit);
            RetireDiagnostics(unit);
        }
        var open = _documents.Keys.ToList();
        _unitByDocument.Clear();
        foreach (var path in open) Assign(path);
    }

    // ------------------------------------------------------------------ diagnostics bookkeeping

    public readonly record struct PublishPlan(string Uri, IReadOnlyList<Diagnostic> Diagnostics);

    /// <summary>Groups diagnostics by file and lists files that must be cleared because they no longer have any.</summary>
    public List<PublishPlan> PlanPublish(CompilationUnit unit, IEnumerable<Diagnostic> diagnostics)
    {
        var plans = new List<PublishPlan>();
        var members = new HashSet<string>(unit.AllPaths(), StringComparer.Ordinal);
        var byFile = new Dictionary<string, List<Diagnostic>>(StringComparer.Ordinal);
        foreach (var d in diagnostics)
        {
            if (d.FilePath == null || !members.Contains(d.FilePath)) continue;
            if (d.EffectiveSeverity == DiagnosticSeverity.Hidden) continue;
            if (!byFile.TryGetValue(d.FilePath, out var list)) byFile[d.FilePath] = list = [];
            list.Add(d);
        }
        foreach (var (path, list) in byFile)
        {
            _publishedOwner[path] = unit;
            plans.Add(new PublishPlan(PathToUri(path), list));
        }
        foreach (var path in _publishedOwner.Where(kv => kv.Value == unit && !byFile.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList())
        {
            _publishedOwner.Remove(path);
            plans.Add(new PublishPlan(PathToUri(path), []));
        }
        return plans;
    }

    private readonly List<PublishPlan> _retired = [];

    private void RetireDiagnostics(CompilationUnit unit)
    {
        foreach (var path in _publishedOwner.Where(kv => kv.Value == unit).Select(kv => kv.Key).ToList())
        {
            _publishedOwner.Remove(path);
            _retired.Add(new PublishPlan(PathToUri(path), []));
        }
    }

    public bool HasRetired => _retired.Count > 0;

    public List<PublishPlan> TakeRetired()
    {
        var result = _retired.ToList();
        _retired.Clear();
        return result;
    }
}
