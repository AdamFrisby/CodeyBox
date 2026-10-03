using System.Net;
using System.Text;
using CodeyBox.Core;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// Guest transport over SSH: resolves the VM's IP through <c>tart ip</c> and
/// runs every guest operation as a host <c>ssh</c> process (password auth via
/// <c>sshpass -e</c> with the password in the child environment, key auth via
/// <c>-i</c>). All remote commands are single argv elements built by
/// <see cref="TartShellCommand"/>; secrets travel stdin pipes or staged files,
/// never argv.
/// </summary>
public sealed class TartSshGuestTransport
{
    private readonly ITartProcessRunner _runner;
    private readonly Func<TartSandboxOptions> _readOptions;
    private readonly Func<string?> _readPassword;

    public TartSshGuestTransport(
        ITartProcessRunner runner,
        Func<TartSandboxOptions> readOptions,
        Func<string?> readPassword)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _readPassword = readPassword ?? throw new ArgumentNullException(nameof(readPassword));
    }

    /// <summary>Resolves the VM's IP via <c>tart ip</c>, validating its shape.</summary>
    public async Task<string> GetIpAsync(string vmName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmName);
        var opts = _readOptions();
        var result = await _runner.RunAsync(
            new TartProcessSpec(opts.TartBinaryPath, ["ip", vmName], Stdin: null, TimeSpan.FromSeconds(opts.CliTimeoutSeconds)),
            stdoutChunk: null, stderrChunk: null, maxOutputBytes: 4096, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw TartFailureClassification.ForExit(opts.TartBinaryPath, ["ip", vmName], result.ExitCode, result.Stderr);

        var ip = result.Stdout.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        if (!IPAddress.TryParse(ip, out _))
            throw new TartCliException(opts.TartBinaryPath, ["ip", vmName], result.ExitCode, "malformed-response", $"tart ip returned unparseable output for '{vmName}'.");
        return ip;
    }

    /// <summary>Polls until the guest answers SSH or the deadline elapses.</summary>
    public async Task<string> WaitForSshAsync(string vmName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmName);
        var opts = _readOptions();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds);
        var poll = TimeSpan.FromMilliseconds(opts.PollIntervalMilliseconds);
        string? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string ip;
            try
            {
                ip = await GetIpAsync(vmName, ct).ConfigureAwait(false);
            }
            catch (TartCliException ex)
            {
                lastError = ex.Detail;
                await DelayCancellable(poll, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var probe = await ExecRawAsync(ip, "echo codeybox-ready", stdin: null, TimeSpan.FromSeconds(opts.SshConnectTimeoutSeconds), stdoutChunk: null, stderrChunk: null, maxOutputBytes: 4096, ct).ConfigureAwait(false);
                if (probe.ExitCode == 0 && probe.Stdout.Contains("codeybox-ready", StringComparison.Ordinal))
                    return ip;
                // Fail fast on refused credentials: polling cannot fix a bad
                // password, and the 255 would otherwise burn the whole ready
                // timeout before surfacing as unauthorised.
                if (string.Equals(TartFailureClassification.ClassifyCore(probe.ExitCode, probe.Stderr), "unauthorised", StringComparison.Ordinal))
                    throw new TartCliException("ssh", ["probe", vmName], probe.ExitCode, "unauthorised", LastLine(probe.Stderr));
                lastError = LastLine(probe.Stderr);
            }
            catch (TartCliException ex) when (!string.Equals(ex.ErrorClass, "unauthorised", StringComparison.Ordinal))
            {
                // Transport wobble while the guest boots is retried; a
                // credential refusal is rethrown by the guard above and
                // never lands here.
                lastError = ex.Detail;
            }

            await DelayCancellable(poll, ct).ConfigureAwait(false);
        }

        throw new TartCliException(opts.TartBinaryPath, ["ssh", vmName], null, "timeout", $"guest '{vmName}' did not answer SSH: {lastError}");
    }

    /// <summary>
    /// Runs one remote command, returning the guest exit code with captured
    /// output. Non-zero guest exits are data, not exceptions; transport
    /// failures throw <see cref="TartCliException"/>.
    /// </summary>
    public Task<TartProcessResult> ExecRawAsync(
        string ip,
        string remoteCommand,
        string? stdin,
        TimeSpan timeout,
        Action<string>? stdoutChunk,
        Action<string>? stderrChunk,
        int maxOutputBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteCommand);
        if (!IPAddress.TryParse(ip.Trim(), out _))
            throw new ArgumentException("IP must parse before it reaches the SSH sink.", nameof(ip));

        var (executable, argv, environment) = BuildSshInvocation(ip, remoteCommand);
        return _runner.RunAsync(
            new TartProcessSpec(executable, argv, stdin, timeout, environment),
            stdoutChunk, stderrChunk, maxOutputBytes, ct);
    }

    /// <summary>Writes guest file content piped as base64 over SSH stdin.</summary>
    public Task WriteFileAsync(string ip, string guestPath, string content, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        return WriteFileBytesAsync(ip, guestPath, Encoding.UTF8.GetBytes(content), timeout, ct);
    }

    /// <summary>Writes raw guest file bytes piped as base64 over SSH stdin.</summary>
    public async Task WriteFileBytesAsync(string ip, string guestPath, byte[] content, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        ArgumentNullException.ThrowIfNull(content);
        var command = TartShellCommand.BuildWriteFileCommand(guestPath);
        var payload = Convert.ToBase64String(content) + "\n";
        var result = await ExecRawAsync(ip, command, payload, timeout, stdoutChunk: null, stderrChunk: null, maxOutputBytes: 65536, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw TartFailureClassification.ForExit("ssh", ["write-file", guestPath], result.ExitCode, result.Stderr);
    }

    /// <summary>Reads a guest file, decoded from base64, bounded by the caller.</summary>
    public async Task<byte[]> ReadFileBytesAsync(string ip, string guestPath, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        var command = TartShellCommand.BuildReadFileCommand(guestPath);
        var result = await ExecRawAsync(ip, command, stdin: null, timeout, stdoutChunk: null, stderrChunk: null, maxOutputBytes: checked((int)Math.Min(maxBytes * 2 + 1024, int.MaxValue)), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw TartFailureClassification.ForExit("ssh", ["read-file", guestPath], result.ExitCode, result.Stderr);

        var condensed = result.Stdout.ReplaceLineEndings(string.Empty);
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(condensed);
        }
        catch (FormatException ex)
        {
            throw new TartCliException("ssh", ["read-file", guestPath], result.ExitCode, "malformed-response", "guest returned non-base64 file content", ex);
        }
        if (decoded.LongLength > maxBytes)
            throw new TartCliException("ssh", ["read-file", guestPath], result.ExitCode, "limit-exceeded", $"guest file exceeds the {maxBytes}-byte bound");
        return decoded;
    }

    /// <summary>Reads a guest file as UTF-8 text, bounded by the caller.</summary>
    public async Task<string> ReadFileAsync(string ip, string guestPath, long maxBytes, TimeSpan timeout, CancellationToken ct) =>
        Encoding.UTF8.GetString(await ReadFileBytesAsync(ip, guestPath, maxBytes, timeout, ct).ConfigureAwait(false));

    /// <summary>Lists regular files under a guest directory (paths relative to it).</summary>
    public async Task<IReadOnlyList<string>> ListFilesAsync(string ip, string guestDirectory, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        var command = TartShellCommand.BuildListFilesCommand(guestDirectory);
        var result = await ExecRawAsync(ip, command, stdin: null, timeout, stdoutChunk: null, stderrChunk: null, maxOutputBytes: 1024 * 1024, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            return [];
        var prefix = guestDirectory.TrimEnd('/') + "/";
        var entries = new List<string>();
        foreach (var row in result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = row.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal) && trimmed.Length > prefix.Length)
                entries.Add(trimmed[prefix.Length..]);
        }
        return entries;
    }

    internal (string Executable, IReadOnlyList<string> Argv, IReadOnlyDictionary<string, string>? Environment) BuildSshInvocation(string ip, string remoteCommand)
    {
        var opts = _readOptions();
        var argv = new List<string>
        {
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=/dev/null",
            "-o", "ConnectTimeout=" + opts.SshConnectTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-p", opts.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (TartCredentialChain.UsesKeyAuth(opts))
        {
            argv.Add("-o");
            argv.Add("BatchMode=yes");
            argv.Add("-i");
            argv.Add(opts.SshPrivateKeyPath.Trim());
            argv.Add($"{opts.SshUsername.Trim()}@{ip.Trim()}");
            argv.Add(remoteCommand);
            return ("ssh", argv, null);
        }

        var password = _readPassword();
        if (string.IsNullOrEmpty(password))
        {
            // No password and no key: fail rather than hang on an auth
            // prompt — every RunAsync carries a timeout, but BatchMode keeps
            // the failure fast and explicit.
            argv.Add("-o");
            argv.Add("BatchMode=yes");
            argv.Add($"{opts.SshUsername.Trim()}@{ip.Trim()}");
            argv.Add(remoteCommand);
            return ("ssh", argv, null);
        }

        var wrapped = new List<string> { "-e", "ssh" };
        wrapped.AddRange(argv);
        wrapped.Add($"{opts.SshUsername.Trim()}@{ip.Trim()}");
        wrapped.Add(remoteCommand);
        return ("sshpass", wrapped, new Dictionary<string, string>(StringComparer.Ordinal) { ["SSHPASS"] = password });
    }

    private static string LastLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
    }

    private static Task DelayCancellable(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;
        return Task.Delay(delay, ct);
    }
}
