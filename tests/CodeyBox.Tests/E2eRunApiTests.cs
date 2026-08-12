using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.MultipassRemote;
using CodeyBox.Sandbox.Process;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeyBox.Tests;

public sealed class E2eRunApiTests : IDisposable
{
    private readonly TestCaseApiFactory _factory = new();
    private readonly HttpClient _client;

    public E2eRunApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public void Program_wires_remote_ssh_pool_to_multipass_remote_provider()
    {
        using var factory = new E2ePoolWiringFactory("remote-ssh");

        var pool = Assert.IsType<MultiHostE2eExecutionPool>(factory.Services.GetRequiredService<IE2eExecutionPool>());

        Assert.Equal("remote-ssh[1]", pool.Name);
        Assert.IsType<MultipassRemoteSandboxProvider>(GetInnerProvider(pool));
    }

    [Fact]
    public void E2eExecutionOptions_default_pool_is_remote_ssh()
    {
        Assert.Equal("remote-ssh", new E2eExecutionOptions().PoolKind);
    }

    [Fact]
    public void Program_remote_e2e_pool_reads_e2e_specific_remote_config()
    {
        using var factory = new E2ePoolWiringFactory("remote-ssh", globalRemoteTarget: "coding@198.51.100.20", e2eRemoteTarget: "e2e@198.51.100.21");

        var pool = factory.Services.GetRequiredService<IE2eExecutionPool>();
        var provider = Assert.IsType<MultipassRemoteSandboxProvider>(GetInnerProvider(pool));
        var opts = ReadRemoteOptions(provider);

        Assert.Equal("e2e@198.51.100.21", opts.SshTarget);
    }

    [Fact]
    public void Program_single_host_remote_e2e_pool_applies_per_host_capacity()
    {
        using var factory = new E2ePoolWiringFactory(
            "remote-ssh",
            e2eRemoteTarget: "e2e@remote.example",
            e2eRemoteMaxConcurrent: 2);

        var pool = Assert.IsType<MultiHostE2eExecutionPool>(factory.Services.GetRequiredService<IE2eExecutionPool>());

        Assert.Equal("remote-ssh[1]", pool.Name);
        Assert.Equal(2, pool.MaxConcurrent);
    }

    [Fact]
    public void Program_wires_multi_host_remote_e2e_pool_from_plural_config()
    {
        using var factory = new E2ePoolWiringFactory(
            "remote-ssh",
            globalRemoteTarget: "coding@198.51.100.20",
            e2eRemoteTargets: ["e2e@198.51.100.10", "e2e@198.51.100.11"]);

        var pool = Assert.IsType<MultiHostE2eExecutionPool>(factory.Services.GetRequiredService<IE2eExecutionPool>());
        var source = Assert.IsAssignableFrom<IManagedSandboxProviderSource>(pool);

        Assert.Equal("remote-ssh[2]", pool.Name);
        Assert.Equal(2, source.ManagedSandboxProviders.Count);
        Assert.All(source.ManagedSandboxProviders, provider => Assert.IsType<MultipassRemoteSandboxProvider>(provider));
    }

    [Fact]
    public void Program_registers_e2e_provider_with_lifecycle_composite()
    {
        using var factory = new E2ePoolWiringFactory("remote-ssh");

        var pool = factory.Services.GetRequiredService<IE2eExecutionPool>();
        var e2eProvider = GetInnerProvider(pool);
        var composite = factory.Services.GetRequiredService<CompositeManagedSandboxProvider>();

        Assert.Contains(factory.Services.GetRequiredService<ISandboxProvider>(), composite.Providers);
        Assert.Contains(e2eProvider, composite.Providers);
    }

    [Fact]
    public void Program_registers_e2e_lifecycle_provider_even_when_remote_hosts_are_initially_unconfigured()
    {
        using var factory = new E2ePoolWiringFactory(
            "remote-ssh",
            e2eRemoteTarget: null,
            e2eEnabled: false);

        var pool = factory.Services.GetRequiredService<IE2eExecutionPool>();
        var e2eProvider = GetInnerProvider(pool);
        var composite = factory.Services.GetRequiredService<CompositeManagedSandboxProvider>();

        Assert.Contains(e2eProvider, composite.Providers);
    }

