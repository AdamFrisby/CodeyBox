using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure transition classifier. Given a raw <see cref="TransitionDataSnapshot"/>
/// and a window, produces a <see cref="TransitionHealthReport"/> by labelling
/// each row as LEGITIMATE forward progress, an INFRA failure, or SKIPPED.
///
/// <para>
/// The taxonomy:
/// <list type="bullet">
/// <item><b>LEGITIMATE</b> — forward progress (Working → WorkComplete →
/// Auditing → AuditPassed → Merging → …), AND loop transitions driven by REAL
/// outcomes: an audit that actually ran and returned genuine blocking findings
/// is the loop working as designed, not a failure.</item>
/// <item><b>INFRA_FAILURE</b> — terminal Failed / MergeConflictResolutionFailed
/// with an infra cause; an audit-stage row whose only "failure" is the
/// auditor itself failing to run (the LlmReviewAuditor "review agent failed to
/// run" / "agent did not write audit/result.json" / "review agent produced
/// invalid JSON" Error finding); agent transport failure / non-zero exit /
    /// SIGTERM-kill; quota-exhaustion mid-run; transient/auth/provisioning
    /// agent involvement failures (<c>failure:transient</c>,
    /// <c>failure:auth</c>, <c>failure:infrastructure</c>);
    /// agent-unavailable timeouts; the silent "produced no changes to commit"
    /// path (which surfaces in the involvement row as <c>failure:agent</c>).</item>
