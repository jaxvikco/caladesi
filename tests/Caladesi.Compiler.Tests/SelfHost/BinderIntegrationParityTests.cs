extern alias SelfHost;

using System.Reflection;
using System.Runtime.Loader;
using Caladesi.Compiler.Symbols.Source;
using Native = SelfHost::Caladesi.Compiler;
using NativeDiagnostics = SelfHost::Caladesi.Compiler.Diagnostics;
using NativeMetadata = SelfHost::Caladesi.Compiler.Symbols.Metadata;
using NativeSyntax = SelfHost::Caladesi.Compiler.Syntax;

namespace Caladesi.Compiler.Tests.SelfHost;

[TestClass]
public sealed class BinderIntegrationParityTests
{
    private static readonly Lazy<NativeMetadata.ReferenceSet> NativeReferences =
        new(() => NativeMetadata.ReferenceSet.Create(TestReferences.Paths));

    [TestMethod]
    [DataRow("export function evaluate(): int { let x = 3; { let x = 7; x += 2; } return x; }", 3)]
    [DataRow("export function id<T>(x: T): T { return x; } export function evaluate(): int { return id(6); }", 6)]
    [DataRow("export function evaluate(): int { let x: long = 4; x *= 3; return x as int; }", 12)]
    [DataRow("export function evaluate(): int { let x = 1; return (x++ * 10) + ++x; }", 13)]
    [DataRow("export function evaluate(): int { const values = [2, 3, 5]; return values[0] + values[2]; }", 7)]
    [DataRow("export function evaluate(): int { const [a, b] = [2, 5]; return a * b; }", 10)]
    [DataRow("export function evaluate(): int { let sum = 0; for (const x of [2, 3, 5]) sum += x; return sum; }", 10)]
    [DataRow("export function evaluate(): int { let sum = 0; for (let i = 0; i < 5; i++) { if (i == 1) continue; sum += i; } return sum; }", 9)]
    [DataRow("export function evaluate(): int { let n = 3; do { n--; } while (n > 0); while (n < 4) n++; return n; }", 4)]
    [DataRow("export function evaluate(): int { let x = 2; switch (x) { case 1: return 4; case 2: return 8; default: return 0; } }", 8)]
    [DataRow("export function evaluate(): int { let x: int? = null; return x ?? 5; }", 5)]
    [DataRow("export function evaluate(): int { let x: int? = 7; if (x != null) return x; return 0; }", 7)]
    [DataRow("export function evaluate(): int { const f: (x: int) => int = x => x * 3; return f(4); }", 12)]
    [DataRow("export function evaluate(): int { let offset = 4; function add(x: int): int { return x + offset; } return add(3); }", 7)]
    [DataRow("export class Box { value: int; constructor(x: int) { this.value = x; } get(): int { return this.value; } } export function evaluate(): int { return new Box(9).get(); }", 9)]
    [DataRow("type Pair = { first: int; second: int; }; export function evaluate(): int { const pair: Pair = { first: 3, second: 4 }; return pair.first + pair.second; }", 7)]
    [DataRow("export function evaluate(): int { let x = 1; try { x = 6; } finally { x += 2; } return x; }", 8)]
    [DataRow("export function evaluate(): int { try { throw new Exception(); } catch (error) { return 11; } }", 11)]
    [DataRow("let count = 0; function next(): bool { count++; return true; } export function evaluate(): int { const same = (next() ? null : null) == null; return same ? count : -1; }", 1)]
    public void ActualNativeBinderMatchesOracleAndEmitsExecutableCode(string source, int expected)
    {
        var oracle = CreateOracle(source);
        var native = CreateNative(source);
        AssertNoErrors(oracle);
        AssertNoErrors(native);
        using MemoryStream oracleImage = new();
        using MemoryStream nativeImage = new();
        Assert.IsTrue(oracle.Emit(oracleImage).Success);
        Assert.IsTrue(native.Emit(nativeImage).Success);
        Assert.AreEqual(expected, Execute(oracleImage));
        Assert.AreEqual(expected, Execute(nativeImage));
    }

