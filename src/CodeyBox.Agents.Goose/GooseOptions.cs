namespace CodeyBox.Agents.Goose;

/// <summary>
/// Hot-reloadable operator knobs for the <c>goose</c> runner, bound under
/// <c>CodeyBox:Goose</c>. Kept in the agents assembly (rather than the API
/// composition root) so the runner stays free of
/// <c>Microsoft.Extensions.Options</c> — <c>Program.cs</c> binds this POCO
/// and hands the runner a <see cref="GooseOptionsAccessor"/> delegate.
/// </summary>
public sealed class GooseOptions
{
    /// <summary>
    /// Goose provider id passed as <c>--provider</c> (e.g.
    /// <c>openrouter</c>). Null/empty omits the flag and defers to the
    /// guest's own default provider (its <c>config.yaml</c> or
    /// <c>GOOSE_PROVIDER</c> environment). The shipped
    /// <c>appsettings.json</c> sets this to the provider the bundled
    /// <c>AgentClasses</c> member routes; operators fronting a different
    /// backend change the value here rather than in source.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Bound on autonomous agent turns, passed as <c>--max-turns</c>.
    /// Caps how many iterations the model may take without asking for user
    /// input — in a non-interactive one-shot run there is no user to ask,
    /// so an unbounded loop would burn quota until the pipeline timeout.
    /// Null/zero/negative omits the flag (goose default: unbounded).
    /// </summary>
    public int? MaxTurns { get; set; } = DefaultMaxTurns;

    /// <summary>Default <see cref="MaxTurns"/> shipped in config.</summary>
    public const int DefaultMaxTurns = 100;
}

/// <summary>
/// Resolver for the runner's hot-reloadable <see cref="GooseOptions"/>.
/// Wrapped behind a delegate so the runner DI registration does not depend on
/// <c>IOptionsMonitor</c> directly — keeps the agents assembly free of
/// Microsoft.Extensions.Options (same shape as
/// <c>CrockSandboxOptionsAccessor</c>).
/// </summary>
public delegate GooseOptions GooseOptionsAccessor();
