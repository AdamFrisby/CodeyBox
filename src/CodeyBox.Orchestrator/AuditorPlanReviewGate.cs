using System.Text;
using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Plan-review gate that reuses the auditor composition machinery: it composes
/// the project's <see cref="AuditTarget.Plan"/>-target auditors (the same
/// registry, preset selection, and config-driven active set as the code-audit
/// phase, just filtered by target) and evaluates the plan artifact with those
/// that implement <see cref="IPlanTextReviewer"/>.
///
/// <para>A plan is a short structured document, so the review is a single
/// text-only model call per reviewer — no sandbox or diff. Any blocking
/// ("error") finding rejects the plan; the pipeline then runs a plan-rework
/// turn and re-reviews. When no plan-target reviewers are configured (or the
/// routed agent has no text-only path), the gate approves after validating the
/// artifact shape — the same permissive behaviour as
/// <see cref="AlwaysPassPlanReviewGate"/>.</para>
/// </summary>
public sealed class AuditorPlanReviewGate : IPlanReviewGate
{
    private readonly ProjectAuditorComposer _composer;
    private readonly IProjectRepository _projects;
    private readonly IAgentRegistry _agents;
    private readonly ICredentialProvider _credentials;
    private readonly ILogger<AuditorPlanReviewGate> _log;

    public AuditorPlanReviewGate(
        ProjectAuditorComposer composer,
        IProjectRepository projects,
        IAgentRegistry agents,
        ICredentialProvider credentials,
        ILogger<AuditorPlanReviewGate> log)
    {
        _composer = composer;
        _projects = projects;
        _agents = agents;
        _credentials = credentials;
        _log = log;
    }

    public async ValueTask<PlanReviewDecision> ReviewAsync(
        PlanReviewRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Always validate the artifact shape first (matches the placeholder
        // gate's contract): a structurally-invalid plan can never be approved.
        _ = PlanArtifactDocument.ParseCanonical(request.PlanArtifact);

        var reviewers = await ResolveReviewersAsync(request, ct);
        if (reviewers is null || reviewers.Reviewers.Count == 0)
        {
            _log.LogInformation(
                "No plan-target reviewers available for work item {WorkItemId}; approving on artifact validity.",
                request.WorkItemId);
            return new PlanReviewDecision(true, "No plan-review auditors configured; plan approved on validity.");
        }

        var context = new AuditContext(
            request.WorkItemId,
            WorkBranch: string.Empty,
            BaseBranch: string.Empty,
            Iteration: 1,
            OriginalPrompt: request.Prompt,
            ModelId: request.ModelId,
            ReasoningMode: request.ReasoningMode,
            ProjectId: request.ProjectId.Value,
            Target: AuditTarget.Plan,
            PlanArtifact: request.PlanArtifact);

        var blocking = new List<AuditFinding>();
        var advisory = 0;
        foreach (var reviewer in reviewers.Reviewers)
        {
            ct.ThrowIfCancellationRequested();
            AuditResult result;
            try
            {
                result = await reviewer.ReviewPlanAsync(context, reviewers.Runner, reviewers.Credential, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "Plan reviewer threw for work item {WorkItemId}; treating as a blocking review failure.",
                    request.WorkItemId);
                blocking.Add(new AuditFinding(
                    ((IAuditor)reviewer).Name,
                    AuditSeverity.Error,
                    "plan reviewer failed",
                    ex.Message));
                continue;
            }

            var errorFindingCount = 0;
            foreach (var finding in result.Findings)
            {
                if (finding.Severity == AuditSeverity.Error)
                {
                    blocking.Add(finding);
                    errorFindingCount++;
                }
                else
                {
                    advisory++;
                }
            }

            // Honour the reviewer's explicit reject verdict even when it emitted
            // no error-severity finding (e.g. passed=false with only warnings, or
            // an empty findings list). This mirrors the code-audit convention
            // (`!result.Passed || Findings.Any(error)` is blocking) so the "plan
            // MUST pass before implementation" invariant cannot be bypassed by an
            // inconsistent-but-plausible model verdict that discards the boolean.
            if (!result.Passed && errorFindingCount == 0)
            {
                blocking.Add(new AuditFinding(
                    ((IAuditor)reviewer).Name,
                    AuditSeverity.Error,
                    "plan rejected by reviewer",
                    "The plan reviewer returned an explicit reject verdict (passed=false) without an error-severity finding."));
            }
        }

        if (blocking.Count == 0)
        {
            return new PlanReviewDecision(
                true,
                advisory == 0
                    ? "Plan approved by all plan-review auditors."
                    : $"Plan approved with {advisory} advisory note(s).");
        }

        return new PlanReviewDecision(
            false,
            $"Plan review found {blocking.Count} blocking issue(s).",
            RejectionReason: FormatFindings(blocking));
    }

    private async Task<ResolvedReviewers?> ResolveReviewersAsync(
        PlanReviewRequest request,
        CancellationToken ct)
    {
        if (request.Agent is not { } agentKind)
            return null;

        var project = await _projects.GetAsync(request.ProjectId, ct);
        if (project is null)
        {
            _log.LogWarning(
                "Plan review could not load project {ProjectId} for work item {WorkItemId}; approving on validity.",
                request.ProjectId, request.WorkItemId);
            return null;
        }

        if (!_agents.TryGet(agentKind, out var runner))
            return null;
        if (runner is not ITextOnlyAgentRunner textRunner)
        {
            _log.LogInformation(
                "Agent {Agent} has no text-only path; plan review is skipped for work item {WorkItemId}.",
                agentKind, request.WorkItemId);
            return null;
        }

        // Subscription CLIs (Cursor / Opencode) execute their text-only path
        // inside the work-item sandbox, which the host-only plan-review gate has
        // no way to supply. Their credential probe reports "available" whenever
        // the auth bundle is present, so without this check the gate would issue
        // a call guaranteed to fail and misclassify that infrastructure
        // condition as a blocking plan rejection — failing the work item after
        // MaxPlanReviewIterations. Degrade-and-skip instead (approve on
        // validity), exactly like the Claude-without-API-key path below.
        if (textRunner.TextOnlyRequiresSandbox)
        {
            _log.LogInformation(
                "Agent {Agent} requires a sandbox for text-only review, which the plan-review gate cannot provide; " +
                "skipping plan review for work item {WorkItemId} and approving on validity.",
                agentKind, request.WorkItemId);
            return null;
        }

        var credential = await _credentials.GetAsync(agentKind, ct);
        if (textRunner.GetTextOnlyUnavailabilityReason(credential) is { } reason)
        {
            _log.LogInformation(
                "Text-only review unavailable for agent {Agent} on work item {WorkItemId}: {Reason}; approving on validity.",
                agentKind, request.WorkItemId, reason);
            return null;
        }

        var reviewers = _composer
            .ComposeForTarget(project, runner, AuditTarget.Plan)
            .OfType<IPlanTextReviewer>()
            .ToList();

        return new ResolvedReviewers(reviewers, textRunner, credential);
    }

    private static string FormatFindings(IReadOnlyList<AuditFinding> findings)
    {
        var sb = new StringBuilder();
        sb.Append("The following blocking problems must be resolved in a revised plan:\n");
        foreach (var f in findings)
        {
            sb.Append("- [").Append(f.AuditorName).Append("] ").Append(f.Title);
            if (!string.IsNullOrWhiteSpace(f.Description))
                sb.Append(": ").Append(f.Description);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private sealed record ResolvedReviewers(
        IReadOnlyList<IPlanTextReviewer> Reviewers,
        ITextOnlyAgentRunner Runner,
        AgentCredential? Credential);
}
