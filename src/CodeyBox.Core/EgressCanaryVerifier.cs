using System.Globalization;

namespace CodeyBox.Core;

/// <summary>
/// One canary probe outcome inside an <see cref="EgressCanaryResult"/>.
/// <see cref="Detail"/> is host-generated and bounded — never guest output.
/// </summary>
public sealed record EgressCanaryCheckResult(
    string Name,
    string Host,
    int Port,
    bool Passed,
    int ExitCode,
    TimeSpan Elapsed,
    string Detail);

/// <summary>
/// Host-owned verdict for one sandbox: every probe the verification ran,
/// plus the overall pass/fail and the reason when it failed.
/// </summary>
public sealed record EgressCanaryResult(
    string SandboxId,
    string ProviderKind,
    bool Passed,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<EgressCanaryCheckResult> Checks,
    string? FailureReason);

/// <summary>
/// Host-owned canary verification for provider-host packet filters (for
/// example Tart Softnet). Runs entirely through the normal
/// <see cref="ISandbox.ExecAsync"/> path — no plugin hook, no side channel:
/// the guest attempts TCP connects and the host judges the exit codes.
/// A check passes only on its exact expected outcome; anything else
/// (reachable-but-should-be-blocked, unreachable-but-should-be-allowed,
/// probe errors, exec failures) fails the whole canary closed.
/// </summary>
public sealed class EgressCanaryVerifier
{
    /// <summary>Allowed destination must connect.</summary>
    public const string CheckAllow = "allow";

    /// <summary>Configured canary destination must NOT connect.</summary>
    public const string CheckBlockCanary = "block-canary";

    /// <summary>Global IPv6 destination must NOT connect (filters are IPv4-only).</summary>
    public const string CheckBlockIpv6 = "block-ipv6";

    /// <summary>Provider-host LAN address must NOT connect.</summary>
    public const string CheckBlockLan = "block-lan";

    /// <summary>Guest interpreter for the probe script (PATH lookup, no shell).</summary>
    public const string ProbeBinary = "python3";

    /// <summary>Maximum characters of probe stderr kept in a check detail.</summary>
    public const int MaxDetailChars = 200;

    /// <summary>
    /// Fixed in-guest TCP probe. Host, port, timeout and expectation travel
    /// as <c>sys.argv</c> — never interpolated into this code — so a
    /// hostile config value cannot break out of the script. Exit codes are
    /// the whole verdict: 0 connected, 1 connection failed (blocked), 2
    /// probe misuse (fail closed). A missing address family stack reports
    /// blocked (1) for block checks — nothing can connect without a stack —
    /// but stays an error (2) for the allow check.
    /// </summary>
    public const string ProbeScript =
        "import socket, sys\n" +
        "def fail(m):\n" +
        "    sys.stderr.write(m[:200] + chr(10))\n" +
        "    sys.exit(2)\n" +
        "if len(sys.argv) != 6:\n" +
        "    fail('usage: probe <check> <allow|block> <host> <port> <timeout-seconds>')\n" +
        "mode, host, port_raw, timeout_raw = sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]\n" +
        "if mode not in ('allow', 'block'):\n" +
        "    fail('unknown mode')\n" +
        "try:\n" +
        "    port = int(port_raw)\n" +
        "except ValueError:\n" +
        "    fail('bad port')\n" +
        "if not 1 <= port <= 65535:\n" +
        "    fail('port out of range')\n" +
        "try:\n" +
        "    timeout = float(timeout_raw)\n" +
        "except ValueError:\n" +
        "    fail('bad timeout')\n" +
        "if not 0 < timeout <= 60:\n" +
        "    fail('timeout out of range')\n" +
        "if not host or len(host) > 253:\n" +
        "    fail('bad host')\n" +
        "family = socket.AF_INET6 if ':' in host else socket.AF_INET\n" +
        "try:\n" +
        "    sock = socket.socket(family, socket.SOCK_STREAM)\n" +
        "except OSError:\n" +
        "    sys.exit(1 if mode == 'block' else 2)\n" +
        "sock.settimeout(timeout)\n" +
        "try:\n" +
        "    sock.connect((host, port))\n" +
        "except OSError:\n" +
        "    sys.exit(1)\n" +
        "else:\n" +
        "    sys.exit(0)\n" +
        "finally:\n" +
        "    sock.close()\n";

    private readonly TimeProvider _clock;

