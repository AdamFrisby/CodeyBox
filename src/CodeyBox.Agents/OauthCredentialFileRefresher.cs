using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Agents;

/// <summary>
/// Minimal read surface an OAuth credential refresher needs from a host-side
/// credential file: the path (for diagnostics and atomic rewrite) and a
/// freshness-checked snapshot of the raw bytes. Implemented by
/// <c>CodeyBox.Orchestrator.CredentialFileSource</c>; refreshers depend only on
/// this contract so provider integrations can live in their own
/// <c>CodeyBox.Agents.*</c> projects without referencing the orchestrator.
/// </summary>
public interface ICredentialFileReader
{
    /// <summary>Path to the credential file on the host filesystem.</summary>
    string FilePath { get; }

    /// <summary>
    /// Returns the current cached file contents, or <c>null</c> if the file
    /// is absent or unreadable.
    /// </summary>
    string? GetRaw();
}

/// <summary>
/// Provider-neutral token-source contract for the subscription quota probes.
/// Each agent integration exposes its own refinement in its
/// <c>CodeyBox.Agents.*</c> project alongside its concrete
/// <see cref="OauthCredentialFileRefresher"/>.
/// </summary>
public interface IOauthCredentialTokenSource : IDisposable
{
    /// <summary>Path to the underlying credentials file (for diagnostics).</summary>
    string FilePath { get; }
}

/// <summary>
/// Provider-neutral OAuth-refresh machinery for the subscription quota probes.
/// Each probe consults the host's credential file each time the router picks
/// up an agent membership; without a refresher the probe would happily forward
/// an expired access_token, the provider would 401, the snapshot would become
/// "unknown" (AvailablePct=-1), and the router's default
/// UnknownPolicy=UseObservedFailures would fall open — assigning work that
/// subsequently 429s on the real provider call.
///
/// <para>Each refresher wraps an <see cref="ICredentialFileReader"/> so
/// out-of-band rewrites by the host CLI are still observed. When the file's
/// embedded expiry is in the past (or within the
/// <see cref="DefaultExpirySkew"/> safety margin), the refresher runs the
/// provider-specific round-trip (see <see cref="PerformRefreshAsync"/>),
/// persists the new access_token + expiry back to disk atomically (preserving
/// 0600 perms), and caches the result in-process for at most
/// <c>expires_in - skew</c>.</para>
///
/// <para>Concurrency: a per-instance <see cref="SemaphoreSlim"/> serialises
/// refresh round-trips so N parallel router calls produce one HTTP request.
/// Failure path: refresh errors are logged at Warning at most once per
/// <see cref="WarningSuppressionWindow"/> per source and the caller observes
/// <c>null</c> (the probe maps that to AvailablePct=-1 "unknown" — the same
/// state as before, but without spamming logs).</para>
///
/// <para>Concrete refreshers live in their own <c>CodeyBox.Agents.*</c>
/// projects; this file holds only the neutral machinery (cache, gate, atomic
/// write, CLI-execution helpers) plus the <see cref="ICredentialFileReader"/>
/// and <see cref="IOauthCredentialTokenSource"/> contracts.</para>
/// </summary>
public abstract class OauthCredentialFileRefresher : IDisposable
{
    /// <summary>Treat the token as expired this many seconds before its real expiry.</summary>
    internal static readonly TimeSpan DefaultExpirySkew = TimeSpan.FromSeconds(60);

    /// <summary>Suppress repeated refresh-failure warnings within this window.</summary>
    internal static readonly TimeSpan WarningSuppressionWindow = TimeSpan.FromMinutes(10);

