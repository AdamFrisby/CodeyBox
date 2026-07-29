using CodeyBox.Api;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

public sealed class GitHubAppConnectStateTests
{
    [Fact]
    public void BindAndConsume_PreservesAppAndRejectsReplay()
    {
        var stateStore = new GitHubAppConnectState(new ControllableTimeProvider());
        Assert.True(stateStore.TryBegin("https://codeybox.example", out var state));
        Assert.True(stateStore.TryGet(state, consume: false, out var pending));

        stateStore.BindApp(state, pending, 123);

        Assert.True(stateStore.TryGet(state, consume: true, out var installed));
        Assert.Equal(123, installed.AppId);
        Assert.False(stateStore.TryGet(state, consume: true, out _));
    }

    [Fact]
    public void TryBegin_BoundsPendingConnectionsAndReclaimsExpiredEntries()
    {
        var clock = new ControllableTimeProvider();
        var stateStore = new GitHubAppConnectState(clock);
        for (var i = 0; i < 64; i++)
            Assert.True(stateStore.TryBegin("https://codeybox.example", out _));

        Assert.False(stateStore.TryBegin("https://codeybox.example", out _));

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.True(stateStore.TryBegin("https://codeybox.example", out _));
    }
}
