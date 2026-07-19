using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CodeyBox.Api;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Runs once when the test assembly loads, before any test executes.
/// Pre-sets <c>ASPNETCORE_URLS</c> to <c>http://127.0.0.1:0</c> so the
/// production default in <c>src/CodeyBox.Api/Program.cs</c> — which pins
/// <c>http://127.0.0.1:5000</c> when no URL config is present — is skipped
/// under <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
/// WebApplicationFactory swaps the IServer for an in-memory TestServer, so
/// the URL is normally inert; port 0 (auto-assign) guarantees safety even
/// if a code path ever lets Kestrel bind, since parallel xunit tests would
/// otherwise race on the fixed 5000.
/// </summary>
internal static class TestAssemblyInitializer
{
    private const long RecommendedInotifyMaxUserWatches = 524_288;
    private const long RecommendedInotifyMaxUserInstances = 1_024;

    // Full-suite runs create a GUID SQLite DB (+ -wal/-shm) and git/log/agent-stream
    // temp trees per test under Path.GetTempPath(). On a small or RAM-backed /tmp the
    // filesystem fills and SQLite can no longer create tables, producing cascading
    // "no such table: work_items" failures across parallel WebApplicationFactory tests.
    // Warn (never fail) when the temp drive lacks headroom so the operator can point
    // TMPDIR at a spacious disk before running the suite.
    internal const long RecommendedTempFreeBytes = 2L * 1024 * 1024 * 1024;

