using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.OnePasswordPlugin;

/// <summary>Bounded outcome of one <c>op</c> invocation. Stdout is capped before buffering.</summary>
public sealed record OnePasswordProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

/// <summary>
/// Runs the 1Password CLI without a shell. Implementations must use an
/// argv array (never a concatenated command string), bound the child
/// output before buffering it, and kill the child on timeout or
/// cancellation. The fake used in tests honours the same contract.
/// </summary>
public interface IOnePasswordProcessRunner
{
    Task<OnePasswordProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        CancellationToken ct = default);
}

/// <summary>
/// Production <see cref="IOnePasswordProcessRunner"/>: no shell
/// (<c>UseShellExecute=false</c>), no window, redirected streams, bounded
/// output, whole-tree kill on timeout or cancellation.
/// </summary>
public sealed class ProcessOnePasswordRunner : IOnePasswordProcessRunner
{
    /// <summary>Hard cap on child stdout/stderr bytes kept, enforced while reading.</summary>
    public const int MaxOutputBytes = 64 * 1024;

    public async Task<OnePasswordProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in environment)
        {
            if (!string.IsNullOrEmpty(value))
                startInfo.Environment[key] = value;
        }

        Process? child;
        try
        {
            child = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or UnauthorizedAccessException)
        {
            throw new OnePasswordException(
                OnePasswordFailureKind.Misconfigured,
                $"1Password CLI '{fileName}' could not start. Install it and set OpBinaryPath.", ex);
        }
        if (child is null)
            throw new OnePasswordException(
                OnePasswordFailureKind.Misconfigured,
                $"1Password CLI '{fileName}' could not start. Install it and set OpBinaryPath.");

        using (child)
        {
            // No stdin: close it so a chatty child can never block on input.
            try { child.StandardInput.Close(); } catch (IOException) { }
            var stdoutTask = ReadBoundedAsync(child.StandardOutput, ct);
            var stderrTask = ReadBoundedAsync(child.StandardError, ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await child.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout (not caller cancellation): kill the whole tree —
                // a lingering `op` holding the service-account token in its
                // environment is worse than a lost read.
                try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { await child.WaitForExitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                return new OnePasswordProcessResult(child.ExitCode, stdout, stderr, TimedOut: true);
            }
            var output = await stdoutTask.ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);
            return new OnePasswordProcessResult(child.ExitCode, output, error, TimedOut: false);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        // Cap BEFORE buffering: a runaway child can never fill host memory.
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int kept = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            var take = Math.Min(read, MaxOutputBytes - kept);
            if (take > 0)
            {
                builder.Append(buffer, 0, take);
                kept += take;
            }
            // Beyond the cap the bytes are dropped; the value path rejects
            // overlong output, the stderr path truncates.
        }
        return builder.ToString();
    }
}

/// <summary>
/// Service-account retrieval through the 1Password CLI
/// (<c>op read --no-newline "op://vault/item/field"</c>) with the
/// service-account token supplied only in the child's environment.
/// <para>The reference is built from validated components at this sink:
/// each of vault/item/field must be non-empty, free of control characters,
/// and free of <c>/</c> (which would escape its reference segment); the
/// argv array carries them to the child without shell quoting. The token
/// travels in the child environment, never in argv, logs, or exceptions;
/// only the reference names (never the value) reach logs and messages.
/// </para>
/// </summary>
public sealed class OnePasswordServiceAccountClient
{
    private readonly IOnePasswordProcessRunner _runner;
    private readonly ILogger _log;

    /// <summary>Maximum characters kept from CLI stderr in an exception.</summary>
    public const int MaxStderrChars = 200;

