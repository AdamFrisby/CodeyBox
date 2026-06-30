using System;

namespace CodeyBox.Core;

/// <summary>
/// Config bound from <c>CodeyBox:E2eExecution</c>. Sizes the cheap CPU-only
/// VM pool that runs committed e2e-replay artifacts, picks the pool
/// implementation, and pins the pre-baked baseline image the pool clones
/// per-test from.
/// </summary>
public sealed class E2eExecutionOptions
{
    /// <summary>
    /// Master switch. When false, the E2E dispatcher does NOT drain the queue
    /// (runs sit in <see cref="E2eRunStatus.Queued"/>). The API surface stays
    /// available so operators can enqueue runs ahead of enabling the pool.
    /// Default false; opt in per deployment.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Hard upper bound on concurrent leases the pool will hand out. Sized for
    /// the cheap-CPU cloud quota, NOT for the local coding fleet. Defaults to
    /// 4; raise on deployments with a larger pool. Hot-reloadable.
    /// </summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>
    /// Which pool implementation to load. <c>remote-ssh</c> fans replays out
    /// to the E2E-specific multipass-over-SSH cheap-CPU pool. <c>local</c> is
    /// accepted only for development/test deployments.
    /// </summary>
    public string PoolKind { get; set; } = "remote-ssh";

    /// <summary>
    /// Logical network profile name used for cloned sandboxes (passed through
    /// to <see cref="SandboxNetworkPolicy.ProfileName"/>). When set, the
    /// sandbox attaches to the matching host bridge — typically the
    /// app-under-test profile that allows only the in-VM HTTP service ports
    /// the runtime needs to talk to.
    /// </summary>
    public string? NetworkProfile { get; set; }

    /// <summary>
    /// Sandbox image reference the local pool's <see cref="ISandboxProvider"/>
    /// clones from. Null falls back to the orchestrator-wide
    /// <c>SandboxImageReference</c>; populate when the e2e pool runs from a
    /// separate pre-baked image (the recommended production shape — the e2e
    /// image carries the app stack already installed).
    /// </summary>
    public string? SandboxImageReference { get; set; }

    /// <summary>
    /// Optional content-hashed baseline ref. When set, the local pool pins
    /// every cloned sandbox to this image rather than re-resolving it from
    /// live config; mirrors <see cref="SandboxSpec.BaselineImageRef"/>.
    /// </summary>
    public string? BaselineImageRef { get; set; }

    /// <summary>
    /// Origins the artifact readiness URL may probe. Values are normalized as
    /// URL origins (<c>scheme://host[:port]</c>) and compared exactly before
    /// any network request is made. Defaults to the conventional app-under-test
    /// DNS name baked into E2E images; production deployments should override
    /// this with their own app origin(s).
    /// </summary>
    public IReadOnlyList<string> AllowedReadinessOrigins { get; set; } =
    [
        "http://app.local",
        "https://app.local",
    ];

    /// <summary>
    /// How often the dispatcher polls for queued runs when the pool is idle.
    /// Defaults to 1 second; raise to reduce DB churn on deployments with low
    /// enqueue cadence. Hot-reloadable.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Per-run wall-clock cap. The dispatcher cancels the run when it exceeds
    /// this; the run records as <see cref="E2eRunStatus.Error"/> with an
    /// appropriate failure kind. Defaults to 15 minutes — replays should be
    /// fast; a stuck run is almost always an infra issue.
    /// </summary>
    public TimeSpan PerRunTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Default API page size for E2E run listing endpoints.</summary>
    public const int DefaultListPageSize = 100;

    /// <summary>Strict maximum API page size for E2E run listing endpoints.</summary>
    public const int MaximumListPageSize = 500;

    /// <summary>Floor for <see cref="MaxConcurrent"/>.</summary>
    public const int MinimumMaxConcurrent = 1;

    /// <summary>
    /// Ceiling for <see cref="MaxConcurrent"/>. Set well above any plausible
    /// CPU-only-pool size (cheap cloud VMs are tens, not hundreds), but bounded
    /// so a config typo can't produce a runaway lease count.
    /// </summary>
    public const int MaximumMaxConcurrent = 512;
}
