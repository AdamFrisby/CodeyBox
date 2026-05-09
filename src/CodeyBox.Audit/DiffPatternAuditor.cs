using System.Text.RegularExpressions;
using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Audits the diff between <see cref="AuditContext.BaseBranch"/> and
/// <see cref="AuditContext.WorkBranch"/> against a list of regex patterns.
/// Each match on an added line (a <c>+</c>-prefixed line in unified diff)
/// emits one <see cref="AuditFinding"/>.
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

    public IReadOnlyList<DiffPattern> Patterns => _opts.Patterns;

    public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
    {
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
        foreach (var rawLine in diff.Stdout.Split('\n'))
        {
            // Track the current file from "+++ b/path/to/file" headers.
            if (rawLine.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                currentFile = rawLine[6..];
                continue;
            }

            // Skip auditing CodeyBox configuration files and test files that contain literal patterns.
            if (currentFile is not null && 
                (currentFile.StartsWith("codeybox/", StringComparison.Ordinal) || 
                 currentFile.Contains("/Defaults/", StringComparison.Ordinal) ||
                 currentFile.EndsWith("Tests.cs", StringComparison.Ordinal)))
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

    [GeneratedRegex(@"\+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}

public sealed record DiffPatternAuditorOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<DiffPattern> Patterns { get; init; }
}

public sealed record DiffPattern
{
    public required Regex Regex { get; init; }
    public required string Description { get; init; }
    public AuditSeverity Severity { get; init; } = AuditSeverity.Error;
}
