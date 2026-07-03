using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>Sandbox provider that runs a scripted exec handler and counts clones.</summary>
internal sealed class ScriptedSandboxProvider : ISandboxProvider
{
    private readonly Func<SandboxExec, SandboxExecResult> _onExec;
    public int CreateCount { get; private set; }
    public Func<Exception>? ThrowOnCreate { get; set; }

    public ScriptedSandboxProvider(Func<SandboxExec, SandboxExecResult> onExec) => _onExec = onExec;

    public string Name => "scripted";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        CreateCount++;
        if (ThrowOnCreate is not null) throw ThrowOnCreate();
        return Task.FromResult<ISandbox>(new ScriptedSandbox(_onExec));
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

    private sealed class ScriptedSandbox : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec;
        public ScriptedSandbox(Func<SandboxExec, SandboxExecResult> onExec) => _onExec = onExec;
        public string Id => "scripted-sandbox";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(_onExec(exec));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// In-VM smoke gate stub that records every baselineRef the router forwards and
/// reports every agent as available, so routing proceeds normally. Used to assert
/// that a caller pinned the work item's <see cref="WorkItem.BaselineImageRef"/>
/// before the router gated on it.
/// </summary>
internal sealed class RecordingInVmSmokeGate : IInVmSmokeGate
{
    public List<string?> SeenBaselineRefs { get; } = [];
    public List<InVmSmokeSandboxTarget> SeenTargets { get; } = [];
    public bool Enabled => true;

    public Task<AgentAvailability> EnsureAvailableAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
    {
        SeenBaselineRefs.Add(target.BaselineRef);
        SeenTargets.Add(target);
        return Task.FromResult(new AgentAvailability(true, null, null));
    }

    public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;

    public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct) =>
        Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));
}

/// <summary>
/// In-VM smoke gate that corroborates a stdout-only auth-required detection:
/// its forced probe marks the agent auth-required on the attached registry
/// (mirroring what the real <c>InVmSmokeProber</c> does when it observes an
/// auth/login prompt in-VM), so the pipeline's corroboration check sees a
/// positive signal and escalates to the fleet-wide bench. Passing an explicit
/// non-auth <paramref name="forcedAvailability"/> models the non-corroborating
/// path (transient / passing probe): the forced probe reports that availability
/// and does NOT mark auth-required.
/// </summary>
internal sealed class AuthCorroboratingInVmSmokeGate : IInVmSmokeGate
{
    private readonly AgentAvailability _forcedAvailability;
    private readonly bool _corroboratesAuth;
    private IAgentAuthAvailabilityRegistry? _authAvailability;
    private IWebhookDispatcher? _webhooks;

    public AuthCorroboratingInVmSmokeGate(AgentAvailability? forcedAvailability = null)
    {
        _forcedAvailability = forcedAvailability
            ?? new AgentAvailability(false, "smoke probe failed [persistent]: credential login required", null);
        _corroboratesAuth = forcedAvailability is null;
    }

    /// <summary>
    /// Wires the auth registry + webhook dispatcher the gate publishes through.
    /// Called post-construction because the gate is usually built before the
    /// registry/dispatcher exist.
    /// </summary>
    public void AttachAuthRegistry(
        IAgentAuthAvailabilityRegistry authAvailability,
        IWebhookDispatcher webhooks)
    {
        _authAvailability = authAvailability;
        _webhooks = webhooks;
    }

    public int ForceProbeCalls { get; private set; }
    public List<InVmSmokeSandboxTarget> ForceProbeTargets { get; } = [];
    public bool Enabled => true;

    public Task<AgentAvailability> EnsureAvailableAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
        => Task.FromResult(new AgentAvailability(true, null, null));

    public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;

    public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct)
    {
        ForceProbeCalls++;
        MarkAuthIfNeeded(kind);
        return Task.FromResult<AgentAvailability?>(_forcedAvailability);
    }

    public Task<AgentAvailability?> ForceProbeAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
    {
        ForceProbeCalls++;
        ForceProbeTargets.Add(target);
        MarkAuthIfNeeded(kind);
        return Task.FromResult<AgentAvailability?>(_forcedAvailability);
    }

    private void MarkAuthIfNeeded(AgentKind kind)
    {
        if (!_corroboratesAuth || _authAvailability is null) return;
        var reason = _forcedAvailability.Reason ?? "in-VM smoke detected auth/login prompt";
        var transition = _authAvailability.MarkAuthRequired(kind, reason);
        if (transition is { PreviouslyExcluded: false, NowExcluded: true } && _webhooks is not null)
        {
            _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "agent.smoke_failed",
                Details = new AgentSmokeFailedDetails
                {
                    AgentKind = kind.Value,
                    Reason = reason,
                    Category = SmokeFailureCategory.Persistent,
                },
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}

/// <summary>Baseline resolver returning a fixed ref; can be made to throw.</summary>
internal sealed class StubBaselineResolver : IBaselineImageResolver, IBaselineImageProvisioner
{
    public string? Ref { get; set; }
    public bool ThrowOnResolve { get; set; }

    public StubBaselineResolver(string? r) => Ref = r;

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        if (ThrowOnResolve) throw new InvalidOperationException("baseline resolve failed");
        return Ref;
    }

    public Task<string?> EnsureBaselineImageAsync(
        string profileName,
        SandboxProfileFlavor flavor,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        if (ThrowOnResolve) throw new InvalidOperationException("baseline resolve failed");
        return Task.FromResult(pinnedBaselineRef ?? Ref);
    }

    public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);

    public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
}
