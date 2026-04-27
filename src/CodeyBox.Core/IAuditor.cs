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
    string OriginalPrompt);

/// <summary>Result from a single auditor invocation.</summary>
public sealed record AuditResult(bool Passed, IReadOnlyList<AuditFinding> Findings);

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
