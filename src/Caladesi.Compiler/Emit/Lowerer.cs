using System.Collections.Immutable;
using Caladesi.Compiler.Bound;
using Caladesi.Compiler.Symbols;
using Caladesi.Compiler.Symbols.Source;

namespace Caladesi.Compiler.Emit;

/// <summary>Runs the lowering passes (closures, iterators, async) over every method body.</summary>
internal static class Lowerer
{
    public static LoweredProgram Lower(Compilation compilation)
    {
        LoweredProgram program = new();
        foreach (SourceNamedType t in compilation.AllSourceTypes)
        {
            program.Types.Add(t);
        }

        foreach (SynthesizedAnonymousRecord a in compilation.AnonymousRecords)
        {
            program.Types.Add(a);
        }

        List<NamedTypeSymbol> originalTypes = [.. program.Types];
        int ordinalCounter = 10000;
        foreach (NamedTypeSymbol? type in originalTypes)
        {
            int ordinal = 0;
            foreach (SourceMethodSymbol m in type.GetMembers().OfType<SourceMethodSymbol>())
            {
                BoundBlock? body = m.GetBoundBody();
                if (body == null)
                {
                    continue;
                }

                if (type is SourceNamedType snt)
                {
                    program.MethodFiles[m] = snt.File;
                }

                BoundBlock lowered = LowerMethod(compilation, program, type, m, ordinal++, body);
                program.Bodies[m] = lowered;
            }
        }
        // lambdas created by closure conversion may themselves be async or iterators
        foreach (SynthesizedMethodSymbol? lm in program.Bodies.Keys.OfType<SynthesizedMethodSymbol>().Where(m => m.LambdaOrigin != null && (m.IsAsync || m.IsIterator)).ToList())
        {
            NamedTypeSymbol host = (NamedTypeSymbol)lm.ContainingSymbol!;
            StateMachineLowerer.Lower(compilation, program, lm, host, ordinalCounter++, program.Bodies[lm]);
        }
        foreach (SourceClassSymbol? type in program.Types.OfType<SourceClassSymbol>().ToList())
        {
            AddInterfaceBridges(program, type);
        }

        program.EntryPoint = new EntryPointChecker(compilation).FindEntryPoint();
        if (program.EntryPoint is { } entry && entry.ReturnType.StripNullable() is NamedTypeSymbol rt
            && rt.OriginalDefinition.SpecialType is SpecialType.Task or SpecialType.TaskT or SpecialType.ValueTask or SpecialType.ValueTaskT)
        {
            program.EntryPoint = CreateAsyncEntryBridge(compilation, program, entry, rt);
        }

        return program;
    }

    private static BoundBlock LowerMethod(Compilation compilation, LoweredProgram program, NamedTypeSymbol type, SourceMethodSymbol method, int ordinal, BoundBlock body)
    {
        body = ClosureLowerer.Lower(compilation, program, method, type, ordinal, body);
        if (method.IsAsync || method.IsIterator)
        {
            StateMachineLowerer.Lower(compilation, program, method, type, ordinal, body);
        }

        return program.Bodies.TryGetValue(method, out BoundBlock? stub) && method.IsAsync | method.IsIterator ? stub : body;
    }

    /// <summary>The CLR cannot start an async entry point: <c>static void Main(args) =&gt; entry(args).GetAwaiter().GetResult()</c>.</summary>
    private static SynthesizedMethodSymbol CreateAsyncEntryBridge(Compilation c, LoweredProgram program, MethodSymbol entry, NamedTypeSymbol taskType)
    {
        NamedTypeSymbol container = (NamedTypeSymbol)entry.ContainingSymbol!;
        bool returnsValue = taskType.OriginalDefinition.SpecialType is SpecialType.TaskT or SpecialType.ValueTaskT;
        TypeSymbol resultType = returnsValue ? taskType.TypeArguments[0] : c.GetSpecialType(SpecialType.Void);
        ImmutableArray<ParameterSymbol> ps = [.. entry.Parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal, null))];
        SynthesizedMethodSymbol bridge = new(container, "<Main>", MethodKind.Ordinary, Accessibility.Private, true, resultType, ps)
        { SynthesizedKind = SynthesizedBodyKind.Custom, SourceMethod = entry };
        BoundCall call = new(null, entry, [.. ps.Select(p => (BoundExpression)new BoundParameter(p))]);
        MethodSymbol awaiter = taskType.GetMembers("GetAwaiter").OfType<MethodSymbol>().First(m => m.Parameters.Length == 0);
        BoundCall getAwaiter = new(call, awaiter, []);
        MethodSymbol getResult = ((NamedTypeSymbol)awaiter.ReturnType).GetMembers("GetResult").OfType<MethodSymbol>().First();
        BoundCall result = new(getAwaiter, getResult, []);
        BoundStatement last = returnsValue ? new BoundReturn(result) : new BoundExpressionStatement(result);
        program.Bodies[bridge] = new BoundBlock([], [last]);
        if (!program.ExtraMembers.TryGetValue(container, out List<Symbol>? list))
        {
            program.ExtraMembers[container] = list = [];
        }

        list.Add(bridge);
        return bridge;
    }

    /// <summary>Explicit interface implementations that forward to a compatible public member (see <see cref="DeclarationChecker.FindBridgeTarget"/>).</summary>
    private static void AddInterfaceBridges(LoweredProgram program, SourceClassSymbol type)
    {
        foreach (NamedTypeSymbol iface in type.AllInterfaces)
        {
            foreach (MethodSymbol im in iface.GetMembers().OfType<MethodSymbol>())
            {
                if (im.MethodKind is not (MethodKind.Ordinary or MethodKind.PropertyGet) || im.IsStatic || !im.IsAbstract)
                {
                    continue;
                }

                if (DeclarationChecker.FindImplementation(type, im, requireOverride: false) != null)
                {
                    continue;
                }

                if (DeclarationChecker.FindBridgeTarget(type, im) is not { } target)
                {
                    continue;
                }

                ImmutableArray<ParameterSymbol> ps = [.. im.Parameters.Select((p, i) => new ParameterSymbol(p.Name, p.Type, i, null))];
                SynthesizedMethodSymbol bridge = new(type, iface.ToDisplayString() + "." + im.Name, MethodKind.Ordinary, Accessibility.Private, false, im.ReturnType, ps)
                { SynthesizedKind = SynthesizedBodyKind.Custom, Overrides = [im] };
                BoundExpression call = new BoundCall(new BoundThis(type.IsGenericType ? type.Construct(type.TypeArguments) : type), target,
                    [.. ps.Select(p => (BoundExpression)new BoundParameter(p))]);
                if (!target.ReturnType.Equals(im.ReturnType))
                {
                    call = new BoundConversion(call, ConversionKind.ImplicitReference, im.ReturnType);
                }

                BoundStatement body = im.ReturnType.IsVoid ? new BoundExpressionStatement(call) : new BoundReturn(call);
                program.Bodies[bridge] = new BoundBlock([], [body]);
                if (!program.ExtraMembers.TryGetValue(type, out List<Symbol>? list))
                {
                    program.ExtraMembers[type] = list = [];
                }

                list.Add(bridge);
            }
        }
    }
}