    [ModuleInitializer]
    public static void Init()
    {
        // Re-home all temp usage into a single per-run root before anything else
        // resolves Path.GetTempPath(), so the per-test-case scratch dirs created
        // by LeakTrackingTestFramework — and the process-exit backstop below —
        // can bound and reclaim temp-disk usage across the run.
        TestTempWorkspace.Initialize(Path.GetTempPath());
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => TestTempWorkspace.WipeRunRoot();

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");

        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        if (workerThreads < 128)
            ThreadPool.SetMinThreads(128, completionPortThreads);

        SetIfMissing("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
        SetIfMissing("ASPNETCORE_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
        SetIfMissing("DOTNET_USE_POLLING_FILE_WATCHER", "1");
        SetIfMissing(CredentialFileWatcherSettings.EnvironmentVariable, "false");

        if (!UsePollingFileWatcher())
            WarnOnLowInotifyLimits(Console.Error);

        WarnOnLowTempDiskHeadroom(Console.Error);

        TestFileSystemWatcherLeakTracker.Install();

        // Fail fast in CI on schema drift: every WebhookEvent that goes
        // through the broadcaster is validated for the three required
        // envelope fields. Production code path keeps this off.
        WebhookEventBroadcaster.StrictSchemaValidationForTests = true;
    }

    private static void SetIfMissing(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    private static bool UsePollingFileWatcher()
        => string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    internal static void WarnOnLowInotifyLimits(TextWriter writer)
    {
        if (!OperatingSystem.IsLinux())
            return;

        WarnIfLow(
            "/proc/sys/fs/inotify/max_user_watches",
            "fs.inotify.max_user_watches",
            RecommendedInotifyMaxUserWatches,
            "sudo sysctl fs.inotify.max_user_watches=524288",
            writer);
        WarnIfLow(
            "/proc/sys/fs/inotify/max_user_instances",
            "fs.inotify.max_user_instances",
            RecommendedInotifyMaxUserInstances,
            "sudo sysctl fs.inotify.max_user_instances=1024",
            writer);
    }

    private static void WarnIfLow(
        string path,
        string sysctlName,
        long recommended,
        string command,
        TextWriter writer)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var raw = File.ReadAllText(path).Trim();
            if (!long.TryParse(raw, out var current) || current >= recommended)
                return;

            writer.WriteLine(
                $"warning: {sysctlName} is {current}; CodeyBox full-suite tests can exhaust file watchers at low Linux defaults. " +
                $"Run `{command}` before `dotnet test CodeyBox.slnx`.");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static void WarnOnLowTempDiskHeadroom(TextWriter writer)
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var root = Path.GetPathRoot(Path.GetFullPath(tempPath));
            if (string.IsNullOrEmpty(root))
                return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return;

            var message = DescribeLowTempDiskHeadroom(
                tempPath,
                drive.AvailableFreeSpace,
                RecommendedTempFreeBytes);
            if (message is not null)
                writer.WriteLine(message);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>
    /// Pure headroom decision: returns a warning message when the temp filesystem's
    /// <paramref name="availableFreeBytes"/> is below <paramref name="recommendedFreeBytes"/>,
    /// otherwise <c>null</c>. Sizes are in bytes; the message reports MiB.
    /// </summary>
    internal static string? DescribeLowTempDiskHeadroom(
        string tempPath,
        long availableFreeBytes,
        long recommendedFreeBytes)
    {
        if (availableFreeBytes >= recommendedFreeBytes)
            return null;

        const long bytesPerMiB = 1024 * 1024;
        return $"warning: temp filesystem for '{tempPath}' has {availableFreeBytes / bytesPerMiB} MiB free " +
            $"(recommended >= {recommendedFreeBytes / bytesPerMiB} MiB). CodeyBox full-suite tests write a per-test " +
            "SQLite database and git/log/agent-stream temp trees; a small or RAM-backed temp directory can fill and " +
            "yield cascading 'no such table' failures. Point TMPDIR at a spacious disk before `dotnet test CodeyBox.slnx`.";
    }
}

internal static class TestFileSystemWatcherLeakTracker
{
    internal const string DisableProcessExitReportEnvironmentVariable = "CODEYBOX_TESTS_DISABLE_WATCHER_LEAK_PROCESS_EXIT_REPORT";

    private const int MaxReportedWatcherLeaks = 5;

    private static readonly AsyncLocal<TestCaseScope?> CurrentScope = new();
    private static readonly ConcurrentDictionary<object, WatcherRecord> Active = new();
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        CredentialFileSourceWatcherDiagnostics.ConfigureForTests(CreateWatcher, MarkDisposed);
        if (!string.Equals(Environment.GetEnvironmentVariable(DisableProcessExitReportEnvironmentVariable), "1", StringComparison.Ordinal))
            AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
                ReportLeaks(Console.Error);
    }

    public static TestCaseScope BeginTestCase(string displayName)
    {
        var previous = CurrentScope.Value;
        var scope = new TestCaseScope(Guid.NewGuid().ToString("N"), displayName, previous);
        CurrentScope.Value = scope;
        return scope;
    }

    public static bool ReportLeaks(TextWriter writer)
        => ReportLeaks(writer, scopeId: null);

    private static bool ReportLeaks(TextWriter writer, string? scopeId)
    {
        var leaks = Active.Values
            .Where(l => scopeId is null || l.TestScopeId == scopeId)
            .OrderBy(l => l.Path, StringComparer.Ordinal)
            .ToArray();
        if (leaks.Length == 0) return false;

        writer.WriteLine(
            $"warning: {leaks.Length} FileSystemWatcher-backed resource(s) created by tests were not disposed. " +
            "Dispose CredentialFileSource, tracked FileSystemWatcher, or tracked reload configuration in the owning test Dispose()/DisposeAsync or a using block.");
        foreach (var leak in leaks.Take(MaxReportedWatcherLeaks))
            writer.WriteLine($"warning: undisposed {leak.Kind} for {leak.Path} (created by {leak.TestDisplayName})");
        if (leaks.Length > MaxReportedWatcherLeaks)
            writer.WriteLine($"warning: {leaks.Length - MaxReportedWatcherLeaks} additional undisposed FileSystemWatcher-backed resource(s) omitted.");
        return true;
    }

    private static bool ReportLeaks(Action<string> writeLine, string? scopeId)
    {
        using var writer = new StringWriter();
        if (!ReportLeaks(writer, scopeId)) return false;
        foreach (var line in writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            writeLine(line);
        return true;
    }

    public static bool IsTrackingPath(string path)
        => Active.Values.Any(l => string.Equals(l.Path, path, StringComparison.Ordinal));

    public static FileSystemWatcher CreateWatcher(string dir, string fileName)
    {
        var watcher = new TrackedFileSystemWatcher(dir, fileName);
        Track(watcher, Path.Combine(dir, fileName), "FileSystemWatcher");
        return watcher;
    }

    public static TrackedConfigurationRoot TrackReloadingConfiguration(IConfigurationRoot configuration, string path)
    {
        var tracked = new TrackedConfigurationRoot(configuration, path);
        Track(tracked, path, "reloadOnChange configuration");
        return tracked;
    }

    public static void MarkDisposed(FileSystemWatcher watcher)
    {
        MarkDisposed((object)watcher);
    }

    private static void Track(object owner, string path, string kind)
    {
        var scope = CurrentScope.Value;
        Active[owner] = new WatcherRecord(
            path,
            kind,
            scope?.Id,
            scope?.DisplayName ?? "unknown test");
    }

    private static void MarkDisposed(object owner)
    {
        Active.TryRemove(owner, out _);
    }

    private sealed record WatcherRecord(
        string Path,
        string Kind,
        string? TestScopeId,
        string TestDisplayName);

    public sealed class TestCaseScope : IDisposable
    {
        private readonly TestCaseScope? _previous;
        private bool _disposed;

        internal TestCaseScope(string id, string displayName, TestCaseScope? previous)
        {
            Id = id;
            DisplayName = displayName;
            _previous = previous;
        }

        internal string Id { get; }
        internal string DisplayName { get; }

        public bool ReportLeaks(TextWriter writer)
            => TestFileSystemWatcherLeakTracker.ReportLeaks(writer, Id);

        public bool ReportLeaks(Action<string> writeLine)
            => TestFileSystemWatcherLeakTracker.ReportLeaks(writeLine, Id);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentScope.Value = _previous;
        }
    }

    public sealed class TrackedConfigurationRoot(IConfigurationRoot configuration, string path) : IDisposable
    {
        private bool _disposed;

        public IConfigurationRoot Configuration => configuration;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            (configuration as IDisposable)?.Dispose();
            MarkDisposed(this);
        }

        public override string ToString() => path;
    }

    private sealed class TrackedFileSystemWatcher : FileSystemWatcher
    {
        public TrackedFileSystemWatcher(string path, string filter)
            : base(path, filter)
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                MarkDisposed(this);
        }
    }
}
