using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Cost and token-usage recording collaborator for <see cref="PipelineRunner"/>.
/// Extracts token counts from agent output, calculates estimated USD spend, and persists
/// durable cost/usage rows. All persistence failures are swallowed with warnings so cost
/// capture never fails a pipeline phase.
/// Extracted mechanically from <see cref="PipelineRunner"/>; behavior is unchanged.
/// </summary>
internal sealed class CostUsageRecorder
{
    internal const string ElapsedFallbackMetadataSource = "elapsed_fallback";

    private readonly IWorkItemCostStore? _costStore;
    private readonly IAgentUsageStore? _usageStore;
    private readonly AgentCostCalculator? _costCalculator;
    private readonly IReadOnlyDictionary<AgentKind, IAgentCostExtractor>? _costExtractors;
    private readonly ILogger _log;

    public CostUsageRecorder(
        IWorkItemCostStore? costStore,
        IAgentUsageStore? usageStore,
        AgentCostCalculator? costCalculator,
        IReadOnlyDictionary<AgentKind, IAgentCostExtractor>? costExtractors,
        ILogger log)
    {
        _costStore = costStore;
        _usageStore = usageStore;
        _costCalculator = costCalculator;
        _costExtractors = costExtractors;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Best-effort cost summary lookup for webhook usage blocks. Returns null
    /// when the cost store is absent, no rows exist for the work item, or the
    /// read fails — usage is reported as absent in any of those cases.
    /// </summary>
    public async Task<WorkItemUsageSummary?> TryGetUsageSummaryAsync(WorkItemId id)
    {
        if (_costStore is null) return null;
        try { return await _costStore.SummariseAsync(id.ToString(), CancellationToken.None); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cost: failed to summarise usage for work item {Id}; webhook will omit usage", id);
            return null;
        }
    }

    public async Task TryRecordCompletionCostAsync(
        CheckAndActCompletionResult result,
        WorkItem item,
        string phase,
        int? iteration,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        if (_costStore is null && _usageStore is null) return;

        var snapshot = NormalizeCostSnapshot(
            new AgentCostSnapshot(
                result.Usage.InputTokens,
                result.Usage.CachedInputTokens,
                result.Usage.OutputTokens,
                result.ModelId),
            result.ModelId);

        var usd = 0m;
        if (_costCalculator is not null)
        {
            try { usd = _costCalculator.Calculate(snapshot, result.AgentKind); }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Cost: calculator threw for check-and-act completion provider '{Provider}' phase '{Phase}'; recording tokens with zero estimated cost",
                    result.Provider, phase);
            }
        }
        usd = Math.Max(0m, usd);

