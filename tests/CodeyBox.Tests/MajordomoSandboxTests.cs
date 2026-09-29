using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class MajordomoSandboxTests
{
    private const string McpUrl = "http://orchestrator:5000/mcp/majordomo";

    private static MajordomoSandboxOptions Options() => new()
    {
        NetworkProfile = "majordomo",
        OrchestratorHost = "orchestrator.internal",
        IdleTimeout = TimeSpan.FromMinutes(10),
        ModelBackend = MajordomoModelBackend.CodingAgentCli,
        AgentKind = "codex",
        RepositoryPaths = ["/tmp/repo-a"],
    };

    [Fact]
    public async Task Create_UsesRestrictedProfile_WithOrchestratorOnlyEgress()
    {
        var provider = new MajordomoFakeProvider("incus");
        var options = Options();
        await using var session = new MajordomoSandboxSession(
            provider,
            () => options,
            () => ["majordomo"]);

        var sandbox = await session.GetOrCreateAsync(McpUrl);

        Assert.Same(sandbox, await session.GetOrCreateAsync(McpUrl));
        Assert.Equal(1, provider.Created);
        var spec = provider.LastSpec;
        Assert.NotNull(spec);
        Assert.Equal("majordomo", spec.Network.ProfileName);
        Assert.Equal(["orchestrator.internal"], spec.Network.AllowedHosts);
        Assert.DoesNotContain(spec.Network.AllowedHosts, h => string.Equals(h, "internet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(MajordomoSandboxSpecFactory.McpUrlVariable, spec.Environment.Keys);
    }

    [Fact]
    public async Task Create_RefusedWhenProfileUnavailable_NoFallbackCreation()
    {
        var provider = new MajordomoFakeProvider("incus");
        var options = Options();
        await using var session = new MajordomoSandboxSession(
            provider,
            () => options,
            () => ["other-profile"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetOrCreateAsync(McpUrl));
        Assert.Contains("majordomo", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, provider.Created);
        Assert.False(session.IsAlive);
    }

    [Fact]
    public async Task Create_RefusedOnNotEnforcedProvider()
    {
        var provider = new MajordomoFakeProvider("process");
        var options = Options();
        await using var session = new MajordomoSandboxSession(
            provider,
            () => options,
            () => ["majordomo"]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.GetOrCreateAsync(McpUrl));
        Assert.Equal(0, provider.Created);
    }

    [Fact]
    public void Spec_RequiresNonBlankProfile()
    {
        var options = Options();
        options.NetworkProfile = "   ";
        Assert.Throws<InvalidOperationException>(
            () => MajordomoSandboxSpecFactory.BuildSpec(options, McpUrl));
    }

    [Fact]
    public async Task RepoMount_IsReadOnly_WriteAttemptFails()
    {
        var provider = new MajordomoFakeProvider("incus");
        var options = Options();
        await using var session = new MajordomoSandboxSession(
            provider,
            () => options,
            () => ["majordomo"]);

        var sandbox = Assert.IsType<MajordomoFakeSandbox>(await session.GetOrCreateAsync(McpUrl));
        var spec = provider.LastSpec;
        Assert.NotNull(spec);
        Assert.NotEmpty(spec.Mounts);
        Assert.All(spec.Mounts, m => Assert.True(m.ReadOnly));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sandbox.WriteFileAsync("/repos/0/README.md", "edit from sandbox"));
        Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sandbox_DoesNotOccupyWorkDispatchSlot()
    {
        var provider = new MajordomoFakeProvider("incus");
        var options = Options();
        await using var session = new MajordomoSandboxSession(
            provider,
            () => options,
            () => ["majordomo"]);

        var gate = new ResizableConcurrencyGate(initialTarget: 1);
        Assert.True(gate.TryEnter());
        var inFlightBefore = gate.CurrentInFlight;

        await session.GetOrCreateAsync(McpUrl);

        Assert.Equal(inFlightBefore, gate.CurrentInFlight);
        Assert.True(session.IsAlive);
        gate.Release();
        gate.Dispose();
    }

    [Fact]
    public async Task IdleBound_TearsDown_AndNextTurnRecreates()
    {
        var provider = new MajordomoFakeProvider("incus");
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var options = Options();
        await using var session = new MajordomoSandboxSession(
            provider,
            () => options,
            () => ["majordomo"],
            clock);

        var first = Assert.IsType<MajordomoFakeSandbox>(await session.GetOrCreateAsync(McpUrl));
        Assert.False(first.Disposed);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await session.NotifyIdleExpiredAsync());
        Assert.True(session.IsAlive);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(await session.NotifyIdleExpiredAsync());
        Assert.True(first.Disposed);
        Assert.False(session.IsAlive);

        var second = await session.GetOrCreateAsync(McpUrl);
        Assert.Equal(2, provider.Created);
        Assert.NotSame(first, second);
        Assert.True(session.IsAlive);
    }

    [Fact]
    public void Options_ApiKeyBackend_RequiresMeteredKey()
    {
        var options = Options();
        options.ModelBackend = MajordomoModelBackend.ApiKey;
        options.HasMeteredApiKey = false;
        Assert.NotNull(MajordomoSandboxOptions.Validate(options));

        options.HasMeteredApiKey = true;
        Assert.Null(MajordomoSandboxOptions.Validate(options));
    }

    private sealed class MajordomoFakeProvider(string kind) : ISandboxProvider
    {
        public string Name => kind;
        public int Created { get; private set; }
        public SandboxSpec? LastSpec { get; private set; }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            Created++;
            LastSpec = spec;
            ISandbox sandbox = new MajordomoFakeSandbox(spec.Mounts);
            return Task.FromResult(sandbox);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class MajordomoFakeSandbox(IReadOnlyList<SandboxMount> mounts) : ISandbox    {
        public string Id => "majordomo-fake";
        public bool Disposed { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            throw new NotSupportedException("Fake sandbox does not execute commands.");

        public Task WriteFileAsync(string guestPath, string content)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
            foreach (var mount in mounts)
            {
                if (mount.ReadOnly
                    && guestPath.StartsWith(mount.SandboxPath, StringComparison.Ordinal)
                    && mount.HostPath is not null)
                {
                    throw new UnauthorizedAccessException(
                        $"Write to '{guestPath}' refused: mount '{mount.SandboxPath}' is read-only.");
                }
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