    public OnePasswordServiceAccountClient(IOnePasswordProcessRunner? runner = null, ILogger? log = null)
    {
        _runner = runner ?? new ProcessOnePasswordRunner();
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Reads one field value with <c>op read</c>. Returns the exact stdout
    /// (no trimming: <c>--no-newline</c> already removes the record
    /// separator); empty output is an <see cref="OnePasswordFailureKind.InvalidResponse"/>.
    /// </summary>
    public async Task<string> ReadFieldAsync(
        string opBinaryPath,
        string serviceAccountToken,
        string vault,
        string item,
        string field,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opBinaryPath);
        ArgumentNullException.ThrowIfNull(serviceAccountToken);
        var vaultComponent = ValidateReferenceComponent(vault, nameof(vault));
        var itemComponent = ValidateReferenceComponent(item, nameof(item));
        var fieldComponent = ValidateReferenceComponent(field, nameof(field));
        var reference = $"op://{vaultComponent}/{itemComponent}/{fieldComponent}";

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Documented service-account mechanism: the CLI reads the token
            // from this variable. Child-only — the parent process
            // environment is never mutated, and the token never appears in
            // argv, logs, or exceptions.
            ["OP_SERVICE_ACCOUNT_TOKEN"] = serviceAccountToken,
            // Non-interactive by construction: no device auth, no prompt.
            ["OP_FORMAT"] = "json",
        };
        OnePasswordProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                opBinaryPath,
                ["read", "--no-newline", reference],
                environment,
                timeout,
                ct).ConfigureAwait(false);
        }
        catch (OnePasswordException)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            throw new OnePasswordException(
                OnePasswordFailureKind.Unreachable, "1Password CLI read was cancelled.", ex);
        }

        if (result.TimedOut)
            throw new OnePasswordException(
                OnePasswordFailureKind.Unreachable,
                $"1Password CLI read of '{reference}' timed out.");
        if (result.ExitCode == 0)
        {
            if (result.StandardOutput.Length == 0)
                throw new OnePasswordException(
                    OnePasswordFailureKind.InvalidResponse,
                    $"1Password CLI read of '{reference}' returned an empty value.");
            if (result.StandardOutput.Length > ProcessOnePasswordRunner.MaxOutputBytes)
                throw new OnePasswordException(
                    OnePasswordFailureKind.InvalidResponse,
                    $"1Password CLI read of '{reference}' exceeded the output cap.");
            _log.LogDebug("1Password CLI read field '{Field}' from item '{Item}'.", fieldComponent, itemComponent);
            return result.StandardOutput;
        }

        var detail = Flatten(result.StandardError);
        throw ClassifyCliFailure(reference, result.ExitCode, detail);
    }

    private static OnePasswordException ClassifyCliFailure(string reference, int exitCode, string detail)
    {
        var lowered = detail.ToLowerInvariant();
        if (lowered.Contains("not found", StringComparison.Ordinal)
            || lowered.Contains("couldn't find", StringComparison.Ordinal)
            || lowered.Contains("isn't found", StringComparison.Ordinal)
            || lowered.Contains("no such", StringComparison.Ordinal))
            return new OnePasswordException(
                OnePasswordFailureKind.NotFound,
                $"1Password CLI read of '{reference}' failed: {detail}", exitCode);
        if (lowered.Contains("unauthorized", StringComparison.Ordinal)
            || lowered.Contains("invalid token", StringComparison.Ordinal)
            || lowered.Contains("authentication", StringComparison.Ordinal)
            || lowered.Contains("not currently signed in", StringComparison.Ordinal)
            || lowered.Contains("401", StringComparison.Ordinal)
            || lowered.Contains("403", StringComparison.Ordinal))
            return new OnePasswordException(
                OnePasswordFailureKind.Unauthorized,
                $"1Password CLI read of '{reference}' failed: {detail}", exitCode);
        return new OnePasswordException(
            OnePasswordFailureKind.BackendError,
            $"1Password CLI read of '{reference}' failed with exit {exitCode}: {detail}", exitCode);
    }

    private static string Flatten(string value)
    {
        var flat = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (flat.Length == 0)
            return "no CLI error output";
        return flat.Length <= MaxStderrChars ? flat : flat[..MaxStderrChars];
    }

    private static string ValidateReferenceComponent(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new OnePasswordException(
                OnePasswordFailureKind.Misconfigured,
                $"1Password op:// reference component '{parameterName}' must not be empty.");
        if (value.Length > OnePasswordOptions.MaxItemChars)
            throw new OnePasswordException(
                OnePasswordFailureKind.Misconfigured,
                $"1Password op:// reference component '{parameterName}' exceeds {OnePasswordOptions.MaxItemChars} characters.");
        if (value.Any(char.IsControl))
            throw new OnePasswordException(
                OnePasswordFailureKind.Misconfigured,
                $"1Password op:// reference component '{parameterName}' must not contain control characters.");
        if (value.Contains('/', StringComparison.Ordinal))
            throw new OnePasswordException(
                OnePasswordFailureKind.Misconfigured,
                $"1Password op:// reference component '{parameterName}' must not contain '/'.");
        return value;
    }
}
