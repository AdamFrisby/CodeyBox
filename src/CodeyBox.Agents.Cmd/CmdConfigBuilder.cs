using System.Text.Json;

namespace CodeyBox.Agents.Cmd;

/// <summary>
/// Pure builder for the guest <c>~/.commandcode/providers.json</c> and
/// <c>~/.commandcode/auth.json</c> the runner materialises before dispatch
/// (verified against command-code 1.54.2).
///
/// <para><b>Why files, not the environment.</b> The interactive
/// <c>/connect</c> flow cannot run headless, so the runner pre-seeds the two
/// files <c>/connect</c> would write. <c>providers.json</c> carries the
/// <c>openrouter</c> entry in the vendor's own <c>/connect</c> template shape
/// (top-level <c>provider</c> map, <c>baseURL</c>,
/// <c>apiKey: "$OPENROUTER_API_KEY"</c> reference, per-model <c>models</c>
/// map) — verified byte-shape against the file the CLI itself auto-created
/// on first run, including <c>"name": "OpenRouter"</c> and empty-object
/// model entries. The key travels as an environment <em>reference</em>,
/// never a raw secret (the vendor template's own comment: "reference —
/// never a raw key"), resolved from the sandbox environment the shipped
/// credential mapping populates. Undeclared dispatch ids are "sent anyway"
/// (verified: the CLI warns and relays them), so the <c>models</c> map is
/// advisory metadata, not a fail-closed allowlist — the runner still writes
/// the union of the config-sourced default and <see cref="CmdKnownModels.All"/>
/// so the shipped member always carries declared metadata.</para>
///
/// <para><b>Why <c>auth.json</c> carries a non-credential placeholder.</b>
/// Print mode gates on the <em>presence</em> of the Command Code account key
/// (<c>COMMAND_CODE_API_KEY</c> env or <c>auth.json apiKey</c>), but a BYOK
/// run under <c>--local-only</c> never transmits it: billing reads never
/// run, the Command Code transport refuses, and telemetry is off (verified
/// live — a presence-satisfying placeholder plus <c>--local-only</c>
/// completed real OpenRouter runs with no plan). The placeholder is written
/// by <see cref="BuildAuthJson"/> as the literal documented constant (see
/// <see cref="CmdAgentRunner.LocalOnlyAuthPlaceholder"/>), never a real
/// credential, and the runner always passes <c>--local-only</c> so it stays
/// a local gate satisfier. Command Code catalog (plan) models are out of
/// scope: <c>--local-only</c> blocks that route by design.</para>
/// </summary>
public static class CmdConfigBuilder
{
    /// <summary>
    /// Guest-relative path of the BYOK provider entries under <c>$HOME</c>.
    /// Shared with <see cref="CmdAgentRunner"/> so the seeding step and the
    /// CLI's default lookup can never drift: the runner writes exactly where
    /// the CLI reads.
    /// </summary>
    public const string GuestProvidersRelativePath = ".commandcode/providers.json";

    /// <summary>
    /// Guest-relative path of the account-key file under <c>$HOME</c>.
    /// Only the <c>apiKey</c> presence gate is satisfied here (see the class
    /// doc); no real credential is ever written.
    /// </summary>
    public const string GuestAuthRelativePath = ".commandcode/auth.json";

    /// <summary>
    /// First-class OpenRouter provider id (one of the CLI's 150+ prefilled
    /// ids), so endpoint and wire come preconfigured — only the key
    /// reference and the model metadata are seeded.
    /// </summary>
    public const string ProviderId = "openrouter";

    /// <summary>
    /// Prefilled OpenRouter endpoint from the vendor's <c>/connect</c>
    /// template. A fixed vendor fact, not an operator knob.
    /// </summary>
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>
    /// The key <em>reference</em> seeded as the provider <c>apiKey</c> (the
    /// vendor template's own form). Resolved from the sandbox environment at
    /// dispatch; the raw key never appears in the file.
    /// </summary>
    public const string ApiKeyEnvReference = "$OPENROUTER_API_KEY";

    /// <summary>
    /// Builds the guest <c>providers.json</c> document in the vendor's
    /// <c>/connect</c> template shape. <paramref name="modelIds"/> seeds the
    /// advisory <c>models</c> map; each id is stored in bare
    /// provider-catalog form (the <c>openrouter/</c> qualifier stripped —
    /// verified: the CLI warns <c>"&lt;bare&gt;" isn't declared under
    /// provider 'openrouter'</c> and sends it anyway when the entry is
    /// missing). Blank entries are dropped.
    /// </summary>
    public static string BuildProvidersJson(IEnumerable<string?> modelIds)
    {
        var models = modelIds
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => StripProviderPrefix(m!.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("provider");
            writer.WriteStartObject(ProviderId);
            writer.WriteString("name", "OpenRouter");
            writer.WriteString("baseURL", DefaultBaseUrl);
            writer.WriteString("apiKey", ApiKeyEnvReference);
            writer.WriteStartObject("models");
            foreach (var model in models)
            {
                writer.WriteStartObject(model);
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
    /// Builds the guest <c>auth.json</c> document. <paramref name="placeholderApiKey"/>
    /// must be the documented non-credential placeholder (see
    /// <see cref="CmdAgentRunner.LocalOnlyAuthPlaceholder"/>) — this file
    /// satisfies only the print-mode presence gate for plan-less
    /// <c>--local-only</c> BYOK runs and must never carry a real credential.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="placeholderApiKey"/> is blank: an empty
    /// file key fails the gate at dispatch time, so fail here instead with a
    /// named cause.
    /// </exception>
    public static string BuildAuthJson(string placeholderApiKey)
    {
        if (string.IsNullOrWhiteSpace(placeholderApiKey))
            throw new ArgumentException(
                "Command Code auth placeholder must be non-blank: the CLI gates print mode on its presence.",
                nameof(placeholderApiKey));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("apiKey", placeholderApiKey.Trim());
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Strips the <c>openrouter/</c> provider qualifier from a <c>-m</c> id
    /// for the <c>models</c> map key: the CLI resolves
    /// <c>-m openrouter/&lt;id&gt;</c> against the map entry
    /// <c>&lt;id&gt;</c> under that provider (verified live — the CLI's own
    /// auto-created file keys the map by the bare id). Ids without the
    /// qualifier pass through unchanged.
    /// </summary>
    internal static string StripProviderPrefix(string modelId)
    {
        var prefix = ProviderId + "/";
        if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return modelId[prefix.Length..];
        return modelId;
    }
}
