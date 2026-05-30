using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Per-provider OAuth-refresh sources used by the subscription quota probes
/// (Gemini, Codex, Claude). The probes consult the host's credential file each
/// time the router picks up an agent membership; without a refresher the probe
/// would happily forward an expired access_token, the provider would 401, the
/// snapshot would become "unknown" (AvailablePct=-1), and the router's default
/// UnknownPolicy=UseObservedFailures would fall open — assigning work that
/// subsequently 429s on the real provider call.
///
/// <para>Each source wraps the existing <see cref="CredentialFileSource"/> so
/// out-of-band rewrites by the host CLI are still observed. When the file's
/// embedded expiry is in the past (or within the
/// <see cref="DefaultExpirySkew"/> safety margin), the source posts the file's
/// refresh_token to the provider's OAuth refresh endpoint, persists the new
/// access_token + expiry back to disk atomically (preserving 0600 perms), and
/// caches the result in-process for at most <c>expires_in - skew</c>.</para>
///
/// <para>Concurrency: a per-instance <see cref="SemaphoreSlim"/> serialises
/// refresh round-trips so N parallel router calls produce one HTTP request.
/// Failure path: refresh errors are logged at Warning at most once per
/// <see cref="WarningSuppressionWindow"/> per source and the caller observes
/// <c>null</c> (the probe maps that to AvailablePct=-1 "unknown" — the same
/// state as before, but without spamming logs).</para>
/// </summary>
public interface IOauthCredentialTokenSource : IDisposable
{
    /// <summary>Path to the underlying credentials file (for diagnostics).</summary>
    string FilePath { get; }
}

/// <summary>Gemini (Google Code Assist OAuth) quota-probe token source.</summary>
public interface IGeminiQuotaTokenSource : IOauthCredentialTokenSource
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>Codex (ChatGPT OAuth) quota-probe token source.</summary>
public interface ICodexQuotaTokenSource : IOauthCredentialTokenSource
{
    Task<(string? AccessToken, string? AccountId)> GetTokensAsync(CancellationToken ct = default);
}

/// <summary>Claude (Anthropic OAuth) quota-probe token source.</summary>
public interface IClaudeQuotaTokenSource : IOauthCredentialTokenSource
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Shared machinery for the three provider-specific refreshers: in-process
/// cache, refresh serialisation, atomic file rewrite, and rate-limited warning
/// logs.
/// </summary>
public abstract class OauthCredentialFileRefresher : IDisposable
{
    /// <summary>Treat the token as expired this many seconds before its real expiry.</summary>
    internal static readonly TimeSpan DefaultExpirySkew = TimeSpan.FromSeconds(60);

    /// <summary>Suppress repeated refresh-failure warnings within this window.</summary>
    internal static readonly TimeSpan WarningSuppressionWindow = TimeSpan.FromMinutes(10);

    protected readonly CredentialFileSource Source;
    protected readonly IHttpClientFactory HttpClientFactory;
    protected readonly TimeProvider TimeProvider;
    protected readonly ILogger Log;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _warnGate = new();
    private DateTimeOffset _lastWarnAt = DateTimeOffset.MinValue;
    private bool _disposed;

    // Cached refresh result. Holds the access_token last produced by a refresh
    // round-trip plus its computed expiry. Reads through the file source still
    // beat this cache when the file is fresh — this is purely an
    // amplification-bounded cache of the refresh endpoint's output.
    protected readonly object CacheLock = new();
    protected string? CachedAccessToken;
    protected DateTimeOffset CachedExpiresAt = DateTimeOffset.MinValue;

    public string FilePath => Source.FilePath;

