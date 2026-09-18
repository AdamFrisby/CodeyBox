using CodeyBox.Api;
using CodeyBox.Audit;
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

public sealed class PipelineOptionsWiringTests
{
    [Fact]
    public void ProgramMapsConfiguredPhaseAbsoluteTimeoutMultiplierIntoPipelineOptions()
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:PhaseAbsoluteTimeoutMultiplier"] = "2.5",
        });

        var options = factory.Services.GetRequiredService<PipelineOptions>();

        Assert.Equal(2.5, options.PhaseAbsoluteTimeoutMultiplier);
    }

    [Fact]
    public void ProgramAcceptsMinimumPhaseAbsoluteTimeoutMultiplier()
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:PhaseAbsoluteTimeoutMultiplier"] = "1.0",
        });

        var options = factory.Services.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;

        Assert.Equal(1.0, options.PhaseAbsoluteTimeoutMultiplier);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void ProgramRejectsInvalidPhaseAbsoluteTimeoutMultiplier(string value)
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:PhaseAbsoluteTimeoutMultiplier"] = value,
        });

        var ex = Assert.Throws<OptionsValidationException>(() =>
            _ = factory.Services.GetRequiredService<IOptions<CodeyBoxOptions>>().Value);

        Assert.Contains("PhaseAbsoluteTimeoutMultiplier", ex.Message);
    }

    [Fact]
    public void ProgramMapsConfiguredRequiredBuildVerificationTimeoutIntoPipelineOptions()
    {
        // Operators must be able to tune the required-build gate's per-call
        // ceiling without recompiling — a very large .NET solution may need
        // longer than the 15-min default, and infrastructure degradation may
        // call for a tighter ceiling. Pin the CodeyBoxOptions →
        // PipelineOptions mapping so a future refactor cannot silently
        // drop the knob and pin the operator to the embedded default.
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:RequiredBuildVerificationTimeoutSeconds"] = "1800",
        });

        var options = factory.Services.GetRequiredService<PipelineOptions>();

        Assert.Equal(TimeSpan.FromMinutes(30), options.RequiredBuildVerificationTimeout);
    }

    [Fact]
    public void ProgramDefaultsRequiredBuildVerificationTimeoutWhenUnset()
    {
        // Default must remain 15 minutes (Pipeline embedded default and the
        // CodeyBoxOptions default agree). A drift between the two would
        // confuse operators reading config vs. observed behavior.
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>());

        var options = factory.Services.GetRequiredService<PipelineOptions>();

        Assert.Equal(TimeSpan.FromMinutes(15), options.RequiredBuildVerificationTimeout);
    }

    [Fact]
    public void ProgramFloorsRequiredBuildVerificationTimeoutBelowMinimum()
    {
        // Sub-minute timeouts would make the gate effectively impossible to
        // pass even on the smallest .NET solution; clamp to a 60 s floor at
        // the wiring boundary so an accidental misconfig fails-safe to an
        // overrun-prone gate rather than a never-passes one.
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:RequiredBuildVerificationTimeoutSeconds"] = "5",
        });

        var options = factory.Services.GetRequiredService<PipelineOptions>();

        Assert.Equal(TimeSpan.FromSeconds(60), options.RequiredBuildVerificationTimeout);
    }

    [Fact]
    public void ProgramBindsBuildScriptAuditTimeoutOptions()
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:BuildScriptAudit:TimeoutSeconds"] = "1200",
        });

        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<BuildScriptAuditorOptions>>()
            .CurrentValue;

        Assert.Equal(1200, options.TimeoutSeconds);
    }

    [Fact]
    public void ProgramBindsConfiguredMaxPlanReviewIterationsIntoHotReloadableSnapshot()
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:PipelineTuning:MaxPlanReviewIterations"] = "7",
        });

        var options = factory.Services.GetRequiredService<PipelineTuningSnapshot>().Current;

        Assert.Equal(7, options.MaxPlanReviewIterations);
    }

    [Fact]
    public void ProgramRejectsInvalidMaxPlanReviewIterations()
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:PipelineTuning:MaxPlanReviewIterations"] = "0",
        });

        var ex = Assert.Throws<OptionsValidationException>(() =>
            _ = factory.Services.GetRequiredService<IOptions<CodeyBoxOptions>>().Value);

        Assert.Contains("MaxPlanReviewIterations", ex.Message);
    }

    [Fact]
    public void ProgramBindsDeprecatedPlanReviewUseAuditorsKey()
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:PlanReview:UseAuditors"] = "false",
        });

        var options = factory.Services.GetRequiredService<IOptions<CodeyBoxOptions>>().Value;

        Assert.False(options.PlanReview.UseAuditors);
    }

    [Fact]
    public void ProgramMapsPreemptiveSelfReviewConfigIntoSessionDispatchOptions()
    {
        using var factory = new PipelineOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:ClaudeSession:Enabled"] = "true",
            ["CodeyBox:ClaudeSession:PreemptiveSelfReview:Enabled"] = "true",
        });

        var options = factory.Services.GetRequiredService<AgentSessionDispatchOptions>();

        Assert.True(options.Enabled);
        Assert.True(options.PreemptiveSelfReviewEnabled);
    }

    private sealed class PipelineOptionsWiringFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _extraConfig;
        private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-pipeline-options-");
        private string _dbPath => _scratch.DbPath("pipeline-options.db");

        public PipelineOptionsWiringFactory(Dictionary<string, string?> extraConfig)
        {
            _extraConfig = extraConfig;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var baseConfig = new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(_scratch.DirectoryPath, "test-agent-streams"),
                };
                foreach (var kvp in _extraConfig)
                    baseConfig[kvp.Key] = kvp.Value;
                cfg.AddInMemoryCollection(baseConfig);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
                TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
                _scratch.Dispose();
            base.Dispose(disposing);
        }
    }
}
