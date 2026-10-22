using Caladesi.LanguageServer;

// Stdout carries the protocol; anything else that writes to Console.Out must not corrupt it.
var protocolOutput = Console.OpenStandardOutput();
var protocolInput = Console.OpenStandardInput();
Console.SetOut(Console.Error);

if (args.Contains("--version"))
{
    Console.Error.WriteLine("cala-ls " + Caladesi.Compiler.CompilerInfo.Version);
    return 0;
}

var options = new ServerOptions();
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--runtime-path") options.RuntimePath = args[i + 1];
    else if (args[i] == "--dotnet-path") options.DotnetPath = args[i + 1];
}

return new LanguageServer(protocolInput, protocolOutput, options).Run();
