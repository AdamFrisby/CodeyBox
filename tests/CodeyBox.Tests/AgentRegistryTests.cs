using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AgentRegistryTests
{
    private sealed class FakeRunner : IAgentRunner
    {
        public AgentKind Kind { get; }
        public FakeRunner(AgentKind kind) { Kind = kind; }
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    [Fact]
    public void TryGet_FindsRegisteredKind()
    {
        var r = new AgentRegistry([new FakeRunner(AgentKind.Claude), new FakeRunner(AgentKind.Codex)]);
        Assert.True(r.TryGet(AgentKind.Claude, out var got));
        Assert.Equal(AgentKind.Claude, got.Kind);
    }

    [Fact]
    public void TryGet_MissingReturnsFalse()
    {
        var r = new AgentRegistry([new FakeRunner(AgentKind.Claude)]);
        Assert.False(r.TryGet(new AgentKind("missing"), out _));
    }

    [Fact]
    public void Available_ListsAllKinds()
    {
        var r = new AgentRegistry([new FakeRunner(AgentKind.Claude), new FakeRunner(AgentKind.Codex)]);
        Assert.Contains(AgentKind.Claude, r.Available);
        Assert.Contains(AgentKind.Codex, r.Available);
    }
}
