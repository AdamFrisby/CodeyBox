using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the Devin CLI's credentials file on every <see cref="GetAsync"/>
/// call and exposes its contents as the env var
/// <c>CODEYBOX_DEVIN_AUTH_TOML</c> in the credential bundle.
///
/// <para>The Devin CLI uses subscription auth written by
/// <c>devin auth login</c> to <c>~/.local/share/devin/credentials.toml</c> on
/// the host (path is operator-configurable via
/// <c>CODEYBOX_DEVIN_AUTH_FILE</c>). The CLI offers no env-var alternative —
/// there is no <c>DEVIN_API_KEY</c> — so the orchestrator ships the file
/// contents into the sandbox via <c>CODEYBOX_DEVIN_AUTH_TOML</c> and the
/// <c>DevinAgentRunner</c> materialises a private copy at the same XDG path
/// inside the VM before invoking the binary.</para>
///
/// <para>Re-reading on each pickup picks up credential rotations from the
/// host's Devin CLI without an orchestrator restart. The host's credentials
/// directory is intentionally NOT bind-mounted into the sandbox.</para>
///
/// Only handles <see cref="AgentKind.Devin"/>; returns null for others so a
/// chained env-var provider can supply the auth blob directly.
/// </summary>
public sealed class DevinCliCredentialsFileCredentialProvider : ICredentialProvider, IDisposable
{
    private readonly CredentialFileSource _source;
    private readonly ILogger<DevinCliCredentialsFileCredentialProvider>? _log;
    private readonly bool _ownsSource;
    private bool _disposed;

    public DevinCliCredentialsFileCredentialProvider(
        string filePath,
        ILogger<DevinCliCredentialsFileCredentialProvider>? log = null,
        bool watch = true)
        : this(new CredentialFileSource(
            filePath ?? throw new ArgumentNullException(nameof(filePath)), log, watch,
            contentValidator: IsUsableCredentialsToml), log, ownsSource: true)
    {
    }

    /// <summary>
    /// Torn-write guard for devin's credentials.toml: the file is TOML (the
    /// JSON-shaped default validator would reject it), so well-formedness is
    /// "carries a non-empty token field" (<c>api_key</c> or
    /// <c>windsurf_api_key</c>) — the field the CLI cannot authenticate
    /// without. Shared by <see cref="DevinCredentialFileSource"/>.
    /// </summary>
    internal static bool IsUsableCredentialsToml(string contents) =>
        !string.IsNullOrWhiteSpace(
            CodeyBox.Agents.DevinCredentialsToml.TryGetToken(contents));

    public DevinCliCredentialsFileCredentialProvider(
        CredentialFileSource source,
        ILogger<DevinCliCredentialsFileCredentialProvider>? log = null)
        : this(source, log, ownsSource: false)
    {
    }

    private DevinCliCredentialsFileCredentialProvider(
        CredentialFileSource source,
        ILogger<DevinCliCredentialsFileCredentialProvider>? log,
        bool ownsSource)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log;
        _ownsSource = ownsSource;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Devin)
            return Task.FromResult<AgentCredential?>(null);

        var raw = _source.GetRaw();
        if (string.IsNullOrWhiteSpace(raw))
        {
            _log?.LogDebug("Devin credentials file not present or empty at {Path}; falling through", _source.FilePath);
            return Task.FromResult<AgentCredential?>(null);
        }

        var credential = new AgentCredential(
            AgentKind.Devin,
            // Same literal the Devin runner declares
            // (DevinAgentRunner.AuthTomlEnvironmentVariable) — the Orchestrator
            // does not reference the per-agent assembly, so the constant is
            // duplicated here the way the cursor provider duplicates
            // CODEYBOX_CURSOR_AUTH_JSON.
            new Dictionary<string, string> { ["CODEYBOX_DEVIN_AUTH_TOML"] = raw },
            new Dictionary<string, string>());
        return Task.FromResult<AgentCredential?>(credential);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsSource)
            _source.Dispose();
    }
}