    protected OauthCredentialFileRefresher(
        CredentialFileSource source,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger log)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        HttpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        TimeProvider = timeProvider ?? TimeProvider.System;
        Log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Reads the file, decides whether the in-file access_token is still fresh,
    /// and returns it; otherwise serialises through the refresh gate and posts
    /// to the provider's OAuth endpoint. Returns <c>null</c> on any failure.
    /// </summary>
    protected async Task<string?> GetOrRefreshAsync(CancellationToken ct)
    {
        if (_disposed) return null;

        var raw = Source.GetRaw();
        if (string.IsNullOrEmpty(raw))
            return null;

        ParsedCreds parsed;
        try
        {
            parsed = ParseCreds(raw);
        }
        catch (JsonException ex)
        {
            MaybeWarn(ex, "Credential file {Path} did not parse for refresh");
            return null;
        }

        if (parsed.AccessToken is null)
            return null;

        var now = TimeProvider.GetUtcNow();
        if (parsed.ExpiresAt is { } exp && exp > now + DefaultExpirySkew)
            return parsed.AccessToken;

        // File-embedded token is stale. Honour any cache from a previous refresh
        // before locking; tests rely on parallel callers all observing the same
        // refreshed token without a queue.
        lock (CacheLock)
        {
            if (CachedAccessToken is not null && CachedExpiresAt > now + DefaultExpirySkew)
                return CachedAccessToken;
        }

        if (parsed.RefreshToken is null)
        {
            MaybeWarn(null, "Credential file {Path} has expired access_token but no refresh_token; cannot refresh");
            return null;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate — the previous holder may have
            // refreshed and updated CachedAccessToken.
            now = TimeProvider.GetUtcNow();
            lock (CacheLock)
            {
                if (CachedAccessToken is not null && CachedExpiresAt > now + DefaultExpirySkew)
                    return CachedAccessToken;
            }

            // Re-parse from disk in case the file rotated while we waited.
            raw = Source.GetRaw();
            if (string.IsNullOrEmpty(raw))
                return null;
            try
            {
                parsed = ParseCreds(raw);
            }
            catch (JsonException ex)
            {
                MaybeWarn(ex, "Credential file {Path} did not parse for refresh");
                return null;
            }

            now = TimeProvider.GetUtcNow();
            if (parsed.AccessToken is not null && parsed.ExpiresAt is { } expAgain && expAgain > now + DefaultExpirySkew)
                return parsed.AccessToken;

            if (parsed.RefreshToken is null)
            {
                MaybeWarn(null, "Credential file {Path} has expired access_token but no refresh_token; cannot refresh");
                return null;
            }

            RefreshResult result;
            try
            {
                result = await PerformRefreshAsync(parsed, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                MaybeWarn(ex, "OAuth refresh failed for {Path}");
                return null;
            }

            if (result.AccessToken is null)
            {
                MaybeWarn(null, "OAuth refresh returned no access_token for {Path}");
                return null;
            }

            var expiresAt = TimeProvider.GetUtcNow() + result.ExpiresIn;
            lock (CacheLock)
            {
                CachedAccessToken = result.AccessToken;
                CachedExpiresAt = expiresAt;
            }

            // Persist back to disk so subsequent reads (and the in-VM CLI) pick
            // up the new token. Failure here is non-fatal — the in-process cache
            // still serves the new token for the rest of this process's life.
            try
            {
                PersistRefreshedToken(parsed, result, expiresAt);
            }
            catch (Exception ex)
            {
                Log.LogWarning(
                    ex,
                    "Refreshed OAuth token for {Path} but could not write back to disk; in-process cache only",
                    Source.FilePath);
            }

            return result.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Parse the credential file into the fields we need for a refresh.</summary>
    protected abstract ParsedCreds ParseCreds(string rawJson);

    /// <summary>Perform the provider-specific OAuth refresh round-trip.</summary>
    protected abstract Task<RefreshResult> PerformRefreshAsync(ParsedCreds creds, CancellationToken ct);

    /// <summary>Merge the refreshed token + expiry into the existing creds JSON shape.</summary>
    protected abstract string BuildPersistedJson(string existingRaw, RefreshResult result, DateTimeOffset newExpiresAt);

    private void PersistRefreshedToken(ParsedCreds parsed, RefreshResult result, DateTimeOffset newExpiresAt)
    {
        var existingRaw = Source.GetRaw();
        if (string.IsNullOrEmpty(existingRaw)) return;
        var nextJson = BuildPersistedJson(existingRaw, result, newExpiresAt);
        AtomicWriteCredsFile(Source.FilePath, nextJson);
    }

    /// <summary>
    /// Atomic-rename write that preserves 0600 perms on POSIX. The tempfile is
    /// created with explicit 0600 perms via <see cref="FileStreamOptions.UnixCreateMode"/>
    /// so the access_token bytes never sit on disk under the process umask
    /// (typically 0644 = world-readable) — closing the local-user read race that
    /// a separate post-create chmod would leave open. The tempfile lives in the
    /// same directory so the rename is atomic on the same filesystem.
    /// </summary>
    private static void AtomicWriteCredsFile(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var fs = new FileStream(tmp, options))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                sw.Write(contents);
            }
            File.Move(tmp, path, overwrite: true);
            tmp = null!;
        }
        finally
        {
            if (tmp is not null && File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }
    }

    private void MaybeWarn(Exception? ex, string template)
    {
        var now = TimeProvider.GetUtcNow();
        lock (_warnGate)
        {
            if (now - _lastWarnAt < WarningSuppressionWindow)
                return;
            _lastWarnAt = now;
        }
        if (ex is null)
            Log.LogWarning(template, Source.FilePath);
        else
            Log.LogWarning(ex, template, Source.FilePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }

    protected sealed record ParsedCreds(
        string? AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        string? ClientId,
        string? ClientSecret,
        string? AccountId);

    protected sealed record RefreshResult(
        string? AccessToken,
        string? RefreshToken,
        TimeSpan ExpiresIn);
}

/// <summary>
/// Refreshes Google Code Assist OAuth tokens (gemini CLI subscription path).
/// Reads <c>~/.gemini/oauth_creds.json</c> — schema:
/// <code>
/// { "access_token": "...", "refresh_token": "...", "client_id": "...",
///   "client_secret": "...", "expiry_date": &lt;ms-since-epoch&gt; }
/// </code>
///
/// <para>Refresh strategy:</para>
/// <list type="number">
///   <item>HTTP refresh using client_id + client_secret pooled from whichever
///   source is available (file creds take precedence; config fallback from
///   env var or <c>codeybox-extra.json</c> fills in missing values). This is
///   a single attempt — if HTTP creds exist but the call fails, CLI is not
///   attempted.</item>
///   <item>CLI-based refresh (only when no client credentials are available
///   for HTTP): invoke the host <c>gemini</c> CLI, which self-refreshes
///   using its own embedded OAuth client, then re-read
///   <c>~/.gemini/oauth_creds.json</c> for the new access_token/expiry.</item>
/// </list>
/// </summary>
public sealed class GeminiOauthCredentialFileRefresher
    : OauthCredentialFileRefresher, IGeminiQuotaTokenSource
{
    internal const string DefaultRefreshEndpoint = "https://oauth2.googleapis.com/token";
    internal const string HttpClientName = "agent-quota";

    /// <summary>Timeout for the gemini CLI refresh sub-process.</summary>
    internal static readonly TimeSpan GeminiCliRefreshTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Fallback token lifetime used when the CLI-refreshed file carries no expiry.</summary>
    internal static readonly TimeSpan FallbackTokenLifetime = TimeSpan.FromHours(1);

    private readonly string _refreshEndpoint;
    private readonly string? _fallbackClientId;
    private readonly string? _fallbackClientSecret;
    private readonly Func<CancellationToken, Task<bool>>? _cliTokenRefresher;

    public GeminiOauthCredentialFileRefresher(
        GeminiOAuthCredentialFileSource source,
        IHttpClientFactory httpClientFactory,
        ILogger<GeminiOauthCredentialFileRefresher> log,
        TimeProvider? timeProvider = null,
        string? refreshEndpoint = null,
        string? geminiOauthClientId = null,
        string? geminiOauthClientSecret = null,
        Func<CancellationToken, Task<bool>>? cliTokenRefresher = null)
        : base(source, httpClientFactory, timeProvider ?? TimeProvider.System, log)
    {
        _refreshEndpoint = refreshEndpoint ?? DefaultRefreshEndpoint;
        _fallbackClientId = geminiOauthClientId;
        _fallbackClientSecret = geminiOauthClientSecret;
        _cliTokenRefresher = cliTokenRefresher;
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => GetOrRefreshAsync(ct);

    protected override ParsedCreds ParseCreds(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        string? access = TryString(root, "access_token");
        string? refresh = TryString(root, "refresh_token");
        string? clientId = TryString(root, "client_id");
        string? clientSecret = TryString(root, "client_secret");
        DateTimeOffset? expires = null;
        if (root.TryGetProperty("expiry_date", out var exp) && exp.ValueKind == JsonValueKind.Number
            && exp.TryGetInt64(out var ms))
        {
            expires = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        return new ParsedCreds(access, refresh, expires, clientId, clientSecret, AccountId: null);
    }

    protected override async Task<RefreshResult> PerformRefreshAsync(ParsedCreds creds, CancellationToken ct)
    {
        var clientId = creds.ClientId ?? _fallbackClientId;
        var clientSecret = creds.ClientSecret ?? _fallbackClientSecret;

        if (!string.IsNullOrEmpty(creds.RefreshToken)
            && !string.IsNullOrEmpty(clientId)
            && !string.IsNullOrEmpty(clientSecret))
        {
            return await HttpRefreshAsync(creds.RefreshToken!, clientId!, clientSecret!, ct)
                .ConfigureAwait(false);
        }

        if (_cliTokenRefresher is not null && await _cliTokenRefresher(ct).ConfigureAwait(false))
        {
            var raw = Source.GetRaw();
            if (!string.IsNullOrEmpty(raw))
            {
                var reparsed = ParseCreds(raw);
                if (!string.IsNullOrEmpty(reparsed.AccessToken))
                {
                    var expiresAt = reparsed.ExpiresAt ?? TimeProvider.GetUtcNow() + FallbackTokenLifetime;
                    var expiresIn = expiresAt - TimeProvider.GetUtcNow();
                    if (expiresIn <= TimeSpan.Zero) expiresIn = FallbackTokenLifetime;
                    return new RefreshResult(reparsed.AccessToken, reparsed.RefreshToken, expiresIn);
                }
            }
        }

        return new RefreshResult(null, null, TimeSpan.Zero);
    }

    private async Task<RefreshResult> HttpRefreshAsync(
        string refreshToken, string clientId, string clientSecret, CancellationToken ct)
    {
        var http = HttpClientFactory.CreateClient(HttpClientName);
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _refreshEndpoint) { Content = form };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            return new RefreshResult(null, null, TimeSpan.Zero);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var newAccess = TryString(doc.RootElement, "access_token");
        var newRefresh = TryString(doc.RootElement, "refresh_token");
        var seconds = doc.RootElement.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number
            && ex.TryGetInt32(out var s) ? s : (int)FallbackTokenLifetime.TotalSeconds;
        return new RefreshResult(newAccess, newRefresh, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Returns a delegate that invokes the host <c>gemini</c> CLI to force an
    /// OAuth token refresh, or null if the CLI cannot be found.
    /// The delegate launches <c>gemini -p "."</c> with a 30 s timeout and
    /// returns <c>true</c> when the process exits successfully (exit code 0),
    /// which indicates the CLI refreshed and rewrote <c>~/.gemini/oauth_creds.json</c>.
    /// </summary>
    /// <param name="resolvePath">Optional test seam. When non-null, used instead
    /// of <see cref="ResolveGeminiCliPath"/> to resolve the gemini binary path.</param>
    internal static Func<CancellationToken, Task<bool>>? TryCreateCliRefreshHandler(
        Func<string?>? resolvePath = null)
    {
        var cliPath = (resolvePath ?? ResolveGeminiCliPath)();
        if (cliPath is null) return null;
        return BuildCliRefreshDelegate(cliPath);
    }

    private static Func<CancellationToken, Task<bool>> BuildCliRefreshDelegate(string cliPath)
    {
        return async ct =>
        {
            Process? proc = null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(GeminiCliRefreshTimeout);
                var psi = new ProcessStartInfo
                {
                    FileName = cliPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(".");
                proc = Process.Start(psi)!;
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return proc.ExitCode == 0;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException
                or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                try { proc?.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }
            finally
            {
                proc?.Dispose();
            }
        };
    }

    internal static readonly TimeSpan WhichTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resolves the absolute path to the host <c>gemini</c> binary using
    /// <c>which</c> (POSIX) or <c>where</c> (Windows). Returns <c>null</c>
    /// when the binary is not found or any OS error occurs.
    /// </summary>
    internal static string? ResolveGeminiCliPath()
    {
        return ResolveExecutablePath(OperatingSystem.IsWindows() ? "where" : "which", "gemini");
    }

    /// <summary>Resolves an executable path using the platform-specific resolver command.</summary>
    internal static string? ResolveExecutablePath(string resolverCommand, string targetBinary)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = resolverCommand,
                ArgumentList = { targetBinary },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return null;
            if (!proc.WaitForExit(WhichTimeout))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            if (proc.ExitCode == 0)
            {
                var path = proc.StandardOutput.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception) { }
        return null;
    }

    protected override string BuildPersistedJson(string existingRaw, RefreshResult result, DateTimeOffset newExpiresAt)
    {
        using var doc = JsonDocument.Parse(existingRaw);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("access_token"))
                {
                    writer.WriteString("access_token", result.AccessToken);
                }
                else if (prop.NameEquals("refresh_token") && !string.IsNullOrEmpty(result.RefreshToken))
                {
                    writer.WriteString("refresh_token", result.RefreshToken);
                }
                else if (prop.NameEquals("expiry_date"))
                {
                    writer.WriteNumber("expiry_date", newExpiresAt.ToUnixTimeMilliseconds());
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            if (!doc.RootElement.TryGetProperty("access_token", out _))
                writer.WriteString("access_token", result.AccessToken);
            if (!doc.RootElement.TryGetProperty("expiry_date", out _))
                writer.WriteNumber("expiry_date", newExpiresAt.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? TryString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Refreshes ChatGPT OAuth tokens (codex CLI subscription path).
/// Reads <c>~/.codex/auth.json</c> — schema:
/// <code>
/// { "tokens": { "id_token": "...", "access_token": "...", "refresh_token": "...", "account_id": "..." } }
/// </code>
/// The access_token is a JWT; expiry is read from the embedded <c>exp</c>
/// claim. Refreshes via <c>POST https://auth.openai.com/oauth/token</c> with
/// <c>grant_type=refresh_token</c>. The codex CLI's hardcoded client id is
/// configurable via the constructor for tests / future ChatGPT app changes.
/// </summary>
public sealed class CodexOauthCredentialFileRefresher
    : OauthCredentialFileRefresher, ICodexQuotaTokenSource
{
    internal const string DefaultRefreshEndpoint = "https://auth.openai.com/oauth/token";

    /// <summary>
    /// Codex CLI's public client id. Pulled into a constant so tests can
    /// override without environment manipulation; in production this matches
    /// the value the codex CLI bundles in its own source.
    /// </summary>
    internal const string DefaultClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    internal const string HttpClientName = "agent-quota";

    private readonly string _refreshEndpoint;
    private readonly string _clientId;
    private string? _cachedAccountId;

    public CodexOauthCredentialFileRefresher(
        CodexCredentialFileSource source,
        IHttpClientFactory httpClientFactory,
        ILogger<CodexOauthCredentialFileRefresher> log,
        TimeProvider? timeProvider = null,
        string? refreshEndpoint = null,
        string? clientId = null)
        : base(source, httpClientFactory, timeProvider ?? TimeProvider.System, log)
    {
        _refreshEndpoint = refreshEndpoint ?? DefaultRefreshEndpoint;
        _clientId = clientId ?? DefaultClientId;
    }

    public async Task<(string? AccessToken, string? AccountId)> GetTokensAsync(CancellationToken ct = default)
    {
        var token = await GetOrRefreshAsync(ct).ConfigureAwait(false);
        string? accountId;
        lock (CacheLock) accountId = _cachedAccountId;
        if (token is null && accountId is null)
        {
            // Fall back to whatever the file currently contains so callers still
            // see the account id when the file is parseable but refresh is
            // unneeded.
            var raw = Source.GetRaw();
            if (!string.IsNullOrEmpty(raw))
            {
                try { accountId = ParseCreds(raw).AccountId; }
                catch (JsonException) { /* swallow */ }
            }
        }
        return (token, accountId);
    }

    protected override ParsedCreds ParseCreds(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tokens", out var tokens)
            || tokens.ValueKind != JsonValueKind.Object)
        {
            return new ParsedCreds(null, null, null, null, null, null);
        }
        var access = tokens.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : null;
        var refresh = tokens.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        var account = tokens.TryGetProperty("account_id", out var acc) && acc.ValueKind == JsonValueKind.String
            ? acc.GetString() : null;
        lock (CacheLock) _cachedAccountId = account ?? _cachedAccountId;
        var expires = ExtractJwtExpiry(access);
        return new ParsedCreds(access, refresh, expires, ClientId: null, ClientSecret: null, AccountId: account);
    }

    protected override async Task<RefreshResult> PerformRefreshAsync(ParsedCreds creds, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(creds.RefreshToken))
            return new RefreshResult(null, null, TimeSpan.Zero);

        var http = HttpClientFactory.CreateClient(HttpClientName);
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", creds.RefreshToken!),
            new KeyValuePair<string, string>("client_id", _clientId),
            // Codex's scope set; harmless if the server ignores it on refresh.
            new KeyValuePair<string, string>("scope", "openid profile email"),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _refreshEndpoint) { Content = form };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            return new RefreshResult(null, null, TimeSpan.Zero);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var newAccess = doc.RootElement.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetString() : null;
        var newRefresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString() : null;
        var jwtExpiry = ExtractJwtExpiry(newAccess);
        var ttl = jwtExpiry is { } e && e > TimeProvider.GetUtcNow()
            ? e - TimeProvider.GetUtcNow()
            : (doc.RootElement.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number
                && ex.TryGetInt32(out var s)
                    ? TimeSpan.FromSeconds(s)
                    : TimeSpan.FromHours(1));
        return new RefreshResult(newAccess, newRefresh, ttl);
    }

    protected override string BuildPersistedJson(string existingRaw, RefreshResult result, DateTimeOffset newExpiresAt)
    {
        using var doc = JsonDocument.Parse(existingRaw);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("tokens") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("tokens");
                    writer.WriteStartObject();
                    foreach (var t in prop.Value.EnumerateObject())
                    {
                        if (t.NameEquals("access_token"))
                            writer.WriteString("access_token", result.AccessToken);
                        else if (t.NameEquals("refresh_token") && !string.IsNullOrEmpty(result.RefreshToken))
                            writer.WriteString("refresh_token", result.RefreshToken);
                        else
                            t.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                else if (prop.NameEquals("last_refresh"))
                {
                    writer.WriteString("last_refresh", TimeProvider.GetUtcNow().ToString("o"));
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Decode a JWT's <c>exp</c> claim without verifying the signature; the
    /// expiry tells us when to refresh and is unauthenticated by design — a
    /// malicious server-side rewrite can only cause us to refresh sooner, never
    /// later, so signature verification adds nothing here.
    /// </summary>
    internal static DateTimeOffset? ExtractJwtExpiry(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("exp", out var exp)
                && exp.ValueKind == JsonValueKind.Number
                && exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        catch (FormatException) { }
        catch (JsonException) { }
        return null;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// Refreshes Anthropic OAuth tokens (claude CLI subscription path).
/// Reads <c>~/.claude/.credentials.json</c> — schema:
/// <code>
/// { "claudeAiOauth": { "accessToken": "...", "refreshToken": "...", "expiresAt": &lt;epoch-ms&gt; } }
/// </code>
/// Refreshes via <c>POST https://console.anthropic.com/v1/oauth/token</c>.
/// </summary>
public sealed class ClaudeOauthCredentialFileRefresher
    : OauthCredentialFileRefresher, IClaudeQuotaTokenSource
{
    internal const string DefaultRefreshEndpoint = "https://console.anthropic.com/v1/oauth/token";
    internal const string DefaultClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    internal const string HttpClientName = "agent-quota";

    private readonly string _refreshEndpoint;
    private readonly string _clientId;

    public ClaudeOauthCredentialFileRefresher(
        ClaudeCredentialFileSource source,
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeOauthCredentialFileRefresher> log,
        TimeProvider? timeProvider = null,
        string? refreshEndpoint = null,
        string? clientId = null)
        : base(source, httpClientFactory, timeProvider ?? TimeProvider.System, log)
    {
        _refreshEndpoint = refreshEndpoint ?? DefaultRefreshEndpoint;
        _clientId = clientId ?? DefaultClientId;
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => GetOrRefreshAsync(ct);

    protected override ParsedCreds ParseCreds(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("claudeAiOauth", out var oauth)
            || oauth.ValueKind != JsonValueKind.Object)
        {
            return new ParsedCreds(null, null, null, null, null, null);
        }
        var access = oauth.TryGetProperty("accessToken", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : null;
        var refresh = oauth.TryGetProperty("refreshToken", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        DateTimeOffset? expires = null;
        if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number
            && exp.TryGetInt64(out var n))
        {
            // Anthropic emits milliseconds-since-epoch; older snapshots used
            // seconds. Disambiguate by magnitude: < 1e11 → seconds (would put
            // anything realistic before year 5138), else milliseconds.
            expires = n < 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeSeconds(n)
                : DateTimeOffset.FromUnixTimeMilliseconds(n);
        }
        return new ParsedCreds(access, refresh, expires, ClientId: null, ClientSecret: null, AccountId: null);
    }

    protected override async Task<RefreshResult> PerformRefreshAsync(ParsedCreds creds, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(creds.RefreshToken))
            return new RefreshResult(null, null, TimeSpan.Zero);

        var http = HttpClientFactory.CreateClient(HttpClientName);
        var body = JsonSerializer.Serialize(new
        {
            grant_type = "refresh_token",
            refresh_token = creds.RefreshToken,
            client_id = _clientId,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _refreshEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            return new RefreshResult(null, null, TimeSpan.Zero);

        var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(respBody);
        var newAccess = doc.RootElement.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetString() : null;
        var newRefresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString() : null;
        var seconds = doc.RootElement.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number
            && ex.TryGetInt32(out var s) ? s : 3600;
        return new RefreshResult(newAccess, newRefresh, TimeSpan.FromSeconds(seconds));
    }

    protected override string BuildPersistedJson(string existingRaw, RefreshResult result, DateTimeOffset newExpiresAt)
    {
        using var doc = JsonDocument.Parse(existingRaw);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("claudeAiOauth") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("claudeAiOauth");
                    writer.WriteStartObject();
                    var sawAccess = false;
                    var sawExpires = false;
                    foreach (var t in prop.Value.EnumerateObject())
                    {
                        if (t.NameEquals("accessToken"))
                        {
                            writer.WriteString("accessToken", result.AccessToken);
                            sawAccess = true;
                        }
                        else if (t.NameEquals("refreshToken") && !string.IsNullOrEmpty(result.RefreshToken))
                        {
                            writer.WriteString("refreshToken", result.RefreshToken);
                        }
                        else if (t.NameEquals("expiresAt"))
                        {
                            writer.WriteNumber("expiresAt", newExpiresAt.ToUnixTimeMilliseconds());
                            sawExpires = true;
                        }
                        else
                        {
                            t.WriteTo(writer);
                        }
                    }
                    if (!sawAccess) writer.WriteString("accessToken", result.AccessToken);
                    if (!sawExpires) writer.WriteNumber("expiresAt", newExpiresAt.ToUnixTimeMilliseconds());
                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
