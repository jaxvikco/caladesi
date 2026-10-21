using System.Diagnostics;
using System.Text;

namespace Caladesi_E2E;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, string Output, bool TimedOut);

internal static class Dotnet
{
    public static Task<ProcessResult> RunAsync(IEnumerable<string> args, string workingDirectory, IReadOnlyDictionary<string, string?> env, TimeSpan timeout)
        => RunProcessAsync("dotnet", args, workingDirectory, env, timeout);

    public static async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> args, string workingDirectory, IReadOnlyDictionary<string, string?> env, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Do not leak the outer MSBuild / dotnet host state into the child build.
        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase) || key is "DOTNET_HOST_PATH" or "DOTNET_ROOT_X64_FALLBACK")
                psi.Environment.Remove(key);
        }
        foreach (var (k, v) in env)
        {
            if (v == null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var combined = new StringBuilder();
        var gate = new object();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (gate) { stdout.AppendLine(e.Data); combined.AppendLine(e.Data); }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (gate) { stderr.AppendLine(e.Data); combined.AppendLine(e.Data); }
        };

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
        }
        process.WaitForExit(); // flush the async output readers

        lock (gate)
        {
            return new ProcessResult(timedOut ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), combined.ToString(), timedOut);
        }
    }
}
