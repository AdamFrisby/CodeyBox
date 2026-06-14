using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the config-binding clamps in <see cref="TransitionHealthConfigMapper"/>
/// and the atomic swap semantics of <see cref="TransitionHealthOptionsSnapshot"/>
/// — the hot-reload contract the <c>AgentConfigHotReload</c> coordinator
/// depends on.
/// </summary>
public sealed class TransitionHealthOptionsTests
{
    [Fact]
    public void Mapper_uses_default_window_when_value_is_zero_or_negative()
    {
        var opts = TransitionHealthConfigMapper.ToOptions(enabled: true, windowHours: 0, maxTransitions: null);
        Assert.Equal(TimeSpan.FromHours(24), opts.Window);

        var neg = TransitionHealthConfigMapper.ToOptions(enabled: true, windowHours: -1, maxTransitions: null);
        Assert.Equal(TimeSpan.FromHours(24), neg.Window);
    }

    [Fact]
    public void Mapper_clamps_window_to_floor_and_ceiling()
    {
        var tinyWindow = TransitionHealthConfigMapper.ToOptions(enabled: true, windowHours: 0.01, maxTransitions: null);
        Assert.Equal(TransitionHealthConfigMapper.MinWindow, tinyWindow.Window);

        var hugeWindow = TransitionHealthConfigMapper.ToOptions(enabled: true, windowHours: 99_999, maxTransitions: null);
        Assert.Equal(TransitionHealthConfigMapper.MaxWindow, hugeWindow.Window);
    }

    [Fact]
    public void Mapper_clamps_max_transitions_to_floor_and_ceiling()
    {
        var tinyCap = TransitionHealthConfigMapper.ToOptions(enabled: true, windowHours: 1, maxTransitions: 1);
        Assert.Equal(TransitionHealthConfigMapper.MinMaxTransitions, tinyCap.MaxTransitions);

        var hugeCap = TransitionHealthConfigMapper.ToOptions(enabled: true, windowHours: 1, maxTransitions: int.MaxValue);
        Assert.Equal(TransitionHealthConfigMapper.MaxMaxTransitions, hugeCap.MaxTransitions);

        var none = TransitionHealthConfigMapper.ToOptions(enabled: true, windowHours: 1, maxTransitions: null);
        Assert.Null(none.MaxTransitions);
    }

    [Fact]
    public void Snapshot_replace_publishes_atomically()
    {
        var initial = new TransitionHealthOptions { Enabled = true, Window = TimeSpan.FromHours(1) };
        var snap = new TransitionHealthOptionsSnapshot(initial);

        Assert.True(snap.Enabled);
        Assert.Equal(TimeSpan.FromHours(1), snap.Current.Window);

        snap.Replace(new TransitionHealthOptions
        {
            Enabled = false,
            Window = TimeSpan.FromHours(6),
            MaxTransitions = 200,
        });

        Assert.False(snap.Enabled);
        Assert.Equal(TimeSpan.FromHours(6), snap.Current.Window);
        Assert.Equal(200, snap.Current.MaxTransitions);
    }

    [Fact]
    public void Snapshot_rejects_null_on_replace()
    {
        var snap = new TransitionHealthOptionsSnapshot(new TransitionHealthOptions { Enabled = true });
        Assert.Throws<ArgumentNullException>(() => snap.Replace(null!));
    }
}
