using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Derives the agent kinds an executor host can actually run: every composed
/// <see cref="IAgentRunner"/> whose <see cref="ICredentialProvider"/>
/// currently yields a credential. Registration declares this derived set, not
/// an operator assertion, so placement never routes an agent class to a host
/// that cannot authenticate it.
///
/// <para>Probing is per-agent and best-effort: a provider that times out or
/// throws simply excludes its agent (the host cannot run that agent right
/// now), and each probe is capped by the caller-supplied timeout so one slow
/// vault plugin cannot stall registration.</para>
/// </summary>
public sealed class ExecutorAgentAdvertiser
{
    private readonly IAgentRegistry _registry;
    private readonly ICredentialProvider _credentials;

    public ExecutorAgentAdvertiser(IAgentRegistry registry, ICredentialProvider credentials)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    /// <summary>
    /// Agent-kind values (for example <c>"claude"</c>) the host can run right
    /// now, sorted ordinal for a deterministic registration body. Resolves
    /// through the same credential chain the orchestrator uses, per agent, so
    /// each credential reaches the agent it was issued for.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRunnableAgentNamesAsync(
        TimeSpan perAgentProbeTimeout,
        CancellationToken ct = default)
    {
        if (perAgentProbeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(perAgentProbeTimeout), "Credential probe timeout must be positive.");
        var runnable = new List<string>();
        foreach (var kind in _registry.Available.OrderBy(static k => k.Value, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (await HasCredentialAsync(kind, perAgentProbeTimeout, ct).ConfigureAwait(false))
                runnable.Add(kind.Value);
        }
        return runnable;
    }

    /// <summary>
    /// Narrows a derived runnable set by the operator's allow-list. An empty
    /// allow-list means "advertise everything runnable"; otherwise only names
    /// the operator also listed are kept, compared exactly as placement
    /// matches them (<see cref="ExecutorEligibility.HoldsCredential"/>:
    /// ordinal, with <c>"*"</c> meaning all). The assertion can only narrow,
    /// never widen: a name with no credential is never advertised.
    /// </summary>
    public static IReadOnlyList<string> ApplyCredentialAllowList(
        IEnumerable<string> runnable,
        IEnumerable<string> allowList)
    {
        ArgumentNullException.ThrowIfNull(runnable);
        ArgumentNullException.ThrowIfNull(allowList);
        var allowed = allowList
            .Where(static entry => !string.IsNullOrWhiteSpace(entry))
            .Select(static entry => entry.Trim())
            .ToList();
        if (allowed.Count == 0 || allowed.Contains("*", StringComparer.Ordinal))
            return runnable.ToList();
        var keep = new HashSet<string>(allowed, StringComparer.Ordinal);
        return runnable.Where(name => keep.Contains(name)).ToList();
    }

    private async Task<bool> HasCredentialAsync(
        AgentKind kind,
        TimeSpan perAgentProbeTimeout,
        CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(perAgentProbeTimeout);
        try
        {
            return await _credentials.GetAsync(kind, probeCts.Token).ConfigureAwait(false) is not null;
        }
        catch (OperationCanceledException)
        {
            // Outer cancellation aborts registration; a probe-local timeout
            // (or a provider honouring the token) just excludes this agent.
            ct.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception)
        {
            // Best-effort advertisement by design: credential providers are an
            // open set (vault plugins throw their own exception types), and a
            // failing provider means its agent is not runnable right now, not
            // that registration must fail. Placement stays honest because the
            // agent is simply not declared.
            return false;
        }
    }
}
