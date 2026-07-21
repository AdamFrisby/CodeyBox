using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the JobTrack test-case export feature end to end through its seams: the
/// pure <see cref="JobTrackTestCaseMapper"/> projection, the pure
/// <see cref="JobTrackExportEndpointResolver"/> URL/token resolution, the
/// best-effort <see cref="JobTrackTestCaseExporter"/> orchestration (skip/retry/
/// swallow branches), the config-load validation in
/// <c>ProjectRepository.ResolveJobTrackExport</c>, and the real
/// <see cref="HttpJobTrackTestCaseClient"/> over a fake transport.
/// </summary>
public sealed class JobTrackTestCaseExportTests
{
    private static readonly WorkItemId TheWid = new(Guid.Parse("01931f0a-0000-7000-8000-0000000000aa"));
    private static string Wid => TheWid.ToString();

    private static TestCase Case(
        string id,
        AutomationKind? kind = AutomationKind.Unit,
        bool archived = false,
        string? label = null,
        string? artifact = null,
        string? conformance = null)
        => new()
        {
            Id = id,
            Name = $"case-{id}",
            Description = $"desc-{id}",
            SourceWorkItemId = Wid,
            AutomationKind = kind,
            IsArchived = archived,
            Label = label,
            ExecutableArtifactJson = artifact,
            ConformanceJson = conformance,
        };

    private static WorkItem Item(params (string ns, string value)[] externalIds)
        => new()
        {
            Id = TheWid,
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            ExternalIds = externalIds.ToDictionary(e => e.ns, e => e.value, StringComparer.OrdinalIgnoreCase),
        };

    private static Project ProjectWith(ProjectJobTrackExport export)
        => new()
        {
            Id = new ProjectId("proj"),
            DisplayName = "Proj",
            RepositoryUrl = "https://example.com/x.git",
            JobTrackExport = export,
        };

    private static ProjectJobTrackExport EnabledCfg(
        string baseUrl = "https://jobtrack.example.com",
        string? tokenEnvVar = null,
        string ns = "jobtrack",
        string? surfaceArea = null,
        int maxAttempts = 3)
        => new()
        {
            Enabled = true,
            BaseUrl = baseUrl,
            TokenEnvVar = tokenEnvVar,
            ExternalIdNamespace = ns,
            DefaultSurfaceArea = surfaceArea,
            MaxAttempts = maxAttempts,
            RetryBaseDelay = TimeSpan.Zero,
        };

    // ---- Mapper (pure) -------------------------------------------------

