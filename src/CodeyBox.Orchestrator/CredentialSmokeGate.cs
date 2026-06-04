using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Evaluates the credential smoke gate for a work item pickup. Resolves the
/// credential, selects the matching probe (if any), checks the cache, and
/// runs the probe on a cache miss.
///
/// Returns <c>null</c> if:
/// <list type="bullet">
///   <item>the gate is disabled (<c>CodeyBox:Smoke:Enabled=false</c>)</item>
///   <item>no probe is registered for the agent kind</item>
///   <item>no credential could be resolved for the agent</item>
/// </list>
///
/// Returns an <see cref="AgentSmokeResult"/> (Ok or not) otherwise.
/// Thread-safe: the underlying cache handles concurrent access.
/// </summary>
public sealed class CredentialSmokeGate
{
    private readonly ICredentialProvider _credentials;
    private readonly IReadOnlyDictionary<AgentKind, IAgentSmokeProbe> _probes;
    private readonly IAgentSmokeCache _cache;
    private readonly SmokeOptionsSnapshot _opts;
    private readonly ILogger<CredentialSmokeGate> _log;

    public bool Enabled => _opts.Enabled;

    public CredentialSmokeGate(
        ICredentialProvider credentials,
        IEnumerable<IAgentSmokeProbe> probes,
        IAgentSmokeCache cache,
        SmokeOptions opts,
        ILogger<CredentialSmokeGate> log)
        : this(credentials, probes, cache, new SmokeOptionsSnapshot(opts), log)
    {
    }

    public CredentialSmokeGate(
        ICredentialProvider credentials,
        IEnumerable<IAgentSmokeProbe> probes,
        IAgentSmokeCache cache,
        SmokeOptionsSnapshot opts,
        ILogger<CredentialSmokeGate> log)
    {
        _credentials = credentials;
        _probes = probes.ToDictionary(p => p.Kind);
        _cache = cache;
        _opts = opts;
        _log = log;
    }

    /// <summary>
    /// Returns null if the gate is disabled, no probe is registered for
    /// <paramref name="kind"/>, or no credential was resolved. Returns a
    /// cached or freshly probed <see cref="AgentSmokeResult"/> otherwise.
    /// Never throws.
    /// </summary>
    public async Task<AgentSmokeResult?> CheckAsync(AgentKind kind, CancellationToken ct)
    {
        if (!_opts.Enabled) return null;
        if (!_probes.TryGetValue(kind, out var probe)) return null;

        AgentCredential? credential;
        try
        {
            credential = await _credentials.GetAsync(kind, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Smoke gate: failed to resolve credential for {Agent}", kind.Value);
            return null;
        }

        if (credential is null) return null;

        var fingerprint = SmokeCredentialFingerprint.Compute(credential);
        if (_cache.TryGet(kind, fingerprint) is { } cached)
        {
            _log.LogDebug("Smoke gate: cache hit for agent {Agent}", kind.Value);
            return cached;
        }

        var result = await RunProbeAsync(probe, credential, ct);
        _cache.Set(kind, fingerprint, result);
        return result;
    }

    private async Task<AgentSmokeResult> RunProbeAsync(
        IAgentSmokeProbe probe, AgentCredential credential, CancellationToken ct)
    {
        try
        {
            return await probe.SmokeTestAsync(credential, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AgentSmokeResult(false, "timeout", TimeSpan.Zero, SmokeFailureCategory.Transient);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Smoke probe for {Agent} threw unexpectedly", probe.Kind.Value);
            return new AgentSmokeResult(
                false, "transient: try later", TimeSpan.Zero, SmokeFailureCategory.Transient);
        }
    }
}
