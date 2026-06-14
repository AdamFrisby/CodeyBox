using System.Net;
using System.Net.Http.Headers;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
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
/// HTTP-level coverage for the Prometheus scrape exporter wired by
/// <c>CodeyBox:Otel:Prometheus</c>. Boots the real API host with a
/// trimmed-down hosted-service set so the metric provider and observable
/// gauges register but the orchestrator pickup loop stays quiet.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class PrometheusEndpointTests
{
    private const string ValidToken = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Enabled_ReturnsPrometheusExpositionWithExpectedSeries()
    {
        using var factory = new PrometheusFactory(new()
        {
            ["CodeyBox:Otel:Prometheus:Enabled"] = "true",
        });
        // Seed one work item so the labeled gauge has a series to emit.
        // The hosted service's StartAsync runs an initial refresh which
        // captures store state before the first scrape is served.
        await factory.WorkItemStore.CreateAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        }, CancellationToken.None);

        using var client = factory.CreateClient();
        using var resp = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // The Prometheus exporter ships text/plain with version=0.0.4 in the
        // media-type parameters; only the top-level media type is contractually
        // required here.
        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();

        // Dots in OTel instrument names become underscores in Prometheus.
        // The state-tagged work-item series has labels.
        Assert.Contains("codeybox_work_item_active{", body);
        Assert.Contains("state=\"Queued\"", body);
        // Worker pool gauges are emitted unconditionally — they don't depend
        // on store state, so they're the stable smoke-test signal.
        Assert.Contains("codeybox_workers_max", body);
        Assert.Contains("codeybox_workers_in_use", body);
        Assert.Contains("codeybox_sandbox_active", body);
        Assert.Contains("# HELP", body);
        Assert.Contains("# TYPE", body);
    }

    [Fact]
    public async Task Disabled_ReturnsNotFound()
    {
        // Default (Prometheus:Enabled omitted) keeps the endpoint off the
        // routing table — the route does not exist, not a 401.
        using var factory = new PrometheusFactory([]);
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task RequireApiKey_True_RejectsAnonymousAndAllowsBearer()
    {
        using var env = new ApiKeyEnvScope(ValidToken);
        using var factory = new PrometheusFactory(new()
        {
            ["CodeyBox:Otel:Prometheus:Enabled"] = "true",
            ["CodeyBox:Otel:Prometheus:RequireApiKey"] = "true",
            ["CodeyBox:DangerouslyDisableAuth"] = "false",
        });

        using var anonClient = factory.CreateClient();
        using var anonResp = await anonClient.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        using var authedClient = factory.CreateClient();
        authedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);
        using var authedResp = await authedClient.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, authedResp.StatusCode);
    }

    [Fact]
    public async Task RequireApiKey_False_AllowsAnonymousButExemptionIsScopedToExactPath()
    {
        using var env = new ApiKeyEnvScope(ValidToken);
        using var factory = new PrometheusFactory(new()
        {
            ["CodeyBox:Otel:Prometheus:Enabled"] = "true",
            ["CodeyBox:Otel:Prometheus:RequireApiKey"] = "false",
            ["CodeyBox:DangerouslyDisableAuth"] = "false",
        });
        using var client = factory.CreateClient();

        using var anonMetrics = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, anonMetrics.StatusCode);

        // The exemption is exact-path: any other route — including descendants
        // of /metrics that don't exist as endpoints — must still be governed
        // by the middleware (i.e. 401, not 404). If the exemption were a
        // prefix match, the middleware would short-circuit before routing and
        // we'd see 404 instead of 401 below.
        using var anonDescendant = await client.GetAsync("/metrics/leak");
        Assert.Equal(HttpStatusCode.Unauthorized, anonDescendant.StatusCode);

        using var anonOther = await client.GetAsync("/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, anonOther.StatusCode);
    }

    [Fact]
    public async Task CustomPath_IsRespectedAndExemptionTracksThePath()
    {
        using var env = new ApiKeyEnvScope(ValidToken);
        using var factory = new PrometheusFactory(new()
        {
            ["CodeyBox:Otel:Prometheus:Enabled"] = "true",
            ["CodeyBox:Otel:Prometheus:Path"] = "/internal/scrape",
            ["CodeyBox:Otel:Prometheus:RequireApiKey"] = "false",
            ["CodeyBox:DangerouslyDisableAuth"] = "false",
        });
        using var client = factory.CreateClient();

        using var customPath = await client.GetAsync("/internal/scrape");
        Assert.Equal(HttpStatusCode.OK, customPath.StatusCode);

        // The default path is NOT mapped when a custom path was configured.
        using var defaultPath = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, defaultPath.StatusCode);
    }

    private sealed class PrometheusFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _configuration;
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"codeybox-prom-test-{Guid.NewGuid():N}.db");
        private readonly List<EnvScope> _envScopes;

        public PrometheusFactory(Dictionary<string, string?> configuration)
        {
            _configuration = configuration;
            WorkItemStore = new SqliteWorkItemStore(_dbPath);

            // Program.cs reads the OpenTelemetry options block BEFORE
            // builder.Build() runs, i.e. before WebApplicationFactory's
            // ConfigureAppConfiguration callbacks fire. In-memory config keys
            // would therefore be invisible at the OTel registration point.
            // Mirror the OTel-related settings into environment variables
            // (which WebApplication.CreateBuilder picks up at construction
            // time) so the pre-Build read sees the same values the post-Build
            // bindings will produce.
            _envScopes = configuration
                .Where(kv => kv.Key.StartsWith("CodeyBox:Otel:", StringComparison.Ordinal))
                .Select(kv => new EnvScope(kv.Key.Replace(':', '_').Replace("_", "__"), kv.Value))
                .ToList();
        }

        public SqliteWorkItemStore WorkItemStore { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                var config = new Dictionary<string, string?>
                {
                    // Default to disabled auth; individual tests opt back in to
                    // exercise the middleware path.
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:SandboxProvider"] = "process",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"prom-test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"prom-test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"prom-test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"prom-test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:Changelog:Enabled"] = "false",
                };
                foreach (var (key, value) in _configuration)
                    config[key] = value;
                cfg.AddInMemoryCollection(config);
            });

            builder.ConfigureTestServices(services =>
            {
                // Drop every hosted service the production composition root
                // wires up, then add back ONLY the OTel observable gauges so
                // the meter provider has something to publish. This keeps the
                // orchestrator pickup loop / sweepers / background workers
                // quiet — they would otherwise race the SQLite store, agents,
                // and timing during the test.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IWorkItemStore>();
                services.AddSingleton<IWorkItemStore>(WorkItemStore);
                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());

                // Re-register the observable-metrics hosted service so the
                // OTel gauges fire when the Prometheus exporter scrapes. All
                // its dependencies (OrchestratorOptions, ISandboxProvider,
                // IWorkItemStore) are already registered by the composition root.
                services.AddHostedService<CodeyBoxObservableMetrics>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WorkItemStore.Dispose();
                foreach (var scope in _envScopes)
                    scope.Dispose();
                try { File.Delete(_dbPath); } catch { /* best effort */ }
            }
            base.Dispose(disposing);
        }

        private sealed class EnvScope : IDisposable
        {
            private readonly string _name;
            private readonly string? _prior;
            public EnvScope(string name, string? value)
            {
                _name = name;
                _prior = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
            public void Dispose() => Environment.SetEnvironmentVariable(_name, _prior);
        }
    }

    private sealed class ApiKeyEnvScope : IDisposable
    {
        private readonly string? _prior;
        public ApiKeyEnvScope(string value)
        {
            _prior = Environment.GetEnvironmentVariable("CODEYBOX_API_KEY");
            Environment.SetEnvironmentVariable("CODEYBOX_API_KEY", value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable("CODEYBOX_API_KEY", _prior);
    }
}
