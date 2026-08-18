using System.Text.RegularExpressions;
using CodeyBox.Agents.Cursor;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Load-bearing regression suite for the NO-fast-mode constraint on the
/// Cursor runner.
///
/// <para><b>Background.</b> Cursor's fast mode burns ~6x more credits for
/// the same output with no parallelism-relevant speed benefit. This pipeline
/// optimises for throughput, not per-iteration latency. The runner must
/// NEVER emit <c>--fast</c> or any equivalent token in its argv, under any
/// combination of input parameters. If a future Cursor release changes its
/// CLI default to fast-by-default, the runner must explicitly opt out — but
/// that is a separate proposal that must be evaluated against the 6x cost
/// penalty in writing, NOT introduced here.</para>
///
/// <para>This suite is intentionally aggressive: it sweeps the full input
/// surface area of <c>BuildInvocation</c> + <c>RunAsync</c> + <c>RunResumedAsync</c>
/// to guard against subtle refactors that re-introduce a fast-mode flag from
/// any code path.</para>
/// </summary>
public sealed class CursorAgentRunnerFastModeRegressionTests
{
    private static readonly Regex FastTokenPattern = new(
        @"fast",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public async Task RunAsync_DefaultArgs_DoesNotEmitFastFlag()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        AssertNoFastTokenInAgentArgv(sandbox);
    }

    [Theory]
    [InlineData("composer-2.5", null)]
    [InlineData("composer-3-preview", "high")]
    [InlineData(null, "low")]
    [InlineData(null, "max")]
    [InlineData("some-other-model", "xhigh")]
    public async Task RunAsync_AllParameterCombinations_DoNotEmitFastFlag(string? modelId, string? reasoning)
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "p", credential: null,
            modelId: modelId, reasoningMode: reasoning, captureStructuredStream: false);

        AssertNoFastTokenInAgentArgv(sandbox);
    }

    [Fact]
    public async Task RunAsync_WithStructuredStreamRequested_DoesNotEmitFastFlag()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunAsync(sandbox, "/work", "p", credential: null, captureStructuredStream: true);

        AssertNoFastTokenInAgentArgv(sandbox);
    }

    [Fact]
    public async Task RunResumedAsync_DoesNotEmitFastFlag()
    {
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();

        await runner.RunResumedAsync(sandbox, "/work", "p", credential: null,
            new AgentResumeContext("refs/heads/codeybox/preempt/wi"));

        AssertNoFastTokenInAgentArgv(sandbox);
    }

    [Fact]
    public async Task RunAsync_WithCredentialEnvVarsCarryingFastSentinel_DoesNotPropagateToArgv()
    {
        // Defense in depth: even if a hostile/malformed credential bundle carried
        // an env var literally named "FAST" or similar, the runner must not pick
        // it up and add it to argv (we don't loop credential keys into argv, but
        // pin that assertion against future refactors that change argv assembly).
        var sandbox = new RecordingSandbox();
        var runner = new CursorAgentRunner();
        var credential = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string>
            {
                ["FAST"] = "1",
                ["CURSOR_FAST_MODE"] = "true",
                ["CODEYBOX_CURSOR_AUTH_JSON"] = "{}",
            },
            new Dictionary<string, string>());

        await runner.RunAsync(sandbox, "/work", "p", credential);

        AssertNoFastTokenInAgentArgv(sandbox);
    }

    private static void AssertNoFastTokenInAgentArgv(RecordingSandbox sandbox)
    {
        var agentExecs = sandbox.Execs.Where(e => e.Argv.Count > 0 && e.Argv[0] == "agent").ToList();
        Assert.NotEmpty(agentExecs);
        foreach (var exec in agentExecs)
        {
            foreach (var token in exec.Argv)
            {
                Assert.False(FastTokenPattern.IsMatch(token),
                    $"Cursor runner emitted a fast-mode-shaped token '{token}' in argv. " +
                    $"NO fast mode under any flag combination, env var, or config option. " +
                    $"See docs/concepts/agents.md and CursorAgentRunner.cs class summary.");
            }
        }
    }

    private sealed class RecordingSandbox : ISandbox
    {
        public string Id => "recording-cursor-fastmode";
        public List<SandboxExec> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            return Task.FromResult(new SandboxExecResult(0, "ok", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
