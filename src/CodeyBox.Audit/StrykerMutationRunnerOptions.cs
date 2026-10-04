using System.Text.RegularExpressions;

namespace CodeyBox.Audit;

/// <summary>
/// Explicit per-project override pinning one project under test to its test
/// projects. When <see cref="StrykerMutationRunnerOptions.Projects"/> is
/// non-empty, csproj auto-discovery is skipped and only these entries run.
/// All paths are repository-relative (forward slashes), validated the same
/// way as discovered paths.
/// </summary>
public sealed record StrykerProjectOverride
{
    /// <summary>Repository-relative path of the project under test.</summary>
    public string Project { get; init; } = "";

    /// <summary>Repository-relative paths of the test projects covering it.</summary>
    public IReadOnlyList<string> TestProjects { get; init; } = [];
}

/// <summary>
/// Operator configuration for <see cref="StrykerMutationRunner"/>, bound from
/// <c>CodeyBox:Mutation:Stryker</c> and re-read per audit via the host's
/// <c>IOptionsMonitor</c>, so edits hot-reload without a restart. Every
/// operational value (tool location, expected version, concurrency, size caps,
/// project selection) is a knob here — never a literal in the runner.
/// </summary>
public sealed record StrykerMutationRunnerOptions
{
    /// <summary>
    /// Stryker.NET version this runner was validated against. The runner
    /// refuses to score a run whose banner reports a different version, so an
    /// operator-side upgrade cannot silently change the mutation semantics
    /// underneath a pinned gate.
    /// </summary>
    public const string PinnedVersion = "4.16.0";

    /// <summary>
    /// Master switch for the real engine. Default false: merely registering
    /// the runner changes nothing until the operator opts in AND enables the
    /// <c>tests:mutation-rigor</c> gate itself.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Tool version the operator provisioned into the audit sandbox baseline.
    /// Default is the validated pin. A detected mismatch fails the run with a
    /// provisioning diagnostic instead of scoring under unknown semantics.
    /// </summary>
    public string ExpectedVersion { get; init; } = PinnedVersion;

    /// <summary>
    /// Argv prefix used to launch Stryker inside the sandbox. Default
    /// <c>["dotnet", "stryker"]</c> resolves a manifest/global
    /// <c>dotnet-stryker</c> tool; operators with a fixed shim may set an
    /// absolute path (e.g. <c>["/opt/stryker/dotnet-stryker"]</c>). Array
    /// elements only — never a shell string. The runner never installs tools;
    /// a missing binary fails with a provisioning diagnostic.
    /// </summary>
    public IReadOnlyList<string> ToolCommand { get; init; } = ["dotnet", "stryker"];

    /// <summary>
    /// Stryker <c>--concurrency</c> worker count. 0 omits the flag (Stryker
    /// default). Must be 0-64; the runner never passes an unbounded value.
    /// </summary>
    public int Concurrency { get; init; }

    /// <summary>
    /// Stryker <c>--mutation-level</c>. One of Basic, Standard, Advanced,
    /// Complete. Default Standard (the Stryker default).
    /// </summary>
    public string MutationLevel { get; init; } = "Standard";

    /// <summary>
    /// Stryker <c>--configuration</c> (Debug/Release). Passed explicitly so
    /// the build flavor is deterministic rather than project-default.
    /// </summary>
    public string Configuration { get; init; } = "Debug";

    /// <summary>Maximum Stryker JSON report bytes read. Default 8 MiB.</summary>
    public int MaxReportBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Per-stream capture cap for Stryker console output. Default 1 MiB.
    /// The run is never killed for console volume; only the retained tail is
    /// bounded.
    /// </summary>
    public int MaxConsoleBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum <c>find</c> depth for csproj discovery. Default 6.</summary>
    public int DiscoveryMaxDepth { get; init; } = 6;

    /// <summary>Maximum csproj files read during discovery. Default 256.</summary>
    public int MaxDiscoveredProjects { get; init; } = 256;

    /// <summary>Maximum changed files accepted per audit. Default 256.</summary>
    public int MaxChangedFiles { get; init; } = 256;

    /// <summary>
    /// Maximum project-under-test groups per audit. More groups fail closed
    /// (narrow the diff or raise the cap) rather than silently dropping
    /// projects from the gate.
    /// </summary>
    public int MaxProjectsPerRun { get; init; } = 8;

