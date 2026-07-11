using CodeyBox.Audit.Llm;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class ProjectAuditorComposerPlanAdherenceTests
{
    private static ProjectAuditorComposer Composer(Func<PlanAdherenceAuditorOptions>? planAdherence) =>
        new(new PresetCatalog(), [], NullLogger<ProjectAuditorComposer>.Instance,
            catalogOptions: null, testRunOptions: null, planAdherenceOptions: planAdherence);

    private static Project ProjectWith(params string[] excluded) => new()
    {
        Id = new ProjectId("alpha"),
        DisplayName = "Alpha",
        RepositoryUrl = "https://example.com/repo.git",
        Audit = new ProjectAudit
        {
            AuditTypes = ["security", "architecture"],
            ExcludedAuditors = excluded,
        },
    };

    [Fact]
    public void CodeTarget_Enabled_IncludesPlanAdherence()
    {
        var composer = Composer(() => new PlanAdherenceAuditorOptions { Enabled = true });

        var names = composer
            .ComposeForTarget(ProjectWith(), new FakeAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();

        Assert.Contains(PlanAdherenceAuditorOptions.DefaultName, names);
    }

    [Fact]
    public void PlanTarget_ExcludesPlanAdherence_BecauseItIsCodeOnly()
    {
        var composer = Composer(() => new PlanAdherenceAuditorOptions { Enabled = true });

        var names = composer
            .ComposeForTarget(ProjectWith(), new FakeAgent(), AuditTarget.Plan)
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain(PlanAdherenceAuditorOptions.DefaultName, names);
        // The architecture reviewer DOES run at the plan stage (PlanAndCode).
        Assert.Contains("architecture:llm-review", names);
    }

    [Fact]
    public void NoAccessor_FeatureOff_PlanAdherenceAbsent()
    {
        var composer = Composer(planAdherence: null);

        var names = composer
            .ComposeForTarget(ProjectWith(), new FakeAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain(PlanAdherenceAuditorOptions.DefaultName, names);
    }

    [Fact]
    public void Disabled_PlanAdherenceAbsent()
    {
        var composer = Composer(() => new PlanAdherenceAuditorOptions { Enabled = false });

        var names = composer
            .ComposeForTarget(ProjectWith(), new FakeAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain(PlanAdherenceAuditorOptions.DefaultName, names);
    }

    [Fact]
    public void ExcludedByName_PlanAdherenceRemoved()
    {
        var composer = Composer(() => new PlanAdherenceAuditorOptions { Enabled = true });

        var names = composer
            .ComposeForTarget(
                ProjectWith(PlanAdherenceAuditorOptions.DefaultName),
                new FakeAgent(),
                AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain(PlanAdherenceAuditorOptions.DefaultName, names);
    }

    [Fact]
    public void CustomName_Honoured()
    {
        var composer = Composer(() => new PlanAdherenceAuditorOptions { Enabled = true, Name = "plan:my-adherence" });

        var names = composer
            .ComposeForTarget(ProjectWith(), new FakeAgent(), AuditTarget.Code)
            .Select(a => a.Name)
            .ToArray();

        Assert.Contains("plan:my-adherence", names);
    }

    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Codex;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null, CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", "", null));
    }
}
