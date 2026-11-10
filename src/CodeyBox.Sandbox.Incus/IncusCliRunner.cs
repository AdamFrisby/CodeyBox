using CodeyBox.HostProcess;

namespace CodeyBox.Sandbox.Incus;

internal sealed class IncusCliRunner
{
    private readonly IProcessRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly AdjustableOperationGate _operationGate = new();

    internal IncusCliRunner(IProcessRunner runner, TimeProvider? timeProvider = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<ProcessRunResult> RunCheckedAsync(
        string operation,
        IncusSandboxOptions options,
        IReadOnlyList<string> argv,
        string? stdin,
        TimeSpan? timeout,
        CancellationToken ct,
        bool heavyOperation = true,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        bool killOnOutputLimit = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0)
            throw new ArgumentException("CLI argv must not be empty.", nameof(argv));

        IDisposable? gateLease = null;
        if (heavyOperation)
            gateLease = await _operationGate.EnterAsync(options.MaxConcurrentOperations, ct).ConfigureAwait(false);

        try
        {
            for (var retry = 0; ; retry++)
            {
                try
                {
                    return await InvokeCheckedAsync(
                        operation,
                        options,
                        argv,
                        stdin,
                        timeout,
                        ct,
                        stdoutChunkCallback,
                        stderrChunkCallback,
                        maxStdoutBytes,
                        maxStderrBytes,
                        killOnOutputLimit).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    retry < options.DaemonUnavailableRetryAttempts
                    && ex is not OperationCanceledException
                    && IsDaemonConnectivityFailure(ex, options))
                {
                    // The daemon is restarting: wait — bounded by
                    // DaemonReadyTimeout — for it to answer a probe again so
                    // the retry does not hit the same restart window. When it
                    // never answers, surface the original failure; the
                    // transient classifier defers the step for re-check.
                    if (!await WaitForDaemonReadyAsync(options, ct).ConfigureAwait(false))
                        throw;
                }
            }
        }
        finally
        {
            gateLease?.Dispose();
        }
    }

    private async Task<ProcessRunResult> InvokeCheckedAsync(
        string operation,
        IncusSandboxOptions options,
        IReadOnlyList<string> argv,
        string? stdin,
        TimeSpan? timeout,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback,
        Action<string>? stderrChunkCallback,
        int? maxStdoutBytes,
        int? maxStderrBytes,
        bool killOnOutputLimit)
    {
        var operationTimeout = timeout ?? options.OperationTimeout;
        using var timeoutCancellation = new CancellationTokenSource(operationTimeout, _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCancellation.Token);
        ProcessRunResult result;
        try
        {
            result = await _runner.RunAsync(
                argv,
                stdin,
                deadline.Token,
                stdoutChunkCallback,
                stderrChunkCallback,
                maxStdoutBytes ?? options.MaxCliStdoutBytes,
                maxStderrBytes ?? options.MaxCliStderrBytes,
                environment: null,
                killOnOutputLimit).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            throw new IncusTransientTimeoutException(
                operation,
                $"Incus {operation} exceeded its {operationTimeout.TotalSeconds:F0}-second deadline.",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Incus {operation} could not be executed.", ex);
        }

        if (result.StdoutLimitExceeded || result.StderrLimitExceeded)
            throw new IncusOutputBoundException(
                operation,
                $"Incus {operation} exceeded its configured output bound (output-bound).");
        if (result.StartFailed || result.ExecutionUnavailable)
            throw new InvalidOperationException($"Incus {operation} could not start the configured CLI.");
        if (result.ExitCode != 0)
        {
            var error = SanitizeError(result.Stderr);
            throw new InvalidOperationException(
                $"Incus {operation} failed with exit code {result.ExitCode}: {error}");
        }
        return result;
    }

    internal async Task<ProcessRunResult> RunAllowFailureAsync(
        IncusSandboxOptions options,
        IReadOnlyList<string> argv,
        string? stdin,
        TimeSpan? timeout,
        CancellationToken ct,
        bool heavyOperation = false,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        bool killOnOutputLimit = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0)
            throw new ArgumentException("CLI argv must not be empty.", nameof(argv));

        IDisposable? gateLease = null;
        if (heavyOperation)
            gateLease = await _operationGate.EnterAsync(options.MaxConcurrentOperations, ct).ConfigureAwait(false);
        try
        {
            var result = await InvokeAllowFailureAsync(
                options,
                argv,
                stdin,
                timeout,
                ct,
                stdoutChunkCallback,
                stderrChunkCallback,
                maxStdoutBytes,
                maxStderrBytes,
                killOnOutputLimit).ConfigureAwait(false);
            if (result.ExitCode != 0
                && !result.StartFailed
                && !result.ExecutionUnavailable
                && IsDaemonConnectivityMessage(result.Stderr, options))
            {
                // The CLI lost its daemon connection mid-call, so the
                // in-guest outcome is ambiguous — never a definitive command
                // result. Report transport loss and let the caller's
                // interrupted-exec/recovery handling decide; allow-failure
                // steps are deliberately not retried in-band here because an
                // `incus exec` may already have run (part of) its command.
                result = result with { ExecutionUnavailable = true };
            }
            return result;
        }
        finally
        {
            gateLease?.Dispose();
        }
    }

