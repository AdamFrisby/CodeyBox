using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class CancellationRegistryTests
{
    [Fact]
    public void RegisterAndCancel_SignalsToken()
    {
        using var reg = new CancellationRegistry(CancellationToken.None);
        var id = WorkItemId.New();
        using var registration = reg.Register(id);
        Assert.False(registration.Token.IsCancellationRequested);
        Assert.True(reg.Cancel(id));
        Assert.True(registration.Token.IsCancellationRequested);
        Assert.Equal(CancellationRequestKind.Operator, reg.GetRequestKind(id));
    }

    [Fact]
    public void CancelForRecovery_SignalsTokenWithRecoveryKind()
    {
        using var reg = new CancellationRegistry(CancellationToken.None);
        var id = WorkItemId.New();
        using var registration = reg.Register(id);

        Assert.True(reg.CancelForRecovery(id));

        Assert.True(registration.Token.IsCancellationRequested);
        Assert.Equal(CancellationRequestKind.Recovery, reg.GetRequestKind(id));
    }

    [Fact]
    public void Cancel_UnknownReturnsFalse()
    {
        using var reg = new CancellationRegistry(CancellationToken.None);
        Assert.False(reg.Cancel(WorkItemId.New()));
    }

    [Fact]
    public void DoubleRegister_Throws()
    {
        using var reg = new CancellationRegistry(CancellationToken.None);
        var id = WorkItemId.New();
        using var first = reg.Register(id);
        Assert.Throws<InvalidOperationException>(() => reg.Register(id));
    }

    [Fact]
    public void RootCancellation_DoesNotCancelRegisteredItem()
    {
        using var rootCts = new CancellationTokenSource();
        using var reg = new CancellationRegistry(rootCts.Token);
        var id = WorkItemId.New();
        using var registration = reg.Register(id);
        rootCts.Cancel();
        Assert.False(registration.Token.IsCancellationRequested);
    }

    [Fact]
    public void DisposedRegistration_RemovesFromRegistry()
    {
        using var reg = new CancellationRegistry(CancellationToken.None);
        var id = WorkItemId.New();
        var registration = reg.Register(id);
        Assert.True(reg.IsActive(id));
        registration.Dispose();
        Assert.False(reg.IsActive(id));
        Assert.Null(reg.GetRequestKind(id));
    }
}
