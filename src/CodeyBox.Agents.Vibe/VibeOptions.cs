namespace CodeyBox.Agents.Vibe;

/// <summary>
/// Hot-reloadable operator knobs for the <c>vibe</c> runner, bound under
/// <c>CodeyBox:Vibe</c>. Kept in the agents assembly (rather than the API
/// composition root) so the runner stays free of
/// <c>Microsoft.Extensions.Options</c> — <c>Program.cs</c> binds this POCO
/// and hands the runner a <see cref="VibeOptionsAccessor"/> delegate.
/// </summary>
public sealed class VibeOptions
{
    /// <summary>
    /// Bound on autonomous agent turns, passed as <c>--max-turns</c> (a
    /// programmatic-mode-only flag). Caps how many iterations the model may
    /// take without asking for user input — in a non-interactive one-shot
    /// run there is no user to ask, so an unbounded loop would burn quota
    /// until the pipeline timeout. Null/zero/negative omits the flag (vibe
    /// default: unbounded).
    /// </summary>
    public int? MaxTurns { get; set; } = DefaultMaxTurns;

    /// <summary>Default <see cref="MaxTurns"/> shipped in config.</summary>
    public const int DefaultMaxTurns = 100;
}

/// <summary>
/// Resolver for the runner's hot-reloadable <see cref="VibeOptions"/>.
/// Wrapped behind a delegate so the runner DI registration does not depend on
/// <c>IOptionsMonitor</c> directly — keeps the agents assembly free of
/// Microsoft.Extensions.Options (same shape as
/// <c>GooseOptionsAccessor</c>).
/// </summary>
public delegate VibeOptions VibeOptionsAccessor();
