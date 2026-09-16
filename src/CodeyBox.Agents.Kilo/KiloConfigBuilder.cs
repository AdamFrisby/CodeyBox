using System.Text.Json;

namespace CodeyBox.Agents.Kilo;

/// <summary>
/// Pure builder for the guest <c>~/.config/kilo/kilo.jsonc</c> the runner
/// materialises before dispatch (verified against @kilocode/cli 7.7.2).
///
/// <para><b>Why a file, not the environment.</b> Kilo Code publishes no
/// first-class <c>openrouter</c> provider id, so the OpenRouter path goes
/// through the generic <c>openai-compatible</c> provider whose
/// <c>options.baseURL</c> + <c>apiKey</c> live in file config. The
/// interactive first-run <c>/connect</c> flow cannot run in a headless
/// sandbox, so the runner writes the config during provisioning instead.
/// <c>{env:VAR}</c> interpolation is supported by the CLI but is not
/// resolved in repo-local config, so the runner seeds the global file
/// (where interpolation does resolve) with the literal key — the same
/// posture as the autohand guest-config seeding. The payload travels
/// through <c>SandboxCredentialFileWriter</c> (stdin transport, mode 0600),
/// never argv.</para>
///
/// <para><b>The <c>models</c> map is a mandatory allowlist.</b> Verified
/// live: a config without a <c>models</c> entry for the dispatch id fails
/// with <c>Model not found: openai-compatible/…</c> (exit 1) even though the
/// provider catalog carries the id. <see cref="BuildConfigJson"/> therefore
/// writes one entry per id in <paramref name="modelIds"/>; the runner passes
/// the union of the config-sourced default and <see cref="KiloKnownModels.All"/>
/// so the shipped member always resolves. A per-member <c>ModelId</c> outside
/// that union fails closed with the named <c>Model not found</c> cause
/// (surfaced via <see cref="KiloTerminalDiagnoser"/>) rather than dispatching
/// against the wrong catalog.</para>
///
/// <para>The document keeps only what headless dispatch needs: the
/// <c>openai-compatible</c> provider block plus the <c>models</c> map. No
/// MCP servers, themes, or TUI state influence a one-shot run.</para>
/// </summary>
public static class KiloConfigBuilder
{
    /// <summary>
    /// Guest-relative path of the kilo user config under <c>$HOME</c>.
    /// Shared with <see cref="KiloAgentRunner"/> so the seeding step and the
    /// CLI's default lookup can never drift: the runner writes exactly where
    /// the CLI reads.
    /// </summary>
    public const string GuestConfigRelativePath = ".config/kilo/kilo.jsonc";

    /// <summary>
    /// Generic provider id carrying the OpenRouter path. Kilo publishes no
    /// first-class <c>openrouter</c> provider id (verified: the id is absent
    /// from the CLI docs and the published config schema), so OpenRouter is
    /// fronted through here.
    /// </summary>
    public const string ProviderId = "openai-compatible";

    /// <summary>
    /// Builds the guest config document. <paramref name="baseUrl"/> is the
    /// OpenAI-compatible endpoint (shipped as OpenRouter v1; hot-reloadable
    /// via <c>CodeyBox:Kilo:BaseUrl</c>). <paramref name="apiKey"/> must be
    /// non-blank — an empty file key fails in the guest at model-resolution
    /// time, so fail here instead with a named cause.
    /// <paramref name="modelIds"/> seeds the mandatory <c>models</c>
    /// allowlist; blank entries are dropped, and an empty union still writes
    /// the provider block (the CLI then fails closed with
    /// <c>Model not found</c>, naming the cause).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="baseUrl"/> or <paramref name="apiKey"/>
    /// is blank.
    /// </exception>
    public static string BuildConfigJson(string? baseUrl, string apiKey, IEnumerable<string?> modelIds)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Kilo base URL must be non-blank.", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException(
                "Kilo API key must be non-blank: the CLI never backfills the config key from the environment.",
                nameof(apiKey));

        var models = modelIds
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("provider");
            writer.WriteStartObject(ProviderId);
            writer.WriteStartObject("options");
            writer.WriteString("baseURL", baseUrl!.Trim());
            writer.WriteString("apiKey", apiKey);
            writer.WriteEndObject();
            writer.WriteStartObject("models");
            foreach (var model in models)
            {
                writer.WriteStartObject(StripProviderPrefix(model));
                writer.WriteString("name", StripProviderPrefix(model));
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Strips the <c>openai-compatible/</c> provider qualifier from a
    /// <c>-m</c> id for the <c>models</c> map key: the CLI resolves
    /// <c>-m openai-compatible/&lt;id&gt;</c> against the map entry
    /// <c>&lt;id&gt;</c> under that provider (verified live — the seeded map
    /// key <c>nvidia/nemotron-3.5-lightning:free</c> served
    /// <c>-m openai-compatible/nvidia/nemotron-3.5-lightning:free</c>).
    /// Ids without the qualifier pass through unchanged.
    /// </summary>
    internal static string StripProviderPrefix(string modelId)
    {
        var prefix = ProviderId + "/";
        if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return modelId[prefix.Length..];
        return modelId;
    }
}
