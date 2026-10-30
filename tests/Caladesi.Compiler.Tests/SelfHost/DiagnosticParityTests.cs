extern alias SelfHost;

using System.Globalization;
using System.Reflection;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Text;
using SelfBag = SelfHost::Caladesi.Compiler.Diagnostics.DiagnosticBag;
using SelfDescriptor = SelfHost::Caladesi.Compiler.Diagnostics.DiagnosticDescriptor;
using SelfDiagnostic = SelfHost::Caladesi.Compiler.Diagnostics.Diagnostic;
using SelfDescriptors = SelfHost::Caladesi.Compiler.Diagnostics.Descriptors;
using SelfSeverity = SelfHost::Caladesi.Compiler.Diagnostics.DiagnosticSeverity;
using SelfSpan = SelfHost::Caladesi.Compiler.Text.TextSpan;
using SelfText = SelfHost::Caladesi.Compiler.Text.SourceText;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class DiagnosticParityTests
{
    [TestMethod]
    public void Descriptors_EntireCatalogMatchesBootstrap()
    {
        var expected = typeof(Descriptors).GetFields(BindingFlags.Public | BindingFlags.Static);
        var actual = typeof(SelfDescriptors).GetProperties(BindingFlags.Public | BindingFlags.Static);
        Assert.HasCount(expected.Length, actual);
        foreach (var field in expected)
        {
            var left = (DiagnosticDescriptor)field.GetValue(null)!;
            var other = typeof(SelfDescriptors).GetProperty(field.Name)!;
            Assert.IsNotNull(other, field.Name);
            var right = (SelfDescriptor)other.GetValue(null)!;
            Assert.AreEqual(left.Id, right.Id, field.Name);
            Assert.AreEqual((int)left.Severity, (int)right.Severity, field.Name);
            Assert.AreEqual(left.MessageFormat, right.MessageFormat, field.Name);
        }
    }

    [TestMethod]
    public void Diagnostic_FormattingCopyingAndInvariantArgumentsMatchBootstrap()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var custom = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            custom.NumberFormat.NumberDecimalSeparator = ",";
            custom.NumberFormat.NumberGroupSeparator = ".";
            CultureInfo.CurrentCulture = custom;
            DiagnosticDescriptor descriptor = new("CAL9999", DiagnosticSeverity.Warning, "Value {0:N2}, '{1}', {2}.");
            SelfDescriptor selfDescriptor = new("CAL9999", SelfSeverity.Warning, "Value {0:N2}, '{1}', {2}.");
            foreach (var path in new[] { null, "", "x.cala", "dir/file.cala" })
            {
                foreach (var start in new[] { -1, 0, 3, 5, 100 })
                {
                    object?[] arguments = [1234.5, "x", null];
                    var expected = Diagnostic.Create(descriptor, path, new(start, 2), arguments);
                    var actual = SelfDiagnostic.Create(selfDescriptor, path, new(start, 2), arguments);
                    AssertDiagnostic(expected, actual);
                    Assert.IsTrue(ReferenceEquals(arguments, actual.Arguments));
                    foreach (var severity in Enum.GetValues<DiagnosticSeverity>())
                    {
                        AssertDiagnostic(expected.WithSeverity(severity), actual.WithSeverity((SelfSeverity)severity));
                        AssertDiagnostic(expected.WithSeverity(severity).WithFilePath("new.cala"),
                            actual.WithSeverity((SelfSeverity)severity).WithFilePath("new.cala"));
                    }

                    AssertDiagnostic(expected.WithFilePath(null), actual.WithFilePath(null));
                    Assert.AreEqual(DiagnosticSeverity.Warning, expected.EffectiveSeverity);
                    Assert.AreEqual(SelfSeverity.Warning, actual.EffectiveSeverity);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1508", Justification = "Explicitly verify equality rejects null and unrelated values.")]
    public void Diagnostic_ExplicitFixItAndDefaultSeverityMatchBootstrap()
    {
        DiagnosticDescriptor descriptor = new("CAL9999", DiagnosticSeverity.Error, "Message.");
        SelfDescriptor selfDescriptor = new("CAL9999", SelfSeverity.Error, "Message.");
        FixIt fix = new("Replace", new(1, 2), "new");
        SelfHost::Caladesi.Compiler.Diagnostics.FixIt selfFix = new("Replace", new(1, 2), "new");
        Diagnostic expected = new(descriptor, "a.cala", new(1, 2), [], fix);
        SelfDiagnostic actual = new(selfDescriptor, "a.cala", new(1, 2), [], selfFix, SelfSeverity.Hidden);
        AssertDiagnostic(expected, actual);
        Assert.AreEqual(fix.Title, actual.FixIt!.Title);
        Assert.AreEqual(fix.Span.Start, actual.FixIt.Span.Start);
        Assert.AreEqual(fix.Span.Length, actual.FixIt.Span.Length);
        Assert.AreEqual(fix.NewText, actual.FixIt.NewText);
        Assert.IsTrue(ReferenceEquals(selfFix, actual.WithFilePath("b.cala").FixIt));
        Assert.IsTrue(ReferenceEquals(selfFix, actual.WithSeverity(SelfSeverity.Error).FixIt));
        Assert.AreEqual(fix.ToString(), selfFix.ToString());
        SelfHost::Caladesi.Compiler.Diagnostics.FixIt equalFix = new("Replace", new(1, 2), "new");
        Assert.IsTrue(selfFix.Equals(equalFix));
        Assert.AreEqual(selfFix.GetHashCode(), equalFix.GetHashCode());
        Assert.IsFalse(selfFix.Equals(new SelfHost::Caladesi.Compiler.Diagnostics.FixIt("Other", new(1, 2), "new")));
        Assert.IsFalse(selfFix.Equals(new SelfHost::Caladesi.Compiler.Diagnostics.FixIt("Replace", new(2, 2), "new")));
        Assert.IsFalse(selfFix.Equals(new SelfHost::Caladesi.Compiler.Diagnostics.FixIt("Replace", new(1, 2), "other")));
        Assert.IsFalse(selfFix.Equals(null));
        Assert.IsFalse(selfFix.Equals("Replace"));
    }

    [TestMethod]
    public void DiagnosticBag_CollectionErrorTrackingAndTruncationMatchBootstrap()
    {
        DiagnosticBag expected = new();
        SelfBag actual = new();
        foreach (var severity in Enum.GetValues<DiagnosticSeverity>())
        {
            expected.Add(Diagnostic.Create(new("CAL9999", severity, "Message."), "", new()));
            actual.Add(SelfDiagnostic.Create(new("CAL9999", (SelfSeverity)severity, "Message."), "", new()));
            Assert.AreEqual(expected.Count, actual.Count);
            Assert.AreEqual(expected.HasErrors, actual.HasErrors);
        }

        var oldExpected = expected.ToImmutable();
        var oldActual = actual.ToImmutable();
        for (var count = 6; count >= 0; count--)
        {
            expected.TruncateTo(count);
            actual.TruncateTo(count);
            Assert.AreEqual(expected.Count, actual.Count);
            Assert.AreEqual(expected.HasErrors, actual.HasErrors);
            Assert.HasCount(expected.ToImmutable().Length, actual.ToImmutable());
        }

        Assert.HasCount(4, oldActual);
        expected.AddRange(oldExpected);
        actual.AddRange(oldActual);
        Assert.AreEqual(expected.Count, actual.Count);
        Assert.AreEqual(expected.HasErrors, actual.HasErrors);
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => actual.TruncateTo(-1));
        Assert.AreEqual(expected.Count, actual.Count);
    }

    [TestMethod]
    public void Diagnostic_NullDescriptorFailsBeforeConstruction()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => SelfDiagnostic.Create(null!, "", new SelfSpan()));
    }

    [TestMethod]
    public void CompilerInfo_VersionReadsOwnAssemblyMetadata()
    {
        var informational = typeof(SelfHost::Caladesi.Compiler.CompilerInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var expected = string.IsNullOrEmpty(informational) ? "0.0.0" : informational.Split('+')[0];
        Assert.AreEqual(expected, SelfHost::Caladesi.Compiler.CompilerInfo.Version);
    }

    private static void AssertDiagnostic(Diagnostic expected, SelfDiagnostic actual)
    {
        const string text = "a\r\nbc\n";
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.Message, actual.Message);
        Assert.AreEqual(expected.FilePath, actual.FilePath);
        Assert.AreEqual(expected.Span.Start, actual.Span.Start);
        Assert.AreEqual(expected.Span.Length, actual.Span.Length);
        Assert.AreEqual((int)expected.EffectiveSeverity, (int)actual.EffectiveSeverity);
        Assert.AreEqual(expected.Format(), actual.Format());
        Assert.AreEqual(expected.ToString(), actual.ToString());
        Assert.AreEqual(expected.Format(SourceText.From(text)), actual.Format(SelfText.From(text)));
    }
}
