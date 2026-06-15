using CodeyBox.Agents;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Webhooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class AuthFailurePatternProgramWiringTests : IDisposable
{
    private readonly string _workspace;

    public AuthFailurePatternProgramWiringTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-auth-program-wiring-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task ProgramWiredCustomStderrAuthPattern_ReachesPipelineRunner_AndBenchesWorkAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var factory = new AuthPatternPipelineFactory(seed);

        factory.Agent.ScriptedFailures.Enqueue(new AgentResult(
            Success: true,
            Summary: "ok",
            Stdout: null,
            Stderr: "operator-only login prompt"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "custom auth pattern",
            Prompt = "do thing",
            BaseBranch = "main",
            Agent = AgentKind.Codex,
            PushUpstream = false,
        };

        var store = factory.Services.GetRequiredService<IWorkItemStore>();
        await store.CreateAsync(item);
        await factory.Services.GetRequiredService<PipelineRunner>().RunAsync(item, CancellationToken.None);

        var final = await store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("auth required from agent output", final.LastError);

        var availability = factory.Services.GetRequiredService<IAgentAvailabilityRegistry>()
            .GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("auth required from agent output", availability.Reason);

        var failed = Assert.Single(factory.Webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
        Assert.Equal(0, factory.InVmSmoke.ForceProbeCalls);
    }

    private sealed class AuthPatternPipelineFactory : WebApplicationFactory<Program>
    {
        private readonly string _seedRepoUrl;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-auth-pattern-wiring-{Guid.NewGuid():N}.db");

        public AuthPatternPipelineFactory(string seedRepoUrl) => _seedRepoUrl = seedRepoUrl;

        public ScriptableAgent Agent { get; } = new(AgentKind.Codex);
        public CapturingWebhookDispatcher Webhooks { get; } = new();
        public CorroboratingInVmSmokeGate InVmSmoke { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:SandboxProvider"] = "process",
                    ["CodeyBox:Smoke:Enabled"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuthFailurePatterns:codex:0:Pattern"] = "operator-only login prompt",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();

                services.RemoveAll<IAgentRegistry>();
                services.AddSingleton<IAgentRegistry>(new AgentRegistry([Agent]));

                services.RemoveAll<ICredentialProvider>();
                services.AddSingleton<ICredentialProvider>(new StaticCredentialProvider());

                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(new Project
                {
                    Id = new ProjectId("test-project"),
                    DisplayName = "Test",
                    RepositoryUrl = _seedRepoUrl,
                    DefaultBaseBranch = "main",
                    DefaultAgent = AgentKind.Codex,
                    SkipCredentialSmokeTest = true,
                    Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
                }));

                services.RemoveAll<IWebhookDispatcher>();
                services.AddSingleton<IWebhookDispatcher>(Webhooks);

                services.RemoveAll<IInVmSmokeGate>();
                services.AddSingleton<IInVmSmokeGate>(InVmSmoke);

                services.RemoveAll<IRequiredBuildVerifier>();
                services.AddSingleton<IRequiredBuildVerifier>(TestRequiredBuildVerifier.NotApplicable);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }

    public sealed class CorroboratingInVmSmokeGate : IInVmSmokeGate
    {
        public int ForceProbeCalls { get; private set; }
        public bool Enabled => true;

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct) =>
            Task.FromResult(new AgentAvailability(true, null, null));

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct) =>
            ForceProbeAsync(kind, default, ct);

        public Task<AgentAvailability?> ForceProbeAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
        {
            ForceProbeCalls++;
            return Task.FromResult<AgentAvailability?>(
                new AgentAvailability(false, "smoke probe failed [persistent]: credential login required", null));
        }
    }
}
