using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

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
        SetIfMissing("CODEYBOX_CREDENTIAL_FILE_WATCHERS", "false");
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
    private static readonly ConcurrentDictionary<FileSystemWatcher, WatcherRecord> Active = new();
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
            $"warning: {leaks.Length} FileSystemWatcher instance(s) created by tests were not disposed. " +
            "Dispose CredentialFileSource in the owning test Dispose()/DisposeAsync or a using block.");
        foreach (var leak in leaks.Take(5))
            writer.WriteLine($"warning: undisposed FileSystemWatcher for {leak.Path}");
        if (leaks.Length > 5)
            writer.WriteLine($"warning: {leaks.Length - 5} additional undisposed FileSystemWatcher instance(s) omitted.");
        return true;
    }

    public static bool IsTrackingPath(string path)
        => Active.Values.Any(l => string.Equals(l.Path, path, StringComparison.Ordinal));

    private static FileSystemWatcher CreateWatcher(string dir, string fileName)
    {
        var watcher = new FileSystemWatcher(dir, fileName);
        Active[watcher] = new WatcherRecord(Path.Combine(dir, fileName));
        return watcher;
    }

    private static void MarkDisposed(FileSystemWatcher watcher)
    {
        Active.TryRemove(watcher, out _);
    }

    private sealed record WatcherRecord(string Path);
}
