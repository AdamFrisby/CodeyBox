using CodeyBox.Audit;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// One test's recorded coverage (possibly empty — no report / no hits).
/// </summary>
public sealed record PerTestCoverage(
    string TestName,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Covers);

/// <summary>
/// Collects per-test XPlat/Cobertura coverage by running each listed test in
/// isolation through <c>dotnet test --filter FullyQualifiedName=… --collect</c>
/// and parsing the report with <see cref="CoberturaParser"/>.
/// </summary>
/// <remarks>
/// Implementations may run collections for DIFFERENT test targets in
/// parallel, but runs against one target's output directory must serialize:
/// coverlet writes backup/hits files next to the instrumented assemblies and
/// restores them at session end, so concurrent sessions on the same bin
/// directory race (<c>BackupOriginalModule</c> throws, and a crashed session
/// can leave the on-disk assembly instrumented).
/// </remarks>
public interface IPerTestCoverageCollector
{
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>>> CollectAsync(
        string repoRoot,
        string testTarget,
        IReadOnlyList<string> testNames,
        TestSelectionProducerOptions options,
        CancellationToken ct);
}

public sealed class PerTestCoverletCollector : IPerTestCoverageCollector
{
    private readonly IHostCommandRunner _runner;

    public PerTestCoverletCollector(IHostCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>>> CollectAsync(
        string repoRoot,
        string testTarget,
        IReadOnlyList<string> testNames,
        TestSelectionProducerOptions options,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(testTarget);
        ArgumentNullException.ThrowIfNull(testNames);
        ArgumentNullException.ThrowIfNull(options);

        var root = Path.GetFullPath(repoRoot);
        var configuredResults = options.ResultsDirectory;
        string resultsRoot;
        bool ephemeralResults;
        if (string.IsNullOrWhiteSpace(configuredResults))
        {
            ephemeralResults = true;
            resultsRoot = Path.Combine(Path.GetTempPath(), "codeybox-test-selection-" + Guid.NewGuid().ToString("N"));
        }
        else
        {
            ephemeralResults = false;
            resultsRoot = Path.GetFullPath(configuredResults);
        }

        Directory.CreateDirectory(resultsRoot);

        var runsettings = WriteRunsettings(resultsRoot, options.Collector);
        var collected = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>>(
            StringComparer.Ordinal);
        var reportsFound = 0;
        var errors = new List<Exception>();
        try
        {
            // Serialized: every run instruments the same bin directory —
            // coverlet's backup/restore is not safe against concurrent
            // sessions (BackupOriginalModule throws IOException sharing the
            // pdb). Parallelism across test projects happens in the
            // producer's per-project fan-out instead.
            foreach (var testName in testNames)
            {
                ct.ThrowIfCancellationRequested();
                var found = await CollectOneAsync(
                    root, testTarget, testName, options, resultsRoot, runsettings, collected,
                    errors, ct).ConfigureAwait(false);
                if (found)
                    reportsFound++;
            }

            if (errors.Count > 0)
                throw errors[0];
        }
        finally
        {
            if (ephemeralResults)
                TryDeleteDirectory(resultsRoot);
        }

        if (testNames.Count > 0 && reportsFound == 0)
        {
            throw new TestSelectionBaselineProduceException(
                "No Cobertura report was produced. Ensure test projects reference coverlet.collector " +
                $"so '--collect \"{options.Collector}\"' emits coverage.");
        }

        return collected;
    }

    private async Task<bool> CollectOneAsync(
        string repoRoot,
        string testTarget,
        string testName,
        TestSelectionProducerOptions options,
        string resultsRoot,
        string runsettings,
        Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>> collected,
        List<Exception> errors,
        CancellationToken ct)
    {
        try
        {
            var perTestDir = Path.Combine(resultsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(perTestDir);
            if (!HostPathPolicy.IsWithinDirectory(perTestDir, resultsRoot))
            {
                throw new TestSelectionBaselineProduceException(
                    "Per-test results directory escaped the results root.");
            }

            var filter = $"FullyQualifiedName={VstestFilterEscaping.EscapeValue(testName)}";
            var argv = new List<string>
            {
                options.DotnetExecutable, "test", testTarget,
                "--no-build",
                "--nologo",
                "--filter", filter,
                "--collect", options.Collector,
                "--results-directory", perTestDir,
                "--settings", runsettings,
            };

            var run = await _runner.RunAsync(
                argv,
                repoRoot,
                extraEnvironment: null,
                options.MaxCommandStdoutChars,
                options.MaxCommandStdoutChars,
                options.PerTestTimeout,
                ct).ConfigureAwait(false);

            if (run.StdoutLimitExceeded || run.StderrLimitExceeded)
            {
                throw new TestSelectionBaselineProduceException(
                    $"Coverage run for '{Sanitize(testName)}' exceeded an output cap.");
            }

            // run.ExitCode is deliberately not fatal: a failing test still
            // emits its coverage report, and a run that produced none yields
            // an empty covers map — which the consumer treats as "no coverage
            // record" and always selects (the fail-safe direction). A systemic
            // failure is caught by the reportsFound check in CollectAsync.
            var reports = ReadReports(perTestDir, options);
            var covers = TestSelectionCoverageMap.FromReports(reports, repoRoot);
            collected[testName] = covers;
            return reports.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add(ex is TestSelectionBaselineProduceException
                ? ex
                : new TestSelectionBaselineProduceException(
                    $"Coverage run for '{Sanitize(testName)}' failed.", ex));
            return false;
        }
    }

    private static IReadOnlyList<CoberturaReport> ReadReports(
        string perTestDir,
        TestSelectionProducerOptions options)
    {
        var files = new List<string>();
        if (!Directory.Exists(perTestDir))
            return [];

        foreach (var file in Directory.EnumerateFiles(
                     perTestDir, "coverage.cobertura.xml", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (!HostPathPolicy.IsWithinDirectory(full, perTestDir))
                continue;
            files.Add(full);
            if (files.Count > options.MaxCoverageReportFilesPerTest)
            {
                throw new TestSelectionBaselineProduceException(
                    $"A per-test coverage run produced more than {options.MaxCoverageReportFilesPerTest} reports.");
            }
        }

        var reports = new List<CoberturaReport>(files.Count);
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (info.Length > options.MaxCoverageReportBytes)
            {
                throw new TestSelectionBaselineProduceException(
                    $"Coverage report '{file}' exceeds the {options.MaxCoverageReportBytes}-byte cap.");
            }

            var xml = File.ReadAllText(file);
            if (xml.Length > options.MaxCoverageReportBytes)
            {
                throw new TestSelectionBaselineProduceException(
                    $"Coverage report '{file}' exceeds the {options.MaxCoverageReportBytes}-byte cap.");
            }

            reports.Add(CoberturaParser.Parse(xml));
        }

        return reports;
    }

    private static string WriteRunsettings(string resultsRoot, string collector)
    {
        var path = Path.Combine(resultsRoot, "coverlet.runsettings");
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<RunSettings><DataCollectionRunSettings><DataCollectors>" +
            $"<DataCollector friendlyName=\"{System.Security.SecurityElement.Escape(collector)}\">" +
            "<Configuration><Format>cobertura</Format>" +
            "<IncludeTestAssembly>false</IncludeTestAssembly>" +
            "</Configuration></DataCollector>" +
            "</DataCollectors></DataCollectionRunSettings></RunSettings>";
        File.WriteAllText(path, xml);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsControl(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
