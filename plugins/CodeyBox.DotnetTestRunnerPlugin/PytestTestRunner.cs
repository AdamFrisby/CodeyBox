using CodeyBox.Core;
using CodeyBox.PluginSdk;

namespace CodeyBox.DotnetTestRunnerPlugin;

/// <summary>
/// Reference-only stub demonstrating the <see cref="ITestRunnerAuditor"/> seam
/// for a second framework. It carries a real <see cref="TestSuiteDescriptor"/>
/// and a real <see cref="BuildInvocation"/> shape (<c>pytest</c>, narrowing via
/// <c>-k</c>), but <see cref="RunAsync"/> is intentionally unimplemented: this
/// host has no Python projects and the stub is NOT registered in any catalog or
/// DI container. A production pytest runner would replace the throw with a
/// sandboxed <c>pytest</c> invocation (mirroring how
/// <see cref="DotnetTestAuditor"/> delegates to <c>ShellCommandAuditor</c>)
/// plus a pytest output classifier — without touching <c>CodeyBox.Core</c>.
/// </summary>
[CodeyBoxPlugin(
    id: PluginId,
    displayName: "CodeyBox: pytest test runner (reference stub)",
    minHostApiVersion: "1.3")]
public sealed class PytestTestRunner : ITestRunnerAuditor
{
    /// <summary>Allowlist / config-scope id for this stub.</summary>
    public const string PluginId = "codeybox.pytest-test-runner";

    /// <inheritdoc/>
    public string Name => "pytest:test-pass";

    /// <inheritdoc/>
    public string Kind => "shell";

    /// <inheritdoc/>
    public AuditCapabilities Required => AuditCapabilities.None;

    /// <inheritdoc/>
    public TestSuiteDescriptor TestSuite =>
        new(TestFramework.Pytest, ["pytest", "--collect-only", "-q"]);

    /// <inheritdoc/>
    public IAuditResultClassifier ResultClassifier { get; } = new PytestPassthroughClassifier();

    /// <inheritdoc/>
    public TestRunOptions CurrentRunOptions => TestRunOptions.Default;

    /// <summary>
    /// Builds the <c>pytest</c> argv: bare <c>pytest</c> for the whole suite,
    /// otherwise <c>pytest -k &lt;expr&gt;</c> with the selected filters
    /// <c>or</c>-joined. pytest <c>-k</c> treats each entry as a substring
    /// match, so entries are passed through verbatim (no VSTest escaping —
    /// that guard belongs to the dotnet runner only).
    /// </summary>
    public IReadOnlyList<string> BuildInvocation(TestSelection selection, TestRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);
        if (selection.IsAll)
            return ["pytest"];
        return ["pytest", "-k", string.Join(" or ", selection.Filters)];
    }

    /// <summary>
    /// Not implemented by design: this is a reference stub, never registered.
    /// Always throws <see cref="NotSupportedException"/> rather than returning
    /// a fabricated pass so the stub can never green a gate it did not run.
    /// </summary>
    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "PytestTestRunner is a reference stub demonstrating the ITestRunnerAuditor seam; " +
            "it is not registered and cannot execute a test run.");

    /// <summary>
    /// No refinement: without a real pytest output grammar there is nothing
    /// sound to classify, so failed commands fall back to the generic
    /// command-failure result.
    /// </summary>
    private sealed class PytestPassthroughClassifier : IAuditResultClassifier
    {
        public AuditResult? ClassifyFailedCommand(AuditResultClassificationContext context) => null;
    }
}
