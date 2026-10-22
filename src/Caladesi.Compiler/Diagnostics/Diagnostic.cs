using System.Collections.Immutable;
using System.Globalization;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Diagnostics;

public enum DiagnosticSeverity { Hidden, Info, Warning, Error }

public sealed class DiagnosticDescriptor(string id, DiagnosticSeverity severity, string messageFormat)
{
    public string Id { get; } = id;
    public DiagnosticSeverity Severity { get; } = severity;
    public string MessageFormat { get; } = messageFormat;
}

/// <summary>A fix-it suggestion attached to a diagnostic: replace <see cref="Span"/> with <see cref="NewText"/>.</summary>
public sealed record FixIt(string Title, TextSpan Span, string NewText);

public sealed class Diagnostic(DiagnosticDescriptor descriptor, string? filePath, TextSpan span, object?[] args, FixIt? fixIt = null)
{
    public DiagnosticDescriptor Descriptor { get; } = descriptor;
    public string Id => Descriptor.Id;
    public string? FilePath { get; } = filePath;
    public TextSpan Span { get; } = span;
#pragma warning disable CA1819 // format arguments are an array to match string.Format
    public object?[] Arguments { get; } = args;
#pragma warning restore CA1819
    public FixIt? FixIt { get; } = fixIt;
    public DiagnosticSeverity EffectiveSeverity { get; init; }

    public string Message => string.Format(CultureInfo.InvariantCulture, Descriptor.MessageFormat, Arguments);

    public Diagnostic WithFilePath(string? path)
    {
        return new(Descriptor, path, Span, Arguments, FixIt) { EffectiveSeverity = EffectiveSeverity };
    }

    public Diagnostic WithSeverity(DiagnosticSeverity severity)
    {
        return new(Descriptor, FilePath, Span, Arguments, FixIt) { EffectiveSeverity = severity };
    }

    public static Diagnostic Create(DiagnosticDescriptor d, string? filePath, TextSpan span, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new(d, filePath, span, args) { EffectiveSeverity = d.Severity };
    }

    public string Format(SourceText? text = null)
    {
        string sev = EffectiveSeverity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Info => "info",
            _ => "hidden",
        };
        string loc = FilePath ?? "<unknown>";
        if (text != null)
        {
            LinePosition p = text.GetLinePosition(Span.Start);
            loc = $"{loc}({p.Line + 1},{p.Character + 1})";
        }
        return $"{loc}: {sev} {Id}: {Message}";
    }

    public override string ToString()
    {
        return Format();
    }
}

/// <summary>Collects diagnostics. Not thread-safe.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];
    public int Count => _items.Count;
    public void Add(Diagnostic d)
    {
        _items.Add(d);
    }

    public void AddRange(IEnumerable<Diagnostic> ds)
    {
        _items.AddRange(ds);
    }

    public bool HasErrors => _items.Exists(d => d.EffectiveSeverity == DiagnosticSeverity.Error);
    public ImmutableArray<Diagnostic> ToImmutable()
    {
        return [.. _items];
    }

    public void TruncateTo(int count)
    {
        if (count < _items.Count)
        {
            _items.RemoveRange(count, _items.Count - count);
        }
    }
    public IReadOnlyList<Diagnostic> Items => _items;
}
