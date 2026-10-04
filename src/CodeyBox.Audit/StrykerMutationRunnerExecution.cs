using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Audit;

/// <summary>Projects discovered in the tree (or taken from explicit overrides).</summary>
internal sealed record DiscoveredProjects(
    IReadOnlyList<string> Production,
    IReadOnlyList<string> Test,
    IReadOnlyDictionary<string, IReadOnlyList<string>> References,
    IReadOnlyList<string> Unreadable,
    IReadOnlyList<string> Notes);

/// <summary>One finished Stryker invocation for a project group.</summary>
internal sealed class StrykerGroupOutcome
{
    public required StrykerParsedReport Parsed { get; init; }

    public required string ToolVersion { get; init; }

    public required string ConsoleTail { get; init; }

    public required IReadOnlySet<string> ChangedSet { get; init; }

    public required string? ReportProjectRoot { get; init; }

    public required long ReportBytes { get; init; }

    public required string ReportSha256 { get; init; }

    public required string OutputDirectory { get; init; }
}

/// <summary>Execution half of <see cref="StrykerMutationRunner"/>.</summary>
public sealed partial class StrykerMutationRunner
{
    private async Task ProbeToolAsync(
        ISandbox sandbox, string root, StrykerMutationRunnerOptions opts, CancellationToken ct)
    {
        var argv = new List<string>(opts.ToolCommand) { "--help" };
        SandboxExecResult probe;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(opts.ProbeTimeoutSeconds));
            probe = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = root,
                MaxStdoutBytes = ProbeCapBytes,
                MaxStderrBytes = ProbeCapBytes,
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new StrykerToolMissingException(
                $"Stryker tool probe timed out after {opts.ProbeTimeoutSeconds}s: " +
                $"'{string.Join(' ', opts.ToolCommand)} --help'. {ProvisioningHint(opts)}");
        }
        if (probe.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(probe.ExitCode);
        if (!probe.Success)
        {
            var reason = probe.ExitCode is 126 or 127
                ? "the binary is not on PATH in the audit sandbox"
                : $"the probe exited {probe.ExitCode}";
            throw new StrykerToolMissingException(
                $"Stryker tool unavailable: {reason}. {ProvisioningHint(opts)} " +
                $"Probe output: {Truncate(CombineOutput(probe), 1000)}",
                probe.ExitCode,
                Truncate(CombineOutput(probe), 2000));
        }
    }

    private static string ProvisioningHint(StrykerMutationRunnerOptions opts) =>
        $"Expected {opts.ToolCommand[0]} providing dotnet-stryker {opts.ExpectedVersion}, " +
        "provisioned into the audit sandbox baseline image (the runner never installs tools " +
        "at audit time).";

    private async Task<DiscoveredProjects> DiscoverProjectsAsync(
        ISandbox sandbox, string root, StrykerMutationRunnerOptions opts, CancellationToken ct)
    {
        if (opts.Projects.Count > 0)
            return FromOverrides(opts);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opts.ProbeTimeoutSeconds));
        var find = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["find", ".", "-maxdepth", opts.DiscoveryMaxDepth.ToString(),
                "-name", "*.csproj", "-print"],
            WorkingDirectory = root,
            MaxStdoutBytes = DiscoveryListCapBytes,
            MaxStderrBytes = ProbeCapBytes,
        }, timeout.Token).ConfigureAwait(false);
        if (find.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(find.ExitCode);
        if (!find.Success)
            throw new StrykerRunFailedException(
                $"Stryker project discovery failed (find exited {find.ExitCode}). " +
                $"Output: {Truncate(CombineOutput(find), 1000)}",
                find.ExitCode,
                Truncate(CombineOutput(find), 2000))
            {
                Kind = StrykerFailureKind.Tool,
            };

        var candidates = find.Stdout
            .Split('\n')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.StartsWith("./", StringComparison.Ordinal) ? s[2..] : s)
            .Select(StrykerPaths.NormalizeRepoPath)
            .Where(p => p is not null)
            .Cast<string>()
            .Where(p => !IsArtifactPath(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count > opts.MaxDiscoveredProjects)
            throw new StrykerRunFailedException(
                $"Stryker discovery found {candidates.Count} .NET projects, above MaxDiscoveredProjects " +
                $"({opts.MaxDiscoveredProjects}). Set an explicit Stryker:Projects selection; projects " +
                "were not silently dropped.")
            {
                Kind = StrykerFailureKind.Report,
            };

        var production = new List<string>();
        var test = new List<string>();
        var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var csproj in candidates)
        {
            var read = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", "--", csproj],
                WorkingDirectory = root,
                MaxStdoutBytes = CsprojReadCapBytes,
                MaxStderrBytes = ProbeCapBytes,
            }, timeout.Token).ConfigureAwait(false);
            if (read.ExecutionUnavailable)
                throw new SandboxExecutionUnavailableException(read.ExitCode);
            if (!read.Success || read.StdoutLimitExceeded)
            {
                unreadable.Add(csproj);
                continue;
            }
            var content = read.Stdout;
            var refs = ProjectReferencePattern.Matches(content)
                .Select(m => m.Groups[1].Value.Replace('\\', '/'))
                .Select(v => v[(v.LastIndexOf('/') + 1)..])
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            references[csproj] = refs;
            if (IsTestProject(csproj, content))
                test.Add(csproj);
            else
                production.Add(csproj);
        }

        return new DiscoveredProjects(production, test, references, unreadable, []);
    }

    private static DiscoveredProjects FromOverrides(StrykerMutationRunnerOptions opts)
    {
        var production = new List<string>();
        var tests = new List<string>();
        var references =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in opts.Projects
                     .OrderBy(e => e.Project, StringComparer.Ordinal))
        {
            var prod = StrykerPaths.NormalizeRepoPath(entry.Project)!;
            production.Add(prod);
            var prodFile = prod[(prod.LastIndexOf('/') + 1)..];
            foreach (var testPath in entry.TestProjects
                         .Select(StrykerPaths.NormalizeRepoPath)
                         .Where(p => p is not null)
                         .Cast<string>()
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!tests.Contains(testPath, StringComparer.Ordinal))
                    tests.Add(testPath);
                if (references.TryGetValue(testPath, out var existing))
                {
                    if (!existing.Contains(prodFile, StringComparer.OrdinalIgnoreCase))
                        references[testPath] = [.. existing, prodFile];
                }
                else
                {
                    references[testPath] = [prodFile];
                }
            }
        }
        tests.Sort(StringComparer.Ordinal);
        return new DiscoveredProjects(production, tests, references, [], []);
    }

    private StrykerSelection BuildSelection(
        IReadOnlyList<string> changed,
        DiscoveredProjects discovered,
        StrykerMutationRunnerOptions opts)
    {
        var selection = StrykerProjectSelector.Select(
            changed, discovered.Production, discovered.Test,
            discovered.References, opts.MaxTestProjectsPerProject);
        if (selection.Groups.Count == 0 && discovered.Unreadable.Count > 0)
        {
            var unreadableDirs = discovered.Unreadable
                .Select(StrykerPaths.DirectoryOf)
                .ToList();
            var blocked = selection.UnmappedChangedFiles
                .Where(f => IsUnderUnreadable(f, unreadableDirs))
                .ToList();
            if (blocked.Count > 0)
                throw new StrykerRunFailedException(
                    $"Stryker could not read the project file(s) owning changed code " +
                    $"({string.Join(", ", blocked.Take(5).Select(p => StrykerPaths.SanitizeForLog(p)))}), " +
                    "so no score can be established. Fix sandbox file permissions or exclude the path.")
                {
                    Kind = StrykerFailureKind.Report,
                };
        }
        return selection;
    }

    private static bool IsUnderUnreadable(string file, IReadOnlyList<string> dirs)
    {
        foreach (var dir in dirs)
        {
            if (dir.Length == 0)
                return true;
            if (file.Length > dir.Length
                && file.StartsWith(dir, StringComparison.Ordinal)
                && file[dir.Length] == '/')
                return true;
        }
        return false;
    }

    private static bool IsArtifactPath(string repoPath)
    {
        foreach (var segment in repoPath.Split('/'))
        {
            if (ArtifactDirectoryNames.Contains(segment))
                return true;
        }
        return false;
    }

    private static bool IsTestProject(string csproj, string content)
    {
        var fileName = csproj[(csproj.LastIndexOf('/') + 1)..];
        if (fileName.Contains(".Test.", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".Tests.", StringComparison.OrdinalIgnoreCase))
            return true;
        var dir = StrykerPaths.DirectoryOf(csproj);
        foreach (var segment in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("test", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("tests", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return content.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
            || content.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase)
            || content.Contains("xunit", StringComparison.OrdinalIgnoreCase)
            || content.Contains("nunit", StringComparison.OrdinalIgnoreCase)
            || content.Contains("mstest", StringComparison.OrdinalIgnoreCase)
            || content.Contains("tunit", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeNoSelection(StrykerSelection selection)
    {
        var parts = new List<string>();
        if (selection.UnmappedChangedFiles.Count > 0)
            parts.Add($"{selection.UnmappedChangedFiles.Count} changed file(s) match no known .NET project " +
                $"({string.Join(", ", selection.UnmappedChangedFiles.Take(5).Select(p => StrykerPaths.SanitizeForLog(p)))})");
        if (selection.TestOnlyChangedFiles.Count > 0)
            parts.Add($"{selection.TestOnlyChangedFiles.Count} changed file(s) are test-only " +
                $"({string.Join(", ", selection.TestOnlyChangedFiles.Take(5).Select(p => StrykerPaths.SanitizeForLog(p)))})");
        if (parts.Count == 0)
            parts.Add("no changed files map to a mutatable production project");
        return string.Join("; ", parts) + ". Only production code covered by a test project can be mutated.";
    }

    private static string DescribeDiscovered(DiscoveredProjects discovered) =>
        $"{discovered.Production.Count} production / {discovered.Test.Count} test project(s) discovered.";

    private async Task<string> ReadSourceShaAsync(ISandbox sandbox, string root, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var rev = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", root, "rev-parse", "HEAD"],
                MaxStdoutBytes = 4096,
                MaxStderrBytes = 4096,
            }, timeout.Token).ConfigureAwait(false);
            if (rev.ExecutionUnavailable)
                throw new SandboxExecutionUnavailableException(rev.ExitCode);
            var sha = rev.Stdout.Trim();
            if (rev.Success && sha.Length == 40 && sha.All(Uri.IsHexDigit))
                return sha.ToLowerInvariant();
        }
        catch (Exception ex) when (SandboxDeferralGuard.ShouldWrap(ex))
        {
        }
        return "unknown";
    }
}
