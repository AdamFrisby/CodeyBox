using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using CodeyBox.Api;

namespace CodeyBox.Tests;

/// <summary>
/// Startup-guard regression tests for changelog automation (opt-in per
/// <c>docs/operating/releases.md</c>). An unconfigured install must start
/// cleanly in Production; an explicitly enabled webhook without a secret must
/// still fail fast; and an enabled webhook must reject unsigned requests.
/// </summary>
public sealed class ChangelogStartupTests
{
    [Fact]
    public void ChangelogAutomation_IsDisabledByDefault()
    {
        Assert.False(new ChangelogOptions().Enabled);
    }

    [Fact]
    public async Task ProductionWithoutChangelogConfig_StartsCleanly()
    {
        using var factory = new ChangelogStartupFactory(
            environment: "Production",
            configuration: new Dictionary<string, string?>());
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public void ProductionWithChangelogEnabledAndNoSecret_FailsStartup()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:Changelog:Enabled"] = "true",
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Program.ValidateChangelogWebhookConfiguration(config, new StubHostEnvironment("Production")));

        Assert.Contains("CodeyBox:Changelog:GitHubWebhookSecretEnvVar", ex.Message);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void DisabledChangelog_StartsWithoutSecretInAnyEnvironment(string environment)
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        Program.ValidateChangelogWebhookConfiguration(config, new StubHostEnvironment(environment));
    }

    [Fact]
    public void EnabledChangelogWithSecret_StartsInProduction()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:Changelog:Enabled"] = "true",
            ["CodeyBox:Changelog:GitHubWebhookSecretEnvVar"] = "TEST_CHANGELOG_STARTUP_SECRET",
        });

        Program.ValidateChangelogWebhookConfiguration(config, new StubHostEnvironment("Production"));
    }

    [Fact]
    public void EnabledChangelogWithoutSecret_WarnsButStartsInDevelopment()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["CodeyBox:Changelog:Enabled"] = "true",
        });

        Program.ValidateChangelogWebhookConfiguration(config, new StubHostEnvironment("Development"));
    }

    [Fact]
    public async Task EnabledWebhook_RejectsRequestsWithoutValidSignature()
    {
        const string secretEnvVar = "TEST_CHANGELOG_STARTUP_SECRET";
        var previous = Environment.GetEnvironmentVariable(secretEnvVar);
        Environment.SetEnvironmentVariable(secretEnvVar, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        try
        {
            using var factory = new ChangelogStartupFactory(
                environment: "Production",
                configuration: new Dictionary<string, string?>
                {
                    ["CodeyBox:Changelog:Enabled"] = "true",
                    ["CodeyBox:Changelog:GitHubWebhookSecretEnvVar"] = secretEnvVar,
                });
            using var client = factory.CreateClient();

            using var missing = await client.PostAsync(
                "/webhooks/github/release",
                new StringContent("""{"zen":"ping"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

            using var badRequest = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github/release");
            badRequest.Headers.Add("X-GitHub-Event", "release");
            badRequest.Headers.Add("X-Hub-Signature-256", "sha256=badhash");
            badRequest.Content = new StringContent("""{"action":"published"}""", Encoding.UTF8, "application/json");
            using var bad = await client.SendAsync(badRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretEnvVar, previous);
        }
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}

internal sealed class StubHostEnvironment : IHostEnvironment
{
    public StubHostEnvironment(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "CodeyBox.Tests";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } =
        new PhysicalFileProvider(Path.GetTempPath());
}

internal sealed class ChangelogStartupFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly Dictionary<string, string?> _configuration;
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-changelog-startup-{Guid.NewGuid():N}.db");
    private readonly string _githubAppStorePath = Path.Combine(
        Path.GetTempPath(), $"codeybox-github-apps-startup-{Guid.NewGuid():N}");

    public ChangelogStartupFactory(
        string environment,
        Dictionary<string, string?> configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.Sources.Clear();
            var tmp = Path.GetTempPath();
            var config = new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                // Production-safe sandbox combo: shared-kernel process runner
                // is only admitted with explicit trust + risk acknowledgement.
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
                ["CodeyBox:WorkloadTrust"] = "Trusted",
                ["CodeyBox:AcknowledgeSharedKernelRisk"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                ["CodeyBox:GitHubAppStorePath"] = _githubAppStorePath,
            };

            foreach (var (key, value) in _configuration)
                config[key] = value;

            cfg.AddInMemoryCollection(config);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { File.Delete(_dbPath); } catch { }
            try { Directory.Delete(_githubAppStorePath, recursive: true); } catch { }
        }
    }
}
