using CodeyBox.Admin.Web.Components.Shared;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// The bound-worker signal crosses the admin edge intact: the mapper carries
/// <c>isRunning</c>/<c>hasPendingResume</c> onto the model (falling back to
/// the state-based reading for servers predating the fields), and the status
/// vocabulary renders a retried checkpoint as "Waiting (resume)", never as
/// running.
/// </summary>
public sealed class RunningSignalEdgeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private static WorkItemDto Dto(string id, string state, bool? isRunning, bool? hasPendingResume) => new()
    {
        Id = id,
        Title = $"Work {id}",
        State = state,
        Agent = "Claude",
        CreatedAt = Now.AddHours(-1),
        UpdatedAt = Now.AddMinutes(-5),
        IsRunning = isRunning,
        HasPendingResume = hasPendingResume,
    };

    [Fact]
    public void Mapper_CarriesBoundWorkerSignal()
    {
        var snapshot = FleetMapSnapshotMapper.ToSnapshot(
            [Dto("wait-1", "Working", false, true)],
            null, null, null, null, Now);

        var item = Assert.Single(snapshot.Items);
        Assert.False(item.IsRunning);
        Assert.True(item.HasPendingResume);
    }

    [Fact]
    public void Mapper_FallsBackToStateReadingWhenServerPredatesSignal()
    {
        var snapshot = FleetMapSnapshotMapper.ToSnapshot(
            [Dto("run-1", "Working", null, null), Dto("q-1", "Queued", null, null)],
            null, null, null, null, Now);

        Assert.True(snapshot.Items.Single(i => i.Id == "run-1").IsRunning);
        Assert.False(snapshot.Items.Single(i => i.Id == "q-1").IsRunning);
    }

    [Fact]
    public void Vocabulary_WaitingResumeRendersWaitingNotRunning()
    {
        var info = StatusVocabulary.ForWorkItem("Working", isRunning: false, hasPendingResume: true);

        Assert.Equal("Waiting (resume)", info.Text);
        Assert.NotEqual("active", info.Tone);
    }

    [Fact]
    public void Vocabulary_RunningRendersRunning()
    {
        var info = StatusVocabulary.ForWorkItem("Working", isRunning: true, hasPendingResume: false);

        Assert.Equal("Working", info.Text);
        Assert.Equal("active", info.Tone);
    }

    [Fact]
    public void Vocabulary_NullSignalKeepsStateReading()
    {
        var info = StatusVocabulary.ForWorkItem("Working", isRunning: null, hasPendingResume: null);

        Assert.Equal("Working", info.Text);
    }

    [Fact]
    public void Vocabulary_UnheldNonResumeStateRendersWaiting()
    {
        var info = StatusVocabulary.ForWorkItem("Auditing", isRunning: false, hasPendingResume: false);

        Assert.Equal("Waiting", info.Text);
        Assert.Equal("wait", info.Tone);
    }

    [Fact]
    public void Vocabulary_QueuedNeverRendersWaitingResume()
    {
        var info = StatusVocabulary.ForWorkItem("Queued", isRunning: false, hasPendingResume: false);

        Assert.Equal("Queued", info.Text);
    }
}
