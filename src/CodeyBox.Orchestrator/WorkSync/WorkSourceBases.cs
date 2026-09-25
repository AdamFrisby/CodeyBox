using System.Runtime.CompilerServices;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator.WorkSync;

/// <summary>
/// Base for polling sources (deployments that cannot receive inbound
/// traffic). Enforces the per-poll bound before buffering and routes every
/// discovered candidate through <see cref="WorkIngestionService"/>, so
/// webhook-driven and polling sources share one ingestion funnel.
/// Webhook payloads for a polling source are rejected: a source that cannot
/// do webhooks polls.
/// </summary>
public abstract class PollingWorkSourceBase : IWorkSource
{
    private readonly Func<WorkSyncOptions> _options;

    /// <param name="namespace">Provider namespace for <see cref="WorkItem.ExternalIds"/>.</param>
    /// <param name="requiredSignal">Operator-configured ingestion signal.</param>
    /// <param name="options">Hot-reloadable knobs; read per poll.</param>
    protected PollingWorkSourceBase(
        string @namespace,
        WorkSignal requiredSignal,
        Func<WorkSyncOptions>? options = null)
    {
        Validation.ValidateExternalIdNamespace(@namespace, nameof(@namespace));
        Namespace = @namespace;
        RequiredSignal = requiredSignal;
        _options = options ?? (() => new WorkSyncOptions());
    }

    /// <inheritdoc />
    public string Namespace { get; }

    /// <inheritdoc />
    public WorkSourceCapabilities Capabilities { get; } = new(SupportsWebhooks: false, SupportsPolling: true);

    /// <inheritdoc />
    public WorkSignal RequiredSignal { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<ExternalWorkItem> PollAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cap = Math.Max(1, _options().MaxItemsPerPoll);
        var count = 0;
        await foreach (var candidate in PollCoreAsync(ct).ConfigureAwait(false))
        {
            if (count >= cap)
                yield break;
            if (!string.Equals(candidate.Namespace, Namespace, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"poll yielded namespace '{candidate.Namespace}' from source '{Namespace}'");
            count++;
            yield return candidate;
        }
    }

    /// <summary>Discovers signalled work in the external system.</summary>
    protected abstract IAsyncEnumerable<ExternalWorkItem> PollCoreAsync(CancellationToken ct);

    /// <inheritdoc />
    public ExternalWorkItem? ParseVerifiedWebhookBody(string verifiedBody) =>
        throw new NotSupportedException(
            $"source '{Namespace}' does not support webhooks; poll instead (see Capabilities)");
}

/// <summary>
/// Base for webhook-driven sources. The hosting endpoint MUST verify the
/// body's authenticity (HMAC signature or shared-token comparison,
/// provider-specific, mirroring the <c>InteractionEndpoints</c> ordering
/// rule) before calling
/// <see cref="ParseVerifiedWebhookBody"/>: this method assigns meaning to
/// untrusted bytes and never verifies. Parsed candidates flow through
/// <see cref="WorkIngestionService"/>, sharing the funnel with polling.
/// </summary>
public abstract class WebhookWorkSourceBase : IWorkSource
{
    /// <param name="namespace">Provider namespace for <see cref="WorkItem.ExternalIds"/>.</param>
    /// <param name="requiredSignal">Operator-configured ingestion signal.</param>
    protected WebhookWorkSourceBase(string @namespace, WorkSignal requiredSignal)
    {
        Validation.ValidateExternalIdNamespace(@namespace, nameof(@namespace));
        Namespace = @namespace;
        RequiredSignal = requiredSignal;
    }

    /// <inheritdoc />
    public string Namespace { get; }

    /// <inheritdoc />
    public WorkSourceCapabilities Capabilities { get; } = new(SupportsWebhooks: true, SupportsPolling: false);

    /// <inheritdoc />
    public WorkSignal RequiredSignal { get; }

    /// <inheritdoc />
    public IAsyncEnumerable<ExternalWorkItem> PollAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"source '{Namespace}' is webhook-driven and cannot be polled (see Capabilities)");

    /// <inheritdoc />
    public ExternalWorkItem? ParseVerifiedWebhookBody(string verifiedBody)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifiedBody);
        var candidate = ParseVerifiedCore(verifiedBody);
        if (candidate is not null
            && !string.Equals(candidate.Namespace, Namespace, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"webhook body yielded namespace '{candidate.Namespace}' from source '{Namespace}'");
        return candidate;
    }

    /// <summary>
    /// Interprets an already-authenticated webhook body. Returns null when
    /// the payload carries no candidate work.
    /// </summary>
    protected abstract ExternalWorkItem? ParseVerifiedCore(string verifiedBody);
}
