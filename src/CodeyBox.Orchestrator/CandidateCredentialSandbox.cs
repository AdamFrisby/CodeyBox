using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Adds one resolver candidate's allowlisted direct credential variables to
/// commands executed through this view of a shared conflict sandbox. The
/// underlying sandbox spec contains no candidate environment secrets, so a
/// candidate process cannot observe credentials belonging to later routes.
/// </summary>
internal sealed class CandidateCredentialSandbox : ISandboxDecorator
{
    private readonly IReadOnlyDictionary<string, string> _directCredentialEnvironment;

    private CandidateCredentialSandbox(
        ISandbox innerSandbox,
        IReadOnlyDictionary<string, string> directCredentialEnvironment)
    {
        InnerSandbox = innerSandbox;
        _directCredentialEnvironment = directCredentialEnvironment;
    }

    public ISandbox InnerSandbox { get; }
    public string Id => InnerSandbox.Id;
    public SandboxAgentOutputTransportKind AgentOutputTransportKind => InnerSandbox.AgentOutputTransportKind;
    public SandboxBatchLaunchMode BatchLaunchMode => InnerSandbox.BatchLaunchMode;
    public SandboxResourceMetrics? ResourceMetrics => InnerSandbox.ResourceMetrics;

    public static ISandbox Create(
        ISandbox sandbox,
        AgenticConflictResolverCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Credential is not { } credential)
            return sandbox;
        if (credential.Agent != candidate.Runner.Kind)
        {
            throw new AgentCredentialScopeException(
                candidate.Runner.Kind,
                $"credential belongs to agent '{credential.Agent.Value}'");
        }
        if (candidate.Runner is not IAgentCredentialEnvironmentPolicy policy)
        {
            if (credential.EnvironmentVariables.Count == 0)
                return sandbox;
            throw new AgentCredentialScopeException(
                candidate.Runner.Kind,
                "runner does not declare a credential environment allowlist");
        }

        var directEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in credential.EnvironmentVariables)
        {
            SandboxEnvironmentVariablePolicy.ValidateForSandboxEnvironment(name, nameof(credential));
            if (value.Contains('\0'))
            {
                throw new AgentCredentialScopeException(
                    candidate.Runner.Kind,
                    $"credential environment variable '{name}' contains a NUL byte");
            }
            if (value.Length > SandboxConventions.CredentialsTmpfsBytes)
            {
                throw new AgentCredentialScopeException(
                    candidate.Runner.Kind,
                    $"credential environment variable '{name}' exceeds the size limit");
            }

            if (policy.FileBackedCredentialEnvironmentVariables.Contains(name))
                continue;
            if (!policy.DirectCredentialEnvironmentVariables.Contains(name))
            {
                throw new AgentCredentialScopeException(
                    candidate.Runner.Kind,
                    $"credential environment variable '{name}' is not allowlisted for this agent");
            }
            directEnvironment.Add(name, value);
        }

        return directEnvironment.Count == 0
            ? sandbox
            : new CandidateCredentialSandbox(sandbox, directEnvironment);
    }

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        if (_directCredentialEnvironment.Count == 0)
            return InnerSandbox.ExecAsync(exec, ct);

        var environment = exec.ExtraEnvironment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(exec.ExtraEnvironment, StringComparer.Ordinal);
        foreach (var (name, value) in _directCredentialEnvironment)
            environment[name] = value;
        return InnerSandbox.ExecAsync(exec with
        {
            ExtraEnvironment = environment,
            EnvironmentContainsSecrets = true,
        }, ct);
    }

    public Task KillActiveExecsAsync(CancellationToken ct = default) =>
        InnerSandbox.KillActiveExecsAsync(ct);

    public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) =>
        InnerSandbox.GetScreenshotAsync(ct);

    public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default) =>
        InnerSandbox.SynthesizeInputAsync(events, ct);

    public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(
        int x,
        int y,
        CancellationToken ct = default) =>
        InnerSandbox.GetAccessibilityAtPointAsync(x, y, ct);

    public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default) =>
        InnerSandbox.GetAccessibilityTreeJsonAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class AgentCredentialScopeException : Exception
{
    public AgentCredentialScopeException(AgentKind agent, string reason)
        : base($"Resolver credential scope for agent '{agent.Value}' is invalid: {reason}")
    {
        Agent = agent;
    }

    public AgentKind Agent { get; }
}
