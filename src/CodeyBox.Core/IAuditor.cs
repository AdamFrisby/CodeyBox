namespace CodeyBox.Core;

/// <summary>
/// An audit pass run against the work-phase output before merge. Auditors
/// can be tool-driven (linters, SAST, custom shell scripts) or LLM-driven
/// (a review-style agent prompt). The pipeline runs all registered auditors,
/// collects findings, and either proceeds to merge or hands findings back
/// to the agent for rework.
///
/// Auditors declare their <see cref="Required"/> capabilities so the
/// pipeline can group them into separate sandboxes — a tool auditor that
/// doesn't need agent credentials must NOT run in a sandbox where those
/// credentials are mounted, since a buggy or compromised tool could
/// exfiltrate them.
/// </summary>
public interface IAuditor
{
    /// <summary>Stable name for logs and findings.</summary>
    string Name { get; }

    /// <summary>
    /// Implementation kind for observability storage.
    /// One of: <c>diff-pattern</c>, <c>shell</c>, <c>llm</c>, <c>tool</c>.
    /// </summary>
    string Kind { get; }

    /// <summary>What the auditor needs to do its job.</summary>
    AuditCapabilities Required { get; }

    /// <summary>
    /// Declares that a blocking result from this auditor can skip the
    /// remaining auditors in the same audit iteration. Cheap mechanical gates
    /// can opt in so failures that already require rework avoid spending
    /// model quota on later advisory auditors.
    /// </summary>
    bool CanShortCircuitOnBlockingFinding => false;

    /// <summary>
    /// Optional constructive self-review guidance to append to the work-phase
    /// prompt for agents. Null when the auditor opts out of contributing
    /// checklist instructions.
    /// </summary>
    string? SelfReviewGuidance => null;

    /// <summary>
    /// Optional role marker used by the pipeline to order and gate later
    /// auditors. The default <see cref="AuditorRole.None"/> means "no special
    /// role". Marking deterministic build/test commands as
    /// <see cref="AuditorRole.BuildTestGate"/> lets later auditors require
    /// verified build/test evidence before they run.
    /// </summary>
    AuditorRole Role => AuditorRole.None;

    /// <summary>
    /// Evidence produced by this auditor when <see cref="Role"/> is
    /// <see cref="AuditorRole.BuildTestGate"/>. Implementations must opt in
    /// explicitly; the default contributes no evidence so a role marker alone
    /// cannot prove build or test coverage.
    /// </summary>
    BuildTestGateEvidence BuildTestGateEvidence => BuildTestGateEvidence.None;

    /// <summary>
    /// Runs the auditor against the working tree at <paramref name="workingDirectory"/>.
    /// </summary>
    Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Optional auditor marker for checks that must not share a mutable audit
/// sandbox with later auditors.
/// </summary>
public interface IAuditSandboxIsolation
{
    bool RequiresFreshSandbox => true;
}

/// <summary>
/// Marker for auditors that require deterministic build/test gates to have
/// completed successfully. The pipeline runs all
/// <see cref="AuditorRole.BuildTestGate"/> auditors first and skips these
/// auditors unless deterministic build and test evidence actually passed.
/// LLM auditors are also gated by their <see cref="IAuditor.Kind"/> because
/// the shared LLM prompt frame states that CI already ran successfully.
/// </summary>
public interface IRequiresPassedBuildTestGate;

[Flags]
public enum AuditCapabilities
{
    None = 0,
    /// <summary>Auditor needs the agent's credentials mounted (e.g. an LLM-based reviewer).</summary>
    AgentCredentials = 1 << 0,
    /// <summary>Auditor needs network egress (to the agent allowlist).</summary>
    Network = 1 << 1,
    /// <summary>Auditor needs a sandbox with graphical desktop capabilities.</summary>
    Graphical = 1 << 2,
}

/// <summary>
/// Coarse-grained role classification used by the pipeline to enforce
/// cross-auditor ordering invariants. Distinct from <see cref="AuditCapabilities"/>:
/// capabilities describe what an auditor NEEDS; <see cref="AuditorRole"/>
/// describes what role it FILLS in the audit panel.
/// </summary>
public enum AuditorRole
{
    /// <summary>Default — no special ordering or gating semantics.</summary>
    None,