/// <item><b>SKIPPED</b> — operator-driven cancel (<c>failure:cancelled</c> /
/// <c>cancelled</c>), unfinalised in-flight rows, terminal-Failed with a
/// FailureKind we do not score (<c>other</c> — ambiguous; documented as
/// non-counting so it cannot pull the score in either direction).</item>
/// </list>
/// </para>
/// </summary>
public static class TransitionHealthClassifier
{
    /// <summary>
    /// Infra titles produced by <c>LlmReviewAuditor</c> when the audit agent
    /// itself failed to run or produced unusable output. Matching here lets
    /// the audit-stage score discriminate "real blocking findings" (LEGITIMATE,
    /// the loop is doing its job) from "the auditor died" (INFRA failure).
    /// </summary>
    private static readonly HashSet<string> AuditorInfraFindingTitles = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "review agent failed to run",
        "review agent produced invalid JSON",
    };

    private const string DidNotWriteResultPrefix = "agent did not write";

    // RequiredBuildGate.ToAuditResult and RunForAuditAsync emit findings titled
    // "required build unavailable: {DisplayCommand}" when the build verifier
    // itself could not run (probe Unavailable, verifier missing, mount timeout,
    // etc.) — that's the audit-stage infra signal we want to catch. The matching
    // "required build failed: {DisplayCommand}" title represents a real,
    // legitimate blocking finding (the build genuinely broke) and must NOT be
    // mapped to infra. Match by prefix because the title carries the command
    // string as a suffix.
    private const string RequiredBuildUnavailablePrefix = "required build unavailable:";

    public static TransitionHealthReport Compute(
        TransitionDataSnapshot snapshot,
        DateTimeOffset now,
        TransitionHealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);

        var windowEnd = now;
        var windowStart = now - options.Window;

        var transitions = new List<TransitionRecord>();

        foreach (var row in snapshot.Involvements)
        {
            if (row.EndedAt < windowStart || row.EndedAt > windowEnd)
                continue;
            var classified = ClassifyInvolvement(row);
            if (classified is { } t) transitions.Add(t);
        }

        foreach (var row in snapshot.AuditReports)
        {
            if (row.EndedAt < windowStart || row.EndedAt > windowEnd)
                continue;
            transitions.Add(ClassifyAuditReport(row));
        }

        foreach (var row in snapshot.TerminalFailures)
        {
            if (row.UpdatedAt < windowStart || row.UpdatedAt > windowEnd)
                continue;
            var classified = ClassifyTerminalFailure(row);
            if (classified is { } t) transitions.Add(t);
        }

        if (options.MaxTransitions is { } cap && transitions.Count > cap)
        {
            transitions.Sort(static (a, b) => b.OccurredAt.CompareTo(a.OccurredAt));
            transitions.RemoveRange(cap, transitions.Count - cap);
        }

        return Aggregate(transitions, windowStart, windowEnd, options);
    }

    internal static TransitionRecord? ClassifyInvolvement(TransitionInvolvementRow row)
    {
        if (string.IsNullOrEmpty(row.Outcome))
            return null;

        var stage = StageForInvolvementPhase(row.Phase);
        if (stage is null)
            return null;

        // The Audit stage's classification comes from audit_reports — the
        // involvement row alone cannot distinguish "auditor died" from "real
        // findings reported" (both leave the involvement at outcome=success
        // for the LlmReviewAuditor's did-not-write / invalid-JSON branches,
        // because the agent itself exited cleanly). Skip audit involvement
        // rows here so the audit-stage score is driven by audit_reports.
        if (stage == TransitionStage.Audit)
            return null;

        if (string.Equals(row.Outcome, AgentInvolvementOutcomes.Success, StringComparison.Ordinal))
            return new TransitionRecord(stage, TransitionClassification.Legitimate, null, row.EndedAt);

        if (!AgentInvolvementOutcomes.TryParseFailure(row.Outcome, out var failureCategory))
        {
            // Includes "error" (transient default before a path-specific
            // outcome was set) and any future label we have not classified
            // yet. Drop rather than guess.
            return null;
        }

        if (failureCategory is AgentInvolvementFailureCategory.Cancelled)
        {
            // Operator-driven cancel: not an infra failure, not a legitimate
            // forward step. Excluded from scoring.
            return null;
        }

        if (failureCategory is AgentInvolvementFailureCategory.SemanticIncompatible)
        {
            // The conflict-rework agent declared the upstream/downstream
            // branches semantically irreconcilable. The agent did its job —
            // this is a real, intended disposition, not an infra failure.
            return null;
        }

        return new TransitionRecord(
            stage,
            TransitionClassification.InfraFailure,
            AgentInvolvementOutcomes.InfraKind(failureCategory),
            row.EndedAt);
    }

    internal static TransitionRecord ClassifyAuditReport(TransitionAuditReportRow row)
    {
        foreach (var title in row.FindingTitles)
        {
            if (string.IsNullOrEmpty(title))
                continue;
            if (AuditorInfraFindingTitles.Contains(title))
                return new TransitionRecord(
                    TransitionStage.Audit,
                    TransitionClassification.InfraFailure,
                    "auditor_failed",
                    row.EndedAt);
            if (title.StartsWith(DidNotWriteResultPrefix, StringComparison.OrdinalIgnoreCase))
                return new TransitionRecord(
                    TransitionStage.Audit,
                    TransitionClassification.InfraFailure,
                    "auditor_failed",
                    row.EndedAt);
            if (title.StartsWith(RequiredBuildUnavailablePrefix, StringComparison.OrdinalIgnoreCase))
                return new TransitionRecord(
                    TransitionStage.Audit,
                    TransitionClassification.InfraFailure,
                    "build_unavailable",
                    row.EndedAt);
        }

        // Either no findings at all (the auditor passed) or only real findings
        // (which makes the next audit→rework loop iteration the audit-system
        // doing its job — LEGITIMATE).
        return new TransitionRecord(
            TransitionStage.Audit,
            TransitionClassification.Legitimate,
            null,
            row.EndedAt);
    }

    internal static TransitionRecord? ClassifyTerminalFailure(TransitionTerminalFailureRow row)
    {
        if (row.State == (int)WorkItemState.MergeConflictResolutionFailed)
        {
            return new TransitionRecord(
                TransitionStage.Terminal,
                TransitionClassification.InfraFailure,
                "merge_conflict_resolution_failed",
                row.UpdatedAt);
        }

        if (row.State == (int)WorkItemState.AbandonedAfterRecoveryAttempts)
        {
            // The recovery loop exhausted MaxRecoveryAttempts after successive
            // host shutdowns / worker-died-without-checkpoint events without
            // the item ever completing. By the task taxonomy this is the
            // "worker-died-without-preempt-checkpoint" infra signature.
            return new TransitionRecord(
                TransitionStage.Terminal,
                TransitionClassification.InfraFailure,
                "abandoned_after_recovery_attempts",
                row.UpdatedAt);
        }

        if (row.State != (int)WorkItemState.Failed)
            return null;

        return row.FailureKind switch
        {
            "quota" => Infra("quota"),
            "timeout" => Infra("timeout"),
            "agent" => Infra("agent"),
            "agent_unavailable" => Infra("agent_unavailable"),
            "agent_routing_unavailable" => Infra("agent_routing_unavailable"),
            "infrastructure" => Infra("infrastructure"),
            "configuration" => Infra("configuration"),
            // "build", "cancelled", and "other" are intentionally not scored:
            //  - build is the RequiredBuildGate catching agent work-product
            //    that left the branch non-compiling — a work-quality failure,
            //    the gate working as designed. The infra-equivalent signal is
            //    failureKind="infrastructure" (RequiredBuildVerificationUnavailable);
            //    counting "build" as infra would conflate the two and contradict
            //    the audit-stage taxonomy, which maps "required build failed:"
            //    findings to LEGITIMATE and only "required build unavailable:"
            //    findings to InfraFailure.
            //  - cancelled is operator intent, not infra health.
            //  - other is the catch-all label PipelineRunner uses for failures
            //    we have not yet classified; counting it as infra would
            //    over-pessimise the score, counting it as legitimate would
            //    under-pessimise. Document and exclude.
            _ => null,
        };

        TransitionRecord Infra(string kind) => new(
            TransitionStage.Terminal,
            TransitionClassification.InfraFailure,
            kind,
            row.UpdatedAt);
    }

    internal static string? StageForInvolvementPhase(string phase)
    {
        if (string.IsNullOrEmpty(phase))
            return null;
        if (phase.StartsWith("audit:", StringComparison.Ordinal)
            || string.Equals(phase, "audit", StringComparison.Ordinal))
            return TransitionStage.Audit;
        if (string.Equals(phase, "work", StringComparison.Ordinal))
            return TransitionStage.Work;
        if (string.Equals(phase, "rework", StringComparison.Ordinal))
            return TransitionStage.Rework;
        if (string.Equals(phase, "merge", StringComparison.Ordinal)
            || phase.Contains("conflict", StringComparison.OrdinalIgnoreCase))
            return TransitionStage.Merge;
        return null;
    }

    private static TransitionHealthReport Aggregate(
        IReadOnlyList<TransitionRecord> transitions,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TransitionHealthOptions options)
    {
        var perStage = new Dictionary<string, StageAccumulator>(StringComparer.Ordinal);
        foreach (var stage in TransitionStage.AllOrdered)
            perStage[stage] = new StageAccumulator();

        var infraByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var legitimate = 0;
        var infra = 0;

        foreach (var t in transitions)
        {
            if (t.Classification == TransitionClassification.Skipped)
                continue;

            // perStage is pre-populated with every TransitionStage.AllOrdered
            // constant above, and every classifier path emits one of those
            // same constants. Indexer access is therefore safe.
            var acc = perStage[t.Stage];

            if (t.Classification == TransitionClassification.Legitimate)
            {
                acc.Legitimate++;
                legitimate++;
            }
            else
            {
                acc.InfraFailure++;
                infra++;
                if (t.InfraFailureKind is { } kind)
                {
                    infraByKind.TryGetValue(kind, out var prev);
                    infraByKind[kind] = prev + 1;
                    acc.InfraByKind.TryGetValue(kind, out var prevStage);
                    acc.InfraByKind[kind] = prevStage + 1;
                }
            }
        }

        var total = legitimate + infra;
        var score = total == 0 ? 1.0 : (double)legitimate / total;
        var infraRate = total == 0 ? 0.0 : (double)infra / total;

        var stages = new List<TransitionStageBreakdown>(perStage.Count);
        foreach (var stage in TransitionStage.AllOrdered)
        {
            var acc = perStage[stage];
            var stageTotal = acc.Legitimate + acc.InfraFailure;
            stages.Add(new TransitionStageBreakdown
            {
                Stage = stage,
                Legitimate = acc.Legitimate,
                InfraFailure = acc.InfraFailure,
                Score = stageTotal == 0 ? 1.0 : (double)acc.Legitimate / stageTotal,
                InfraByKind = acc.InfraByKind,
            });
        }

        var worstStage = stages
            .Where(s => s.InfraFailure > 0)
            .OrderByDescending(s => s.InfraFailure)
            .ThenBy(s => s.Stage, StringComparer.Ordinal)
            .Select(s => s.Stage)
            .FirstOrDefault();

        return new TransitionHealthReport
        {
            Score = score,
            InfraFailureRate = infraRate,
            TotalTransitions = total,
            LegitimateTransitions = legitimate,
            InfraFailureTransitions = infra,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            WindowDuration = options.Window,
            MaxTransitions = options.MaxTransitions,
            WorstStage = worstStage,
            Stages = stages,
            InfraByKind = infraByKind,
        };
    }

    private sealed class StageAccumulator
    {
        public int Legitimate;
        public int InfraFailure;
        public readonly Dictionary<string, int> InfraByKind = new(StringComparer.Ordinal);
    }
}
