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
        var composed = composer.Compose(project, new ScriptedAgent([]));
        Assert.IsType<GraphicalSmokeAuditor>(Assert.Single(composed));
    }
}
