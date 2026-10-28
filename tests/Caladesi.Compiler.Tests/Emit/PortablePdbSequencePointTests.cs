using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public sealed class PortablePdbSequencePointTests
{
    [TestMethod]
    [DataRow("let first: string;\nlet second: string;\nfirst = \"one\";\nsecond = \"two\";\nConsole.WriteLine(first + second);")]
    [DataRow("function read(): void {\ntry {\nwhile (true) {\nlet text: string;\ntry { text = \"input\"; }\ncatch (ex: Exception) { continue; }\nConsole.WriteLine(text);\nbreak;\n}\n} finally { Console.WriteLine(\"done\"); }\n}\nread();")]
    [DataRow("function empty(): void {\nlet unused: string;\n}\nempty();")]
    public void SequencePoints_HaveValidDocumentsAndStrictlyIncreasingOffsets(string source)
    {
        Compilation compilation = TestCompiler.Create(source);
        using MemoryStream pe = new();
        using MemoryStream pdb = new();
        Assert.IsTrue(compilation.Emit(pe, pdb, "Test.pdb").Success);
        pe.Position = 0;
        pdb.Position = 0;
        using PEReader peReader = new(pe);
        MetadataReader metadata = peReader.GetMetadataReader();
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
        MetadataReader debug = provider.GetMetadataReader();
        List<int> offsets = [];
        foreach (MethodDebugInformationHandle handle in debug.MethodDebugInformation)
        {
            MethodDebugInformation info = debug.GetMethodDebugInformation(handle);
            if (info.SequencePointsBlob.IsNil)
            {
                continue;
            }

            MethodDefinition definition = metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle)));
            int bodyLength = peReader.GetMethodBody(definition.RelativeVirtualAddress).GetILContent().Length;
            int previous = -1;
            foreach (SequencePoint point in info.GetSequencePoints())
            {
                Assert.IsGreaterThan(previous, point.Offset);
                Assert.IsLessThan(bodyLength, point.Offset);
                Assert.IsFalse(point.Document.IsNil);
                Document document = debug.GetDocument(point.Document);
                Assert.IsNotEmpty(debug.GetString(document.Name));
                offsets.Add(point.Offset);
                previous = point.Offset;
            }
        }

        Assert.IsNotEmpty(offsets);
    }
}