        if (_costStore is not null)
        {
            try
            {
                await _costStore.RecordAsync(new WorkItemCost
                {
                    Id = Guid.NewGuid().ToString(),
                    WorkItemId = item.Id.ToString(),
                    Phase = phase,
                    Iteration = iteration,
                    AgentKind = result.AgentKind.Value,
                    AgentInstanceId = item.AgentInstanceId,
                    ModelId = snapshot.ModelId,
                    InputTokens = snapshot.InputTokens,
                    CachedInputTokens = snapshot.CachedInputTokens,
                    OutputTokens = snapshot.OutputTokens,
                    EstimatedUsd = (double)usd,
                    StartedAt = startedAt,
                    EndedAt = endedAt,
                    RawMetadataJson = JsonSerializer.Serialize(new
                    {
                        source = "check_and_act_completion",
                        provider = result.Provider,
                        cacheHit = result.Usage.CacheHit,
                    }),
                    HasExtractedTokenUsage = true,
                }, CancellationToken.None);

                var model = snapshot.ModelId ?? "(default)";
                var agentTag = new KeyValuePair<string, object?>("agent.kind", result.AgentKind.Value);
                var agentInstanceTag = new KeyValuePair<string, object?>("agent.instance", item.AgentInstanceId ?? result.AgentKind.Value);
                var modelTag = new KeyValuePair<string, object?>("model", model);
                CodeyBoxMeters.AgentTokens.Add(snapshot.InputTokens, agentTag, agentInstanceTag, modelTag,
                    new KeyValuePair<string, object?>("token_type", "input"));
                CodeyBoxMeters.AgentTokens.Add(snapshot.CachedInputTokens, agentTag, agentInstanceTag, modelTag,
                    new KeyValuePair<string, object?>("token_type", "cached_input"));
                CodeyBoxMeters.AgentTokens.Add(snapshot.OutputTokens, agentTag, agentInstanceTag, modelTag,
                    new KeyValuePair<string, object?>("token_type", "output"));
                CodeyBoxMeters.AgentCostUsd.Add((double)usd, agentTag, agentInstanceTag, modelTag);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cost: failed to persist completion row for work item {Id} phase '{Phase}'",
                    item.Id, phase);
            }
        }

        if (_usageStore is not null)
        {
            try
            {
                await _usageStore.RecordAsync(
                    BuildUsageEvent(result.AgentKind, item.AgentInstanceId, result.ModelId, snapshot, usd, item.Id, endedAt, phase, startedAt),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Usage: failed to persist completion event for work item {Id} phase '{Phase}'",
                    item.Id, phase);
            }
        }
    }

    /// <summary>
    /// Best-effort cost capture: extracts token counts from agent output, calculates
    /// estimated USD, and persists a cost row. Any failure is swallowed with a warning
    /// so cost capture never aborts a pipeline phase.
    /// </summary>
    public async Task TryRecordCostAsync(
        string? stdout,
        string? stderr,
        AgentKind agentKind,
        string? agentInstanceId,
        WorkItemId workItemId,
        string phase,
        int? iteration,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? dispatchModelId)
    {
        if (_costStore is null && _usageStore is null) return;

        AgentCostSnapshot? snapshot;
        if (_costExtractors is not null && _costExtractors.TryGetValue(agentKind, out var extractor))
        {
            try { snapshot = extractor.TryExtract(stdout, stderr); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cost: extractor threw for agent '{Agent}' phase '{Phase}'; recording elapsed fallback",
                    agentKind.Value, phase);
                snapshot = null;
            }
        }
        else
        {
            snapshot = null;
        }

        var usedElapsedFallback = snapshot is null;
        snapshot ??= new AgentCostSnapshot(
            InputTokens: 0,
            CachedInputTokens: 0,
            OutputTokens: 0,
            ModelId: dispatchModelId);
        snapshot = NormalizeCostSnapshot(snapshot, dispatchModelId);

        var usd = 0m;
        if (!usedElapsedFallback && _costCalculator is not null)
        {
            try { usd = _costCalculator.Calculate(snapshot, agentKind); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cost: calculator threw for agent '{Agent}' phase '{Phase}'; recording tokens with zero estimated cost",
                    agentKind.Value, phase);
            }
        }
        usd = Math.Max(0m, usd);

        if (_costStore is not null)
        {
            try
            {
                await _costStore.RecordAsync(new WorkItemCost
                {
                    Id = Guid.NewGuid().ToString(),
                    WorkItemId = workItemId.ToString(),
                    Phase = phase,
                    Iteration = iteration,
                    AgentKind = agentKind.Value,
                    AgentInstanceId = agentInstanceId,
                    ModelId = snapshot.ModelId,
                    InputTokens = snapshot.InputTokens,
                    CachedInputTokens = snapshot.CachedInputTokens,
                    OutputTokens = snapshot.OutputTokens,
                    EstimatedUsd = (double)usd,
                    StartedAt = startedAt,
                    EndedAt = endedAt,
                    RawMetadataJson = usedElapsedFallback
                        ? JsonSerializer.Serialize(new { source = ElapsedFallbackMetadataSource })
                        : "{}",
                    HasExtractedTokenUsage = !usedElapsedFallback,
                }, CancellationToken.None);

                // Emit the same accounting as OTel counters so dashboards align with
                // the per-work-item cost rows (no double-counting — one emit per row).
                var model = snapshot.ModelId ?? "(default)";
                var agentTag = new KeyValuePair<string, object?>("agent.kind", agentKind.Value);
                var agentInstanceTag = new KeyValuePair<string, object?>("agent.instance", agentInstanceId ?? agentKind.Value);
                var modelTag = new KeyValuePair<string, object?>("model", model);
                CodeyBoxMeters.AgentTokens.Add(snapshot.InputTokens, agentTag, agentInstanceTag, modelTag,
                    new KeyValuePair<string, object?>("token_type", "input"));
                CodeyBoxMeters.AgentTokens.Add(snapshot.CachedInputTokens, agentTag, agentInstanceTag, modelTag,
                    new KeyValuePair<string, object?>("token_type", "cached_input"));
                CodeyBoxMeters.AgentTokens.Add(snapshot.OutputTokens, agentTag, agentInstanceTag, modelTag,
                    new KeyValuePair<string, object?>("token_type", "output"));
                CodeyBoxMeters.AgentCostUsd.Add((double)usd, agentTag, agentInstanceTag, modelTag);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cost: failed to persist row for work item {Id} phase '{Phase}'",
                    workItemId, phase);
            }
        }

        if (_usageStore is not null)
        {
            try
            {
                await _usageStore.RecordAsync(
                    BuildUsageEvent(agentKind, agentInstanceId, dispatchModelId, snapshot, usd, workItemId, endedAt, phase, startedAt),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Usage: failed to persist event for work item {Id} phase '{Phase}'",
                    workItemId, phase);
            }
        }
    }

    internal static AgentCostSnapshot NormalizeCostSnapshot(AgentCostSnapshot snapshot, string? dispatchModelId) => new(
        InputTokens: Math.Max(0, snapshot.InputTokens),
        CachedInputTokens: Math.Max(0, snapshot.CachedInputTokens),
        OutputTokens: Math.Max(0, snapshot.OutputTokens),
        ModelId: ResolveCostRowModelId(snapshot.ModelId, dispatchModelId));

    internal static AgentCostSnapshot ClampCostSnapshot(AgentCostSnapshot snapshot, string? dispatchModelId = null) =>
        NormalizeCostSnapshot(snapshot, dispatchModelId);

    internal static string? ResolveCostRowModelId(string? extractedModelId, string? dispatchModelId)
    {
        if (!string.IsNullOrWhiteSpace(extractedModelId))
            return extractedModelId;

        return string.IsNullOrWhiteSpace(dispatchModelId) ? null : dispatchModelId;
    }

    /// <summary>
    /// Builds the durable usage-accounting row for one agent invocation.
    /// <para>
    /// The row is keyed by the DISPATCHED model id, never the model id parsed from
    /// agent output (<see cref="AgentCostSnapshot.ModelId"/>). The budget gate sums
    /// spend filtered on the operator-configured <c>member.ModelId</c> — the same
    /// value used to route/dispatch. Persisting under the parsed model id (which is
    /// null on many human-readable footers and a provider-supplied string on JSON
    /// paths) would store spend in a different or NULL bucket than the one being
    /// gated, so the gate's SUM returns zero used and AvailablePct stays at 100%
    /// while real cost accrues — a fail-open bypass of the operator spend cap.
    /// <paramref name="dispatchModelId"/> == <c>member.ModelId</c> guarantees the
    /// bucket the gate reads is the bucket spend lands in.
    /// </para>
    /// <para>
    /// Token counts and cost come from parsing untrusted agent stdout/stderr. A
    /// hostile or malformed CLI emission (e.g. <c>completion_tokens:-999999999</c>)
    /// would otherwise persist a negative legacy cost unit, deflate the budget
    /// window SUM, and keep AvailablePct artificially high — fail-open on the
    /// spend cap. Every persisted component is clamped non-negative so a bad
    /// emission can only ever over-report spend, never deflate it.
    /// </para>
    /// </summary>
    internal static AgentUsageEvent BuildUsageEvent(
        AgentKind agentKind,
        string? dispatchModelId,
        AgentCostSnapshot snapshot,
        decimal usd,
        WorkItemId workItemId,
        DateTimeOffset endedAt,
        string? phase = null,
        DateTimeOffset? startedAt = null) =>
        BuildUsageEvent(agentKind, null, dispatchModelId, snapshot, usd, workItemId, endedAt, phase, startedAt);

    internal static AgentUsageEvent BuildUsageEvent(
        AgentKind agentKind,
        string? agentInstanceId,
        string? dispatchModelId,
        AgentCostSnapshot snapshot,
        decimal usd,
        WorkItemId workItemId,
        DateTimeOffset endedAt,
        string? phase = null,
        DateTimeOffset? startedAt = null) => new()
        {
            Id = Guid.NewGuid().ToString(),
            TimeUtc = endedAt,
            AgentKind = agentKind.Value,
            AgentInstanceId = agentInstanceId,
            ModelId = dispatchModelId,
            Phase = phase,
            StartedUtc = startedAt,
            EndedUtc = endedAt,
            ElapsedMs = startedAt is { } start
                ? (long)Math.Max(0, (endedAt - start).TotalMilliseconds)
                : 0,
            InputTokens = Math.Max(0, snapshot.InputTokens),
            CachedInputTokens = Math.Max(0, snapshot.CachedInputTokens),
            OutputTokens = Math.Max(0, snapshot.OutputTokens),
            CostMicroCents = Math.Max(0L, AgentUsageEvent.UsdToMicroCents(usd)),
            WorkItemId = workItemId.ToString(),
        };
}
