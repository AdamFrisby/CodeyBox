using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class PlanReviewGateProgramWiringTests
{
    [Fact]
    public void Program_RegistersAuditorPlanReviewGateAsIPlanReviewGateByDefault()
    {
        using var factory = new PlanReviewGateWiringFactory();

        var gate = factory.Services.GetRequiredService<IPlanReviewGate>();

        Assert.IsType<AuditorPlanReviewGate>(gate);
    }

    [Fact]
    public void Program_IgnoresDeprecatedUseAuditorsFlagButStillBindsIt()
    {
        using var factory = new PlanReviewGateWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:PlanReview:UseAuditors"] = "false",
        });

        var gate = factory.Services.GetRequiredService<IPlanReviewGate>();
        var options = factory.Services.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;

        Assert.IsType<AuditorPlanReviewGate>(gate);
        Assert.False(options.PlanReview.UseAuditors);
    }

    [Fact]
    public void Program_RegistersPlanReviewGateAsSingleton()
    {
        using var factory = new PlanReviewGateWiringFactory();

        var first = factory.Services.GetRequiredService<IPlanReviewGate>();
        var second = factory.Services.GetRequiredService<IPlanReviewGate>();

        Assert.Same(first, second);
    }

    private sealed class PlanReviewGateWiringFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _extraConfig;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-planreviewgate-wiring-{Guid.NewGuid():N}.db");

        public PlanReviewGateWiringFactory(Dictionary<string, string?>? extraConfig = null)
        {
            _extraConfig = extraConfig ?? new Dictionary<string, string?>();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var tmp = Path.GetTempPath();
                var baseConfig = new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                };
                foreach (var kvp in _extraConfig)
                    baseConfig[kvp.Key] = kvp.Value;
                cfg.AddInMemoryCollection(baseConfig);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }
}
