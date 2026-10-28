using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Tests.Emit;

namespace Caladesi.Compiler.Tests.SelfHostRuntime;

[TestClass]
public sealed class NamedUnionTests
{
    [TestMethod]
    public void NamedUnions_TypedEqualityOperatorsAndHashRemainConsistent()
    {
        (string output, int exitCode) = EmitHarness.Run("""
            import { HashCode } from "System";
            type Choice = int | string;
            function check(first: Choice, same: Choice, other: Choice): void {
                Console.WriteLine(first.Equals(same));
                Console.WriteLine(first == same);
                Console.WriteLine(first != other);
                Console.WriteLine(first.Equals(other));
                Console.WriteLine(first.GetHashCode() == HashCode.Combine<int, object>(1, 42));
            }
            check(42, 42, "42");
            """);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("True\nTrue\nTrue\nFalse\nTrue\n", output);
    }

    [TestMethod]
    public void GenericDeclarations_DifferentAritiesCoexistButExactDuplicatesAreRejected()
    {
        (string output, int exitCode) = EmitHarness.Run("""
            type Choice<T1, T2> = T1 | T2;
            type Choice<T1, T2, T3> = T1 | T2 | T3;
            function tags(pair: Choice<int, string>, triple: Choice<int, string, bool>): void {
                Console.WriteLine(pair.Tag);
                Console.WriteLine(triple.Tag);
            }
            tags(42, true);
            """);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("1\n3\n", output);
        Compilation compilation = TestCompiler.Create("""
            type Choice<T1, T2> = T1 | T2;
            type Choice<T1, T2> = T1 | T2;
            """);
        Assert.IsTrue(compilation.GetDiagnostics().Any(d => d.Id == "CAL0156"));
    }

    [TestMethod]
    public void WellKnownTypes_SourceMetadataNamesTakePrecedenceOverReferences()
    {
        Compilation compilation = Compilation.Create(
            new CompilationOptions { RootNamespace = "Caladesi.Runtime", ProjectDirectory = "/proj" },
            [SyntaxTree.ParseText("""
                import { Attribute } from "System";
                export sealed class CaladesiUnionAttribute extends Attribute {}
                export type Union<T1, T2> = T1 | T2;
                export type Union<T1, T2, T3> = T1 | T2 | T3;
                """, "/proj/runtime.cala")],
            TestCompiler.References);
        Assert.IsTrue(compilation.GetWellKnownType("Caladesi.Runtime.CaladesiUnionAttribute")!.IsFromSource);
        Assert.IsTrue(compilation.GetWellKnownType("Caladesi.Runtime.Union`2")!.IsFromSource);
        Assert.AreEqual(2, compilation.GetWellKnownType("Caladesi.Runtime.Union`2")!.Arity);
        Assert.AreEqual(3, compilation.GetWellKnownType("Caladesi.Runtime.Union`3")!.Arity);
        Assert.IsNull(compilation.GetWellKnownType("Caladesi.Runtime.Missing"));
        Assert.IsFalse(compilation.GetWellKnownType("System.IEquatable`1")!.IsFromSource);
    }

    [TestMethod]
    public void WellKnownTypes_DeclarationLookupPreservesDeferredCrossFileConstraints()
    {
        Compilation compilation = Compilation.Create(
            new CompilationOptions { RootNamespace = "Test", ProjectDirectory = "/proj" },
            [
                SyntaxTree.ParseText("""
                    import { Marker } from "./marker";
                    export class Box<T extends Marker> {}
                    export class Box {}
                    """, "/proj/box.cala"),
                SyntaxTree.ParseText("export class Marker {}", "/proj/marker.cala"),
            ],
            TestCompiler.References);
        NamedTypeSymbol generic = compilation.GetWellKnownType("Test.Box`1")!;
        NamedTypeSymbol plain = compilation.GetWellKnownType("Test.Box")!;
        Assert.AreEqual(1, generic.Arity);
        Assert.AreEqual(0, plain.Arity);
        Assert.AreEqual("Marker", generic.TypeParameters[0].ConstraintTypes.Single().Name);
        Assert.IsTrue(compilation.GetWellKnownType("Test.Marker")!.IsFromSource);
    }
}
