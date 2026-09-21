using System.Globalization;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.DotnetTestRunnerPlugin;

/// <summary>
/// Plugin entry point for the bundled <c>dotnet test</c> runner. A thin,
/// parameterless-constructible <see cref="ITestRunnerAuditor"/> over the shared
/// <see cref="DotnetTestAuditor"/> implementation, so the plugin loader (which
/// registers entry types without constructor arguments) can load this package
/// while the host keeps default-registering the implementation directly with
/// live hot-reloadable options.
///
/// <para>Do NOT add this plugin id to the allowlist of a host that already
/// default-registers the dotnet runner (this repository's host does): the
/// discovered copy would join the audit panel as a second test gate alongside
/// the default one. The id exists so downstream hosts can load this package as
/// an external plugin instead of referencing it.</para>
///
/// <para>Run options are static per load (not hot-reloadable): the optional
/// <c>BlameHangTimeout</c> / <c>AuditorIdleTimeout</c> scoped-config values are
/// read once in <see cref="InitializeAsync"/>. An invalid value logs a warning
/// and keeps <see cref="TestRunOptions.Default"/> — a bad config value must
/// never break plugin load, mirroring the auditor's fail-safe mode fallback.
/// The host's default registration (live <c>Func{TestRunOptions}</c>) is the
/// hot-reloadable path. Test selection, failure attribution, and shadow
/// telemetry are not wired on the standalone entry, so its runs always execute
/// the full suite (the fail-safe default).</para>
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: dotnet test runner",
    minHostApiVersion: "1.3")]
public sealed class DotnetTestRunner : ITestRunnerAuditor, IShellAuditorArgvProvider, IPluginInitializer
{
    /// <summary>Allowlist / config-scope id for this plugin.</summary>
    public const string PluginId = "codeybox.dotnet-test-runner";

    /// <summary>Runner name, identical to the host's default registration.</summary>
    public const string RunnerName = "csharp:test-pass";

    /// <summary>Canonical base command the selection seam enumerates against.</summary>
    public static readonly IReadOnlyList<string> CanonicalBaseArgv = ["dotnet", "test", "--no-build"];

    private TestRunOptions _runOptions = TestRunOptions.Default;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// Parameterless for the plugin loader. Builds the canonical
    /// <c>csharp:test-pass</c> build-test-gate runner over
    /// <see cref="TestRunOptions.Default"/> until <see cref="InitializeAsync"/>
    /// optionally narrows it from scoped config.
    /// </summary>
    public DotnetTestRunner()
    {
    }

    public string Name => RunnerName;

    public string Kind => "shell";

    public AuditCapabilities Required => AuditCapabilities.None;

    public bool CanShortCircuitOnBlockingFinding => true;

    public AuditorRole Role => AuditorRole.BuildTestGate;

    public BuildTestGateEvidence BuildTestGateEvidence => BuildTestGateEvidence.Test;

    public TestSuiteDescriptor TestSuite =>
        new(TestFramework.DotnetTest, [.. CanonicalBaseArgv, "--list-tests"]);

    public IAuditResultClassifier ResultClassifier { get; } = new DotnetTestCommandResultClassifier();

    public TestRunOptions CurrentRunOptions => _runOptions;

    public IReadOnlyList<string> Argv => BuildInner().Argv;

    public IReadOnlyList<string> BuildInvocation(TestSelection selection, TestRunOptions options)
        => BuildInner().BuildInvocation(selection, options);

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => BuildInner().RunAsync(sandbox, workingDirectory, context, ct);

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        var blameHang = ParseOptionalTimeout(context.ScopedConfig["BlameHangTimeout"], "BlameHangTimeout");
        var idle = ParseOptionalTimeout(context.ScopedConfig["AuditorIdleTimeout"], "AuditorIdleTimeout");
        if (blameHang is null && idle is null)
            return Task.CompletedTask;
        _runOptions = new TestRunOptions
        {
            BlameHangTimeout = blameHang ?? _runOptions.BlameHangTimeout,
            IdleTimeout = idle ?? _runOptions.IdleTimeout,
        };
        _logger.LogInformation(
            "DotnetTestRunner initialized: blameHang={BlameHang} idleTimeout={IdleTimeout}",
            _runOptions.BlameHangTimeout, _runOptions.IdleTimeout);
        return Task.CompletedTask;
    }

    private TimeSpan? ParseOptionalTimeout(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero)
            return parsed;
        _logger.LogWarning(
            "DotnetTestRunner: ignoring invalid {Key}={Value}; keeping the current run options",
            key, value);
        return null;
    }

    private DotnetTestAuditor BuildInner()
        => new(new DotnetTestAuditorOptions
        {
            Name = RunnerName,
            BaseArgv = CanonicalBaseArgv,
            CanShortCircuitOnBlockingFinding = true,
            Role = AuditorRole.BuildTestGate,
            BuildTestGateEvidence = BuildTestGateEvidence.Test,
            RunOptionsAccessor = () => _runOptions,
            SelfHealNuGetHome = true,
        });
}