    /// <param name="clock">Clock for probe timings. Null defaults to system.</param>
    public EgressCanaryVerifier(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Builds one probe exec as an argv array — never a shell string. The
    /// check name rides as <c>sys.argv[1]</c> alongside the fixed mode token,
    /// so fakes can route on it deterministically.
    /// </summary>
    /// <exception cref="ArgumentException">Any value is blank or out of range.</exception>
    public static SandboxExec BuildProbeExec(
        string checkName,
        string mode,
        string host,
        int port,
        TimeSpan probeTimeout,
        int maxOutputBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (mode is not ("allow" or "block"))
            throw new ArgumentException("Probe mode must be 'allow' or 'block'.", nameof(mode));
        if (host.Length > EgressVerificationOptions.MaxHostLength)
            throw new ArgumentException("Probe host is too long.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (probeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        if (maxOutputBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        var timeoutSeconds = probeTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return new SandboxExec
        {
            Argv = [ProbeBinary, "-c", ProbeScript, checkName, mode, host, port.ToString(CultureInfo.InvariantCulture), timeoutSeconds],
            MaxStdoutBytes = maxOutputBytes,
            MaxStderrBytes = maxOutputBytes,
        };
    }

    /// <summary>
    /// Runs the four canary probes sequentially through
    /// <see cref="ISandbox.ExecAsync"/>: allowed connects, canary-blocked,
    /// IPv6-global and provider-host-LAN must not. Each probe is bounded by
    /// twice <see cref="EgressVerificationOptions.PerCheckTimeout"/> (the
    /// in-guest timeout fires first at exactly one times), so the whole
    /// canary completes within roughly four times the per-check timeout plus
    /// exec overhead. The provider's filter-health signal, when exposed, is
    /// checked before and after the probes and fails the canary when the
    /// filter process is gone.
    /// </summary>
    public async Task<EgressCanaryResult> VerifyAsync(
        ISandbox sandbox,
        string providerKind,
        EgressVerificationOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        ArgumentNullException.ThrowIfNull(options);
        var kind = providerKind.Trim().ToLowerInvariant();
        var started = _clock.GetUtcNow();
        options.Validate();
        if (!options.AreCanaryEndpointsConfigured())
        {
            var finished = _clock.GetUtcNow();
            return new EgressCanaryResult(
                sandbox.Id, kind, Passed: false, started, finished, [],
                "Egress verification is not configured: CodeyBox:EgressVerification must set " +
                "AllowedHost, BlockedHost and LanHost before any canary can pass.");
        }
        var healthFailure = CheckFilterHealth(sandbox);
        if (healthFailure is not null)
        {
            var finished = _clock.GetUtcNow();
            return new EgressCanaryResult(sandbox.Id, kind, Passed: false, started, finished, [], healthFailure);
        }
        var checks = new List<EgressCanaryCheckResult>(4);
        checks.Add(await RunProbeAsync(sandbox, CheckAllow, "allow", options.AllowedHost, options.AllowedPort, options, ct).ConfigureAwait(false));
        checks.Add(await RunProbeAsync(sandbox, CheckBlockCanary, "block", options.BlockedHost, options.BlockedPort, options, ct).ConfigureAwait(false));
        checks.Add(await RunProbeAsync(sandbox, CheckBlockIpv6, "block", options.Ipv6Host, options.Ipv6Port, options, ct).ConfigureAwait(false));
        checks.Add(await RunProbeAsync(sandbox, CheckBlockLan, "block", options.LanHost, options.LanPort, options, ct).ConfigureAwait(false));
        healthFailure = CheckFilterHealth(sandbox);
        var finishedAt = _clock.GetUtcNow();
        if (healthFailure is not null)
            return new EgressCanaryResult(sandbox.Id, kind, Passed: false, started, finishedAt, checks, healthFailure);
        var failed = checks.FirstOrDefault(static c => !c.Passed);
        return new EgressCanaryResult(
            sandbox.Id, kind, failed is null, started, finishedAt, checks,
            failed is null ? null : $"Canary probe '{failed.Name}' failed: {failed.Detail}");
    }

    private static string? CheckFilterHealth(ISandbox sandbox)
    {
        IEgressFilterHealth? health;
        try
        {
            health = SandboxCapability.Find<IEgressFilterHealth>(sandbox);
        }
        catch (InvalidOperationException ex)
        {
            return $"Filter-health capability chain is malformed ({ex.Message}); failing closed.";
        }
        if (health is not null && !health.IsFilterAlive)
            return "Provider filter process is not alive; failing closed without running guest probes.";
        return null;
    }

    private async Task<EgressCanaryCheckResult> RunProbeAsync(
        ISandbox sandbox,
        string checkName,
        string mode,
        string host,
        int port,
        EgressVerificationOptions options,
        CancellationToken ct)
    {
        var probeStarted = _clock.GetUtcNow();
        var exec = BuildProbeExec(checkName, mode, host, port, options.PerCheckTimeout, options.MaxProbeOutputBytes);
        SandboxExecResult result;
        try
        {
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(options.PerCheckTimeout + options.PerCheckTimeout);
            result = await sandbox.ExecAsync(exec, checkCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(checkName, host, port, probeStarted, -1, "probe timed out");
        }
        var elapsed = _clock.GetUtcNow() - probeStarted;
        if (result.ExecutionUnavailable)
            return Fail(checkName, host, port, probeStarted, result.ExitCode, "probe exec unavailable");
        var passed = mode == "allow" ? result.ExitCode == 0 : result.ExitCode == 1;
        var detail = passed
            ? mode == "allow" ? "connected" : "blocked"
            : mode == "allow"
                ? $"no connection (exit {result.ExitCode})"
                : result.ExitCode == 0
                    ? "reachable but must be blocked"
                    : $"probe error (exit {result.ExitCode})";
        if (!passed && result.ExitCode == 2 && result.Stderr.Length > 0)
            detail += ": " + Truncate(result.Stderr);
        return new EgressCanaryCheckResult(checkName, host, port, passed, result.ExitCode, elapsed, detail);
    }

    private EgressCanaryCheckResult Fail(
        string checkName, string host, int port, DateTimeOffset started, int exitCode, string detail) =>
        new(checkName, host, port, Passed: false, exitCode, _clock.GetUtcNow() - started, detail);

    private static string Truncate(string value)
    {
        var flat = value.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= MaxDetailChars ? flat : flat[..MaxDetailChars];
    }
}
