using CodeyBox.HostProcess;

namespace CodeyBox.Sandbox.Incus;

internal sealed class IncusCliRunner
{
    private readonly IProcessRunner _runner;
    private readonly AdjustableOperationGate _operationGate = new();

    internal IncusCliRunner(IProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
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
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout ?? options.OperationTimeout);
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
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Incus {operation} exceeded its {(timeout ?? options.OperationTimeout).TotalSeconds:F0}-second deadline.",
                    ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Incus {operation} could not be executed.", ex);
            }

            if (result.StdoutLimitExceeded || result.StderrLimitExceeded)
                throw new InvalidOperationException($"Incus {operation} exceeded its configured output bound.");
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
        finally
        {
            gateLease?.Dispose();
        }
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
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout ?? options.OperationTimeout);
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
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Incus CLI operation exceeded its {(timeout ?? options.OperationTimeout).TotalSeconds:F0}-second deadline.",
                    ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "Incus CLI operation could not be executed.",
                    ex);
            }
        }
        finally
        {
            gateLease?.Dispose();
        }
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
