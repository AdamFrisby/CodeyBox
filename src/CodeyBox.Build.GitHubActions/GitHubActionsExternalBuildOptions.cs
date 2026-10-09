using System.Text.RegularExpressions;

namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Hot-reloadable operator knobs for the GitHub Actions execution/evidence
/// adapter (CBX-NEXT-103). Disabled by default: nothing dispatches, polls,
/// or reads artifacts until the operator opts in AND pins at least one
/// already-existing workflow in <see cref="ApprovedWorkflows"/>. This task
/// never creates workflows, grants permissions, or configures access; see
/// <c>docs/concepts/github-actions-builds.md</c> for the explicit later
/// setup requirements. All values are plain operational data; GitHub
/// specifics (API version, endpoints) live beside the transport, never in
/// Core.
/// </summary>
public sealed class GitHubActionsExternalBuildOptions
{
    public const string SectionName = "CodeyBox:GitHubActionsBuilds";

    /// <summary>Stable provider id this adapter registers under.</summary>
    public const string ProviderId = "github-actions";

    /// <summary>
    /// GitHub REST API version sent as <c>X-GitHub-Api-Version</c>. Matches
    /// the version the existing Checks client implements against; see
    /// https://docs.github.com/en/rest/about-the-rest-api/api-versions.
    /// </summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>Master switch. Default false: the adapter dispatches nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Operator-pinned workflows, keyed by the framework target id
    /// (<c>ExternalBuildTargetApproval.TargetId</c>, exact match). Only
    /// already-existing repository workflows may be listed; arbitrary
    /// repositories, workflows, refs, or inputs are rejected. Empty by
    /// default.
    /// </summary>
    public Dictionary<string, GitHubActionsWorkflowApproval> ApprovedWorkflows { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// API base override (GitHub Enterprise Server or an isolated loopback
    /// fixture in tests). Defaults to <c>https://api.github.com</c>.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>
    /// Exact-host allowlist for artifact download redirects (https only, no
    /// credentials in URL). The API host itself is always allowed.
    /// </summary>
    public List<string> AllowedArtifactHosts { get; set; } = ["objects.githubusercontent.com"];

    /// <summary>Max artifact-download redirects followed manually. Default 3.</summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>Dispatch HTTP timeout in seconds. Default 30.</summary>
    public int DispatchTimeoutSeconds { get; set; } = 30;

    /// <summary>Per-call poll/read HTTP timeout in seconds. Default 30.</summary>
    public int PollTimeoutSeconds { get; set; } = 30;

    /// <summary>Max test-report JSON bytes decompressed/parsed. Default 1 MiB.</summary>
    public long MaxReportBytes { get; set; } = 1024L * 1024;

    /// <summary>Max jobs read per run (list pages are bounded to this). Default 100.</summary>
    public int MaxJobsPerRun { get; set; } = 100;

    /// <summary>
    /// Max persisted adapter bindings (build-id to expected-checkout truth).
    /// Oldest-completed-first eviction keeps memory bounded. Default 10,000.
    /// </summary>
    public int MaxBindings { get; set; } = 10_000;

    /// <summary>
    /// Allowed temporary-candidate ref namespace (exact prefix match).
    /// Dispatchable branches only: <c>refs/heads/</c> members, so the
    /// workflow_dispatch ref addresses the published candidate. Default
    /// <c>refs/heads/codeybox-candidates/</c>.
    /// </summary>
    public string CandidateRefPrefix { get; set; } = GitHubActionsCandidateRefPolicy.DefaultPrefix;

    /// <summary>Temporary candidate ref TTL in seconds. Default 86400 (24h).</summary>
    public int CandidateRefTtlSeconds { get; set; } = 86400;

    /// <summary>
    /// Fail-fast structural validation for operator configuration. Positive
    /// timeouts still yield typed transient errors (not hangs) at runtime.
    /// </summary>
    public static bool IsValid(GitHubActionsExternalBuildOptions? options) =>
        options is not null
        && Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var apiBase)
        && string.Equals(apiBase.Scheme, "https", StringComparison.OrdinalIgnoreCase)
        && options.AllowedArtifactHosts.Count <= 32
        && options.AllowedArtifactHosts.All(static h => !string.IsNullOrWhiteSpace(h) && !h.Contains('/'))
        && options.MaxRedirects is >= 0 and <= 10
        && options.DispatchTimeoutSeconds is >= 1 and <= 600
        && options.PollTimeoutSeconds is >= 1 and <= 600
        && options.MaxReportBytes is >= 1024 and <= 64L * 1024 * 1024
        && options.MaxJobsPerRun is >= 1 and <= 1000
        && options.MaxBindings is >= 100 and <= 1_000_000
        && !string.IsNullOrWhiteSpace(options.CandidateRefPrefix)
        && options.CandidateRefPrefix.StartsWith("refs/", StringComparison.Ordinal)
        && options.CandidateRefTtlSeconds is >= 60 and <= 30 * 86400
        && options.ApprovedWorkflows.All(static kv =>
            !string.IsNullOrWhiteSpace(kv.Key) && GitHubActionsWorkflowApproval.IsValid(kv.Value));
}

