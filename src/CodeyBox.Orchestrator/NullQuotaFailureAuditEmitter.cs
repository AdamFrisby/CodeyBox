using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// No-op fallback used when the wired <see cref="IQuotaFailureClassifier"/> does
/// not also implement <see cref="IQuotaFailureAuditEmitter"/>. Tests that swap
/// in a classification-only fake should not silently lose advisory audit-event
/// emission for components they did wire; production composition uses
/// <see cref="CompositeQuotaFailureClassifier"/>, which implements both.
/// </summary>
internal sealed class NullQuotaFailureAuditEmitter : IQuotaFailureAuditEmitter
{
    public static readonly NullQuotaFailureAuditEmitter Instance = new();

    private NullQuotaFailureAuditEmitter() { }

    public void EmitAdvisoryAuditEvents(AgentKind agent, string? stderr, string? stdout, string phase, string? sandboxName)
    {
        // intentional no-op
    }
}
