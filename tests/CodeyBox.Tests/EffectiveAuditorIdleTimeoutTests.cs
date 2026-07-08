using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Covers <see cref="PipelineRunner.ResolveEffectiveAuditorIdleTimeout"/> — the
/// branch logic that decides whether a single auditor run gets the global
/// <see cref="PipelineTuningOptions.AuditorIdleTimeout"/> or a longer
/// test-specific window declared via
/// <see cref="ITestRunnerAuditor.CurrentRunOptions"/>. The real
/// <c>csharp:test-pass</c> auditor reaches the pipeline wrapped in an
/// <see cref="ITestRunnerAuditorProvider"/>, so the provider branch is the one
/// that must fire for the feature to work end-to-end.
/// </summary>
public sealed class EffectiveAuditorIdleTimeoutTests
{
    private static readonly TimeSpan Global = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void NonTestRunnerAuditor_UsesGlobalTimeout()
    {
        var auditor = new NotATestRunnerAuditor();

        var effective = PipelineRunner.ResolveEffectiveAuditorIdleTimeout(auditor, Global);

        Assert.Equal(Global, effective);
    }

    [Fact]
    public void DirectTestRunner_WithPositiveOverride_UsesOverride()
    {
        var overrideWindow = TimeSpan.FromMinutes(20);
        var auditor = TestRunner(new TestRunOptions { IdleTimeout = overrideWindow });

        var effective = PipelineRunner.ResolveEffectiveAuditorIdleTimeout(auditor, Global);

        Assert.Equal(overrideWindow, effective);
    }

    [Fact]
    public void DirectTestRunner_WithNullOverride_FallsBackToGlobal()
    {
        var auditor = TestRunner(new TestRunOptions { IdleTimeout = null });

        var effective = PipelineRunner.ResolveEffectiveAuditorIdleTimeout(auditor, Global);

        Assert.Equal(Global, effective);
    }

    [Fact]
    public void DirectTestRunner_WithZeroOverride_FallsBackToGlobal()
    {
        // The '> TimeSpan.Zero' guard: a zero override is not a valid window and
        // must fall back rather than pinning the idle timeout to zero.
        var auditor = TestRunner(new TestRunOptions { IdleTimeout = TimeSpan.Zero });

        var effective = PipelineRunner.ResolveEffectiveAuditorIdleTimeout(auditor, Global);

        Assert.Equal(Global, effective);
    }

    [Fact]
    public void WrappedTestRunner_WithPositiveOverride_UsesOverride()
    {
        // The real csharp:test-pass path: the pipeline sees a wrapper that
        // exposes the inner test runner via ITestRunnerAuditorProvider.
        var overrideWindow = TimeSpan.FromMinutes(20);
        var wrapper = new ProviderWrapper(TestRunner(new TestRunOptions { IdleTimeout = overrideWindow }));

        var effective = PipelineRunner.ResolveEffectiveAuditorIdleTimeout(wrapper, Global);

        Assert.Equal(overrideWindow, effective);
    }

    [Fact]
    public void WrapperWithNoInnerTestRunner_UsesGlobalTimeout()
    {
        var wrapper = new ProviderWrapper(null);

        var effective = PipelineRunner.ResolveEffectiveAuditorIdleTimeout(wrapper, Global);

        Assert.Equal(Global, effective);
    }

    private static DotnetTestAuditor TestRunner(TestRunOptions options) =>
        new(new DotnetTestAuditorOptions
        {
            Name = "csharp:test-pass",
            BaseArgv = ["dotnet", "test", "--no-build"],
            RunOptionsAccessor = () => options,
        });

    private sealed class NotATestRunnerAuditor : IAuditor
    {
        public string Name => "quality:something";
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.None;
        public bool CanShortCircuitOnBlockingFinding => false;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => throw new NotSupportedException("resolution inspects interfaces only");
    }

    private sealed class ProviderWrapper(ITestRunnerAuditor? inner) : IAuditor, ITestRunnerAuditorProvider
    {
        public ITestRunnerAuditor? TestRunner { get; } = inner;
        public string Name => "csharp:test-pass";
        public string Kind => "shell";
        public AuditCapabilities Required => AuditCapabilities.None;
        public bool CanShortCircuitOnBlockingFinding => false;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => throw new NotSupportedException("resolution inspects interfaces only");
    }
}
