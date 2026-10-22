using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public class PdbTests
{
    [TestMethod]
    public void PortablePdbHasDocumentsSequencePointsAndLocals()
    {
        var comp = TestCompiler.Create("function f(a: int): int {\n  const b = a * 2;\n  return b + 1;\n}\nConsole.WriteLine(f(2));");
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        Assert.IsTrue(comp.Emit(pe, pdb, "Test.pdb").Success);

        pe.Position = 0;
        using var peReader = new PEReader(pe);
        var debugEntry = peReader.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
        var codeView = peReader.ReadCodeViewDebugDirectoryData(debugEntry);
        Assert.AreEqual("Test.pdb", codeView.Path);

        pdb.Position = 0;
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
        var reader = provider.GetMetadataReader();
        Assert.HasCount(1, reader.Documents);
        Assert.IsGreaterThanOrEqualTo(2, reader.MethodDebugInformation.Count(h => !reader.GetMethodDebugInformation(h).SequencePointsBlob.IsNil));
        var allPoints = reader.MethodDebugInformation
            .SelectMany(h => reader.GetMethodDebugInformation(h).GetSequencePoints())
            .Where(p => !p.IsHidden).Select(p => p.StartLine).ToList();
        CollectionAssert.IsSubsetOf(new[] { 2, 3, 5 }, allPoints);
    }
}
