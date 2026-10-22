using Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.Syntax;

[TestClass]
public class TemplateSourceTests
{
    private static string TemplatesRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caladesi.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "Caladesi.Templates", "templates");
    }

    public static IEnumerable<string> TemplateSources()
    {
        return Directory.EnumerateFiles(TemplatesRoot(), "*.cala", SearchOption.AllDirectories);
    }

    [TestMethod]
    [DynamicData(nameof(TemplateSources))]
    public void Item20_TemplateSourceParsesWithoutErrors(string path)
    {
        SyntaxTree tree = SyntaxTree.ParseText(File.ReadAllText(path), path);
        Assert.IsEmpty(tree.Diagnostics, TestCompiler.Format(tree.Diagnostics));
    }
}