    protected readonly ICredentialFileReader Source;
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
        ICredentialFileReader source,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger log)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        HttpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        TimeProvider = timeProvider ?? TimeProvider.System;
        Log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected virtual bool RequiresRefreshToken => true;
    protected virtual bool CanRefreshWithoutFile => false;

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
        {
            if (!CanRefreshWithoutFile)
                return null;
            raw = "{}";
        }

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

        if (!CanRefreshWithoutFile && parsed.AccessToken is null)
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

        if (RequiresRefreshToken && parsed.RefreshToken is null)
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
            {
                if (!CanRefreshWithoutFile)
                    return null;
                raw = "{}";
            }
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

            if (RequiresRefreshToken && parsed.RefreshToken is null)
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
        var baseJson = string.IsNullOrEmpty(existingRaw) ? "{}" : existingRaw;
        var nextJson = BuildPersistedJson(baseJson, result, newExpiresAt);
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
    protected static void AtomicWriteCredsFile(string path, string contents)
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

    protected internal static readonly TimeSpan WhichTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Resolves an executable path using the platform-specific resolver command.</summary>
    protected internal static string? ResolveExecutablePath(string resolverCommand, string targetBinary)
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
                try { proc.Kill(entireProcessTree: true); } catch (Exception ex2) when (ex2 is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
                return null;
            }
            if (proc.ExitCode == 0)
            {
                var path = proc.StandardOutput.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        { }
        return null;
    }

    protected internal static string ResolveCliRefreshPathValue(string? inheritedPath)
    {
        if (!string.IsNullOrWhiteSpace(inheritedPath))
            return inheritedPath;

        return OperatingSystem.IsWindows()
            ? @"C:\Windows\System32;C:\Windows;C:\Windows\System32\Wbem"
            : "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    }

    protected internal static string ResolveCliRefreshWorkingDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
            return home;

        var temp = Path.GetTempPath();
        return Directory.Exists(temp) ? temp : Path.DirectorySeparatorChar.ToString();
    }

    protected internal static void PopulateCliRefreshEnvironment(ProcessStartInfo psi)
    {
        psi.Environment.Clear();
        foreach (var key in new[]
                 {
                     "HOME", "PATH", "LANG", "LC_ALL", "LC_CTYPE", "USER", "LOGNAME", "SHELL",
                     "SystemRoot", "WINDIR", "ComSpec", "PATHEXT", "TEMP", "TMP",
                     "DBUS_SESSION_BUS_ADDRESS", "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME",
                 })
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (val is not null) psi.Environment[key] = val;
        }

        psi.Environment["PATH"] = ResolveCliRefreshPathValue(
            psi.Environment.TryGetValue("PATH", out var path) ? path : null);
    }

    protected internal static async Task<bool> ExecuteCliProcessAsync(
        string cliPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                WorkingDirectory = ResolveCliRefreshWorkingDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }
            PopulateCliRefreshEnvironment(psi);
            proc = Process.Start(psi);
            if (proc is null) return false;
            // Bounded capture: the child can emit an arbitrary amount of
            // output and only the exit code is observed, so buffer at most
            // MaxCliOutputChars per stream while still draining both pipes
            // (abandoning a pipe would let the child block on a full buffer).
            var maxChars = OauthCredentialRefresherBounds.MaxCliOutputChars;
            var stdoutTask = ReadBoundedTextAsync(proc.StandardOutput, maxChars, cts.Token);
            var stderrTask = ReadBoundedTextAsync(proc.StandardError, maxChars, cts.Token);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try { proc?.Kill(entireProcessTree: true); } catch (Exception ex2) when (ex2 is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
            return false;
        }
        finally
        {
            proc?.Dispose();
        }
    }

    /// <summary>
    /// Single code path for every OAuth refresh response body. Streams the
    /// response under <see cref="OauthCredentialRefresherBounds.MaxRefreshBodyBytes"/>
    /// — read fresh on each call so config hot-reload applies — and reports
    /// an oversize body via <c>BodyTooLarge</c> without ever buffering it.
    /// Callers treat a too-large body exactly like a failed refresh (no token)
    /// rather than parsing unbounded endpoint-controlled bytes.
    /// </summary>
    internal static Task<BoundedHttpResponse> SendBoundedRefreshAsync(
        HttpClient http,
        HttpRequestMessage request,
        CancellationToken ct)
        => BoundedHttpResponseReader.SendAsync(
            http, request, OauthCredentialRefresherBounds.MaxRefreshBodyBytes, ct);

    /// <summary>
    /// Drains <paramref name="reader"/> to the end but buffers at most
    /// <paramref name="maxChars"/> characters, discarding the remainder.
    /// Draining (rather than abandoning the stream) keeps a child process
    /// from blocking on a full pipe buffer; discarding keeps memory bounded
    /// no matter how much the child emits.
    /// </summary>
    internal static async Task<string> ReadBoundedTextAsync(TextReader reader, int maxChars, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxChars < 1) throw new ArgumentOutOfRangeException(nameof(maxChars));
        var sb = new StringBuilder(Math.Min(maxChars, 4096));
        var chunk = new char[4096];
        var remaining = maxChars;
        int read;
        while ((read = await reader.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (remaining <= 0) continue;
            var take = Math.Min(read, remaining);
            sb.Append(chunk, 0, take);
            remaining -= take;
        }
        return sb.ToString();
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