    /// <summary>
    /// Deterministic build/test gate (e.g. <c>csharp:build-WaE</c>,
    /// <c>csharp:test-pass</c>). The pipeline runs auditors with this role
    /// before auditors that require verified build/test evidence. If any
    /// build/test gate does not pass, dependent auditors are skipped for that
    /// iteration. Findings still flow to rework as normal.
    /// </summary>
    BuildTestGate,
}

/// <summary>
/// The build/test evidence a BuildTestGate auditor can verify when it passes.
/// </summary>
[Flags]
public enum BuildTestGateEvidence
{
    None = 0,
    Build = 1 << 0,
    Test = 1 << 1,
    BuildAndTest = Build | Test,
}

/// <summary>Information the pipeline passes to each auditor.</summary>
public sealed record AuditContext(
    WorkItemId WorkItemId,
    string WorkBranch,
    string BaseBranch,
    int Iteration,
    string OriginalPrompt,
    /// <summary>
    /// The resolved agent runner for this auditor invocation. Set by the
    /// pipeline when a cross-review agent override is in effect; null when
    /// the auditor should use its own configured default. <see
    /// cref="LlmReviewAuditor"/> reads this to use the override instead of
    /// its baked-in runner. Tool auditors ignore it.
    /// </summary>
    IAgentRunner? AuditRunner = null,
    /// <summary>
    /// Optional callback invoked per stdout chunk as the LLM agent emits
    /// output. Set by the pipeline when live-stdout broadcasting is active;
    /// null otherwise. LLM auditors pass this through to IAgentRunner.RunAsync;
    /// tool auditors ignore it.
    /// </summary>
    Action<string>? StdoutChunkCallback = null,
    /// <summary>
    /// The credential bundle resolved for <see cref="AuditRunner"/>. The
    /// sandbox already receives these values in its environment, but some
    /// runners also need the bundle to materialise auth files before startup.
    /// </summary>
    AgentCredential? AuditCredential = null,
    /// <summary>
    /// When true, instructs the auditor's <see cref="AuditRunner"/> to capture
    /// the structured (JSON / stream-json) output of the agent CLI in addition
    /// to plain stdout, so the orchestrator can persist it for replay/audit.
    /// </summary>
    bool CaptureStructuredStream = false,
    /// <summary>
    /// Model id to pass to <see cref="AuditRunner"/>, resolved by the pipeline
    /// from the work item's chosen <c>AgentMembership</c>. Null when the runner
    /// should use its own default (e.g. when the audit agent kind differs from
    /// the work agent kind and the membership's model id would be invalid).
    /// </summary>
    string? ModelId = null,
    /// <summary>
    /// Reasoning-mode hint passed to <see cref="AuditRunner"/>, resolved from
    /// the work item's <c>AgentMembership.ReasoningMode</c>. The runner maps
    /// this onto the agent CLI's effort/reasoning flag.
    /// </summary>
    string? ReasoningMode = null,
    /// <summary>
    /// Prompt revision snapshotted when the iteration that produced the commit
    /// under audit was dispatched. The <c>process:prompt-revision-trailer</c>
    /// auditor compares this against the HEAD commit's
    /// <c>CodeyBox-Prompt-Revision</c> trailer to detect agents that finished
    /// against a stale prompt. Null when the orchestrator did not record a
    /// dispatch row (legacy data) — the trailer auditor emits a non-blocking
    /// Warning finding in that case so the missing row is visible to operators
    /// rather than silently disabling the check.
    /// </summary>
    int? PromptRevisionAtDispatch = null,
    /// <summary>
    /// Project-level policy for <c>process:build-script</c>. When false, a
    /// missing repo-root <c>build.sh</c> makes that auditor skip. When true,
    /// a missing script is a blocking audit finding.
    /// </summary>
    bool BuildScriptRequired = false,
    /// <summary>
    /// Stable project identifier the work item belongs to. Used by auditors
    /// whose per-project persistence is keyed (e.g. the mutation-rigor
    /// auditor's ratchet baseline). Null when the call site is the legacy
    /// shape from before this field existed; auditors that consume it must
    /// fall back gracefully (typically to a base-branch-only key).
    /// </summary>
    string? ProjectId = null);

/// <summary>Result from a single auditor invocation.</summary>
public sealed record AuditResult
{
    public AuditResult(
        bool Passed,
        IReadOnlyList<AuditFinding> Findings,
        string? RawOutput = null,
        string? AgentStderr = null,
        string? AgentSummary = null,
        string? AgentStdout = null)
    {
        this.Passed = Passed;
        this.Findings = Findings;
        this.RawOutput = RawOutput;
        this.AgentStderr = AgentStderr;
        this.AgentSummary = AgentSummary;
        this.AgentStdout = AgentStdout;
    }

    public bool Passed { get; init; }
    public IReadOnlyList<AuditFinding> Findings { get; init; }
    public string? RawOutput { get; init; }
    public string? AgentStderr { get; init; }
    public string? AgentSummary { get; init; }
    public string? AgentStdout { get; init; }

    public void Deconstruct(
        out bool Passed,
        out IReadOnlyList<AuditFinding> Findings,
        out string? RawOutput,
        out string? AgentStderr,
        out string? AgentSummary,
        out string? AgentStdout)
    {
        Passed = this.Passed;
        Findings = this.Findings;
        RawOutput = this.RawOutput;
        AgentStderr = this.AgentStderr;
        AgentSummary = this.AgentSummary;
        AgentStdout = this.AgentStdout;
    }

    /// <summary>
    /// When set to false, the result explicitly did not verify build/test
    /// evidence. For BuildTestGate auditors, the pipeline treats that as a
    /// blocking gate failure before any dependent auditor can run. For
    /// ordinary auditors it only records the evidence state. Used for
    /// classified "unrunnable in this environment" outcomes.
    /// </summary>
    public bool? BuildTestGateEvidenceVerified { get; init; }
}

public sealed record AuditFinding(
    string AuditorName,
    AuditSeverity Severity,
    string Title,
    string Description,
    string? Location = null);

/// <summary>
/// Raised by an auditor when audit infrastructure could not verify the check.
/// This is distinct from an <see cref="AuditFinding"/>: no source-code finding
/// should be persisted for a command that did not successfully run.
/// </summary>
public class AuditUnavailableException : Exception
{
    public AuditUnavailableException(string message)
        : base(message) { }

    public AuditUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }

    public AuditUnavailableException(string message, int exitCode, string output)
        : base(message)
    {
        ExitCode = exitCode;
        Output = output;
    }

    public int? ExitCode { get; }
    public string? Output { get; }
}

public enum AuditSeverity { Info, Warning, Error }

public static class AuditSeverityParser
{
    public static AuditSeverity Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "info" => AuditSeverity.Info,
        "warning" or "warn" => AuditSeverity.Warning,
        _ => AuditSeverity.Error,
    };
}

/// <summary>
/// Maps registered auditors. Loose coupling: new auditors are added via DI
/// without changing the orchestrator.
/// </summary>
public interface IAuditorRegistry
{
    IReadOnlyList<IAuditor> All { get; }
}
