using System.Diagnostics;
using System.Text;

namespace CodeyBox.Agents.Opencode;

/// <summary>
/// Host process runner for <c>opencode models</c>. Uses <see cref="Process"/>
/// directly — the Multipass <c>IProcessRunner</c> is internal to that assembly.
/// </summary>
internal sealed class DefaultOpencodeCliRunner : IOpencodeCliRunner
{
    private const int MaxOutputChars = 512 * 1024;

    public async Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("models");

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new OpencodeCliRunResult(1, "", "failed to start process");
        }
        catch (FileNotFoundException)
        {
            throw;
        }

        var stdoutTask = ReadAllLimitedAsync(process.StandardOutput, ct);
        var stderrTask = ReadAllLimitedAsync(process.StandardError, ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new OpencodeCliRunResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadAllLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        while (sb.Length < MaxOutputChars)
        {
            ct.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            var remaining = MaxOutputChars - sb.Length;
            sb.Append(buffer, 0, Math.Min(read, remaining));
        }
        return sb.ToString();
    }
}
