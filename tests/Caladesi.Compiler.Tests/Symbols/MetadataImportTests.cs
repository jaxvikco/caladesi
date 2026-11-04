extern alias SelfHost;

using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Metadata;
using NativeMetadata = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using NativeSymbols = SelfHost::Caladesi.Compiler.Symbols;

namespace Caladesi.Compiler.Tests.Symbols;

[TestClass]
public class MetadataImportTests
{
    private static readonly Lazy<ReferenceSet> s_set = new(() => ReferenceSet.Create(TestReferences.Paths));

    public static void NullableTupleGeneric(Tuple<(string? Text, int Number)?, string?> values)
    {
        _ = values;
    }

    [TestMethod]
    public void SpecialTypes_Resolve()
    {
        ReferenceSet set = s_set.Value;
        foreach (SpecialType t in new[] { SpecialType.Object, SpecialType.Int32, SpecialType.String, SpecialType.Double, SpecialType.TaskT, SpecialType.Nullable, SpecialType.BigInteger })
        {
            Assert.IsNotNull(set.GetSpecialType(t), t.ToString());
        }

        Assert.AreEqual(TypeKind.Struct, set.GetSpecialType(SpecialType.Int32)!.TypeKind);
        Assert.AreEqual(TypeKind.Class, set.GetSpecialType(SpecialType.String)!.TypeKind);
    }

    [TestMethod]
    public void List_Add_Substituted()
    {
        ReferenceSet set = s_set.Value;
        NamedTypeSymbol list = set.FindType("System.Collections.Generic.List`1")!;
        NamedTypeSymbol listInt = list.Construct(set.GetSpecialType(SpecialType.Int32)!);
        MethodSymbol add = listInt.GetMembers("Add").OfType<MethodSymbol>().Single();
        Assert.AreEqual("int", add.Parameters[0].Type.ToDisplayString());
        Assert.AreEqual("List<int>", listInt.ToDisplayString());
        PropertySymbol indexer = listInt.GetMembers("Item").OfType<PropertySymbol>().Single();
        Assert.IsTrue(indexer.IsIndexer);
        Assert.AreEqual("int", indexer.Type.ToDisplayString());
    }

    [TestMethod]
    public void Enumerable_Select_IsExtension_AndGeneric()
    {
        ReferenceSet set = s_set.Value;
        NamedTypeSymbol en = set.FindType("System.Linq.Enumerable")!;
        List<MethodSymbol> select = [.. en.GetMembers("Select").OfType<MethodSymbol>()];
        Assert.HasCount(2, select);
        Assert.IsTrue(select.All(m => m.IsExtensionMethod && m.Arity == 2));
    }

    [TestMethod]
    public void Console_WriteLine_Overloads_And_Nullability()
    {
        ReferenceSet set = s_set.Value;
        NamedTypeSymbol console = set.FindType("System.Console")!;
        Assert.IsTrue(console.IsStatic);
        List<MethodSymbol> wl = [.. console.GetMembers("WriteLine").OfType<MethodSymbol>()];
        Assert.IsGreaterThan(10, wl.Count);
        MethodSymbol rl = console.GetMembers("ReadLine").OfType<MethodSymbol>().Single();
        Assert.AreEqual("string | null", rl.ReturnType.ToDisplayString());
    }

    [TestMethod]
    public void NullableMetadataDecoderKeepsGenericFlagPositionsAligned()
    {
        string[] paths = [.. TestReferences.Paths, typeof(MetadataImportTests).Assembly.Location];
        MethodSymbol oracle = ReferenceSet.Create(paths)
            .FindType("Caladesi.Compiler.Tests.Symbols.MetadataImportTests")!
            .GetMembers(nameof(NullableTupleGeneric)).OfType<MethodSymbol>().Single();
        NativeSymbols.MethodSymbol native = NativeMetadata.ReferenceSet.Create(paths)
            .FindType("Caladesi.Compiler.Tests.Symbols.MetadataImportTests")!
            .GetMembers(nameof(NullableTupleGeneric)).OfType<NativeSymbols.MethodSymbol>().Single();

        AssertNestedNullableTypes(oracle.Parameters[0].Type);
        AssertNestedNullableTypes(native.Parameters[0].Type);
    }

    [TestMethod]
    public void Dictionary_TryGetValue_OutParam()
    {
        ReferenceSet set = s_set.Value;
        NamedTypeSymbol dict = set.FindType("System.Collections.Generic.Dictionary`2")!;
        MethodSymbol tgv = dict.GetMembers("TryGetValue").OfType<MethodSymbol>().Single();
        Assert.AreEqual(RefKind.Out, tgv.Parameters[1].RefKind);
    }

    [TestMethod]
    public void Enum_And_Delegate_Kinds()
    {
        ReferenceSet set = s_set.Value;
        Assert.AreEqual(TypeKind.Enum, set.FindType("System.DayOfWeek")!.TypeKind);
        Assert.AreEqual(TypeKind.Delegate, set.FindType("System.Func`2")!.TypeKind);
        Assert.AreEqual(TypeKind.Interface, set.FindType("System.IDisposable")!.TypeKind);
        NamedTypeSymbol func = set.FindType("System.Func`2")!.Construct(set.GetSpecialType(SpecialType.Int32)!, set.GetSpecialType(SpecialType.String)!);
        Assert.AreEqual("string", func.DelegateInvokeMethod!.ReturnType.ToDisplayString());
    }

    private static void AssertNestedNullableTypes(TypeSymbol dictionaryType)
    {
        NamedTypeSymbol dictionary = (NamedTypeSymbol)dictionaryType;
        Assert.HasCount(2, dictionary.TypeArguments);
        NullableTypeSymbol tupleType = (NullableTypeSymbol)dictionary.TypeArguments[0];
        NamedTypeSymbol tuple = (NamedTypeSymbol)tupleType.Underlying;
        Assert.IsTrue(tuple.TypeArguments[0] is NullableTypeSymbol);
        Assert.IsTrue(dictionary.TypeArguments[1] is NullableTypeSymbol);
    }

    private static void AssertNestedNullableTypes(NativeSymbols.TypeSymbol dictionaryType)
    {
        NativeSymbols.NamedTypeSymbol dictionary = (NativeSymbols.NamedTypeSymbol)dictionaryType;
        Assert.HasCount(2, dictionary.TypeArguments);
        NativeSymbols.NullableTypeSymbol tupleType = (NativeSymbols.NullableTypeSymbol)dictionary.TypeArguments[0];
        NativeSymbols.NamedTypeSymbol tuple = (NativeSymbols.NamedTypeSymbol)tupleType.Underlying;
        Assert.IsTrue(tuple.TypeArguments[0] is NativeSymbols.NullableTypeSymbol);
        Assert.IsTrue(dictionary.TypeArguments[1] is NativeSymbols.NullableTypeSymbol);
    }
}
