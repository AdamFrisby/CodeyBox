using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using SupervisionPage = CodeyBox.Admin.Web.Components.Pages.Supervision;

namespace CodeyBox.Admin.Tests;

public sealed class SupervisionPageTests : BunitContext
{
    public SupervisionPageTests()
    {
        Services.AddSingleton(new OrchestratorHubSettings("", null));
    }

    [Fact]
    public void Supervision_ShowsRetainedCodeyBoxCommandsForLateJoiners()
    {
        var fake = new FakeApiClient([]);
        fake.AgentSupervisionSessionsOverride = new AgentSupervisionSessionsResponse
        {
            Enabled = true,
            Sessions =
            [
                new AgentSupervisionSessionDto
                {
                    SessionId = "ags-session-1",
                    WorkItemId = "wi-1",
                    ProjectId = "project",
                    Phase = "work",
                    Iteration = 1,
                    Agent = "claude",
                    SandboxId = "sandbox",
                    WorkingDirectory = "/work",
                    Source = "pipeline",
                    StartedAt = DateTimeOffset.UtcNow,
                    State = "running",
                    AcceptingInjections = true,
                    OutputTail = "agent output tail",
                    RecentCommands =
                    [
                        new AgentSupervisionCommandRecordDto
                        {
                            Kind = "autonomous",
                            SentAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                            Prompt = "CodeyBox prompt sent before dashboard opened",
                        },
                    ],
                },
            ],
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<SupervisionPage>();

        Assert.Contains("[codeybox:autonomous]", cut.Markup);
        Assert.Contains("CodeyBox prompt sent before dashboard opened", cut.Markup);
        Assert.Contains("agent output tail", cut.Markup);
    }
}
