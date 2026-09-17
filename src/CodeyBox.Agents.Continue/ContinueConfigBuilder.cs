namespace CodeyBox.Agents.Continue;

/// <summary>
/// Pure builder for the guest <c>~/.continue/config.yaml</c> the runner
/// materialises before dispatch (verified against @continuedev/cli 1.5.47).
///
/// <para><b>Why a file, not the environment.</b> The <c>cn</c> CLI reads the
/// provider key from <c>config.yaml</c> (<c>~/.continue/config.yaml</c> by
/// default, or <c>--config &lt;path&gt;</c>). The <c>${{ secrets.X }}</c>
/// template form does resolve against process env (verified live), but the
/// runner writes the literal key instead — one fewer resolution mechanism to
/// drift — through <see cref="Sandbox.SandboxCredentialFileWriter"/> (stdin
/// transport, mode 0600), never argv. The interactive first-run onboarding
/// cannot run headless, so the runner seeds the file during provisioning
/// instead; the bake additionally creates the
/// <c>~/.continue/.onboarding_complete</c> marker (see
/// <c>docs/reference/sandbox-baselines.md</c>).</para>
///
/// <para><b>Model selection is first-entry-wins.</b> Verified live with two
/// different free models in one file: the run served the FIRST
/// <c>models[]</c> entry, and <c>--model &lt;name&gt;</c> (a hub-slug adder)
/// did not switch entries. The runner therefore seeds exactly ONE entry —
/// the effective dispatch model (explicit member model, else the
/// config-sourced default) — so a per-member <c>ModelId</c> can never
/// silently dispatch the wrong model. The provider id is the first-class
/// <c>openrouter</c> with <c>apiBase</c> + <c>apiKey</c> (verified live
/// against the OpenRouter v1 endpoint).</para>
///
/// <para>The document keeps only what headless dispatch needs: the single
/// model entry. No MCP servers, rules, or TUI state influence a one-shot
/// run.</para>
/// </summary>
public static class ContinueConfigBuilder
{
    /// <summary>
    /// Guest-relative path of the Continue user config under <c>$HOME</c> —
    /// the CLI's default lookup. Shared with
    /// <see cref="ContinueAgentRunner"/> so the seeding step and the CLI's
    /// default lookup can never drift: the runner writes exactly where the
    /// CLI reads, and passes no <c>--config</c> flag. The file is
    /// overwritten on every dispatch (the sandbox VM is throwaway).
    /// </summary>
    public const string GuestConfigRelativePath = ".continue/config.yaml";

    /// <summary>
    /// First-class provider id carrying the OpenRouter path (verified live:
    /// <c>provider: openrouter</c> with <c>apiBase</c> +
    /// <c>apiKey</c> served a request with no Continue account).
    /// </summary>
    public const string ProviderId = "openrouter";

    /// <summary>
    /// Builds the guest config document with a single model entry for
    /// <paramref name="modelId"/>. <paramref name="baseUrl"/> is the
    /// OpenAI-compatible endpoint (shipped as OpenRouter v1; hot-reloadable
    /// via <c>CodeyBox:Continue:BaseUrl</c>). Values are YAML single-quoted
    /// (<c>'</c> escaped as <c>''</c>) so keys containing <c>:</c>,
    /// <c>#</c>, or quotes survive the parse.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="baseUrl"/>, <paramref name="apiKey"/>, or
    /// <paramref name="modelId"/> is blank, or when the key carries a
    /// newline (never valid in an API key; would break the YAML document).
    /// </exception>
    public static string BuildConfigYaml(string? baseUrl, string apiKey, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Continue base URL must be non-blank.", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException(
                "Continue API key must be non-blank: the CLI never backfills the config key from the environment.",
                nameof(apiKey));
        if (apiKey.Contains('\n') || apiKey.Contains('\r'))
            throw new ArgumentException(
                "Continue API key must not contain a newline.",
                nameof(apiKey));
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Continue model id must be non-blank.", nameof(modelId));

        var model = modelId.Trim();
        return
            "name: CodeyBox Config\n" +
            "version: 1.0.0\n" +
            "schema: v1\n" +
            "models:\n" +
            $"  - name: {Quote(model)}\n" +
            $"    provider: {ProviderId}\n" +
            $"    model: {Quote(model)}\n" +
            $"    apiBase: {Quote(baseUrl.Trim())}\n" +
            $"    apiKey: {Quote(apiKey)}\n";
    }

    internal static string Quote(string value) =>
        "'" + value.Replace("'", "''") + "'";
}
