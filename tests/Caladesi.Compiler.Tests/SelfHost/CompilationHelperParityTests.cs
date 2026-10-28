using System.Reflection;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Tests.Emit;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class CompilationHelperParityTests
{
    private static readonly Lazy<Assembly> s_helpers = new(BuildHelpers);

    [TestMethod]
    public void CompilationOptions_RealSourceEmitsWithMatchingDefaults()
    {
        CompilationOptions expected = new();
        Type type = HelperType("Caladesi.Compiler.CompilationOptions");
        object actual = Activator.CreateInstance(type)!;
        foreach (PropertyInfo property in typeof(CompilationOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            object? left = property.GetValue(expected);
            object? right = type.GetProperty(property.Name)!.GetValue(actual);
            if (left is IEnumerable<string> values)
            {
                CollectionAssert.AreEqual(values.ToArray(), ((IEnumerable<string>)right!).ToArray(), property.Name);
            }
            else if (left is Enum)
            {
                Assert.AreEqual(Convert.ToInt32(left), Convert.ToInt32(right), property.Name);
            }
            else
            {
                Assert.AreEqual(left, right, property.Name);
            }
        }
    }

    [TestMethod]
    public void NameUtilities_RealSourceEmitsWithMatchingUnicodeConversions()
    {
        Type type = HelperType("Caladesi.Compiler.NameUtilities");
        string[] names = ["", "data-access", "my_file", "foo bar", "123 thing", "AlreadyPascal", "éclair data",
            "αβ_γδ", "a😀b", "𐐀x", " \t\r\n ", "_123_", "URL-parser", "iİıI"];
        foreach (string name in names)
        {
            Assert.AreEqual(NameUtilities.ToPascalCase(name), type.GetMethod("ToPascalCase")!.Invoke(null, [name]), name);
            Assert.AreEqual(NameUtilities.FlipFirstCase(name), type.GetMethod("FlipFirstCase")!.Invoke(null, [name]), name);
        }

        foreach (string method in new[] { "ToPascalCase", "FlipFirstCase" })
        {
            TargetInvocationException error = Assert.ThrowsExactly<TargetInvocationException>(
                () => type.GetMethod(method)!.Invoke(null, [null]));
            Assert.IsInstanceOfType<ArgumentNullException>(error.InnerException);
        }
    }

    [TestMethod]
    public void TestEntryPoint_RealSourceEmitsAndMatchesHookOrderingAndPaths()
    {
        Type optionsType = HelperType("Caladesi.Compiler.CompilationOptions");
        Type entryType = HelperType("Caladesi.Compiler.Syntax.TestEntryPoint");
        string[] hooks = ["NoNamespace", "Example.UnitTesting.TestingPlatformBuilderHook", "Other.Extension.Hook",
            "Other.Extension.Hook", "Coverage.TestingPlatformBuilderHook", "Example.UnitTesting.TestingPlatformBuilderHook"];
        foreach (string? directory in new[] { null, Path.Combine(RepositoryRoot(), "test-project") })
        {
            CompilationOptions expectedOptions = new() { ProjectDirectory = directory };
            SyntaxTree expected = TestEntryPoint.Create(expectedOptions, hooks);
            object options = Activator.CreateInstance(optionsType)!;
            optionsType.GetProperty("ProjectDirectory")!.SetValue(options, directory);
            object actual = entryType.GetMethod("Create")!.Invoke(null, [options, hooks])!;
            Type treeType = actual.GetType();
            Assert.AreEqual(expected.FilePath, treeType.GetProperty("FilePath")!.GetValue(actual));
            Assert.AreEqual(expected.Text.ToString(), treeType.GetProperty("Text")!.GetValue(actual)!.ToString());
        }
    }

    private static Type HelperType(string name) => s_helpers.Value.GetType(name, throwOnError: true)!;

    private static Assembly BuildHelpers()
    {
        string sourceRoot = Path.Combine(RepositoryRoot(), "src", "Caladesi.Compiler.SelfHost");
        List<SyntaxTree> trees = [];
        foreach (string folder in new[] { "Text", "Syntax", "Diagnostics", "Formatting" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(sourceRoot, folder), "*.cala"))
            {
                trees.Add(SyntaxTree.ParseText(File.ReadAllText(file), file));
            }
        }

        string options = Path.Combine(sourceRoot, "CompilationOptions.cala");
        trees.Add(SyntaxTree.ParseText(File.ReadAllText(options), options));
        string namesPath = Path.Combine(sourceRoot, "NameUtilities.cala");
        trees.Add(SyntaxTree.ParseText(File.ReadAllText(namesPath), namesPath));
        Compilation compilation = Compilation.Create(new CompilationOptions
        {
            OutputKind = OutputKind.DynamicallyLinkedLibrary,
            AssemblyName = "CompilationHelpers",
            RootNamespace = "Caladesi.Compiler",
            ProjectDirectory = sourceRoot,
        }, trees, TestCompiler.References);
        using MemoryStream pe = new();
        var result = compilation.Emit(pe);
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.EffectiveSeverity == DiagnosticSeverity.Error).Select(d => d.Format())));
        byte[] image = pe.ToArray();
        Assert.IsEmpty(ILVerifier.Verify("CompilationHelpers", image));
        return Assembly.Load(image);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Caladesi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
