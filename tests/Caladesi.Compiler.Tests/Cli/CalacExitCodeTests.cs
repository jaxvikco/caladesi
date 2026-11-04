using System.Diagnostics;

namespace Caladesi.Compiler.Tests.Cli;

[TestClass]
public class CalacExitCodeTests
{
    private static (int ExitCode, string Output, bool OutputWritten) RunCalac(string source, params string[] extraReferences)
    {
        string work = Path.Join(Path.GetTempPath(), "calac-test-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            string main = Path.Join(work, "main.cala");
            string output = Path.Join(work, "app.dll");
            File.WriteAllText(main, source);
            ProcessStartInfo psi = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(SelfHostCli.CliParityTests.FindCli(selfHost: false));
            psi.ArgumentList.Add("-out:" + output);
            psi.ArgumentList.Add("-assemblyname:app");
            psi.ArgumentList.Add("-rootnamespace:App");
            psi.ArgumentList.Add("-projectdir:" + work);
            foreach (string r in TestReferences.Paths.Concat(extraReferences))
            {
                psi.ArgumentList.Add("-ref:" + r);
            }

            psi.ArgumentList.Add("-ref:" + typeof(Runtime.CaladesiUnionAttribute).Assembly.Location);
            psi.ArgumentList.Add(main);
            using Process p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, text, File.Exists(output));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [TestMethod]
    public void Item29_MissingReferenceFailsTheBuild()
    {
        (int exit, string output, bool written) = RunCalac("Console.WriteLine(1);", "/missing/Foo.dll");
        StringAssert.Contains(output, "CAL0100");
        Assert.AreEqual(1, exit, output);
        Assert.IsFalse(written);
    }

    [TestMethod]
    public void Item29_ValidReferencesStillBuild()
    {
        (int exit, string output, bool written) = RunCalac("Console.WriteLine(1);");
        Assert.AreEqual(0, exit, output);
        Assert.IsTrue(written);
    }
}
