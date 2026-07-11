using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Audit.Llm.PlanAudit;

/// <summary>
/// A plan-stage <see cref="IAuditor"/> that runs one <see cref="PlanAuditTest"/>
/// from the plan-audit chain. It targets the PLAN artifact only, drives a
/// host-side text-only review model through the shared
/// <see cref="TextOnlyPlanReview"/> injection-safe gate, parses the structured
/// verdict, and maps it to an independent PASS/FAIL through
/// <see cref="PlanAuditVerdictMapper"/> — a single BLOCKER finding FAILs the
/// plan on its own, sending it back to re-plan, with no aggregate or
/// cross-auditor compromise verdict.
///
/// <para>The same class implements every test in the chain: the test-specific
/// criteria live in the injected <see cref="PlanAuditTest"/>, and the shared
/// reviewer framework lives in <see cref="PlanAuditChainFramework"/>. Per-project
/// relevance is handled by toggling the auditor off (e.g. ExcludedAuditors);
/// per-plan relevance is the reviewer's NOT_APPLICABLE output for that plan.</para>
/// </summary>
public sealed class PlanAuditChainAuditor : IAuditor
{
    private readonly PlanAuditChainAuditorOptions _opts;

    public PlanAuditChainAuditor(PlanAuditChainAuditorOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        _opts = opts;
    }

    public string Name => _opts.Test.AuditorName;
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;

    /// <summary>Plan-stage only — this auditor never reviews a code diff.</summary>
    public IReadOnlySet<AuditTarget> Targets => AuditTargets.PlanOnly;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Reject an unhandled future target loudly rather than silently reviewing
        // a code diff as a plan (the auditor only declares Plan, so this is a
        // defensive guard against a mis-wired call site).
        if (AuditTargetSemantics.Classify(context.EffectiveTarget) != AuditReviewStrategy.PlanReview)
        {
            return Blocking(
                "plan-audit auditor invoked off the plan target",
                $"{Name} targets the PLAN artifact only, but was invoked for target '{context.EffectiveTarget.Value}'.");
        }

        if (string.IsNullOrWhiteSpace(context.PlanArtifact))
        {
            return Blocking(
                "no plan artifact to review",
                "The plan-review context carried no PLAN artifact.");
        }

        var agent = context.AuditRunner ?? _opts.Agent;
        var (textOnlyAgent, unavailable) = TextOnlyPlanReview.ResolveRunner(agent, context.AuditCredential);
        if (textOnlyAgent is null)
        {
            return Unavailable(unavailable!);
        }

        var prompts = PlanAuditPromptBuilder.Build(_opts.Test, context.OriginalPrompt, context.PlanArtifact);
        var result = await textOnlyAgent.RunTextOnlyWithSystemPromptAsync(
            prompts.SystemPrompt,
            prompts.UserPrompt,
            context.AuditCredential,
            context.ModelId,
            context.ReasoningMode,
            ct,
            sandbox,
            workingDirectory);

        if (!result.Success)
        {
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "review agent failed to run",
                Description: result.Error ?? result.Summary)],
                RawOutput: result.Output,
                AgentStderr: result.Error,
                AgentSummary: result.Summary,
                AgentStdout: result.Output);
        }

        var raw = result.Output ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "review agent produced no verdict",
                Description: result.Summary ?? string.Empty)],
                RawOutput: result.Output,
                AgentStderr: result.Error,
                AgentSummary: result.Summary,
                AgentStdout: result.Output);
        }

        try
        {
            var verdict = PlanAuditVerdictParser.Parse(raw);
            return PlanAuditVerdictMapper.ToAuditResult(
                verdict,
                Name,
                rawOutput: raw,
                agentStderr: result.Error,
                agentSummary: result.Summary,
                agentStdout: result.Output);
        }
        catch (JsonException ex)
        {
            return new AuditResult(false, [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "review agent produced invalid JSON",
                Description: $"{ex.Message}\n---\n{Truncate(raw, 1024)}")],
                RawOutput: raw,
                AgentStderr: result.Error,
                AgentSummary: result.Summary,
                AgentStdout: result.Output);
        }
    }

    private AuditResult Blocking(string title, string description)
        => new(false, [new AuditFinding(Name, AuditSeverity.Error, title, description)]);

    private AuditResult Unavailable(string description)
        => new(
            false,
            [new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "review agent failed to run",
                Description: description)],
            AgentStderr: description,
            AgentSummary: "plan review agent capability or credential is unavailable");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Construction options for a <see cref="PlanAuditChainAuditor"/>.</summary>
public sealed record PlanAuditChainAuditorOptions
{
    /// <summary>The chain test this auditor runs.</summary>
    public required PlanAuditTest Test { get; init; }

    /// <summary>
    /// Default review runner. The pipeline supplies a per-invocation override on
    /// <see cref="AuditContext.AuditRunner"/> for cross-review; this is the
    /// fallback when no override is in effect.
    /// </summary>
    public required IAgentRunner Agent { get; init; }
}
