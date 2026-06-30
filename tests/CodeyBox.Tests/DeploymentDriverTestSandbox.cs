using System.Collections.Concurrent;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Test double used across the deployment driver tests. Implements
/// <see cref="ISandboxProvider"/> + <see cref="ISandbox"/> with controllable
/// exec behaviour (success/fail per-command-pattern, scripted readiness
/// probe transitions) so each test can pin a specific lifecycle outcome
/// without spinning a real VM.
/// </summary>
internal sealed class FakeDeploymentSandboxProvider : ISandboxProvider
{
    private readonly List<FakeDeploymentSandbox> _created = new();
    private readonly List<SandboxSpec> _specs = new();
    private readonly ConcurrentDictionary<string, byte> _disposedNames = new(StringComparer.Ordinal);
    private bool _createThrows;

    public string Name => "fake-deployment";

    public IReadOnlyList<FakeDeploymentSandbox> Created
    {
        get { lock (_created) return _created.ToList(); }
    }

    /// <summary>Every <see cref="SandboxSpec"/> CreateAsync received, in order.</summary>
    public IReadOnlyList<SandboxSpec> Specs
    {
        get { lock (_specs) return _specs.ToList(); }
    }

    public IReadOnlyCollection<string> DisposedNames => _disposedNames.Keys.ToList();

    /// <summary>Stable ordered list of every exec invocation across all created sandboxes.</summary>
    public List<string> ExecLog { get; } = new();

    /// <summary>Stable ordered list of every full exec request across all created sandboxes.</summary>
    public List<SandboxExec> ExecInvocations { get; } = new();

    /// <summary>Script of (commandPattern → result). First match wins.</summary>
    public List<ExecRule> ExecRules { get; } = new();

    public string? HostAddress { get; set; } = "10.42.0.10";
    public Func<DeploymentEndpointRequest, DeploymentEndpoint>? PublishEndpointOverride { get; set; }
    public HashSet<string> DisposeThrowsFor { get; } = new(StringComparer.Ordinal);
    public HashSet<string> SandboxDisposeThrowsFor { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional override for the synthetic <see cref="ManagedSandboxInfo"/>
    /// returned by <see cref="ListAllManagedAsync"/>. Set by leak-reaper tests
    /// that need to model HasPreemptMarker / IsSuspendLifecycleOrFrozen
    /// preserve-grace shapes without spinning a real provider.
    /// </summary>
    public Func<FakeDeploymentSandbox, ManagedSandboxInfo>? ManagedInfoOverride { get; set; }

    public void SetCreateThrows() => _createThrows = true;

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        if (_createThrows)
            throw new InvalidOperationException("Simulated provisioning failure.");
        lock (_specs) _specs.Add(spec);
        var sb = new FakeDeploymentSandbox(this, spec);
        lock (_created) _created.Add(sb);
        return Task.FromResult<ISandbox>(sb);
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        IReadOnlyList<ManagedSandboxInfo> snapshot;
        lock (_created)
        {
            snapshot = _created
                .Select(sb => ManagedInfoOverride is { } ov
                    ? ov(sb)
                    : new ManagedSandboxInfo(
                        sb.Id,
                        sb.CreatedAt,
                        DiskBytes: null,
                        IsTrackedActive: !sb.IsDisposed,
                        Purpose: sb.Spec.Purpose))
                .ToList();
        }
        return Task.FromResult(snapshot);
    }

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        if (DisposeThrowsFor.Contains(name))
            throw new InvalidOperationException($"Simulated dispose failure for {name}.");
        _disposedNames[name] = 0;
        lock (_created)
        {
            var sb = _created.FirstOrDefault(s => s.Id == name);
            sb?.MarkDisposed();
        }
        return Task.CompletedTask;
    }

    internal async Task<SandboxExecResult> ResolveExecAsync(SandboxExec exec, CancellationToken ct)
    {
        var command = string.Join(' ', exec.Argv);
        lock (ExecLog) ExecLog.Add(command);
        lock (ExecInvocations) ExecInvocations.Add(exec);
        foreach (var rule in ExecRules)
        {
            if (rule.Matches(command))
                return await rule.ApplyAsync(ct).ConfigureAwait(false);
        }
        return new SandboxExecResult(0, string.Empty, string.Empty);
    }
}

/// <summary>
/// Programmable exec rule. <see cref="Substring"/> matches a substring of
/// the command; the first matching rule applies. The rule can supply a
/// scripted sequence of results so a readiness probe can be made to fail
/// N times and succeed afterwards (matches the realistic startup pattern).
/// </summary>
internal sealed class ExecRule
{
    public string Substring { get; }
    private readonly Queue<SandboxExecResult> _results;
    private readonly SandboxExecResult? _finalLoop;
    private readonly TimeSpan? _delay;
    public int InvocationCount { get; private set; }

    public ExecRule(string substring, SandboxExecResult result, TimeSpan? delay = null)
    {
        Substring = substring;
        _results = new Queue<SandboxExecResult>();
        _finalLoop = result;
        _delay = delay;
    }

    public ExecRule(
        string substring,
        IEnumerable<SandboxExecResult> scripted,
        SandboxExecResult? finalLoop = null,
        TimeSpan? delay = null)
    {
        Substring = substring;
        _results = new Queue<SandboxExecResult>(scripted);
        _finalLoop = finalLoop;
        _delay = delay;
    }

    public bool Matches(string command) => command.Contains(Substring, StringComparison.Ordinal);

    public async Task<SandboxExecResult> ApplyAsync(CancellationToken ct)
    {
        InvocationCount++;
        if (_delay is { } delay)
            await Task.Delay(delay, ct).ConfigureAwait(false);
        if (_results.Count > 0)
            return _results.Dequeue();
        return _finalLoop ?? new SandboxExecResult(0, string.Empty, string.Empty);
    }
}

internal sealed class FakeDeploymentSandbox : IRoutableSandbox, IDeploymentEndpointPublisher
{
    private readonly FakeDeploymentSandboxProvider _provider;
    public string Id { get; } = $"codeybox-{Guid.NewGuid():N}"[..23];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsDisposed { get; private set; }
    public int DisposeCallCount { get; private set; }
    public SandboxSpec Spec { get; }
    public string? HostAddress => _provider.HostAddress;

    public FakeDeploymentSandbox(FakeDeploymentSandboxProvider provider, SandboxSpec spec)
    {
        _provider = provider;
        Spec = spec;
    }

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _provider.ResolveExecAsync(exec, ct);
    }

    public bool CanPublishEndpoint(DeploymentEndpointRequest request)
        => !string.IsNullOrWhiteSpace(HostAddress)
            && request.Port is >= 1 and <= 65535
            && request.Kind is DeploymentEndpointKind.Http or DeploymentEndpointKind.Tcp;

    public DeploymentEndpoint PublishEndpoint(DeploymentEndpointRequest request)
    {
        if (!CanPublishEndpoint(request))
            throw new NotSupportedException(
                $"Fake deployment sandbox '{Id}' cannot publish {request.Kind} endpoint on port {request.Port?.ToString() ?? "<none>"}.");
        return _provider.PublishEndpointOverride?.Invoke(request)
            ?? DeploymentEndpointPublisher.ForHostPort(request, HostAddress!);
    }

    public ValueTask DisposeAsync()
    {
        if (_provider.SandboxDisposeThrowsFor.Contains(Id))
            throw new InvalidOperationException($"Simulated sandbox dispose failure for {Id}.");
        DisposeCallCount++;
        MarkDisposed();
        return ValueTask.CompletedTask;
    }

    internal void MarkDisposed() => IsDisposed = true;
}
