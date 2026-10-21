using System.Collections;
using System.Collections.Immutable;
using System.Text;
using Caladesi.Compiler.Text;

namespace Caladesi.Compiler.Syntax;

public readonly record struct SyntaxTrivia(SyntaxKind Kind, string Text)
{
    public int Width => Text.Length;
    public override string ToString() => Text;
}

/// <summary>Common base of tokens and nodes in the full-fidelity syntax tree.</summary>
public abstract class SyntaxElement
{
    protected SyntaxElement(SyntaxKind kind) => Kind = kind;

    public SyntaxKind Kind { get; }
    public SyntaxNode? Parent { get; internal set; }
    internal int IndexInParent { get; set; }

    /// <summary>Width including leading and trailing trivia.</summary>
    public abstract int FullWidth { get; }
    public abstract int FullStart { get; }
    public int FullEnd => FullStart + FullWidth;
    public TextSpan FullSpan => new(FullStart, FullWidth);
    public abstract int Start { get; }
    public int End => Start + Width;
    public int Width => FullEnd - Start - TrailingWidth;
    public TextSpan Span => new(Start, Width);
    public abstract bool IsMissing { get; }
    internal abstract int TrailingWidth { get; }

    public abstract void WriteTo(StringBuilder sb);

    public string ToFullString()
    {
        var sb = new StringBuilder(FullWidth);
        WriteTo(sb);
        return sb.ToString();
    }

    public SyntaxTree? SyntaxTree
    {
        get
        {
            SyntaxElement e = this;
            while (e.Parent != null) e = e.Parent;
            return (e as SyntaxNode)?.OwnerTree;
        }
    }

    public SyntaxNode? FirstAncestorOrSelf(Func<SyntaxNode, bool> predicate)
    {
        SyntaxElement? e = this;
        while (e != null)
        {
            if (e is SyntaxNode n && predicate(n)) return n;
            e = e.Parent;
        }
        return null;
    }

    public T? FirstAncestorOrSelf<T>() where T : SyntaxNode
    {
        SyntaxElement? e = this;
        while (e != null)
        {
            if (e is T t) return t;
            e = e.Parent;
        }
        return null;
    }
}

public sealed class SyntaxToken : SyntaxElement
{
    private readonly int _fullStart;

    public SyntaxToken(SyntaxKind kind, string text, int fullStart,
        ImmutableArray<SyntaxTrivia> leading, ImmutableArray<SyntaxTrivia> trailing,
        object? value = null, bool isMissing = false)
        : base(kind)
    {
        Text = text;
        _fullStart = fullStart;
        LeadingTrivia = leading.IsDefault ? ImmutableArray<SyntaxTrivia>.Empty : leading;
        TrailingTrivia = trailing.IsDefault ? ImmutableArray<SyntaxTrivia>.Empty : trailing;
        Value = value;
        IsMissingToken = isMissing;
        var lw = 0;
        foreach (var t in LeadingTrivia) lw += t.Width;
        var tw = 0;
        foreach (var t in TrailingTrivia) tw += t.Width;
        LeadingWidth = lw;
        TrailingWidthValue = tw;
    }

    public string Text { get; }
    /// <summary>The parsed value of a literal token (int, long, double, string, ...).</summary>
    public object? Value { get; }
    public ImmutableArray<SyntaxTrivia> LeadingTrivia { get; }
    public ImmutableArray<SyntaxTrivia> TrailingTrivia { get; }
    internal bool IsMissingToken { get; }
    internal int LeadingWidth { get; }
    internal int TrailingWidthValue { get; }

    public override bool IsMissing => IsMissingToken;
    public override int FullStart => _fullStart;
    public override int FullWidth => LeadingWidth + Text.Length + TrailingWidthValue;
    public override int Start => _fullStart + LeadingWidth;
    internal override int TrailingWidth => TrailingWidthValue;

    public string ValueText => Value as string ?? Text;

