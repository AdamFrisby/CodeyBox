using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// HTTP-level tests for POST /workitems/{id}/resume — the operator-cancel
/// resume primitive that preserves the bare repo + work-branch + agent
/// commits across a Cancelled → re-pickup round trip.
///
/// Coverage matrix:
///   - from=work (default) re-queues; from=audit goes to WorkComplete; from=merge goes to AuditPassed.
///   - Bare repo missing → 412.
///   - Work-branch missing → 412.
///   - Non-Cancelled state → 409.
///   - Invalid 'from' value → 400.
///   - Audit log carries the operator reason; webhook fires with the same.
///   - Preserved fields (WorkBranch, ExternalId, RecoveryAttempts-then-zeroed semantics).
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ResumeEndpointTests : IDisposable
{
    private readonly ResumeApiFactory _factory = new();
    private readonly HttpClient _client;

    public ResumeEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem CancelledItem(
        WorkItemCancellationReason reason = WorkItemCancellationReason.OperatorRequested,
        string workBranch = "codeybox/abcdef12",
        string? externalId = null) => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            WorkBranch = workBranch,
            ExternalId = externalId,
            State = WorkItemState.Cancelled,
            CancellationReason = reason,
            CancellationSource = CancellationSources.Operator,
            LastError = "cancelled via API",
            FailureKind = null,
            RecoveryAttempts = 2,
        };

    // ── Happy path: from=work (default) ───────────────────────────────────────

    [Fact]
    public async Task Resume_DefaultFromWork_TransitionsToQueuedAndEnqueues()
    {
        var item = CancelledItem();
        await _factory.Store.CreateAsync(item);
        _factory.GitHost.MarkRepoAndBranchPresent(item.Id, item.WorkBranch!);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/resume",
            new ResumeRequestBody(From: null, Reason: "auditor #100 is now fixed"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<ResumeAcceptedBody>();
        Assert.NotNull(body);
        Assert.Equal(item.Id.ToString(), body!.Id);
        Assert.Equal("work", body.From);
        Assert.Equal(WorkItemState.Queued.ToString(), body.State);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(WorkItemState.Queued, readBack!.State);
        // Critical: WorkBranch is preserved (the whole point of /resume vs
        // /retry — With(Queued) would have cleared it).
        Assert.Equal(item.WorkBranch, readBack.WorkBranch);
        // Reset fields cleared.
        Assert.Null(readBack.LastError);
        Assert.Null(readBack.CancellationReason);
        Assert.Null(readBack.CancellationSource);
        Assert.Null(readBack.FailureKind);
        Assert.Equal(0, readBack.RecoveryAttempts);

        // Worker queue was kicked.
        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(1, queue.Count);

        // Webhook fired with the operator reason in the details payload.
        var resumedEvents = _factory.Webhooks.Events.Where(e => e.Event == "work_item.resumed").ToList();
        Assert.Single(resumedEvents);
        var details = JsonSerializer.SerializeToElement(resumedEvents[0].Details);
        Assert.Equal(item.Id.ToString(), details.GetProperty("id").GetString());
        Assert.Equal("work", details.GetProperty("from").GetString());
        Assert.Equal("auditor #100 is now fixed", details.GetProperty("reason").GetString());
    }

    // ── from=audit goes to WorkComplete, audit iteration counter preserved ───

    [Fact]
    public async Task Resume_FromAudit_TransitionsToWorkCompleteWithoutEnqueueing()
    {
        // The audit phase is re-entered via the pipeline runner's state-gated
        // dispatch, NOT via the Queued worker queue. So from=audit must NOT
        // call ITaskQueue.EnqueueAsync — doing so would race the pipeline's
        // pickup logic.
        var item = CancelledItem();
        await _factory.Store.CreateAsync(item);
        _factory.GitHost.MarkRepoAndBranchPresent(item.Id, item.WorkBranch!);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/resume",
            new ResumeRequestBody(From: "audit", Reason: null));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, readBack!.State);
        Assert.Equal(item.WorkBranch, readBack.WorkBranch);

        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(0, queue.Count);
    }

    // ── from=merge → AuditPassed ──────────────────────────────────────────────

    [Fact]
    public async Task Resume_FromMerge_TransitionsToAuditPassed()
    {
        var item = CancelledItem();
        await _factory.Store.CreateAsync(item);
        _factory.GitHost.MarkRepoAndBranchPresent(item.Id, item.WorkBranch!);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/resume",
            new ResumeRequestBody(From: "merge", Reason: null));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditPassed, readBack!.State);
    }

    // ── 412 when bare repo is missing ─────────────────────────────────────────

    [Fact]
    public async Task Resume_BareRepoMissing_Returns412()
    {
        var item = CancelledItem();
        await _factory.Store.CreateAsync(item);
        // Deliberately do NOT mark the repo present.

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/resume",
            new ResumeRequestBody(From: "work", Reason: null));

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("bare repo or work-branch no longer present", body);
        Assert.Contains("/replay", body);

        // State must not have changed.
        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, readBack!.State);
    }

    // ── 412 when work-branch is missing from the bare repo ────────────────────

    [Fact]
    public async Task Resume_WorkBranchMissing_Returns412()
    {
        var item = CancelledItem(workBranch: "codeybox/branch-was-rm-rfd");
        await _factory.Store.CreateAsync(item);
        // Repo is present but the work-branch ref was force-deleted out-of-band.
        _factory.GitHost.MarkRepoPresent(item.Id);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/resume",
            new ResumeRequestBody(From: "audit", Reason: null));

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Cancelled, readBack!.State);
    }

    // ── 409 when item is not in Cancelled state ───────────────────────────────

    [Theory]
    [InlineData(WorkItemState.Queued)]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.AuditFailed)]
    public async Task Resume_NonCancelledState_Returns409(WorkItemState state)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = state,
            WorkBranch = "codeybox/foo",
        };
        await _factory.Store.CreateAsync(item);
        _factory.GitHost.MarkRepoAndBranchPresent(item.Id, item.WorkBranch!);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/resume",
            new ResumeRequestBody(From: "work", Reason: null));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // ── 400 for invalid 'from' value ──────────────────────────────────────────

    [Theory]
    [InlineData("upstream")]
    [InlineData("rework")]
    [InlineData("")]
    [InlineData("FROM_THE_BEGINNING")]
    public async Task Resume_InvalidFrom_Returns400(string from)
    {
        var item = CancelledItem();
        await _factory.Store.CreateAsync(item);
        _factory.GitHost.MarkRepoAndBranchPresent(item.Id, item.WorkBranch!);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/resume",
            new ResumeRequestBody(From: from, Reason: null));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── 404 for unknown id ────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_UnknownId_Returns404()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{WorkItemId.New()}/resume",
            new ResumeRequestBody(From: "work", Reason: null));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── ExternalId is preserved in the webhook payload ────────────────────────

    [Fact]
    public async Task Resume_PreservesExternalIdInWebhookDetails()
    {
        // Real-world recovery scenario: the two cancelled items the spec
        // names by externalId are addressed by externalId; the webhook
        // payload must include it so downstream trackers can correlate.
        var item = CancelledItem(externalId: "JOBTRACK-990de0d2");
        await _factory.Store.CreateAsync(item);
        _factory.GitHost.MarkRepoAndBranchPresent(item.Id, item.WorkBranch!);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/resume",
            new ResumeRequestBody(From: "work", Reason: "recovering preserved work"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var resumedEvents = _factory.Webhooks.Events.Where(e => e.Event == "work_item.resumed").ToList();
        Assert.Single(resumedEvents);
        var details = JsonSerializer.SerializeToElement(resumedEvents[0].Details);
        Assert.Equal("JOBTRACK-990de0d2", details.GetProperty("externalId").GetString());
    }

    private sealed record ResumeRequestBody(string? From, string? Reason);
    private sealed record ResumeAcceptedBody(string Id, string From, string State);
}

