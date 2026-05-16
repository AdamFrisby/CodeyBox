using Microsoft.Extensions.Options;
using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Hosted service that, at host start, cross-checks each
/// <see cref="AgentMembership.ModelId"/> declared in
/// <c>CodeyBox:AgentClasses</c> against the provider's live model list.
///
/// <para>The probe + class router is happy to route a typoed model id; the
/// failure only surfaces hours later as cascading <c>LimitReached</c> /
/// quota errors. This validator catches the typo at startup so operators see
/// a clear log line instead of a quota-failure backlog.</para>
///
/// <para>Behavior:</para>
/// <list type="bullet">
///   <item><description>One Info per validated member: <c>"AgentClass '&lt;id&gt;' member &lt;agent&gt;/&lt;modelId&gt; validated against provider"</c>.</description></item>
///   <item><description>One Warning per unknown member naming the typoed id and the valid alternatives.</description></item>
///   <item><description>Probe HTTP errors, timeouts, and empty model lists log Warning once per agent kind and skip
///   validation for that run — the host still starts.</description></item>
///   <item><description>When <c>CodeyBox:ConfigValidation:FailOnUnknownModel=true</c>, any unknown member instead
///   throws <see cref="InvalidOperationException"/> from <see cref="StartAsync"/> so the host fails-fast.</description></item>
/// </list>
///
/// <para>Total validation budget is 10 seconds; the validator falls through to
/// Warning behavior on timeout so a slow network never blocks startup.</para>
/// </summary>
internal sealed class AgentClassConfigValidator : IHostedService
{
    // Instance (not static) so tests can override via object initializer with a
    // millisecond-scale deadline to exercise the timeout branch without sleeping
    // for the production 10s budget.
    internal TimeSpan ValidationDeadline { get; init; } = TimeSpan.FromSeconds(10);

    private readonly IOptions<CodeyBoxOptions> _options;
    private readonly IEnumerable<IAgentModelListProbe> _probes;
    private readonly ILogger<AgentClassConfigValidator> _log;

    public AgentClassConfigValidator(
        IOptions<CodeyBoxOptions> options,
        IEnumerable<IAgentModelListProbe> probes,
        ILogger<AgentClassConfigValidator> log)
    {
        _options = options;
        _probes = probes;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var opts = _options.Value;
        var classes = opts.AgentClasses;
        if (classes.Count == 0) return;

        // Group declared ModelIds by agent kind. Members without ModelId use the
        // agent's own default and aren't validated.
        var requested = new Dictionary<string, List<(string ClassId, string Agent, string ModelId)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in classes)
        {
            foreach (var m in cls.Members)
            {
                if (string.IsNullOrWhiteSpace(m.ModelId)) continue;
                if (string.IsNullOrWhiteSpace(m.Agent)) continue;
                if (!requested.TryGetValue(m.Agent, out var list))
                {
                    list = new List<(string, string, string)>();
                    requested[m.Agent] = list;
                }
                list.Add((cls.Id, m.Agent, m.ModelId!));
            }
        }
        if (requested.Count == 0) return;

        var probesByKind = new Dictionary<string, IAgentModelListProbe>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in _probes)
            probesByKind[probe.Kind.Value] = probe;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ValidationDeadline);

        var unknownModels = new List<string>();

        foreach (var (agentKey, members) in requested)
        {
            if (!probesByKind.TryGetValue(agentKey, out var probe))
            {
                _log.LogWarning(
                    "No IAgentModelListProbe registered for agent '{Agent}'; skipping ModelId validation for {Count} member(s).",
                    agentKey, members.Count);
                continue;
            }

            AgentModelListResult result;
            try
            {
                result = await probe.GetModelListAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    "Could not validate {Agent} model list (timed out after {Seconds}s); skipping validation for this run.",
                    agentKey, ValidationDeadline.TotalSeconds);
                continue;
            }
            catch (Exception ex)
            {
                // Probes are documented as non-throwing; defensively treat any
                // escape as a transient/auth failure rather than crashing the host.
                _log.LogWarning(ex,
                    "Could not validate {Agent} model list (probe threw {Type}); skipping validation for this run.",
                    agentKey, ex.GetType().Name);
                continue;
            }

            if (result.FailureReason is not null)
            {
                _log.LogWarning(
                    "Could not validate {Agent} model list ({Reason}); skipping validation for this run.",
                    agentKey, result.FailureReason);
                continue;
            }

            if (result.ModelIds.Count == 0)
            {
                _log.LogWarning(
                    "{Agent} provider returned no models; skipping validation for this run.",
                    agentKey);
                continue;
            }

            var validSet = new HashSet<string>(result.ModelIds, StringComparer.OrdinalIgnoreCase);
            foreach (var (classId, agent, modelId) in members)
            {
                if (validSet.Contains(modelId))
                {
                    _log.LogInformation(
                        "AgentClass '{ClassId}' member {Agent}/{ModelId} validated against provider",
                        classId, agent, modelId);
                }
                else
                {
                    var preview = TruncateModelList(result.ModelIds, maxChars: 200);
                    _log.LogWarning(
                        "AgentClass '{ClassId}' member {Agent}/{ModelId} NOT in provider model list (have: {ValidIds}). " +
                        "Pipeline will fail-fast or fall-open per QuotaUnknownPolicy.",
                        classId, agent, modelId, preview);
                    unknownModels.Add($"{classId}/{agent}/{modelId}");
                }
            }
        }

        if (unknownModels.Count > 0 && opts.ConfigValidation.FailOnUnknownModel)
        {
            throw new InvalidOperationException(
                "CodeyBox:ConfigValidation:FailOnUnknownModel=true and one or more AgentClass members " +
                $"declare a ModelId that the provider does not list: {string.Join(", ", unknownModels)}. " +
                "Fix the ModelId values in CodeyBox:AgentClasses (or set FailOnUnknownModel=false to downgrade to a warning).");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private static string TruncateModelList(IReadOnlyList<string> ids, int maxChars)
    {
        var joined = string.Join(",", ids);
        if (joined.Length <= maxChars) return joined;
        // Truncate on a model-id boundary so the preview is still readable.
        var sb = new System.Text.StringBuilder(maxChars + 16);
        var count = 0;
        foreach (var id in ids)
        {
            var needed = sb.Length == 0 ? id.Length : id.Length + 1;
            if (sb.Length + needed > maxChars) break;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(id);
            count++;
        }
        sb.Append(",… (");
        sb.Append(ids.Count - count);
        sb.Append(" more)");
        return sb.ToString();
    }
}
