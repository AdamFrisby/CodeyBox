using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads an opencode subscription credentials file on every pickup and
/// exposes its raw bytes to the sandbox as <c>OPENCODE_AUTH_JSON</c>, plus
/// an optional in-sandbox destination path under <c>OPENCODE_AUTH_DEST_PATH</c>.
///
/// <para><see cref="CodeyBox.Agents.Opencode.OpencodeAgentRunner"/> writes
/// the bytes back to disk inside the VM before invoking <c>opencode run</c>.
/// Re-reading on each pickup picks up token rotations from the host's
/// opencode CLI without an orchestrator restart, mirroring the Codex flow.</para>
///
/// <para>This provider only handles <see cref="AgentKind.Opencode"/>; it
/// returns null for any other agent so the chained env-var provider can
/// supply API-key based auth where applicable.</para>
/// </summary>
public sealed class OpencodeOAuthFileCredentialProvider : ICredentialProvider, IDisposable
{
    private readonly CredentialFileSource _source;
    private readonly string? _destinationPath;
    private readonly ILogger<OpencodeOAuthFileCredentialProvider>? _log;
    private readonly bool _ownsSource;
    private bool _disposed;

    public OpencodeOAuthFileCredentialProvider(
        string filePath,
        string? destinationPath = null,
        ILogger<OpencodeOAuthFileCredentialProvider>? log = null,
        bool watch = true)
        : this(
            new CredentialFileSource(
                filePath ?? throw new ArgumentNullException(nameof(filePath)), log, watch),
            destinationPath,
            log,
            ownsSource: true)
    {
    }

    public OpencodeOAuthFileCredentialProvider(
        CredentialFileSource source,
        string? destinationPath = null,
        ILogger<OpencodeOAuthFileCredentialProvider>? log = null)
        : this(source, destinationPath, log, ownsSource: false)
    {
    }

    private OpencodeOAuthFileCredentialProvider(
        CredentialFileSource source,
        string? destinationPath,
        ILogger<OpencodeOAuthFileCredentialProvider>? log,
        bool ownsSource)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _destinationPath = string.IsNullOrWhiteSpace(destinationPath) ? null : destinationPath;
        _log = log;
        _ownsSource = ownsSource;
    }

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        if (agent != AgentKind.Opencode)
            return Task.FromResult<AgentCredential?>(null);

        var raw = _source.GetRaw();
        if (string.IsNullOrWhiteSpace(raw))
        {
            _log?.LogDebug("Opencode auth file not present or empty at {Path}; falling through", _source.FilePath);
            return Task.FromResult<AgentCredential?>(null);
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENCODE_AUTH_JSON"] = raw,
        };
        if (_destinationPath is not null)
            env["OPENCODE_AUTH_DEST_PATH"] = _destinationPath;

        return Task.FromResult<AgentCredential?>(
            new AgentCredential(AgentKind.Opencode, env, new Dictionary<string, string>()));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsSource)
            _source.Dispose();
    }
}
