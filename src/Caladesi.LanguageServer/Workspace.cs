using System.Diagnostics.CodeAnalysis;
using Caladesi.Compiler;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Syntax;
using Caladesi.LanguageServer.Projects;
using Caladesi.LanguageServer.Protocol;

namespace Caladesi.LanguageServer;

internal sealed class OpenDocument(string uri, string path, string text, int version)
{
    public string Uri { get; } = uri;
    public string Path { get; } = path;
    public string Text { get; set; } = text;
    public int Version { get; set; } = version;
}

/// <summary>Open documents, project discovery and the compilation units. Only touched from the worker thread.</summary>
internal sealed class Workspace(ServerOptions options, Action<string> showMessage)
{
    private readonly ProjectLoader _loader = new(options);
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _diskCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationUnit> _projects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failedProjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationUnit> _standalone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationUnit> _unitByDocument = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _projectByDirectory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationUnit> _publishedOwner = new(StringComparer.Ordinal);
    private ReferenceSet? _standaloneReferences;

    public List<string> Roots { get; } = [];
    public HashSet<CompilationUnit> Dirty { get; } = [];

    public IEnumerable<OpenDocument> OpenDocuments => _documents.Values;

    // ------------------------------------------------------------------ paths

    public static string UriToPath(string uri)
    {
        if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        try { return Path.GetFullPath(new Uri(uri).LocalPath); }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or PathTooLongException) { return uri; }
    }

    public string PathToUri(string path)
    {
        return _documents.TryGetValue(path, out OpenDocument? doc) ? doc.Uri : RawPathToUri(path);
    }

    public static string RawPathToUri(string path)
    {
        if (!Path.IsPathRooted(path))
        {
            return path;
        }

        try { return new Uri(path).AbsoluteUri; }
        catch (UriFormatException) { return path; }
    }

    // ------------------------------------------------------------------ documents

    public bool IsWorkspaceFile(string path)
    {
        return _documents.ContainsKey(path) || File.Exists(path);
    }

    public OpenDocument? GetDocument(string uri)
    {
        return _documents.GetValueOrDefault(UriToPath(uri));
    }

    public string? GetText(string path)
    {
        if (_documents.TryGetValue(path, out OpenDocument? doc))
        {
            return doc.Text;
        }

        if (_diskCache.TryGetValue(path, out string? cached))
        {
            return cached;
        }

        string? text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { text = null; }
        _diskCache[path] = text;
        return text;
    }

    public void Open(string uri, string text, int version)
    {
        string path = UriToPath(uri);
        _documents[path] = new OpenDocument(uri, path, text, version);
        Assign(path);
    }

    public void Change(OpenDocument document, string text, int version)
    {
        document.Text = text;
        document.Version = version;
        if (_unitByDocument.TryGetValue(document.Path, out CompilationUnit? unit))
        {
            _ = Dirty.Add(unit);
        }
    }

    public void Close(string uri)
    {
        string path = UriToPath(uri);
        if (!_documents.ContainsKey(path))
        {
            return;
        }

        if (_unitByDocument.Remove(path, out CompilationUnit? unit))
        {
            _ = unit.OpenFiles.Remove(path);
            unit.MarkChanged();
            if (!unit.IsProject)
            {
                _ = _standalone.Remove(path);
                _ = Dirty.Remove(unit);
                RetireDiagnostics(unit);
            }
            else
            {
                _ = Dirty.Add(unit);
            }
        }
        _ = _documents.Remove(path);
        _ = _diskCache.Remove(path);
    }

    // ------------------------------------------------------------------ units

    public CompilationUnit? GetUnit(string uri)
    {
        return _unitByDocument.GetValueOrDefault(UriToPath(uri));
    }

    private void Assign(string path)
    {
        if (_unitByDocument.Remove(path, out CompilationUnit? old))
        {
            _ = old.OpenFiles.Remove(path);
        }

        CompilationUnit unit = ResolveUnit(path);
        _ = unit.OpenFiles.Add(path);
        unit.MarkChanged();
        _unitByDocument[path] = unit;
        _ = Dirty.Add(unit);
    }

    private CompilationUnit ResolveUnit(string path)
    {
        if (FindProjectFile(path) is { } projectPath && !_failedProjects.ContainsKey(projectPath))
        {
            if (_projects.TryGetValue(projectPath, out CompilationUnit? existing))
            {
                return existing;
            }

            CompilationUnit? loaded = LoadProject(projectPath);
            if (loaded != null)
            {
                _projects[projectPath] = loaded;
                return loaded;
            }
        }
        if (_standalone.TryGetValue(path, out CompilationUnit? unit))
        {
            return unit;
        }

        unit = CreateStandalone(path);
        _standalone[path] = unit;
        return unit;
    }

    private CompilationUnit CreateStandalone(string path)
    {
        if (_standaloneReferences == null)
        {
            List<string> paths = StandaloneReferences.Find(options, out string? warning);
            if (warning != null)
            {
                showMessage(warning);
            }

            _standaloneReferences = ReferenceSet.Create(paths);
        }
        string? directory = Path.IsPathRooted(path) ? Path.GetDirectoryName(path) : null;
        CompilationOptions standaloneOptions = new()
        {
            AssemblyName = Path.GetFileNameWithoutExtension(path) is { Length: > 0 } n ? n : "Caladesi",
            RootNamespace = "",
            ProjectDirectory = directory,
            RecordSemanticInfo = true,
        };
        return new CompilationUnit(path, null, standaloneOptions, _standaloneReferences, []);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A project that fails to load must be reported to the user, not crash the server.")]
    private CompilationUnit? LoadProject(string projectPath)
    {
        ProjectLoadResult result = _loader.Load(projectPath);
        if (result.Data == null)
        {
            string message = "Caladesi: could not load " + Path.GetFileName(projectPath) + ", using standalone mode. " + result.Error;
            Log.Write(message);
            _failedProjects[projectPath] = result.Error ?? "";
            showMessage(message);
            return null;
        }
        MsBuildProjectData data = result.Data;
        CompilationOptions options = data.ToCompilationOptions(projectPath);
        ReferenceSet references = ReferenceSet.Create(data.References);
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
        if (!Path.IsPathRooted(path))
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(path);
        string? root = Roots.Where(r => IsUnder(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        List<string> visited = [];
        string? found = null;
        while (directory != null)
        {
            if (_projectByDirectory.TryGetValue(directory, out string? cached))
            {
                found = cached;
                break;
            }
            visited.Add(directory);
            found = FirstProjectIn(directory);
            if (found != null)
            {
                break;
            }

            if (root == null || string.Equals(directory, root, StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
        foreach (string d in visited)
        {
            _projectByDirectory[d] = found;
        }

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
        string r = root.TrimEnd(Path.DirectorySeparatorChar);
        return path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal) || string.Equals(path, r, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ file system changes

    /// <summary>Applies <c>workspace/didChangeWatchedFiles</c>; returns true when anything relevant changed.</summary>
    public bool FilesChanged(IEnumerable<(string Path, int Type)> changes)
    {
        bool reloadProjects = false;
        bool any = false;
        foreach ((string? path, int type) in changes)
        {
            string extension = Path.GetExtension(path);
            if (extension.Equals(".calaproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith("project.assets.json", StringComparison.Ordinal))
            {
                reloadProjects = true;
                any = true;
            }
            else if (extension.Equals(".cala", StringComparison.OrdinalIgnoreCase))
            {
                any = true;
                _ = _diskCache.Remove(path);
                if (type != 2)
                {
                    reloadProjects = true;
                }

                foreach (CompilationUnit? unit in _unitByDocument.Values.Distinct())
                {
                    if (unit.IsMember(path))
                    {
                        unit.MarkChanged();
                    }
                }

                foreach (CompilationUnit unit in _projects.Values)
                {
                    if (unit.IsMember(path)) { unit.MarkChanged(); _ = Dirty.Add(unit); }
                }
            }
        }
        if (reloadProjects)
        {
            ReloadAll();
        }

        foreach (CompilationUnit? unit in _unitByDocument.Values.Distinct())
        {
            _ = Dirty.Add(unit);
        }

        return any;
    }

    private void ReloadAll()
    {
        List<CompilationUnit> oldUnits = [.. _projects.Values.Concat(_standalone.Values).Distinct()];
        _projects.Clear();
        _failedProjects.Clear();
        _projectByDirectory.Clear();
        _standalone.Clear();
        foreach (CompilationUnit? unit in oldUnits)
        {
            _ = Dirty.Remove(unit);
            RetireDiagnostics(unit);
        }
        List<string> open = [.. _documents.Keys];
        _unitByDocument.Clear();
        foreach (string path in open)
        {
            Assign(path);
        }
    }

    // ------------------------------------------------------------------ diagnostics bookkeeping

    internal readonly record struct PublishPlan(string Uri, IReadOnlyList<Diagnostic> Diagnostics);

    /// <summary>Groups diagnostics by file and lists files that must be cleared because they no longer have any.</summary>
    public List<PublishPlan> PlanPublish(CompilationUnit unit, IEnumerable<Diagnostic> diagnostics)
    {
        List<PublishPlan> plans = [];
        HashSet<string> members = new(unit.AllPaths(), StringComparer.Ordinal);
        Dictionary<string, List<Diagnostic>> byFile = new(StringComparer.Ordinal);
        foreach (Diagnostic d in diagnostics)
        {
            if (d.FilePath == null || !members.Contains(d.FilePath))
            {
                continue;
            }

            if (d.EffectiveSeverity == DiagnosticSeverity.Hidden)
            {
                continue;
            }

            if (!byFile.TryGetValue(d.FilePath, out List<Diagnostic>? list))
            {
                byFile[d.FilePath] = list = [];
            }

            list.Add(d);
        }
        foreach ((string? path, List<Diagnostic>? list) in byFile)
        {
            _publishedOwner[path] = unit;
            plans.Add(new PublishPlan(PathToUri(path), list));
        }
        foreach (string? path in _publishedOwner.Where(kv => kv.Value == unit && !byFile.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList())
        {
            _ = _publishedOwner.Remove(path);
            plans.Add(new PublishPlan(PathToUri(path), []));
        }
        return plans;
    }

    private readonly List<PublishPlan> _retired = [];

    private void RetireDiagnostics(CompilationUnit unit)
    {
        foreach (string? path in _publishedOwner.Where(kv => kv.Value == unit).Select(kv => kv.Key).ToList())
        {
            _ = _publishedOwner.Remove(path);
            _retired.Add(new PublishPlan(PathToUri(path), []));
        }
    }

    public bool HasRetired => _retired.Count > 0;

    public List<PublishPlan> TakeRetired()
    {
        List<PublishPlan> result = [.. _retired];
        _retired.Clear();
        return result;
    }
}
