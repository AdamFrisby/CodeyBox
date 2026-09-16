namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Hot-reloadable operator knobs for the <c>kilo</c> runner, bound under
/// <c>CodeyBox:Kilo</c>. Kept in the agents assembly (rather than the API
/// composition root) so the runner stays free of
/// <c>Microsoft.Extensions.Options</c> — <c>Program.cs</c> binds this POCO
/// and hands the runner a <see cref="KiloOptionsAccessor"/> delegate
/// (same shape as <c>AutohandOptionsAccessor</c>).
/// </summary>
public sealed class KiloOptions
{
    /// <summary>
    /// OpenAI-compatible inference endpoint written to the guest
    /// <c>~/.config/kilo/kilo.jsonc</c> as
    /// <c>provider."openai-compatible".options.baseURL</c>. Shipped as the
    /// OpenRouter v1 endpoint the bundled <c>AgentClasses</c> member routes;
    /// operators fronting a different OpenAI-compatible backend change the
    /// value here rather than in source. The provider API key itself is NOT
    /// here — it arrives through the credential chain as
    /// <c>CODEYBOX_KILO_API_KEY</c> so the secret never sits in config.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Default <see cref="BaseUrl"/> shipped in config.</summary>
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
}

/// <summary>
/// Resolver for the runner's hot-reloadable <see cref="KiloOptions"/>.
/// Wrapped behind a delegate so the runner DI registration does not depend on
/// <c>IOptionsMonitor</c> directly — keeps the agents assembly free of
/// Microsoft.Extensions.Options (same shape as
/// <c>AutohandOptionsAccessor</c>).
/// </summary>
public delegate KiloOptions KiloOptionsAccessor();
