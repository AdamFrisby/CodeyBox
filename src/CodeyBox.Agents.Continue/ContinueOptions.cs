namespace CodeyBox.Agents.Continue;

/// <summary>
/// Hot-reloadable operator knobs for the <c>cn</c> runner, bound under
/// <c>CodeyBox:Continue</c>. Kept in the agents assembly (rather than the API
/// composition root) so the runner stays free of
/// <c>Microsoft.Extensions.Options</c> — <c>Program.cs</c> binds this POCO
/// and hands the runner a <see cref="ContinueOptionsAccessor"/> delegate
/// (same shape as <c>KiloOptionsAccessor</c>).
/// </summary>
public sealed class ContinueOptions
{
    /// <summary>
    /// OpenAI-compatible inference endpoint written to the guest
    /// <c>~/.continue/config.yaml</c> as the model entry's
    /// <c>apiBase</c>. Shipped as the OpenRouter v1 endpoint the bundled
    /// <c>AgentClasses</c> member routes; operators fronting a different
    /// OpenAI-compatible backend change the value here rather than in
    /// source. The provider API key itself is NOT here — it arrives through
    /// the credential chain as <c>CODEYBOX_CONTINUE_API_KEY</c> so the secret
    /// never sits in config.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Default <see cref="BaseUrl"/> shipped in config.</summary>
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
}

/// <summary>
/// Resolver for the runner's hot-reloadable <see cref="ContinueOptions"/>.
/// Wrapped behind a delegate so the runner DI registration does not depend on
/// <c>IOptionsMonitor</c> directly — keeps the agents assembly free of
/// Microsoft.Extensions.Options (same shape as
/// <c>KiloOptionsAccessor</c>).
/// </summary>
public delegate ContinueOptions ContinueOptionsAccessor();
