using System.Reflection;
using Caladesi.Build.Tasks;
using Microsoft.Build.Utilities;

namespace Caladesi.Compiler.Tests.Build;

[TestClass]
public class CaladesiCompileTests
{
    private static readonly string[] s_sources = ["/proj/a.cala"];
    private static readonly string[] s_defines = ["-define:DEBUG", "-define:TRACE", "-define:OTHER"];
    private static readonly string[] s_noWarn = ["-nowarn:CAL0001;CAL0002;CAL0003"];

    private static string[] ResponseFileLines(CaladesiCompile task)
    {
        MethodInfo generate = typeof(CaladesiCompile).GetMethod("GenerateResponseFileCommands", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string text = (string)generate.Invoke(task, null)!;
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [TestMethod]
    public void Item40_MultiLineNoWarnAndDefineConstantsStayOptionsInTheResponseFile()
    {
        CaladesiCompile task = new()
        {
            CompilerPath = "calac.dll",
            OutputAssembly = "out.dll",
            NoWarn = "CAL0001;\n    CAL0002,\r\n\tCAL0003 ",
            DefineConstants = "DEBUG;\n    TRACE\r\n\tOTHER",
            Sources = [new TaskItem("/proj/a.cala")],
        };

        string[] lines = ResponseFileLines(task);
        CollectionAssert.AreEqual(s_sources, lines.Where(l => !l.StartsWith('-')).ToArray());
        CollectionAssert.AreEqual(s_defines, lines.Where(l => l.StartsWith("-define:", StringComparison.Ordinal)).ToArray());
        CollectionAssert.AreEqual(s_noWarn, lines.Where(l => l.StartsWith("-nowarn:", StringComparison.Ordinal)).ToArray());
    }
}
