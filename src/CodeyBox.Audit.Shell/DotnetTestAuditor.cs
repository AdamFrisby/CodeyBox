using System.Globalization;
using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// First-class <c>dotnet test</c> auditor. Replaces the previous arrangement
/// where <c>csharp:test-pass</c> was a generic <see cref="ShellCommandAuditor"/>
/// that three separate call sites had to sniff as "really a dotnet test"
/// (result-classifier selection by <c>argv[1]=="test"</c>, per-test hang
/// handling, and a future <c>--filter</c> injection).
///
/// This type OWNS building the invocation — base command, test selection
/// <c>--filter</c>, and <c>--blame-hang</c> args — and carries its own result
/// classifier. Actual execution (tool-presence probe, missing-tool handling,
/// classification) is delegated to a <see cref="ShellCommandAuditor"/> so the
/// well-tested shell run semantics are reused verbatim.
///
/// With an all-tests selection and default options the emitted command is
/// byte-identical to the legacy <c>["dotnet","test","--no-build"]</c> path.
/// </summary>
public sealed class DotnetTestAuditor : IAuditor, ITestRunnerAuditor, IShellAuditorArgvProvider
{
    private readonly DotnetTestAuditorOptions _opts;
    private readonly Func<TestRunOptions> _runOptions;
    private readonly DotnetTestCommandResultClassifier _classifier = new();

    public DotnetTestAuditor(DotnetTestAuditorOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (opts.BaseArgv.Count == 0)
            throw new ArgumentException("BaseArgv must be non-empty", nameof(opts));
        _opts = opts;
        _runOptions = opts.RunOptionsAccessor ?? (static () => TestRunOptions.Default);
    }

    public string Name => _opts.Name;
    public string Kind => "shell";
    public AuditCapabilities Required => AuditCapabilities.None;
    public bool CanShortCircuitOnBlockingFinding => _opts.CanShortCircuitOnBlockingFinding;
    public AuditorRole Role => _opts.Role;
    public BuildTestGateEvidence BuildTestGateEvidence => _opts.Role == AuditorRole.BuildTestGate
        ? _opts.BuildTestGateEvidence
        : BuildTestGateEvidence.None;

    public TestSuiteDescriptor TestSuite =>
        new(TestFramework.DotnetTest, [.. _opts.BaseArgv, "--list-tests"]);

    public IAuditResultClassifier ResultClassifier => _classifier;

    public TestRunOptions CurrentRunOptions => _runOptions();

    /// <summary>
    /// The argv this auditor invokes for a full test run under the current
    /// (hot-reloadable) options. Exposed so the work-phase prompt builder can
    /// advise the agent to run the same command before committing.
    /// </summary>
    public IReadOnlyList<string> Argv => BuildInvocation(TestSelection.All, CurrentRunOptions);

    public IReadOnlyList<string> BuildInvocation(TestSelection selection, TestRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);

        var argv = new List<string>(_opts.BaseArgv);

        if (!selection.IsAll)
        {
            argv.Add("--filter");
            argv.Add(BuildFilterExpression(selection.Filters));
        }

        if (options.BlameHangTimeout is { } hang && hang > TimeSpan.Zero)
        {
            argv.Add("--blame-hang");
            argv.Add("--blame-hang-timeout");
            argv.Add(FormatHangTimeout(hang));
        }

        return argv;
    }

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        // Delegate the run to a ShellCommandAuditor built from the current
        // invocation so the tool-presence probe, missing-tool handling and
        // result classification stay identical to the generic shell path.
        var inner = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = _opts.Name,
            Argv = BuildInvocation(TestSelection.All, CurrentRunOptions),
            ResultClassifier = _classifier,
            CanShortCircuitOnBlockingFinding = _opts.CanShortCircuitOnBlockingFinding,
            Role = _opts.Role,
            BuildTestGateEvidence = _opts.BuildTestGateEvidence,
            SelfHealNuGetHome = _opts.SelfHealNuGetHome,
        });
        return inner.RunAsync(sandbox, workingDirectory, context, ct);
    }

    /// <summary>
    /// Maps a set of selected tests to a <c>dotnet test --filter</c> expression.
    /// Bare names are matched by fully-qualified name; entries that already carry
    /// an <c>=</c> or <c>~</c> operator are passed through unchanged so a selector
    /// can supply raw expressions (this covers <c>!=</c> too, since it contains
    /// <c>=</c>). Multiple entries are OR-joined.
    /// </summary>
    private static string BuildFilterExpression(IReadOnlyList<string> filters)
        => string.Join("|", filters.Select(f =>
            f.Contains('=', StringComparison.Ordinal) || f.Contains('~', StringComparison.Ordinal)
                ? f
                : $"FullyQualifiedName={f}"));

    /// <summary>
    /// Formats a hang timeout for <c>--blame-hang-timeout</c>, which accepts a
    /// number suffixed with a unit. Whole-second values are emitted as <c>s</c>;
    /// any non-whole-second value (including sub-second) falls back to <c>ms</c>.
    /// </summary>
    private static string FormatHangTimeout(TimeSpan timeout)
    {
        var totalMs = (long)timeout.TotalMilliseconds;
        return totalMs % 1000 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{totalMs / 1000}s")
            : string.Create(CultureInfo.InvariantCulture, $"{totalMs}ms");
    }
}

/// <summary>
/// Construction inputs for <see cref="DotnetTestAuditor"/>. <see cref="BaseArgv"/>
/// is the framework command the preset supplies (e.g.
/// <c>["dotnet","test","--no-build"]</c>); selection filters and blame-hang args
/// are layered on by <see cref="DotnetTestAuditor.BuildInvocation"/>.
/// </summary>
public sealed record DotnetTestAuditorOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> BaseArgv { get; init; }
    public bool CanShortCircuitOnBlockingFinding { get; init; }
    public AuditorRole Role { get; init; } = AuditorRole.None;
    public BuildTestGateEvidence BuildTestGateEvidence { get; init; } = BuildTestGateEvidence.None;

    /// <summary>
    /// Forwarded to the delegated <see cref="ShellCommandAuditor"/> so a
    /// <c>dotnet test</c> run self-heals a root-owned <c>~/.nuget</c>. Off by
    /// default; see <see cref="ShellCommandAuditorOptions.SelfHealNuGetHome"/>.
    /// </summary>
    public bool SelfHealNuGetHome { get; init; }

    /// <summary>
    /// Live accessor for hot-reloadable run options (blame-hang / idle-timeout).
    /// Null defaults to <see cref="TestRunOptions.Default"/>, which keeps the
    /// emitted command byte-identical to the legacy path.
    /// </summary>
    public Func<TestRunOptions>? RunOptionsAccessor { get; init; }
}
