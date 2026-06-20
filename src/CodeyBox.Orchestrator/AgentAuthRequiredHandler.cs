using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Shared auth-required side-effect handler used by both the work-item pipeline
/// and release deep-audit paths. Owns the reason formatting, availability
/// bench, structured audit-log entry, and webhook publish so the two consumers
/// cannot drift on classification fix landings.
/// </summary>
public interface IAgentAuthRequiredHandler
{
    /// <summary>
    /// Builds the single-line reason string surfaced on the
    /// <c>agent.smoke_failed</c> webhook, the availability registry, and the
    /// raised <see cref="AgentAuthRequiredException"/>. Format:
    /// <c>auth required from agent output during {phase}[ for release {id}]: {detail}</c>
    /// with optional stdout-only annotation when the evidence is model-controlled.
    /// </summary>
    string BuildReason(
        string phase,
        AgentFailureClassification classification,
        bool stdoutOnlyEvidence,
        string? stdoutOnlyNote = null,
        Release? release = null);

    /// <summary>
    /// Emits the auth-required side effects (structured audit log, registry
    /// bench, webhook publish). Skipped when the caller has already determined
    /// the evidence is insufficient for global benching (e.g. stdout-only with
    /// no smoke corroboration).
    /// </summary>
    Task PublishSideEffectsAsync(
        AgentKind agent,
        string reason,
        WorkItem? item = null,
        Project? project = null,
        Release? release = null,
        CancellationToken ct = default);
}

internal sealed class AgentAuthRequiredHandler : IAgentAuthRequiredHandler
{
    private readonly IAgentAuthAvailabilityRegistry _availability;
    private readonly IWebhookDispatcher _webhooks;
    private readonly ILogger _log;

    public AgentAuthRequiredHandler(
        IAgentAuthAvailabilityRegistry availability,
        IWebhookDispatcher webhooks,
        ILogger log)
    {
        _availability = availability ?? throw new ArgumentNullException(nameof(availability));
        _webhooks = webhooks ?? throw new ArgumentNullException(nameof(webhooks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string BuildReason(
        string phase,
        AgentFailureClassification classification,
        bool stdoutOnlyEvidence,
        string? stdoutOnlyNote = null,
        Release? release = null)
    {
        var detail = classification.Reason ?? "login prompt matched";
        if (stdoutOnlyEvidence)
            detail = $"{detail}; {stdoutOnlyNote ?? "stdout accepted as authoritative CLI output for this phase"}";

        var phaseScope = release is null ? phase : $"{phase} for release {release.Id}";
        return SingleLineSummary($"auth required from agent output during {phaseScope}: {detail}");
    }

    public async Task PublishSideEffectsAsync(
        AgentKind agent,
        string reason,
        WorkItem? item = null,
        Project? project = null,
        Release? release = null,
        CancellationToken ct = default)
    {
        // The webhook publish below intentionally uses CancellationToken.None
        // so terminal auth-bench delivery survives an operator-driven worker
        // cancel; the parameter is honored only for the registry mark above.
        ct.ThrowIfCancellationRequested();

        AuditLog.AgentSmokeFailed(agent, reason, TimeSpan.Zero, SmokeFailureCategory.Persistent);

        // AuthRequired is intentionally outside the smoke-gate taxonomy:
        // if the operator disables the master smoke switch
        // (CodeyBox:Smoke:Enabled=false), HostSmoke/InVmSmoke/MissingProbe
        // exclusions are ignored at dispatch — but authoritative runtime
        // login-prompt evidence means the binary is broken, so
        // AuthRequired survives the smoke-disabled gate via
        // AgentAvailabilityRegistry.IsNonSmokeExclusion.
        var transition = _availability.MarkAuthRequired(agent, reason);

        if (transition.SourceChanged)
        {
            try
            {
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "agent.smoke_failed",
                    WorkItem = item,
                    Project = project,
                    Release = release,
                    Details = new AgentSmokeFailedDetails
                    {
                        AgentKind = agent.Value,
                        Reason = reason,
                        Category = SmokeFailureCategory.Persistent,
                    },
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Failed to publish agent.smoke_failed webhook for auth-required agent {Agent}: {Reason}",
                    agent.Value,
                    reason);
            }
        }
    }

    /// <summary>
    /// Normalises a reason string for log / webhook serialisation: strips
    /// CR/LF and other control characters (replaced with spaces) so plain-text
    /// log sinks cannot be spoofed by embedded newlines (CWE-117), collapses
    /// runs of whitespace, and trims. Returns an empty string for null input.
    /// </summary>
    public static string SingleLineSummary(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' || char.IsControl(ch))
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
            }
            else if (ch == ' ')
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
