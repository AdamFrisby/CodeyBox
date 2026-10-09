namespace CodeyBox.Orchestrator.ExternalBuilds;

/// <summary>
/// Runner transport capability negotiation for external-build tools.
/// Compatibility baseline: ordinary durable start/status/result/cancel tools
/// work with every runner. The optional MCP <c>2026-07-28</c>
/// <c>io.modelcontextprotocol/tasks</c> extension is used only after
/// capability negotiation, with server-directed async task creation and
/// <c>tasks/get</c> returning the final result inline. A task handle is never
/// returned to a client that did not negotiate support. The old
/// <c>2025-11-25</c> <c>tasks/result</c> shape is available only as an
/// explicitly versioned compatibility path, not current semantics. Custom
/// events/task notifications are distinct from durable state and never resume
/// an agent by themselves. A scoped CLI bridge is the fallback when no MCP
/// transport is available.
/// </summary>
public sealed class ExternalBuildRunnerTransport
{
    public const string CurrentTasksExtension = "io.modelcontextprotocol/tasks@2026-07-28";
    public const string LegacyTasksExtension = "io.modelcontextprotocol/tasks@2025-11-25";

    private readonly ExternalBuildSandboxTools _tools;
    private readonly ExternalBuildCliBridge _cliBridge;

    public ExternalBuildRunnerTransport(ExternalBuildSandboxTools tools, ExternalBuildCliBridge? cliBridge = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _cliBridge = cliBridge ?? new ExternalBuildCliBridge(tools);
    }

    /// <summary>Negotiated client capabilities.</summary>
    public sealed record RunnerCapabilities(
        bool SupportsMcpTools,
        bool SupportsTasksCurrent,
        bool SupportsTasksLegacy,
        bool SupportsCliBridge);

    public sealed record DispatchOutcome(
        RunnerTransportKind Kind,
        string? TaskHandle,
        string ResultSummary);

    /// <summary>
    /// Real wiring through the in-process runner transport: MCP-tool calls
    /// when supported, CLI bridge otherwise. Demonstrates capability and
    /// fallback behavior explicitly; no universal async-client support is
    /// assumed.
    /// </summary>
    public async Task<DispatchOutcome> DispatchStatusAsync(
        RunnerCapabilities capabilities, string capabilityHandle, string buildId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.SupportsMcpTools)
        {
            var record = await _tools.StatusAsync(capabilityHandle, buildId, ct).ConfigureAwait(false);
            if (capabilities.SupportsTasksCurrent && IsLongRunning(record.State))
                return new DispatchOutcome(RunnerTransportKind.McpTasksCurrent,
                    "task-" + record.Id, $"poll {record.State} via tasks/get");
            if (capabilities.SupportsTasksLegacy && IsLongRunning(record.State))
                return new DispatchOutcome(RunnerTransportKind.McpTasksLegacy,
                    "compat-" + record.Id, $"poll {record.State} via legacy tasks/result");
            return new DispatchOutcome(RunnerTransportKind.McpTools, null, record.State.ToString());
        }
        if (capabilities.SupportsCliBridge)
        {
            var summary = await _cliBridge.StatusAsync(capabilityHandle, buildId, ct).ConfigureAwait(false);
            return new DispatchOutcome(RunnerTransportKind.CliBridge, null, summary);
        }
        throw new InvalidOperationException("Runner supports neither MCP tools nor the CLI bridge.");
    }

    private static bool IsLongRunning(Core.ExternalBuilds.ExternalBuildState state) =>
        state is Core.ExternalBuilds.ExternalBuildState.Queued
            or Core.ExternalBuilds.ExternalBuildState.Running
            or Core.ExternalBuilds.ExternalBuildState.SubmitUncertain;
}

public enum RunnerTransportKind
{
    McpTools = 0,
    McpTasksCurrent = 1,
    McpTasksLegacy = 2,
    CliBridge = 3,
}

/// <summary>
/// Scoped CLI bridge fallback: exposes the same durable verbs over a
/// restricted local command surface carrying only the capability handle.
/// No provider credentials, no operator vocabulary, no arbitrary commands.
/// </summary>
public sealed class ExternalBuildCliBridge(ExternalBuildSandboxTools tools)
{
    private readonly ExternalBuildSandboxTools _tools = tools ?? throw new ArgumentNullException(nameof(tools));

    public async Task<string> StatusAsync(string capabilityHandle, string buildId, CancellationToken ct = default)
    {
        var record = await _tools.StatusAsync(capabilityHandle, buildId, ct).ConfigureAwait(false);
        return $"build {record.Id} state={record.State} run={record.ProviderRunId ?? "-"}";
    }

    public async Task<string> ResultAsync(string capabilityHandle, string buildId, CancellationToken ct = default)
    {
        var record = await _tools.ResultAsync(capabilityHandle, buildId, ct).ConfigureAwait(false);
        return $"build {record.Id} state={record.State} cause={record.TerminalCause}";
    }

    public async Task<string> CancelAsync(string capabilityHandle, string buildId, CancellationToken ct = default)
    {
        var record = await _tools.CancelAsync(capabilityHandle, buildId, ct).ConfigureAwait(false);
        return $"build {record.Id} state={record.State}";
    }
}
