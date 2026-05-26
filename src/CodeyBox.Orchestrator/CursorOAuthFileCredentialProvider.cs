using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the Cursor CLI's subscription credentials file on every
/// <see cref="GetAsync"/> call and exposes its contents as the env var
/// <c>CODEYBOX_CURSOR_AUTH_JSON</c> in the credential bundle.
///
/// <para>The Cursor CLI uses subscription auth written by <c>agent login</c>
/// to a credentials file on the host (path is operator-configurable; defaults
/// to <c>~/.cursor/credentials.json</c>). Unlike Claude, the CLI offers no
/// env-var alternative for OAuth — the orchestrator ships the file contents
/// into the sandbox via <c>CODEYBOX_CURSOR_AUTH_JSON</c> and the
/// <c>CursorAgentRunner</c> materialises a private copy inside the VM before
/// invoking the binary.</para>
///
/// <para>Re-reading on each pickup picks up token rotations from the host's
/// Cursor CLI without an orchestrator restart. The host's credentials
/// directory is intentionally NOT bind-mounted into the sandbox.</para>
///
/// Only handles <see cref="AgentKind.Cursor"/>; returns null for others so a
/// chained env-var provider can supply the auth blob directly.
/// </summary>
public sealed class CursorOAuthFileCredentialProvider : ICredentialProvider, IDisposable
{
    private readonly CredentialFileSource _source;
    private readonly ILogger<CursorOAuthFileCredentialProvider>? _log;
    private readonly bool _ownsSource;
    private bool _disposed;

    public CursorOAuthFileCredentialProvider(
        string filePath,
        ILogger<CursorOAuthFileCredentialProvider>? log = null,
        bool watch = true)
        : this(new CredentialFileSource(
            filePath ?? throw new ArgumentNullException(nameof(filePath)), log, watch), log, ownsSource: true)
    {
    }

    public CursorOAuthFileCredentialProvider(
        CredentialFileSource source,
        ILogger<CursorOAuthFileCredentialProvider>? log = null)
        : this(source, log, ownsSource: false)
    {
    }

    private CursorOAuthFileCredentialProvider(
        CredentialFileSource source,
        ILogger<CursorOAuthFileCredentialProvider>? log,
        bool ownsSource)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log;
        _ownsSource = ownsSource;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Cursor)
            return Task.FromResult<AgentCredential?>(null);

        var raw = _source.GetRaw();
        if (string.IsNullOrWhiteSpace(raw))
        {
            _log?.LogDebug("Cursor credentials file not present or empty at {Path}; falling through", _source.FilePath);
            return Task.FromResult<AgentCredential?>(null);
        }

        var credential = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = raw },
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
