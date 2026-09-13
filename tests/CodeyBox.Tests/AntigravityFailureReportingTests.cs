using System.Text;
using CodeyBox.Agents;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies failure reporting, budget exhaustion detection, tail preservation,
/// and log line collapsing for Antigravity and orchestrator failure formatting.
/// </summary>
public sealed class AntigravityFailureReportingTests
{
    private sealed class CaptureSandbox : ISandbox
    {
        private readonly int _exitCode;
        private readonly string _stdout;
        private readonly string _stderr;

        public CaptureSandbox(int exitCode, string stdout, string stderr = "")
        {
            _exitCode = exitCode;
            _stdout = stdout;
            _stderr = stderr;
        }

        public string Id => "fake-capture-sandbox";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "mkdir")
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            if (exec.Argv.Count > 0 && exec.Argv[0] == "sh")
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            if (exec.Argv.Count > 0 && exec.Argv[0] == "tail")
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

            return Task.FromResult(new SandboxExecResult(_exitCode, _stdout, _stderr));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Run_WhenCaptureOpensWithStartupBannerAndEndsWithTimeout_ReportsTimeoutNotBanner()
    {
        const string banner = "{\"event\":\"init\",\"conversation_id\":\"test-123\",\"init\":{\"model\":\"gemini-3.8-flash-high\",\"cwd\":\"/work\",\"tools\":[]}}";
        const string timeoutBlock =
            "E0912 14:34:30 printmode.go:521] Print mode: timed out after 13454 polls (printed=478)\n" +
            "I0912 14:34:30 manager.go:756] CLI store manager shutting down\n" +
            "I0912 14:34:30 server.go:2783] Language server shutting down";

        // Build a capture opening with the startup banner, followed by noise lines, ending with timeoutBlock
        var sb = new StringBuilder();
        sb.AppendLine(banner);
        for (var i = 0; i < 50; i++)
        {
            sb.AppendLine($"ERROR: logging before google.Init: E0912 ... file_watcher.go:194] skipping empty or temp file: /work/temp_{i}.tmp");
        }
        sb.Append(timeoutBlock);

        var capture = sb.ToString();
        var sandbox = new CaptureSandbox(exitCode: 1, stdout: capture);
        var runner = new AntigravityAgentRunner
        {
            PrintTimeout = TimeSpan.FromMinutes(45),
        };

        var result = await runner.RunAsync(sandbox, "/work", "do something", credential: null);
        var failureDetail = PipelineRunner.BuildAgentFailureDetail("Agent antigravity reported failure", result);

        // Must report the timeout condition and shutdown sequence
        Assert.Contains("Print mode: timed out after 13454 polls (printed=478)", failureDetail);
        Assert.Contains("CLI store manager shutting down", failureDetail);
        Assert.Contains("Language server shutting down", failureDetail);

        // Must NOT report the opening startup banner
        Assert.DoesNotContain("{\"event\":\"init\"", failureDetail);
    }

    [Fact]
    public async Task Run_TerminatedByConfiguredPrintModeBudget_ReportsBudgetExhaustionAndNamesConfigKey()
    {
        const string capture =
            "E0912 14:34:30 printmode.go:521] Print mode: timed out after 13454 polls (printed=478)\n" +
            "I0912 14:34:30 manager.go:756] CLI store manager shutting down\n" +
            "I0912 14:34:30 server.go:2783] Language server shutting down";

        var sandbox = new CaptureSandbox(exitCode: 1, stdout: capture);
        var runner = new AntigravityAgentRunner
        {
            PrintTimeout = TimeSpan.FromMinutes(45),
        };

        var result = await runner.RunAsync(sandbox, "/work", "task", credential: null);
        var failureDetail = PipelineRunner.BuildAgentFailureDetail("Agent antigravity reported failure", result);

        // result.Summary must report budget exhaustion naming the key and configured value
        Assert.Equal("budget exhaustion: CodeyBox:Antigravity:PrintTimeoutMinutes=45m", result.Summary);

        // Formatted failure detail must name the config key and report budget exhaustion
        Assert.Contains("budget exhaustion", failureDetail);
        Assert.Contains("CodeyBox:Antigravity:PrintTimeoutMinutes=45m", failureDetail);
    }

    [Fact]
    public void ReportedFailureText_ForLargeCapture_IncludesContentFromEndOfCapture()
    {
        const string endContent = "CRITICAL_TERMINATING_DIAGNOSTIC_AT_VERY_END";
        const string startContent = "EARLY_INITIALIZATION_BANNER_AT_VERY_START";

        // Build a capture exceeding 64 KiB
        var sb = new StringBuilder();
        sb.AppendLine(startContent);
        for (var i = 0; i < 2000; i++)
        {
            sb.AppendLine($"[log line {i}] intermediate telemetry payload data block info {i}");
        }
        sb.AppendLine(endContent);

        var largeCapture = sb.ToString();
        Assert.True(Encoding.UTF8.GetByteCount(largeCapture) > 64 * 1024);

        var agentResult = new AgentResult(
            Success: false,
            Summary: "agent exited 1",
            Stdout: largeCapture,
            Stderr: string.Empty);

        var failureDetail = PipelineRunner.BuildAgentFailureDetail("Agent antigravity reported failure", agentResult);

        // Detail must contain content from the tail of the capture
        Assert.Contains(endContent, failureDetail);

        // Detail must truncate from the head and not contain early start content
        Assert.DoesNotContain(startContent, failureDetail);
        Assert.Contains("[...truncated]", failureDetail);
    }