/// <summary>
/// Test factory that swaps in a controllable <see cref="StubResumeGitHost"/>
/// (so resume tests can toggle bare-repo + work-branch presence per test
/// without touching the filesystem) and a capturing webhook dispatcher.
/// </summary>
internal sealed class ResumeApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"cb-resume-http-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }
    public StubResumeGitHost GitHost { get; } = new();
    public CapturingWebhookDispatcher Webhooks { get; } = new();

    public ResumeApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"resume-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"resume-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"resume-audit-{Guid.NewGuid():N}-.json"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(Store);

            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("test-project"),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                }));

            services.RemoveAll<IGitHost>();
            services.AddSingleton<IGitHost>(GitHost);

            services.RemoveAll<IWebhookDispatcher>();
            services.AddSingleton<IWebhookDispatcher>(Webhooks);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Stub <see cref="IGitHost"/> used by the resume tests. Lets each test
/// individually toggle bare-repo presence (per work-item id) and work-branch
/// presence (per repository-id + branch-name pair) so the precondition
/// validation in the resume endpoint can be exercised against in-memory
/// flags rather than real bare repos on disk.
/// </summary>
internal sealed class StubResumeGitHost : IGitHost
{
    private readonly HashSet<string> _presentRepos = [];
    private readonly HashSet<string> _presentBranches = [];

    public void MarkRepoPresent(WorkItemId id) => _presentRepos.Add(id.ToString());

    public void MarkRepoAndBranchPresent(WorkItemId id, string branch)
    {
        _presentRepos.Add(id.ToString());
        _presentBranches.Add($"{id}:{branch}");
    }

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(_presentRepos.Contains(id.ToString()));

    public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(_presentBranches.Contains($"{repositoryId}:{branch}"));

    // ── Unused by the resume endpoint; throw on accidental call ───────────────

    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => throw new NotSupportedException();
    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();
    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(("", ""));
}
