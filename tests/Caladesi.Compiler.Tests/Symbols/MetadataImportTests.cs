using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Metadata;

namespace Caladesi.Compiler.Tests.Symbols;

[TestClass]
public class MetadataImportTests
{
    private static readonly Lazy<ReferenceSet> s_set = new(() => ReferenceSet.Create(TestReferences.Paths));

    [TestMethod]
    public void SpecialTypes_Resolve()
    {
        var set = s_set.Value;
        foreach (var t in new[] { SpecialType.Object, SpecialType.Int32, SpecialType.String, SpecialType.Double, SpecialType.TaskT, SpecialType.Nullable, SpecialType.BigInteger })
            Assert.IsNotNull(set.GetSpecialType(t), t.ToString());
        Assert.AreEqual(TypeKind.Struct, set.GetSpecialType(SpecialType.Int32)!.TypeKind);
        Assert.AreEqual(TypeKind.Class, set.GetSpecialType(SpecialType.String)!.TypeKind);
    }

    [TestMethod]
    public void List_Add_Substituted()
    {
        var set = s_set.Value;
        var list = set.FindType("System.Collections.Generic.List`1")!;
        var listInt = list.Construct(set.GetSpecialType(SpecialType.Int32)!);
        var add = listInt.GetMembers("Add").OfType<MethodSymbol>().Single();
        Assert.AreEqual("int", add.Parameters[0].Type.ToDisplayString());
        Assert.AreEqual("List<int>", listInt.ToDisplayString());
        var indexer = listInt.GetMembers("Item").OfType<PropertySymbol>().Single();
        Assert.IsTrue(indexer.IsIndexer);
        Assert.AreEqual("int", indexer.Type.ToDisplayString());
    }

    [TestMethod]
    public void Enumerable_Select_IsExtension_AndGeneric()
    {
        var set = s_set.Value;
        var en = set.FindType("System.Linq.Enumerable")!;
        var select = en.GetMembers("Select").OfType<MethodSymbol>().ToList();
        Assert.HasCount(2, select);
        Assert.IsTrue(select.All(m => m.IsExtensionMethod && m.Arity == 2));
    }

    [TestMethod]
    public void Console_WriteLine_Overloads_And_Nullability()
    {
        var set = s_set.Value;
        var console = set.FindType("System.Console")!;
        Assert.IsTrue(console.IsStatic);
        var wl = console.GetMembers("WriteLine").OfType<MethodSymbol>().ToList();
        Assert.IsGreaterThan(10, wl.Count);
        var rl = console.GetMembers("ReadLine").OfType<MethodSymbol>().Single();
        Assert.AreEqual("string | null", rl.ReturnType.ToDisplayString());
    }

    [TestMethod]
    public void Dictionary_TryGetValue_OutParam()
    {
        var set = s_set.Value;
        var dict = set.FindType("System.Collections.Generic.Dictionary`2")!;
        var tgv = dict.GetMembers("TryGetValue").OfType<MethodSymbol>().Single();
        Assert.AreEqual(RefKind.Out, tgv.Parameters[1].RefKind);
    }

    [TestMethod]
    public void Enum_And_Delegate_Kinds()
    {
        var set = s_set.Value;
        Assert.AreEqual(TypeKind.Enum, set.FindType("System.DayOfWeek")!.TypeKind);
        Assert.AreEqual(TypeKind.Delegate, set.FindType("System.Func`2")!.TypeKind);
        Assert.AreEqual(TypeKind.Interface, set.FindType("System.IDisposable")!.TypeKind);
        var func = set.FindType("System.Func`2")!.Construct(set.GetSpecialType(SpecialType.Int32)!, set.GetSpecialType(SpecialType.String)!);
        Assert.AreEqual("string", func.DelegateInvokeMethod!.ReturnType.ToDisplayString());
    }
}
