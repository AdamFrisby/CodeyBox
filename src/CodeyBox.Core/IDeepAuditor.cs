namespace CodeyBox.Core;

/// <summary>
/// A codebase-level auditor that runs during the <c>in_review</c> phase of a
/// <see cref="Release"/>. Unlike per-work-item <see cref="IAuditor"/>s, deep
/// auditors receive the full release branch tree rather than a diff, and are
/// expected to apply broad rubrics: OWASP ASVS, architecture coherence, CVE
/// scans, etc.
///
/// Deep auditors declare their <see cref="Required"/> capabilities exactly as
/// regular auditors do, so the release service can group them into the
/// appropriate sandboxes (credential-free vs. LLM-bearing).
/// </summary>
public interface IDeepAuditor
{
    string Name { get; }
    string Kind { get; }
    AuditCapabilities Required { get; }

    Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        DeepAuditContext context,
        CancellationToken ct = default);
}
