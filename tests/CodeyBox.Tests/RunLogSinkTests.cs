using System.Globalization;
using System.Text.RegularExpressions;
using CodeyBox.Api;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the bounded run log (<see cref="RunLogSink"/>): rotation
/// triggers at the configured size, the retained-file count keeps the total
/// footprint bounded under sustained high-rate writes without losing or
/// duplicating lines, both knobs hot-reload on a live instance, and every
/// emitted line carries a parseable full-date UTC timestamp.
/// </summary>
public sealed class RunLogSinkTests : IDisposable
{
    private static readonly Regex LinePattern = new(
        @"^\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z) (VRB|DBG|INF|WRN|ERR|FTL)\] ",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SequencePattern = new(
        @"seq=(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var dir in _directories)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort: temp cleanup must not fail the suite.
            }
        }
    }

    [Fact]
    public void EmittedLines_CarryParseableFullDateUtcTimestamp()
    {
        var dir = NewDirectory();
        var now = new DateTimeOffset(2026, 9, 8, 16, 15, 29, 123, TimeSpan.Zero);
        var options = new ConsoleLogOptions { MaxFileSizeBytes = 10 * 1024 * 1024, RetainedFileCountLimit = 5 };
        using var sink = new RunLogSink(
            Path.Combine(dir, "run-.log"),
            () => options,
            () => now);
        using (var log = NewLogger(sink))
        {
            log.Information("worker picked up item {ItemId}", "abc123");
            log.Warning("worker-pickup deferred: disk guard tripped");
            log.Debug("trace detail {N}", 7);
        }

        var lines = ReadAllLines(dir);
        Assert.Equal(3, lines.Count);
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        foreach (var line in lines)
        {
            var match = LinePattern.Match(line);
            Assert.True(match.Success, $"Line missing full-date UTC timestamp: {line}");
            Assert.True(
                DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed),
                $"Timestamp not parseable: {match.Groups[1].Value}");
            // The line timestamp is Serilog's event time (real UTC now); the
            // assertion that matters is shape + full date, not the value.
            Assert.InRange(parsed, before, DateTimeOffset.UtcNow.AddMinutes(5));
        }

        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z INF\] ", lines[0]);
        Assert.Contains("worker picked up item", lines[0], StringComparison.Ordinal);
        Assert.Contains("abc123", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z WRN\] worker-pickup deferred: disk guard tripped$", lines[1]);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z DBG\] trace detail 7$", lines[2]);
    }

    [Fact]
    public void EmittedException_IsPreservedOnFollowingLines()
    {
        var dir = NewDirectory();
        var options = new ConsoleLogOptions { MaxFileSizeBytes = 1024 * 1024, RetainedFileCountLimit = 3 };
        using var sink = new RunLogSink(Path.Combine(dir, "run-.log"), () => options);
        var failure = new InvalidOperationException("boom");
        using (var log = NewLogger(sink))
        {
            log.Error(failure, "agent run failed");
        }

        var text = File.ReadAllText(Directory.GetFiles(dir, "run-*.log").Single());
        Assert.StartsWith("[", text, StringComparison.Ordinal);
        Assert.Contains("agent run failed", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", text, StringComparison.Ordinal);
        Assert.Matches(LinePattern, text.Split('\n')[0]);
    }

    [Fact]
    public void Rotation_TriggersAtConfiguredSize()
    {
        var dir = NewDirectory();
        var options = new ConsoleLogOptions { MaxFileSizeBytes = 1024, RetainedFileCountLimit = 10 };
        using var sink = new RunLogSink(Path.Combine(dir, "run-.log"), () => options);
        using (var log = NewLogger(sink))
        {
            for (var i = 0; i < 50; i++)
                log.Information("worker-pickup deferred seq={Seq} reason=disk-guard", i);
        }

        var files = Directory.GetFiles(dir, "run-*.log");
        Assert.True(files.Length >= 2, $"Expected size rotation to seal segments, found: {string.Join(",", files)}");
        // Every sealed file holds whole lines only and never exceeds the cap.
        Assert.All(files, f => Assert.True(
            new FileInfo(f).Length <= options.MaxFileSizeBytes,
            $"{f} exceeds MaxFileSizeBytes"));
    }

    [Fact]
    public void SustainedWrites_StayBoundedAndLoseNoLines()
    {
        var dir = NewDirectory();
        const int retained = 4;
        const long maxSize = 2048;
        var options = new ConsoleLogOptions { MaxFileSizeBytes = maxSize, RetainedFileCountLimit = retained };
        using var sink = new RunLogSink(Path.Combine(dir, "run-.log"), () => options);

        const int total = 3000;
        using (var log = NewLogger(sink))
        {
            for (var i = 0; i < total; i++)
                log.Information("worker-pickup deferred seq={Seq} reason=disk-guard", i);
        }

        var files = Directory.GetFiles(dir, "run-*.log");
        // Retained-file count holds regardless of write rate ...
        Assert.True(files.Length <= retained, $"Expected at most {retained} files, found {files.Length}");
        // ... so the total footprint has a computable ceiling.
        var totalBytes = files.Sum(f => new FileInfo(f).Length);
        Assert.True(totalBytes <= retained * maxSize, $"Footprint {totalBytes} exceeds ceiling {retained * maxSize}");

        // The newest `retained` files cannot hold all 3000 lines, but the
        // surviving window must be gap-free and duplicate-free: rotation
        // flushes before every rename, so no line is lost or repeated.
        var sequences = ReadAllLines(dir)
            .Select(l => SequencePattern.Match(l))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .OrderBy(n => n)
            .ToList();
        Assert.Equal(sequences.Count, sequences.Distinct().Count());
        Assert.Equal(sequences[^1] - sequences[0] + 1, sequences.Count);
        Assert.Equal(total - 1, sequences[^1]);
    }

    [Fact]
    public void RotationKnobs_HotReloadWithoutRestart()
    {
        var dir = NewDirectory();
        var options = new ConsoleLogOptions { MaxFileSizeBytes = 1024 * 1024, RetainedFileCountLimit = 5 };
        using var sink = new RunLogSink(Path.Combine(dir, "run-.log"), () => options);
        using (var log = NewLogger(sink))
        {
            for (var i = 0; i < 10; i++)
                log.Information("steady state seq={Seq}", i);
            Assert.Single(Directory.GetFiles(dir, "run-*.log"));

            // Shrink the size knob on the live instance: rotation must start
            // without recreating the sink (no restart).
            options.MaxFileSizeBytes = 512;
            for (var i = 10; i < 60; i++)
                log.Information("hot loop seq={Seq} padding-padding-padding-padding", i);
            Assert.True(Directory.GetFiles(dir, "run-*.log").Length >= 2);

            // Shrink the retained count on the live instance: old sealed
            // files must be reaped on the next roll.
            options.RetainedFileCountLimit = 2;
            for (var i = 60; i < 140; i++)
                log.Information("hot loop seq={Seq} padding-padding-padding-padding", i);
            Assert.True(Directory.GetFiles(dir, "run-*.log").Length <= 2);
        }
    }

    [Fact]
    public void InvalidLiveValues_AreClampedAndNeverThrow()
    {
        var dir = NewDirectory();
        var options = new ConsoleLogOptions { MaxFileSizeBytes = 0, RetainedFileCountLimit = -3 };
        using var sink = new RunLogSink(Path.Combine(dir, "run-.log"), () => options);
        using (var log = NewLogger(sink))
        {
            for (var i = 0; i < 5; i++)
                log.Information("seq={Seq}", i);
        }

        var files = Directory.GetFiles(dir, "run-*.log");
        Assert.Single(files);
        // Retained count clamps to 1, so only the active file (with the
        // newest line) survives — still bounded, nothing throws.
        var surviving = ReadAllLines(dir, "run-*.log");
        Assert.Single(surviving);
        Assert.Contains("seq=4", surviving[0], StringComparison.Ordinal);
    }

    [Fact]
    public void DateBoundary_OpensNewDatedFile()
    {
        var dir = NewDirectory();
        var now = new DateTimeOffset(2026, 9, 8, 23, 59, 59, TimeSpan.Zero);
        var options = new ConsoleLogOptions { MaxFileSizeBytes = 1024 * 1024, RetainedFileCountLimit = 5 };
        using var sink = new RunLogSink(
            Path.Combine(dir, "run-.log"),
            () => options,
            () => now);
        using (var log = NewLogger(sink))
        {
            log.Information("last line of the day");
            now = now.AddSeconds(2);
            log.Information("first line of the next day");
        }

        Assert.True(File.Exists(Path.Combine(dir, "run-20260908.log")), "Expected dated file for Sep 8");
        Assert.True(File.Exists(Path.Combine(dir, "run-20260909.log")), "Expected dated file for Sep 9");
        Assert.Contains("last line of the day", File.ReadAllText(Path.Combine(dir, "run-20260908.log")), StringComparison.Ordinal);
        var nextDay = File.ReadAllText(Path.Combine(dir, "run-20260909.log"));
        Assert.Contains("first line of the next day", nextDay, StringComparison.Ordinal);
        // The line itself carries a parseable full-date UTC timestamp, so a
        // time-based grep can never confuse it with an earlier day's lines.
        Assert.Matches(LinePattern, nextDay.Split('\n')[0]);
    }

    [Fact]
    public void UndatedTemplate_UsesLiteralPathAndStillRotates()
    {
        var dir = NewDirectory();
        var options = new ConsoleLogOptions { MaxFileSizeBytes = 512, RetainedFileCountLimit = 3 };
        using var sink = new RunLogSink(Path.Combine(dir, "orchestrator.run.log"), () => options);
        using (var log = NewLogger(sink))
        {
            for (var i = 0; i < 40; i++)
                log.Information("seq={Seq} padding-padding-padding-padding-padding", i);
        }

        var files = Directory.GetFiles(dir, "orchestrator.run*.log");
        Assert.Contains(Path.Combine(dir, "orchestrator.run.log"), files);
        Assert.True(files.Length <= 3, $"Expected at most 3 files, found {files.Length}");
        Assert.All(ReadAllLines(dir, "orchestrator.run*.log"), l => Assert.True(LinePattern.IsMatch(l), $"Bad line: {l}"));
    }

    private static Serilog.Core.Logger NewLogger(RunLogSink sink) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

    private string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "runlog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _directories.Add(dir);
        return dir;
    }

    private static List<string> ReadAllLines(string dir, string pattern = "run-*.log") =>
        Directory.GetFiles(dir, pattern)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => File.ReadAllLines(f))
            .Where(l => l.Length > 0)
            .ToList();
}
