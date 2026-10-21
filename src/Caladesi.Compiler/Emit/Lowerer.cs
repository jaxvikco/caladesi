using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>Runs the lowering passes (closures, iterators, async) over every method body.</summary>
internal static class Lowerer
{
    public static LoweredProgram Lower(Compilation compilation)
    {
        var program = new LoweredProgram();
        foreach (var t in compilation.AllSourceTypes) program.Types.Add(t);
        foreach (var a in compilation.AnonymousRecords) program.Types.Add(a);

        var originalTypes = program.Types.ToList();
        var ordinalCounter = 10000;
        foreach (var type in originalTypes)
        {
            var ordinal = 0;
            foreach (var m in type.GetMembers().OfType<SourceMethodSymbol>())
            {
                var body = m.GetBoundBody();
                if (body == null) continue;
                if (type is SourceNamedType snt) program.MethodFiles[m] = snt.File;
                var lowered = LowerMethod(compilation, program, type, m, ordinal++, body);
                program.Bodies[m] = lowered;
            }
        }
        // lambdas created by closure conversion may themselves be async or iterators
        foreach (var lm in program.Bodies.Keys.OfType<SynthesizedMethodSymbol>().Where(m => m.LambdaOrigin != null && (m.IsAsync || m.IsIterator)).ToList())
        {
            var host = (NamedTypeSymbol)lm.ContainingSymbol!;
            StateMachineLowerer.Lower(compilation, program, lm, host, ordinalCounter++, program.Bodies[lm]);
        }
        program.EntryPoint = new EntryPointChecker(compilation).FindEntryPoint();
        if (program.EntryPoint is { } entry && entry.ReturnType.StripNullable() is NamedTypeSymbol rt
            && rt.OriginalDefinition.SpecialType is SpecialType.Task or SpecialType.TaskT or SpecialType.ValueTask or SpecialType.ValueTaskT)
            program.EntryPoint = CreateAsyncEntryBridge(compilation, program, entry, rt);
        return program;
    }

    private static BoundBlock LowerMethod(Compilation compilation, LoweredProgram program, NamedTypeSymbol type, SourceMethodSymbol method, int ordinal, BoundBlock body)
    {
        body = ClosureLowerer.Lower(compilation, program, method, type, ordinal, body);
        if (method.IsAsync || method.IsIterator) StateMachineLowerer.Lower(compilation, program, method, type, ordinal, body);
        return program.Bodies.TryGetValue(method, out var stub) && method.IsAsync | method.IsIterator ? stub : body;
    }

    /// <summary>The CLR cannot start an async entry point: <c>static void Main(args) =&gt; entry(args).GetAwaiter().GetResult()</c>.</summary>
    private static MethodSymbol CreateAsyncEntryBridge(Compilation c, LoweredProgram program, MethodSymbol entry, NamedTypeSymbol taskType)
    {
        var container = (NamedTypeSymbol)entry.ContainingSymbol!;
        var returnsValue = taskType.OriginalDefinition.SpecialType is SpecialType.TaskT or SpecialType.ValueTaskT;
        var resultType = returnsValue ? taskType.TypeArguments[0] : c.GetSpecialType(SpecialType.Void);
        var ps = entry.Parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal, null)).ToImmutableArray();
        var bridge = new SynthesizedMethodSymbol(container, "<Main>", MethodKind.Ordinary, Accessibility.Private, true, resultType, ps)
        { SynthesizedKind = SynthesizedBodyKind.Custom, SourceMethod = entry };
        var call = new BoundCall(null, entry, ps.Select(p => (BoundExpression)new BoundParameter(p)).ToImmutableArray());
        var awaiter = taskType.GetMembers("GetAwaiter").OfType<MethodSymbol>().First(m => m.Parameters.Length == 0);
        var getAwaiter = new BoundCall(call, awaiter, ImmutableArray<BoundExpression>.Empty);
        var getResult = ((NamedTypeSymbol)awaiter.ReturnType).GetMembers("GetResult").OfType<MethodSymbol>().First();
        var result = new BoundCall(getAwaiter, getResult, ImmutableArray<BoundExpression>.Empty);
        BoundStatement last = returnsValue ? new BoundReturn(result) : new BoundExpressionStatement(result);
        program.Bodies[bridge] = new BoundBlock(ImmutableArray<LocalSymbol>.Empty, [last]);
        if (!program.ExtraMembers.TryGetValue(container, out var list)) program.ExtraMembers[container] = list = new List<Symbol>();
        list.Add(bridge);
        return bridge;
    }
}
