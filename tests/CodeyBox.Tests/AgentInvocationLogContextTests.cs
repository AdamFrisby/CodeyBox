using CodeyBox.Agents;

namespace CodeyBox.Tests;

/// <summary>
/// Contract tests for <see cref="AgentInvocationLogContext"/>: the AsyncLocal
/// side-channel that flows the in-VM agent log path from PipelineRunner to
/// CliAgentRunnerBase so the codeybox-exec wrapper can tee'd-capture
/// stdout/stderr for suspend/resume recovery.
///
/// <para>Regressions in scope-restore would cause cross-contamination between
/// concurrent agent invocations: a long audit-loop invocation would see the
/// log path of an unrelated peer pipeline whose scope happened to outlive it
/// in async-context terms.</para>
/// </summary>
public sealed class AgentInvocationLogContextTests
{
    [Fact]
    public void CurrentLogPath_DefaultsToNull()
    {
        // Sanity: with no active scope, the ambient value is null and the
        // runner short-circuits the AgentLogFileEnv injection branch.
        Assert.Null(AgentInvocationLogContext.CurrentLogPath);
    }

    [Fact]
    public void BeginScope_SetsCurrent_AndDisposeRestoresPrevious()
    {
        Assert.Null(AgentInvocationLogContext.CurrentLogPath);

        using (AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/abc.log"))
        {
            Assert.Equal("/work/.codeybox/agent-logs/abc.log", AgentInvocationLogContext.CurrentLogPath);
        }

        Assert.Null(AgentInvocationLogContext.CurrentLogPath);
    }

    [Fact]
    public void NestedScopes_RestoreInLifoOrder()
    {
        // Pipeline pattern: an outer phase opens a scope, an inner retry opens
        // another scope, the inner restores to the outer's path, the outer
        // restores to null. A regression that always restored to null would
        // strip the wrapper's log redirect from the rest of the outer phase.
        using (AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/outer.log"))
        {
            Assert.Equal("/work/.codeybox/agent-logs/outer.log", AgentInvocationLogContext.CurrentLogPath);
            using (AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/inner.log"))
            {
                Assert.Equal("/work/.codeybox/agent-logs/inner.log", AgentInvocationLogContext.CurrentLogPath);
            }
            Assert.Equal("/work/.codeybox/agent-logs/outer.log", AgentInvocationLogContext.CurrentLogPath);
        }

        Assert.Null(AgentInvocationLogContext.CurrentLogPath);
    }

    [Fact]
    public void DoubleDispose_IsIdempotent()
    {
        // BeginScope returns an IDisposable that explicitly guards against
        // re-entrant Dispose so a using-block that throws followed by an
        // outer catch's manual Dispose() cannot stomp on a subsequent scope's
        // value.
        var outer = AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/a.log");
        var inner = AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/b.log");

        inner.Dispose();
        Assert.Equal("/work/.codeybox/agent-logs/a.log", AgentInvocationLogContext.CurrentLogPath);

        // Second dispose must not overwrite the live ambient value.
        inner.Dispose();
        Assert.Equal("/work/.codeybox/agent-logs/a.log", AgentInvocationLogContext.CurrentLogPath);

        outer.Dispose();
        Assert.Null(AgentInvocationLogContext.CurrentLogPath);
    }

    [Fact]
    public async Task ScopePropagates_AcrossAwait()
    {
        // AsyncLocal contract: the value set before an await flows through to
        // the continuation. A regression that reverted to ThreadStatic or used
        // a plain field would lose the value on async hops.
        using (AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/await.log"))
        {
            await Task.Yield();
            Assert.Equal("/work/.codeybox/agent-logs/await.log", AgentInvocationLogContext.CurrentLogPath);
            await Task.Delay(1);
            Assert.Equal("/work/.codeybox/agent-logs/await.log", AgentInvocationLogContext.CurrentLogPath);
        }
    }

    [Fact]
    public async Task ConcurrentScopes_DoNotLeakAcrossTasks()
    {
        // Two concurrent "agent invocations" on the same process must see
        // their own log path. AsyncLocal's copy-on-write semantics give us
        // this for free, but a regression that switched to a shared static
        // would corrupt cross-VM forwarding.
        var startedA = new TaskCompletionSource();
        var startedB = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var releaseB = new TaskCompletionSource();

        var taskA = Task.Run(async () =>
        {
            using (AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/A.log"))
            {
                startedA.SetResult();
                await releaseA.Task;
                return AgentInvocationLogContext.CurrentLogPath;
            }
        });

        var taskB = Task.Run(async () =>
        {
            using (AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/B.log"))
            {
                startedB.SetResult();
                await releaseB.Task;
                return AgentInvocationLogContext.CurrentLogPath;
            }
        });

        await Task.WhenAll(startedA.Task, startedB.Task);
        releaseB.SetResult();
        releaseA.SetResult();

        Assert.Equal("/work/.codeybox/agent-logs/A.log", await taskA);
        Assert.Equal("/work/.codeybox/agent-logs/B.log", await taskB);
        Assert.Null(AgentInvocationLogContext.CurrentLogPath);
    }

    [Fact]
    public void BeginScope_AcceptsNull_ToTemporarilyDisableCapture()
    {
        // A scope can explicitly null out the ambient path to suppress capture
        // for a nested invocation (e.g. a fire-and-forget helper run that
        // shouldn't pollute the parent's log file).
        using (AgentInvocationLogContext.BeginScope("/work/.codeybox/agent-logs/outer.log"))
        {
            using (AgentInvocationLogContext.BeginScope(null))
            {
                Assert.Null(AgentInvocationLogContext.CurrentLogPath);
            }
            Assert.Equal("/work/.codeybox/agent-logs/outer.log", AgentInvocationLogContext.CurrentLogPath);
        }
    }
}