    private async Task<ProcessRunResult> InvokeAllowFailureAsync(
        IncusSandboxOptions options,
        IReadOnlyList<string> argv,
        string? stdin,
        TimeSpan? timeout,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback,
        Action<string>? stderrChunkCallback,
        int? maxStdoutBytes,
        int? maxStderrBytes,
        bool killOnOutputLimit)
    {
        var operationTimeout = timeout ?? options.OperationTimeout;
        using var timeoutCancellation = new CancellationTokenSource(operationTimeout, _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCancellation.Token);
        try
        {
            var result = await _runner.RunAsync(
                argv,
                stdin,
                deadline.Token,
                stdoutChunkCallback,
                stderrChunkCallback,
                maxStdoutBytes: maxStdoutBytes ?? options.MaxCliStdoutBytes,
                maxStderrBytes: maxStderrBytes ?? options.MaxCliStderrBytes,
                environment: null,
                killOnOutputLimit).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            var subcommand = DescribeSubcommand(argv);
            throw new IncusTransientTimeoutException(
                subcommand,
                $"Incus CLI operation [{subcommand}] exceeded its {operationTimeout.TotalSeconds:F0}-second deadline.",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Incus CLI operation could not be executed.",
                ex);
        }
    }

    private const int DaemonProbeMaxStdoutBytes = 64 * 1024;
    private const int DaemonProbeMaxStderrBytes = 4096;

