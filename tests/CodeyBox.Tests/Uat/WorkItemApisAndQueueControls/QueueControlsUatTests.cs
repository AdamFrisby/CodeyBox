using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Tests;

namespace CodeyBox.Tests.Uat.WorkItemApisAndQueueControls;

/// <summary>
/// UAT coverage for queue pause/resume/status controls.
/// Plan anchor: docs/uat/00-plan.md#queue-pause-resume-and-status-endpoints---controls-global-and-project-pickup
/// </summary>
[Collection("GlobalSerilog")]
public sealed class QueueControlsUatTests : IDisposable
{
    private readonly QueueApiFactory _factory = new();
    private readonly HttpClient _client;

    public QueueControlsUatTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GlobalPauseStatusAndResume_RoundTripPersistedState()
    {
        var pause = await _client.PostAsJsonAsync("/queue/pause", new { reason = "operator maintenance" });

        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        var paused = await _client.GetFromJsonAsync<QueueStatusDto>("/queue/status");
        Assert.Equal("Paused", paused!.State);
        Assert.Equal("operator maintenance", paused.PausedReason);
        Assert.NotNull(paused.PausedAt);
        Assert.Equal(QueueState.Paused, _factory.QueueController.State);

        var resume = await _client.PostAsJsonAsync("/queue/resume", new { });

        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        var running = await _client.GetFromJsonAsync<QueueStatusDto>("/queue/status");
        Assert.Equal("Running", running!.State);
        Assert.Null(running.PausedAt);
        Assert.Null(running.PausedReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains\ncontrol")]
    public async Task GlobalPause_RejectsInvalidReasons(string reason)
    {
        var response = await _client.PostAsJsonAsync("/queue/pause", new { reason });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProjectPause_IsScopedAndVisibleFromBudgetEndpoint()
    {
        var pause = await _client.PostAsJsonAsync("/projects/proj/queue/pause", new { reason = "budget review" });

        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        var paused = await pause.Content.ReadFromJsonAsync<ProjectQueueDto>();
        Assert.True(paused!.Paused);
        Assert.Equal("budget review", paused.PausedReason);

        var state = await _factory.QueueController.GetProjectStateAsync(new ProjectId("proj"));
        Assert.True(state!.Paused);
        Assert.Equal("budget review", state.PausedReason);

        var budget = await _client.GetAsync("/projects/proj/budget");
        budget.EnsureSuccessStatusCode();
        var budgetJson = await budget.ReadJsonAsync();
        Assert.True(budgetJson.GetProperty("projectQueue").GetProperty("paused").GetBoolean());
        Assert.Equal("budget review", budgetJson.GetProperty("projectQueue").GetProperty("pausedReason").GetString());
    }

    [Fact]
    public async Task ProjectResume_ClearsProjectPauseWithoutChangingGlobalPause()
    {
        await _client.PostAsJsonAsync("/queue/pause", new { reason = "global maintenance" });
        await _client.PostAsJsonAsync("/projects/proj/queue/pause", new { reason = "project hold" });

        var resumeProject = await _client.PostAsJsonAsync("/projects/proj/queue/resume", new { });

        Assert.Equal(HttpStatusCode.OK, resumeProject.StatusCode);
        var projectState = await _factory.QueueController.GetProjectStateAsync(new ProjectId("proj"));
        Assert.NotNull(projectState);
        Assert.False(projectState!.Paused);
        Assert.Equal(QueueState.Paused, _factory.QueueController.State);
    }

    [Fact]
    public async Task WorkersStatus_ReturnsDispatchCounters()
    {
        var response = await _client.GetAsync("/workers/status");

        response.EnsureSuccessStatusCode();
        var json = await response.ReadJsonAsync();
        Assert.True(json.TryGetProperty("maxConcurrent", out _));
        Assert.True(json.TryGetProperty("currentlyRunning", out _));
        Assert.True(json.TryGetProperty("queuedCount", out _));
        Assert.True(json.TryGetProperty("lastSpawnAt", out _));
    }

    private sealed record QueueStatusDto(string State, DateTimeOffset? PausedAt, string? PausedReason);
    private sealed record ProjectQueueDto(string ProjectId, bool Paused, DateTimeOffset? PausedAt, string? PausedReason);
}
