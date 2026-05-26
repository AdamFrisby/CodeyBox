using CodeyBox.Agents.Cursor;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the Cursor model-list probe contract: it returns a static
/// success result containing the operator-configured known models (Cursor
/// exposes no model-enumeration endpoint reachable from a subscription token).
///
/// <para>The AgentClassConfigValidator hosted service consumes this list at
/// startup; a future swap to <see cref="AgentModelListResult.Failed"/> or a
/// typo in <c>composer-2.5</c> would silently make Cursor unroutable. These
/// tests fail loudly in that case.</para>
/// </summary>
public sealed class CursorModelListProbeTests
{
    private static readonly CursorModelListProbe Probe = new();

    [Fact]
    public void Kind_IsCursor()
        => Assert.Equal(AgentKind.Cursor, Probe.Kind);

    [Fact]
    public async Task GetModelListAsync_ReturnsSuccess_NotFailed()
    {
        var result = await Probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task GetModelListAsync_ContainsDefaultModel()
    {
        // composer-2.5 is the operator's default model under the subscription;
        // if it ever drops out of KnownModels the agent-class config validator
        // would reject Cursor at startup.
        var result = await Probe.GetModelListAsync(CancellationToken.None);

        Assert.Contains("composer-2.5", result.ModelIds);
    }

    [Fact]
    public async Task GetModelListAsync_ReturnsNonEmptyList()
    {
        var result = await Probe.GetModelListAsync(CancellationToken.None);

        Assert.NotEmpty(result.ModelIds);
    }

    [Fact]
    public async Task GetModelListAsync_IsIdempotent()
    {
        // Probe is a static-list returner; repeated calls must yield the same
        // sequence (the validator may call once per process startup, but a
        // future caching layer or test fixture might call repeatedly).
        var a = await Probe.GetModelListAsync(CancellationToken.None);
        var b = await Probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal(a.ModelIds, b.ModelIds);
    }
}