    [Fact]
    public void ToImport_projects_all_carried_fields()
    {
        var tc = Case("tc1", kind: AutomationKind.E2eReplay, label: "checkout",
            artifact: "{\"steps\":[]}", conformance: "{\"rule\":1}");

        var import = JobTrackTestCaseMapper.ToImport(tc, "TASK-9", defaultSurfaceArea: "Web/Checkout");

        Assert.Equal("tc1", import.ExternalSourceId);
        Assert.Equal("TASK-9", import.SourceTaskId);
        Assert.Equal("case-tc1", import.Name);
        Assert.Equal("desc-tc1", import.Description);
        Assert.Equal("e2e-replay", import.AutomationKind);
        Assert.Equal("{\"steps\":[]}", import.ExecutableArtifactJson);
        Assert.Equal("{\"rule\":1}", import.ConformanceJson);
        Assert.Equal("checkout", import.Label);
        Assert.Equal("Web/Checkout", import.SurfaceArea);
        Assert.False(import.IsArchived);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToImport_blank_default_surface_area_maps_to_null(string? surfaceArea)
        => Assert.Null(JobTrackTestCaseMapper.ToImport(Case("a"), "T1", surfaceArea).SurfaceArea);

    [Fact]
    public void ToImport_trims_surface_area()
        => Assert.Equal("Area", JobTrackTestCaseMapper.ToImport(Case("a"), "T1", "  Area  ").SurfaceArea);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ToImport_requires_non_blank_source_task_id(string? sourceTaskId)
        => Assert.ThrowsAny<ArgumentException>(() => JobTrackTestCaseMapper.ToImport(Case("a"), sourceTaskId!));

    [Fact]
    public void ToImport_null_test_case_throws()
        => Assert.Throws<ArgumentNullException>(() => JobTrackTestCaseMapper.ToImport(null!, "T1"));

    [Theory]
    [InlineData(AutomationKind.Manual, "manual")]
    [InlineData(AutomationKind.Unit, "unit")]
    [InlineData(AutomationKind.Integration, "integration")]
    [InlineData(AutomationKind.E2eReplay, "e2e-replay")]
    public void MapAutomationKind_maps_known_kinds(AutomationKind kind, string expected)
        => Assert.Equal(expected, JobTrackTestCaseMapper.MapAutomationKind(kind));

    [Fact]
    public void MapAutomationKind_null_maps_to_null()
        => Assert.Null(JobTrackTestCaseMapper.MapAutomationKind(null));

    // ---- Endpoint resolver (pure) --------------------------------------

    [Fact]
    public void TryResolve_composes_import_url_without_double_slash()
    {
        var cfg = EnabledCfg(baseUrl: "https://jobtrack.example.com/") with { ImportPath = "/api/test-cases/import" };

        var ok = JobTrackExportEndpointResolver.TryResolve(cfg, _ => null, out var endpoint, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("https://jobtrack.example.com/api/test-cases/import", endpoint!.ImportUri.ToString());
        Assert.Null(endpoint.Token);
    }

    [Fact]
    public void TryResolve_reads_token_from_environment()
    {
        var cfg = EnabledCfg(tokenEnvVar: "JT_TOKEN");

        var ok = JobTrackExportEndpointResolver.TryResolve(
            cfg, name => name == "JT_TOKEN" ? "secret-abc" : null, out var endpoint, out var error);

        Assert.True(ok);
        Assert.Equal("secret-abc", endpoint!.Token);
        Assert.Null(error);
    }

    [Fact]
    public void TryResolve_empty_token_env_var_fails()
    {
        var cfg = EnabledCfg(tokenEnvVar: "JT_TOKEN");

        var ok = JobTrackExportEndpointResolver.TryResolve(cfg, _ => "   ", out var endpoint, out var error);

        Assert.False(ok);
        Assert.Null(endpoint);
        Assert.Contains("JT_TOKEN", error);
    }

    [Theory]
    [InlineData("ftp://jobtrack.example.com")]
    [InlineData("not-a-url")]
    [InlineData("/relative/only")]
    public void TryResolve_non_http_base_url_fails(string baseUrl)
    {
        var cfg = EnabledCfg(baseUrl: baseUrl);

        var ok = JobTrackExportEndpointResolver.TryResolve(cfg, _ => null, out var endpoint, out var error);

        Assert.False(ok);
        Assert.Null(endpoint);
        Assert.NotNull(error);
    }

    // ---- Exporter orchestration (best-effort) --------------------------

    [Fact]
    public async Task Export_disabled_project_skips_without_touching_client()
    {
        var client = new FakeClient();
        var exporter = new JobTrackTestCaseExporter(new InMemoryTestCaseStore(), client, environment: _ => null);

        var summary = await exporter.ExportForWorkItemAsync(
            Item(("jobtrack", "T1")), ProjectWith(ProjectJobTrackExport.Disabled));

        Assert.Equal(JobTrackExportStatus.Disabled, summary.Status);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task Export_missing_jobtrack_id_skips()
    {
        var client = new FakeClient();
        var exporter = new JobTrackTestCaseExporter(new InMemoryTestCaseStore(), client, environment: _ => null);

        var summary = await exporter.ExportForWorkItemAsync(
            Item(("other", "X")), ProjectWith(EnabledCfg()));

        Assert.Equal(JobTrackExportStatus.NoJobTrackId, summary.Status);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task Export_misconfigured_token_skips()
    {
        var client = new FakeClient();
        var exporter = new JobTrackTestCaseExporter(
            new InMemoryTestCaseStore(), client, environment: _ => null);

        var summary = await exporter.ExportForWorkItemAsync(
            Item(("jobtrack", "T1")), ProjectWith(EnabledCfg(tokenEnvVar: "MISSING")));

        Assert.Equal(JobTrackExportStatus.Misconfigured, summary.Status);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task Export_upserts_every_case_and_carries_source_task_id()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("tc1"));
        await store.CreateAsync(Case("tc2"));
        var client = new FakeClient();
        var exporter = new JobTrackTestCaseExporter(store, client, environment: _ => null);

        var summary = await exporter.ExportForWorkItemAsync(
            Item(("jobtrack", "TASK-77")), ProjectWith(EnabledCfg()));

        Assert.Equal(JobTrackExportStatus.Completed, summary.Status);
        Assert.Equal(2, summary.Exported);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(new[] { "tc1", "tc2" }, client.Sent.Select(i => i.ExternalSourceId).OrderBy(x => x));
        Assert.All(client.Sent, i => Assert.Equal("TASK-77", i.SourceTaskId));
    }

    [Fact]
    public async Task Export_retries_up_to_max_attempts_then_counts_failed_without_throwing()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("tc1"));
        var client = new FakeClient { Throw = _ => new HttpRequestException("boom") };
        var exporter = new JobTrackTestCaseExporter(store, client, environment: _ => null);

        var summary = await exporter.ExportForWorkItemAsync(
            Item(("jobtrack", "T1")), ProjectWith(EnabledCfg(maxAttempts: 3)));

        Assert.Equal(JobTrackExportStatus.Completed, summary.Status);
        Assert.Equal(0, summary.Exported);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(3, client.Attempts); // initial + 2 retries
    }

    [Fact]
    public async Task Export_partial_failure_counts_both_and_completes()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("good"));
        await store.CreateAsync(Case("bad"));
        var client = new FakeClient
        {
            Throw = import => import.ExternalSourceId == "bad" ? new HttpRequestException("boom") : null,
        };
        var exporter = new JobTrackTestCaseExporter(store, client, environment: _ => null);

