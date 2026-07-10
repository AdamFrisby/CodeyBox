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
public sealed partial class DiffPatternAuditor : IAuditor
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
        string? currentFile = null;
        var lineNumber = 0;
        foreach (var line in diff.Stdout.Split('\n'))
        {
            var rawLine = line.TrimEnd('\r');

            // Track the current file from "+++ b/path/to/file" headers.
            if (rawLine.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                currentFile = rawLine[6..];
                continue;
            }

            // Skip auditing CodeyBox configuration files and test files that contain literal patterns.
            if (currentFile is not null &&
                (currentFile.StartsWith("codeybox", StringComparison.OrdinalIgnoreCase) ||
                 currentFile.Contains("Defaults", StringComparison.OrdinalIgnoreCase) ||
                 currentFile.Contains("tests", StringComparison.OrdinalIgnoreCase) ||
                 currentFile.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Track line number from "@@ -A,B +C,D @@" hunk headers.
            if (rawLine.StartsWith("@@", StringComparison.Ordinal))
            {
                var m = HunkHeader().Match(rawLine);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var ln))
                    lineNumber = ln;
                continue;
            }
            if (rawLine.StartsWith("+++", StringComparison.Ordinal)) continue;
            if (rawLine.StartsWith("---", StringComparison.Ordinal)) continue;
            if (!rawLine.StartsWith('+')) continue;

            var addedLine = rawLine[1..];
            foreach (var pattern in _opts.Patterns)
            {
                if (pattern.Regex.IsMatch(addedLine))
                {
                    findings.Add(new AuditFinding(
                        AuditorName: Name,
                        Severity: pattern.Severity,
                        Title: pattern.Description,
                        Description: addedLine.Trim(),
                        Location: currentFile is null ? null : $"{currentFile}:{lineNumber}"));
                }
            }
            lineNumber++;
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

    [GeneratedRegex(@"\+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
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
