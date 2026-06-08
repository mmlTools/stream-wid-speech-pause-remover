using System.Diagnostics;
using System.Text;

namespace StreamWID.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string exe,
        string args,
        CancellationToken ct = default,
        Action<string>? onStdOutLine = null,
        Action<string>? onStdErrLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            stdout.AppendLine(e.Data);
            onStdOutLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            stderr.AppendLine(e.Data);
            onStdErrLine?.Invoke(e.Data);
        };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
