namespace CodeyBox.Core;

/// <summary>
/// Per-project rate limits applied at pickup time. All caps default to 0 (unlimited).
/// Existing projects without a Budget section keep the zero defaults — no behaviour change.
/// </summary>
public sealed record ProjectBudget
{
    /// <summary>Max work items that can START per rolling hour. 0 = unlimited.</summary>
    public int MaxItemsPerHour { get; init; }
    /// <summary>Max work items that can START per rolling 24h. 0 = unlimited.</summary>
    public int MaxItemsPerDay { get; init; }
    /// <summary>
    /// Max work items in non-terminal, non-Queued state simultaneously for this project.
    /// 0 = unlimited (subject to global MaxConcurrentWorkers).
    /// </summary>
    public int MaxConcurrentForProject { get; init; }

    /// <summary>
    /// Maximum estimated USD spend per rolling 30-day window. 0 = unlimited.
    /// Computed from work_item_costs.estimated_usd summed across all work items
    /// belonging to this project whose started_at is within the window.
    /// </summary>
    public decimal MonthlyCostBudgetUsd { get; init; }

    /// <summary>
    /// Webhook fires at this percentage of the monthly cost budget (default 80).
    /// Set to 0 to disable the warning event. Ignored when MonthlyCostBudgetUsd = 0.
    /// </summary>
    public int CostWarningThresholdPct { get; init; } = 80;

    /// <summary>
    /// The project queue is automatically paused at this percentage (default 100).
    /// Set to 0 to disable auto-pause (webhook still fires). Ignored when MonthlyCostBudgetUsd = 0.
    /// </summary>
    public int CostHardCapPct { get; init; } = 100;

    /// <summary>
    /// When true, the project queue is automatically resumed when spend drops back below
    /// CostWarningThresholdPct. Default false; operator should manually unpause to acknowledge.
    /// </summary>
    public bool AutoResumeOnRecovery { get; init; }
}

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
    /// Default agent class for work items that don't set their own
    /// <see cref="WorkItem.AgentClassId"/>. When set, quota routing applies to all
    /// items in this project. May be overridden per-item by setting
    /// <see cref="WorkItem.AgentClassId"/> to a different class or to null.
    /// </summary>
    public string? DefaultAgentClass { get; init; }

    /// <summary>
    /// Where to push merged work. Each project has its own upstream — the
    /// orchestrator resolves and instantiates the matching IUpstreamRemote
    /// at pipeline time so per-project tokens never cross project boundaries.
    /// </summary>
    public ProjectUpstream Upstream { get; init; } = ProjectUpstream.Noop;

    /// <summary>Audit configuration. Empty list disables the audit phase for this project.</summary>
    public ProjectAudit Audit { get; init; } = new();

    /// <summary>
    /// Override the git commit author name for this project. When set, takes
    /// precedence over the host's global git identity. Falls back to the host
    /// identity, then to "CodeyBox".
    /// </summary>
    public string? GitAuthorName { get; init; }

    /// <summary>
    /// Override the git commit author email for this project. When set, takes
    /// precedence over the host's global git identity. Falls back to the host
    /// identity, then to "codeybox@local".
    /// </summary>
    public string? GitAuthorEmail { get; init; }

    /// <summary>
    /// When true, the credential smoke test is skipped for work items in this
    /// project. Use for agents whose surface is harder to probe directly
    /// (e.g. Copilot), or for projects that run on air-gapped networks where
    /// the probe would always fail transiently. Default false.
    /// </summary>
    public bool SkipCredentialSmokeTest { get; init; }

    /// <summary>
    /// Optional rate limits applied at pickup time. All caps default to 0
    /// (unlimited). Set any cap &gt; 0 to throttle this project without
    /// affecting others.
    /// </summary>
    public ProjectBudget Budget { get; init; } = new();

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

    /// <summary>
    /// Per-project changelog automation overrides. When set, these take precedence
    /// over the global <c>CodeyBox:Changelog</c> options.
    /// </summary>
    public ProjectChangelog? Changelog { get; init; }

    /// <summary>
    /// When true, agents in this project may emit structured <c>&lt;codeybox-question&gt;</c>
    /// blocks mid-work to escalate ambiguity to the operator. The work item parks
    /// at NeedsOperatorInput, fires a webhook, and waits for POST /workitems/{id}/answer.
    /// Default false: agents must make their own calls.
    /// </summary>
    public bool AllowAgentQuestions { get; init; } = false;

    /// <summary>
    /// Ordered list of credential plugin IDs this project prefers. When set,
    /// only the listed plugin IDs are included in the credential chain for this
    /// project, in the order given (between the built-in OAuth-file provider and
    /// the env-var fallback). Plugins not in this list are skipped. An empty
    /// list means "use all discovered plugins in global discovery order."
    ///
    /// <para>Example: <c>["myorg.vault-creds", "myorg.aws-ssm"]</c> — the chain
    /// tries Vault first, then AWS SSM, and falls back to env vars. A plugin
    /// installed but not listed (e.g. "myorg.1password") is not tried.</para>
    ///
    /// <para>See <c>docs/credential-plugins.md</c> for the full chain-order
    /// rationale and per-project override semantics.</para>
    /// </summary>
    public IReadOnlyList<string> CredentialProviderPriority { get; init; } = [];

    /// <summary>
    /// Release management configuration. When <see cref="ProjectReleaseConfig.Enabled"/>
    /// is false (the default) the release flow is completely inactive for this project:
    /// POSTing a work item with a <c>releaseId</c> returns 400, and no release branches
    /// are ever created. Opt in by setting Enabled=true.
    /// </summary>
    public ProjectReleaseConfig ReleaseConfig { get; init; } = new();
}

