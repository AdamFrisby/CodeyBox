using CodeyBox.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Unit coverage for <see cref="MultipassDiskGuardConfig.Build"/> — the
/// pure-function translator that turns operator-facing
/// <c>CodeyBox:DiskGuard</c> options into the
/// <c>MultipassDiskGuardOptions</c> the multipass provider consumes.
///
/// These branches were previously inline in <c>Program.cs</c> with no
/// coverage; a typo in the threshold-comparison sign, a missed parse
/// validation, or skipping the state-database-directory auto-include
/// would have shipped silently.
/// </summary>
public sealed class MultipassDiskGuardConfigTests
{
    [Fact]
    public void Build_ReturnsNull_WhenDiskGuardSectionDisabled()
    {
        var opts = new CodeyBoxOptions
        {
            StateDatabasePath = "/tmp/cb-state.db",
            DiskGuard = new DiskGuardOptions { Enabled = false },
        };

        var result = MultipassDiskGuardConfig.Build(opts, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void Build_ReturnsNullAndWarns_WhenMinFreeBytesIsZero()
    {
        var opts = new CodeyBoxOptions
        {
            StateDatabasePath = "/tmp/cb-state.db",
            DiskGuard = new DiskGuardOptions { Enabled = true, MinFreeBytes = 0 },
        };
        var capture = new RecordingLogger();

        var result = MultipassDiskGuardConfig.Build(opts, capture);

        Assert.Null(result);
        var entry = Assert.Single(capture.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("non-positive", entry.Message);
    }

    [Fact]
    public void Build_ReturnsNullAndWarns_WhenMinFreeBytesIsNegative()
    {
        var opts = new CodeyBoxOptions
        {
            StateDatabasePath = "/tmp/cb-state.db",
            DiskGuard = new DiskGuardOptions { Enabled = true, MinFreeBytes = -1 },
        };
        var capture = new RecordingLogger();

        var result = MultipassDiskGuardConfig.Build(opts, capture);

        Assert.Null(result);
        Assert.Single(capture.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Build_ThrowsInvalidOperationException_OnUnparseableRecheckIn()
    {
        var opts = new CodeyBoxOptions
        {
            StateDatabasePath = "/tmp/cb-state.db",
            DiskGuard = new DiskGuardOptions
            {
                Enabled = true,
                MinFreeBytes = 1024,
                RecheckIn = "not-a-timespan",
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => MultipassDiskGuardConfig.Build(opts, NullLogger.Instance));
        Assert.Contains("RecheckIn", ex.Message);
        Assert.Contains("not-a-timespan", ex.Message);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    public void Build_ThrowsInvalidOperationException_OnNonPositiveRecheckIn(string recheckIn)
    {
        var opts = new CodeyBoxOptions
        {
            StateDatabasePath = "/tmp/cb-state.db",
            DiskGuard = new DiskGuardOptions
            {
                Enabled = true,
                MinFreeBytes = 1024,
                RecheckIn = recheckIn,
            },
        };

        Assert.Throws<InvalidOperationException>(
            () => MultipassDiskGuardConfig.Build(opts, NullLogger.Instance));
    }

    [Fact]
    public void Build_FallsBackToFiveMinuteDefault_WhenRecheckInIsEmpty()
    {
        var opts = new CodeyBoxOptions
        {
            StateDatabasePath = "/tmp/cb-state.db",
            DiskGuard = new DiskGuardOptions
            {
                Enabled = true,
                MinFreeBytes = 1024,
                RecheckIn = string.Empty,
            },
        };

        var result = MultipassDiskGuardConfig.Build(opts, NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromMinutes(5), result!.RecheckIn);
    }

    [Fact]
    public void Build_RejectsOversizedSharedDiskGuardTextBeforeParsingOrPathScanning()
    {
        var options = new CodeyBoxOptions
        {
            StateDatabasePath = "/tmp/cb-state.db",
            DiskGuard = new DiskGuardOptions
            {
                Enabled = true,
                MinFreeBytes = 1024,
                RecheckIn = new string('1', 65),
            },
        };

        var recheckFailure = Assert.Throws<InvalidOperationException>(() =>
            MultipassDiskGuardConfig.Build(options, NullLogger.Instance));
        Assert.Contains("RecheckIn", recheckFailure.Message, StringComparison.Ordinal);

        options.DiskGuard.RecheckIn = "00:05:00";
        options.DiskGuard.AdditionalPaths = [new string('p', 4097)];
        var pathFailure = Assert.Throws<InvalidOperationException>(() =>
            MultipassDiskGuardConfig.Build(options, NullLogger.Instance));
        Assert.Contains("AdditionalPaths entry", pathFailure.Message, StringComparison.Ordinal);

        options.DiskGuard.AdditionalPaths = Enumerable.Range(0, 65)
            .Select(index => $"/srv/path-{index}")
            .ToList();
        var countFailure = Assert.Throws<InvalidOperationException>(() =>
            MultipassDiskGuardConfig.Build(options, NullLogger.Instance));
        Assert.Contains("more than 64", countFailure.Message, StringComparison.Ordinal);

        options.DiskGuard.AdditionalPaths = [];
        options.StateDatabasePath = new string('s', 4097);
        var stateFailure = Assert.Throws<InvalidOperationException>(() =>
            MultipassDiskGuardConfig.Build(options, NullLogger.Instance));
        Assert.Contains("StateDatabasePath", stateFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AutoIncludesStateDatabaseDirectory_InAdditionalPaths()
    {
        var opts = new CodeyBoxOptions
        {
            StateDatabasePath = "/var/lib/codeybox/state.db",
            DiskGuard = new DiskGuardOptions
            {
                Enabled = true,
                MinFreeBytes = 1024,
                AdditionalPaths = ["/srv/extra"],
            },
        };

        var result = MultipassDiskGuardConfig.Build(opts, NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Contains("/var/lib/codeybox", result!.AdditionalPaths);
        Assert.Contains("/srv/extra", result.AdditionalPaths);
    }

    [Fact]
    public void Build_DoesNotDuplicate_WhenStateDatabaseDirectoryAlreadyListed()
    {
        var opts = new CodeyBoxOptions
        {
            StateDatabasePath = "/var/lib/codeybox/state.db",
            DiskGuard = new DiskGuardOptions
            {
                Enabled = true,
                MinFreeBytes = 1024,
                AdditionalPaths = ["/var/lib/codeybox"],
            },
        };

        var result = MultipassDiskGuardConfig.Build(opts, NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Single(result!.AdditionalPaths, "/var/lib/codeybox");
    }

    [Fact]
    public void Build_PropagatesMinFreeBytesAndDataPath()
    {
        var opts = new CodeyBoxOptions
        {
            StateDatabasePath = "/tmp/cb-state.db",
            DiskGuard = new DiskGuardOptions
            {
                Enabled = true,
                MinFreeBytes = 42L * 1024 * 1024 * 1024,
                MultipassDataPath = "/custom/mp/data",
                RecheckIn = "00:10:00",
            },
        };

        var result = MultipassDiskGuardConfig.Build(opts, NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Equal(42L * 1024 * 1024 * 1024, result!.MinFreeBytes);
        Assert.Equal("/custom/mp/data", result.MultipassDataPath);
        Assert.Equal(TimeSpan.FromMinutes(10), result.RecheckIn);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
