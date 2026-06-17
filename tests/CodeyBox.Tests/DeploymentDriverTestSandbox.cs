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
    private readonly ConcurrentDictionary<string, byte> _disposedNames = new(StringComparer.Ordinal);
    private bool _createThrows;

    public string Name => "fake-deployment";

    public IReadOnlyList<FakeDeploymentSandbox> Created
    {
        get { lock (_created) return _created.ToList(); }
    }

    public IReadOnlyCollection<string> DisposedNames => _disposedNames.Keys.ToList();

    /// <summary>Stable ordered list of every exec invocation across all created sandboxes.</summary>
    public List<string> ExecLog { get; } = new();

    /// <summary>Script of (commandPattern → result). First match wins.</summary>
    public List<ExecRule> ExecRules { get; } = new();

    public void SetCreateThrows() => _createThrows = true;

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        if (_createThrows)
            throw new InvalidOperationException("Simulated provisioning failure.");
        var sb = new FakeDeploymentSandbox(this);
        lock (_created) _created.Add(sb);
        return Task.FromResult<ISandbox>(sb);
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        IReadOnlyList<ManagedSandboxInfo> snapshot;
        lock (_created)
        {
            snapshot = _created
                .Select(sb => new ManagedSandboxInfo(
                    sb.Id,
                    sb.CreatedAt,
                    DiskBytes: null,
                    IsTrackedActive: !sb.IsDisposed))
                .ToList();
        }
        return Task.FromResult(snapshot);
    }

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        _disposedNames[name] = 0;
        lock (_created)
        {
            var sb = _created.FirstOrDefault(s => s.Id == name);
            sb?.MarkDisposed();
        }
        return Task.CompletedTask;
    }

    internal SandboxExecResult ResolveExec(string command)
    {
        lock (ExecLog) ExecLog.Add(command);
        foreach (var rule in ExecRules)
        {
            if (rule.Matches(command))
                return rule.Apply();
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
    public int InvocationCount { get; private set; }

    public ExecRule(string substring, SandboxExecResult result)
    {
        Substring = substring;
        _results = new Queue<SandboxExecResult>();
        _finalLoop = result;
    }

    public ExecRule(string substring, IEnumerable<SandboxExecResult> scripted, SandboxExecResult? finalLoop = null)
    {
        Substring = substring;
        _results = new Queue<SandboxExecResult>(scripted);
        _finalLoop = finalLoop;
    }

    public bool Matches(string command) => command.Contains(Substring, StringComparison.Ordinal);

    public SandboxExecResult Apply()
    {
        InvocationCount++;
        if (_results.Count > 0)
            return _results.Dequeue();
        return _finalLoop ?? new SandboxExecResult(0, string.Empty, string.Empty);
    }
}

internal sealed class FakeDeploymentSandbox : ISandbox
{
    private readonly FakeDeploymentSandboxProvider _provider;
    public string Id { get; } = $"codeybox-{Guid.NewGuid():N}"[..23];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsDisposed { get; private set; }

    public FakeDeploymentSandbox(FakeDeploymentSandboxProvider provider)
    {
        _provider = provider;
    }

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var command = string.Join(' ', exec.Argv);
        return Task.FromResult(_provider.ResolveExec(command));
    }

    public ValueTask DisposeAsync()
    {
        MarkDisposed();
        return ValueTask.CompletedTask;
    }

    internal void MarkDisposed() => IsDisposed = true;
}