/// <summary>
/// Per-project changelog automation overrides. Unset fields fall back to the
/// global <c>ChangelogOptions</c>. See <c>docs/changelog-automation.md</c>.
/// </summary>
public sealed record ProjectChangelog
{
    /// <summary>Override the global Enabled flag for this project.</summary>
    public bool? Enabled { get; init; }
    /// <summary>Path to CHANGELOG.md within the project repo. Overrides global ChangelogPath.</summary>
    public string? ChangelogPath { get; init; }
    /// <summary>Section header format. Overrides global SectionHeaderFormat.</summary>
    public string? SectionHeaderFormat { get; init; }
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

    // GitHub-specific completion options
    /// <summary>How GitHub merges the PR. One of "merge" | "squash" | "rebase". Default "merge".</summary>
    public string MergeMethod { get; init; } = "merge";
    /// <summary>When true, merges the opened PR via the GitHub API immediately after creation.</summary>
    public bool AutoMerge { get; init; }
    /// <summary>
    /// Optional PR title template. Supports {title} (work item title) and
    /// {branch} (work branch name) placeholders. Defaults to the work item title.
    /// </summary>
    public string? PullRequestTitleTemplate { get; init; }

    /// <summary>
    /// LLM-generated PR description settings. When <see cref="ProjectPrDescription.SandboxImageReference"/>
    /// is empty the generator is skipped and the static template is used.
    /// </summary>
    public ProjectPrDescription PrDescription { get; init; } = new();

    /// <summary>
    /// Plugin-specific key/value settings passed to the upstream remote plugin
    /// named by <see cref="Kind"/>. Plugin authors document which keys they read.
    /// Accessible at runtime via
    /// <c>IUpstreamPluginHost.GetProjectUpstreamConfig(projectId)</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> PluginConfig { get; init; }
        = new Dictionary<string, string>();

    public static ProjectUpstream Noop { get; } = new();
}

/// <summary>
/// Per-project LLM-generated PR description configuration.
/// See <c>docs/git-workflow.md</c> for configuration guidance.
/// </summary>
public sealed record ProjectPrDescription
{
    /// <summary>When false the generator is skipped entirely. Default: true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Agent kind for generation, e.g. "claude". Default: "claude".</summary>
    public string GeneratorAgent { get; init; } = "claude";

