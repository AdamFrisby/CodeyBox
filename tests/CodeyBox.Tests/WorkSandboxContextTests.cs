using CodeyBox.Agents.Cursor;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class WorkSandboxContextTests
{
    [Fact]
    public async Task ReusableSandbox_ForwardsBatchLaunchModeToBatchRunner()
    {
        var innerSandbox = new RecordingSandbox(
            SandboxAgentOutputTransportKind.HttpIngest,
            SandboxBatchLaunchMode.Detached);
        var provider = new SingleSandboxProvider(innerSandbox);
        await using var context = new WorkSandboxContext(
            provider,
            new PipelineTuningSnapshot(new PipelineTuningOptions { EnableSandboxReuse = true }),
            NullLogger.Instance);
        await using var wrapped = await context.GetOrCreateSandboxAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
        }, CancellationToken.None);
        var runner = new CursorAgentRunner();
        var cred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = """{"token":"x"}""" },
            new Dictionary<string, string>());

        var result = await runner.RunTextOnlyAsync("prompt", cred, sandbox: wrapped, workingDirectory: "/work");

        Assert.True(result.Success, $"{result.Summary} | {result.Error}");
        Assert.Equal(SandboxBatchLaunchMode.Detached, wrapped.BatchLaunchMode);
        var agentExec = innerSandbox.Execs.Last(e => e.Argv.Count > 0 && e.Argv[0] == "agent");
        Assert.Equal(SandboxAgentOutputTransportPreference.PreferHttpIngest, agentExec.AgentOutputTransport);
        Assert.Equal(SandboxExecLaunchMode.DetachedBatch, agentExec.LaunchMode);
    }

    [Fact]
    public async Task GetOrCreateSandboxAsync_DoesNotReuseAcrossTimingPhases_WhenCapturingMetrics()
    {
        // Capture on: each phase must get its own VM so the per-phase resource
        // record is attributable to a single phase.
        var provider = new RecordingSandboxProvider { CapturesResourceMetrics = true };
        await using var context = new WorkSandboxContext(
            provider,
            new PipelineTuningSnapshot(new PipelineTuningOptions { EnableSandboxReuse = true, MaxSandboxReuses = 10 }),
            NullLogger.Instance);

        await using (await context.GetOrCreateSandboxAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            TimingPhase = "work",
        }, CancellationToken.None))
        {
        }

        provider.CapturesResourceMetrics = false;

        await using (await context.GetOrCreateSandboxAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            TimingPhase = "rework",
        }, CancellationToken.None))
        {
        }

        Assert.Equal(2, provider.Created.Count);
        Assert.True(provider.Created[0].Disposed);
        Assert.False(provider.Created[1].Disposed);
    }

    [Fact]
    public async Task GetOrCreateSandboxAsync_ReusesAcrossTimingPhases_WhenNotCapturingMetrics()
    {
        // Capture off (the default): the warm VM is reused across work<->rework,
        // exactly as before the resource-capture feature — no per-phase churn.
        var provider = new RecordingSandboxProvider { CapturesResourceMetrics = false };
        await using var context = new WorkSandboxContext(
            provider,
            new PipelineTuningSnapshot(new PipelineTuningOptions { EnableSandboxReuse = true, MaxSandboxReuses = 10 }),
            NullLogger.Instance);

        await using (await context.GetOrCreateSandboxAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            TimingPhase = "work",
        }, CancellationToken.None))
        {
        }


        provider.CapturesResourceMetrics = true;

        await using (await context.GetOrCreateSandboxAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            TimingPhase = "rework",
        }, CancellationToken.None))
        {
        }

        Assert.Single(provider.Created);
        Assert.False(provider.Created[0].Disposed);
    }

    [Fact]
    public async Task GetOrCreateSandboxAsync_DoesNotReuseAcrossProviderSelectionChange()
    {
        var provider = new RecordingSandboxProvider { Name = "alpha" };
        await using var context = new WorkSandboxContext(
            provider,
            new PipelineTuningSnapshot(new PipelineTuningOptions
            {
                EnableSandboxReuse = true,
                MaxSandboxReuses = 10,
            }),
            NullLogger.Instance);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            TimingPhase = "work",
        };

        await using (await context.GetOrCreateSandboxAsync(spec, CancellationToken.None))
        {
        }

        provider.Name = "beta";
        await using (await context.GetOrCreateSandboxAsync(spec, CancellationToken.None))
        {
        }

        Assert.Equal(2, provider.Created.Count);
        Assert.True(provider.Created[0].Disposed);
        Assert.Equal("alpha", provider.Created[0].ProviderId);
        Assert.Equal("beta", provider.Created[1].ProviderId);
    }

    [Fact]
    public async Task GetOrCreateSandboxAsync_ReusesSamePhase_EvenWhenCapturingMetrics()
    {
        // Same phase twice must still reuse one VM even with capture on — the
        // phase-mismatch recreation must not degenerate into always-recreate.
        var provider = new RecordingSandboxProvider { CapturesResourceMetrics = true };
        await using var context = new WorkSandboxContext(
            provider,
            new PipelineTuningSnapshot(new PipelineTuningOptions { EnableSandboxReuse = true, MaxSandboxReuses = 10 }),
            NullLogger.Instance);

        await using (await context.GetOrCreateSandboxAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            TimingPhase = "rework",
        }, CancellationToken.None))
        {
        }

        await using (await context.GetOrCreateSandboxAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            WorkingDirectory = "/work",
            TimingPhase = "rework",
        }, CancellationToken.None))
        {
        }

        Assert.Single(provider.Created);
        Assert.False(provider.Created[0].Disposed);
    }

    private sealed class SingleSandboxProvider(ISandbox sandbox) : ISandboxProvider
    {
        public string Name => "single";
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => sandbox.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => sandbox.BatchLaunchMode;
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) => Task.FromResult(sandbox);
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingSandboxProvider : ISandboxProvider, IResourceMetricsCapturingProvider
    {
        public string Name { get; set; } = "recording";
        public List<RecordingSandbox> Created { get; } = [];
        public bool CapturesResourceMetrics { get; set; }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            var sandbox = new RecordingSandbox(
                SandboxAgentOutputTransportKind.ExecPipe,
                SandboxBatchLaunchMode.Attached,
                $"recording-{Created.Count + 1}",
                Name,
                CapturesResourceMetrics);
            Created.Add(sandbox);
            return Task.FromResult<ISandbox>(sandbox);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingSandbox(
        SandboxAgentOutputTransportKind transportKind,
        SandboxBatchLaunchMode batchLaunchMode,
        string? id = null,
        string providerId = "recording",
        bool capturesResourceMetrics = false) :
        ISandbox,
        IProviderOwnedSandbox,
        IResourceMetricsCapturingSandbox
    {
        public string Id { get; } = id ?? "recording-work-context";
        public string ProviderId { get; } = providerId;
        public bool CapturesResourceMetrics { get; } = capturesResourceMetrics;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind { get; } = transportKind;
        public SandboxBatchLaunchMode BatchLaunchMode { get; } = batchLaunchMode;
        public List<SandboxExec> Execs { get; } = [];
        public bool Disposed { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (exec.Argv.Count >= 3
                && exec.Argv[0] == "bash"
                && exec.Argv[1] == "-c"
                && exec.Argv[2].Contains("CODEYBOX_CURSOR_AUTH_JSON", StringComparison.Ordinal))
            {
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }

            return Task.FromResult(new SandboxExecResult(0, "assistant text", ""));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
