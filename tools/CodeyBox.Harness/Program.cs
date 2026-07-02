using CodeyBox.ExploratoryTesting;
using CodeyBox.ExploratoryTesting.Recipes;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Harness;

/// <summary>
/// Dev/test entrypoint for the exploratory-testing app-launch harness.
/// Brings a target up inside a graphical Multipass sandbox, captures a
/// readiness screenshot, and tears down (or holds for manual driving).
/// </summary>
public static class Program
{
    public const int ExitUsage = 2;
    public const int ExitLaunchFailed = 1;

    public static Task<int> Main(string[] args) => RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args is ["-h"] or ["--help"] or ["help"])
        {
            PrintUsage(error);
            return ExitUsage;
        }

        return args[0] switch
        {
            "jobtrack" => await RunJobTrackAsync(args[1..], output, error),
            _ => UnknownCommand(args[0], error),
        };
    }

    public enum JobTrackParseStatus
    {
        Ok,
        Usage,
        SourceMissing,
    }

    public sealed record JobTrackParseResult(
        JobTrackParseStatus Status,
        string? Source,
        string ScreenshotOut,
        bool Interactive,
        string? Error);

    /// <summary>
    /// Pure arg-parsing helper for the <c>jobtrack</c> subcommand. Extracted so
    /// the CLI surface (options, env-var fallback, source-directory validation,
    /// exit-code conventions) can be unit-tested without spinning up Multipass.
    /// </summary>
    public static JobTrackParseResult ParseJobTrackArgs(
        string[] args,
        Func<string, string?> envLookup,
        Func<string, bool> directoryExists)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
            return new JobTrackParseResult(JobTrackParseStatus.Usage, null, "", false, null);

        if (!string.Equals(args[0], "launch", StringComparison.OrdinalIgnoreCase))
            return new JobTrackParseResult(
                JobTrackParseStatus.Usage, null, "", false,
                $"Unknown command: jobtrack {args[0]}");

        var source = "";
        var screenshotOut = "harness-ready.png";
        var interactive = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source" when i + 1 < args.Length:
                    source = args[++i];
                    break;
                case "--screenshot-out" when i + 1 < args.Length:
                    screenshotOut = args[++i];
                    break;
                case "--interactive":
                    interactive = true;
                    break;
                default:
                    return new JobTrackParseResult(
                        JobTrackParseStatus.Usage, null, "", false,
                        $"Unknown option: {args[i]}");
            }
        }

        source = string.IsNullOrWhiteSpace(source)
            ? envLookup("JOBTRACK_SOURCE") ?? ""
            : source;
        if (string.IsNullOrWhiteSpace(source))
            return new JobTrackParseResult(
                JobTrackParseStatus.Usage, null, "", false,
                "JobTrack source path is required (--source or JOBTRACK_SOURCE).");

        if (!directoryExists(source))
            return new JobTrackParseResult(
                JobTrackParseStatus.SourceMissing, source, screenshotOut, interactive,
                $"JobTrack source directory does not exist: {source}");

        return new JobTrackParseResult(JobTrackParseStatus.Ok, source, screenshotOut, interactive, null);
    }

    private static async Task<int> RunJobTrackAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintJobTrackUsage(error);
            return ExitUsage;
        }

        var parsed = ParseJobTrackArgs(args, Environment.GetEnvironmentVariable, Directory.Exists);
        switch (parsed.Status)
        {
            case JobTrackParseStatus.Usage:
                if (!string.IsNullOrEmpty(parsed.Error))
                    error.WriteLine(parsed.Error);
                return ExitUsage;
            case JobTrackParseStatus.SourceMissing:
                error.WriteLine(parsed.Error);
                return ExitLaunchFailed;
        }

        using var logFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));
        var log = logFactory.CreateLogger<WebAppHarness>();
        var provider = new MultipassSandboxProvider(ResolveMultipassOptions(), logFactory.CreateLogger<MultipassSandboxProvider>());
        var harness = new WebAppHarness(provider, log);
        var recipe = JobTrackRecipe.Default(Path.GetFullPath(parsed.Source!));

        output.WriteLine($"Launching JobTrack (recipe target={recipe.TargetName}, entry={recipe.EntryUrl}) …");
        AppUnderTestSession? session = null;
        try
        {
            session = await harness.LaunchAsync(recipe);
            await File.WriteAllBytesAsync(parsed.ScreenshotOut, session.ReadinessScreenshotPng);
            output.WriteLine($"Ready. Entry URL (in-VM): {session.EntryUrl}");
            output.WriteLine($"Readiness screenshot: {parsed.ScreenshotOut} ({session.ReadinessScreenshotPng.Length} bytes)");
            output.WriteLine($"Sandbox id: {session.Sandbox.Id}");

            if (parsed.Interactive)
            {
                output.WriteLine("Interactive mode — drive via ComputerUseBridge; press Ctrl+C to tear down.");
                var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    quit.TrySetResult();
                };
                await quit.Task;
            }
        }
        catch (Exception ex)
        {
            error.WriteLine($"Launch failed: {ex.Message}");
            return ExitLaunchFailed;
        }
        finally
        {
            if (session is not null)
            {
                output.WriteLine("Tearing down sandbox …");
                await session.DisposeAsync();
                output.WriteLine("Teardown complete.");
            }
        }

        return 0;
    }

    private static MultipassSandboxOptions ResolveMultipassOptions()
    {
        var bridge = Environment.GetEnvironmentVariable("CODEYBOX_GRAPHICAL_BRIDGE");
        if (string.IsNullOrWhiteSpace(bridge))
            bridge = "cb-graphical";

        return new MultipassSandboxOptions
        {
            NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [SandboxConventions.GraphicalNetworkProfile] = bridge,
            },
        };
    }

    private static int UnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"Unknown command: {command}");
        PrintUsage(error);
        return ExitUsage;
    }

    private static void PrintJobTrackUsage(TextWriter error)
    {
        error.WriteLine("Usage: codeybox-harness jobtrack launch --source <path> [options]");
        error.WriteLine();
        error.WriteLine("Options:");
        error.WriteLine("  --source <path>       Host directory with the JobTrack source tree (required).");
        error.WriteLine("                        Falls back to JOBTRACK_SOURCE when omitted.");
        error.WriteLine("  --screenshot-out <p>  Write the readiness PNG here (default: harness-ready.png).");
        error.WriteLine("  --interactive         Keep the session alive until Ctrl+C, then tear down.");
    }

    private static void PrintUsage(TextWriter error)
    {
        error.WriteLine("""
            codeybox-harness — dev/test launcher for exploratory-testing harnesses

            Usage:
              codeybox-harness jobtrack launch --source <path> [options]

            Environment:
              JOBTRACK_SOURCE              Default --source when flag omitted
              CODEYBOX_GRAPHICAL_BRIDGE    Host bridge for the graphical profile (default: cb-graphical)

            Examples:
              dotnet run --project tools/CodeyBox.Harness -- jobtrack launch --source ../jobtrack
              dotnet run --project tools/CodeyBox.Harness -- jobtrack launch --source ../jobtrack --interactive
            """);
    }
}
