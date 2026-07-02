using System.Reflection;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// Wiring coverage for the first-class dotnet-test runner seam: the
/// <see cref="ITestRunnerAuditor"/> registered for the future test-selector
/// resolves from DI with the canonical command, and the shared
/// <see cref="Func{TestRunOptions}"/> accessor threads into both the
/// <see cref="ProjectAuditorComposer"/> and the DI-registered runner so
/// per-project override catalogs keep the hot-reloadable knobs.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class TestRunnerAuditorProgramWiringTests
{
    [Fact]
    public void ProgramRegistersTestRunnerAuditorWithCanonicalCommand()
    {
        using var factory = new WiringFactory();

        var runner = factory.Services.GetRequiredService<ITestRunnerAuditor>();

        // Default (all-tests, default options) command is the byte-identical
        // legacy dotnet-test invocation the selector seam enumerates against.
        Assert.Equal<string[]>(
            ["dotnet", "test", "--no-build"],
            [.. runner.BuildInvocation(TestSelection.All, TestRunOptions.Default)]);
        Assert.Equal(TestFramework.DotnetTest, runner.TestSuite.Framework);
        Assert.Equal<string[]>(
            ["dotnet", "test", "--no-build", "--list-tests"],
            [.. runner.TestSuite.EnumerationArgv]);
    }

    [Fact]
    public void ProgramSharesOneRunOptionsAccessorAcrossConsumers()
    {
        using var factory = new WiringFactory();

        // The accessor is registered once and shared; both the DI runner and any
        // other consumer resolve the same closure (single hot-reloadable source).
        var accessor1 = factory.Services.GetRequiredService<Func<TestRunOptions>>();
        var accessor2 = factory.Services.GetRequiredService<Func<TestRunOptions>>();
        Assert.Same(accessor1, accessor2);

        // The composer resolves (proves DI still selects the full constructor now
        // that it also takes the accessor) — a broken registration would throw here.
        var composer = factory.Services.GetRequiredService<ProjectAuditorComposer>();
        Assert.NotNull(composer);

        // ...and it actually captured the shared accessor. If the optional param
        // silently defaulted to null, per-project override catalogs would drop
        // the hot-reloadable blame-hang / idle-timeout knobs — the exact regression
        // this wiring guards against.
        var field = typeof(ProjectAuditorComposer).GetField(
            "_testRunOptions", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Same(accessor1, field!.GetValue(composer));
    }

    private sealed class WiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-testrunner-wiring-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                });
            });
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }
}
