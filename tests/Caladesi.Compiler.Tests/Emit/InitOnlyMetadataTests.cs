namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public sealed class InitOnlyMetadataTests
{
    [TestMethod]
    public void ReferencedInitSettersKeepRequiredReturnModifier()
    {
        (string output, int exitCode) = EmitHarness.RunWithReferences("""
            import { CompilationOptions, OutputKind } from "Caladesi.Compiler";
            const options: CompilationOptions = {
                OutputKind: OutputKind.DynamicallyLinkedLibrary,
                AssemblyName: "self-host",
                NullableEnabled: false
            };
            Console.WriteLine(options.OutputKind);
            Console.WriteLine(options.AssemblyName);
            Console.WriteLine(options.NullableEnabled);
            """, typeof(CompilationOptions).Assembly.Location);

        Assert.AreEqual("DynamicallyLinkedLibrary\nself-host\nFalse\n", output);
        Assert.AreEqual(0, exitCode);
    }
}
