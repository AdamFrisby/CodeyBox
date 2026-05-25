using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
    [ModuleInitializer]
    public static void Init()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");

        SetIfMissing("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
        SetIfMissing("ASPNETCORE_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
        SetIfMissing("CodeyBox__CredentialFileWatchers", "false");
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
}

internal static class TestFileSystemWatcherLeakTracker
{
    private const int MaxReportedWatcherLeaks = 5;

    private static readonly ConcurrentDictionary<object, WatcherRecord> Active = new();
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        CredentialFileSourceWatcherDiagnostics.ConfigureForTests(CreateWatcher, MarkDisposed);
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
            ReportLeaks(Console.Error);
    }

    public static bool ReportLeaks(TextWriter writer)
    {
        var leaks = Active.Values
            .OrderBy(l => l.Path, StringComparer.Ordinal)
            .ToArray();
        if (leaks.Length == 0) return false;

        writer.WriteLine(
            $"warning: {leaks.Length} FileSystemWatcher-backed resource(s) created by tests were not disposed. " +
            "Dispose CredentialFileSource, tracked FileSystemWatcher, or tracked reload configuration in the owning test Dispose()/DisposeAsync or a using block.");
        foreach (var leak in leaks.Take(MaxReportedWatcherLeaks))
            writer.WriteLine($"warning: undisposed {leak.Kind} for {leak.Path}");
        if (leaks.Length > MaxReportedWatcherLeaks)
            writer.WriteLine($"warning: {leaks.Length - MaxReportedWatcherLeaks} additional undisposed FileSystemWatcher-backed resource(s) omitted.");
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
        Active[owner] = new WatcherRecord(path, kind);
    }

    private static void MarkDisposed(object owner)
    {
        Active.TryRemove(owner, out _);
    }

    private sealed record WatcherRecord(string Path, string Kind);

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
