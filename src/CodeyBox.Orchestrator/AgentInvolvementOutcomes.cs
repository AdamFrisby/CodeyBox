using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal enum AgentInvolvementFailureCategory
{
    Quota,
    Timeout,
    Transient,
    Infrastructure,
    Auth,
    Agent,
    Cancelled,
    SemanticIncompatible,
}

internal static class AgentInvolvementOutcomes
{
    public const string Success = "success";
    public const string Cancelled = "cancelled";
    public const string FailureQuota = "failure:quota";
    public const string FailureTimeout = "failure:timeout";
    public const string FailureTransient = "failure:transient";
    public const string FailureInfrastructure = "failure:infrastructure";
    public const string FailureAuth = "failure:auth";
    public const string FailureAgent = "failure:agent";
    public const string FailureCancelled = "failure:cancelled";
    public const string FailureSemanticIncompatible = "failure:semantic-incompatible";

    public static string ForFailure(Exception ex) => ex switch
    {
        TerminalQuotaError => FailureQuota,
        AgentAuthRequiredException => FailureAuth,
        AuditorIdleTimeoutException => FailureTimeout,
        TerminalTransientNetworkError => FailureTransient,
        AgentInfrastructureFailureException => FailureInfrastructure,
        PipelineRunner.AgentAttemptTimeoutException => FailureTimeout,
        OperationCanceledException => FailureCancelled,
        _ => FailureAgent,
    };

    public static bool IsFailure(string? outcome) =>
        TryParseFailure(outcome, out _);

    public static bool TryParseFailure(
        string? outcome,
        out AgentInvolvementFailureCategory category)
    {
        switch (outcome)
        {
            case FailureQuota:
                category = AgentInvolvementFailureCategory.Quota;
                return true;
            case FailureTimeout:
                category = AgentInvolvementFailureCategory.Timeout;
                return true;
            case FailureTransient:
                category = AgentInvolvementFailureCategory.Transient;
                return true;
            case FailureInfrastructure:
                category = AgentInvolvementFailureCategory.Infrastructure;
                return true;
            case FailureAuth:
                category = AgentInvolvementFailureCategory.Auth;
                return true;
            case FailureAgent:
                category = AgentInvolvementFailureCategory.Agent;
                return true;
            case Cancelled:
            case FailureCancelled:
                category = AgentInvolvementFailureCategory.Cancelled;
                return true;
            case FailureSemanticIncompatible:
                category = AgentInvolvementFailureCategory.SemanticIncompatible;
                return true;
            default:
                category = default;
                return false;
        }
    }

    public static string InfraKind(AgentInvolvementFailureCategory category) => category switch
    {
        AgentInvolvementFailureCategory.Quota => "quota",
        AgentInvolvementFailureCategory.Timeout => "timeout",
        AgentInvolvementFailureCategory.Transient => "transient",
        AgentInvolvementFailureCategory.Infrastructure => "infrastructure",
        AgentInvolvementFailureCategory.Auth => "auth",
        AgentInvolvementFailureCategory.Agent => "agent",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Outcome is not an infrastructure health failure."),
    };
}
