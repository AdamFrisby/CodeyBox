namespace CodeyBox.Agents.Cline;

/// <summary>
/// Hot-reloadable operator knobs for the <c>cline</c> runner, bound under
/// <c>CodeyBox:Cline</c>. Kept in the agents assembly (rather than the API
/// composition root) so the runner stays free of
/// <c>Microsoft.Extensions.Options</c> — <c>Program.cs</c> binds this POCO
/// and hands the runner a <see cref="ClineOptionsAccessor"/> delegate.
/// </summary>
public sealed class ClineOptions
{
    /// <summary>
    /// Cline provider id passed as <c>-P/--provider</c> (e.g.
    /// <c>openrouter</c>). Null/empty falls back to
    /// <see cref="DefaultProvider"/>. The shipped
    /// <c>appsettings.json</c> sets this to the provider the bundled
    /// <c>AgentClasses</c> member routes; operators fronting a different
    /// backend change the value here rather than in source. The provider API
    /// key itself is NOT here — it arrives through the credential chain as
    /// <c>CODEYBOX_CLINE_API_KEY</c> so the secret never sits in config.
    /// The fallback matters: cline's built-in default provider is the
    /// <c>cline</c> vendor account (not the key in
    /// <c>OPENROUTER_API_KEY</c>), so omitting <c>-P</c> would bill the
    /// vendor account and fail fast on vendor auth even with a valid
    /// provider key (verified live).
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>Default <see cref="Provider"/> shipped in config.</summary>
    public const string DefaultProvider = "openrouter";
}

/// <summary>
/// Resolver for the runner's hot-reloadable <see cref="ClineOptions"/>.
/// Wrapped behind a delegate so the runner DI registration does not depend on
/// <c>IOptionsMonitor</c> directly — keeps the agents assembly free of
/// Microsoft.Extensions.Options (same shape as
/// <c>GooseOptionsAccessor</c>).
/// </summary>
public delegate ClineOptions ClineOptionsAccessor();
