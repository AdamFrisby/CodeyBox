using CodeyBox.Core;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// Produces a <c>codeybox-test-selection-baseline/1</c> artifact from a
/// checkout: project graph, <c>dotnet test --list-tests</c>, per-test XPlat
/// Cobertura, then an atomic write that refuses to emit a truncated file.
/// </summary>
public sealed class TestSelectionBaselineProducer
{
    private readonly IHostCommandRunner _runner;
    private readonly IPerTestCoverageCollector _coverage;

    public TestSelectionBaselineProducer(
        IHostCommandRunner runner,
        IPerTestCoverageCollector? coverage = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _coverage = coverage ?? new PerTestCoverletCollector(runner);
    }

    public async Task<TestSelectionBaseline> ProduceAsync(
        TestSelectionProducerOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var repoRoot = Path.GetFullPath(options.RepoRoot);
        if (!Directory.Exists(repoRoot))
            throw new DirectoryNotFoundException($"Repository '{repoRoot}' does not exist.");

        var outputPath = Path.GetFullPath(options.OutputPath);
        var commit = await ResolveCommitAsync(repoRoot, options, ct).ConfigureAwait(false);
        var graph = await MsBuildProjectGraph.LoadAsync(
            repoRoot, options.SolutionPath, options, _runner, ct).ConfigureAwait(false);

        var buildTarget = options.SolutionPath is { Length: > 0 } sln
            ? MsBuildProjectGraph.ToRepoRelative(repoRoot, Path.GetFullPath(sln))
            : MsBuildProjectGraph.FindSolutionFile(repoRoot) is { } found
                ? MsBuildProjectGraph.ToRepoRelative(repoRoot, found)
                : null;

        if (!options.SkipBuild)
            await BuildAsync(repoRoot, buildTarget, graph, options, ct).ConfigureAwait(false);

        var listed = await ListTestsAsync(repoRoot, buildTarget, graph, options, ct)
            .ConfigureAwait(false);
        if (listed.All.Count == 0)
            throw new TestSelectionBaselineProduceException("dotnet test --list-tests produced no tests.");

        var definingFiles = await ResolveDefiningFilesAsync(
            repoRoot, listed, graph, options, ct).ConfigureAwait(false);

        var coversByTest = await CollectCoverageAsync(
            repoRoot, buildTarget, graph, listed, options, ct).ConfigureAwait(false);

        var tests = new Dictionary<string, BaselineTestEntry>(StringComparer.Ordinal);
        foreach (var name in listed.All)
        {
            definingFiles.TryGetValue(name, out var file);
            coversByTest.TryGetValue(name, out var covers);
            tests[name] = new BaselineTestEntry(
                file ?? "",
                covers ?? new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal));
        }

        var affected = AffectedTestsGraph.Compute(graph.ProjectReferences, listed.OwningProject);
        var baseline = new TestSelectionBaseline(
            commit,
            options.Clock.GetUtcNow(),
            new BaselineProjectGraph(graph.FileProject, affected),
            tests);

