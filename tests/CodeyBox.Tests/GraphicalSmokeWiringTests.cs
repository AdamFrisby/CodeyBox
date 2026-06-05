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
        // always-on build-script and prompt-revision auditors are appended
        // after preset auditors. With no language/auditType presets this
        // project ends up with all three registered auditors composed.
        Assert.Equal(3, composed.Count);
        Assert.IsType<GraphicalSmokeAuditor>(composed[0]);
        Assert.IsType<BuildScriptAuditor>(composed[1]);
        Assert.IsType<PromptRevisionTrailerAuditor>(composed[2]);
    }
}
