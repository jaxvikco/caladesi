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
        return program;
    }

    private static BoundBlock LowerMethod(Compilation compilation, LoweredProgram program, NamedTypeSymbol type, SourceMethodSymbol method, int ordinal, BoundBlock body)
    {
        body = ClosureLowerer.Lower(compilation, program, method, type, ordinal, body);
        if (method.IsAsync || method.IsIterator) StateMachineLowerer.Lower(compilation, program, method, type, ordinal, body);
        return program.Bodies.TryGetValue(method, out var stub) && method.IsAsync | method.IsIterator ? stub : body;
    }
}
