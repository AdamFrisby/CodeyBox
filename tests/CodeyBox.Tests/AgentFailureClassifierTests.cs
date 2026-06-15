using CodeyBox.Api;
using CodeyBox.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AgentFailureClassifier"/> and the default
/// <see cref="IAgentRunner.ClassifyFailure"/> implementation. Mid-iteration
/// quota fallback depends on this classification — a mis-classified work
/// failure as <see cref="AgentFailureKind.QuotaExhausted"/> would burn through
/// every class member on a task no agent can complete; the inverse would
/// silently fail items that a fallback could have rescued.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentFailureClassifierTests
{
    [Theory]
    [InlineData("[error] usage_limit reached: weekly cap")]
    [InlineData("hit your usage limit")]
    [InlineData("hit your limit")]
    [InlineData("RESOURCE_EXHAUSTED")]
    [InlineData("quota exceeded for project")]
    [InlineData("[API Error: You have exhausted your capacity on this model.]")]
    public void HardQuotaPatterns_Classified_AsHardQuota(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.HardQuota, c.QuotaFailure);
        Assert.Equal(AgentFailureClassifier.HardQuotaReason, c.Reason);
    }

    [Theory]
    [InlineData("API Error: rate_limit_exceeded")]
    [InlineData("rate limit exceeded")]
    [InlineData("status 429 too many requests")]
    [InlineData("HTTP 529")]
    [InlineData("HTTP 429")]
    [InlineData("API Error: 429")]
    [InlineData("status 529")]
    [InlineData("overloaded_error")]
    [InlineData("exceeded the rate limit")]
    public void SoftRateLimitPatterns_Classified_AsSoftRateLimit(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.SoftRateLimit, c.QuotaFailure);
        Assert.Equal(AgentFailureClassifier.SoftRateLimitReason, c.Reason);
    }

    [Theory]
    [InlineData("compile error: missing semicolon at line 42")]
    [InlineData("test failures: 3/100 assertions failed")]
    [InlineData("agent refused: cannot perform this task")]
    [InlineData("ENOENT: no such file 'foo.txt'")]
    public void NormalFailures_NotClassified_AsQuota(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.Normal, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Theory]
    [InlineData("API Error: 401 Unauthorized")]
    [InlineData("invalid_api_key supplied")]
    [InlineData("OAuth token expired; please reauthenticate")]
    public void AuthPatterns_Classified_AsAuthError(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.AuthError, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Theory]
    [InlineData("ECONNRESET while contacting api.anthropic.com")]
    [InlineData("Temporary failure in name resolution")]
    [InlineData("503 Service Unavailable")]
    [InlineData("socket hang up")]
    [InlineData("fetch failed")]
    [InlineData("request timed out while reading agent stream")]
    [InlineData("request_timeout")]
    [InlineData("Reconnecting... attempt 4")]
    [InlineData("Transport channel closed")]
    [InlineData("timeout waiting for child process to exit")]
    [InlineData("Connection timed out")]
    [InlineData("i/o timeout")]
    public void NetworkPatterns_Classified_AsTransient(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.TransientNetwork, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Theory]
    [InlineData("agent exited 127", "env: 'agy': No such file or directory")]
    [InlineData("agent exited 127", "bash: codex: command not found")]
    [InlineData("agent exited 127", "")]
    [InlineData("exit 127", "command not found")]
    public void Exit127BinaryLaunchFailures_Classified_AsInfrastructure(string summary, string stderr)
    {
        var c = AgentFailureClassifier.Classify(stderr: stderr, stdout: null, summary: summary);
        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    [Theory]
    [InlineData("agent exited 1", "bwrap: execvp agy: No such file or directory")]
    [InlineData("agent exited 1", "bwrap: execv codex: No such file or directory")]
    public void SandboxWrapperBinaryLaunchFailures_Classified_AsInfrastructure(string summary, string stderr)
    {
        var c = AgentFailureClassifier.Classify(stderr: stderr, stdout: null, summary: summary);
        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    [Fact]
    public void AggregateSummaryExit127Trail_DoesNotClassifySilentFinalCrash_AsInfrastructure()
    {
        var c = AgentFailureClassifier.Classify(
            stderr: null,
            stdout: null,
            summary: "agentic conflict resolution failed: agent exited 2 (attempts: " +
                     "codex#1(agent failed: agent exited 127; stderr: env: 'codex': No such file or directory); " +
                     "codex#2(agent failed: agent exited 2; stderr: ))");

        Assert.NotEqual(AgentFailureKind.Infrastructure, c.Kind);
    }

    [Fact]
    public void Exit127BinaryLaunchFailure_InStdout_Classified_AsInfrastructure()
    {
        var c = AgentFailureClassifier.Classify(
            stderr: null,
            stdout: "bash: codex: command not found",
            summary: "agent exited 127");

        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    // Realistic non-binary filesystem ENOENT shapes that the broad
    // "No such file or directory" pattern used to swallow. The Node.js fs
    // syscall message and the GNU open-file ENOENT both carry the directory
    // suffix verbatim, so a regression that re-introduced the broad pattern
    // would silently flip a repo-level file-missing error into an
    // infrastructure signal, hiding it from the work-item failure path.
    [Theory]
    [InlineData("ENOENT: no such file or directory, open 'foo.txt'")]
    [InlineData("Error: ENOENT: no such file or directory, scandir '/work/src/missing'")]
    [InlineData("fopen('foo.txt'): No such file or directory")]
    public void Exit127NonBinaryFailure_RemainsNormal(string stderr)
    {
        var c = AgentFailureClassifier.Classify(
            stderr: stderr,
            stdout: null,
            summary: "agent exited 127");

        Assert.Equal(AgentFailureKind.Normal, c.Kind);
    }

    // POSIX /bin/sh emits "1: <name>: not found" rather than the bash
    // "command not found" shape. The classifier must still catch this as a
    // binary-launch failure (the sandbox is missing the agent binary) without
    // matching a generic "Not Found" HTTP body.
    [Theory]
    [InlineData("/bin/sh: 1: agy: not found")]
    [InlineData("sh: 1: codex: not found")]
    public void Exit127PosixShellNotFound_Classified_AsInfrastructure(string stderr)
    {
        var c = AgentFailureClassifier.Classify(
            stderr: stderr,
            stdout: null,
            summary: "agent exited 127");

        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    [Theory]
    [InlineData("failed to materialise codex auth: exit 1")]
    [InlineData("failed to materialize cursor auth: exit 7")]
    public void MaterialisationFailures_Classified_AsInfrastructure(string summary)
    {
        var c = AgentFailureClassifier.Classify(stderr: "permission denied", stdout: null, summary: summary);
        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    [Fact]
    public void PublicEnumValues_AreStable_ForPluginSdkCompatibility()
    {
        Assert.Equal(0, (int)AgentFailureKind.Normal);
        Assert.Equal(1, (int)AgentFailureKind.QuotaExhausted);
        Assert.Equal(2, (int)AgentFailureKind.TransientNetwork);
        Assert.Equal(3, (int)AgentFailureKind.AuthError);
        Assert.Equal(4, (int)AgentFailureKind.Unknown);
        Assert.Equal(5, (int)AgentFailureKind.Infrastructure);
    }

    [Theory]
    [InlineData("""{"type":"turn.failed","error":"request\u0020timed\u0020out while reading stream"}""")]
    [InlineData("""{"type":"turn.failed","error":{"message":"request\u005ftimeout"}}""")]
    [InlineData("""{"type":"turn.failed","result":{"error":{"message":"Transport\u0020channel\u0020closed"}}}""")]
    public void TurnFailed_WithStructuredTransientMessage_Classified_AsTransient(string payload)
    {
        Assert.DoesNotContain("request timed out", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("request_timeout", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transport channel closed", payload, StringComparison.OrdinalIgnoreCase);

        var c = AgentFailureClassifier.Classify(stderr: null, stdout: payload);

        Assert.Equal(AgentFailureKind.TransientNetwork, c.Kind);
    }

    [Fact]
    public void TurnFailed_WithBareStructuredTimeout_NotClassified_AsTransient()
    {
        var c = AgentFailureClassifier.Classify(
            stderr: null,
            stdout: """{"type":"turn.failed","error":{"message":"timeout"}}""");

        Assert.Equal(AgentFailureKind.Normal, c.Kind);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("Timeout")]
    [InlineData("build timeout after 10 minutes")]
    public void BareTimeout_NotClassified_AsTransient(string snippet)
    {
        var c = AgentFailureClassifier.Classify(stderr: snippet);
        Assert.Equal(AgentFailureKind.Normal, c.Kind);
    }

    [Fact]
    public void SummaryOnlyTransientNetworkPattern_Classified_AsTransient()
    {
        var c = AgentFailureClassifier.Classify(
            stderr: null,
            stdout: null,
            summary: "agent stream failed: request timed out while waiting for provider");

        Assert.Equal(AgentFailureKind.TransientNetwork, c.Kind);
    }

    [Fact]
    public void AdditionalTransientNetworkPatterns_AreOperatorTunable()
    {
        try
        {
            AgentFailureClassifier.SetAdditionalTransientNetworkPatterns(["vendor transport marker"]);

            var c = AgentFailureClassifier.Classify(stderr: "fatal: vendor transport marker");

            Assert.Equal(AgentFailureKind.TransientNetwork, c.Kind);
        }
        finally
        {
            AgentFailureClassifier.SetAdditionalTransientNetworkPatterns(null);
        }
    }

    [Fact]
    public void ProgramWiresTransientNetworkFailurePatternsFromConfigAndReload()
    {
        var root = Directory.CreateTempSubdirectory("codeybox-transient-patterns-").FullName;
        var source = new ReloadableMemorySource
        {
            Data = BuildProgramConfig(root, "operator initial transport marker"),
        };

        try
        {
            AgentFailureClassifier.SetAdditionalTransientNetworkPatterns(null);
            using var factory = new TransientPatternWiringFactory(source);
            var monitor = factory.Services.GetRequiredService<IOptionsMonitor<CodeyBoxOptions>>();

            Assert.Contains(
                "operator initial transport marker",
                monitor.CurrentValue.TransientNetworkFailurePatterns);
            Assert.Equal(
                AgentFailureKind.TransientNetwork,
                AgentFailureClassifier.Classify(stderr: "fatal: operator initial transport marker").Kind);
            Assert.Equal(
                AgentFailureKind.Normal,
                AgentFailureClassifier.Classify(stderr: "fatal: operator reloaded transport marker").Kind);

            source.TriggerReload(BuildProgramConfig(root, "operator reloaded transport marker"));
            Assert.Contains(
                "operator reloaded transport marker",
                monitor.CurrentValue.TransientNetworkFailurePatterns);
            Assert.Equal(
                AgentFailureKind.TransientNetwork,
                AgentFailureClassifier.Classify(stderr: "fatal: operator reloaded transport marker").Kind);
            Assert.Equal(
                AgentFailureKind.Normal,
                AgentFailureClassifier.Classify(stderr: "fatal: operator initial transport marker").Kind);

            var rejected = BuildProgramConfig(root, "operator rejected transport marker");
            rejected["CodeyBox:StateDatabasePath"] = Path.Combine(root, "different-state.db");
            var reloadException = Record.Exception(() => source.TriggerReload(rejected));
            var currentValueException = Record.Exception(() => _ = monitor.CurrentValue);

            Assert.True(
                reloadException is not null || currentValueException is not null,
                "invalid reload should be rejected by options validation");
            Assert.Contains(
                "operator reloaded transport marker",
                monitor.CurrentValue.TransientNetworkFailurePatterns);
            Assert.Equal(
                AgentFailureKind.TransientNetwork,
                AgentFailureClassifier.Classify(stderr: "fatal: operator reloaded transport marker").Kind);
            Assert.Equal(
                AgentFailureKind.Normal,
                AgentFailureClassifier.Classify(stderr: "fatal: operator rejected transport marker").Kind);
        }
        finally
        {
            AgentFailureClassifier.SetAdditionalTransientNetworkPatterns(null);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Quota_BeatsNetwork_WhenBothPatternsPresent()
    {
        // A 429-with-ECONNRESET-tail must classify as quota — falling back to
        // quota/rate handling is still correct even when the native session
        // resume loop later chooses to spend its bounded soft-rate retry.
        var c = AgentFailureClassifier.Classify(
            stderr: "API rate_limit_exceeded\nECONNRESET while retrying");
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    [Fact]
    public void QuotaClassification_SeparatesHardQuotaFromSoftRateLimitReason()
    {
        var hard = AgentFailureClassifier.Classify(stderr: "usage_limit reached");
        Assert.Equal(AgentFailureKind.QuotaExhausted, hard.Kind);
        Assert.Equal(AgentFailureClassifier.HardQuotaReason, hard.Reason);
        Assert.Equal(AgentQuotaFailureKind.HardQuota, hard.QuotaFailure);

        var soft = AgentFailureClassifier.Classify(stderr: "API Error: 429 rate_limit_exceeded");
        Assert.Equal(AgentFailureKind.QuotaExhausted, soft.Kind);
        Assert.Equal(AgentFailureClassifier.SoftRateLimitReason, soft.Reason);
        Assert.Equal(AgentQuotaFailureKind.SoftRateLimit, soft.QuotaFailure);
    }

    [Fact]
    public void Quota_BeatsAuth_WhenBothPatternsPresent()
    {
        // Defence against a re-ordering bug: a 429 with an "invalid_api_key"
        // tail must still classify as quota. Reversing the check order would
        // silently misclassify quota events as auth failures, sending operators
        // to rotate credentials rather than waiting for the quota window.
        var c = AgentFailureClassifier.Classify(
            stderr: "API Error: rate_limit_exceeded\nfollow-up: invalid_api_key reported");
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    [Fact]
    public void Auth_BeatsNetwork_WhenBothPatternsPresent()
    {
        var c = AgentFailureClassifier.Classify(
            stderr: "API Error: 401 Unauthorized; subsequent ECONNRESET");
        Assert.Equal(AgentFailureKind.AuthError, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Fact]
    public void EmptyOutput_ClassifiedAsUnknown()
    {
        var c = AgentFailureClassifier.Classify(stderr: null, stdout: null);
        Assert.Equal(AgentFailureKind.Unknown, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Fact]
    public void DefaultClassifyFailure_OnSuccessResult_ReturnsNormal()
    {
        IAgentRunner runner = new ProbeOnlyRunner();
        var c = runner.ClassifyFailure(new AgentResult(true, "ok", "", null));
        Assert.Equal(AgentFailureKind.Normal, c.Kind);
        Assert.Equal(AgentQuotaFailureKind.None, c.QuotaFailure);
    }

    [Fact]
    public void DefaultClassifyFailure_DelegatesToSharedClassifier()
    {
        IAgentRunner runner = new ProbeOnlyRunner();
        var c = runner.ClassifyFailure(new AgentResult(false, "exit 1", "", "RESOURCE_EXHAUSTED"));
        Assert.Equal(AgentFailureKind.QuotaExhausted, c.Kind);
    }

    [Fact]
    public void DefaultClassifyFailure_PassesSummaryToSharedClassifier()
    {
        IAgentRunner runner = new ProbeOnlyRunner();
        var c = runner.ClassifyFailure(new AgentResult(false, "agent exited 127", "", ""));
        Assert.Equal(AgentFailureKind.Infrastructure, c.Kind);
    }

    private sealed class ProbeOnlyRunner : IAgentRunner
    {
        public AgentKind Kind => new("probe");
        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => throw new NotSupportedException("test fixture only");
    }

    private static Dictionary<string, string?> BuildProgramConfig(string root, string pattern) => new()
    {
        ["CodeyBox:DangerouslyDisableAuth"] = "true",
        ["CodeyBox:StateDatabasePath"] = Path.Combine(root, "state.db"),
        ["CodeyBox:GitRootDirectory"] = Path.Combine(root, "git"),
        ["CodeyBox:AuditLog:Path"] = Path.Combine(root, "logs", "api-.json"),
        ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(root, "logs", "audit-.json"),
        ["CodeyBox:AgentStreams:Path"] = Path.Combine(root, "agent-streams"),
        ["CodeyBox:TransientNetworkFailurePatterns:0"] = pattern,
    };

    private sealed class TransientPatternWiringFactory : WebApplicationFactory<Program>
    {
        private readonly ReloadableMemorySource _source;

        public TransientPatternWiringFactory(ReloadableMemorySource source) => _source = source;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                cfg.Add(_source);
            });
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        }
    }

    private sealed class ReloadableMemorySource : IConfigurationSource
    {
        public Dictionary<string, string?> Data { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public ReloadableMemoryProvider? Provider { get; private set; }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            Provider = new ReloadableMemoryProvider(this);
            return Provider;
        }

        public void TriggerReload(Dictionary<string, string?> next)
        {
            Data = new Dictionary<string, string?>(next, StringComparer.OrdinalIgnoreCase);
            Provider!.ReloadFromSource();
        }
    }

    private sealed class ReloadableMemoryProvider : ConfigurationProvider
    {
        private readonly ReloadableMemorySource _source;

        public ReloadableMemoryProvider(ReloadableMemorySource source)
        {
            _source = source;
            ReloadFromSource();
        }

        public override void Load() { }

        public void ReloadFromSource()
        {
            Data = new Dictionary<string, string?>(_source.Data, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }
}
