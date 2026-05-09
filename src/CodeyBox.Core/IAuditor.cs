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
    /// Runs the auditor against the working tree at <paramref name="workingDirectory"/>.
    /// </summary>
    Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default);
}

[Flags]
public enum AuditCapabilities
{
    None = 0,
    /// <summary>Auditor needs the agent's credentials mounted (e.g. an LLM-based reviewer).</summary>
    AgentCredentials = 1 << 0,
    /// <summary>Auditor needs network egress (to the agent allowlist).</summary>
    Network = 1 << 1,
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
    bool CaptureStructuredStream = false);

/// <summary>Result from a single auditor invocation.</summary>
public sealed record AuditResult(
    bool Passed,
    IReadOnlyList<AuditFinding> Findings,
    string? RawOutput = null,
    string? AgentStderr = null,
    string? AgentSummary = null,
    string? AgentStdout = null);

public sealed record AuditFinding(
    string AuditorName,
    AuditSeverity Severity,
    string Title,
    string Description,
    string? Location = null);

public enum AuditSeverity { Info, Warning, Error }

/// <summary>
/// Maps registered auditors. Loose coupling: new auditors are added via DI
/// without changing the orchestrator.
/// </summary>
public interface IAuditorRegistry
{
    IReadOnlyList<IAuditor> All { get; }
}