        TestSelectionBaselineJson.WriteAtomic(outputPath, baseline, options.ToReadLimits());
        return baseline;
    }

    private async Task BuildAsync(
        string repoRoot,
        string? solutionRelative,
        MsBuildProjectGraph.Graph graph,
        TestSelectionProducerOptions options,
        CancellationToken ct)
    {
        if (solutionRelative is not null)
        {
            await RunDotnetAsync(
                repoRoot,
                [options.DotnetExecutable, "build", solutionRelative, "--nologo"],
                options,
                "dotnet build",
                ct).ConfigureAwait(false);
            return;
        }

        foreach (var project in graph.TestProjects)
        {
            await RunDotnetAsync(
                repoRoot,
                [options.DotnetExecutable, "build", project, "--nologo"],
                options,
                $"dotnet build {project}",
                ct).ConfigureAwait(false);
        }
    }

    private async Task<ListedTests> ListTestsAsync(
        string repoRoot,
        string? solutionRelative,
        MsBuildProjectGraph.Graph graph,
        TestSelectionProducerOptions options,
        CancellationToken ct)
    {
        var all = new List<string>();
        var owning = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = graph.TestProjects.Count > 0
            ? graph.TestProjects
            : solutionRelative is null ? Array.Empty<string>() : [solutionRelative];

        if (targets.Count == 0)
            throw new TestSelectionBaselineProduceException("No test projects were found.");

        foreach (var target in targets)
        {
            var result = await HostCommandRun.CappedAsync(
                _runner,
                [options.DotnetExecutable, "test", target, "--no-build", "--nologo", "--list-tests"],
                repoRoot,
                options.MaxListTestsChars,
                options.MaxCommandStdoutChars,
                options.CommandTimeout,
                $"dotnet test --list-tests on '{target}'",
                ct).ConfigureAwait(false);
            if (result.StdoutLimitExceeded || result.StderrLimitExceeded)
            {
                throw new TestSelectionBaselineProduceException(
                    $"dotnet test --list-tests on '{target}' exceeded an output cap.");
            }

            if (!result.Success)
            {
                throw new TestSelectionBaselineProduceException(
                    $"dotnet test --list-tests on '{target}' exited {result.ExitCode}: {HostCommandRun.Tail(result.Stderr)}");
            }

            var names = DotnetTestListParser.Parse(
                result.Stdout, options.MaxBaselineTests, options.MaxTestNameChars);
            foreach (var name in names)
            {
                if (owning.ContainsKey(name))
                    continue;
                if (all.Count >= options.MaxBaselineTests)
                {
                    throw new TestSelectionBaselineProduceException(
                        $"Baseline exceeds the test cap ({options.MaxBaselineTests} tests).");
                }

                all.Add(name);
                owning[name] = target;
            }
        }

        return new ListedTests(all, owning);
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveDefiningFilesAsync(
        string repoRoot,
        ListedTests listed,
        MsBuildProjectGraph.Graph graph,
        TestSelectionProducerOptions options,
        CancellationToken ct)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var assemblies = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var project in graph.TestProjects)
        {
            assemblies[project] = await TestDefiningFileResolver.ResolveTargetPathAsync(
                repoRoot, project, options, _runner, ct).ConfigureAwait(false);
        }

        foreach (var name in listed.All)
        {
            if (!listed.OwningProject.TryGetValue(name, out var project))
            {
                files[name] = "";
                continue;
            }

            if (!assemblies.TryGetValue(project, out var assembly) || assembly is null)
            {
                files[name] = "";
                continue;
            }

            files[name] = TestDefiningFileResolver.Resolve(name, assembly, repoRoot);
        }

        return files;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>>> CollectCoverageAsync(
        string repoRoot,
        string? solutionRelative,
        MsBuildProjectGraph.Graph graph,
        ListedTests listed,
        TestSelectionProducerOptions options,
        CancellationToken ct)
    {
        var merged = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>>(
            StringComparer.Ordinal);
        var groups = listed.All
            .GroupBy(name => listed.OwningProject.TryGetValue(name, out var project) ? project : "", StringComparer.Ordinal)
            .ToList();

        foreach (var group in groups)
        {
            var target = group.Key.Length > 0
                ? group.Key
                : solutionRelative ?? throw new TestSelectionBaselineProduceException(
                    "Cannot collect coverage: no test project or solution target.");
            var names = group.ToList();
            var covers = await _coverage.CollectAsync(repoRoot, target, names, options, ct)
                .ConfigureAwait(false);
            foreach (var (name, map) in covers)
                merged[name] = map;
        }

        return merged;
    }

    private async Task<string> ResolveCommitAsync(
        string repoRoot,
        TestSelectionProducerOptions options,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Commit))
            return options.Commit.Trim();

        var result = await HostCommandRun.CappedAsync(
            _runner,
            [options.GitExecutable, "-C", repoRoot, "rev-parse", "--verify", "HEAD"],
            repoRoot,
            maxStdoutChars: 256,
            maxStderrChars: 16 * 1024,
            options.CommandTimeout,
            "git rev-parse HEAD",
            ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new TestSelectionBaselineProduceException(
                $"git rev-parse HEAD failed (exit {result.ExitCode}): {HostCommandRun.Tail(result.Stderr)}");
        }

        var sha = result.Stdout.Trim();
        if (!IsCommitSha(sha))
        {
            throw new TestSelectionBaselineProduceException(
                "git rev-parse HEAD did not return a hex commit sha.");
        }

        return sha;
    }

    private async Task RunDotnetAsync(
        string repoRoot,
        IReadOnlyList<string> argv,
        TestSelectionProducerOptions options,
        string label,
        CancellationToken ct)
    {
        var result = await HostCommandRun.CappedAsync(
            _runner,
            argv,
            repoRoot,
            options.MaxCommandStdoutChars,
            options.MaxCommandStdoutChars,
            options.CommandTimeout,
            label,
            ct).ConfigureAwait(false);
        if (result.StdoutLimitExceeded || result.StderrLimitExceeded)
            throw new TestSelectionBaselineProduceException($"{label} exceeded an output cap.");
        if (!result.Success)
        {
            throw new TestSelectionBaselineProduceException(
                $"{label} exited {result.ExitCode}: {HostCommandRun.Tail(result.Stderr)}{HostCommandRun.Tail(result.Stdout)}");
        }
    }

    private static void ValidateOptions(TestSelectionProducerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RepoRoot))
            throw new TestSelectionBaselineProduceException("A repository path is required (--repo).");
        if (string.IsNullOrWhiteSpace(options.OutputPath))
            throw new TestSelectionBaselineProduceException("An output path is required (--output).");
        if (options.MaxBaselineBytes <= 0 || options.MaxBaselineTests <= 0 || options.MaxBaselineCoveredLines <= 0)
            throw new TestSelectionBaselineProduceException("Baseline size caps must be positive.");
        if (options.MaxParallelism <= 0)
            throw new TestSelectionBaselineProduceException("--max-parallelism must be positive.");
        if (options.MaxCoverageReportBytes <= 0 || options.MaxCoverageReportFilesPerTest <= 0)
            throw new TestSelectionBaselineProduceException("Coverage report caps must be positive.");
        ValidateExecutableName(options.DotnetExecutable, "dotnet");
        ValidateExecutableName(options.GitExecutable, "git");
        if (string.IsNullOrWhiteSpace(options.Collector))
            throw new TestSelectionBaselineProduceException("A coverage collector name is required.");
    }

    private static void ValidateExecutableName(string value, string expectedFileName)
    {
        if (string.Equals(value, expectedFileName, StringComparison.Ordinal))
            return;
        if (!Path.IsPathRooted(value))
        {
            throw new TestSelectionBaselineProduceException(
                $"'{expectedFileName}' must be the bare name '{expectedFileName}' or an absolute path.");
        }

        var full = Path.GetFullPath(value);
        var name = Path.GetFileName(full);
        if (!string.Equals(name, expectedFileName, StringComparison.Ordinal)
            && !string.Equals(name, expectedFileName + ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new TestSelectionBaselineProduceException(
                $"Refusing to execute '{value}' as {expectedFileName}: file name must be '{expectedFileName}'.");
        }
    }

    private static bool IsCommitSha(string sha)
    {
        if (sha.Length is not (40 or 64))
            return false;
        foreach (var c in sha)
        {
            var hex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!hex)
                return false;
        }

        return true;
    }

    private sealed record ListedTests(
        IReadOnlyList<string> All,
        IReadOnlyDictionary<string, string> OwningProject);
}