        var summary = await exporter.ExportForWorkItemAsync(
            Item(("jobtrack", "T1")), ProjectWith(EnabledCfg(maxAttempts: 2)));

        Assert.Equal(1, summary.Exported);
        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public async Task Export_uses_configured_namespace_for_source_task_id()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("tc1"));
        var client = new FakeClient();
        var exporter = new JobTrackTestCaseExporter(store, client, environment: _ => null);

        var summary = await exporter.ExportForWorkItemAsync(
            Item(("tracker", "ALT-5")), ProjectWith(EnabledCfg(ns: "tracker")));

        Assert.Equal(JobTrackExportStatus.Completed, summary.Status);
        Assert.Equal("ALT-5", Assert.Single(client.Sent).SourceTaskId);
    }

    [Fact]
    public async Task Export_propagates_cancellation()
    {
        var store = new InMemoryTestCaseStore();
        await store.CreateAsync(Case("tc1"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exporter = new JobTrackTestCaseExporter(store, new FakeClient(), environment: _ => null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportForWorkItemAsync(
                Item(("jobtrack", "T1")), ProjectWith(EnabledCfg()), cts.Token));
    }

    // ---- Config-load validation (ProjectRepository) --------------------

    private static ProjectRepository RepoWith(ProjectJobTrackExportConfig? export)
        => new(Options.Create(new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "alpha",
                    RepositoryUrl = "https://example.com/x.git",
                    JobTrackExport = export,
                },
            ],
        }));

    [Fact]
    public async Task Resolve_absent_config_is_disabled_sentinel()
    {
        using var repo = RepoWith(null);
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.Same(ProjectJobTrackExport.Disabled, p!.JobTrackExport);
    }

    [Fact]
    public async Task Resolve_enabled_binds_defaults()
    {
        using var repo = RepoWith(new ProjectJobTrackExportConfig
        {
            Enabled = true,
            BaseUrl = "https://jobtrack.example.com",
        });

        var p = await repo.GetAsync(new ProjectId("alpha"));

        var jt = p!.JobTrackExport;
        Assert.True(jt.Enabled);
        Assert.Equal("https://jobtrack.example.com", jt.BaseUrl);
        Assert.Equal(ProjectJobTrackExport.DefaultImportPath, jt.ImportPath);
        Assert.Equal(ProjectJobTrackExport.DefaultExternalIdNamespace, jt.ExternalIdNamespace);
        Assert.Equal(ProjectJobTrackExport.DefaultMaxAttempts, jt.MaxAttempts);
    }

    [Fact]
    public async Task Resolve_disabled_config_skips_validation_of_bad_url()
    {
        // Disabled: an invalid BaseUrl must not fail config load.
        using var repo = RepoWith(new ProjectJobTrackExportConfig { Enabled = false, BaseUrl = "not-a-url" });
        var p = await repo.GetAsync(new ProjectId("alpha"));
        Assert.False(p!.JobTrackExport.Enabled);
    }

    [Fact]
    public void Resolve_enabled_without_base_url_throws()
        => Assert.Throws<InvalidOperationException>(() =>
            RepoWith(new ProjectJobTrackExportConfig { Enabled = true }));

    [Fact]
    public void Resolve_enabled_with_non_http_base_url_throws()
        => Assert.Throws<InvalidOperationException>(() =>
            RepoWith(new ProjectJobTrackExportConfig { Enabled = true, BaseUrl = "ftp://x/y" }));

    [Fact]
    public void Resolve_enabled_with_invalid_namespace_throws()
        => Assert.Throws<InvalidOperationException>(() =>
            RepoWith(new ProjectJobTrackExportConfig
            {
                Enabled = true,
                BaseUrl = "https://jobtrack.example.com",
                ExternalIdNamespace = "bad namespace!",
            }));

    [Fact]
    public void Resolve_max_attempts_below_one_throws()
        => Assert.Throws<InvalidOperationException>(() =>
            RepoWith(new ProjectJobTrackExportConfig
            {
                Enabled = true,
                BaseUrl = "https://jobtrack.example.com",
                MaxAttempts = 0,
            }));

    // ---- HTTP client over a fake transport -----------------------------

    [Fact]
    public async Task HttpClient_posts_camel_case_payload_with_bearer_token()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpJobTrackTestCaseClient(new SingleClientFactory(handler));
        var endpoint = new JobTrackExportEndpoint
        {
            ImportUri = new Uri("https://jobtrack.example.com/api/test-cases/import"),
            Token = "secret-xyz",
        };
        var import = JobTrackTestCaseMapper.ToImport(Case("tc1"), "TASK-1");

        await client.UpsertAsync(endpoint, import);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(endpoint.ImportUri, req.Uri);
        Assert.Equal("Bearer", req.Authorization!.Scheme);
        Assert.Equal("secret-xyz", req.Authorization.Parameter);
        Assert.Contains("\"externalSourceId\":\"tc1\"", req.Body);
        Assert.Contains("\"sourceTaskId\":\"TASK-1\"", req.Body);
    }

    [Fact]
    public async Task HttpClient_omits_authorization_when_no_token()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpJobTrackTestCaseClient(new SingleClientFactory(handler));
        var endpoint = new JobTrackExportEndpoint
        {
            ImportUri = new Uri("https://jobtrack.example.com/api/test-cases/import"),
        };

        await client.UpsertAsync(endpoint, JobTrackTestCaseMapper.ToImport(Case("tc1"), "T1"));

        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task HttpClient_non_success_status_throws()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new HttpJobTrackTestCaseClient(new SingleClientFactory(handler));
        var endpoint = new JobTrackExportEndpoint
        {
            ImportUri = new Uri("https://jobtrack.example.com/api/test-cases/import"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UpsertAsync(endpoint, JobTrackTestCaseMapper.ToImport(Case("tc1"), "T1")));
    }

    // ---- Fakes ---------------------------------------------------------

    private sealed class FakeClient : IJobTrackTestCaseClient
    {
        public List<JobTrackTestCaseImport> Sent { get; } = [];
        public int Attempts { get; private set; }

        /// <summary>Returns an exception to throw for a given import, or null to succeed.</summary>
        public Func<JobTrackTestCaseImport, Exception?>? Throw { get; init; }

        public Task UpsertAsync(
            JobTrackExportEndpoint endpoint, JobTrackTestCaseImport import, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Attempts++;
            var ex = Throw?.Invoke(import);
            if (ex is not null)
                return Task.FromException(ex);
            Sent.Add(import);
            return Task.CompletedTask;
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method, request.RequestUri!, request.Headers.Authorization, body));
            return _respond(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method, Uri Uri, AuthenticationHeaderValue? Authorization, string Body);
}
