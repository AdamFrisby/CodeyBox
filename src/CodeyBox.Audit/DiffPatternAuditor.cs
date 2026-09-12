using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Applies regex patterns according to the current review target. Code-target
/// invocations inspect only added unified-diff lines. Plan-target invocations
/// inspect every line of <see cref="AuditContext.PlanArtifact"/> and report
/// locations as <c>PLAN:&lt;line&gt;</c>.
///
/// Used by the "cheating" preset to spot suppression markers
/// (@ts-ignore, eslint-disable, # noqa, #pragma warning disable, etc.) and
/// stubbed implementations (NotImplementedException, lonely <c>pass</c>,
/// skipped tests).
///
/// Tool-only auditor (no agent credentials, no network). Cheap; runs first.
/// </summary>
public sealed class DiffPatternAuditor : IAuditor
{
    private readonly DiffPatternAuditorOptions _opts;

    public DiffPatternAuditor(DiffPatternAuditorOptions opts)
    {
        _opts = opts;
    }

    public string Name => _opts.Name;
    public string Kind => "diff-pattern";
    public AuditCapabilities Required => AuditCapabilities.None;
    public IReadOnlySet<AuditTarget> Targets => _opts.Targets;

    public IReadOnlyList<DiffPattern> Patterns => _opts.Patterns;

    public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
        // Dispatch on the explicit review strategy: an unhandled future target is
        // rejected in Classify rather than silently treated as a code diff.
        if (AuditTargetSemantics.Classify(context.EffectiveTarget) == AuditReviewStrategy.PlanReview)
            return AuditPlanArtifact(context);

        // Diff workBranch against baseBranch (three-dot: "the changes on
        // workBranch since it diverged from baseBranch"). --unified=0 keeps
        // only added/removed lines, no surrounding context, so our line-
        // counting matches what the agent actually wrote.
        var diff = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "diff",
                    $"origin/{context.BaseBranch}...HEAD",
                    "--unified=0", "--no-color"],
        }, ct);

        if (!diff.Success)
        {
            // Fall back to local diff against baseBranch if origin ref isn't
            // reachable (some sandboxes don't fetch the origin remote).
            diff = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "diff",
                        $"{context.BaseBranch}...HEAD",
                        "--unified=0", "--no-color"],
            }, ct);
        }
        if (!diff.Success)
        {
            return new AuditResult(false, [new AuditFinding(
                Name, AuditSeverity.Error, "git diff failed", diff.Stderr)],
                RawOutput: diff.Stderr);
        }

        var findings = new List<AuditFinding>();
        foreach (var added in UnifiedDiffParser.ParseAddedLines(diff.Stdout))
        {
            // Skip auditing CodeyBox configuration files and test files that contain literal patterns.
            if (added.File is not null && ShouldSkipFile(added.File))
                continue;

            foreach (var pattern in _opts.Patterns)
            {
                if (pattern.Regex.IsMatch(added.Content))
                {
                    findings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: pattern.Severity,
                        Title: pattern.Description,
                        Description: added.Content.Trim(),
                        Location: added.File is null ? null : $"{added.File}:{added.NewLine}"));
                }
            }
        }

        return new AuditResult(findings.Count == 0, findings, RawOutput: diff.Stdout);
    }

    private AuditResult AuditPlanArtifact(AuditContext context)
    {
        if (string.IsNullOrWhiteSpace(context.PlanArtifact))
        {
            return new AuditResult(false, [new AuditFinding(
                Name,
                AuditSeverity.Error,
                "no plan artifact to review",
                "The plan-review context carried no PLAN artifact.")]);
        }

        var findings = new List<AuditFinding>();
        var lines = context.PlanArtifact.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var pattern in _opts.Patterns)
            {
                if (pattern.Regex.IsMatch(line))
                {
                    findings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: pattern.Severity,
                        Title: pattern.Description,
                        Description: line.Trim(),
                        Location: $"PLAN:{i + 1}"));
                }
            }
        }

        return new AuditResult(findings.Count == 0, findings, RawOutput: context.PlanArtifact);
    }

    /// <summary>
    /// Repository configuration and test files legitimately contain the literal
    /// suppression/stub markers this auditor hunts for, so they are excluded from
    /// code-target diff scanning to avoid tautological findings.
    /// </summary>
    private static bool ShouldSkipFile(string file)
        => file.StartsWith("codeybox", StringComparison.OrdinalIgnoreCase) ||
           file.Contains("Defaults", StringComparison.OrdinalIgnoreCase) ||
           file.Contains("tests", StringComparison.OrdinalIgnoreCase) ||
           file.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);
}

public sealed record DiffPatternAuditorOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<DiffPattern> Patterns { get; init; }
    public IReadOnlySet<AuditTarget> Targets { get; init; } = AuditTargets.CodeOnly;
}

public sealed record DiffPattern
{
    public required Regex Regex { get; init; }
    public required string Description { get; init; }
    public AuditSeverity Severity { get; init; } = AuditSeverity.Error;
}
