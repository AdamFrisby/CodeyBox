using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.DependencyInjection;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for POST /workitems/{id}/retry when the item's bare repo
/// was reaped underneath a durable agent-turn checkpoint. The endpoint must not
/// answer with the self-defeating pair "cannot retry from 'work'" plus a hint
/// telling the operator to retry from="work": a work/auto restart discards the
/// now-unrecoverable checkpoint and fresh-clones, while a retained-sandbox
/// lease refusal must not advertise a hint that would fail the same way.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class RetryReapedRepoEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly int _originalResumeAttempts = SessionResumeOptions.MaxResumeAttempts;

    public RetryReapedRepoEndpointTests()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        SessionResumeOptions.SetMaxResumeAttempts(_originalResumeAttempts);
        _client.Dispose();
        _factory.Dispose();
    }

    [Theory]
    [InlineData("work")]
    [InlineData(null)]
    public async Task Retry_CheckpointedItemWithReapedRepo_RestartsFromFreshClone(string? from)
    {
        // No bare repo is created for the item: the git host root exists but
        // holds nothing, which is exactly the post-reap state.
        var item = NewFailedCheckpointedItem(AgentTurnResumePhase.Work);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("work", payload.GetProperty("actualFrom").GetString());
        Assert.Equal(WorkItemState.Queued.ToString(), payload.GetProperty("state").GetString());

        var persisted = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.Queued, persisted!.State);
        Assert.Null(persisted.PreemptCheckpoint);
        Assert.Null(persisted.AgentTurnResumeCheckpoint);
        Assert.Equal(1, _factory.Services.GetRequiredService<ITaskQueue>().Count);
    }

    [Fact]
    public async Task Retry_CheckpointedItemWithReapedRepo_ExplicitPostWorkPhaseConflictsWithUsableHint()
    {
        var item = NewFailedCheckpointedItem(AgentTurnResumePhase.Work);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/retry",
            new { from = RetryFromPolicy.Audit });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("no longer exists", payload.GetProperty("error").GetString());
        // from="work" would discard the dead checkpoint and succeed, so the
        // hint must be offered for this failure.
        Assert.Contains("from=\"work\"", payload.GetProperty("hint").GetString());

        var persisted = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(item.State, persisted!.State);
        Assert.Equal(item.AgentTurnResumeCheckpoint, persisted.AgentTurnResumeCheckpoint);
        Assert.Equal(0, _factory.Services.GetRequiredService<ITaskQueue>().Count);
    }

    [Fact]
    public async Task Retry_RetainedLeaseWithReapedRepo_ConflictsWithoutHint()
    {
        var item = NewFailedCheckpointedItem(AgentTurnResumePhase.Work, retainedLease: true);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/retry",
            new { from = RetryFromPolicy.Work });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("cannot discard a retained-sandbox recovery lease", payload.GetProperty("error").GetString());
        // A from="work" retry would hit this same refusal, so the response
        // must not recommend it.
        Assert.False(payload.TryGetProperty("hint", out _));

        var persisted = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(item.State, persisted!.State);
        Assert.Equal(item.AgentTurnRecoveryLease, persisted!.AgentTurnRecoveryLease);
        Assert.Equal(0, _factory.Services.GetRequiredService<ITaskQueue>().Count);
    }

    private static WorkItem NewFailedCheckpointedItem(
        AgentTurnResumePhase phase,
        bool retainedLease = false)
    {
        var id = WorkItemId.New();
        var promptRevision = 5;
        var resumeState = phase == AgentTurnResumePhase.Work
            ? WorkItemState.Working
            : WorkItemState.Reworking;
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("test-project"),
            Title = "retry after repo reap",
            Prompt = "continue the interrupted task",
            PromptRevision = promptRevision,
            BaseBranch = "main",
            WorkBranch = $"codeybox/reap-{Guid.NewGuid():N}",
            State = WorkItemState.Failed,
            LastError = "agent infrastructure failed",
            FailureKind = WorkItemFailureKinds.Infrastructure,
            Agent = AgentKind.Codex,
            AgentInstanceId = "codex/original",
            PreemptedAt = CheckpointCreatedAt,
            // Typed checkpoints allow exactly one recovery boundary: a git
            // preempt ref OR a retained-sandbox lease.
            PreemptCheckpoint = retainedLease
                ? null
                : AgentTurnCheckpointRef.Create(
                    id,
                    new string('1', 40),
                    new AgentTurnScratchpadArchive(new byte[] { 1 })).Value,
            AgentTurnRecoveryLease = retainedLease
                ? new SandboxRecoveryLease("incus", "codeybox-retained-reaped", "retained-token")
                : null,
            AgentTurnResumeCheckpoint = new AgentTurnResumeCheckpoint(
                AgentKind.Claude,
                "claude/acct-a",
                "claude-opus-4-7",
                "high",
                new AgentNativeSessionId("native-session-endpoint"),
                resumeState,
                phase,
                phase == AgentTurnResumePhase.Rework ? 3 : null,
                promptRevision,
                CheckpointCreatedAt,
                attemptCount: 0),
        };
    }

    private static readonly DateTimeOffset CheckpointCreatedAt =
        new(2026, 7, 12, 2, 3, 4, TimeSpan.Zero);
}