/// <summary>
/// Operator pin for one already-existing workflow: exact repository,
/// workflow file, toolchain/platform/configuration policy, and the evidence
/// contract (required jobs, test-report artifact, package prefixes). Any run
/// observed from another repository, fork, workflow file, or run id family
/// is rejected as substitution — never adopted as evidence.
/// </summary>
public sealed class GitHubActionsWorkflowApproval
{
    private static readonly Regex NamePattern = new(
        "^[A-Za-z0-9_.-]{1,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PathPattern = new(
        "^[A-Za-z0-9_./-]{1,256}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Repository owner (exact, case-insensitive at the sink).</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Repository name (exact, case-insensitive at the sink).</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>
    /// Workflow file path exactly as reported by the runs API
    /// (e.g. <c>.github/workflows/build.yml</c>). Matched by exact equality.
    /// </summary>
    public string WorkflowPath { get; set; } = string.Empty;

    /// <summary>Opaque toolchain descriptor recorded into neutral evidence.</summary>
    public string Toolchain { get; set; } = string.Empty;

    /// <summary>Opaque platform descriptor recorded into neutral evidence.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Configuration profile the workflow executes (e.g. <c>release</c>).</summary>
    public string Configuration { get; set; } = "release";

    /// <summary>
    /// When true (default), a workflow success without the explicit approved
    /// test-report artifact never counts tests as passed. Build success
    /// cannot imply tests ran.
    /// </summary>
    public bool RequireTestReport { get; set; } = true;

    /// <summary>Exact artifact file inside the report artifact zip. Default <c>codeybox-test-report.json</c>.</summary>
    public string TestReportArtifact { get; set; } = "codeybox-test-report.json";

    /// <summary>Exact GitHub artifact name carrying the report zip. Default <c>codeybox-reports</c>.</summary>
    public string TestReportArtifactName { get; set; } = "codeybox-reports";

    /// <summary>Exact compile/build job names that must all succeed.</summary>
    public List<string> CompileJobNames { get; set; } = ["build"];

    /// <summary>Exact test job names (informational when a report is required).</summary>
    public List<string> TestJobNames { get; set; } = ["test"];

    /// <summary>Artifact name prefixes that count as package output.</summary>
    public List<string> PackageArtifactPrefixes { get; set; } = ["package"];

    public static bool IsValid(GitHubActionsWorkflowApproval? approval) =>
        approval is not null
        && IsRoName(approval.Owner)
        && IsRoName(approval.Repository)
        && !string.IsNullOrWhiteSpace(approval.WorkflowPath)
        && approval.WorkflowPath.Length <= 256
        && PathPattern.IsMatch(approval.WorkflowPath)
        && approval.WorkflowPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(approval.Toolchain)
        && approval.Toolchain.Length <= 128
        && !string.IsNullOrWhiteSpace(approval.Platform)
        && approval.Platform.Length <= 128
        && !string.IsNullOrWhiteSpace(approval.Configuration)
        && approval.Configuration.Length <= 128
        && !string.IsNullOrWhiteSpace(approval.TestReportArtifact)
        && approval.TestReportArtifact.Length <= 256
        && !string.IsNullOrWhiteSpace(approval.TestReportArtifactName)
        && approval.TestReportArtifactName.Length <= 256
        && approval.CompileJobNames.Count is >= 1 and <= 32
        && approval.CompileJobNames.All(IsJobName)
        && approval.TestJobNames.Count <= 32
        && approval.TestJobNames.All(IsJobName)
        && approval.PackageArtifactPrefixes.Count is >= 1 and <= 32
        && approval.PackageArtifactPrefixes.All(static p => p.Length is >= 1 and <= 128);

    private static bool IsRoName(string value) =>
        value.Length is >= 1 and <= 128 && NamePattern.IsMatch(value);

    private static bool IsJobName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}