    [Fact]
    public void Capture_ContainingManyIdenticalRepeatedLines_IsCollapsedAndPreservesTerminatingEvent()
    {
        const string repeatedLine = "ERROR: logging before google.Init: E0912 ... file_watcher.go:194] skipping empty or temp file:";
        const string terminatingEvent = "E0912 14:34:30 printmode.go:521] Print mode: timed out after 13454 polls (printed=478)";

        var sb = new StringBuilder();
        for (var i = 0; i < 567; i++)
        {
            sb.AppendLine(repeatedLine);
        }
        sb.AppendLine(terminatingEvent);

        var uncollapsed = sb.ToString();
        var collapsed = RawOutputRedactor.CollapseRepeatedLines(uncollapsed);

        // Repeated lines collapsed
        Assert.Contains("[... repeated 566 times ...]", collapsed);
        Assert.True(collapsed.Length < uncollapsed.Length / 10);

        // Terminating event remains present
        Assert.Contains(terminatingEvent, collapsed);
    }

    [Fact]
    public async Task TwoRuns_FailingForDifferentReasons_ProduceDifferentReportedFailureText()
    {
        // Run 1: fails due to print-mode timeout budget exhaustion
        const string timeoutCapture =
            "{\"event\":\"init\",\"init\":{}}\n" +
            "E0912 14:34:30 printmode.go:521] Print mode: timed out after 13454 polls (printed=478)\n" +
            "I0912 14:34:30 manager.go:756] CLI store manager shutting down\n" +
            "I0912 14:34:30 server.go:2783] Language server shutting down";

        var sandbox1 = new CaptureSandbox(exitCode: 1, stdout: timeoutCapture);
        var runner1 = new AntigravityAgentRunner { PrintTimeout = TimeSpan.FromMinutes(45) };
        var result1 = await runner1.RunAsync(sandbox1, "/work", "task 1", credential: null);
        var failureDetail1 = PipelineRunner.BuildAgentFailureDetail("Agent antigravity reported failure", result1);

        // Run 2: fails due to CLI syntax / configuration error
        const string errorCapture =
            "{\"event\":\"init\",\"init\":{}}\n" +
            "Error: unknown shorthand flag: '-z' in -z";

        var sandbox2 = new CaptureSandbox(exitCode: 1, stdout: errorCapture, stderr: "flag parse error");
        var runner2 = new AntigravityAgentRunner { PrintTimeout = TimeSpan.FromMinutes(45) };
        var result2 = await runner2.RunAsync(sandbox2, "/work", "task 2", credential: null);
        var failureDetail2 = PipelineRunner.BuildAgentFailureDetail("Agent antigravity reported failure", result2);

        // The two failure details must be distinctly different and reflect their respective causes
        Assert.NotEqual(failureDetail1, failureDetail2);
        Assert.Contains("budget exhaustion", failureDetail1);
        Assert.Contains("PrintTimeoutMinutes=45m", failureDetail1);
        Assert.DoesNotContain("budget exhaustion", failureDetail2);
        Assert.Contains("unknown shorthand flag", failureDetail2);
    }

    [Fact]
    public void RawOutputRedactor_TruncateTailToBytes_PreservesTailAndPrependsMarker()
    {
        const string text = "line 1\nline 2\nline 3\nline 4\nline 5\n";
        var maxBytes = Encoding.UTF8.GetByteCount("line 5\n") + Encoding.UTF8.GetByteCount("[...truncated]\n");

        var truncated = RawOutputRedactor.TruncateTailToBytes(text, maxBytes);

        Assert.StartsWith("[...truncated]\n", truncated, StringComparison.Ordinal);
        Assert.EndsWith("line 5\n", truncated, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= maxBytes);
    }

    [Fact]
    public void SanitizedAgentDetail_FromRawTail_CollapsesRepeatsAndRedactsSecrets()
    {
        var raw = "token: ghp_AbCdEfGhIjKlMnOpQrStUvWxYz1234567890\n" +
                  "repeated line\n" +
                  "repeated line\n" +
                  "repeated line\n" +
                  "terminal line";

        var sanitized = SanitizedAgentDetail.FromRawTail(raw);

        Assert.DoesNotContain("ghp_AbCdEfGhIjKlMnOpQrStUvWxYz1234567890", sanitized.Value);
        Assert.Contains("***", sanitized.Value);
        Assert.Contains("[... repeated 2 times ...]", sanitized.Value);
        Assert.Contains("terminal line", sanitized.Value);
    }
}
