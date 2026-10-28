extern alias SelfHost;

using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;
using SelfElement = SelfHost::Caladesi.Compiler.Syntax.SyntaxElement;
using SelfKind = SelfHost::Caladesi.Compiler.Syntax.SyntaxKind;
using SelfListNode = SelfHost::Caladesi.Compiler.Syntax.ListNode;
using SelfNode = SelfHost::Caladesi.Compiler.Syntax.SyntaxNode;
using SelfSpan = SelfHost::Caladesi.Compiler.Text.TextSpan;
using SelfToken = SelfHost::Caladesi.Compiler.Syntax.SyntaxToken;
using SelfTree = SelfHost::Caladesi.Compiler.Syntax.SyntaxTree;
using SelfTrivia = SelfHost::Caladesi.Compiler.Syntax.SyntaxTrivia;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class SyntaxTreeParityTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow(" \r\n/* only trivia */ // end")]
    [DataRow(" // leading\r\nlet x: int = 1 + 2; /* trailing */\r\nx = f(x, 3);\n")]
    [DataRow("function f<T>(x: T, y: int = 2): T { if (y > 0) { return x; } else { throw x; } }")]
    [DataRow("class C<T> { private value: int; constructor(x: int) { this.value = x; } get(): int { return this.value; } }")]
    [DataRow("type T = { x: int; y?: string }; type U = [name: string, int] | null; let a = [1, 2,]; let b = { x: 1, y: 2 };")]
    [DataRow("import { A, B as C } from \"module\"; export function f() { for (let i = 0; i < 2; i++) { continue; } }")]
    [DataRow("let s = `a${f(1, 2)}b`; let f = (x: int, y: int) => x + y;")]
    [DataRow("function f( { let x: = ; return (1 + );")]
    [DataRow("class C { f(x: int { return ;")]
    [DataRow("let x = ; @")]
    public void Tree_NavigationAndAccessorsMatchBootstrap(string source)
    {
        AssertTreeParity(SyntaxTree.ParseText(source, "navigation.cala"), SelfTree.ParseText(source, "navigation.cala"));
    }

    [TestMethod]
    public void Tree_WithTextPreservesPathDiagnosticsAndOriginalOwnership()
    {
        const string path = "nested/invalid.cala";
        SyntaxTree expected = SyntaxTree.ParseText("let original = 1;", path);
        SelfTree actual = SelfTree.ParseText("let original = 1;", path);
        SyntaxTree expectedChanged = expected.WithText("let changed: = ;\n$");
        SelfTree actualChanged = actual.WithText("let changed: = ;\n$");

        Assert.AreNotSame(expected, expectedChanged);
        Assert.AreNotSame(actual, actualChanged);
        Assert.AreSame(expected, expected.Root.SyntaxTree);
        Assert.IsTrue(ReferenceEquals(actual, actual.Root.SyntaxTree));
        Assert.IsGreaterThan(0, expectedChanged.Diagnostics.Length);
        AssertTreeParity(expectedChanged, actualChanged);
        AssertTreeParity(expected, actual);
        AssertTreeParity(SyntaxTree.ParseText("let x = ;"), SelfTree.ParseText("let x = ;"));
    }

    [TestMethod]
    public void DetachedNodes_EmptyListsAndMissingTokensMatchBootstrap()
    {
        SyntaxToken expectedMissing = new(SyntaxKind.IdentifierToken, "", 12, default, default, isMissing: true);
        SelfToken actualMissing = new(SelfKind.IdentifierToken, "", 12, default, default, null, true);
        AssertDetachedParity(new MissingTypeSyntax(expectedMissing),
            new SelfHost::Caladesi.Compiler.Syntax.MissingTypeSyntax(actualMissing));
        AssertDetachedParity(new ListNode(SyntaxKind.List, []), new SelfListNode(SelfKind.List, []));

        SyntaxToken expectedToken = new(SyntaxKind.IdentifierToken, "x", 5, default, default);
        SelfToken actualToken = new(SelfKind.IdentifierToken, "x", 5, default, default, null);
        ListNode expectedEmpty = new(SyntaxKind.List, []);
        SelfListNode actualEmpty = new(SelfKind.List, []);
        AssertDetachedParity(new ListNode(SyntaxKind.List, [expectedToken, expectedEmpty]),
            new SelfListNode(SelfKind.List, [actualToken, actualEmpty]));
        Assert.AreEqual(6, expectedEmpty.FullStart);
        Assert.AreEqual(6, actualEmpty.FullStart);

        AssertLists(new SyntaxList<SyntaxElement>(null),
            new SelfHost::Caladesi.Compiler.Syntax.SyntaxList<SelfElement>(null), []);
        AssertSeparatedLists(new SeparatedSyntaxList<SyntaxElement>(null),
            new SelfHost::Caladesi.Compiler.Syntax.SeparatedSyntaxList<SelfElement>(null), []);
    }

    [TestMethod]
    public void Token_CloningTriviaAndMissingStateMatchBootstrap()
    {
        ImmutableArray<SyntaxTrivia> leading = [new(SyntaxKind.WhitespaceTrivia, "  ")];
        ImmutableArray<SelfTrivia> actualLeading = [new(SelfKind.WhitespaceTrivia, "  ")];
        ImmutableArray<SyntaxTrivia> trailing = [new(SyntaxKind.SingleLineCommentTrivia, "// end")];
        ImmutableArray<SelfTrivia> actualTrailing = [new(SelfKind.SingleLineCommentTrivia, "// end")];
        SyntaxToken expected = new(SyntaxKind.StringLiteralToken, "'value'", 9, leading, trailing, "value");
        SelfToken actual = new(SelfKind.StringLiteralToken, "'value'", 9, actualLeading, actualTrailing, "value");
        AssertTokenParity(expected, actual);
        AssertTokenParity(expected.WithKind(SyntaxKind.IdentifierToken), actual.WithKind(SelfKind.IdentifierToken));
        AssertTokenParity(expected.WithLeadingTrivia(default), actual.WithLeadingTrivia(default));
        AssertTokenParity(expected.WithLeadingTrivia([]), actual.WithLeadingTrivia([]));
        Assert.IsNull(actual.WithKind(SelfKind.IdentifierToken).Parent);

        SyntaxToken expectedMissing = new(SyntaxKind.IdentifierToken, "", 4, default, default, isMissing: true);
        SelfToken actualMissing = new(SelfKind.IdentifierToken, "", 4, default, default, null, true);
        AssertTokenParity(expectedMissing.WithKind(SyntaxKind.StringLiteralToken),
            actualMissing.WithKind(SelfKind.StringLiteralToken));
        AssertTokenParity(expectedMissing.WithLeadingTrivia(leading), actualMissing.WithLeadingTrivia(actualLeading));
    }

    private static void AssertTreeParity(SyntaxTree expected, SelfTree actual)
    {
        Assert.AreEqual(expected.FilePath, actual.FilePath);
        Assert.AreEqual(expected.Text.ToString(), actual.Text.ToString());
        Assert.HasCount(expected.Diagnostics.Length, actual.Diagnostics);
        for (int i = 0; i < expected.Diagnostics.Length; i++)
        {
            var left = expected.Diagnostics[i];
            var right = actual.Diagnostics[i];
            Assert.AreEqual(left.Id, right.Id);
            Assert.AreEqual(left.Message, right.Message);
            Assert.AreEqual(left.FilePath, right.FilePath);
            Assert.AreEqual(left.Span.Start, right.Span.Start);
            Assert.AreEqual(left.Span.Length, right.Span.Length);
            Assert.AreEqual((int)left.EffectiveSeverity, (int)right.EffectiveSeverity);
            Assert.AreEqual(left.Format(expected.Text), right.Format(actual.Text));
        }

        Dictionary<SyntaxElement, SelfElement> pairs = [];
        PairElements(expected.Root, actual.Root, pairs);
        foreach (var pair in pairs)
        {
            Assert.AreSame(expected, pair.Key.SyntaxTree);
            Assert.IsTrue(ReferenceEquals(actual, pair.Value.SyntaxTree));
        }

        AssertNavigation(pairs);
        for (int position = -1; position <= expected.Text.Length + 1; position++)
        {
            AssertMapped(expected.Root.FindToken(position), actual.Root.FindToken(position), pairs);
            AssertMapped(expected.Root.FindNode(new TextSpan(position, 0)),
                actual.Root.FindNode(new SelfSpan(position, 0)), pairs);
            if (position >= 0 && position < expected.Text.Length)
            {
                AssertMapped(expected.Root.FindNode(new TextSpan(position, 1)),
                    actual.Root.FindNode(new SelfSpan(position, 1)), pairs);
            }
        }
    }

    private static void AssertDetachedParity(SyntaxNode expected, SelfNode actual)
    {
        Dictionary<SyntaxElement, SelfElement> pairs = [];
        PairElements(expected, actual, pairs);
        foreach (var pair in pairs)
        {
            Assert.IsNull(pair.Key.SyntaxTree);
            Assert.IsNull(pair.Value.SyntaxTree);
        }

        AssertNavigation(pairs);
    }

    private static void PairElements(SyntaxElement expected, SelfElement actual, Dictionary<SyntaxElement, SelfElement> pairs)
    {
        pairs.Add(expected, actual);
        Assert.AreEqual(expected.GetType().Name, actual.GetType().Name);
        AssertElementParity(expected, actual);
        if (expected is SyntaxToken token)
        {
            AssertTokenParity(token, (SelfToken)actual);
            return;
        }

        SyntaxNode left = (SyntaxNode)expected;
        SelfNode right = (SelfNode)actual;
        Assert.AreEqual(left.SlotCount, right.SlotCount);
        for (int i = 0; i < left.SlotCount; i++)
        {
            SyntaxElement? child = left.Slot(i);
            SelfElement? actualChild = right.Slot(i);
            if (child == null)
            {
                Assert.IsNull(actualChild);
                continue;
            }

            Assert.IsNotNull(actualChild);
            Assert.AreSame(left, child.Parent);
            Assert.AreSame(right, actualChild.Parent);
            PairElements(child, actualChild, pairs);
        }
    }

    private static void AssertElementParity(SyntaxElement expected, SelfElement actual)
    {
        string context = $"{expected.Kind}: {expected.ToFullString()}";
        Assert.AreEqual((int)expected.Kind, (int)actual.Kind, context);
        Assert.AreEqual(expected.FullStart, actual.FullStart, context);
        Assert.AreEqual(expected.FullStart, actual.FullStart, "Cached FullStart: " + context);
        Assert.AreEqual(expected.FullEnd, actual.FullEnd, context);
        Assert.AreEqual(expected.FullWidth, actual.FullWidth, context);
        Assert.AreEqual(expected.Start, actual.Start, context);
        Assert.AreEqual(expected.End, actual.End, context);
        Assert.AreEqual(expected.Width, actual.Width, context);
        Assert.AreEqual(expected.Span.Start, actual.Span.Start, context);
        Assert.AreEqual(expected.Span.Length, actual.Span.Length, context);
        Assert.AreEqual(expected.FullSpan.Start, actual.FullSpan.Start, context);
        Assert.AreEqual(expected.FullSpan.Length, actual.FullSpan.Length, context);
        Assert.AreEqual(expected.IsMissing, actual.IsMissing, context);
        Assert.AreEqual(expected.ToString(), actual.ToString(), context);
        Assert.AreEqual(expected.ToFullString(), actual.ToFullString(), context);
        StringBuilder left = new("prefix:");
        StringBuilder right = new("prefix:");
        expected.WriteTo(left);
        actual.WriteTo(right);
        Assert.AreEqual(left.ToString(), right.ToString(), context);
    }

    private static void AssertTokenParity(SyntaxToken expected, SelfToken actual)
    {
        AssertElementParity(expected, actual);
        Assert.AreEqual(expected.Text, actual.Text);
        Assert.AreEqual(expected.ValueText, actual.ValueText);
        Assert.AreEqual(expected.Value, actual.Value);
        Assert.AreEqual(expected.Value?.GetType(), actual.Value?.GetType());
        Assert.HasCount(expected.LeadingTrivia.Length, actual.LeadingTrivia);
        Assert.HasCount(expected.TrailingTrivia.Length, actual.TrailingTrivia);
        for (int i = 0; i < expected.LeadingTrivia.Length; i++)
        {
            Assert.AreEqual((int)expected.LeadingTrivia[i].Kind, (int)actual.LeadingTrivia[i].Kind);
            Assert.AreEqual(expected.LeadingTrivia[i].Text, actual.LeadingTrivia[i].Text);
        }

        for (int i = 0; i < expected.TrailingTrivia.Length; i++)
        {
            Assert.AreEqual((int)expected.TrailingTrivia[i].Kind, (int)actual.TrailingTrivia[i].Kind);
            Assert.AreEqual(expected.TrailingTrivia[i].Text, actual.TrailingTrivia[i].Text);
        }

        Assert.IsTrue(actual.IsKind((SelfKind)(int)expected.Kind));
        Assert.AreEqual(expected.IsKind(SyntaxKind.EndOfFileToken), actual.IsKind(SelfKind.EndOfFileToken));
    }

    private static void AssertNavigation(Dictionary<SyntaxElement, SelfElement> pairs)
    {
        foreach (var (left, right) in pairs)
        {
            AssertMapped(left.Parent, right.Parent, pairs);
            AssertMapped(left.FirstAncestorOrSelf(_ => true), right.FirstAncestorOrSelf(_ => true), pairs);
            AssertMapped(left.FirstAncestorOrSelf(_ => false), right.FirstAncestorOrSelf(_ => false), pairs);
            AssertMapped(left.FirstAncestorOrSelf(n => n.Kind == SyntaxKind.Block),
                right.FirstAncestorOrSelf(n => n.Kind == SelfKind.Block), pairs);
            AssertMapped(left.FirstAncestorOrSelf<SyntaxNode>(), right.FirstAncestorOrSelf<SelfNode>(), pairs);
            AssertMapped(left.FirstAncestorOrSelf<CompilationUnitSyntax>(),
                right.FirstAncestorOrSelf<SelfHost::Caladesi.Compiler.Syntax.CompilationUnitSyntax>(), pairs);
            AssertMapped(left.FirstAncestorOrSelf<ExpressionSyntax>(),
                right.FirstAncestorOrSelf<SelfHost::Caladesi.Compiler.Syntax.ExpressionSyntax>(), pairs);
            AssertMapped(left.FirstAncestorOrSelf<BlockSyntax>(),
                right.FirstAncestorOrSelf<SelfHost::Caladesi.Compiler.Syntax.BlockSyntax>(), pairs);
            Assert.ThrowsExactly<ArgumentNullException>(() => left.FirstAncestorOrSelf(null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => right.FirstAncestorOrSelf(null!));

            if (left is not SyntaxNode node)
            {
                continue;
            }

            SelfNode actual = (SelfNode)right;
            AssertSequence(node.Children(), actual.Children(), pairs);
            AssertSequence(node.ChildNodes(), actual.ChildNodes(), pairs);
            AssertSequence(node.DescendantTokens(), actual.DescendantTokens(), pairs);
            AssertSequence(node.DescendantNodes(), actual.DescendantNodes(), pairs);
            AssertSequence(node.DescendantNodesAndSelf(), actual.DescendantNodesAndSelf(), pairs);
            AssertMapped(node.GetFirstToken(), actual.GetFirstToken(), pairs);
            AssertMapped(node.GetLastToken(), actual.GetLastToken(), pairs);
            foreach (int position in new[] { node.FullStart - 1, node.FullStart, node.Start, node.End, node.FullEnd, node.FullEnd + 1 })
            {
                AssertMapped(node.FindToken(position), actual.FindToken(position), pairs);
            }

            AssertMapped(node.FindNode(node.Span), actual.FindNode(actual.Span), pairs);
            AssertMapped(node.FindNode(node.FullSpan), actual.FindNode(actual.FullSpan), pairs);
            AssertAccessors(node, actual, pairs);
            if (node is ListNode)
            {
                AssertLists(new SyntaxList<SyntaxElement>(node),
                    new SelfHost::Caladesi.Compiler.Syntax.SyntaxList<SelfElement>(actual), pairs);
                AssertSeparatedLists(new SeparatedSyntaxList<SyntaxElement>(node),
                    new SelfHost::Caladesi.Compiler.Syntax.SeparatedSyntaxList<SelfElement>(actual), pairs);
            }
        }
    }

    private static void AssertAccessors(SyntaxNode expected, SelfNode actual, Dictionary<SyntaxElement, SelfElement> pairs)
    {
        foreach (PropertyInfo property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            object? left = property.GetValue(expected);
            PropertyInfo? actualProperty = actual.GetType().GetProperty(property.Name);
            Assert.IsNotNull(actualProperty, $"{expected.GetType().Name}.{property.Name}");
            object? right = actualProperty.GetValue(actual);
            if (left is SyntaxElement element)
            {
                AssertMapped(element, (SelfElement?)right, pairs);
            }
            else if (left is SyntaxKind kind)
            {
                Assert.AreEqual((int)kind, (int)(SelfKind)right!);
            }
            else if (left != null && property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() is Type definition &&
                (definition == typeof(SyntaxList<>) || definition == typeof(SeparatedSyntaxList<>)))
            {
                Assert.IsNotNull(right);
                Assert.AreEqual(property.PropertyType.GetProperty("Count")!.GetValue(left),
                    right.GetType().GetProperty("Count")!.GetValue(right));
                AssertMapped((SyntaxElement?)property.PropertyType.GetProperty("Node")!.GetValue(left),
                    (SelfElement?)right.GetType().GetProperty("Node")!.GetValue(right), pairs);
                int count = (int)property.PropertyType.GetProperty("Count")!.GetValue(left)!;
                for (int i = 0; i < count; i++)
                {
                    AssertMapped((SyntaxElement?)property.PropertyType.GetProperty("Item")!.GetValue(left, [i]),
                        (SelfElement?)right.GetType().GetMethod("Get")!.Invoke(right, [i]), pairs);
                }
            }
            else
            {
                Assert.AreEqual(left, right, $"{expected.GetType().Name}.{property.Name}");
            }
        }
    }

    private static void AssertLists(SyntaxList<SyntaxElement> expected,
        SelfHost::Caladesi.Compiler.Syntax.SyntaxList<SelfElement> actual,
        Dictionary<SyntaxElement, SelfElement> pairs)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        Assert.AreEqual(expected.Any(), actual.Any());
        AssertMapped(expected.Node, actual.Node, pairs);
        AssertMapped(expected.FirstOrDefault(), actual.FirstOrDefault(), pairs);
        AssertMapped(expected.LastOrDefault(), actual.LastOrDefault(), pairs);
        using IEnumerator<SelfElement> iterator = actual.GetEnumerator();
        for (int i = 0; i < expected.Count; i++)
        {
            AssertMapped(expected[i], actual.Get(i), pairs);
            Assert.IsTrue(iterator.MoveNext());
            AssertMapped(expected[i], iterator.Current, pairs);
        }

        Assert.IsFalse(iterator.MoveNext());
    }

    private static void AssertSeparatedLists(SeparatedSyntaxList<SyntaxElement> expected,
        SelfHost::Caladesi.Compiler.Syntax.SeparatedSyntaxList<SelfElement> actual,
        Dictionary<SyntaxElement, SelfElement> pairs)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        Assert.AreEqual(expected.SeparatorCount, actual.SeparatorCount);
        AssertMapped(expected.Node, actual.Node, pairs);
        using IEnumerator<SelfElement> iterator = actual.GetEnumerator();
        for (int i = 0; i < expected.Count; i++)
        {
            AssertMapped(expected[i], actual.Get(i), pairs);
            Assert.IsTrue(iterator.MoveNext());
            AssertMapped(expected[i], iterator.Current, pairs);
        }

        Assert.IsFalse(iterator.MoveNext());
        for (int i = 0; i < expected.SeparatorCount; i++)
        {
            if (expected.Node!.Slot((i * 2) + 1) is SyntaxToken)
            {
                AssertMapped(expected.GetSeparator(i), actual.GetSeparator(i), pairs);
            }
        }
    }

    private static void AssertSequence(IEnumerable<SyntaxElement> expected, IEnumerable<SelfElement> actual,
        Dictionary<SyntaxElement, SelfElement> pairs)
    {
        using IEnumerator<SelfElement> iterator = actual.GetEnumerator();
        foreach (SyntaxElement element in expected)
        {
            Assert.IsTrue(iterator.MoveNext());
            AssertMapped(element, iterator.Current, pairs);
        }

        Assert.IsFalse(iterator.MoveNext());
    }

    private static void AssertMapped(SyntaxElement? expected, SelfElement? actual,
        Dictionary<SyntaxElement, SelfElement> pairs)
    {
        if (expected == null)
        {
            Assert.IsNull(actual);
        }
        else
        {
            Assert.AreSame(pairs[expected], actual, expected.Kind.ToString());
        }
    }
}