    /// <summary>Optional model override forwarded to the agent runner.</summary>
    public string? GeneratorModelId { get; init; }

    /// <summary>
    /// Max UTF-8 byte size of the diff sent to the LLM. Diffs larger than this are
    /// truncated from the middle. Default: 32 768 bytes (32 KB).
    /// </summary>
    public int MaxDiffBytes { get; init; } = 32_768;

    /// <summary>
    /// Hard deadline for the generation round-trip. On expiry the static template
    /// is used. Default: 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Container / VM image reference for the generator sandbox. Must have the
    /// configured agent CLI installed. When empty the generator is disabled.
    /// </summary>
    public string SandboxImageReference { get; init; } = string.Empty;

    /// <summary>Hosts reachable from the generator sandbox. Default: Anthropic API.</summary>
    public IReadOnlyList<string> AgentAllowedHosts { get; init; } = ["api.anthropic.com"];
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

    /// <summary>
    /// Minutes of zero CPU + zero TCP-connection activity before an agent is
    /// classified as stuck and killed. -1 = inherit from
    /// <c>PipelineOptions.StuckThresholdMinutes</c>; 0 = disabled for this project.
    /// </summary>
    public int StuckThresholdMinutes { get; init; } = -1;

    /// <summary>
    /// When true, a stuck-killed work item is automatically re-queued from the
    /// same phase rather than transitioning to Failed. Capped by
    /// <see cref="MaxStuckRetries"/> to prevent infinite kill-respawn loops.
    /// </summary>
    public bool AutoRetryOnStuck { get; init; }

    /// <summary>Maximum automatic re-queues per work item due to stuck detection.</summary>
    public int MaxStuckRetries { get; init; } = 2;

    /// <summary>
    /// Number of context lines around each deterministic merge conflict hunk
    /// that the conflict resolver may change. Defaults to 5.
    /// </summary>
    public int MergeScopeBufferLines { get; init; } = 5;

    public IReadOnlyList<string> Languages { get; init; } = [];
    public bool LanguagesConfigured { get; init; }
    public IReadOnlyList<string> AuditTypes { get; init; } = [];
    public IReadOnlyDictionary<string, ProjectAuditTypeOverride> AuditTypeOverrides { get; init; }
        = new Dictionary<string, ProjectAuditTypeOverride>(StringComparer.OrdinalIgnoreCase);
    public string? LlmPromptFrameTemplate { get; init; }
    public IReadOnlyList<CustomAuditorDescriptor> Custom { get; init; } = [];

    /// <summary>
    /// Agent runner used for LLM-based auditors (security:llm-review,
    /// completeness:llm-review, cheating:llm-review, etc.). Defaults to the
    /// project's primary <see cref="Project.DefaultAgent"/> for backwards
    /// compatibility. Set to a different agent (e.g. <c>gemini</c> when the
    /// work agent is <c>claude</c>) to diversify audit signal — different
    /// models have different blind spots. Requires the agent to be registered
    /// and its credentials to be available; otherwise the pipeline falls back
    /// to the work agent with a warning.
    /// </summary>
    public AgentKind? AuditAgent { get; init; }

    /// <summary>
    /// Optional per-auditor agent override. Keys are auditor names (e.g.
    /// <c>"security:llm-review"</c>); values are the agent kind to use for
    /// that auditor. Checked before <see cref="AuditAgent"/>, then falls
    /// through to <see cref="AuditAgent"/>, then to the work agent. Lets
    /// operators route individual auditors to specific models — e.g. security
    /// review on Claude (more cautious) and completeness review on Gemini
    /// (different perspective).
    /// </summary>
    public IReadOnlyDictionary<string, AgentKind> PerAuditorAgent { get; init; }
        = new Dictionary<string, AgentKind>();

    /// <summary>
    /// Maximum number of LLM auditors to run concurrently within an audit
    /// iteration. Default 3 matches the typical security/completeness/cheating
    /// set. Set to 1 to disable parallelism and fall back to sequential
    /// execution — useful for debugging or subscription accounts hitting 429s.
    /// Each parallel LLM auditor runs in its own sandbox clone to avoid result
    /// file races. Tool auditors are unaffected and always run sequentially.
    /// </summary>
    public int MaxLlmAuditorParallelism { get; init; } = 3;
}

