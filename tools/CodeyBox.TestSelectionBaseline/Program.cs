using CodeyBox.Core;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// CLI for the test-selection baseline producer. Orchestrator jobs and humans
/// invoke the same entry point:
/// <c>dotnet run --project tools/CodeyBox.TestSelectionBaseline -- produce --repo &lt;checkout&gt; --output &lt;baseline.json&gt;</c>
/// </summary>
public static class Program
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public static Task<int> Main(string[] args)
        => RunAsync(args, Console.Out, Console.Error, CancellationToken.None);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
        => await RunAsync(args, output, error, new HostCommandRunner(), ct).ConfigureAwait(false);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        IHostCommandRunner runner,
        CancellationToken ct)
        => await RunAsync(args, output, error, runner, coverage: null, ct).ConfigureAwait(false);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        IHostCommandRunner runner,
        IPerTestCoverageCollector? coverage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(runner);

        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage(error);
            return ExitUsage;
        }

        var rest = args;
        if (string.Equals(args[0], "produce", StringComparison.OrdinalIgnoreCase))
            rest = args[1..];
        else if (args[0].StartsWith('-'))
            rest = args;
        else
        {
            error.WriteLine($"Unknown command: {args[0]}");
            PrintUsage(error);
            return ExitUsage;
        }

        if (rest.Length == 0 || rest.Any(IsHelp))
        {
            PrintUsage(error);
            return ExitUsage;
        }

        TestSelectionProducerOptions options;
        try
        {
            options = ParseArgs(rest);
        }
        catch (TestSelectionBaselineProduceException ex)
        {
            error.WriteLine(ex.Message);
            PrintUsage(error);
            return ExitUsage;
        }

        try
        {
            var producer = new TestSelectionBaselineProducer(runner, coverage);
            var baseline = await producer.ProduceAsync(options, ct).ConfigureAwait(false);
            output.WriteLine(
                $"Wrote {TestSelectionBaseline.FormatMarker} with {baseline.Tests.Count} tests to {options.OutputPath}");
            return ExitOk;
        }
        catch (TestSelectionBaselineProduceException ex)
        {
            error.WriteLine(ex.Message);
            return ExitFailed;
        }
        catch (FormatException ex)
        {
            error.WriteLine(ex.Message);
            return ExitFailed;
        }
        catch (TimeoutException ex)
        {
            error.WriteLine(ex.Message);
            return ExitFailed;
        }
        catch (DirectoryNotFoundException ex)
        {
            error.WriteLine(ex.Message);
            return ExitFailed;
        }
        catch (FileNotFoundException ex)
        {
            error.WriteLine(ex.Message);
            return ExitFailed;
        }
        catch (IOException ex)
        {
            error.WriteLine(ex.Message);
            return ExitFailed;
        }
        catch (UnauthorizedAccessException ex)
        {
            error.WriteLine(ex.Message);
            return ExitFailed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error.WriteLine("Cancelled.");
            return ExitFailed;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Unexpected failure ({ex.GetType().Name}): {ex.Message}");
            return ExitFailed;
        }
    }

    internal static TestSelectionProducerOptions ParseArgs(string[] args)
    {
        var repo = Environment.CurrentDirectory;
        string? output = null;
        string? commit = null;
        string? solution = null;
        var defaults = new TestSelectionProducerOptions();
        var dotnet = defaults.DotnetExecutable;
        var git = defaults.GitExecutable;
        var collector = defaults.Collector;
        var maxBytes = defaults.MaxBaselineBytes;
        var maxTests = defaults.MaxBaselineTests;
        var maxLines = defaults.MaxBaselineCoveredLines;
        var maxParallelism = defaults.MaxParallelism;
        var skipBuild = false;
        string? resultsDirectory = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--repo" when i + 1 < args.Length:
                    repo = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--commit" when i + 1 < args.Length:
                    commit = args[++i];
                    break;
                case "--solution" when i + 1 < args.Length:
                    solution = args[++i];
                    break;
                case "--dotnet" when i + 1 < args.Length:
                    dotnet = args[++i];
                    break;
                case "--git" when i + 1 < args.Length:
                    git = args[++i];
                    break;
                case "--results-directory" when i + 1 < args.Length:
                    resultsDirectory = args[++i];
                    break;
                case "--collector" when i + 1 < args.Length:
                    collector = args[++i];
                    break;
                case "--max-bytes" when i + 1 < args.Length:
                    maxBytes = ParsePositiveInt64(args[++i], "--max-bytes");
                    break;
                case "--max-tests" when i + 1 < args.Length:
                    maxTests = ParsePositiveInt32(args[++i], "--max-tests");
                    break;
                case "--max-covered-lines" when i + 1 < args.Length:
                    maxLines = ParsePositiveInt64(args[++i], "--max-covered-lines");
                    break;
                case "--max-parallelism" when i + 1 < args.Length:
                    maxParallelism = ParsePositiveInt32(args[++i], "--max-parallelism");
                    break;
                case "--skip-build":
                    skipBuild = true;
                    break;
                default:
                    throw new TestSelectionBaselineProduceException($"Unknown argument: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(output))
            throw new TestSelectionBaselineProduceException("--output is required.");

        return new TestSelectionProducerOptions
        {
            RepoRoot = repo,
            OutputPath = output,
            Commit = commit,
            SolutionPath = solution,
            DotnetExecutable = dotnet,
            GitExecutable = git,
            Collector = collector,
            MaxBaselineBytes = maxBytes,
            MaxBaselineTests = maxTests,
            MaxBaselineCoveredLines = maxLines,
            MaxParallelism = maxParallelism,
            SkipBuild = skipBuild,
            ResultsDirectory = resultsDirectory,
        };
    }

    private static long ParsePositiveInt64(string raw, string flag)
    {
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new TestSelectionBaselineProduceException($"{flag} must be a positive integer.");
        }

        return value;
    }

    private static int ParsePositiveInt32(string raw, string flag)
    {
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new TestSelectionBaselineProduceException($"{flag} must be a positive integer.");
        }

        return value;
    }

    private static bool IsHelp(string value)
        => value is "-h" or "--help" or "help";

    private static void PrintUsage(TextWriter error)
    {
        error.WriteLine(
            """
            Usage: codeybox-test-selection-baseline produce --repo <checkout> --output <baseline.json> [options]

              --repo PATH              Checkout to measure (default: current directory)
              --output PATH            Destination baseline JSON (required)
              --commit SHA             Recorded commit (default: git rev-parse HEAD)
              --solution PATH          Solution file (default: repo-root *.slnx / *.sln)
              --dotnet PATH            dotnet executable (default: dotnet)
              --git PATH               git executable (default: git)
              --collector NAME         Coverlet collector (default: XPlat Code Coverage)
              --results-directory DIR  Keep per-test coverage reports under DIR
                                       (default: ephemeral temp directory)
              --max-bytes N            JSON character cap (default: consumer MaxBaselineBytes)
              --max-tests N            Test-entry cap (default: consumer MaxBaselineTests)
              --max-covered-lines N    Covered-line cap (default: consumer MaxBaselineCoveredLines)
              --max-parallelism N      Isolated per-test coverage runs in parallel
              --skip-build             Assume the checkout is already built
            """);
    }
}
