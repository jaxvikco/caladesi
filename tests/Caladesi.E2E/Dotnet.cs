using System.Diagnostics;
using System.Text;

namespace Caladesi.E2E;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, string Output, bool TimedOut);

internal static class Dotnet
{
    public static Task<ProcessResult> RunAsync(IEnumerable<string> args, string workingDirectory, IReadOnlyDictionary<string, string?> env, TimeSpan timeout)
    {
        return RunProcessAsync("dotnet", args, workingDirectory, env, timeout);
    }

    public static async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> args, string workingDirectory, IReadOnlyDictionary<string, string?> env, TimeSpan timeout)
    {
        ProcessStartInfo psi = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // Do not leak the outer MSBuild / dotnet host state into the child build.
        foreach (string key in psi.Environment.Keys.ToList())
        {
            if (key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase) || key is "DOTNET_HOST_PATH" or "DOTNET_ROOT_X64_FALLBACK")
            {
                _ = psi.Environment.Remove(key);
            }
        }
        foreach (KeyValuePair<string, string?> entry in env)
        {
            if (entry.Value == null)
            {
                _ = psi.Environment.Remove(entry.Key);
            }
            else
            {
                psi.Environment[entry.Key] = entry.Value;
            }
        }

        StringBuilder stdout = new();
        StringBuilder stderr = new();
        StringBuilder combined = new();
        object gate = new();
        using Process process = new() { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            lock (gate) { _ = stdout.AppendLine(e.Data); _ = combined.AppendLine(e.Data); }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            lock (gate) { _ = stderr.AppendLine(e.Data); _ = combined.AppendLine(e.Data); }
        };

        _ = process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource cts = new(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        lock (gate)
        {
            return new(timedOut ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), combined.ToString(), timedOut);
        }
    }
}