public sealed record ProjectAuditTypeOverride
{
    public string? DisplayName { get; init; }
    public string? ReviewFocus { get; init; }
}

/// <summary>
/// Free-form auditor description. The composer picks the matching factory
/// based on <see cref="Kind"/> and applies the remaining fields.
///
/// <para>Kinds: <c>shell</c>, <c>diff-pattern</c>, <c>llm</c>, <c>plugin</c>.</para>
///
/// <para>For <c>Kind = "plugin"</c>, set <see cref="PluginId"/> to the plugin's
/// reverse-domain ID (the value passed to <c>[CodeyBoxPlugin(Id = …)]</c>). The
/// composer looks up the registered <see cref="IAuditor"/> singleton by that ID
/// and includes it in the run. <see cref="Name"/> is not used for plugin auditors;
/// the auditor's own <see cref="IAuditor.Name"/> is used instead.</para>
/// </summary>
public sealed record CustomAuditorDescriptor
{
    public string Name { get; init; } = string.Empty;
    public required string Kind { get; init; }

    /// <summary>
    /// Plugin reverse-domain ID. Required when <see cref="Kind"/> is <c>"plugin"</c>;
    /// ignored for all other kinds. Must match the <c>Id</c> declared in the
    /// plugin's <c>[CodeyBoxPlugin]</c> attribute.
    /// </summary>
    public string? PluginId { get; init; }

    public IReadOnlyList<string> Argv { get; init; } = [];
    public string? ReviewFocus { get; init; }
    public IReadOnlyList<DiffPatternDescriptor> Patterns { get; init; } = [];
}

public sealed record DiffPatternDescriptor
{
    public required string Description { get; init; }
    public required string Regex { get; init; }
}

/// <summary>
/// Per-project release management configuration.
/// This feature is opt-in: set <see cref="Enabled"/> to true to activate it.
/// Projects with <see cref="Enabled"/> = false see zero behaviour change.
/// </summary>
public sealed record ProjectReleaseConfig
{
    /// <summary>
    /// When true this project participates in the release flow.
    /// When false (default), a work item POST with a releaseId returns 400.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Branch name template. <c>{name}</c> is replaced with the release name.
    /// Default: <c>release/{name}</c>.
    /// </summary>
    public string BranchNameTemplate { get; init; } = "release/{name}";

    /// <summary>
    /// Auto-merge main into the release branch at this interval while the release is open.
    /// Keeps release-branch divergence bounded and reduces final-merge conflict surface.
    /// Null = disabled. Default: 12 hours.
    /// </summary>
    public TimeSpan? AutoSyncMainInterval { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Named deep auditors to run in the in_review phase. Each entry is a built-in
    /// auditor name ("owasp-asvs", "arch-coherence", "deps-cve-scan") or
    /// a plugin auditor ID. Empty list = skip deep audit (in_review immediately succeeds).
    /// </summary>
    public IReadOnlyList<string> DeepAuditors { get; init; } = [];

    /// <summary>
    /// Maximum iterations of deep audit + remediation work items before the release
    /// transitions to <see cref="ReleaseState.Failed"/>. Default: 5.
    /// Lower than the per-PR limit because convergence at the codebase level is harder.
    /// </summary>
    public int DeepAuditMaxIterations { get; init; } = 5;

    /// <summary>
    /// When true, on the <see cref="ReleaseState.Released"/> transition, also create a
    /// GitHub release via the project's existing upstream remote. Default: false.
    /// </summary>
    public bool CreateGitHubRelease { get; init; }

    /// <summary>
    /// Template for the GitHub release tag when <see cref="CreateGitHubRelease"/> is true.
    /// <c>{name}</c> is replaced with the release name. Default: <c>{name}</c>.
    /// </summary>
    public string GitHubTagTemplate { get; init; } = "{name}";
}
