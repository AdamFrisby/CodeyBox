namespace CodeyBox.Orchestrator;

/// <summary>
/// Ambient attribution for sandbox-permit waits. The pipeline sets this around
/// each phase's sandbox acquisition (work, rework, planning, audit, merge,
/// conflict-rework) so <see cref="SandboxAdmissionGate"/> can name the waiting
/// work item and phase in its slow-permit diagnostic instead of failing
/// silently. Flows with the async call context; each scope restores its
/// predecessor on dispose.
/// </summary>
internal sealed class SandboxPermitWaitScope : IDisposable
{
    private static readonly AsyncLocal<SandboxPermitWaitScope?> _current = new();

    private readonly SandboxPermitWaitScope? _previous;
    private bool _disposed;

    private SandboxPermitWaitScope(string workItemId, string phase, SandboxPermitWaitScope? previous)
    {
        WorkItemId = workItemId;
        Phase = phase;
        _previous = previous;
    }

    public static SandboxPermitWaitScope? Current => _current.Value;

    public string WorkItemId { get; }

    public string Phase { get; }

    public static SandboxPermitWaitScope Begin(string workItemId, string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        var scope = new SandboxPermitWaitScope(workItemId, phase, _current.Value);
        _current.Value = scope;
        return scope;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _current.Value = _previous;
    }
}
