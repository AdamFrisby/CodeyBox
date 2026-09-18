using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// The single seam every path that replaces the executing agent runner after
/// sandbox creation must pass through. Two defect shapes motivate it:
///
/// <list type="bullet">
/// <item>Direct credential environment variables are baked into a sandbox
/// spec at creation time for exactly one agent kind. A runner swapped in
/// afterwards that never had its own credential materialised into the
/// environment it actually executes with runs unauthenticated and 401s —
/// which the auth-failure detector then misreports as "agent requires
/// re-authentication" even though the credential itself is healthy.
/// </item>
/// <item>A swap that cannot work is not a fallback: dispatching an agent
/// whose credential belongs to another agent or fails the runner's own
/// environment classification is a guaranteed failure. The swap must be
/// refused and the original failure kept instead.
/// </item>
/// </list>
///
/// <para>Both the mid-iteration quota-fallback path
/// (<c>PipelineRunner.InvokeAgentWithQuotaFallbackAsync</c>) and the shared-
/// sandbox conflict-resolver path (<see cref="AgenticConflictResolver"/>)
/// assess every incoming runner through <see cref="AssessSwitch"/> and scope
/// shared sandboxes through <see cref="ScopeSandbox"/>, so the next path
/// that swaps runners inherits the same behaviour instead of reintroducing
/// the bug a third time. The fallback path additionally surrenders the warm
/// reusable sandbox on every swap: direct credential variables are baked
/// into the sandbox spec at creation, so a reused sandbox would still carry
/// the exhausted member's environment no matter what the new spec says.
/// </para>
///
/// <para>Credential exposure is unchanged by this gate: the incoming agent is
/// assessed against — and later executed with — its own credential only. The
/// existing agent-match rule stays; the gate re-evaluates the match against
/// the agent that will actually run. Refusal reasons carry variable names
/// and runner identities only, never secret values.</para>
/// </summary>
internal static class AgentRunnerSwitchGate
{
    /// <summary>
    /// Upper bound on scoped credential environment names. Matches the
    /// sandbox exec cap on unset variables so a scope can always be applied.
    /// </summary>
    public const int MaximumScopedCredentialEnvironmentVariables =
        SandboxExec.MaximumEnvironmentVariablesToUnset;

    /// <summary>
    /// Outcome of assessing one incoming runner. <see cref="Allowed"/> is
    /// true only when the runner may be dispatched with
    /// <see cref="Credential"/> — a null credential is always allowed (there
    /// is nothing to materialise; runtime auth detection owns the outcome).
    /// A refused assessment carries a short operator-safe
    /// <see cref="RefusalReason"/> naming the runner and the materialisation
    /// defect — never a secret value.
    /// </summary>
    public sealed record SwitchAssessment(
        bool Allowed,
        AgentCredential? Credential,
        string? RefusalReason);

    /// <summary>
    /// Decides whether <paramref name="incomingRunner"/> may be dispatched
    /// with <paramref name="incomingCredential"/>. Pure except for the
    /// runner's own credential-environment classification (the same
    /// <c>SandboxEnvironmentVariablePolicy</c> check sandbox-spec building
    /// applies), so it never admits a swap the execution environment would
    /// reject — or silently run unauthenticated — later.
    /// </summary>
    public static SwitchAssessment AssessSwitch(
        IAgentRunner incomingRunner,
        AgentCredential? incomingCredential)
    {
        ArgumentNullException.ThrowIfNull(incomingRunner);
        var kind = incomingRunner.Kind;

        if (incomingCredential is null)
        {
            // No credential to materialise. This is not a refusal: runners
            // without credential environment (test doubles, local-only CLIs)
            // need nothing, and runners WITH declared environment may still
            // authenticate from image-baked or ambient sandbox state — the
            // resolver and fallback paths both historically dispatch such
            // candidates and let runtime auth detection own the outcome. A
            // 401 that follows a swap is reclassified by
            // ToPostSwapInfrastructureFailure instead of refused here, so a
            // missing credential can never surface as "requires
            // re-authentication" after a runner change.
            return new SwitchAssessment(Allowed: true, Credential: null, RefusalReason: null);
        }

        if (incomingCredential.Agent != kind)
        {
            return new SwitchAssessment(
                Allowed: false,
                Credential: incomingCredential,
                RefusalReason:
                    $"credential belongs to agent '{incomingCredential.Agent.Value}', " +
                    $"not '{kind.Value}'");
        }

        try
        {
            _ = SandboxEnvironmentVariablePolicy.SelectDirectCredentialEnvironment(
                incomingCredential,
                incomingRunner,
                nameof(AgentCredential.EnvironmentVariables));
        }
        catch (ArgumentException ex)
        {
            return new SwitchAssessment(
                Allowed: false,
                Credential: incomingCredential,
                RefusalReason:
                    $"credential for agent '{kind.Value}' cannot be materialised: {ex.Message}");
        }

        return new SwitchAssessment(Allowed: true, Credential: incomingCredential, RefusalReason: null);
    }

