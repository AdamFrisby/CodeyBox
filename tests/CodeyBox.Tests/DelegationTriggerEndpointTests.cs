using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for the operator delegation command
/// (<c>POST /workitems/{id}/delegate</c>): it delegates from a non-terminal
/// state and from terminal failure states, carries the operator note, and
/// refuses the states and shapes that have nothing to delegate.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class DelegationTriggerEndpointTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public DelegationTriggerEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Delegate_QueuedItem_TransitionsToDelegatingAndEnqueues()
    {
        var item = NewItem(WorkItemState.Queued);
        await _factory.Store.CreateAsync(item);

        var queue = _factory.Services.GetRequiredService<ITaskQueue>();
        Assert.Equal(0, queue.Count);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/delegate", new { });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal(1, queue.Count);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(WorkItemState.Delegating, readBack!.State);
        Assert.True(readBack.DelegationRequested);
        Assert.Contains("operator", readBack.DelegationReason);
        Assert.Equal(item.Priority, readBack.Priority);
    }

    [Fact]
    public async Task Delegate_FailedItem_PreservesFailureSignal()
    {
        // Entered through With() like every production terminal write, so
        // the episode count reflects a real failure episode.
        var item = NewItem(WorkItemState.Queued).With(WorkItemState.Failed, "build broke badly");
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/delegate", new { });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(WorkItemState.Delegating, readBack!.State);
        // Escalation must not consume the failure signal: the prior error
        // rides into LastError and the episode count survives the retry.
        Assert.Contains("build broke badly", readBack.LastError);
        Assert.Equal(1, readBack.TerminalFailureCount);
    }

    [Theory]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.MergeConflictResolutionFailed)]
    [InlineData(WorkItemState.AbandonedAfterRecoveryAttempts)]
    [InlineData(WorkItemState.NeedsOperatorInput)]
    public async Task Delegate_TerminalFailureAndParkedStates_Accepted(WorkItemState state)
    {
        var item = NewItem(state);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/delegate", new { });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Delegating, readBack!.State);
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.NoActionRequired)]
    public async Task Delegate_ResolvedStates_Conflict(WorkItemState state)
    {
        var item = NewItem(state);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/delegate", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(state, readBack!.State);
    }

    [Fact]
    public async Task Delegate_WithNote_StoresNoteOnItem()
    {
        var item = NewItem(WorkItemState.Failed);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/delegate",
            new { note = "focus on the auth race; token refresh is suspect" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(
            "focus on the auth race; token refresh is suspect",
            readBack!.DelegationNote);
    }

    [Fact]
    public async Task Delegate_NoteTooLong_BadRequest()
    {
        var item = NewItem(WorkItemState.Failed);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/delegate",
            new { note = new string('x', 4001) });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, readBack!.State);
    }

    [Fact]
    public async Task Delegate_NoteWithControlCharacters_BadRequest()
    {
        var item = NewItem(WorkItemState.Failed);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/delegate",
            new { note = "line one\x00line two" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Delegate_MultilineNote_Accepted()
    {
        var item = NewItem(WorkItemState.Failed);
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/delegate",
            new { note = "line one\nline two\ttabbed" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal("line one\nline two\ttabbed", readBack!.DelegationNote);
    }

    [Fact]
    public async Task Delegate_NeedsOperatorInputWithOpenQuestion_Conflict()
    {
        var item = NewItem(WorkItemState.NeedsOperatorInput);
        await _factory.Store.CreateAsync(item);
        var questions = _factory.Services.GetRequiredService<IWorkItemQuestionStore>();
        await questions.CreateIfNotExistsAsync(new WorkItemQuestion
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            QuestionId = "q-001",
            QuestionText = "which approach?",
            State = "open",
        });

        var resp = await _client.PostAsJsonAsync($"/workitems/{item.Id}/delegate", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var readBack = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, readBack!.State);
    }

    [Fact]
    public async Task Delegate_UnknownId_NotFound()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{WorkItemId.New()}/delegate", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static WorkItem NewItem(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "delegate me",
        Prompt = "do the thing",
        BaseBranch = "main",
        State = state,
        Priority = 7,
    };
}
