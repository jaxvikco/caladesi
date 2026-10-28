extern alias SelfHost;

using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;
using Caladesi.Compiler.Syntax;
using H = SelfHost::Caladesi.Compiler.Emit;
using HS = SelfHost::Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class PortableDebugEmitterParityTests
{
    private static readonly Type ModuleType = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.EmitModule", true)!;
    private static readonly Type PointType = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.SequencePoint", true)!;
    private static readonly Type LocalType = typeof(Compilation).Assembly.GetType("Caladesi.Compiler.Emit.LocalSlot", true)!;

    [TestMethod]
    public void SequencePointEncoding_MatchesOriginalAcrossCompressedBoundariesAndHiddenTransitions()
    {
        List<H.SequencePoint>[] cases =
        [
            [],
            [Hidden(0)],
            [Hidden(0), Hidden(1), Point(2, 1, 0, 1, 1)],
            [Point(0, 10, 4, 10, 5), Hidden(1), Point(2, 9, 2, 11, 0)],
            [Point(0, 1, 1, 1, 2), Point(0, 90, 90, 90, 91), Hidden(0), Hidden(-1),
                Point(2, 2, 2, 2, 3), Point(1, 99, 99, 99, 100), Hidden(3), Point(4, 3, 0, 4, 0)],
            [Point(0, 0, 0, 0, 1), Point(1, 0, 2, 1, 0), Point(2, 1, 0, 1, 1)],
        ];
        foreach (int signature in new[] { 0, 1, 127, 128, 16383, 16384, 0xffffff })
            foreach (List<H.SequencePoint> points in cases)
                CompareEncoding(points, signature);

        foreach (int boundary in new[] { 0, 1, 63, 64, 127, 128, 8191, 8192, 16383, 16384, 0x1fffffff })
        {
            CompareEncoding([Point(boundary, 1, 0, 1, 1)], 0);
            CompareEncoding([Point(0, 1, 0, 1, boundary)], 1);
            CompareEncoding([Point(0, 1, 0, 1 + boundary, 0)], 1);
            CompareEncoding([Point(0, boundary, boundary, boundary, boundary)], 128);
        }
        foreach (int boundary in new[] { -268435456, -8193, -8192, -65, -64, -1, 0, 1, 63, 64, 8191, 8192, 268435455 })
        {
            int start = Math.Max(0, -boundary);
            CompareEncoding([Point(0, 1, start, 2, start + boundary)], 1);
            int line = Math.Max(1, 1 - boundary);
            CompareEncoding([Point(0, line, start, line, start + 1),
                Point(1, line + boundary, start + boundary, line + boundary, start + boundary + 1)], 1);
        }
    }

    [TestMethod]
    public void SequencePointEncoding_InvalidRangesPreserveOriginalExceptions()
    {
        foreach (List<H.SequencePoint> points in new List<H.SequencePoint>[]
        {
            [Hidden(-1)], [Hidden(0x20000000)],
            [Point(0, 1, 2, 0, 3)], [Point(0, 1, 2, 1, 1)],
            [Point(0, -1, 0, 0, 1)], [Point(0, 1, -1, 1, 0)],
            [Point(0, 1, 0, 2, 268435456)],
            [Point(0, 1, 0, 2, -268435457)],
            [Point(0, int.MaxValue, 0, int.MinValue, 1)],
        })
            CompareEncoding(points, 0);
    }

    [TestMethod]
    public void CompletePdb_MatchesOriginalDecodesAndIsIndependentlyDeterministic()
    {
        foreach (bool entryPoint in new[] { false, true })
        {
            Fixture oracle = new(entryPoint);
            (byte[] expected, byte[] expectedPe) = oracle.Emit(original: true);
            Fixture first = new(entryPoint);
            (byte[] actual, byte[] actualPe) = first.Emit(original: false);
            CollectionAssert.AreEqual(expected, actual);
            CollectionAssert.AreEqual(expectedPe, actualPe);
            Fixture independent = new(entryPoint);
            (byte[] repeated, byte[] repeatedPe) = independent.Emit(original: false);
            CollectionAssert.AreEqual(actual, repeated);
            CollectionAssert.AreEqual(actualPe, repeatedPe);
            first.Check(actual, actualPe);
        }
    }

    private static H.SequencePoint Point(int offset, int line, int column, int endLine, int endColumn) =>
        new(offset, line, column, endLine, endColumn, false);

    private static H.SequencePoint Hidden(int offset) => new(offset, 0xfeefee, 0, 0xfeefee, 0, true);

    private static IList OriginalPoints(List<H.SequencePoint> points)
    {
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(PointType))!;
        foreach (H.SequencePoint point in points)
            list.Add(Activator.CreateInstance(PointType, point.Offset, point.StartLine, point.StartColumn,
                point.EndLine, point.EndColumn, point.IsHidden));
        return list;
    }

    private static void CompareEncoding(List<H.SequencePoint> points, int signature)
    {
        byte[]? expected = null;
        byte[]? actual = null;
        Exception? expectedError = null;
        Exception? actualError = null;
        StandaloneSignatureHandle handle = MetadataTokens.StandaloneSignatureHandle(signature);
        try
        {
            expected = (byte[])ModuleType.GetMethod("EncodeSequencePoints", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [OriginalPoints(points), handle])!;
        }
        catch (TargetInvocationException error) { expectedError = error.InnerException; }
        try { actual = H.PortableDebugEmitter<SourceFile>.EncodeSequencePoints(points, handle); }
        catch (ArgumentException error) { actualError = error; }
        Assert.AreEqual(expectedError?.GetType(), actualError?.GetType());
        Assert.AreEqual(expectedError?.Message, actualError?.Message);
        if (expectedError == null) CollectionAssert.AreEqual(expected!, actual!);
    }

    private sealed class Fixture
    {
        private static readonly int[] ExpectedOffsets = [0, 1, 3, 5];
        private static readonly int[] ExpectedScopeMethods = [2, 4, 7];
        private static readonly int[] ExpectedScopeLengths = [1, 24, 1];
        private static readonly string[] ExpectedLocalNames = ["withoutFile", "z", "", "é", "tail"];
        private static readonly int[] ExpectedLocalIndices = [3, 4, 2, 1, 5];
        private readonly Compilation compilation = Compilation.Create(new CompilationOptions(),
            [SyntaxTree.ParseText("// café ☃\r\n", "src/α.cala"),
             SyntaxTree.ParseText("// second\n", "other/β.cala"),
             SyntaxTree.ParseText("", "unused.cala")], Array.Empty<string>());
        private readonly object original;
        private readonly MetadataBuilder metadata;
        private readonly MetadataRootBuilder root;
        private readonly Dictionary<MethodDefinitionHandle, H.MethodDebugInfo<SourceFile>> debug = [];
        private readonly Dictionary<MethodDefinitionHandle, List<H.SequencePoint>> points = [];
        private readonly MethodDefinitionHandle entryPoint;
        private readonly SourceFile[] files;

        public Fixture(bool hasEntryPoint)
        {
            files = compilation.SourceFiles.ToArray();
            original = Activator.CreateInstance(ModuleType, compilation)!;
            metadata = (MetadataBuilder)ModuleType.GetProperty("Metadata")!.GetValue(original)!;
            metadata.AddModule(0, metadata.GetOrAddString("debug.dll"),
                metadata.GetOrAddGuid(new Guid("a6c668bb-62e4-4c6a-9c0f-d21008795388")), default, default);
            metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"),
                default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            BlobHandle methodSignature = metadata.GetOrAddBlob(new byte[] { 0, 0, 1 });
            for (int row = 1; row <= 7; row++)
                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                    metadata.GetOrAddString("M" + row), methodSignature, 0, MetadataTokens.ParameterHandle(1));
            StandaloneSignatureHandle signature = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(new byte[] { 7, 0 }));
            points[MetadataTokens.MethodDefinitionHandle(1)] = [Hidden(0)];
            Add(2, null, signature, 0, [(3, "withoutFile"), (0, null)], [Hidden(0)]);
            Add(3, files[0], default, 9, [(0, null)], null);
            Add(4, files[0], signature, 24, [(4, "z"), (0, null), (2, ""), (1, "é")],
                [Hidden(0), Point(1, 2, 3, 2, 5), Point(1, 99, 0, 99, 1),
                 Point(0, 90, 0, 90, 1), Hidden(3), Point(5, 1, 1, 3, 0)]);
            Add(5, files[1], default, 2, [], [Hidden(0), Hidden(1)]);
            Add(6, files[1], default, 2, [], []);
            Add(7, files[0], default, -5, [(5, "tail")], [Point(0, 1, 0, 1, 1)]);
            Type programType = ModuleType.Assembly.GetType("Caladesi.Compiler.Emit.LoweredProgram", true)!;
            object program = Activator.CreateInstance(programType)!;
            if (hasEntryPoint)
            {
                SynthesizedMethodSymbol method = new(ErrorTypeSymbol.Instance, "Entry", MethodKind.Ordinary,
                    Accessibility.Public, true, ErrorTypeSymbol.Instance, []);
                entryPoint = MetadataTokens.MethodDefinitionHandle(4);
                programType.GetProperty("EntryPoint")!.SetValue(program, method);
                ((IDictionary)Field("_methodDefs")).Add(method, entryPoint);
            }
            ModuleType.GetField("_program", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(original, program);
            foreach ((MethodDefinitionHandle method, List<H.SequencePoint> sequence) in points)
                ((IDictionary)Field("_sequencePoints")).Add(method, OriginalPoints(sequence));
            root = new MetadataRootBuilder(metadata, "v4.0.30319", suppressValidation: false);
        }

        private object Field(string name) =>
            ModuleType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original)!;

        private void Add(int row, SourceFile? file, StandaloneSignatureHandle signature, int length,
            (int Index, string? Name)[] locals, List<H.SequencePoint>? sequence)
        {
            MethodDefinitionHandle method = MetadataTokens.MethodDefinitionHandle(row);
            List<H.LocalSlot> slots = locals.Select(local => new H.LocalSlot(HS.ErrorTypeSymbol.Instance, local.Name, local.Index)).ToList();
            debug.Add(method, new H.MethodDebugInfo<SourceFile>(slots, signature, length, file!));
            if (sequence != null) points.Add(method, sequence);
            IList originalLocals = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(LocalType))!;
            foreach ((int index, string? name) in locals)
                originalLocals.Add(Activator.CreateInstance(LocalType, ErrorTypeSymbol.Instance, name, index));
            IDictionary dictionary = (IDictionary)Field("_methodDebug");
            Type recordType = dictionary.GetType().GetGenericArguments()[1];
            dictionary.Add(method, Activator.CreateInstance(recordType, originalLocals, signature, length, file)!);
        }

        public (byte[] Pdb, byte[] Pe) Emit(bool original)
        {
            DebugDirectoryBuilder directory = new();
            using MemoryStream stream = new();
            if (original)
                ModuleType.GetMethod("EmitPdb", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(this.original, [directory, stream, "out/α.pdb", root]);
            else
                new H.PortableDebugEmitter<SourceFile>(metadata,
                    files.Select(file => new H.SourceDebugDocument<SourceFile>(file, file.FilePath, file.Tree.Text.Text)),
                    debug, points, entryPoint).Emit(directory, stream, "out/α.pdb", root);
            BlobBuilder pe = new();
            new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), root, new BlobBuilder(),
                debugDirectoryBuilder: directory, entryPoint: entryPoint,
                deterministicIdProvider: _ => new BlobContentId(new Guid("388ea1ca-90d5-4ffc-aaba-c29582d29330"), 42))
                .Serialize(pe);
            return (stream.ToArray(), pe.ToArray());
        }

        public void Check(byte[] bytes, byte[] peBytes)
        {
            using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(bytes));
            MetadataReader reader = provider.GetMetadataReader();
            Assert.AreEqual(entryPoint, reader.DebugMetadataHeader!.EntryPoint);
            Assert.HasCount(3, reader.Documents);
            int index = 0;
            foreach (DocumentHandle handle in reader.Documents)
            {
                Document document = reader.GetDocument(handle);
                SourceFile file = files[index++];
                Assert.AreEqual(file.FilePath, reader.GetString(document.Name));
                Assert.AreEqual(new Guid("8829d00f-11b8-4213-878b-770e8597ac16"), reader.GetGuid(document.HashAlgorithm));
                Assert.AreEqual(new Guid("3f5162f8-07c6-11d3-9053-00c04fa302a1"), reader.GetGuid(document.Language));
                CollectionAssert.AreEqual(SHA256.HashData(Encoding.UTF8.GetBytes(file.Tree.Text.Text)), reader.GetBlobBytes(document.Hash));
            }
            Assert.AreEqual(7, reader.GetTableRowCount(TableIndex.MethodDebugInformation));
            foreach (int row in new[] { 1, 2, 3, 6 })
            {
                MethodDebugInformation info = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(row));
                Assert.IsTrue(info.Document.IsNil);
                Assert.IsTrue(info.SequencePointsBlob.IsNil);
            }
            MethodDebugInformation fourth = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(4));
            Assert.AreEqual(MetadataTokens.DocumentHandle(1), fourth.Document);
            Assert.AreEqual(MetadataTokens.StandaloneSignatureHandle(1), fourth.LocalSignature);
            System.Reflection.Metadata.SequencePoint[] decoded = fourth.GetSequencePoints().ToArray();
            CollectionAssert.AreEqual(ExpectedOffsets, decoded.Select(point => point.Offset).ToArray());
            Assert.IsTrue(decoded[0].IsHidden);
            Assert.IsTrue(decoded[2].IsHidden);
            Assert.IsTrue(decoded.All(point => point.Document == fourth.Document));
            Assert.AreEqual(1, decoded[3].StartLine);
            Assert.AreEqual(0, decoded[3].EndColumn);
            Assert.AreEqual(MetadataTokens.DocumentHandle(2),
                reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(5)).Document);
            Assert.HasCount(3, reader.LocalScopes);
            Assert.HasCount(1, reader.ImportScopes);
            LocalScope[] scopes = reader.LocalScopes.Select(reader.GetLocalScope).ToArray();
            CollectionAssert.AreEqual(ExpectedScopeMethods, scopes.Select(scope => MetadataTokens.GetRowNumber(scope.Method)).ToArray());
            CollectionAssert.AreEqual(ExpectedScopeLengths, scopes.Select(scope => scope.Length).ToArray());
            Assert.IsTrue(scopes.All(scope => scope.StartOffset == 0 && scope.ImportScope == MetadataTokens.ImportScopeHandle(1)));
            LocalVariable[] variables = scopes.SelectMany(scope => scope.GetLocalVariables()).Select(reader.GetLocalVariable).ToArray();
            CollectionAssert.AreEqual(ExpectedLocalNames, variables.Select(variable => reader.GetString(variable.Name)).ToArray());
            CollectionAssert.AreEqual(ExpectedLocalIndices, variables.Select(variable => variable.Index).ToArray());
            Assert.IsTrue(variables.All(variable => variable.Attributes == LocalVariableAttributes.None));
            using PEReader pe = new(new MemoryStream(peBytes));
            DebugDirectoryEntry directory = pe.ReadDebugDirectory().Single();
            CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(directory);
            BlobContentId id = new(reader.DebugMetadataHeader.Id);
            Assert.AreEqual(DebugDirectoryEntryType.CodeView, directory.Type);
            Assert.IsTrue(directory.IsPortableCodeView);
            Assert.AreEqual("out/α.pdb", codeView.Path);
            Assert.AreEqual(1, codeView.Age);
            Assert.AreEqual(id.Guid, codeView.Guid);
            Assert.AreEqual(id.Stamp, directory.Stamp);
        }
    }
}
