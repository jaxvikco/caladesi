using System.Diagnostics;
using System.IO.Compression;

namespace Caladesi.Compiler.Tests.Build;

/// <summary>The SDK props and the templates take their version from the packed version instead of a hard-coded 1.0.0.</summary>
[TestClass]
public class SdkVersionTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caladesi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private static (int ExitCode, string Output) Dotnet(string workingDirectory, params string[] args)
    {
        ProcessStartInfo psi = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        Task<string> error = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + error.Result);
    }

    private static string EvaluateRuntimeVersion(string sdkFolderName, string extraProperties = "")
    {
        string work = Path.Combine(Path.GetTempPath(), "sdk-version-" + Guid.NewGuid().ToString("N"));
        string targets = Path.Combine(work, sdkFolderName, "targets");
        _ = Directory.CreateDirectory(targets);
        try
        {
            File.Copy(Path.Combine(RepoRoot(), "src", "Caladesi.Sdk", "targets", "Caladesi.props"), Path.Combine(targets, "Caladesi.props"));
            string project = Path.Combine(work, "eval.proj");
            File.WriteAllText(project, $"""
                <Project>
                  <PropertyGroup>{extraProperties}</PropertyGroup>
                  <Import Project="{Path.Combine(targets, "Caladesi.props")}" />
                </Project>
                """);
            (int exit, string output) = Dotnet(work, "msbuild", project, "-getProperty:CaladesiRuntimeVersion", "-nologo");
            Assert.AreEqual(0, exit, output);
            return output.Trim();
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [TestMethod]
    public void Item41_RuntimeVersionIsTheVersionOfThePackedSdk()
    {
        Assert.AreEqual("4.5.6", EvaluateRuntimeVersion("4.5.6"));
        Assert.AreEqual("4.5.6-ci.7", EvaluateRuntimeVersion("4.5.6-ci.7"));
    }

    [TestMethod]
    public void Item41_ExplicitRuntimeVersionWinsAndUnversionedLocationsGiveNone()
    {
        Assert.AreEqual("9.9.9", EvaluateRuntimeVersion("4.5.6", "<CaladesiRuntimeVersion>9.9.9</CaladesiRuntimeVersion>"));
        Assert.AreEqual("", EvaluateRuntimeVersion("Caladesi.Sdk"));
    }

    [TestMethod]
    public void Item41_PackedTemplatesRequestTheSdkTheyWerePackedWith()
    {
        string work = Path.Combine(Path.GetTempPath(), "templates-pack-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            (int exit, string output) = Dotnet(RepoRoot(), "pack", Path.Combine("src", "Caladesi.Templates", "Caladesi.Templates.csproj"), "-c", "Release", "-p:Version=2.3.4",
                "--artifacts-path", Path.Combine(work, "art"), "-o", Path.Combine(work, "out"), "--nologo", "-v:q");
            Assert.AreEqual(0, exit, output);
            using ZipArchive package = ZipFile.OpenRead(Path.Combine(work, "out", "Caladesi.Templates.2.3.4.nupkg"));
            List<ZipArchiveEntry> projects = [.. package.Entries.Where(e => e.FullName.EndsWith(".calaproj", StringComparison.Ordinal))];
            Assert.HasCount(3, projects);
            foreach (ZipArchiveEntry entry in projects)
            {
                using StreamReader reader = new(entry.Open());
                string text = reader.ReadToEnd();
                StringAssert.Contains(text, "Sdk=\"Caladesi.Sdk/2.3.4\"", entry.FullName);
                Assert.IsFalse(text.Contains("__CALADESI_VERSION__", StringComparison.Ordinal), entry.FullName);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