    public override void WriteTo(StringBuilder sb)
    {
        foreach (var t in LeadingTrivia) sb.Append(t.Text);
        sb.Append(Text);
        foreach (var t in TrailingTrivia) sb.Append(t.Text);
    }

    public SyntaxToken WithLeadingTrivia(ImmutableArray<SyntaxTrivia> leading) =>
        new(Kind, Text, _fullStart, leading, TrailingTrivia, Value, IsMissingToken);

    public SyntaxToken WithKind(SyntaxKind kind) =>
        new(kind, Text, _fullStart, LeadingTrivia, TrailingTrivia, Value, IsMissingToken);

    public bool IsKind(SyntaxKind k) => Kind == k;

    public override string ToString() => Text;
}

/// <summary>An immutable view over a list node.</summary>
public readonly struct SyntaxList<T> : IReadOnlyList<T> where T : SyntaxElement
{
    private readonly SyntaxNode? _node;
    public SyntaxList(SyntaxNode? node) => _node = node;
    public int Count => _node?.SlotCount ?? 0;
    public T this[int index] => (T)_node!.Slot(index)!;
    public SyntaxNode? Node => _node;
    public bool Any() => Count > 0;
    public T? FirstOrDefault() => Count > 0 ? this[0] : null;
    public T? LastOrDefault() => Count > 0 ? this[Count - 1] : null;

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly SyntaxList<T> _list;
        private int _i;
        internal Enumerator(SyntaxList<T> list) { _list = list; _i = -1; }
        public T Current => _list[_i];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++_i < _list.Count;
        public void Reset() => _i = -1;
        public void Dispose() { }
    }
}

