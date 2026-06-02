using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="PostAgentTransitionBound"/> — the lifecycle-wide
/// timeout that fences post-agent steps (commit / branch push / state
/// transition). Covers the f9ea330a / 69ee86c4 wedge class where the agent
/// exited cleanly but the worker hung in <c>store.UpdateAsync</c> or
/// <c>webhooks.PublishAsync</c>.
/// </summary>
public sealed class PostAgentTransitionBoundTests
{
    private static readonly WorkItemId Id = WorkItemId.New();

    [Fact]
    public async Task NullAccessor_RunsBodyUnbounded()
    {
        var ran = false;
        await PostAgentTransitionBound.RunAsync(
            optionsAccessor: null,
            itemId: Id,
            stepName: "transition-to-WorkComplete",
            ct: CancellationToken.None,
            body: _ => { ran = true; return Task.CompletedTask; });
        Assert.True(ran);
    }

    [Fact]
    public async Task ZeroTimeout_RunsBodyUnbounded()
    {
        var ran = false;
        await PostAgentTransitionBound.RunAsync(
            optionsAccessor: () => new WorkerProgressWatchdogOptions { PostAgentTransitionTimeout = TimeSpan.Zero },
            itemId: Id,
            stepName: "transition-to-WorkComplete",
            ct: CancellationToken.None,
            body: _ => { ran = true; return Task.CompletedTask; });
        Assert.True(ran);
    }

    [Fact]
    public async Task FastBody_RunsToCompletionWithinTimeout()
    {
        var ran = false;
        await PostAgentTransitionBound.RunAsync(
            optionsAccessor: () => new WorkerProgressWatchdogOptions { PostAgentTransitionTimeout = TimeSpan.FromMinutes(1) },
            itemId: Id,
            stepName: "transition-to-WorkComplete",
            ct: CancellationToken.None,
            body: async ct => { await Task.Yield(); ran = true; });
        Assert.True(ran);
    }

    [Fact]
    public async Task SlowBody_ExceedingTimeout_ThrowsTimeoutException()
    {
        // The agent has already exited; the bounded helper is the last line of
        // defense before the pool slot is held indefinitely.
        var caught = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await PostAgentTransitionBound.RunAsync(
                optionsAccessor: () => new WorkerProgressWatchdogOptions
                {
                    PostAgentTransitionTimeout = TimeSpan.FromMilliseconds(50),
                },
                itemId: Id,
                stepName: "transition-to-WorkComplete",
                ct: CancellationToken.None,
                body: async ct =>
                {
                    // Hangs forever from the bound's perspective — only the
                    // linked CTS releases the await when the timeout fires.
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                });
        });

        Assert.Contains("transition-to-WorkComplete", caught.Message);
        Assert.Contains(Id.ToString(), caught.Message);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsOCE_NotTimeoutException()
    {
        // When the OUTER cancellation token fires (operator cancel, host
        // shutdown) the helper must let the OCE flow through unchanged so the
        // pipeline's operator-cancel handler matches it. A TimeoutException
        // would mis-route through TransitionFailed(infrastructure).
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await PostAgentTransitionBound.RunAsync(
                optionsAccessor: () => new WorkerProgressWatchdogOptions
                {
                    PostAgentTransitionTimeout = TimeSpan.FromMinutes(5),
                },
                itemId: Id,
                stepName: "transition-to-WorkComplete",
                ct: cts.Token,
                body: async ct => { await Task.Delay(TimeSpan.FromMinutes(5), ct); });
        });
    }

    [Fact]
    public async Task TimeoutChange_HotReload_TakesEffectOnNextInvocation()
    {
        // The accessor is invoked on every RunAsync call so a hot-reload to
        // PostAgentTransitionTimeout applies without restarting the pipeline.
        var current = TimeSpan.FromMinutes(10);
        Func<WorkerProgressWatchdogOptions> accessor =
            () => new WorkerProgressWatchdogOptions { PostAgentTransitionTimeout = current };

        // First call: bounded with a long timeout, completes fast.
        await PostAgentTransitionBound.RunAsync(
            accessor, Id, "transition-to-WorkComplete", CancellationToken.None,
            ct => Task.CompletedTask);

        // Operator slashes the timeout via config hot-reload.
        current = TimeSpan.FromMilliseconds(25);

        // Next call observes the shorter window.
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await PostAgentTransitionBound.RunAsync(
                accessor, Id, "transition-to-WorkComplete", CancellationToken.None,
                async ct => await Task.Delay(TimeSpan.FromSeconds(2), ct));
        });
    }
}
