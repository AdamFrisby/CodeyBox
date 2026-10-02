using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// A provider-side transient failure (model capacity / overload, output-token
/// truncation, transport or upstream 5xx after the CLI's own retries) that
/// must park the work item for bounded transient retry on the SAME agent and
/// SAME model — never a terminal failure (which would increment
/// <see cref="WorkItem.TerminalFailureCount"/>) and never a quota failover
/// (which could switch models; for some agents only the configured model may
/// be used). The parked failure kind stays the established
/// <c>transient</c> value so the existing backoff-with-jitter budget applies;
/// the matched family and detector-owned signature label travel here for
/// logging, audit, and host-level correlation.
/// </summary>
public sealed class ProviderTransientRetryException : Exception
{
    public AgentKind Agent { get; }

    public string? Phase { get; }

    public ProviderTransientDetection Detection { get; }

    public ProviderTransientRetryException(
        AgentKind agent,
        string? phase,
        ProviderTransientDetection detection,
        string message)
        : base(message)
    {
        Agent = agent;
        Phase = phase;
        Detection = detection;
    }
}