    /// <summary>Maximum test projects attached to one project under test.</summary>
    public int MaxTestProjectsPerProject { get; init; } = 8;

    /// <summary>Timeout in seconds for control-plane probes (help, find, cat).</summary>
    public int ProbeTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Explicit project selection. Empty (default) means deterministic
    /// auto-discovery: every production csproj owning a changed file runs
    /// with all test projects that reference it.
    /// </summary>
    public IReadOnlyList<StrykerProjectOverride> Projects { get; init; } = [];

    /// <summary>
    /// Validates the options. Returns an empty list when valid; every entry
    /// is an operator-actionable message. Pure: no I/O, no ambient state.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ExpectedVersion) || ExpectedVersion.Length > 64
            || !VersionPattern.IsMatch(ExpectedVersion.Trim()))
            errors.Add($"Stryker:ExpectedVersion '{ExpectedVersion}' is not a plain version number (e.g. '4.16.0').");
        if (ToolCommand.Count == 0 || ToolCommand.Count > 4)
            errors.Add("Stryker:ToolCommand must have 1-4 argv elements.");
        else
        {
            foreach (var element in ToolCommand)
            {
                if (!StrykerArgv.IsValidToolCommandElement(element))
                    errors.Add($"Stryker:ToolCommand element '{element}' is not a bare binary name or rooted path.");
            }
        }
        if (Concurrency is < 0 or > 64)
            errors.Add("Stryker:Concurrency must be 0-64 (0 omits the flag).");
        if (!ValidMutationLevels.Contains(MutationLevel))
            errors.Add($"Stryker:MutationLevel '{MutationLevel}' must be one of {string.Join(", ", ValidMutationLevels)}.");
        if (string.IsNullOrWhiteSpace(Configuration) || Configuration.Length > 64
            || !NamePattern.IsMatch(Configuration.Trim()))
            errors.Add($"Stryker:Configuration '{Configuration}' must be a plain build configuration name.");
        if (MaxReportBytes < 64 * 1024 || MaxReportBytes > 256 * 1024 * 1024)
            errors.Add("Stryker:MaxReportBytes must be 64 KiB-256 MiB.");
        if (MaxConsoleBytes < 4096 || MaxConsoleBytes > 64 * 1024 * 1024)
            errors.Add("Stryker:MaxConsoleBytes must be 4 KiB-64 MiB.");
        if (DiscoveryMaxDepth is < 1 or > 16)
            errors.Add("Stryker:DiscoveryMaxDepth must be 1-16.");
        if (MaxDiscoveredProjects is < 1 or > 4096)
            errors.Add("Stryker:MaxDiscoveredProjects must be 1-4096.");
        if (MaxChangedFiles is < 1 or > 4096)
            errors.Add("Stryker:MaxChangedFiles must be 1-4096.");
        if (MaxProjectsPerRun is < 1 or > 64)
            errors.Add("Stryker:MaxProjectsPerRun must be 1-64.");
        if (MaxTestProjectsPerProject is < 1 or > 64)
            errors.Add("Stryker:MaxTestProjectsPerProject must be 1-64.");
        if (ProbeTimeoutSeconds is < 10 or > 600)
            errors.Add("Stryker:ProbeTimeoutSeconds must be 10-600.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Projects)
        {
            if (string.IsNullOrWhiteSpace(entry.Project)
                || StrykerPaths.NormalizeRepoPath(entry.Project) is null)
                errors.Add($"Stryker:Projects entry '{entry.Project}' is not a valid repository-relative path.");
            else if (!seen.Add(entry.Project))
                errors.Add($"Stryker:Projects entry '{entry.Project}' is duplicated.");
            if (entry.TestProjects.Count == 0 || entry.TestProjects.Count > 64)
                errors.Add($"Stryker:Projects entry '{entry.Project}' must list 1-64 test projects.");
            foreach (var test in entry.TestProjects)
            {
                if (StrykerPaths.NormalizeRepoPath(test) is null)
                    errors.Add($"Stryker:Projects entry '{entry.Project}' has an invalid test-project path '{test}'.");
            }
        }
        return errors;
    }

    private static readonly Regex VersionPattern = new(
        @"^\d+\.\d+\.\d+([\-.+][0-9A-Za-z\-.+]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlySet<string> ValidMutationLevels =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Basic", "Standard", "Advanced", "Complete" };
}
