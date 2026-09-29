using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// Knobs for the test-selection baseline producer. Size caps default to the
/// same values the consumer reads from
/// <see cref="CoverageTestSelectionOptions"/>; collector and report caps
/// default to <see cref="CoverageAuditorOptions"/> so the producer and the
/// <c>tests:coverage</c> gate cannot drift.
/// </summary>
public sealed class TestSelectionProducerOptions
{
    private static readonly CoverageTestSelectionOptions SelectionDefaults = new();
    private static readonly CoverageAuditorOptions CoverageDefaults = new();

    public string RepoRoot { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string? Commit { get; init; }
    public string? SolutionPath { get; init; }
    public string DotnetExecutable { get; init; } = "dotnet";
    public string GitExecutable { get; init; } = "git";
    public string Collector { get; init; } = CoverageDefaults.Collector;
    public long MaxBaselineBytes { get; init; } = SelectionDefaults.MaxBaselineBytes;
    public int MaxBaselineTests { get; init; } = SelectionDefaults.MaxBaselineTests;
    public long MaxBaselineCoveredLines { get; init; } = SelectionDefaults.MaxBaselineCoveredLines;
    public int MaxCoverageReportBytes { get; init; } = CoverageDefaults.MaxCoverageReportBytes;
    public int MaxCoverageReportFilesPerTest { get; init; } = CoverageDefaults.MaxCoverageReportFiles;
    public int MaxParallelism { get; init; } = DefaultMaxParallelism;
    public TimeSpan PerTestTimeout { get; init; } = DefaultPerTestTimeout;
    public TimeSpan CommandTimeout { get; init; } = DefaultCommandTimeout;
    public int MaxListTestsChars { get; init; } = DefaultMaxListTestsChars;
    public int MaxCommandStdoutChars { get; init; } = DefaultMaxCommandStdoutChars;
    public int MaxProjectFiles { get; init; } = DefaultMaxProjectFiles;
    public int MaxSourceFiles { get; init; } = DefaultMaxSourceFiles;
    public int MaxTestNameChars { get; init; } = DefaultMaxTestNameChars;
    public bool SkipBuild { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public string? ResultsDirectory { get; init; }

    /// <summary>Default isolated-run parallelism: one worker per host CPU.</summary>
    public static int DefaultMaxParallelism => Math.Max(1, Environment.ProcessorCount);

    /// <summary>Default timeout for one isolated per-test coverage run.</summary>
    public static TimeSpan DefaultPerTestTimeout { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Default timeout for build / list-tests / git / sln commands.</summary>
    public static TimeSpan DefaultCommandTimeout { get; } = TimeSpan.FromMinutes(30);

    public const int DefaultMaxListTestsChars = 32 * 1024 * 1024;
    public const int DefaultMaxCommandStdoutChars = 32 * 1024 * 1024;
    public const int DefaultMaxProjectFiles = 10_000;
    public const int DefaultMaxSourceFiles = 500_000;
    public const int DefaultMaxTestNameChars = 1024;

    public BaselineReadLimits ToReadLimits()
        => new(MaxBaselineBytes, MaxBaselineTests, MaxBaselineCoveredLines);
}
