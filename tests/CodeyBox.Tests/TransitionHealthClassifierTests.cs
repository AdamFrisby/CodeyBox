using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that <see cref="TransitionHealthClassifier"/> labels each
/// persisted-signal row according to the documented taxonomy in
/// <c>docs/transition-health.md</c>:
///
/// <list type="bullet">
/// <item>An audit→rework caused by a genuine blocking finding is LEGITIMATE
///   (the loop working as designed).</item>
/// <item>An audit→rework caused by the auditor failing to run / not writing
///   its result / producing invalid JSON is an INFRA failure.</item>
/// <item>The "produced no changes to commit" silent failure surfaces as
///   <c>failure:agent</c> on the involvement row → INFRA.</item>
/// <item>Quota exhaustion mid-run (<c>failure:quota</c>) → INFRA.</item>
/// <item>Terminal <c>MergeConflictResolutionFailed</c> → INFRA.</item>
/// <item>Operator-driven cancellation is SKIPPED.</item>
/// <item>Done items are not in the source set, so throughput cannot move the
///   score.</item>
/// </list>
/// </summary>
public sealed class TransitionHealthClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private static TransitionHealthOptions DefaultOptions(TimeSpan? window = null, int? max = null) =>
        new()
        {
            Enabled = true,
            Window = window ?? TimeSpan.FromHours(24),
            MaxTransitions = max,
        };

    private static TransitionInvolvementRow Involvement(
        string phase, string outcome, DateTimeOffset endedAt, int? iteration = null) =>
        new("wi-1", phase, iteration, outcome, endedAt);

    private static TransitionAuditReportRow AuditReport(
        DateTimeOffset endedAt, IReadOnlyList<string> findingTitles, string worstSeverity = "Error",
        int iteration = 1, string auditorName = "review:quality") =>
        new("wi-1", iteration, auditorName, worstSeverity, endedAt, findingTitles);

    private static TransitionTerminalFailureRow TerminalFailure(
        int state, string? failureKind, DateTimeOffset updatedAt) =>
        new("wi-1", state, failureKind, updatedAt);

    private static TransitionDataSnapshot Snapshot(
        IEnumerable<TransitionInvolvementRow>? involvements = null,
        IEnumerable<TransitionAuditReportRow>? audits = null,
        IEnumerable<TransitionTerminalFailureRow>? terminals = null) =>
        new(
            (involvements ?? []).ToList(),
            (audits ?? []).ToList(),
            (terminals ?? []).ToList());

    [Fact]
    public void Audit_with_real_findings_counts_as_legitimate_not_infra_failure()
    {
        // An audit that found genuine blocking issues (severity=Error, title
        // points at the actual bug — NOT one of the LlmReviewAuditor infra
        // titles) is the audit→rework loop working as designed.
        var snapshot = Snapshot(audits: [AuditReport(
            endedAt: Now.AddMinutes(-30),
            findingTitles: ["Hardcoded secret found in src/api/Auth.cs"],
            worstSeverity: "Error")]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.LegitimateTransitions);
        Assert.Equal(0, report.InfraFailureTransitions);
        Assert.Equal(1.0, report.Score);
        var auditStage = report.Stages.First(s => s.Stage == TransitionStage.Audit);
        Assert.Equal(1, auditStage.Legitimate);
        Assert.Equal(0, auditStage.InfraFailure);
    }

    [Fact]
    public void Audit_review_agent_failed_to_run_is_infra_failure()
    {
        var snapshot = Snapshot(audits: [AuditReport(
            endedAt: Now.AddMinutes(-15),
            findingTitles: ["review agent failed to run"])]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(0, report.LegitimateTransitions);
        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(0.0, report.Score);
        Assert.Equal(1, report.InfraByKind["auditor_failed"]);
        Assert.Equal(TransitionStage.Audit, report.WorstStage);
    }

    [Fact]
    public void Audit_did_not_write_result_file_is_infra_failure()
    {
        // LlmReviewAuditor writes a finding titled
        // "agent did not write audit/result.json" when the agent exited 0 but
        // never produced the JSON the pipeline expected. Per the taxonomy
        // this is an infra failure even though the involvement row stays
        // outcome=success.
        var snapshot = Snapshot(audits: [AuditReport(
            endedAt: Now.AddMinutes(-10),
            findingTitles: ["agent did not write audit/result.json"])]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(0, report.LegitimateTransitions);
        Assert.Equal(1, report.InfraByKind["auditor_failed"]);
    }

    [Fact]
    public void Audit_produced_invalid_json_is_infra_failure()
    {
        var snapshot = Snapshot(audits: [AuditReport(
            endedAt: Now.AddMinutes(-5),
            findingTitles: ["review agent produced invalid JSON"])]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(1, report.InfraByKind["auditor_failed"]);
    }

    [Fact]
    public void Audit_required_build_unavailable_is_infra_failure()
    {
        // RequiredBuildGate.ToAuditResult emits findings whose title is
        // "required build unavailable: {DisplayCommand}" when the build
        // verifier itself could not run. Match by prefix so the command suffix
        // does not break classification.
        var snapshot = Snapshot(audits: [AuditReport(
            endedAt: Now.AddMinutes(-1),
            findingTitles: ["required build unavailable: dotnet build"])]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(1, report.InfraByKind["build_unavailable"]);
    }

    [Fact]
    public void Audit_required_build_failed_is_legitimate_real_finding()
    {
        // "required build failed: {DisplayCommand}" represents a real, valid
        // audit finding (the build genuinely broke). It MUST NOT be mapped to
        // infra — the audit→rework loop is doing its job. Only the
        // "unavailable" sibling means the auditor couldn't run.
        var snapshot = Snapshot(audits: [AuditReport(
            endedAt: Now.AddMinutes(-1),
            findingTitles: ["required build failed: dotnet build"])]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.LegitimateTransitions);
        Assert.Equal(0, report.InfraFailureTransitions);
    }

    [Fact]
    public void Audit_with_mix_of_real_and_infra_findings_classifies_as_infra()
    {
        // If the audit ran, found some real findings, AND ALSO emitted one of
        // the infra titles (e.g. a multi-auditor batch where one auditor died
        // but others reported), the infra signal wins — the auditor still
        // failed.
        var snapshot = Snapshot(audits: [AuditReport(
            endedAt: Now.AddMinutes(-2),
            findingTitles: ["Unused variable in foo.cs", "review agent failed to run"])]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(0, report.LegitimateTransitions);
    }

    [Fact]
    public void Work_success_is_legitimate()
    {
        var snapshot = Snapshot(involvements: [Involvement("work", "success", Now.AddMinutes(-30))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.LegitimateTransitions);
        Assert.Equal(0, report.InfraFailureTransitions);
        var work = report.Stages.First(s => s.Stage == TransitionStage.Work);
        Assert.Equal(1, work.Legitimate);
    }

    [Fact]
    public void Work_failure_agent_is_infra_silent_no_changes()
    {
        // "produced no changes to commit" surfaces as failure:agent on the
        // involvement row (OutcomeForFailure's default path). The taxonomy
        // calls this an infra failure (the agent transport returned no work).
        var snapshot = Snapshot(involvements: [Involvement("work", "failure:agent", Now.AddMinutes(-10))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(0, report.LegitimateTransitions);
        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(1, report.InfraByKind["agent"]);
    }

    [Fact]
    public void Work_failure_quota_is_infra_failure_quota()
    {
        var snapshot = Snapshot(involvements: [Involvement("work", "failure:quota", Now.AddMinutes(-10))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(1, report.InfraByKind["quota"]);
    }

    [Fact]
    public void Work_failure_timeout_is_infra_failure_timeout()
    {
        var snapshot = Snapshot(involvements: [Involvement("work", "failure:timeout", Now.AddMinutes(-2))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(1, report.InfraByKind["timeout"]);
    }

    [Fact]
    public void Work_failure_infrastructure_is_infra_failure_infrastructure()
    {
        var snapshot = Snapshot(involvements: [Involvement("work", "failure:infrastructure", Now.AddMinutes(-2))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(1, report.InfraByKind["infrastructure"]);
    }

    [Fact]
    public void Operator_cancellation_is_skipped_not_counted()
    {
        // Operator-driven cancel is neither healthy forward progress nor
        // infra failure — it cannot pull the score in either direction.
        var snapshot = Snapshot(involvements:
        [
            Involvement("work", "failure:cancelled", Now.AddMinutes(-1)),
            Involvement("rework", "cancelled", Now.AddMinutes(-2)),
        ]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(0, report.TotalTransitions);
    }

    [Fact]
    public void Rework_phase_is_classified_under_Rework_stage()
    {
        var snapshot = Snapshot(involvements:
        [
            Involvement("rework", "success", Now.AddMinutes(-10), iteration: 2),
            Involvement("rework", "failure:agent", Now.AddMinutes(-5), iteration: 3),
        ]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        var rework = report.Stages.First(s => s.Stage == TransitionStage.Rework);
        Assert.Equal(1, rework.Legitimate);
        Assert.Equal(1, rework.InfraFailure);
    }

    [Fact]
    public void Audit_involvement_row_is_not_double_counted_against_audit_report()
    {
        // The audit stage MUST be scored from audit_reports, not from
        // involvement rows: the involvement row for an LlmReviewAuditor that
        // exited cleanly but wrote no result.json has outcome=success even
        // though the audit_report row shows the infra failure. Classifying
        // both would double-count and (incorrectly) make every infra-audit
        // appear as a 50/50 mixed stage.
        var snapshot = Snapshot(
            involvements: [Involvement("audit:review:quality", "success", Now.AddMinutes(-10), iteration: 1)],
            audits: [AuditReport(
                endedAt: Now.AddMinutes(-10),
                findingTitles: ["agent did not write audit/result.json"])]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.TotalTransitions);
        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(0, report.LegitimateTransitions);
    }

    [Fact]
    public void Terminal_abandoned_after_recovery_attempts_is_infra()
    {
        // AbandonedAfterRecoveryAttempts is the recovery loop giving up after
        // MaxRecoveryAttempts host-shutdown cycles — the canonical
        // worker-died-without-preempt-checkpoint infra signature.
        var snapshot = Snapshot(terminals: [TerminalFailure(
            state: (int)WorkItemState.AbandonedAfterRecoveryAttempts,
            failureKind: null,
            updatedAt: Now.AddMinutes(-5))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(1, report.InfraByKind["abandoned_after_recovery_attempts"]);
        Assert.Equal(TransitionStage.Terminal, report.WorstStage);
    }

    [Fact]
    public void Terminal_merge_conflict_resolution_failed_is_infra()
    {
        var snapshot = Snapshot(terminals: [TerminalFailure(
            state: (int)WorkItemState.MergeConflictResolutionFailed,
            failureKind: "infrastructure",
            updatedAt: Now.AddMinutes(-10))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(1, report.InfraByKind["merge_conflict_resolution_failed"]);
        Assert.Equal(TransitionStage.Terminal, report.WorstStage);
    }

    [Theory]
    [InlineData("quota")]
    [InlineData("timeout")]
    [InlineData("agent")]
    [InlineData("agent_unavailable")]
    [InlineData("infrastructure")]
    [InlineData("configuration")]
    public void Terminal_failed_infra_kinds_count_as_infra_failure(string kind)
    {
        var snapshot = Snapshot(terminals: [TerminalFailure(
            state: (int)WorkItemState.Failed,
            failureKind: kind,
            updatedAt: Now.AddMinutes(-1))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.Equal(1, report.InfraByKind[kind]);
    }

    [Fact]
    public void Terminal_failed_build_is_not_counted_as_infra()
    {
        // failureKind="build" comes from RequiredBuildFailedException — the
        // agent's work-product left the branch non-compiling. The gate caught
        // it, which means the gate is working as designed (a work-quality
        // failure, NOT an infra failure). The infra-equivalent signature is
        // failureKind="infrastructure" (RequiredBuildVerificationUnavailable).
        // Mirrors the audit-stage taxonomy, which classifies "required build
        // failed:" findings as LEGITIMATE and only "required build unavailable:"
        // findings as InfraFailure.
        var snapshot = Snapshot(terminals: [TerminalFailure(
            state: (int)WorkItemState.Failed,
            failureKind: "build",
            updatedAt: Now.AddMinutes(-1))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(0, report.TotalTransitions);
        Assert.Equal(0, report.InfraFailureTransitions);
        Assert.False(report.InfraByKind.ContainsKey("build"));
    }

    [Fact]
    public void Conflict_rework_semantic_incompatible_is_skipped_not_infra()
    {
        // PipelineRunner finalises the conflict-rework involvement with
        // outcome="failure:semantic-incompatible" when the agent declares the
        // upstream/downstream branches semantically irreconcilable. That's
        // the agent doing its job — a real, intended disposition — not an
        // infra failure. Must not pull the Merge-stage score down.
        var snapshot = Snapshot(involvements:
        [
            Involvement("conflict_rework", "failure:semantic-incompatible", Now.AddMinutes(-10)),
        ]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(0, report.TotalTransitions);
        Assert.Equal(0, report.InfraFailureTransitions);
        Assert.Equal(0, report.LegitimateTransitions);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("other")]
    [InlineData(null)]
    public void Terminal_failed_ambiguous_kinds_are_not_counted(string? kind)
    {
        // 'cancelled' is operator intent, 'other' is a catch-all the
        // pipeline uses when it has not classified the failure yet, and null
        // means the failure kind never landed. Counting any of them as
        // infrastructure would over-pessimise the score; counting any as
        // legitimate would under-pessimise. Document and exclude.
        var snapshot = Snapshot(terminals: [TerminalFailure(
            state: (int)WorkItemState.Failed,
            failureKind: kind,
            updatedAt: Now.AddMinutes(-1))]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(0, report.TotalTransitions);
    }

    [Fact]
    public void Done_items_do_not_appear_in_terminal_source_so_throughput_does_not_move_score()
    {
        // Mix: 100 successful work-phase transitions across many items (high
        // throughput on a healthy fleet) plus one auditor death. The score
        // should be 100/(100+1) = 0.990… — not dominated by throughput, and
        // crucially NOT affected by how many of those items eventually
        // reached Done (the data source intentionally excludes Done items).
        var involvements = Enumerable.Range(0, 100)
            .Select(i => new TransitionInvolvementRow(
                $"wi-{i}", "work", null, "success", Now.AddMinutes(-100 + i)))
            .ToList();
        var snapshot = new TransitionDataSnapshot(
            involvements,
            [AuditReport(endedAt: Now.AddMinutes(-1), findingTitles: ["review agent failed to run"])],
            []);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        Assert.Equal(101, report.TotalTransitions);
        Assert.Equal(100, report.LegitimateTransitions);
        Assert.Equal(1, report.InfraFailureTransitions);
        Assert.InRange(report.Score, 0.989, 0.991);
    }

    [Fact]
    public void Window_boundary_excludes_transitions_older_than_window_start()
    {
        // 90-minute window. A transition that ended 2h ago must be excluded.
        var options = DefaultOptions(window: TimeSpan.FromMinutes(90));
        var snapshot = Snapshot(involvements:
        [
            Involvement("work", "success", Now.AddMinutes(-30)),
            Involvement("work", "failure:agent", Now.AddMinutes(-120)),
        ]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, options);

        Assert.Equal(1, report.LegitimateTransitions);
        Assert.Equal(0, report.InfraFailureTransitions);
    }

    [Fact]
    public void MaxTransitions_cap_takes_most_recent_first()
    {
        // 10 transitions across an hour; cap at 3 — only the most recent 3
        // contribute to the score.
        var ts = Enumerable.Range(0, 10)
            .Select(i => Involvement("work",
                i < 7 ? "failure:agent" : "success",
                Now.AddMinutes(-60 + (i * 6))))
            .ToList();
        var snapshot = Snapshot(involvements: ts);

        var report = TransitionHealthClassifier.Compute(
            snapshot, Now,
            DefaultOptions(max: 3));

        // The 3 most recent rows (i=7,8,9) are all "success" → all
        // LEGITIMATE; the 7 older failure:agent rows were dropped by the cap.
        Assert.Equal(3, report.TotalTransitions);
        Assert.Equal(3, report.LegitimateTransitions);
        Assert.Equal(0, report.InfraFailureTransitions);
        Assert.Equal(1.0, report.Score);
    }

    [Fact]
    public void Empty_snapshot_returns_score_one_and_zero_total()
    {
        // Healthy by default when nothing happened. The score does not divide
        // by zero, and no stage shows infra signal.
        var report = TransitionHealthClassifier.Compute(Snapshot(), Now, DefaultOptions());

        Assert.Equal(0, report.TotalTransitions);
        Assert.Equal(1.0, report.Score);
        Assert.Equal(0.0, report.InfraFailureRate);
        Assert.Null(report.WorstStage);
    }

    [Fact]
    public void Worst_stage_is_the_stage_with_most_infra_failures()
    {
        var snapshot = Snapshot(
            involvements:
            [
                Involvement("work", "failure:agent", Now.AddMinutes(-50)),
                Involvement("work", "failure:agent", Now.AddMinutes(-49)),
                Involvement("rework", "failure:agent", Now.AddMinutes(-48)),
                Involvement("merge", "success", Now.AddMinutes(-10)),
            ],
            audits:
            [
                AuditReport(endedAt: Now.AddMinutes(-5), findingTitles: ["review agent failed to run"]),
            ]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        // Work has 2 infra failures, Rework 1, Audit 1, Merge 0 → Work wins.
        Assert.Equal(TransitionStage.Work, report.WorstStage);
    }

    [Fact]
    public void Stage_breakdown_lists_all_five_canonical_stages_even_when_empty()
    {
        // The breakdown is fixed-shape so operator dashboards can render
        // every stage without conditional logic.
        var report = TransitionHealthClassifier.Compute(Snapshot(), Now, DefaultOptions());

        var stageNames = report.Stages.Select(s => s.Stage).ToList();
        Assert.Equal(
            new[]
            {
                TransitionStage.Work,
                TransitionStage.Rework,
                TransitionStage.Audit,
                TransitionStage.Merge,
                TransitionStage.Terminal,
            },
            stageNames);
    }

    [Fact]
    public void Merge_phase_uses_phase_string_contains_check_for_conflict_rework_variants()
    {
        // PipelineRunner uses "merge" plus a "conflict_rework" variant when
        // the merge-time conflict-resolution agent runs (the literal stored in
        // agent_involvement.phase is PipelineRunner.ConflictReworkPhaseKey =
        // "conflict_rework"). Both belong in the Merge stage bucket so the
        // operator sees one number for "merges dying".
        var snapshot = Snapshot(involvements:
        [
            Involvement("merge", "success", Now.AddMinutes(-30)),
            Involvement("conflict_rework", "failure:agent", Now.AddMinutes(-20)),
        ]);

        var report = TransitionHealthClassifier.Compute(snapshot, Now, DefaultOptions());

        var merge = report.Stages.First(s => s.Stage == TransitionStage.Merge);
        Assert.Equal(1, merge.Legitimate);
        Assert.Equal(1, merge.InfraFailure);
    }
}
