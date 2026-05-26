using System.Globalization;
using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Deterministic auditor that verifies the agent's most recent commit carries
/// a <c>CodeyBox-Prompt-Revision: N</c> trailer matching the revision the
/// orchestrator snapshotted at iteration-dispatch time. A missing or
/// mismatched trailer is a blocking finding — it means the agent finished
/// against a stale prompt (the operator updated the prompt mid-iteration via
/// PUT /workitems/{id}/prompt) or did not emit the required trailer at all.
///
/// Tool-only auditor: needs neither agent credentials nor network. Pairs with
/// the existing <c>Co-Authored-By</c> trailer expectation; runs cheap before
/// any LLM-based audits.
/// </summary>
public sealed class PromptRevisionTrailerAuditor : IAuditor
{
    public const string AuditorName = "process:prompt-revision-trailer";

    public string Name => AuditorName;
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        // Legacy / unknown dispatch revision — no expectation to enforce. Emit
        // a non-blocking Warning so the missing dispatch row is visible (the
        // dispatch ledger is now always populated by the orchestrator before
        // Working/Reworking transitions; this branch should be unreachable
        // for items created after the prompt-revision feature shipped). Pass
        // the audit verdict so the auditor never blocks a legacy item.
        if (context.PromptRevisionAtDispatch is not { } expected)
            return new AuditResult(true,
            [
                new AuditFinding(
                    Name, AuditSeverity.Warning,
                    "no prompt_revision_at_dispatch recorded for this iteration",
                    "The orchestrator did not record a dispatch row for this iteration (legacy data, or the iteration ledger was cleared). The prompt-revision trailer cannot be verified; treat the result as informational."),
            ]);

        // %(trailers:key=...) prints only the matching trailer values. --unfold
        // collapses continuation lines so multi-line trailer values are joined
        // into one. Empty output means the trailer is absent.
        var trailers = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", workingDirectory, "log", "-1",
                $"--pretty=format:%(trailers:key={CodeyBoxTrailers.PromptRevisionTrailerKey},valueonly=true,unfold=true)",
            ],
        }, ct);

        if (!trailers.Success)
        {
            return new AuditResult(false,
            [
                new AuditFinding(
                    Name, AuditSeverity.Error,
                    $"failed to read HEAD trailers (git exit {trailers.ExitCode})",
                    trailers.Stderr ?? string.Empty),
            ],
            RawOutput: trailers.Stderr);
        }

        var raw = (trailers.Stdout ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return new AuditResult(false,
            [
                new AuditFinding(
                    Name, AuditSeverity.Error,
                    $"missing {CodeyBoxTrailers.PromptRevisionTrailerKey} trailer on HEAD commit",
                    $"Expected `{CodeyBoxTrailers.PromptRevisionTrailerKey}: {expected}` (the value of `$CODEYBOX_PROMPT_REVISION` when this iteration was dispatched). Add the trailer to your commit message and create a new commit."),
            ],
            RawOutput: trailers.Stdout);
        }

        // Multiple commits between rework iterations may each carry their own
        // trailer; --pretty=format... -1 already pins to HEAD so we expect at
        // most one value here. Take the first line defensively.
        var firstLine = raw.Split('\n', 2)[0].Trim();
        if (!int.TryParse(firstLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out var found))
        {
            return new AuditResult(false,
            [
                new AuditFinding(
                    Name, AuditSeverity.Error,
                    $"{CodeyBoxTrailers.PromptRevisionTrailerKey} trailer is not an integer",
                    $"HEAD commit's {CodeyBoxTrailers.PromptRevisionTrailerKey} value is '{firstLine}'; expected the integer {expected}."),
            ],
            RawOutput: trailers.Stdout);
        }

        if (found != expected)
        {
            return new AuditResult(false,
            [
                new AuditFinding(
                    Name, AuditSeverity.Error,
                    $"stale {CodeyBoxTrailers.PromptRevisionTrailerKey} trailer (found {found}, expected {expected})",
                    "The work item's prompt was updated after this iteration started. Re-read the latest prompt from the work-item context and commit again with the current value of `$CODEYBOX_PROMPT_REVISION`."),
            ],
            RawOutput: trailers.Stdout);
        }

        return new AuditResult(true, [], RawOutput: trailers.Stdout);
    }
}
