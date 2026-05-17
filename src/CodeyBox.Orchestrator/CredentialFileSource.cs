using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Singleton owner of a single host-side OAuth credential file (Claude's
/// <c>.credentials.json</c>, Codex's <c>auth.json</c>, Gemini's
/// <c>oauth_creds.json</c>/<c>settings.json</c>). Reads the file once at
/// startup, watches the file for changes via <see cref="FileSystemWatcher"/>,
/// re-parses on change, and raises <see cref="TokenUpdated"/> so quota probes
/// can invalidate their per-token caches and credential providers can hand
/// the fresh JSON to every new sandbox.
///
/// <para>Closes the loop on out-of-band refreshes: a child sandbox or an
/// operator running the agent CLI on the host can rewrite the file at any
/// time; without this class the host would keep injecting the pre-rotation
/// token until a CodeyBox restart.</para>
///
/// <para>Reads use <c>FileShare.ReadWrite | FileShare.Delete</c> to avoid
/// blocking writers, with a short retry loop on transient
/// <see cref="IOException"/> or torn writes (partial JSON observed while the
/// CLI is rewriting the file).</para>
///
/// <para>GetRaw() also stat-checks the file on each call as a backstop for
/// platforms where the watcher is slow to fire — the first caller after a
/// write observes the change synchronously and the event fires before the
/// raw bytes are returned.</para>
/// </summary>
public class CredentialFileSource : IDisposable
{
    private const int MaxReadAttempts = 4; // 1 initial + 3 retries
    private const int RetryDelayMs = 100;

    private readonly ILogger? _log;
    private readonly object _gate = new();
    private string? _cached;
    private DateTime _cachedMtimeUtc = DateTime.MinValue;
    private long _cachedLength = -1;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>Path to the credential file on the host filesystem.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Raised after the file is observed to change and the new contents are
    /// cached. Subscribers should be cheap and non-throwing — this fires on
    /// the watcher thread (or whichever thread invoked GetRaw() and detected
    /// a stale cache).
    /// </summary>
    public event Action? TokenUpdated;

    public CredentialFileSource(string filePath, ILogger? log = null)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _log = log;
        TryReload(out _);
        StartWatcher();
    }

    /// <summary>
    /// Returns the current cached file contents, or <c>null</c> if the file
    /// is absent or unreadable. Performs a cheap stat-based freshness check
    /// and reloads inline if the on-disk file is newer than the cache. Safe
    /// to call concurrently from multiple threads.
    /// </summary>
    public string? GetRaw()
    {
        if (_disposed) return null;
        var changed = TryReload(out var current);
        if (changed) RaiseTokenUpdated();
        return current;
    }

    internal void Reload()
    {
        var changed = TryReload(out _);
        if (changed) RaiseTokenUpdated();
    }

    private bool TryReload(out string? current)
    {
        DateTime mtime;
        long length;
        try
        {
            if (!File.Exists(FilePath))
            {
                lock (_gate) current = _cached;
                return false;
            }
            var info = new FileInfo(FilePath);
            mtime = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (IOException)
        {
            lock (_gate) current = _cached;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            lock (_gate) current = _cached;
            return false;
        }

        lock (_gate)
        {
            if (mtime == _cachedMtimeUtc && length == _cachedLength && _cached is not null)
            {
                current = _cached;
                return false;
            }

            var next = ReadWithRetry();
            if (next is null)
            {
                current = _cached;
                return false;
            }

            // Refresh mtime/length after the successful read so a torn-write
            // retry doesn't pin the cache to the partial state's timestamp.
            try
            {
                var info = new FileInfo(FilePath);
                _cachedMtimeUtc = info.LastWriteTimeUtc;
                _cachedLength = info.Length;
            }
            catch (IOException) { }

            var contentChanged = !string.Equals(_cached, next, StringComparison.Ordinal);
            _cached = next;
            current = _cached;
            return contentChanged;
        }
    }

    private string? ReadWithRetry()
    {
        for (int attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(
                    FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                var contents = reader.ReadToEnd();

                if (string.IsNullOrWhiteSpace(contents))
                {
                    if (attempt + 1 < MaxReadAttempts)
                    {
                        Thread.Sleep(RetryDelayMs);
                        continue;
                    }
                    return null;
                }

                // Validate JSON — protects against TOCTOU where the writer is
                // partway through dumping a fresh token. Retry once or twice;
                // if still bad, leave the existing cache untouched.
                try
                {
                    using var doc = JsonDocument.Parse(contents);
                }
                catch (JsonException)
                {
                    if (attempt + 1 < MaxReadAttempts)
                    {
                        Thread.Sleep(RetryDelayMs);
                        continue;
                    }
                    _log?.LogWarning(
                        "Credential file {Path} did not parse as JSON after {Attempts} attempts; keeping previous snapshot",
                        FilePath, MaxReadAttempts);
                    return null;
                }

                return contents;
            }
            catch (IOException) when (attempt + 1 < MaxReadAttempts)
            {
                Thread.Sleep(RetryDelayMs);
                continue;
            }
            catch (IOException ex)
            {
                _log?.LogWarning(ex, "Failed to read credential file {Path}; keeping previous snapshot", FilePath);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log?.LogWarning(ex, "Credential file {Path} is not readable; keeping previous snapshot", FilePath);
                return null;
            }
        }
        return null;
    }

    private void StartWatcher()
    {
        var dir = Path.GetDirectoryName(FilePath);
        var fileName = Path.GetFileName(FilePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName))
            return;
        if (!Directory.Exists(dir))
        {
            _log?.LogDebug(
                "Credential file directory {Dir} does not exist; deferring file watching until GetRaw is called",
                dir);
            return;
        }

        try
        {
            var w = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.FileName
                    | NotifyFilters.CreationTime
                    | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            w.Changed += OnFsEvent;
            w.Created += OnFsEvent;
            w.Renamed += OnFsRenamed;
            _watcher = w;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to register FileSystemWatcher for {Path}; relying on stat-based reload", FilePath);
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Reload();
    private void OnFsRenamed(object sender, RenamedEventArgs e) => Reload();

    private void RaiseTokenUpdated()
    {
        var handlers = TokenUpdated;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action)h)(); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "TokenUpdated subscriber threw for {Path}", FilePath);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var w = _watcher;
        _watcher = null;
        if (w is not null)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Changed -= OnFsEvent;
                w.Created -= OnFsEvent;
                w.Renamed -= OnFsRenamed;
                w.Dispose();
            }
            catch { }
        }
    }
}

/// <summary>Marker for the Claude OAuth credentials file source.</summary>
public sealed class ClaudeCredentialFileSource : CredentialFileSource
{
    public ClaudeCredentialFileSource(string filePath, ILogger<CredentialFileSource>? log = null)
        : base(filePath, log) { }
}

/// <summary>Marker for the Codex OAuth credentials file source.</summary>
public sealed class CodexCredentialFileSource : CredentialFileSource
{
    public CodexCredentialFileSource(string filePath, ILogger<CredentialFileSource>? log = null)
        : base(filePath, log) { }
}

/// <summary>Marker for the Gemini OAuth credentials file source.</summary>
public sealed class GeminiOAuthCredentialFileSource : CredentialFileSource
{
    public GeminiOAuthCredentialFileSource(string filePath, ILogger<CredentialFileSource>? log = null)
        : base(filePath, log) { }
}

/// <summary>Marker for the Gemini settings file source (not a credential, but
/// rotates alongside the OAuth file, and the CLI requires both).</summary>
public sealed class GeminiSettingsCredentialFileSource : CredentialFileSource
{
    public GeminiSettingsCredentialFileSource(string filePath, ILogger<CredentialFileSource>? log = null)
        : base(filePath, log) { }
}