/// <summary>A list node whose slots alternate between elements and separator tokens.</summary>
public readonly struct SeparatedSyntaxList<T> : IReadOnlyList<T> where T : SyntaxElement
{
    private readonly SyntaxNode? _node;
    public SeparatedSyntaxList(SyntaxNode? node) => _node = node;
    public int Count => ((_node?.SlotCount ?? 0) + 1) / 2;
    public T this[int index] => (T)_node!.Slot(index * 2)!;
    public SyntaxNode? Node => _node;
    public int SeparatorCount => (_node?.SlotCount ?? 0) / 2;
    public SyntaxToken GetSeparator(int index) => (SyntaxToken)_node!.Slot(index * 2 + 1)!;

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public abstract partial class SyntaxNode : SyntaxElement
{
    private readonly SyntaxElement?[] _slots;
    private readonly int _fullWidth;
    private int _cachedFullStart = -1;
    private SyntaxTree? _ownerTree;

    protected SyntaxNode(SyntaxKind kind, params SyntaxElement?[] slots) : base(kind)
    {
        _slots = slots;
        var w = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            if (s == null) continue;
            s.Parent = this;
            s.IndexInParent = i;
            w += s.FullWidth;
        }
        _fullWidth = w;
    }

    internal SyntaxTree? OwnerTree
    {
        get => _ownerTree;
        set => _ownerTree = value;
    }

    public int SlotCount => _slots.Length;
    public SyntaxElement? Slot(int i) => _slots[i];

    public override int FullWidth => _fullWidth;

    public override int FullStart
    {
        get
        {
            if (_cachedFullStart >= 0) return _cachedFullStart;
            int pos;
            var first = FirstChild();
            if (first != null) pos = first.FullStart;
            else if (Parent == null) pos = 0;
            else
            {
                // empty node: sit right after the previous sibling (or at the parent's start)
                pos = Parent.FullStart;
                for (var i = 0; i < IndexInParent; i++)
                {
                    var s = Parent.Slot(i);
                    if (s != null) pos = s.FullEnd;
                }
            }
            _cachedFullStart = pos;
            return pos;
        }
    }

    private SyntaxElement? FirstChild()
    {
        foreach (var s in _slots)
        {
            if (s == null) continue;
            if (s is SyntaxNode n && n.FullWidth == 0 && n.FirstChild() == null) continue;
            return s;
        }
        return null;
    }

    public override int Start
    {
        get
        {
            var t = GetFirstToken();
            return t?.Start ?? FullStart;
        }
    }

    internal override int TrailingWidth
    {
        get
        {
            var t = GetLastToken();
            return t?.TrailingWidthValue ?? 0;
        }
    }

    public override bool IsMissing
    {
        get
        {
            var any = false;
            foreach (var t in DescendantTokens())
            {
                any = true;
                if (!t.IsMissing) return false;
            }
            return any;
        }
    }

    public override void WriteTo(StringBuilder sb)
    {
        foreach (var s in _slots) s?.WriteTo(sb);
    }

    public IEnumerable<SyntaxElement> Children()
    {
        foreach (var s in _slots)
            if (s != null) yield return s;
    }

    public IEnumerable<SyntaxNode> ChildNodes()
    {
        foreach (var s in _slots)
            if (s is SyntaxNode n) yield return n;
    }

    public SyntaxToken? GetFirstToken()
    {
        foreach (var s in _slots)
        {
            if (s is SyntaxToken t) return t;
            if (s is SyntaxNode n)
            {
                var r = n.GetFirstToken();
                if (r != null) return r;
            }
        }
        return null;
    }

    public SyntaxToken? GetLastToken()
    {
        for (var i = _slots.Length - 1; i >= 0; i--)
        {
            var s = _slots[i];
            if (s is SyntaxToken t) return t;
            if (s is SyntaxNode n)
            {
                var r = n.GetLastToken();
                if (r != null) return r;
            }
        }
        return null;
    }

    public IEnumerable<SyntaxToken> DescendantTokens()
    {
        foreach (var s in _slots)
        {
            if (s is SyntaxToken t) yield return t;
            else if (s is SyntaxNode n)
                foreach (var x in n.DescendantTokens()) yield return x;
        }
    }

    public IEnumerable<SyntaxNode> DescendantNodes()
    {
        foreach (var s in _slots)
        {
            if (s is SyntaxNode n)
            {
                yield return n;
                foreach (var x in n.DescendantNodes()) yield return x;
            }
        }
    }

    public IEnumerable<SyntaxNode> DescendantNodesAndSelf()
    {
        yield return this;
        foreach (var x in DescendantNodes()) yield return x;
    }

    /// <summary>Finds the token whose full span contains <paramref name="position"/>.</summary>
    public SyntaxToken? FindToken(int position)
    {
        if (position < FullStart || position > FullEnd) return null;
        SyntaxToken? last = null;
        foreach (var s in _slots)
        {
            if (s == null) continue;
            if (s is SyntaxToken t)
            {
                if (position < t.FullEnd) return t;
                last = t;
            }
            else if (s is SyntaxNode n)
            {
                if (n.FullWidth == 0) continue;
                if (position < n.FullEnd) return n.FindToken(position);
                last = n.GetLastToken() ?? last;
            }
        }
        return last;
    }

    /// <summary>Finds the smallest node whose span covers <paramref name="span"/>.</summary>
    public SyntaxNode FindNode(TextSpan span)
    {
        var node = this;
        while (true)
        {
            SyntaxNode? next = null;
            foreach (var c in node.ChildNodes())
            {
                if (c.FullWidth == 0) continue;
                if (c.Span.Start <= span.Start && c.Span.End >= span.End && (c.Width > 0 || span.Length == 0))
                {
                    next = c;
                    break;
                }
            }
            if (next == null) return node;
            node = next;
        }
    }

    public override string ToString() => Kind.ToString();
}

/// <summary>Node that carries a list of elements (see SyntaxList / SeparatedSyntaxList).</summary>
public sealed class ListNode : SyntaxNode
{
    public ListNode(SyntaxKind kind, SyntaxElement[] items) : base(kind, items) { }
}
