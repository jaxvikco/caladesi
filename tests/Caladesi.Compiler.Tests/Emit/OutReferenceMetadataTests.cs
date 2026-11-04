namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public sealed class OutReferenceMetadataTests
{
    private static void Check(string source, string expected)
    {
        (string output, int exitCode) = EmitHarness.Run(source);
        Assert.AreEqual(expected, output);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void OmittedOuts_PreserveReferenceAndValueTypeResults()
    {
        Check("""
            const [parsed, version] = Version.TryParse("2.3.4.5");
            Console.WriteLine(parsed);
            Console.WriteLine(version?.ToString() ?? "<null>");
            const [invalid, missing] = Version.TryParse("invalid");
            Console.WriteLine(invalid);
            Console.WriteLine(missing == null);
            const [numberParsed, number] = int.TryParse("42");
            Console.WriteLine(numberParsed);
            Console.WriteLine(number);
            const [numberInvalid, zero] = int.TryParse("invalid");
            Console.WriteLine(numberInvalid);
            Console.WriteLine(zero);
            """, "True\n2.3.4.5\nFalse\nTrue\nTrue\n42\nFalse\n0\n");
    }

    [TestMethod]
    public void ConstructedGenericReferenceOuts_PreservePresentNullAndMissingValues()
    {
        Check("""
            const values = new Dictionary<string, string?>();
            values["present"] = "value";
            values["null"] = null;
            for (const key of ["present", "null", "missing"]) {
              const [found, value] = values.TryGetValue(key);
              Console.WriteLine(found);
              Console.WriteLine(value ?? "<null>");
            }
            const arrays = new Dictionary<string, string[]>();
            arrays["present"] = ["first", "second"];
            const [foundArray, array] = arrays.TryGetValue("present");
            Console.WriteLine(foundArray);
            Console.WriteLine(array![1]);
            """, "True\nvalue\nTrue\n<null>\nFalse\n<null>\nTrue\nsecond\n");
    }

    [TestMethod]
    public void ConstructedGenericNullableValueOuts_KeepValueTypeAddressing()
    {
        Check("""
            const values = new Dictionary<string, int?>();
            values["present"] = 42;
            values["null"] = null;
            for (const key of ["present", "null", "missing"]) {
              const [found, value] = values.TryGetValue(key);
              Console.WriteLine(found);
              Console.WriteLine(value ?? -1);
            }
            """, "True\n42\nTrue\n-1\nFalse\n-1\n");
    }

    [TestMethod]
    public void OpenGenericOuts_PreserveReferenceAndValueInstantiations()
    {
        Check("""
            function show<T>(values: Dictionary<string, T>): void {
              const [found, value] = values.TryGetValue("present");
              Console.WriteLine(found);
              Console.WriteLine(value);
            }
            const strings = new Dictionary<string, string>();
            strings["present"] = "generic";
            show(strings);
            const numbers = new Dictionary<string, int>();
            numbers["present"] = 17;
            show(numbers);
            """, "True\ngeneric\nTrue\n17\n");
    }

    [TestMethod]
    public void CapturedOutResults_AndCallsInsideClosuresKeepTheirValues()
    {
        Check("""
            const [parsed, version] = Version.TryParse("3.4");
            const readVersion: Func<string> = () => version?.ToString() ?? "<null>";
            Console.WriteLine(readVersion());
            function makeReader(): Func<string> {
              const values = new Dictionary<string, string>();
              values["present"] = "captured";
              return () => {
                const [found, value] = values.TryGetValue("present");
                return value ?? "<null>";
              };
            }
            Console.WriteLine(makeReader()());
            """, "3.4\ncaptured\n");
    }

    [TestMethod]
    public void AsyncOutResults_SurviveSuspension()
    {
        Check("""
            async function read(): Task<string> {
              const values = new Dictionary<string, string>();
              values["present"] = "async";
              await Task.Yield();
              const [found, value] = values.TryGetValue("present");
              await Task.Yield();
              const [parsed, version] = Version.TryParse("5.6");
              await Task.Yield();
              return (value ?? "<null>") + ":" + (version?.ToString() ?? "<null>");
            }
            Console.WriteLine(read().Result);
            """, "async:5.6\n");
    }
}
