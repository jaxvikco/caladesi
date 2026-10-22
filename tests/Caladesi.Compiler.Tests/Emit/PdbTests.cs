using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public class PdbTests
{
    private static readonly int[] s_expectedPoints = [2, 3, 5];

    [TestMethod]
    public void PortablePdbHasDocumentsSequencePointsAndLocals()
    {
        Compilation comp = TestCompiler.Create("function f(a: int): int {\n  const b = a * 2;\n  return b + 1;\n}\nConsole.WriteLine(f(2));");
        using MemoryStream pe = new();
        using MemoryStream pdb = new();
        Assert.IsTrue(comp.Emit(pe, pdb, "Test.pdb").Success);

        pe.Position = 0;
        using PEReader peReader = new(pe);
        DebugDirectoryEntry debugEntry = peReader.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
        CodeViewDebugDirectoryData codeView = peReader.ReadCodeViewDebugDirectoryData(debugEntry);
        Assert.AreEqual("Test.pdb", codeView.Path);

        pdb.Position = 0;
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
        MetadataReader reader = provider.GetMetadataReader();
        Assert.HasCount(1, reader.Documents);
        Assert.IsGreaterThanOrEqualTo(2, reader.MethodDebugInformation.Count(h => !reader.GetMethodDebugInformation(h).SequencePointsBlob.IsNil));
        List<int> allPoints = [.. reader.MethodDebugInformation
            .SelectMany(h => reader.GetMethodDebugInformation(h).GetSequencePoints())
            .Where(p => !p.IsHidden).Select(p => p.StartLine)];
        CollectionAssert.IsSubsetOf(s_expectedPoints, allPoints);
    }
}