    [TestMethod]
    [DataRow("export function bad(): int { return missing; }")]
    [DataRow("export function bad(): int { const x = 1; x = 2; return x; }")]
    [DataRow("export function bad(): int { let x = 1; let x = 2; return x; }")]
    [DataRow("export function bad(x: int, x: int): int { return x; }")]
    [DataRow("export function bad(): int { return 'text'; }")]
    [DataRow("export function bad(): void { break; }")]
    [DataRow("export function bad(): void { continue; }")]
    [DataRow("export function bad(): void { throw; }")]
    [DataRow("export function bad(): void { const x = null; }")]
    [DataRow("export function bad(value: Missing): void {}")]
    [DataRow("export function bad(value: void): void {}")]
    [DataRow("export function bad(): void { const x: int = 1; x.missing(); }")]
    public void BinderDiagnosticsMatchOracleWhileRecovering(string source)
    {
        var oracle = CreateOracle(source).GetDiagnostics().Where(d =>
            d.EffectiveSeverity == Diagnostics.DiagnosticSeverity.Error).Select(d => d.Id).Order().ToArray();
        var native = CreateNative(source).GetDiagnostics().Where(d =>
            d.EffectiveSeverity == NativeDiagnostics.DiagnosticSeverity.Error).Select(d => d.Id).Order().ToArray();
        CollectionAssert.AreEqual(oracle, native);
        Assert.IsNotEmpty(native);
    }

    [TestMethod]
    [DataRow("receiver()[index()] += 3", 127)]
    [DataRow("++receiver()[index()]", 125)]
    [DataRow("receiver()[index()]++", 125)]
    public void CompoundIndexerAssignmentsEvaluateReceiverBeforeIndexExactlyOnce(string operation, int expected)
    {
        var source = """
            import { List } from 'System.Collections.Generic';
            let order = 0;
            const values = new List<int>();
            function receiver(): List<int> { order = order * 10 + 1; return values; }
            function index(): int { order = order * 10 + 2; return 0; }
            export function evaluate(): int {
              values.Add(4);
            """ + operation + "; return order * 10 + values[0]; }";
        var oracle = CreateOracle(source);
        var native = CreateNative(source);
        AssertNoErrors(oracle);
        AssertNoErrors(native);
        using MemoryStream expectedImage = new();
        using MemoryStream actualImage = new();
        Assert.IsTrue(oracle.Emit(expectedImage).Success);
        Assert.IsTrue(native.Emit(actualImage).Success);
        Assert.AreEqual(expected, Execute(expectedImage));
        Assert.AreEqual(expected, Execute(actualImage));
    }

    [TestMethod]
    public void GenericConstraintsOptionalParametersAndInferredReturnsRetainSymbolIdentity()
    {
        const string source = """
            export function identity<T extends class & new()>(value: T): T { return value; }
            export function defaulted(value: int = 4): int { return value; }
            export function inferred() { return defaulted(); }
            """;
        var oracle = CreateOracle(source);
        var native = CreateNative(source);
        AssertNoErrors(oracle);
        AssertNoErrors(native);
        SourceMethodSymbol[] expected = [.. oracle.GetFile(oracle.SyntaxTrees[0]).ModuleClass!.GetMembers()
            .OfType<SourceMethodSymbol>().Where(m => m.Name is "identity" or "defaulted" or "inferred")];
        var actual = native.GetFile(native.SyntaxTrees[0]).ModuleClass!.GetMembers()
            .OfType<Native.Symbols.Source.SourceMethodSymbol<Native.SourceFile>>()
            .Where(m => m.Name is "identity" or "defaulted" or "inferred").ToArray();
        CollectionAssert.AreEqual(expected.Select(m => (m.Name, m.ReturnType.ToDisplayString(),
            Parameters: string.Join(",", m.Parameters.Select(p => $"{p.Type}:{p.IsOptional}")))).ToArray(),
            actual.Select(m => (m.Name, m.ReturnType.ToDisplayString(),
            Parameters: string.Join(",", m.Parameters.Select(p => $"{p.Type}:{p.IsOptional}")))).ToArray());
        var identity = actual.Single(m => m.Name == "identity");
        Assert.IsTrue(identity.TypeParameters[0].HasReferenceTypeConstraint);
        Assert.IsTrue(identity.TypeParameters[0].HasConstructorConstraint);
        Assert.AreSame(identity.TypeParameters[0], identity.Parameters[0].Type);
        Assert.AreSame(identity.TypeParameters[0], identity.ReturnType);
        Assert.IsTrue(actual.All(m => m.GetBoundBody() is not null));
    }

