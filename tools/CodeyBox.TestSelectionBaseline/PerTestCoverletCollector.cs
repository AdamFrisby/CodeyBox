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
        var reportsFound = new int[1];
        var gate = new SemaphoreSlim(Math.Max(1, options.MaxParallelism));
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        try
        {
            var tasks = new List<Task>(testNames.Count);
            foreach (var testName in testNames)
            {
                tasks.Add(CollectOneAsync(
                    root, testTarget, testName, options, resultsRoot, runsettings, collected, gate,
                    () => Interlocked.Increment(ref reportsFound[0]), errors, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            if (!errors.IsEmpty)
                throw errors.ToArray()[0];
        }
        finally
        {
            gate.Dispose();
            if (ephemeralResults)
                TryDeleteDirectory(resultsRoot);
        }

        if (testNames.Count > 0 && reportsFound[0] == 0)
        {
            throw new TestSelectionBaselineProduceException(
                "No Cobertura report was produced. Ensure test projects reference coverlet.collector " +
                $"so '--collect \"{options.Collector}\"' emits coverage.");
        }

        return collected;
    }

    private async Task CollectOneAsync(
        string repoRoot,
        string testTarget,
        string testName,
        TestSelectionProducerOptions options,
        string resultsRoot,
        string runsettings,
        Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>> collected,
        SemaphoreSlim gate,
        Func<int> onReport,
        System.Collections.Concurrent.ConcurrentBag<Exception> errors,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
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
            if (reports.Count > 0)
                onReport();

            var covers = TestSelectionCoverageMap.FromReports(reports, repoRoot);
            lock (collected)
                collected[testName] = covers;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add(ex is TestSelectionBaselineProduceException
                ? ex
                : new TestSelectionBaselineProduceException(
                    $"Coverage run for '{Sanitize(testName)}' failed.", ex));
        }
        finally
        {
            gate.Release();
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
