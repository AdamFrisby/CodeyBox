namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Lazily builds and owns a credential plugin's HTTP pair — a redirect-proof
/// <see cref="HttpClient"/> plus the backend client wrapping it — so client
/// creation, the timeout poke, and ownership/disposal cannot drift per
/// backend. The pair is built once under a gate (double-checked); the
/// configured timeout is applied while construction runs, and a poke that
/// loses to an in-flight request keeps the existing value. An injected
/// client (tests) is used as-is and never disposed here.
/// <para>Disposal is coordinated with construction: <see cref="Dispose"/>
/// takes the same gate, so a client can never be built after (or leak past)
/// disposal, and a post-dispose <see cref="Get"/> throws
/// <see cref="ObjectDisposedException"/> instead of handing out a client
/// whose transport is already gone.</para>
/// </summary>
/// <typeparam name="TClient">
/// The backend client wrapping the <see cref="HttpClient"/> — the HttpClient
/// itself when a plugin needs a bare forward client.
/// </typeparam>
public sealed class LazyCredentialClient<TClient> : IDisposable where TClient : class
{
    private readonly object _gate = new();
    private readonly Func<TimeSpan> _timeout;
    private readonly Func<TimeSpan>? _effectiveTimeout;
    private readonly Func<HttpClient, TClient> _factory;
    private HttpClient? _http;
    private TClient? _client;
    private bool _ownsHttp;
    private bool _disposed;

    /// <param name="timeout">
    /// Reads the current per-request timeout; evaluated each time
    /// construction runs so a hot-reloaded value applies to the next build.
    /// Also bounds construction of an owned client —
    /// <see cref="CredentialHttp.CreateNoRedirectClient(TimeSpan)"/>
    /// requires a finite value.
    /// </param>
    /// <param name="factory">Builds the backend client around the HttpClient.</param>
    /// <param name="injected">
    /// Optional caller-owned client (tests): used as-is, never disposed here.
    /// </param>
    /// <param name="effectiveTimeout">
    /// Optional override for the timeout poked onto the client after
    /// construction, when the effective client-level timeout should differ
    /// from the construction bound — e.g. a backend that enforces
    /// per-attempt timeouts itself and needs
    /// <see cref="Timeout.InfiniteTimeSpan"/> here. Defaults to
    /// <paramref name="timeout"/>.
    /// </param>
    public LazyCredentialClient(
        Func<TimeSpan> timeout,
        Func<HttpClient, TClient> factory,
        HttpClient? injected = null,
        Func<TimeSpan>? effectiveTimeout = null)
    {
        _timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _effectiveTimeout = effectiveTimeout;
        _http = injected;
    }

    /// <summary>
    /// The shared backend client, built on first use. Throws
    /// <see cref="ObjectDisposedException"/> once disposed.
    /// </summary>
    public TClient Get()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is { } ready)
            return ready;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is { } inside)
                return inside;
            _http ??= CreateHttp();
            try
            {
                _http.Timeout = (_effectiveTimeout ?? _timeout)();
            }
            catch (InvalidOperationException)
            {
                // A request is already in flight on this (injected) client;
                // keep its existing timeout.
            }
            _client = _factory(_http);
            return _client;
        }
    }

    /// <summary>
    /// Disposes the HttpClient only when this holder built it. Runs under
    /// the construction gate so a build in flight finishes before teardown
    /// and no client escapes disposal. Never throws: a faulted handler
    /// teardown must not mask the real teardown outcome.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _client = null;
            if (_ownsHttp)
            {
                try { _http?.Dispose(); } catch (Exception) { }
            }
        }
    }

    private HttpClient CreateHttp()
    {
        _ownsHttp = true;
        return CredentialHttp.CreateNoRedirectClient(_timeout());
    }
}
