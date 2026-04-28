namespace CodeyBox.Core;

/// <summary>
/// A managed project: an upstream git repo, the auditor configuration
/// applied to its work items, and per-project defaults. Resolved at work-
/// item-pickup time from <see cref="IProjectRepository"/>.
///
/// Projects are independent: per-project upstream creds, per-project
/// auditor list, per-project agent default. The orchestrator pipeline
/// reads the project at the start of each work item and uses its config
/// for every phase (work, audit, merge, upstream).
/// </summary>
public sealed record Project
{
    public required ProjectId Id { get; init; }

    /// <summary>Human-readable name for logs and the API.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Origin git URL the host bare repo is seeded from on first reference,
    /// and the natural target of the configured upstream.
    /// </summary>
    public required string RepositoryUrl { get; init; }

    /// <summary>Default branch for new work items. May be overridden per-item.</summary>
    public string? DefaultBaseBranch { get; init; }

    /// <summary>Default agent for new work items. May be overridden per-item.</summary>
    public AgentKind DefaultAgent { get; init; } = AgentKind.Claude;

    /// <summary>
    /// Where to push merged work. Each project has its own upstream — the
    /// orchestrator resolves and instantiates the matching IUpstreamRemote
    /// at pipeline time so per-project tokens never cross project boundaries.
    /// </summary>
    public ProjectUpstream Upstream { get; init; } = ProjectUpstream.Noop;

    /// <summary>Audit configuration. Empty list disables the audit phase for this project.</summary>
    public ProjectAudit Audit { get; init; } = new();

    /// <summary>
    /// Per-phase host-enforced network profile selection. Each profile name
    /// references an entry in the sandbox provider's
    /// <c>NetworkProfiles</c> map (which maps logical names to host bridge
    /// names set up by the operator's setup-host-networks.sh).
    ///
    /// Per-project so a project whose tests need network access at merge
    /// time can grant it; another project's merge phase can stay isolated.
    /// </summary>
    public ProjectNetworkProfiles NetworkProfiles { get; init; } = new();
}

/// <summary>
/// Network profile names, one per pipeline phase. Each maps via the
/// sandbox provider's profile map to a host bridge with its own egress
/// allowlist enforced by host-side nftables.
///
/// <para>Null for any phase → that phase's sandbox is launched with
/// no host-enforced egress profile. The Multipass provider falls back
/// to its default network (Multipass's `mpqemubr0`), which
/// <c>scripts/setup-host-networks.sh</c> blocks at the host — so an
/// unset profile means the sandbox effectively has no internet, which
/// will break any phase that needs the LLM API. Populate every phase
/// the project actually runs.</para>
///
/// <para>Phases:</para>
/// <list type="bullet">
///   <item><b>Work</b>: agent does the initial change. Needs LLM API.</item>
///   <item><b>Rework</b>: agent fixes audit findings. Same network needs as work.</item>
///   <item><b>AuditAgent</b>: LLM-driven auditors (architecture, security review, …). Needs LLM API.</item>
///   <item><b>AuditTool</b>: tool-only auditors (linters, scanners). Should be isolated — no LLM API access lives in this sandbox.</item>
///   <item><b>Merge</b>: agent resolves merge conflicts. Often needs LLM API; may also need network if your tests reach external services.</item>
/// </list>
/// </summary>
public sealed record ProjectNetworkProfiles
{
    public string? Work { get; init; }
    public string? Rework { get; init; }
    public string? AuditAgent { get; init; }
    public string? AuditTool { get; init; }
    public string? Merge { get; init; }
}

/// <summary>
/// Per-project upstream config. <see cref="TokenEnvVar"/> lets each project
/// have its own credential — the orchestrator never holds tokens in config,
/// only the env var name.
/// </summary>
public sealed record ProjectUpstream
{
    public string Kind { get; init; } = "noop";
    public string? GitHubOwner { get; init; }
    public string? GitHubRepository { get; init; }
    public string? GenericUrl { get; init; }
    public string? TokenEnvVar { get; init; }

    public static ProjectUpstream Noop { get; } = new();
}

/// <summary>
/// Per-project audit config. <see cref="Languages"/> and <see cref="AuditTypes"/>
/// expand into preset auditor bundles at composition time. <see cref="Custom"/>
/// is a free-form list of additional auditor descriptors.
/// </summary>
public sealed record ProjectAudit
{
    public int MaxIterations { get; init; } = 10;
    public AuditSeverity FailingSeverity { get; init; } = AuditSeverity.Error;
    public TimeSpan PerIterationTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public bool StopOnFirstFailure { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<string> AuditTypes { get; init; } = [];
    public IReadOnlyList<CustomAuditorDescriptor> Custom { get; init; } = [];
}

/// <summary>
/// Free-form auditor description. The composer picks the matching factory
/// (shell, llm, diff-pattern) based on <see cref="Kind"/> and applies the
/// remaining fields. Lets operators express one-off auditors in config
/// without writing code.
/// </summary>
public sealed record CustomAuditorDescriptor
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public IReadOnlyList<string> Argv { get; init; } = [];
    public string? ReviewFocus { get; init; }
    public IReadOnlyList<DiffPatternDescriptor> Patterns { get; init; } = [];
}

public sealed record DiffPatternDescriptor
{
    public required string Description { get; init; }
    public required string Regex { get; init; }
}
