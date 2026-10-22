using Caladesi.Cli;
using Caladesi.Compiler;
using Caladesi.Compiler.Diagnostics;
using Caladesi.Compiler.Emit;
using Caladesi.Compiler.Symbols.Metadata;
using Caladesi.Compiler.Syntax;
using Caladesi.Compiler.Text;

try
{
    return Run(args);
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine($"calac : error CALC9999: internal compiler error: {ex.GetType().Name}: {ex.Message}");
    return 2;
}

static int Run(string[] args)
{
    CliOptions options = CliOptions.Parse(args);
    if (options.ShowVersion)
    {
        Console.WriteLine($"calac {CompilerInfo.Version}");
        return 0;
    }
    foreach (string e in options.Errors)
    {
        Console.Error.WriteLine($"calac : error CALC0001: {e}");
    }

    if (options.Errors.Count > 0)
    {
        return 1;
    }

    if (options.Sources.Count == 0)
    {
        Console.Error.WriteLine("calac : error CALC0002: no source files");
        Console.Error.WriteLine("usage: calac [-out:file] [-target:exe|library] [-ref:assembly]... [@response-file] source.cala...");
        return 1;
    }
    if (options.Output == null)
    {
        Console.Error.WriteLine("calac : error CALC0003: -out is required");
        return 1;
    }

    Dictionary<string, SourceText> texts = [];
    List<SyntaxTree> trees = [];
    foreach (string source in options.Sources)
    {
        string text;
        try { text = File.ReadAllText(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{source} : error CALC0004: cannot read file: {ex.Message}");
            return 1;
        }
        SyntaxTree tree = SyntaxTree.ParseText(text, Path.GetFullPath(source));
        texts[tree.FilePath] = tree.Text;
        trees.Add(tree);
    }

    if (options.GenerateTestEntryPoint)
    {
        SyntaxTree entry = TestEntryPoint.Create(options.ToCompilationOptions(), options.TestHooks);
        texts[entry.FilePath] = entry.Text;
        trees.Add(entry);
    }

    DiagnosticBag refDiags = new();
    ReferenceSet references = Caladesi.Compiler.Symbols.Metadata.ReferenceSet.Create(options.References, refDiags);
    Compilation compilation = Compilation.Create(options.ToCompilationOptions(), trees, references);

    using MemoryStream pe = new();
    using MemoryStream pdb = new();
    string? pdbName = options.PdbOutput != null ? Path.GetFileName(options.PdbOutput) : null;
    EmitResult result = compilation.Emit(pe, options.PdbOutput != null ? pdb : null, pdbName);

    foreach (Diagnostic? d in refDiags.Items.Concat(result.Diagnostics).Distinct())
    {
        if (d.EffectiveSeverity == DiagnosticSeverity.Hidden)
        {
            continue;
        }

        _ = texts.TryGetValue(d.FilePath ?? "", out SourceText? text);
        string line = d.Format(text);
        if (d.EffectiveSeverity == DiagnosticSeverity.Error)
        {
            Console.Error.WriteLine(line);
        }
        else
        {
            Console.WriteLine(line);
        }
    }
    if (!result.Success)
    {
        return 1;
    }

    try
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(options.Output));
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(options.Output, pe.ToArray());
        if (options.PdbOutput != null)
        {
            File.WriteAllBytes(options.PdbOutput, pdb.ToArray());
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"calac : error CALC0005: cannot write output: {ex.Message}");
        return 1;
    }
    return 0;
}
