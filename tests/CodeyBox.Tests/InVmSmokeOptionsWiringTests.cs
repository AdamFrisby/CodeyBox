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

namespace CodeyBox.Tests;

/// <summary>
/// Pins that <c>CodeyBox:Smoke:InVm</c> config keys for the timeout fields
/// (<c>ProvisionTimeoutSeconds</c>, <c>GateDeadlineSeconds</c>) are actually
/// bound into the DI <see cref="InVmSmokeOptions"/> singleton at startup.
/// The prober tests construct <see cref="InVmSmokeOptions"/> directly, so a
/// typo or omitted assignment in Program.cs would leave production using
/// defaults (180s gate, 120s provisioning) while every timeout test still
/// passed against the in-process default — this test catches the wiring gap.
/// </summary>
public sealed class InVmSmokeOptionsWiringTests
{
    [Fact]
    public void ProgramMapsConfiguredInVmSmokeTimeoutsIntoOptions()
    {
        using var factory = new InVmSmokeOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:Smoke:InVm:ProvisionTimeoutSeconds"] = "7",
            ["CodeyBox:Smoke:InVm:GateDeadlineSeconds"] = "11",
        });

        var opts = factory.Services.GetRequiredService<InVmSmokeOptions>();

        Assert.Equal(7, opts.ProvisionTimeoutSeconds);
        Assert.Equal(11, opts.GateDeadlineSeconds);
    }

    [Fact]
    public void ProgramKeepsInVmSmokeTimeoutDefaultsWhenUnset()
    {
        // Companion guard: when the config keys are absent, the bound options
        // must carry the documented defaults (120s provisioning / 180s gate
        // deadline) — so a regression that silently zeroed either field would
        // be caught too, not just a misnamed config key.
        using var factory = new InVmSmokeOptionsWiringFactory(new Dictionary<string, string?>());

        var opts = factory.Services.GetRequiredService<InVmSmokeOptions>();

        Assert.Equal(120, opts.ProvisionTimeoutSeconds);
        Assert.Equal(180, opts.GateDeadlineSeconds);
    }

    [Fact]
    public void ProgramMapsExplicitInVmSmokeNetworkProfileIntoOptions()
    {
        using var factory = new InVmSmokeOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:Smoke:InVm:NetworkProfile"] = "smoke-explicit",
        });

        var opts = factory.Services.GetRequiredService<InVmSmokeOptions>();

        Assert.Equal("smoke-explicit", opts.NetworkProfile);
    }

    [Fact]
    public void ProgramDoesNotFallbackToDefaultsWorkNetworkProfile()
    {
        using var factory = new InVmSmokeOptionsWiringFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:Defaults:NetworkProfiles:Work"] = "default-work",
        });

        var opts = factory.Services.GetRequiredService<InVmSmokeOptions>();

        Assert.Null(opts.NetworkProfile);
    }

    private sealed class InVmSmokeOptionsWiringFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _extraConfig;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-invm-options-{Guid.NewGuid():N}.db");

        public InVmSmokeOptionsWiringFactory(Dictionary<string, string?> extraConfig)
        {
            _extraConfig = extraConfig;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
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
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
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
