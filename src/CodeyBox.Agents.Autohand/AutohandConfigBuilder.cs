using System.Text.Json;

namespace CodeyBox.Agents.Autohand;

/// <summary>
/// Pure builder for the guest <c>~/.autohand/config.json</c> the runner
/// materialises before dispatch (verified against autohand-cli 0.9.7).
///
/// <para><b>Why a file, not the environment.</b> The CLI resolves its provider
/// credential exclusively from the config file's
/// <c>&lt;provider&gt;.apiKey</c> field: a run with an empty file key fails
/// with <c>Setup cancelled</c> even when <c>OPENROUTER_API_KEY</c> is set,
/// and a dummy file key fails with <c>Authentication failed … User not
/// found</c> even when <c>AUTOHAND_API_KEY</c> carries the real key (both
/// verified live). Environment variables never backfill the file key, so the
/// runner must write the real key into the guest config. The payload travels
/// through <c>SandboxCredentialFileWriter</c> (stdin transport, mode 0600),
/// never argv.</para>
///
/// <para>The document keeps only what headless dispatch needs:
/// <c>provider</c>, the provider block (<c>apiKey</c>, <c>baseUrl</c>,
/// <c>model</c>), and telemetry off (a throwaway sandbox must not phone
/// home). Bare mode skips hooks, LSP, attribution, and AGENTS.md discovery,
/// so no other section influences the run.</para>
/// </summary>
public static class AutohandConfigBuilder
{
    /// <summary>
    /// Guest-relative path of the autohand user config under <c>$HOME</c>.
    /// Shared with <see cref="AutohandAgentRunner"/> so the seeding step and
    /// the CLI's default lookup (<c>--config</c> defaults here) can never
    /// drift: the runner writes exactly where the CLI reads.
    /// </summary>
    public const string GuestConfigRelativePath = ".autohand/config.json";

    /// <summary>OpenRouter v1 endpoint the shipped member routes.</summary>
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Builds the guest config document. <paramref name="provider"/> selects
    /// both the top-level <c>provider</c> and the provider block name;
    /// <paramref name="model"/> is written into that block so the dispatched
    /// <c>--model</c> and the file default agree. <paramref name="apiKey"/>
    /// must be non-blank — an empty file key fails closed in the guest with
    /// <c>Setup cancelled</c>, so fail here instead with a named cause.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="apiKey"/> or <paramref name="provider"/>
    /// is blank.
    /// </exception>
    public static string BuildConfigJson(string provider, string? model, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Autohand provider must be non-blank.", nameof(provider));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException(
                "Autohand API key must be non-blank: the CLI never backfills the config key from the environment.",
                nameof(apiKey));

        var trimmedProvider = provider.Trim();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("provider", trimmedProvider);
            writer.WriteStartObject(trimmedProvider);
            writer.WriteString("apiKey", apiKey);
            if (string.Equals(trimmedProvider, "openrouter", StringComparison.OrdinalIgnoreCase))
                writer.WriteString("baseUrl", OpenRouterBaseUrl);
            if (!string.IsNullOrWhiteSpace(model))
                writer.WriteString("model", model!.Trim());
            writer.WriteEndObject();
            writer.WriteStartObject("telemetry");
            writer.WriteBoolean("enabled", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
