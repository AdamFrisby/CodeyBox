using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class GraphicalSmokeWiringTests
{
    [Fact]
    public void ProgramRegistersGraphicalSmokeAuditorForProjectComposition()
    {
        var project = new Project
        {
            Id = new ProjectId("gui"),
            DisplayName = "GUI",
            RepositoryUrl = "https://example.com/gui.git",
            GraphicalSandbox = true,
            Audit = new ProjectAudit
            {
                Languages = [],
                AuditTypes = [],
            },
        };

        using var factory = new WorkItemApiFactory(null, project);
        var registeredAuditors = factory.Services.GetServices<IAuditor>().ToArray();
        var composer = factory.Services.GetRequiredService<ProjectAuditorComposer>();

        Assert.Contains(registeredAuditors, a => a is GraphicalSmokeAuditor && a.Name == "gui:smoke");
        Assert.Contains(registeredAuditors, a => a is BuildScriptAuditor && a.Name == BuildScriptAuditor.AuditorName);
        var composed = composer.Compose(project, new ScriptedAgent([]));

        // gui:smoke is composed FIRST (prepended for graphical projects); the
        // always-on prompt-revision trailer, build-script, and mutation-rigor
        // auditors are appended after preset auditors (all auto-included when
        // registered), then the config-enabled-by-default plan-adherence
        // reviewer is appended last (it self-limits to planned items at run
        // time). With no language/auditType presets this project ends up with
        // all five registered always-on auditors composed.
        Assert.Equal(5, composed.Count);
        Assert.IsType<GraphicalSmokeAuditor>(composed[0]);
        Assert.IsType<PromptRevisionTrailerAuditor>(composed[1]);
        Assert.IsType<BuildScriptAuditor>(composed[2]);
        Assert.Equal("tests:mutation-rigor", composed[3].Name);
        Assert.Equal("plan:adherence", composed[4].Name);
    }

    [Fact]
    public void ProgramComposesBuildScriptAuditorForDefaultHeadlessProject()
    {
        var project = new Project
        {
            Id = new ProjectId("headless"),
            DisplayName = "Headless",
            RepositoryUrl = "https://example.com/headless.git",
            Audit = new ProjectAudit
            {
                Languages = [],
                AuditTypes = [],
            },
        };

        using var factory = new WorkItemApiFactory(null, project);
        var composer = factory.Services.GetRequiredService<ProjectAuditorComposer>();

        var composed = composer.Compose(project, new ScriptedAgent([]));

        Assert.Equal(4, composed.Count);
        Assert.IsType<PromptRevisionTrailerAuditor>(composed[0]);
        Assert.IsType<BuildScriptAuditor>(composed[1]);
        Assert.Equal("tests:mutation-rigor", composed[2].Name);
        Assert.Equal("plan:adherence", composed[3].Name);
        Assert.Contains(composed, a => a.Name == BuildScriptAuditor.AuditorName);
        Assert.DoesNotContain(composed, a => a.Name == "gui:smoke");
    }
}
