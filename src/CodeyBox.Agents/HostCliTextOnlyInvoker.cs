using CodeyBox.Core;
using CodeyBox.HostProcess;

namespace CodeyBox.Agents;

/// <summary>
/// Runs subscription-CLI agents on the host for pure text-in/text-out calls
/// (pickup-time rebase resolver, advisory merge review). Materialises OAuth
/// files into an isolated temporary <c>HOME</c> so credentials from the
/// orchestrator bundle reach the CLI without bind-mounting host paths.
/// </summary>
internal static class HostCliTextOnlyInvoker
{
    private const int MaxOutputBytes = 8 * 1024 * 1024;

    public static async Task<TextOnlyAgentResult> RunAsync(
        IProcessRunner runner,
        IReadOnlyList<string> argv,
        string? stdin,
        AgentCredential? credential,
        HostCliTextOnlyAuthKind authKind,
        CancellationToken ct)
    {
        await using var scope = await HostCliTextOnlyAuthScope.CreateAsync(credential, authKind, ct)
            .ConfigureAwait(false);
        if (scope.UnavailabilityReason is not null)
            return new TextOnlyAgentResult(false, scope.UnavailabilityReason, null, scope.UnavailabilityReason);

        try
        {
            var result = await runner.RunAsync(
                argv,
                stdin,
                ct,
                maxStdoutBytes: MaxOutputBytes,
                maxStderrBytes: MaxOutputBytes,
                environment: scope.Environment).ConfigureAwait(false);

            if (result.StartFailed)
                return new TextOnlyAgentResult(false, $"{argv[0]} CLI not found on host PATH", null, null);

            if (result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
                return new TextOnlyAgentResult(
                    false,
                    $"{argv[0]} text-only call failed: exit {result.ExitCode}",
                    result.Stdout,
                    detail.Trim());
            }

            var output = string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout;
            return new TextOnlyAgentResult(true, "ok", output.Trim(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TextOnlyAgentResult(false, $"{argv[0]} text-only call failed", null, ex.Message);
        }
    }
}

internal enum HostCliTextOnlyAuthKind
{
    Cursor,
    Opencode,
}

/// <summary>
/// Temporary <c>HOME</c> with materialised subscription auth for one host CLI call.
/// </summary>
internal sealed class HostCliTextOnlyAuthScope : IAsyncDisposable
{
    public string? UnavailabilityReason { get; private init; }
    public IReadOnlyDictionary<string, string> Environment { get; private init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private string? _tempHome;

    public static async Task<HostCliTextOnlyAuthScope> CreateAsync(
        AgentCredential? credential,
        HostCliTextOnlyAuthKind authKind,
        CancellationToken ct)
    {
        _ = ct;
        var reason = GetUnavailabilityReason(credential, authKind);
        if (reason is not null)
            return new HostCliTextOnlyAuthScope { UnavailabilityReason = reason };

        var tempHome = Path.Combine(
            Path.GetTempPath(),
            "codeybox-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);

        var env = new Dictionary<string, string>(MinimalHostProcessEnvironment.ForCliAuthDiscovery(), StringComparer.Ordinal)
        {
            ["HOME"] = tempHome,
        };

        switch (authKind)
        {
            case HostCliTextOnlyAuthKind.Cursor:
                credential!.EnvironmentVariables.TryGetValue("CODEYBOX_CURSOR_AUTH_JSON", out var cursorJson);
                await WriteAuthFileAsync(
                    Path.Combine(tempHome, ".config", "cursor", "auth.json"),
                    cursorJson!,
                    ct).ConfigureAwait(false);
                break;
            case HostCliTextOnlyAuthKind.Opencode:
                credential!.EnvironmentVariables.TryGetValue("OPENCODE_AUTH_JSON", out var opencodeJson);
                var dest = credential.EnvironmentVariables.TryGetValue("OPENCODE_AUTH_DEST_PATH", out var destPath)
                    && !string.IsNullOrWhiteSpace(destPath)
                    ? destPath
                    : Path.Combine(tempHome, ".local", "share", "opencode", "auth.json");
                if (!Path.IsPathFullyQualified(dest))
                    dest = Path.Combine(tempHome, dest.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                await WriteAuthFileAsync(dest, opencodeJson!, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(authKind), authKind, null);
        }

        return new HostCliTextOnlyAuthScope
        {
            _tempHome = tempHome,
            Environment = env,
        };
    }

    public static string? GetUnavailabilityReason(AgentCredential? credential, HostCliTextOnlyAuthKind authKind)
    {
        if (credential is null)
            return authKind switch
            {
                HostCliTextOnlyAuthKind.Cursor => "CODEYBOX_CURSOR_AUTH_JSON is required",
                HostCliTextOnlyAuthKind.Opencode => "OPENCODE_AUTH_JSON is required",
                _ => "credential is required",
            };

        return authKind switch
        {
            HostCliTextOnlyAuthKind.Cursor =>
                credential.EnvironmentVariables.TryGetValue("CODEYBOX_CURSOR_AUTH_JSON", out var cursor)
                && !string.IsNullOrEmpty(cursor)
                    ? null
                    : "CODEYBOX_CURSOR_AUTH_JSON is required",
            HostCliTextOnlyAuthKind.Opencode =>
                credential.EnvironmentVariables.TryGetValue("OPENCODE_AUTH_JSON", out var opencode)
                && !string.IsNullOrEmpty(opencode)
                    ? null
                    : "OPENCODE_AUTH_JSON is required",
            _ => "credential is required",
        };
    }

    private static async Task WriteAuthFileAsync(string path, string contents, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, contents, ct).ConfigureAwait(false);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_tempHome is not null)
        {
            try { Directory.Delete(_tempHome, recursive: true); } catch { }
            _tempHome = null;
        }

        return ValueTask.CompletedTask;
    }
}