    /// <summary>
    /// Polls <c>incus query /1.0</c> until the daemon answers or
    /// <see cref="IncusSandboxOptions.DaemonReadyTimeout"/> elapses. Returns
    /// true when the daemon answered; false when the bounded wait expired.
    /// Never throws for daemon-side failures — only caller cancellation
    /// propagates.
    /// </summary>
    private async Task<bool> WaitForDaemonReadyAsync(IncusSandboxOptions options, CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + options.DaemonReadyTimeout;
        var pollInterval = options.DaemonReadyPollInterval;
        var probeTimeout = options.DaemonReadyProbeTimeout;
        var probeArgv = new[] { options.BinaryPath, "query", "/1.0" };
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return false;
            var slice = remaining < probeTimeout ? remaining : probeTimeout;
            using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // CancelAfter is hard-wired to the system clock; an ITimer on the
            // injected TimeProvider keeps the probe deadline on the same clock
            // as the readiness wait (a fake-clock Advance() then fires both).
            using var probeTimer = _timeProvider.CreateTimer(
                static state =>
                {
                    try
                    {
                        ((CancellationTokenSource)state!).Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The probe completed and disposed the CTS first.
                    }
                },
                probeDeadline,
                slice,
                Timeout.InfiniteTimeSpan);
            ProcessRunResult? probe = null;
            try
            {
                probe = await _runner.RunAsync(
                    probeArgv,
                    stdin: null,
                    probeDeadline.Token,
                    maxStdoutBytes: DaemonProbeMaxStdoutBytes,
                    maxStderrBytes: DaemonProbeMaxStderrBytes).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A refused socket, a timed-out probe, or a spawn failure all
                // mean "not ready yet" — keep polling inside the bound.
            }
            if (probe is { ExitCode: 0, StartFailed: false, ExecutionUnavailable: false })
                return true;
            var delay = pollInterval < remaining ? pollInterval : remaining;
            await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private static bool IsDaemonConnectivityFailure(Exception ex, IncusSandboxOptions options) =>
        IncusTransientInfrastructure.IsDaemonConnectivityFault(
            IncusTransientInfrastructure.TryClassify(ex, options.TransientInfrastructureSignatures));

    private static bool IsDaemonConnectivityMessage(string? message, IncusSandboxOptions options) =>
        IncusTransientInfrastructure.IsDaemonConnectivityFault(
            IncusTransientInfrastructure.TryClassifyMessage(message, options.TransientInfrastructureSignatures));

    // incus command groups whose second token is part of the subcommand
    // (e.g. `incus file push`, `incus config device`). Kept small and matched
    // exactly so an unrelated argument can never widen the emitted label.
    private static readonly HashSet<string> SubcommandGroups = new(StringComparer.Ordinal)
    {
        "file", "config", "network", "storage", "project", "profile",
        "image", "cluster", "snapshot", "operation", "remote",
    };

    /// <summary>
    /// Best-effort human label naming which incus subcommand a bounded argv
    /// invokes, for diagnostics only. argv[0] is the binary path; the verbs
    /// follow. Only lowercase verb tokens are emitted — never instance names,
    /// paths, or other untrusted argument values — so the label cannot leak or
    /// be spoofed by caller input.
    /// </summary>
    internal static string DescribeSubcommand(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        // A verb is a bare lowercase token; a leading '-' marks a flag, never a
        // subcommand, so flags fall through to the "operation" fallback rather
        // than being emitted as if they named the hung call.
        static bool IsVerb(string token) =>
            token.Length is > 0 and <= 32
            && token[0] != '-'
            && token.All(static c => c is (>= 'a' and <= 'z') or '-');

        // argv[0] is the binary path. IncusCommandBuilder.Prefix emits a global
        // `--project <name>` before the subcommand; skip it. Direct argv that
        // omit the prefix start their verb at index 1.
        var start = 1;
        if (start + 1 < argv.Count && string.Equals(argv[start], "--project", StringComparison.Ordinal))
            start += 2;

        if (start >= argv.Count || !IsVerb(argv[start]))
            return "operation";
        var primary = argv[start];
        if (SubcommandGroups.Contains(primary) && start + 1 < argv.Count && IsVerb(argv[start + 1]))
            return $"{primary} {argv[start + 1]}";
        return primary;
    }

    private static string SanitizeError(string error)
    {
        const int maxChars = 4096;
        var buffer = new char[Math.Min(error.Length, maxChars + 1)];
        var length = 0;
        foreach (var c in error)
        {
            if (length >= buffer.Length)
                break;
            buffer[length++] = char.IsControl(c) ? ' ' : c;
        }
        var sanitized = new string(buffer, 0, length).Trim();
        return sanitized.Length <= maxChars ? sanitized : sanitized[..maxChars] + "...";
    }

    private sealed class AdjustableOperationGate
    {
        private readonly object _sync = new();
        private readonly LinkedList<Waiter> _waiters = [];
        private int _capacity = 1;
        private int _active;

        internal async ValueTask<IDisposable> EnterAsync(int capacity, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Waiter? waiter = null;
            lock (_sync)
            {
                _capacity = capacity;
                if (_waiters.Count == 0 && _active < _capacity)
                {
                    _active++;
                    return new Lease(this);
                }
                waiter = new Waiter();
                waiter.Node = _waiters.AddLast(waiter);
                GrantWaitersLocked();
            }
            var cancelState = new CancelState(this, waiter, ct);
            using var registration = ct.Register(() => cancelState.Gate.Cancel(cancelState));
            return await waiter.Completion.Task.ConfigureAwait(false);
        }

        private void Cancel(CancelState state)
        {
            lock (_sync)
            {
                if (state.Waiter.Node?.List is null)
                    return;
                _waiters.Remove(state.Waiter.Node);
                state.Waiter.Node = null;
                state.Waiter.Completion.TrySetCanceled(state.Token);
            }
        }

        private void Release()
        {
            lock (_sync)
            {
                if (_active <= 0)
                    throw new InvalidOperationException("Incus operation gate released without an active lease.");
                _active--;
                GrantWaitersLocked();
            }
        }

        private void GrantWaitersLocked()
        {
            while (_active < _capacity && _waiters.First is { } node)
            {
                _waiters.RemoveFirst();
                var waiter = node.Value;
                waiter.Node = null;
                _active++;
                waiter.Completion.TrySetResult(new Lease(this));
            }
        }

        private sealed class Waiter
        {
            internal TaskCompletionSource<IDisposable> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal LinkedListNode<Waiter>? Node { get; set; }
        }

        private sealed class Lease : IDisposable
        {
            private AdjustableOperationGate? _gate;
            internal Lease(AdjustableOperationGate gate) => _gate = gate;
            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }

        private sealed record CancelState(
            AdjustableOperationGate Gate,
            Waiter Waiter,
            CancellationToken Token);
    }
}