    [TestMethod]
    [DataRow("import { Task } from 'System.Threading.Tasks'; export async function run(): Task<int> { return await Task.FromResult(5); }")]
    [DataRow("import { IEnumerable } from 'System.Collections.Generic'; export function* run(): IEnumerable<int> { yield 1; yield* [2, 3]; }")]
    [DataRow("export function run(): string { const n = 3; return `value=${n}`; }")]
    [DataRow("export function run(): int { const [success, value] = int.TryParse('17'); return success ? value : 0; }")]
    [DataRow("export function run(): int { const value: [int, string] = [3, 'x']; return value[0]; }")]
    public void AwaitIteratorsTemplatesOutParametersAndTuplesProduceActualMethodBodies(string source)
    {
        var oracle = CreateOracle(source);
        var native = CreateNative(source);
        AssertNoErrors(oracle);
        AssertNoErrors(native);
        var expected = oracle.GetFile(oracle.SyntaxTrees[0]).ModuleClass!.GetMembers()
            .OfType<SourceMethodSymbol>().Single(m => m.Name == "run");
        var actual = native.GetFile(native.SyntaxTrees[0]).ModuleClass!.GetMembers()
            .OfType<Native.Symbols.Source.SourceMethodSymbol<Native.SourceFile>>()
            .Single(m => m.Name == "run");
        Assert.AreEqual(expected.ReturnType.ToDisplayString(), actual.ReturnType.ToDisplayString());
        Assert.IsNotNull(actual.GetBoundBody());
        Assert.AreEqual(expected.IsAsync, actual.IsAsync);
        Assert.AreEqual(expected.IsIterator, actual.IsIterator);
    }

    [TestMethod]
    public void DecoratorBindingRetainsConstructorArgumentsThroughNativeEmission()
    {
        const string source = "@Obsolete('native binder', false) export function evaluate(): int { return 3; }";
        var native = CreateNative(source);
        AssertNoErrors(native);
        using MemoryStream image = new();
        Assert.IsTrue(native.Emit(image).Success);
        image.Position = 0;
        AssemblyLoadContext context = new("BinderDecorator_" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var method = context.LoadFromStream(image).GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Single(m => m.Name == "evaluate");
            var attribute = method.GetCustomAttribute<ObsoleteAttribute>()!;
            Assert.IsNotNull(attribute);
            Assert.AreEqual("native binder", attribute.Message);
            Assert.IsFalse(attribute.IsError);
        }
        finally { context.Unload(); }
    }

    private static Compilation CreateOracle(string source)
    {
        return Compilation.Create(new CompilationOptions
        {
            AssemblyName = "BinderOracle_" + Guid.NewGuid().ToString("N"),
            OutputKind = OutputKind.DynamicallyLinkedLibrary,
            RecordSemanticInfo = true,
        }, [Compiler.Syntax.SyntaxTree.ParseText(source, "binder.cala")], TestReferences.Paths);
    }

    private static Native.Compilation CreateNative(string source)
    {
        return Native.Compilation.Create(new Native.CompilationOptions
        {
            AssemblyName = "BinderNative_" + Guid.NewGuid().ToString("N"),
            OutputKind = Native.OutputKind.DynamicallyLinkedLibrary,
            RecordSemanticInfo = true,
        }, [NativeSyntax.SyntaxTree.ParseText(source, "binder.cala")], NativeReferences.Value);
    }

    private static void AssertNoErrors(Compilation compilation)
    {
        Assert.IsFalse(compilation.HasErrors,
        string.Join(Environment.NewLine, compilation.GetDiagnostics().Select(d => d.ToString())));
    }

    private static void AssertNoErrors(Native.Compilation compilation)
    {
        Assert.IsFalse(compilation.HasErrors,
        string.Join(Environment.NewLine, compilation.GetDiagnostics().Select(d => d.ToString())));
    }

    private static int Execute(MemoryStream image)
    {
        image.Position = 0;
        AssemblyLoadContext context = new("BinderParity_" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(image);
            var method = assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Single(m => m.Name == "evaluate");
            return (int)method.Invoke(null, null)!;
        }
        finally { context.Unload(); }
    }
}
