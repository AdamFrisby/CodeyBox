using CodeyBox.Core;
using CodeyBox.ExampleSarifAuditorPlugin;
using CodeyBox.PluginSdk.Tools;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the shared external-tool auditor base: a missing binary is
/// infrastructure naming the tool (never a pass, never a diff finding),
/// declared non-zero exits yield findings while undeclared ones are
/// infrastructure, SARIF maps to findings with rule/file/line preserved,
/// overruns are bounded, and severity mapping is applied rather than passed
/// through raw.
/// </summary>
public sealed class ExternalToolAuditorTests
{
    private const string SarifWithOneError = """
        {
          "version": "2.1.0",
          "runs": [{
            "results": [{
              "ruleId": "no-eval",
              "level": "error",
              "message": { "text": "Avoid eval()." },
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "src/app.js" },
                  "region": { "startLine": 42 }
                }
              }]
            }]
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructure_NamingTheTool()
    {
        var toolExecs = 0;
        var auditor = new TestToolAuditor(new ExternalToolAuditorOptions());
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsToolProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "not found"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("fictional-scanner", ex.Message);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task DeclaredFindingsExit_ReportsFindings_WhileUndeclaredExit_IsInfrastructure()
    {
        var options = new ExternalToolAuditorOptions
        {
            FindingsExitCodes = new HashSet<int> { 0, 1 },
        };
        var withFindings = new FakeSandbox((exec, _) => Task.FromResult(IsToolProbe(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(1, SarifWithOneError, "")));

        var result = await new TestToolAuditor(options).RunAsync(
            withFindings, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("no-eval", finding.Title);

        var couldNotRun = new FakeSandbox((exec, _) => Task.FromResult(IsToolProbe(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(2, "usage: scanner [options]", "")));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => new TestToolAuditor(options).RunAsync(
                couldNotRun, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 2", ex.Message);
    }

    [Fact]
    public async Task UnknownExitConvention_FailsLoudly_RatherThanGuessed()
    {
        var sandbox = new FakeSandbox((exec, _) => Task.FromResult(IsToolProbe(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(1, SarifWithOneError, "")));

        // Default options declare only exit 0 as findings-producing.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => new TestToolAuditor(new ExternalToolAuditorOptions()).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SarifOutput_MapsToFinding_WithRuleFileAndLine()
    {
        var sandbox = ToolReturning(0, SarifWithOneError, "");
        var result = await new TestToolAuditor(
            new ExternalToolAuditorOptions { FindingsExitCodes = new HashSet<int> { 0, 1 } }).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("test:tool", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("no-eval", finding.Title);
        Assert.Contains("Avoid eval().", finding.Description);
        Assert.Contains("fictional-scanner", finding.Description);
        Assert.Equal("src/app.js:42", finding.Location);
    }

    [Fact]
    public async Task SeverityMapping_IsApplied_RatherThanRawVocabulary()
    {
        // Under the default map "high" is an Error; this custom map demotes it.
        var mapping = new ExternalToolSeverityMapping(
            new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
            {
                ["high"] = AuditSeverity.Info,
            },
            AuditSeverity.Warning);
        var sarif = SarifWithOneError.Replace("\"error\"", "\"high\"");
        var sandbox = ToolReturning(0, sarif, "");

        var result = await new TestToolAuditor(
            new ExternalToolAuditorOptions(), mapping).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task MinimumSeverity_RuleSelection_AndPathExcludes_FilterFindings()
    {
        const string twoFindings = """
            {
              "version": "2.1.0",
              "runs": [{
                "results": [
                  {
                    "ruleId": "keep-me",
                    "level": "error",
                    "message": { "text": "Kept." },
                    "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/keep.js" }, "region": { "startLine": 1 } } }]
                  },
                  {
                    "ruleId": "drop-me",
                    "level": "error",
                    "message": { "text": "Dropped." },
                    "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "generated/drop.js" }, "region": { "startLine": 2 } } }]
                  }
                ]
              }]
            }
            """;
        var options = new ExternalToolAuditorOptions
        {
            ExcludedRules = new HashSet<string>(["drop-me"], StringComparer.Ordinal),
            ExcludePaths = ["generated/"],
        };
        var result = await new TestToolAuditor(options).RunAsync(
            ToolReturning(0, twoFindings, ""), "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("keep-me", finding.Title);
    }

    [Fact]
    public async Task ToolExceedingTimeout_IsBounded_AndReportedAsInfrastructure()
    {
        var options = new ExternalToolAuditorOptions
        {
            Timeout = TimeSpan.FromMilliseconds(200),
        };
        var sandbox = new FakeSandbox(async (exec, ct) =>
        {
            if (IsToolProbe(exec))
                return new SandboxExecResult(0, "", "");
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return new SandboxExecResult(0, "", "");
        });

        var started = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => new TestToolAuditor(options).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("fictional-scanner", ex.Message);
        Assert.Contains("timed out", ex.Message);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task UnparseableOutput_IsInfrastructure_NotAPass()
    {
        var sandbox = ToolReturning(0, "this is not sarif", "");
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => new TestToolAuditor(new ExternalToolAuditorOptions()).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task CleanRun_Passes_WithNoFindings()
    {
        var sandbox = ToolReturning(0, """{ "version": "2.1.0", "runs": [] }""", "");
        var result = await new TestToolAuditor(new ExternalToolAuditorOptions()).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExamplePlugin_RunsEndToEnd_OnSarifOutput()
    {
        var auditor = new ExampleSarifAuditor();
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsToolProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/usr/bin/example-scanner\n", ""));
            Assert.Equal("example-scanner", exec.Argv[0]);
            return Task.FromResult(new SandboxExecResult(1, SarifWithOneError.Replace("\"error\"", "\"high\""), ""));
        });

        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:example-sarif", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("src/app.js:42", finding.Location);
        Assert.Contains("no-eval", finding.Title);
    }

    [Fact]
    public void OptionsBind_ReadsOperationalKnobs_WithSafeFallbacks()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auditor:FindingsExitCodes"] = "0, 1",
                ["Auditor:MinimumSeverity"] = "warning",
                ["Auditor:IncludedRules"] = "R1, R2",
                ["Auditor:ExcludePaths"] = "generated/",
                ["Auditor:TimeoutSeconds"] = "60",
                ["Auditor:MaxFindings"] = "25",
                ["Auditor:BogusKey"] = "ignored",
            })
            .Build();

        var bound = ExternalToolAuditorOptions.Bind(config.GetSection("Auditor"));

        Assert.Equal([0, 1], bound.FindingsExitCodes.Order().ToArray());
        Assert.Equal(AuditSeverity.Warning, bound.MinimumSeverity);
        Assert.Equal(["R1", "R2"], bound.IncludedRules.Order().ToArray());
        Assert.Equal(["generated/"], bound.ExcludePaths);
        Assert.Equal(TimeSpan.FromSeconds(60), bound.Timeout);
        Assert.Equal(25, bound.MaxFindings);
    }

    [Fact]
    public void InvalidToolName_IsRejectedFailClosed()
    {
        Assert.Throws<ArgumentException>(() => ExternalToolNames.Validate("x; touch /tmp/pwned"));
        Assert.Throws<ArgumentException>(() => ExternalToolNames.Validate("/bin/sh"));
        Assert.Throws<ArgumentException>(() => ExternalToolNames.Validate(""));
        Assert.Equal("gitleaks", ExternalToolNames.Validate("gitleaks"));
    }

    private static FakeSandbox ToolReturning(int exitCode, string stdout, string stderr)
        => new((exec, _) => Task.FromResult(IsToolProbe(exec)
            ? new SandboxExecResult(0, "", "")
            : new SandboxExecResult(exitCode, stdout, stderr)));

    private static AuditContext FakeContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

    private static bool IsToolProbe(SandboxExec exec) =>
        exec.Argv.Count >= 3 &&
        exec.Argv[0] == "sh" &&
        exec.Argv[1] == "-c" &&
        exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private sealed class TestToolAuditor(
        ExternalToolAuditorOptions options,
        ExternalToolSeverityMapping? mapping = null)
        : ExternalToolAuditorBase
    {
        public override string Name => "test:tool";
        protected override string ToolName => "fictional-scanner";
        protected override IExternalToolOutputParser OutputParser { get; } = new SarifToolOutputParser();
        protected override ExternalToolSeverityMapping SeverityMapping => mapping ?? ExternalToolSeverityMapping.Default;
        protected override Func<ExternalToolAuditorOptions> OptionsAccessor => () => options;
        protected override IReadOnlyList<string> BuildToolArguments(ExternalToolAuditorOptions toolOptions) => ["scan", "."];
    }

    private sealed class FakeSandbox(Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> onExec) : ISandbox
    {
        private readonly Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> _onExec = onExec;
        public string Id => "fake";
        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return await _onExec(exec, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
