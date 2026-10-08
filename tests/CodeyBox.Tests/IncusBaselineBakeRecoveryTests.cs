using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Notifications;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the Incus baseline-bake failure loop: a failed bake
/// whose candidate delete cannot be confirmed must (a) surface the bake cause
/// in the deferral, (b) re-check and clear the uncertainty without a restart,
/// (c) raise one rate-limited operator alert with backoff, and (d) report the
/// blockage in /queue/status.
/// </summary>
public sealed class IncusBaselineBakeRecoveryTests : IDisposable
{
    private const string BakeCauseMarker = "pool-space-boom";

    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-incus-bake-recovery-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task BakeFailureWithUnconfirmedDelete_SurfacesCauseThenRecheckClearsWithoutRestart()
    {
        var stagingRoot = Path.Combine(_root, "staging");
        var runner = new BakeRecoveryRunner(
            snapshotFailuresRemaining: 1,
            deleteMode: BakeRecoveryRunner.DeleteMode.FailFirst);
        var options = BaseOptions() with { StagingDirectory = stagingRoot };
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));
        provider.JitterFactor = static () => 0.5;

        var deferred = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        Assert.Equal("baseline-cleanup", deferred.Operation);
        Assert.Equal("incus-baseline-delete-unconfirmed", deferred.ErrorClass);
        Assert.Contains(BakeCauseMarker, deferred.Detail, StringComparison.Ordinal);
        Assert.Contains("exit 3", deferred.Detail, StringComparison.Ordinal);
        Assert.NotNull(deferred.InnerException);
        Assert.Contains(BakeCauseMarker, deferred.InnerException.Message, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(5), deferred.RecheckIn);
        var blocked = provider.GetProvisioningBlockedStatus();
        Assert.NotNull(blocked);
        Assert.Contains(BakeCauseMarker, blocked.LastBakeCause, StringComparison.Ordinal);
        Assert.Equal(1, blocked.ConsecutiveFailures);

        // The candidate is still present (the first delete failed), so the next
        // attempt re-checks it, deletes it again, and clears the uncertainty —
        // no restart involved. The retry bakes cleanly.
        var baseline = await provider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.Null(provider.GetProvisioningBlockedStatus());
        var listed = await provider.ListBaselineImagesAsync(CancellationToken.None);
        Assert.Contains(listed, image => image.Name == baseline);
        Assert.DoesNotContain(listed, image => image.Name.Contains("cb-bake-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PersistentBakeFailure_RaisesOneAlertBacksOffAndClearsOnSuccess()
    {
        var stagingRoot = Path.Combine(_root, "staging-persistent");
        var runner = new BakeRecoveryRunner(
            snapshotFailuresRemaining: int.MaxValue,
            deleteMode: BakeRecoveryRunner.DeleteMode.AlwaysFail);
        var options = BaseOptions() with { StagingDirectory = stagingRoot };
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));
        provider.JitterFactor = static () => 0.5;

        // Prime the alert engine while healthy so the first sweep below fires
        // exactly on the false→true edge.
        var sent = new RecordingNotificationProvider();
        var engine = new NotificationRulesEngine(
            new StubOptionsMonitor(new NotificationsOptions
            {
                Enabled = true,
                Rules =
                [
                    new NotificationRuleOptions
                    {
                        Condition = BaselineProvisioningBlockedCondition.ConditionId,
                        Providers = ["test"],
                        Cooldown = "01:00:00",
                    },
                ],
            }),
            [new BaselineProvisioningBlockedCondition(provider)],
            [new BaselineProvisioningBlockedNotificationBuilder(provider)],
            [sent],
            NullLogger<NotificationRulesEngine>.Instance);
        await engine.PrimeInitialStateAsync(CancellationToken.None);

        var first = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));
        var second = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));
        var third = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        // Deterministic failures back off exponentially instead of spinning.
        Assert.Equal(TimeSpan.FromSeconds(5), first.RecheckIn);
        Assert.Equal(TimeSpan.FromSeconds(10), second.RecheckIn);
        Assert.Equal(TimeSpan.FromSeconds(20), third.RecheckIn);
        var blocked = provider.GetProvisioningBlockedStatus();
        Assert.NotNull(blocked);
        Assert.Equal(3, blocked!.ConsecutiveFailures);
        Assert.True(blocked.BlockedSince <= blocked.LastFailureAt);
        Assert.Contains(BakeCauseMarker, blocked.LastBakeCause, StringComparison.Ordinal);

        await engine.RunSweepAsync(CancellationToken.None);
        await engine.RunSweepAsync(CancellationToken.None);
        var alert = Assert.Single(sent.Notifications);
        Assert.Equal(BaselineProvisioningBlockedCondition.ConditionId, alert.ConditionId);
        Assert.Equal(NotificationSeverity.Critical, alert.Severity);
        Assert.Contains("no usable baseline; all provisioning blocked since", alert.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(BakeCauseMarker, alert.Summary, StringComparison.Ordinal);

        // A success clears the alert without a restart.
        runner.SucceedSnapshots();
        runner.AllowDeletes();
        var baseline = await provider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.Null(provider.GetProvisioningBlockedStatus());
        Assert.False(await new BaselineProvisioningBlockedCondition(provider).EvaluateAsync(CancellationToken.None));
        engine.Dispose();
    }

    [Fact]
    public async Task MaxDelay_CapsDeterministicBackoff()
    {
        var capped = BaseOptions() with
        {
            BaselineBakeRetryBaseDelay = TimeSpan.FromMinutes(5),
            BaselineBakeRetryMaxDelay = TimeSpan.FromMinutes(7),
        };
        var provider = new IncusSandboxProvider(
            () => capped,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            new NeverRunner(),
            environmentVariableReader: EnvironmentReader(_root));
        provider.JitterFactor = static () => 1.0;

        Assert.Equal(TimeSpan.FromMinutes(6), provider.ComputeBakeRecheckDelay(capped, consecutiveFailures: 1, isTransient: false));
        Assert.Equal(TimeSpan.FromMinutes(7), provider.ComputeBakeRecheckDelay(
            capped,
            consecutiveFailures: 10,
            isTransient: false));
    }

    [Fact]
    public async Task TransientBakeFailure_KeepsShortRecheck()
    {
        var options = BaseOptions();
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            new NeverRunner(),
            environmentVariableReader: EnvironmentReader(_root));

        var transient = new IncusTransientTimeoutException("incus-agent-readiness", "agent readiness timed out");
        Assert.True(IncusSandboxProvider.IsTransientBakeFailure(transient, options));
        Assert.Equal(
            options.ProvisioningRetryRecheckIn,
            provider.ComputeBakeRecheckDelay(options, consecutiveFailures: 8, isTransient: true));

        Assert.False(IncusSandboxProvider.IsTransientBakeFailure(
            new InvalidOperationException("plain deterministic boom"), options));
    }

    [Fact]
    public async Task QueueStatus_ReportsProvisioningBlockedWhileNoBaselineUsable()
    {
        using var factory = new BlockedStatusApiFactory();
        using var client = factory.CreateClient();

        factory.Blocked.Current = new BaselineProvisioningBlockedStatus
        {
            BlockedSince = new DateTimeOffset(2026, 10, 6, 18, 42, 0, TimeSpan.Zero),
            Provider = "incus",
            BaselineName = "cb-incus-baseline-internet-headless-deadbeef",
            LastBakeCause = "boom-cause-9",
            ConsecutiveFailures = 4,
            LastFailureAt = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero),
        };
        using (var resp = await client.GetAsync("/queue/status"))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var blocked = doc.RootElement.GetProperty("provisioningBlocked");
            Assert.Equal(JsonValueKind.Object, blocked.ValueKind);
            Assert.Contains("boom-cause-9", blocked.GetProperty("lastBakeCause").GetString(), StringComparison.Ordinal);
            Assert.Contains("no usable baseline", blocked.GetProperty("reason").GetString(), StringComparison.Ordinal);
            Assert.Equal(4, blocked.GetProperty("consecutiveFailures").GetInt32());
        }

        factory.Blocked.Current = null;
        using (var resp = await client.GetAsync("/queue/status"))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("provisioningBlocked").ValueKind);
        }
    }

    private IncusSandboxOptions BaseOptions() => new()
    {
        ProjectName = "codeybox-tests",
        StoragePoolName = "codeybox-zfs",
        DefaultImage = "images:ubuntu/24.04/cloud",
        UseBaselineImages = true,
        DiskGuard = null,
        BootLaunchDelay = TimeSpan.Zero,
        BaselineBakeRetryBaseDelay = TimeSpan.FromSeconds(5),
        BaselineBakeRetryMaxDelay = TimeSpan.FromHours(1),
        NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["internet-only"] = "cb-net",
        },
    };

    private static Func<string, string?> EnvironmentReader(string home) =>
        name => string.Equals(name, "HOME", StringComparison.Ordinal) ? home : null;

    private sealed class NeverRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true,
            string? workingDirectory = null) =>
            throw new InvalidOperationException("Incus must not run.");
    }

    private sealed class RecordingNotificationProvider : INotificationProvider
    {
        private readonly List<Notification> _notifications = new();
        private readonly object _lock = new();

        public string Name => "test";

        public IReadOnlyList<Notification> Notifications
        {
            get { lock (_lock) return _notifications.ToArray(); }
        }

        public Task SendAsync(Notification notification, CancellationToken ct)
        {
            lock (_lock) _notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class StubOptionsMonitor(NotificationsOptions current) : IOptionsMonitor<NotificationsOptions>
    {
        public NotificationsOptions CurrentValue { get; set; } = current;

        public NotificationsOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<NotificationsOptions, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose() { }
        }
    }

    private sealed class BlockingStatusStub : IBaselineProvisioningBlockedStatusProvider
    {
        public BaselineProvisioningBlockedStatus? Current { get; set; }

        public BaselineProvisioningBlockedStatus? GetProvisioningBlockedStatus() => Current;
    }

    private sealed class BlockedStatusApiFactory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-blockedstatus-");
        private readonly SqliteWorkItemStore _store;
        private readonly SqliteQueueController _queue;
        private string _dbPath => _scratch.DbPath("blockedstatus.db");

        public BlockingStatusStub Blocked { get; } = new();

        public BlockedStatusApiFactory()
        {
            _store = new SqliteWorkItemStore(_dbPath);
            _queue = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitHubAppStorePath"] = Path.Combine(_scratch.DirectoryPath, "github-apps"),
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(_scratch.DirectoryPath, "test-git"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(_scratch.DirectoryPath, "test-log.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_scratch.DirectoryPath, "test-audit.json"),
                    ["CodeyBox:Smoke:Enabled"] = "false",
                    ["CodeyBox:AgentClasses:0:Id"] = "frontier",
                    ["CodeyBox:AgentClasses:0:DisplayName"] = "Frontier",
                    ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
                    ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
                    ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "100",
                });
            });
            var store = _store;
            var queue = _queue;
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IWorkItemStore>();
                services.AddSingleton<IWorkItemStore>(store);
                services.RemoveAll<IQueueController>();
                services.AddSingleton<IQueueController>(queue);
                services.AddSingleton<IBaselineProvisioningBlockedStatusProvider>(Blocked);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _queue.Dispose();
                _store.Dispose();
                try { File.Delete(_dbPath); } catch { /* best-effort */ }
                TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
                _scratch.Dispose();
            }
        }
    }

    /// <summary>
    /// Minimal fake incus CLI that walks the real bake path (init → NIC →
    /// cloud-init → start → provisioning no-op → snapshot → publish) with two
    /// fault injectors: the immutable-snapshot create can fail N times with a
    /// distinctive cause, and candidate deletes can fail once or always.
    /// </summary>
    private sealed class BakeRecoveryRunner : IProcessRunner
    {
        internal enum DeleteMode
        {
            Succeed,
            FailFirst,
            AlwaysFail,
        }

        private int _snapshotFailuresRemaining;
        private DeleteMode _deleteMode;
        private string? _instanceName;
        private string _instanceStatus = "STOPPED";
        private Dictionary<string, string> _instanceConfig = new(StringComparer.Ordinal);
        private Dictionary<string, string> _projectConfig = new(StringComparer.Ordinal);
        private bool _projectExists;
        private bool _published;

        internal BakeRecoveryRunner(int snapshotFailuresRemaining, DeleteMode deleteMode)
        {
            _snapshotFailuresRemaining = snapshotFailuresRemaining;
            _deleteMode = deleteMode;
        }

        internal void SucceedSnapshots() => _snapshotFailuresRemaining = 0;

        internal void AllowDeletes() => _deleteMode = DeleteMode.Succeed;

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true,
            string? workingDirectory = null)
        {
            ct.ThrowIfCancellationRequested();
            if (argv.SequenceEqual(["incus", "query", "/1.0"]))
            {
                return Success("{\"metadata\":{\"api_extensions\":[\"disk_io_bus_cache_filesystem\",\"projects_restrictions\"],\"environment\":{\"kernel_version\":\"6.14.0-test\"}}}");
            }
            if (argv.SequenceEqual(["incus", "project", "list", "--format=json"]))
                return Success(_projectExists ? "[{\"name\":\"codeybox-tests\"}]" : "[]");
            if (argv.Count >= 4 && argv.Take(3).SequenceEqual(["incus", "project", "create"]))
            {
                _projectExists = true;
                _projectConfig = ParseConfigArguments(argv);
                return Success();
            }
            if (argv.SequenceEqual(["incus", "query", "/1.0/projects/codeybox-tests"]))
            {
                if (!_projectExists)
                    throw new InvalidOperationException("Project query preceded project creation.");
                var pairs = _projectConfig.Select(pair =>
                    "\"" + pair.Key + "\":\"" + pair.Value + "\"");
                return Success("{\"metadata\":{\"name\":\"codeybox-tests\",\"config\":{" + string.Join(",", pairs) + "}}}");
            }
            if (argv.Contains("storage", StringComparer.Ordinal) && argv.Contains("list", StringComparer.Ordinal))
                return Success("[{\"name\":\"codeybox-zfs\",\"driver\":\"zfs\",\"config\":{}}]");
            if (argv.Contains("snapshot", StringComparer.Ordinal)
                && argv.Contains("list", StringComparer.Ordinal))
                return Success(_published ? "[{\"name\":\"ready\"}]" : "[]");
            if (argv.Contains("list", StringComparer.Ordinal) && argv.Contains("--format=json", StringComparer.Ordinal))
            {
                if (_instanceName is null)
                    return Success("[]");
                return Success(
                    "[{\"name\":\"" + _instanceName + "\",\"type\":\"virtual-machine\",\"status\":\""
                    + _instanceStatus + "\",\"config\":" + ConfigJson() + "}]");
            }
            var initIndex = IndexOf(argv, "init");
            if (initIndex >= 0)
            {
                _instanceName = argv[initIndex + 2];
                _instanceStatus = "STOPPED";
                _instanceConfig = ParseConfigArguments(argv);
                _published = false;
                return Success();
            }
            if (argv.Contains("query", StringComparer.Ordinal)
                && argv.Any(argument => argument.StartsWith("/1.0/instances/", StringComparison.Ordinal)))
            {
                return Success(
                    "{\"metadata\":{\"type\":\"virtual-machine\",\"profiles\":[]," +
                    "\"config\":{},\"expanded_config\":{}," +
                    "\"expanded_devices\":{\"root\":{\"type\":\"disk\",\"path\":\"/\",\"pool\":\"codeybox-zfs\"}," +
                    "\"codeybox-net\":{\"type\":\"nic\",\"nictype\":\"bridged\",\"parent\":\"cb-net\",\"name\":\"eth0\"}}}}");
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                _instanceStatus = "RUNNING";
                return Success();
            }
            if (argv.Contains("stop", StringComparer.Ordinal))
            {
                _instanceStatus = "STOPPED";
                return Success();
            }
            if (argv.Contains("exec", StringComparer.Ordinal))
            {
                if (argv.Contains("cloud-init", StringComparer.Ordinal)
                    && argv.Contains("status", StringComparer.Ordinal))
                {
                    return Success("{\"status\":\"done\",\"extended_status\":\"done\",\"errors\":[]}");
                }
                return Success();
            }
            if (argv.Contains("config", StringComparer.Ordinal)
                && argv.Contains("device", StringComparer.Ordinal)
                && argv.Contains("get", StringComparer.Ordinal)
                && string.Equals(argv[^1], "pool", StringComparison.Ordinal))
            {
                return Success("codeybox-zfs\n");
            }
            if (argv.Contains("config", StringComparer.Ordinal)
                && argv.Contains("get", StringComparer.Ordinal))
            {
                var key = argv[^1];
                return Success(_instanceConfig.GetValueOrDefault(key, string.Empty));
            }
            if (argv.Contains("config", StringComparer.Ordinal)
                && argv.Contains("unset", StringComparer.Ordinal))
            {
                _instanceConfig.Remove(argv[^1]);
                return Success();
            }
            var configIndex = IndexOf(argv, "config");
            if (configIndex >= 0
                && configIndex + 3 < argv.Count
                && string.Equals(argv[configIndex + 1], "set", StringComparison.Ordinal))
            {
                var field = argv[configIndex + 3];
                var separator = field.IndexOf('=');
                if (separator > 0)
                    _instanceConfig[field[..separator]] = field[(separator + 1)..];
                return Success();
            }
            if (argv.Contains("config", StringComparer.Ordinal))
                return Success();
            if (argv.Contains("snapshot", StringComparer.Ordinal)
                && argv.Contains("create", StringComparer.Ordinal))
            {
                if (_snapshotFailuresRemaining > 0)
                {
                    if (_snapshotFailuresRemaining != int.MaxValue)
                        _snapshotFailuresRemaining--;
                    return Task.FromResult(new ProcessRunResult(
                        3,
                        string.Empty,
                        "Error: snapshot target pool out of space (" + BakeCauseMarker + ")"));
                }
                return Success();
            }
            if (argv.Contains("move", StringComparer.Ordinal))
            {
                var moveIndex = IndexOf(argv, "move");
                _instanceName = argv[moveIndex + 2];
                _published = true;
                return Success();
            }
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                if (_deleteMode is DeleteMode.AlwaysFail
                    || (_deleteMode is DeleteMode.FailFirst && !_deleteFailedOnce))
                {
                    _deleteFailedOnce = true;
                    return Task.FromResult(new ProcessRunResult(
                        9,
                        string.Empty,
                        "Error: candidate delete denied"));
                }
                _instanceName = null;
                _instanceConfig.Clear();
                _published = false;
                return Success();
            }
            throw new InvalidOperationException($"Unexpected Incus bake recovery command: {string.Join(' ', argv)}");
        }

        private bool _deleteFailedOnce;

        private string ConfigJson()
        {
            var parts = _instanceConfig.Select(pair =>
                "\"" + pair.Key + "\":\"" + pair.Value + "\"");
            return "{" + string.Join(",", parts) + "}";
        }

        private static Dictionary<string, string> ParseConfigArguments(IReadOnlyList<string> argv)
        {
            var config = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < argv.Count; i++)
            {
                if (!string.Equals(argv[i], "--config", StringComparison.Ordinal))
                    continue;
                var field = argv[++i];
                var separator = field.IndexOf('=');
                if (separator > 0)
                    config[field[..separator]] = field[(separator + 1)..];
            }
            return config;
        }

        private static Task<ProcessRunResult> Success(string stdout = "") =>
            Task.FromResult(new ProcessRunResult(0, stdout, string.Empty));

        private static int IndexOf(IReadOnlyList<string> values, string expected)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], expected, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }
    }
}
