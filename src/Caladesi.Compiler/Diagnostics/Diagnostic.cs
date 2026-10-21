using System.Collections.Immutable;
using System.Globalization;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Diagnostics;

public enum DiagnosticSeverity { Hidden, Info, Warning, Error }

public sealed class DiagnosticDescriptor
{
    public DiagnosticDescriptor(string id, DiagnosticSeverity severity, string messageFormat)
    {
        Id = id;
        Severity = severity;
        MessageFormat = messageFormat;
    }

    public string Id { get; }
    public DiagnosticSeverity Severity { get; }
    public string MessageFormat { get; }
}

/// <summary>A fix-it suggestion attached to a diagnostic: replace <see cref="Span"/> with <see cref="NewText"/>.</summary>
public sealed record FixIt(string Title, TextSpan Span, string NewText);

public sealed class Diagnostic
{
    public Diagnostic(DiagnosticDescriptor descriptor, string? filePath, TextSpan span, object?[] args, FixIt? fixIt = null)
    {
        Descriptor = descriptor;
        FilePath = filePath;
        Span = span;
        Arguments = args;
        FixIt = fixIt;
    }

    public DiagnosticDescriptor Descriptor { get; }
    public string Id => Descriptor.Id;
    public string? FilePath { get; }
    public TextSpan Span { get; }
    public object?[] Arguments { get; }
    public FixIt? FixIt { get; }
    public DiagnosticSeverity EffectiveSeverity { get; init; }

    public string Message => string.Format(CultureInfo.InvariantCulture, Descriptor.MessageFormat, Arguments);

    public Diagnostic WithFilePath(string? path) =>
        new(Descriptor, path, Span, Arguments, FixIt) { EffectiveSeverity = EffectiveSeverity };

    public Diagnostic WithSeverity(DiagnosticSeverity severity) =>
        new(Descriptor, FilePath, Span, Arguments, FixIt) { EffectiveSeverity = severity };

    public static Diagnostic Create(DiagnosticDescriptor d, string? filePath, TextSpan span, params object?[] args) =>
        new(d, filePath, span, args) { EffectiveSeverity = d.Severity };

    public string Format(SourceText? text = null)
    {
        var sev = EffectiveSeverity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Info => "info",
            _ => "hidden",
        };
        var loc = FilePath ?? "<unknown>";
        if (text != null)
        {
            var p = text.GetLinePosition(Span.Start);
            loc = $"{loc}({p.Line + 1},{p.Character + 1})";
        }
        return $"{loc}: {sev} {Id}: {Message}";
    }

    public override string ToString() => Format();
}

/// <summary>Collects diagnostics. Not thread-safe.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();
    public int Count => _items.Count;
    public void Add(Diagnostic d) => _items.Add(d);
    public void AddRange(IEnumerable<Diagnostic> ds) => _items.AddRange(ds);
    public bool HasErrors => _items.Exists(d => d.EffectiveSeverity == DiagnosticSeverity.Error);
    public ImmutableArray<Diagnostic> ToImmutable() => _items.ToImmutableArray();
    public void TruncateTo(int count) { if (count < _items.Count) _items.RemoveRange(count, _items.Count - count); }
    public IReadOnlyList<Diagnostic> Items => _items;
}