    /// <summary>
    /// Throws <see cref="AgentCredentialScopeException"/> when
    /// <paramref name="credential"/> may not be used with
    /// <paramref name="runner"/>. Same check as <see cref="AssessSwitch"/>
    /// in throwing form, for call sites (credential-file staging) that
    /// historically fail the candidate with an exception rather than a
    /// refusal record.
    /// </summary>
    public static void ValidateCredentialScope(IAgentRunner runner, AgentCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (credential is { } cred && cred.Agent != runner.Kind)
        {
            throw new AgentCredentialScopeException(
                runner.Kind,
                $"credential belongs to agent '{cred.Agent.Value}'");
        }
    }

    /// <summary>
    /// Collects the union of credential environment names across already-
    /// assessed candidates so a shared sandbox can strip every credential
    /// name before re-adding only the current candidate's direct values
    /// (see <see cref="ScopeSandbox"/>). Candidates are pre-validated pairs;
    /// entries with no environment variables contribute nothing. Throws only
    /// for a null entry (programming error) or an aggregate over the sandbox
    /// exec unset cap (which would make the scope unappliable).
    /// </summary>
    public static IReadOnlySet<string> CollectCredentialEnvironmentScope(
        IEnumerable<(IAgentRunner Runner, AgentCredential? Credential)> assessed,
        string paramName)
    {
        ArgumentNullException.ThrowIfNull(assessed);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (runner, credential) in assessed)
        {
            if (runner is null)
                throw new ArgumentException("Agent runner entries cannot contain null entries.", paramName);
            if (credential is not { EnvironmentVariables.Count: > 0 })
                continue;
            _ = SandboxEnvironmentVariablePolicy.SelectDirectCredentialEnvironment(
                credential,
                runner,
                nameof(AgentCredential.EnvironmentVariables));

            foreach (var name in credential.EnvironmentVariables.Keys)
            {
                names.Add(name);
                if (names.Count > MaximumScopedCredentialEnvironmentVariables)
                {
                    throw new ArgumentException(
                        $"Agent candidates cannot declare more than {MaximumScopedCredentialEnvironmentVariables} credential environment variables in aggregate.",
                        paramName);
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Scopes a shared <paramref name="sandbox"/> to one assessed candidate:
    /// every scoped credential name is removed from each launched process and
    /// only the candidate's declared direct values are re-added. File-backed
    /// values stay confined to the runner's stdin materialisation path even
    /// when an older caller provisioned them in the sandbox's base
    /// environment. Returns the sandbox untouched when the scope is empty.
    /// </summary>
    public static ISandbox ScopeSandbox(
        ISandbox sandbox,
        IAgentRunner runner,
        AgentCredential? credential,
        IReadOnlySet<string> credentialEnvironmentNames)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(credentialEnvironmentNames);
        if (credentialEnvironmentNames.Count == 0)
            return sandbox;

        var directEnvironment = credential is { } cred
            ? SandboxEnvironmentVariablePolicy.SelectDirectCredentialEnvironment(
                cred,
                runner,
                nameof(AgentCredential.EnvironmentVariables))
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new AgentCredentialScopedSandbox(
            sandbox,
            credentialEnvironmentNames,
            directEnvironment);
    }

    /// <summary>
    /// Reclassifies an authentication failure observed on the first attempt
    /// immediately after a runner swap as infrastructure. The swapped-in
    /// agent was assessed as materialisable before dispatch, so a 401 on its
    /// very first attempt far more likely means the swap executed without
    /// the agent's credential material than a simultaneously-expired
    /// credential — and reporting it as "agent requires re-authentication"
    /// sends the operator to check a healthy credential while failing the
    /// item as though the agent needed re-auth. The original message
    /// (already redacted upstream) is preserved verbatim after the swap
    /// context prefix, and the original exception is kept as
    /// <see cref="Exception.InnerException"/> so the 401 evidence is not
    /// lost. Auth failures with no preceding swap are untouched: a genuinely
    /// expired credential still fails the item as
    /// <see cref="AgentAuthRequiredException"/>.
    /// </summary>
    public static AgentInfrastructureFailureException ToPostSwapInfrastructureFailure(
        AgentAuthRequiredException authFailure,
        string phase)
    {
        ArgumentNullException.ThrowIfNull(authFailure);
        return new AgentInfrastructureFailureException(
            authFailure.Agent,
            phase,
            $"Agent '{authFailure.Agent.Value}' reported provider authentication failure " +
            $"immediately after a mid-iteration runner swap in phase '{phase}'; " +
            "treating as infrastructure (the swapped-in agent likely executed without " +
            "its own credentials) rather than an agent re-authentication requirement: " +
            authFailure.Message,
            authFailure);
    }
}

/// <summary>
/// Scopes a shared sandbox to one agent-switch candidate. Every non-current
/// credential name is removed from each launched process; only the current
/// candidate's declared direct values survive. File-backed values therefore
/// remain confined to the stdin materialisation path even when an older
/// caller accidentally provisioned them in the sandbox's base environment.
/// </summary>
internal sealed class AgentCredentialScopedSandbox : ISandboxDecorator
{
    private readonly ISandbox _inner;
    private readonly IReadOnlySet<string> _credentialEnvironmentNames;
    private readonly IReadOnlyDictionary<string, string> _directEnvironment;

    public AgentCredentialScopedSandbox(
        ISandbox inner,
        IReadOnlySet<string> credentialEnvironmentNames,
        IReadOnlyDictionary<string, string> directEnvironment)
    {
        _inner = inner;
        _credentialEnvironmentNames = credentialEnvironmentNames;
        _directEnvironment = directEnvironment;
    }

    public ISandbox InnerSandbox => _inner;
    public string Id => _inner.Id;
    public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
    public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;
    public SandboxResourceMetrics? ResourceMetrics => _inner.ResourceMetrics;

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        var environment = exec.ExtraEnvironment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(exec.ExtraEnvironment, StringComparer.Ordinal);
        foreach (var (name, value) in _directEnvironment)
            environment[name] = value;

        var removals = exec.EnvironmentVariablesToUnset.ToHashSet(StringComparer.Ordinal);
        foreach (var name in _credentialEnvironmentNames)
        {
            if (!_directEnvironment.ContainsKey(name))
                removals.Add(name);
        }
        if (removals.Count > SandboxExec.MaximumEnvironmentVariablesToUnset)
        {
            throw new ArgumentException(
                $"Candidate credential scope cannot unset more than {SandboxExec.MaximumEnvironmentVariablesToUnset} environment variables.",
                nameof(exec));
        }

        return _inner.ExecAsync(exec with
        {
            ExtraEnvironment = environment.Count == 0 ? null : environment,
            EnvironmentVariablesToUnset = removals.Order(StringComparer.Ordinal).ToArray(),
            EnvironmentContainsSecrets = exec.EnvironmentContainsSecrets || _directEnvironment.Count > 0,
        }, ct);
    }

    public Task SyncStateToHostAsync(CancellationToken ct = default) =>
        _inner.SyncStateToHostAsync(ct);

    public Task KillActiveExecsAsync(CancellationToken ct = default) =>
        _inner.KillActiveExecsAsync(ct);

    public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) =>
        _inner.GetScreenshotAsync(ct);

    public Task SynthesizeInputAsync(
        IReadOnlyList<SandboxInputEvent> events,
        CancellationToken ct = default) =>
        _inner.SynthesizeInputAsync(events, ct);

    public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(
        int x,
        int y,
        CancellationToken ct = default) =>
        _inner.GetAccessibilityAtPointAsync(x, y, ct);

    public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default) =>
        _inner.GetAccessibilityTreeJsonAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