    [Fact]
    public void Program_rejects_enabled_remote_e2e_without_baseline_ref()
    {
        using var factory = new E2ePoolWiringFactory("remote-ssh", e2eEnabled: true, baselineImageRef: null);

        var ex = Assert.Throws<OptionsValidationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());
        Assert.Contains("BaselineImageRef", ex.Message);
    }

    [Fact]
    public void Program_rejects_enabled_remote_e2e_without_dedicated_target()
    {
        using var factory = new E2ePoolWiringFactory("remote-ssh", e2eEnabled: true, e2eRemoteTarget: null);

        var ex = Assert.Throws<OptionsValidationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());
        Assert.Contains("SshTarget", ex.Message);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("codeybox@127.0.0.1")]
    [InlineData("codeybox@[::1]")]
    public void Program_rejects_enabled_remote_e2e_when_target_is_loopback_or_localhost(string target)
    {
        using var factory = new E2ePoolWiringFactory(
            "remote-ssh",
            e2eRemoteTarget: target,
            e2eEnabled: true);

        var ex = Assert.Throws<OptionsValidationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());
        Assert.Contains("dedicated remote SSH host", ex.Message);
    }

    [Fact]
    public void Program_rejects_enabled_remote_e2e_when_target_is_orchestrator_host_name()
    {
        using var factory = new E2ePoolWiringFactory(
            "remote-ssh",
            e2eRemoteTarget: $"e2e@{Dns.GetHostName()}",
            e2eEnabled: true);

        var ex = Assert.Throws<OptionsValidationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());
        Assert.Contains("dedicated remote SSH host", ex.Message);
    }

    [Fact]
    public void Program_rejects_enabled_remote_e2e_when_network_profile_is_set()
    {
        using var factory = new E2ePoolWiringFactory("remote-ssh", e2eEnabled: true, networkProfile: "coding-net");

        var ex = Assert.Throws<OptionsValidationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());
        Assert.Contains("NetworkProfile", ex.Message);
    }

    [Fact]
    public void Program_rejects_enabled_remote_e2e_when_target_matches_coding_fleet()
    {
        using var factory = new E2ePoolWiringFactory(
            "remote-ssh",
            globalRemoteTarget: "same@remote.example",
            e2eRemoteTarget: "same@remote.example",
            e2eEnabled: true);

        var ex = Assert.Throws<OptionsValidationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());
        Assert.Contains("different SSH host", ex.Message);
    }

    [Fact]
    public void Program_rejects_enabled_remote_e2e_when_target_matches_coding_host_with_different_user()
    {
        using var factory = new E2ePoolWiringFactory(
            "remote-ssh",
            globalRemoteTarget: "coding@remote.example",
            e2eRemoteTarget: "e2e@remote.example",
            e2eEnabled: true);

        var ex = Assert.Throws<OptionsValidationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());
        Assert.Contains("different SSH host", ex.Message);
    }

    [Fact]
    public void Program_rejects_enabled_remote_e2e_when_target_alias_resolves_to_loopback()
    {
        using var factory = new E2ePoolWiringFactory(
            "remote-ssh",
            e2eRemoteTarget: "e2e-local-alias",
            e2eRemoteExtraSshOptions: ["HostName=127.0.0.1"],
            e2eEnabled: true);

        var ex = Assert.Throws<OptionsValidationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());
        Assert.Contains("dedicated", ex.Message);
    }

    [Fact]
    public void Program_rejects_enabled_remote_e2e_when_alias_resolves_to_coding_fleet_address()
    {
        using var factory = new E2ePoolWiringFactory(
            "remote-ssh",
            globalRemoteTarget: "coding-alias",
            globalRemoteExtraSshOptions: ["HostName=198.51.100.10"],
            e2eRemoteTarget: "e2e-alias",
            e2eRemoteExtraSshOptions: ["HostName=198.51.100.10"],
            e2eEnabled: true);

        var ex = Assert.Throws<OptionsValidationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());
        Assert.Contains("different SSH host", ex.Message);
    }

    [Fact]
    public void Program_registers_e2e_dispatcher_as_hosted_service()
    {
        using var factory = new E2eHostedServiceWiringFactory();

        var hosted = factory.Services.GetServices<IHostedService>();

        Assert.Contains(hosted, service => service.GetType() == typeof(E2eRunDispatcher));
    }

    [Fact]
    public void Program_wires_local_e2e_pool_to_separate_unadmitted_provider()
    {
        using var factory = new E2ePoolWiringFactory("local");

        var pool = factory.Services.GetRequiredService<IE2eExecutionPool>();
        var codingProvider = factory.Services.GetRequiredService<ISandboxProvider>();
        var e2eProvider = GetInnerProvider(pool);

        Assert.Equal("local", pool.Name);
        Assert.IsType<ProcessSandboxProvider>(e2eProvider);
        Assert.IsAssignableFrom<SandboxAdmissionControlledProvider>(codingProvider);
        Assert.NotSame(codingProvider, e2eProvider);
    }

    [Fact]
    public void Program_rejects_local_e2e_pool_outside_development()
    {
        using var env = ConfigureRequiredProductionChangelogSecret();
        using var factory = new E2ePoolWiringFactory("local", environment: "Production");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Services.GetRequiredService<IE2eExecutionPool>());

        Assert.Contains("PoolKind=local", ex.Message);
        Assert.Contains("development-only", ex.Message);
    }

    private static IDisposable ConfigureRequiredProductionChangelogSecret()
    {
        const string configKey = "CodeyBox__Changelog__GitHubWebhookSecretEnvVar";
        const string secretKey = "CODEYBOX_CHANGELOG_SECRET_TEST";
        var oldConfig = Environment.GetEnvironmentVariable(configKey);
        var oldSecret = Environment.GetEnvironmentVariable(secretKey);
        Environment.SetEnvironmentVariable(configKey, secretKey);
        Environment.SetEnvironmentVariable(secretKey, "test-secret");
        return new EnvScope(() =>
        {
            Environment.SetEnvironmentVariable(configKey, oldConfig);
            Environment.SetEnvironmentVariable(secretKey, oldSecret);
        });
    }

    private sealed class EnvScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    [Fact]
    public async Task E2eRun_routes_enqueue_list_get_cancel_and_summarise_batch()
    {
        var testCaseId = await SeedE2eCaseAsync("api-run-case");
        var batchId = Guid.NewGuid().ToString("N");

        var enqueue = await _client.PostAsJsonAsync("/e2eruns", new EnqueueE2eRunRequest(testCaseId, batchId));
        Assert.Equal(HttpStatusCode.Created, enqueue.StatusCode);
        var created = await enqueue.Content.ReadFromJsonAsync<E2eRunDto>();
        Assert.NotNull(created);
        Assert.Equal(testCaseId, created.TestCaseId);
        Assert.Equal(E2eRunStatus.Queued, created.Status);

        var get = await _client.GetAsync($"/e2eruns/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<E2eRunDto>();
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);

        var all = await _client.GetFromJsonAsync<E2eRunPageDto>("/e2eruns?limit=10");
        Assert.NotNull(all);
        Assert.Contains(all.Runs, r => r.Id == created.Id);

        var byCase = await _client.GetFromJsonAsync<E2eRunPageDto>($"/testcases/{testCaseId}/runs?limit=10");
        Assert.NotNull(byCase);
        Assert.Single(byCase.Runs, r => r.Id == created.Id);

        var cancel = await _client.PostAsync($"/e2eruns/{created.Id}/cancel", content: null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var canceled = await cancel.Content.ReadFromJsonAsync<E2eRunDto>();
        Assert.NotNull(canceled);
        Assert.Equal(E2eRunStatus.Canceled, canceled.Status);

        var cancelAgain = await _client.PostAsync($"/e2eruns/{created.Id}/cancel", content: null);
        Assert.Equal(HttpStatusCode.Conflict, cancelAgain.StatusCode);

        var summary = await _client.GetFromJsonAsync<BatchSummaryDto>($"/e2eruns/batches/{batchId}");
        Assert.NotNull(summary);
        Assert.Equal(batchId, summary.BatchId);
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Canceled);
        Assert.True(summary.Complete);

        var batchRuns = await _client.GetFromJsonAsync<E2eRunPageDto>($"/e2eruns/batches/{batchId}/runs?limit=10");
        Assert.NotNull(batchRuns);
        Assert.Single(batchRuns.Runs, r => r.Id == created.Id);
    }

    [Fact]
    public async Task E2eRun_bulk_enqueue_validates_before_creating_any_rows()
    {
        var valid = await SeedE2eCaseAsync("api-bulk-valid");

        var response = await _client.PostAsJsonAsync(
            "/e2eruns/bulk",
            new EnqueueBulkE2eRunsRequest([valid, "missing-case"], BatchId: "batch-atomic"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var runs = new List<E2eRun>();
        await foreach (var run in _factory.E2eRunStore.ListAsync())
            runs.Add(run);
        Assert.Empty(runs);
    }

    [Fact]
    public async Task E2eRun_enqueue_rejects_invalid_requests()
    {
        var missingId = await _client.PostAsJsonAsync("/e2eruns", new EnqueueE2eRunRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, missingId.StatusCode);

        var missingCase = await _client.PostAsJsonAsync("/e2eruns", new EnqueueE2eRunRequest("missing-case"));
        Assert.Equal(HttpStatusCode.NotFound, missingCase.StatusCode);

        var wrongKind = await SeedCaseAsync("api-wrong-kind", AutomationKind.Unit, "{}");
        var wrongKindResponse = await _client.PostAsJsonAsync("/e2eruns", new EnqueueE2eRunRequest(wrongKind));
        Assert.Equal(HttpStatusCode.BadRequest, wrongKindResponse.StatusCode);

        var missingArtifact = await SeedCaseAsync("api-missing-artifact", AutomationKind.E2eReplay, null);
        var missingArtifactResponse = await _client.PostAsJsonAsync("/e2eruns", new EnqueueE2eRunRequest(missingArtifact));
        Assert.Equal(HttpStatusCode.BadRequest, missingArtifactResponse.StatusCode);

        var nullBody = await _client.PostAsJsonAsync<EnqueueE2eRunRequest?>("/e2eruns", null);
        Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);

        var rejectedTarget = await SeedCaseAsync(
            "api-rejected-target",
            AutomationKind.E2eReplay,
            JsonSerializer.Serialize(new E2eReplayArtifact
            {
                Steps = [new E2eReplayStep { Action = "navigate", Target = "http://169.254.169.254/latest/meta-data" }],
            }));
        var rejectedTargetResponse = await _client.PostAsJsonAsync("/e2eruns", new EnqueueE2eRunRequest(rejectedTarget));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedTargetResponse.StatusCode);
    }

    [Fact]
    public async Task E2eRun_bulk_enqueue_rejects_invalid_request_shapes()
    {
        var empty = await _client.PostAsJsonAsync("/e2eruns/bulk", new EnqueueBulkE2eRunsRequest([]));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var blankEntry = await _client.PostAsJsonAsync("/e2eruns/bulk", new EnqueueBulkE2eRunsRequest([""]));
        Assert.Equal(HttpStatusCode.BadRequest, blankEntry.StatusCode);

        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:MaxBulkItems"] = "1",
                });
            });
        });
        using var client = customFactory.CreateClient();
        var tooMany = await client.PostAsJsonAsync("/e2eruns/bulk", new EnqueueBulkE2eRunsRequest(["a", "b"]));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task E2eRun_bulk_enqueue_rejects_wrong_kind_and_missing_artifact()
    {
        var wrongKind = await SeedCaseAsync("api-bulk-wrong-kind", AutomationKind.Unit, "{}");
        var wrongKindResponse = await _client.PostAsJsonAsync(
            "/e2eruns/bulk",
            new EnqueueBulkE2eRunsRequest([wrongKind]));
        Assert.Equal(HttpStatusCode.BadRequest, wrongKindResponse.StatusCode);

        var missingArtifact = await SeedCaseAsync("api-bulk-missing-artifact", AutomationKind.E2eReplay, null);
        var missingArtifactResponse = await _client.PostAsJsonAsync(
            "/e2eruns/bulk",
            new EnqueueBulkE2eRunsRequest([missingArtifact]));
        Assert.Equal(HttpStatusCode.BadRequest, missingArtifactResponse.StatusCode);
    }

    [Fact]
    public async Task E2eRun_endpoints_return_not_found_for_missing_resources()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/e2eruns/missing-run")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/e2eruns/missing-run/cancel", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/testcases/missing-case/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/e2eruns/batches/missing-batch")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/e2eruns/batches/missing-batch/runs")).StatusCode);
    }

    [Fact]
    public async Task E2eRun_list_normalizes_page_bounds_and_ignores_invalid_result_json()
    {
        var testCaseId = await SeedE2eCaseAsync("api-invalid-result-case");
        var run = new E2eRun
        {
            Id = Guid.NewGuid().ToString("N"),
            TestCaseId = testCaseId,
            Status = E2eRunStatus.Error,
            Result = "{ not-json",
        };
        await _factory.E2eRunStore.CreateAsync(run);

        var page = await _client.GetFromJsonAsync<E2eRunPageDto>("/e2eruns?offset=-5&limit=999999");

        Assert.NotNull(page);
        Assert.Equal(0, page.Offset);
        Assert.Equal(E2eExecutionOptions.MaximumListPageSize, page.Limit);
        var dto = Assert.Single(page.Runs, r => r.Id == run.Id);
        Assert.Null(dto.Result);
    }

    [Fact]
    public async Task E2eRun_cancel_running_run_signals_registry()
    {
        var testCaseId = await SeedE2eCaseAsync("api-running-cancel");
        var runId = Guid.NewGuid().ToString("N");
        await _factory.E2eRunStore.CreateAsync(new E2eRun
        {
            Id = runId,
            TestCaseId = testCaseId,
            Status = E2eRunStatus.Running,
            SandboxId = "sandbox-a",
        });
        var registry = _factory.Services.GetRequiredService<E2eRunCancellationRegistry>();
        using var cts = registry.Register(runId);

        var cancel = await _client.PostAsync($"/e2eruns/{runId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.True(cts.IsCancellationRequested);
        var stored = await _factory.E2eRunStore.GetAsync(runId);
        Assert.NotNull(stored);
        Assert.Equal(E2eRunStatus.Canceled, stored.Status);
    }

    [Fact]
    public async Task E2eRun_cancel_returns_ok_when_concurrent_cancel_wins_race()
    {
        const string runId = "race-run";
        var store = new CancelRaceE2eRunStore(runId);
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IE2eRunStore>();
                services.AddSingleton<IE2eRunStore>(store);
            });
        });
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<E2eRunCancellationRegistry>();
        using var cts = registry.Register(runId);

        var cancel = await client.PostAsync($"/e2eruns/{runId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var dto = await cancel.Content.ReadFromJsonAsync<E2eRunDto>();
        Assert.NotNull(dto);
        Assert.Equal(E2eRunStatus.Canceled, dto.Status);
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(2, store.GetCalls);
        Assert.Equal(1, store.CancelCalls);
    }

    [Fact]
    public async Task E2eRun_bulk_enqueue_creates_batch_and_summary()
    {
        var first = await SeedE2eCaseAsync("api-bulk-first");
        var second = await SeedE2eCaseAsync("api-bulk-second");

        var response = await _client.PostAsJsonAsync(
            "/e2eruns/bulk",
            new EnqueueBulkE2eRunsRequest([first, second], BatchId: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EnqueueBulkE2eRunsResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Runs.Count);

        var summary = await _client.GetFromJsonAsync<BatchSummaryDto>($"/e2eruns/batches/{body.BatchId}");
        Assert.NotNull(summary);
        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.Queued);
        Assert.False(summary.Complete);
    }

    private async Task<string> SeedE2eCaseAsync(string id)
        => await SeedCaseAsync(
            id,
            AutomationKind.E2eReplay,
            JsonSerializer.Serialize(new E2eReplayArtifact
            {
                Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }],
                Assertions = [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#root" }],
            }));

    private async Task<string> SeedCaseAsync(string id, AutomationKind automationKind, string? artifactJson)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(TestCaseApiFactory.ProjectId),
            Title = id,
            Prompt = "fixture",
        };
        await _factory.WorkItemStore.CreateAsync(item);

        var testCase = new TestCase
        {
            Id = id,
            Name = id,
            Description = "",
            SourceWorkItemId = item.Id.ToString(),
            AutomationKind = automationKind,
            ExecutableArtifactJson = artifactJson,
        };
        await _factory.TestCaseStore.CreateAsync(testCase);
        return testCase.Id;
    }

    private static ISandboxProvider GetInnerProvider(IE2eExecutionPool pool)
    {
        if (pool is IManagedSandboxProviderSource { ManagedSandboxProviders.Count: > 0 } source)
            return Assert.IsAssignableFrom<ISandboxProvider>(source.ManagedSandboxProviders[0]);

        var field = typeof(LocalE2eExecutionPool).GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<ISandboxProvider>(field.GetValue(pool));
    }

    private static MultipassRemoteSandboxOptions ReadRemoteOptions(MultipassRemoteSandboxProvider provider)
    {
        var field = typeof(MultipassRemoteSandboxProvider).GetField("_optsAccessor", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var accessor = Assert.IsType<Func<MultipassRemoteSandboxOptions>>(field.GetValue(provider));
        return accessor();
    }

    private sealed class CancelRaceE2eRunStore(string runId) : IE2eRunStore
    {
        public int GetCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task CreateAsync(E2eRun run, CancellationToken ct = default) => Task.CompletedTask;
        public Task BulkCreateAsync(IReadOnlyList<E2eRun> runs, CancellationToken ct = default) => Task.CompletedTask;
        public IAsyncEnumerable<E2eRun> ListAsync(int offset = 0, int limit = E2eExecutionOptions.DefaultListPageSize, CancellationToken ct = default) => Empty();
        public IAsyncEnumerable<E2eRun> ListByTestCaseAsync(string testCaseId, int offset = 0, int limit = E2eExecutionOptions.DefaultListPageSize, CancellationToken ct = default) => Empty();
        public IAsyncEnumerable<E2eRun> ListByBatchAsync(string batchId, int offset = 0, int limit = E2eExecutionOptions.DefaultListPageSize, CancellationToken ct = default) => Empty();
        public Task<E2eRunBatchCounts?> GetBatchCountsAsync(string batchId, CancellationToken ct = default) => Task.FromResult<E2eRunBatchCounts?>(null);
        public Task<bool> HasQueuedAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<E2eRun?> ClaimNextQueuedAsync(string? sandboxId, CancellationToken ct = default) => Task.FromResult<E2eRun?>(null);
        public Task<bool> AssignSandboxAsync(string id, string sandboxId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> RequeueRunningAsync(DateTimeOffset startedBefore, CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> UpdateStatusAsync(string id, E2eRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, string? result, CancellationToken ct = default) => Task.FromResult(false);

        public Task<E2eRun?> GetAsync(string id, CancellationToken ct = default)
        {
            GetCalls++;
            if (!string.Equals(id, runId, StringComparison.Ordinal))
                return Task.FromResult<E2eRun?>(null);

            var status = GetCalls == 1 ? E2eRunStatus.Running : E2eRunStatus.Canceled;
            return Task.FromResult<E2eRun?>(new E2eRun
            {
                Id = runId,
                TestCaseId = "race-case",
                Status = status,
                SandboxId = "sandbox-race",
            });
        }

        public Task<bool> CancelAsync(string id, CancellationToken ct = default)
        {
            CancelCalls++;
            return Task.FromResult(false);
        }

        private static async IAsyncEnumerable<E2eRun> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

internal sealed class E2ePoolWiringFactory(
    string poolKind,
    string? globalRemoteTarget = null,
    IReadOnlyList<string>? globalRemoteExtraSshOptions = null,
    string? e2eRemoteTarget = "codeybox@e2e.example",
    IReadOnlyList<string>? e2eRemoteExtraSshOptions = null,
    int? e2eRemoteMaxConcurrent = null,
    bool e2eEnabled = false,
    string? baselineImageRef = "cb-e2e-baseline",
    string? networkProfile = null,
    IReadOnlyList<string>? e2eRemoteTargets = null,
    string environment = "Development") : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-e2epool-{Guid.NewGuid():N}.db");
    private readonly IReadOnlyList<string>? _e2eRemoteTargets = e2eRemoteTargets;
    private readonly IReadOnlyList<string>? _globalRemoteExtraSshOptions = globalRemoteExtraSshOptions;
    private readonly IReadOnlyList<string>? _e2eRemoteExtraSshOptions = e2eRemoteExtraSshOptions;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            var values = new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:WorkloadTrust"] = "Trusted",
                ["CodeyBox:AcknowledgeSharedKernelRisk"] = "true",
                ["CodeyBox:E2eExecution:PoolKind"] = poolKind,
                ["CodeyBox:E2eExecution:Enabled"] = e2eEnabled.ToString(),
                ["CodeyBox:E2eExecution:BaselineImageRef"] = baselineImageRef,
                ["CodeyBox:E2eExecution:NetworkProfile"] = networkProfile,
                ["CodeyBox:Changelog:Enabled"] = "false",
                ["CodeyBox:Changelog:GitHubWebhookSecretEnvVar"] = "TEST_CHANGELOG_SECRET",
                ["CodeyBox:MultipassRemoteSandbox:SshTarget"] = globalRemoteTarget,
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
            };
            if (_globalRemoteExtraSshOptions is { Count: > 0 })
            {
                for (var i = 0; i < _globalRemoteExtraSshOptions.Count; i++)
                    values[$"CodeyBox:MultipassRemoteSandbox:ExtraSshOptions:{i}"] = _globalRemoteExtraSshOptions[i];
            }
            if (_e2eRemoteTargets is { Count: > 0 })
            {
                for (var i = 0; i < _e2eRemoteTargets.Count; i++)
                {
                    values[$"CodeyBox:E2eMultipassRemoteSandboxes:{i}:SshTarget"] = _e2eRemoteTargets[i];
                    values[$"CodeyBox:E2eMultipassRemoteSandboxes:{i}:MaxConcurrent"] = "1";
                }
            }
            else
            {
                values["CodeyBox:E2eMultipassRemoteSandbox:SshTarget"] = e2eRemoteTarget;
                values["CodeyBox:E2eMultipassRemoteSandbox:MaxConcurrent"] = e2eRemoteMaxConcurrent?.ToString();
                if (_e2eRemoteExtraSshOptions is { Count: > 0 })
                {
                    for (var i = 0; i < _e2eRemoteExtraSshOptions.Count; i++)
                        values[$"CodeyBox:E2eMultipassRemoteSandbox:ExtraSshOptions:{i}"] = _e2eRemoteExtraSshOptions[i];
                }
            }

            cfg.AddInMemoryCollection(values);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId(TestCaseApiFactory.ProjectId),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    DefaultAgent = AgentKind.Claude,
                    DefaultBaseBranch = "main",
                }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}

internal sealed class E2eHostedServiceWiringFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-e2ehosted-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:E2eExecution:Enabled"] = "false",
                ["CodeyBox:E2eExecution:PoolKind"] = "local",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId(TestCaseApiFactory.ProjectId),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    DefaultAgent = AgentKind.Claude,
                    DefaultBaseBranch = "main",
                }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
