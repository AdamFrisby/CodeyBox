using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Harness;
using CodeyBox.Projects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Standalone-path coverage for <c>admin-seeded serve</c>: the seeded instance
/// must boot from default configuration with no manual setup. Previously each
/// startup validation tripped in sequence (missing <c>RepositoryUrl</c>,
/// unbound <c>SeededFakeAgents</c> key, missing API key) and nothing exercised
/// this path outside the graphical-sandbox recipe, so every gap shipped.
/// </summary>
public sealed class AdminSeededStandaloneTests
{
    [Fact]
    public async Task SeededInstanceEnv_SatisfiesProjectValidation()
    {
        var (config, _) = BuildSeededConfig();
        var options = ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));

        // Throws InvalidOperationException ("missing 'RepositoryUrl'") when the
        // seeded projects do not carry every key project validation requires.
        using var repo = new ProjectRepository(Options.Create(options));
        var projects = await repo.ListAsync();

        Assert.Equal(2, projects.Count);
        Assert.Contains(projects, p => p.Id.Value == "seeded-shop");
        Assert.Contains(projects, p => p.Id.Value == "seeded-portal");
    }

    [Fact]
    public void SeededInstanceEnv_DoesNotTripUnboundKeyValidator()
    {
        var (config, _) = BuildSeededConfig();

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        Assert.Empty(reports);
    }

    [Fact]
    public void SeededFakeAgents_TypoStillFlagged()
    {
        // The SeededFakeAgents section must bind to its typed options, not sit
        // behind a blanket exemption: a typo inside it has to surface.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:SeededFakeAgents:Enabeld"] = "true",
            })
            .Build();

        var reports = UnboundConfigKeyHostedValidator.Inspect(config);

        var report = Assert.Single(reports);
        Assert.Equal("CodeyBox:SeededFakeAgents:Enabeld", report.Path);
    }

    [Fact]
    public async Task SeededProjects_CannotPublishUpstream()
    {
        var (config, _) = BuildSeededConfig();
        var options = ProjectsOptionsBinder.Bind(config.GetSection("CodeyBox"));
        using var repo = new ProjectRepository(Options.Create(options));
        var projects = await repo.ListAsync();

        Assert.NotEmpty(projects);
        foreach (var project in projects)
        {
            Assert.Equal("noop", project.Upstream.Kind);
            Assert.Contains(".invalid", project.RepositoryUrl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GenerateEphemeralApiKey_MeetsApiMinimumLength()
    {
        var first = AdminSeededCommand.GenerateEphemeralApiKey();
        var second = AdminSeededCommand.GenerateEphemeralApiKey();

        Assert.True(first.Length >= 32, $"Ephemeral key must satisfy the 32-char API minimum, was {first.Length}.");
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ResolveSeededApiKey_PrefersOperatorSuppliedKey()
    {
        const string operatorKey = "0123456789abcdef0123456789abcdef";
        using (new EnvironmentVariableScope(AdminSeededCommand.ApiKeyEnvVar, operatorKey))
        {
            Assert.Equal(operatorKey, AdminSeededCommand.ResolveSeededApiKey());
        }

        using (new EnvironmentVariableScope(AdminSeededCommand.ApiKeyEnvVar, null))
        {
            var generated = AdminSeededCommand.ResolveSeededApiKey();
            Assert.True(generated.Length >= 32, $"Generated key must satisfy the 32-char API minimum, was {generated.Length}.");
        }
    }

    [Fact]
    public void SeededAdminEnv_CarriesApiBaseUrlAndSharedKey()
    {
        const string apiUrl = "http://localhost:5050";
        const string apiKey = "0123456789abcdef0123456789abcdef";

        var adminEnv = AdminSeededCommand.SeededAdminEnv("http://localhost:5070", apiUrl, apiKey);

        Assert.Equal(apiUrl, adminEnv["CodeyBoxAdmin__ApiBaseUrl"]);
        Assert.Equal(apiKey, adminEnv[AdminSeededCommand.ApiKeyEnvVar]);
    }

    [Fact]
    public async Task SeededInstance_BootsEndToEnd_BothChildrenReachReadiness()
    {
        // Boots the real API host with exactly the seeded serve configuration
        // (strict unbound-key validation, real project repository, mandatory
        // API key) and asserts readiness the same way the serve supervisor
        // does: /healthz for the API child, plus proof that the Admin.Web
        // child's wiring (shared key + ApiBaseUrl) authenticates, which is the
        // Admin.Web readiness contract its anonymous root probe cannot cover.
        // (Admin.Web itself has no factory-addressable Program — top-level
        // statements — and its Development boot performs no startup validation
        // the seeded config could trip, so its side is covered via this
        // config-parity + live-auth proof.)
        var apiKey = AdminSeededCommand.GenerateEphemeralApiKey();
        using var keyScope = new EnvironmentVariableScope(AdminSeededCommand.ApiKeyEnvVar, apiKey);
        using var factory = new SeededApiFactory();
        var client = factory.CreateClient();

        using (var health = await client.GetAsync("/healthz"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        var repo = factory.Services.GetRequiredService<IProjectRepository>();
        var projects = await repo.ListAsync();
        Assert.Equal(2, projects.Count);
        Assert.All(projects, p => Assert.Equal("noop", p.Upstream.Kind));

        var adminEnv = AdminSeededCommand.SeededAdminEnv(
            "http://localhost:5070", "http://localhost:5050", apiKey);
        using (var authed = new HttpRequestMessage(HttpMethod.Get, "/events/schema"))
        {
            authed.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", adminEnv[AdminSeededCommand.ApiKeyEnvVar]);
            using var okResp = await client.SendAsync(authed);
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        }

        using (var anon = await client.GetAsync("/events/schema"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        }
    }

    private static (IConfigurationRoot Config, string DbPath) BuildSeededConfig()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "codeybox-test-seeded-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var dbPath = Path.Combine(dir, "admin-seed.db");
        var env = AdminSeededCommand.SeededInstanceEnv(
            dbPath, "42", new Dictionary<string, string>(StringComparer.Ordinal));
        var pairs = env.ToDictionary(
            kv => kv.Key.Replace("__", ":"),
            kv => (string?)kv.Value,
            StringComparer.Ordinal);
        pairs["CodeyBox:AuditLog:Path"] = Path.Combine(dir, "test-log.json");
        pairs["CodeyBox:AuditLog:AuditPath"] = Path.Combine(dir, "test-audit.json");
        pairs["CodeyBox:AgentStreams:Path"] = Path.Combine(dir, "agent-streams");
        return (new ConfigurationBuilder().AddInMemoryCollection(pairs).Build(), dbPath);
    }

    private sealed class SeededApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(),
            "codeybox-test-seeded-e2e-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var dbPath = Path.Combine(_dir, "admin-seed.db");
                var env = AdminSeededCommand.SeededInstanceEnv(
                    dbPath, "42", new Dictionary<string, string>(StringComparer.Ordinal));
                var pairs = env.ToDictionary(
                    kv => kv.Key.Replace("__", ":"),
                    kv => (string?)kv.Value,
                    StringComparer.Ordinal);
                pairs["CodeyBox:AuditLog:Path"] = Path.Combine(_dir, "test-log.json");
                pairs["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_dir, "test-audit.json");
                pairs["CodeyBox:AgentStreams:Path"] = Path.Combine(_dir, "agent-streams");
                cfg.AddInMemoryCollection(pairs);
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    Directory.Delete(_dir, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
