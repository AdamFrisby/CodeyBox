using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the E2E replay execution infrastructure:
/// - deterministic replay (pass / fail / readiness probe failure / per-run timeout)
/// - SQLite run store (queue claim semantics, batch listing)
/// - LocalE2eExecutionPool concurrency cap (clone-per-test, no leak past dispose)
/// - dispatcher runs many replays in parallel
/// - the pool is wired to a sandbox provider; the coding-fleet WorkerPool is NEVER
///   touched (architectural separation enforced by a fake provider that asserts no
///   external collaborators reach into it)
/// </summary>
[Collection("Background service timing")]
public sealed class E2eExecutionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteWorkItemStore _itemStore;
    private readonly SqliteTestCaseStore _testCases;
    private readonly SqliteE2eRunStore _runs;

    public E2eExecutionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-e2e-{Guid.NewGuid():N}.db");
        _itemStore = new SqliteWorkItemStore(_dbPath);
        _testCases = new SqliteTestCaseStore(_dbPath);
        _runs = new SqliteE2eRunStore(_dbPath);
    }

    public void Dispose()
    {
        TestTempArtifacts.CleanupAll(
            _runs.Dispose,
            _testCases.Dispose,
            _itemStore.Dispose,
            () => TestTempArtifacts.DeleteSqliteDatabase(_dbPath));
    }

    // --------------------------------------------------------------------
    // Deterministic replay engine
    // --------------------------------------------------------------------

    [Fact]
    public async Task Replay_passes_when_every_step_and_assertion_succeeds()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(2, 1));

        var artifact = new E2eReplayArtifact
        {
            Name = "happy",
            Steps =
            [
                new E2eReplayStep { Action = "navigate", Target = "http://app.local/" },
                new E2eReplayStep { Action = "click", Selector = "#login" },
            ],
            Assertions =
            [
                new E2eReplayAssertion
                {
                    Kind = "selectorVisible",
                    Selector = "#account",
                    Description = "account panel should be visible",
                },
            ],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed);
        Assert.Null(result.FailedStepIndex);
        Assert.Equal(2, result.StepResults.Count);
        Assert.All(result.StepResults, step => Assert.True(step.Passed));
        Assert.Single(result.AssertionResults);
        Assert.True(result.AssertionResults[0].Passed);
        var exec = Assert.Single(sandbox.ExecRequests, exec => exec.Argv.Contains("node"));
        var nodeIndex = exec.Argv.ToList().IndexOf("node");
        Assert.True(nodeIndex >= 0);
        Assert.Equal("-e", exec.Argv[nodeIndex + 1]);
        Assert.Equal(1024 * 1024, exec.MaxStdoutBytes);
        Assert.Equal(1024 * 1024, exec.MaxStderrBytes);
        var sent = JsonSerializer.Deserialize<E2eReplayArtifact>(exec.Stdin!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(sent);
        Assert.Equal("#login", sent.Steps[1].Selector);
        Assert.Equal("#account", sent.Assertions[0].Selector);
    }

    [Fact]
    public async Task Replay_executes_embedded_playwright_driver_script()
    {
        await using var sandbox = new LocalNodeSandbox();
        var artifact = new E2eReplayArtifact
        {
            Steps =
            [
                new E2eReplayStep { Action = "navigate", Target = "http://app.local/" },
                new E2eReplayStep { Action = "fill", Selector = "#name", Value = "Ada" },
                new E2eReplayStep { Action = "click", Selector = "#submit", DelayAfterMs = 1 },
                new E2eReplayStep { Action = "doubleClick", Selector = "#double" },
                new E2eReplayStep { Action = "press", Selector = "#name", Value = "Enter" },
                new E2eReplayStep { Action = "select", Selector = "#choice", Value = "one" },
                new E2eReplayStep { Action = "check", Selector = "#accepted" },
                new E2eReplayStep { Action = "uncheck", Selector = "#archived" },
                new E2eReplayStep { Action = "hover", Selector = "#menu" },
                new E2eReplayStep { Action = "waitForSelector", Selector = "#ready" },
                new E2eReplayStep { Action = "wait", Value = "0" },
            ],
            Assertions =
            [
                new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#ready" },
                new E2eReplayAssertion { Kind = "selectorHidden", Selector = "#hidden" },
                new E2eReplayAssertion { Kind = "selectorTextContains", Selector = "#message", Value = "Welcome Ada" },
                new E2eReplayAssertion { Kind = "urlContains", Value = "app.local" },
                new E2eReplayAssertion { Kind = "titleContains", Value = "Dashboard" },
            ],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed, result.Summary);
        Assert.Equal(11, result.StepResults.Count);
        Assert.Equal(5, result.AssertionResults.Count);
    }

    [Fact]
    public async Task Replay_embedded_driver_reports_step_failure()
    {
        await using var sandbox = new LocalNodeSandbox();
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "click", Selector = "#missing" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("StepFailed", result.FailureKind);
        Assert.Equal(0, result.FailedStepIndex);
        Assert.Single(result.StepResults);
    }

    [Fact]
    public async Task Replay_embedded_driver_reports_assertion_failure()
    {
        await using var sandbox = new LocalNodeSandbox();
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }],
            Assertions = [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#hidden" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("AssertionFailed", result.FailureKind);
        Assert.Equal(1, result.FailedStepIndex);
        Assert.Single(result.AssertionResults);
    }

    [Fact]
    public async Task Replay_embedded_driver_fails_closed_when_allowed_origin_dns_lookup_fails()
    {
        await using var sandbox = new LocalNodeSandbox();
        var options = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            AllowedReadinessOrigins = ["http://lookup-fails.invalid"],
        });
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance, options);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReplayEgressResolutionFailed", result.FailureKind);
        Assert.Contains("DNS resolution failed", result.Summary);
    }

    [Theory]
    [InlineData("http://app.local/blocked-subresource", "request blocked")]
    [InlineData("http://app.local/redirect-off-origin", "final navigation URL origin")]
    [InlineData("http://app.local/websocket-off-origin", "websocket blocked")]
    public async Task Replay_embedded_driver_enforces_request_firewall_and_final_url(
        string target,
        string expectedDetail)
    {
        await using var sandbox = new LocalNodeSandbox();
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "navigate", Target = target }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("StepFailed", result.FailureKind);
        Assert.Contains(expectedDetail, result.Summary);
    }

    [Fact]
    public async Task Replay_installs_vm_egress_firewall_around_driver()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "203.0.113.10 STREAM app.local\n", string.Empty);
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0));
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed, result.Summary);
        Assert.Equal(["getent", "getent", "sh", "sudo", "sh"], sandbox.ExecLog.Select(static argv => argv[0]).ToArray());
        var install = sandbox.ExecRequests.Single(exec => exec.Argv.SequenceEqual(["sh", "-s"]) && exec.Stdin!.Contains("iptables -I OUTPUT", StringComparison.Ordinal));
        Assert.Contains("203.0.113.10 80", install.Stdin, StringComparison.Ordinal);
        Assert.Contains("203.0.113.10 443", install.Stdin, StringComparison.Ordinal);
        Assert.Contains("-m owner --uid-owner", install.Stdin, StringComparison.Ordinal);
        Assert.Contains("--ctstate ESTABLISHED,RELATED", install.Stdin, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_fails_closed_when_vm_egress_firewall_install_fails()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "203.0.113.10 STREAM app.local\n", string.Empty);
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0));
        var shCalls = 0;
        sandbox.Programs["sh"] = _ => ++shCalls == 1
            ? new SandboxExecResult(42, string.Empty, "iptables is required")
            : new SandboxExecResult(0, string.Empty, string.Empty);
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReplayEgressFirewallUnavailable", result.FailureKind);
        Assert.Contains("iptables is required", result.Summary);
        Assert.Equal(2, shCalls);
        Assert.DoesNotContain(sandbox.ExecLog, argv => argv.Contains("node"));
    }

    [Fact]
    public async Task Replay_embedded_driver_caps_wait_action_duration()
    {
        await using var sandbox = new LocalNodeSandbox();
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "wait", Value = (E2eReplayArtifactValidation.MaxStepDelayAfterMs + 1).ToString() }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed, result.Summary);
    }

    [Fact]
    public async Task Replay_fails_when_step_exits_nonzero()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = _ => DriverResult(FailedDriverResult("StepFailed", 0, "button missing"));

        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "click", Selector = "#missing" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal(0, result.FailedStepIndex);
        Assert.Equal("StepFailed", result.FailureKind);
        Assert.Equal(1, result.StepResults[0].ExitCode);
    }

    [Fact]
    public async Task Replay_rejects_legacy_argv_without_executing_artifact_command()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["sh"] = _ => new SandboxExecResult(0, "should not run\n", string.Empty);

        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Argv = ["sh", "-c", "touch /tmp/pwned"] }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("UnsupportedLegacyArgv", result.FailureKind);
        Assert.Empty(sandbox.ExecLog);
    }

    [Fact]
    public async Task Replay_reports_readiness_failure_distinctly_from_assertion_failure()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["curl"] = _ => new SandboxExecResult(7, string.Empty, "could not connect\n");
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "203.0.113.10 STREAM app.local\n", string.Empty);
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app.local/healthz", MaxAttempts = 2, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/items" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReadinessProbe", result.FailureKind);
        Assert.Empty(result.StepResults);
    }

    [Fact]
    public async Task Replay_readiness_retry_success_proceeds_to_driver()
    {
        var sandbox = new FakeSandbox();
        var curlAttempts = 0;
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "203.0.113.10 STREAM app.local\n", string.Empty);
        sandbox.Programs["curl"] = _ => ++curlAttempts == 1
            ? new SandboxExecResult(7, string.Empty, "not yet\n")
            : new SandboxExecResult(0, "ok\n", string.Empty);
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(0, 0));
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app.local/healthz", MaxAttempts = 2, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed);
        Assert.Equal(2, curlAttempts);
        Assert.Contains(sandbox.ExecLog, argv => argv.Contains("node"));
    }

    [Fact]
    public async Task Replay_allows_readiness_url_that_resolves_loopback_for_configured_app_origin()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "127.0.0.1 STREAM app.local\n", string.Empty);
        sandbox.Programs["curl"] = _ => new SandboxExecResult(0, "ok\n", string.Empty);
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0));
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app.local/healthz", MaxAttempts = 1, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed);
        Assert.Contains(sandbox.ExecLog, argv => argv[0] == "curl");
        Assert.Contains(sandbox.ExecLog, argv => argv.Contains("node"));
        var curl = sandbox.ExecRequests.Single(exec => exec.Argv[0] == "curl");
        Assert.Contains("app.local:80:127.0.0.1", curl.Argv);
    }

    [Fact]
    public async Task Replay_rejects_readiness_only_artifact_before_probe_runs()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "127.0.0.1 STREAM app.local\n", string.Empty);
        sandbox.Programs["curl"] = _ => new SandboxExecResult(0, "ok\n", string.Empty);
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app.local/healthz", MaxAttempts = 1, DelayMs = 0 },
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("EmptyArtifact", result.FailureKind);
        Assert.Empty(sandbox.ExecLog);
    }

    [Fact]
    public async Task Replay_rejects_readiness_url_that_resolves_metadata_address()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "169.254.169.254 STREAM app.local\n", string.Empty);
        sandbox.Programs["curl"] = _ => new SandboxExecResult(0, "should not run\n", string.Empty);
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app.local/healthz", MaxAttempts = 1, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReadinessUrlRejected", result.FailureKind);
        Assert.DoesNotContain(sandbox.ExecLog, argv => argv[0] == "curl");
    }

    [Fact]
    public async Task Replay_rejects_allowed_origin_that_resolves_metadata_address()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "169.254.169.254 STREAM metadata.local\n", string.Empty);
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0));
        var options = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            AllowedReadinessOrigins = ["http://metadata.local"],
        });
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance, options);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReplayEgressOriginRejected", result.FailureKind);
        Assert.Contains("metadata", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sandbox.ExecLog, argv => argv.Contains("node"));
    }

    [Fact]
    public async Task Replay_reports_readiness_dns_exec_exception()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => throw new InvalidOperationException("dns command failed");
        sandbox.Programs["curl"] = _ => new SandboxExecResult(0, "should not run\n", string.Empty);
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app.local/healthz", MaxAttempts = 1, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReadinessProbe", result.FailureKind);
        Assert.Contains("DNS resolution failed", result.Summary);
        Assert.DoesNotContain(sandbox.ExecLog, argv => argv[0] == "curl");
    }

    [Fact]
    public async Task Replay_reports_readiness_dns_nonzero_exit()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(2, string.Empty, "no such host");
        sandbox.Programs["curl"] = _ => new SandboxExecResult(0, "should not run\n", string.Empty);
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app.local/healthz", MaxAttempts = 1, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReadinessProbe", result.FailureKind);
        Assert.Contains("DNS resolution failed", result.Summary);
        Assert.DoesNotContain(sandbox.ExecLog, argv => argv[0] == "curl");
    }

    [Fact]
    public async Task Replay_reports_readiness_dns_without_usable_addresses()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "not-an-ip STREAM app.local\n", string.Empty);
        sandbox.Programs["curl"] = _ => new SandboxExecResult(0, "should not run\n", string.Empty);
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app.local/healthz", MaxAttempts = 1, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReadinessProbe", result.FailureKind);
        Assert.Contains("no usable addresses", result.Summary);
        Assert.DoesNotContain(sandbox.ExecLog, argv => argv[0] == "curl");
    }

    [Fact]
    public async Task Replay_reports_readiness_curl_exec_exception()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "203.0.113.10 STREAM app.local\n", string.Empty);
        sandbox.Programs["curl"] = _ => throw new InvalidOperationException("curl command failed");
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://app.local/healthz", MaxAttempts = 1, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReadinessProbe", result.FailureKind);
        Assert.Contains("last exit -1", result.Summary);
    }

    [Fact]
    public async Task Replay_rejects_navigation_outside_configured_app_origins_before_driver_runs()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0));
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://evil.local/" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("NavigationUrlRejected", result.FailureKind);
        Assert.DoesNotContain(sandbox.ExecLog, argv => argv.Contains("node"));
    }

    [Fact]
    public async Task Replay_rejects_readiness_url_with_userinfo_before_probe_runs()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "203.0.113.10 STREAM app.local\n", string.Empty);
        sandbox.Programs["curl"] = _ => new SandboxExecResult(0, "should not run\n", string.Empty);
        var artifact = new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://user@app.local/healthz", MaxAttempts = 1, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReadinessUrlRejected", result.FailureKind);
        Assert.Contains("userinfo", result.Summary);
        Assert.Empty(sandbox.ExecLog);
    }

    [Fact]
    public async Task Replay_rejects_navigation_url_with_userinfo_before_driver_runs()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0));
        var artifact = new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://user@app.local/" }],
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("NavigationUrlRejected", result.FailureKind);
        Assert.Contains("userinfo", result.Summary);
        Assert.DoesNotContain(sandbox.ExecLog, argv => argv.Contains("node"));
    }

    [Fact]
    public async Task Replay_custom_readiness_allowlist_allows_matching_origin_and_rejects_others_before_dns()
    {
        var options = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            AllowedReadinessOrigins = ["http://custom.local:8080"],
        });
        var allowedSandbox = new FakeSandbox();
        allowedSandbox.Programs["getent"] = _ => new SandboxExecResult(0, "10.42.0.5 STREAM custom.local\n", string.Empty);
        allowedSandbox.Programs["curl"] = _ => new SandboxExecResult(0, "ok\n", string.Empty);
        allowedSandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0));
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance, options);

        var allowed = await runtime.ExecuteAsync(new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://custom.local:8080/healthz", MaxAttempts = 1, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        }, allowedSandbox);

        Assert.True(allowed.Passed);
        Assert.Contains(allowedSandbox.ExecLog, argv => argv[0] == "curl");
        Assert.Contains(allowedSandbox.ExecLog, argv => argv.Contains("node"));

        var rejectedSandbox = new FakeSandbox();
        rejectedSandbox.Programs["getent"] = _ => new SandboxExecResult(0, "10.42.0.6 STREAM other.local\n", string.Empty);
        rejectedSandbox.Programs["curl"] = _ => new SandboxExecResult(0, "should not run\n", string.Empty);

        var rejected = await runtime.ExecuteAsync(new E2eReplayArtifact
        {
            Readiness = new E2eReadinessProbe { Url = "http://other.local:8080/healthz", MaxAttempts = 1, DelayMs = 0 },
            Steps = [new E2eReplayStep { Action = "wait", Value = "0" }],
        }, rejectedSandbox);

        Assert.False(rejected.Passed);
        Assert.Equal("ReadinessUrlRejected", rejected.FailureKind);
        Assert.Empty(rejectedSandbox.ExecLog);
    }

    [Fact]
    public async Task Replay_reports_exec_exception_from_driver()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = _ => throw new InvalidOperationException("exec blew up");
        var artifact = new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#x" }] };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ExecException", result.FailureKind);
    }

    [Fact]
    public async Task Replay_reports_driver_output_overflow()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = _ => new SandboxExecResult(0, string.Empty, string.Empty, StdoutLimitExceeded: true);
        var artifact = new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#x" }] };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("OutputLimitExceeded", result.FailureKind);
    }

    [Theory]
    [InlineData(0, "", "", "ReplayDriverProtocolError")]
    [InlineData(0, "not-json\n", "", "ReplayDriverProtocolError")]
    [InlineData(2, "", "driver exploded", "ReplayDriverFailed")]
    [InlineData(2, "not-json\n", "driver exploded", "ReplayDriverFailed")]
    public async Task Replay_reports_invalid_driver_protocol_outputs(
        int exitCode,
        string stdout,
        string stderr,
        string expectedFailureKind)
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = _ => new SandboxExecResult(exitCode, stdout, stderr);
        var artifact = new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#x" }] };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal(expectedFailureKind, result.FailureKind);
    }

    [Fact]
    public async Task Replay_treats_nonzero_driver_exit_with_passed_json_as_driver_failure()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0), exitCode: 2);
        var artifact = new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#x" }] };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.Equal("ReplayDriverFailed", result.FailureKind);
        Assert.Equal(-1, result.FailedStepIndex);
    }

    [Fact]
    public async Task Replay_accepts_maximum_valid_step_and_assertion_result_without_small_stdout_cap()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(E2eReplayArtifactValidation.MaxSteps, E2eReplayArtifactValidation.MaxAssertions));
        var artifact = new E2eReplayArtifact
        {
            Steps = Enumerable.Range(0, E2eReplayArtifactValidation.MaxSteps)
                .Select(_ => new E2eReplayStep { Action = "wait", Value = "0" })
                .ToArray(),
            Assertions = Enumerable.Range(0, E2eReplayArtifactValidation.MaxAssertions)
                .Select(_ => new E2eReplayAssertion { Kind = "titleContains", Value = "Dashboard" })
                .ToArray(),
        };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed);
        var exec = Assert.Single(sandbox.ExecRequests, exec => exec.Argv.Contains("node"));
        Assert.Equal(1024 * 1024, exec.MaxStdoutBytes);
    }

    public static IEnumerable<object[]> InvalidArtifactCases()
    {
        yield return [new E2eReplayArtifact { Readiness = new E2eReadinessProbe { Argv = ["curl"], Url = "http://app.local/" } }, "UnsupportedLegacyArgv"];
        yield return [new E2eReplayArtifact { Readiness = new E2eReadinessProbe { Argv = null!, Url = "http://app.local/" } }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Readiness = new E2eReadinessProbe { Url = "" } }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Readiness = new E2eReadinessProbe { Url = "ftp://app.local/" } }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Readiness = new E2eReadinessProbe { Url = "http://app.local/", MaxAttempts = 0 } }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Readiness = new E2eReadinessProbe { Url = "http://app.local/", DelayMs = E2eReplayArtifactValidation.MaxReadinessDelayMs + 1 } }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Readiness = new E2eReadinessProbe { Url = "http://app.local/" } }, "EmptyArtifact"];
        yield return [new E2eReplayArtifact { Steps = null! }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Assertions = null! }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Steps = Enumerable.Range(0, E2eReplayArtifactValidation.MaxSteps + 1).Select(_ => new E2eReplayStep { Action = "click", Selector = "#x" }).ToArray() }, "ArtifactTooLarge"];
        yield return [new E2eReplayArtifact { Assertions = Enumerable.Range(0, E2eReplayArtifactValidation.MaxAssertions + 1).Select(_ => new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#x" }).ToArray() }, "ArtifactTooLarge"];
        yield return [new E2eReplayArtifact { Name = new string('x', E2eReplayArtifactValidation.MaxStringLength + 1), Steps = [new E2eReplayStep { Action = "click", Selector = "#x" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#x", Stdin = "ignored" }] }, "UnsupportedLegacyField"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#x", WorkingDirectory = "/work" }] }, "UnsupportedLegacyField"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#x", FailOnNonZeroExit = false }] }, "UnsupportedLegacyField"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#x", Argv = null! }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "bogus" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "navigate" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "fill", Selector = "#x" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "wait", DelayAfterMs = E2eReplayArtifactValidation.MaxStepDelayAfterMs + 1 }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = new string('x', E2eReplayArtifactValidation.MaxStringLength + 1) }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Argv = ["sh"], Kind = "selectorVisible", Selector = "#x" }] }, "UnsupportedLegacyArgv"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Argv = null!, Kind = "selectorVisible", Selector = "#x" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#x", ExpectExitCode = 1 }] }, "UnsupportedLegacyField"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#x", ExpectStdoutContains = "ignored" }] }, "UnsupportedLegacyField"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#x", ExpectStdoutNotContains = "ignored" }] }, "UnsupportedLegacyField"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Kind = "" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Kind = "bogus" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Kind = "selectorVisible" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Kind = "urlContains" }] }, "ArtifactSchemaError"];
        yield return [new E2eReplayArtifact { Assertions = [new E2eReplayAssertion { Kind = "titleContains", Value = new string('x', E2eReplayArtifactValidation.MaxStringLength + 1) }] }, "ArtifactSchemaError"];
    }

    [Theory]
    [MemberData(nameof(InvalidArtifactCases))]
    public void Artifact_validation_rejects_invalid_schema_branches(E2eReplayArtifact artifact, string expectedKind)
    {
        Assert.False(E2eReplayArtifactValidation.TryValidate(artifact, out var failureKind, out _));
        Assert.Equal(expectedKind, failureKind);
    }

    [Fact]
    public void Artifact_validation_accepts_supported_action_and_assertion_variants()
    {
        var artifact = new E2eReplayArtifact
        {
            Steps =
            [
                new E2eReplayStep { Action = "navigate", Target = "http://app.local/" },
                new E2eReplayStep { Action = "click", Selector = "#click" },
                new E2eReplayStep { Action = "doubleClick", Selector = "#double" },
                new E2eReplayStep { Action = "fill", Selector = "#fill", Value = "abc" },
                new E2eReplayStep { Action = "press", Selector = "#press", Value = "Enter" },
                new E2eReplayStep { Action = "select", Selector = "#select", Value = "one" },
                new E2eReplayStep { Action = "check", Selector = "#check" },
                new E2eReplayStep { Action = "uncheck", Selector = "#uncheck" },
                new E2eReplayStep { Action = "hover", Selector = "#hover" },
                new E2eReplayStep { Action = "waitForSelector", Selector = "#ready" },
                new E2eReplayStep { Action = "wait", Value = "10", DelayAfterMs = 0 },
            ],
            Assertions =
            [
                new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#ready" },
                new E2eReplayAssertion { Kind = "selectorHidden", Selector = "#hidden" },
                new E2eReplayAssertion { Kind = "selectorTextContains", Selector = "#message", Value = "ok" },
                new E2eReplayAssertion { Kind = "urlContains", Value = "app.local" },
                new E2eReplayAssertion { Kind = "titleContains", Value = "Dashboard" },
            ],
        };

        Assert.True(E2eReplayArtifactValidation.TryValidate(artifact, out var failureKind, out var detail),
            $"{failureKind}: {detail}");
    }

    [Fact]
    public void AdmissionValidator_rejects_unknown_artifact_json_fields()
    {
        var validator = new E2eReplayArtifactAdmissionValidator();
        var json = """
            {
              "steps": [
                { "action": "navigate", "target": "http://app.local/" }
              ],
              "unexpectedField": true
            }
            """;

        var accepted = validator.TryValidateJson(json, out _, out var failureKind, out var detail);

        Assert.False(accepted);
        Assert.Equal("ArtifactParseError", failureKind);
        Assert.Contains("unexpectedField", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replay_cancellation_propagates()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["node"] = ct =>
        {
            ct.WaitHandle.WaitOne();
            ct.ThrowIfCancellationRequested();
            return new SandboxExecResult(0, string.Empty, string.Empty);
        };
        var artifact = new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "click", Selector = "#slow" }] };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.ExecuteAsync(artifact, sandbox, cts.Token));
    }

    // --------------------------------------------------------------------
    // SQLite run store
    // --------------------------------------------------------------------

    [Fact]
    public async Task RunStore_round_trips_and_indexes_queue_for_dispatch()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());

        var run = new E2eRun
        {
            Id = Guid.NewGuid().ToString("N"),
            TestCaseId = tcId,
            Status = E2eRunStatus.Queued,
        };
        await _runs.CreateAsync(run);

        var fetched = await _runs.GetAsync(run.Id);
        Assert.NotNull(fetched);
        Assert.Equal(E2eRunStatus.Queued, fetched.Status);

        var claimed = await _runs.ClaimNextQueuedAsync("sandbox-A");
        Assert.NotNull(claimed);
        Assert.Equal(run.Id, claimed.Id);
        Assert.Equal(E2eRunStatus.Running, claimed.Status);
        Assert.Equal("sandbox-A", claimed.SandboxId);
        Assert.NotNull(claimed.StartedAt);

        // No second queued row → second claim returns null.
        var noClaim = await _runs.ClaimNextQueuedAsync("sandbox-B");
        Assert.Null(noClaim);

        await _runs.UpdateStatusAsync(run.Id, E2eRunStatus.Passed, null, DateTimeOffset.UtcNow, "result-json");
        var finished = await _runs.GetAsync(run.Id);
        Assert.NotNull(finished);
        Assert.Equal(E2eRunStatus.Passed, finished.Status);
        Assert.Equal("result-json", finished.Result);
    }

    [Fact]
    public async Task RunStore_terminal_update_does_not_overwrite_canceled_running_run()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var run = new E2eRun { Id = Guid.NewGuid().ToString("N"), TestCaseId = tcId, Status = E2eRunStatus.Queued };
        await _runs.CreateAsync(run);
        var claimed = await _runs.ClaimNextQueuedAsync("sandbox-A");
        Assert.NotNull(claimed);

        Assert.True(await _runs.CancelAsync(run.Id));
        Assert.False(await _runs.UpdateStatusAsync(run.Id, E2eRunStatus.Passed, null, DateTimeOffset.UtcNow, "late-pass"));

        var fetched = await _runs.GetAsync(run.Id);
        Assert.NotNull(fetched);
        Assert.Equal(E2eRunStatus.Canceled, fetched.Status);
        Assert.Null(fetched.Result);
    }

    [Fact]
    public async Task RunStore_assigns_sandbox_after_null_claim_and_requeues_running_rows_on_startup_recovery()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var queued = new E2eRun { Id = Guid.NewGuid().ToString("N"), TestCaseId = tcId, Status = E2eRunStatus.Queued };
        var staleRunning = new E2eRun
        {
            Id = Guid.NewGuid().ToString("N"),
            TestCaseId = tcId,
            Status = E2eRunStatus.Running,
            SandboxId = "old-sandbox",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        var terminal = new E2eRun { Id = Guid.NewGuid().ToString("N"), TestCaseId = tcId, Status = E2eRunStatus.Passed };
        await _runs.CreateAsync(queued);
        await _runs.CreateAsync(staleRunning);
        await _runs.CreateAsync(terminal);

        var claimed = await _runs.ClaimNextQueuedAsync(null);
        Assert.NotNull(claimed);
        Assert.Null(claimed.SandboxId);
        Assert.True(await _runs.AssignSandboxAsync(queued.Id, "assigned-sandbox"));
        var assigned = await _runs.GetAsync(queued.Id);
        Assert.NotNull(assigned);
        Assert.Equal("assigned-sandbox", assigned.SandboxId);

        var recovered = await _runs.RequeueRunningAsync(DateTimeOffset.UtcNow);

        Assert.Equal(2, recovered);
        var recoveredQueued = await _runs.GetAsync(staleRunning.Id);
        Assert.NotNull(recoveredQueued);
        Assert.Equal(E2eRunStatus.Queued, recoveredQueued.Status);
        Assert.Null(recoveredQueued.SandboxId);
        Assert.Null(recoveredQueued.StartedAt);
        var recoveredAssigned = await _runs.GetAsync(queued.Id);
        Assert.NotNull(recoveredAssigned);
        Assert.Equal(E2eRunStatus.Queued, recoveredAssigned.Status);
        Assert.Null(recoveredAssigned.SandboxId);
        var stillTerminal = await _runs.GetAsync(terminal.Id);
        Assert.NotNull(stillTerminal);
        Assert.Equal(E2eRunStatus.Passed, stillTerminal.Status);
    }

    [Fact]
    public async Task RunStore_groups_runs_by_batch_for_aggregate_reporting()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var batch = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 3; i++)
        {
            await _runs.CreateAsync(new E2eRun
            {
                Id = Guid.NewGuid().ToString("N"),
                TestCaseId = tcId,
                Status = E2eRunStatus.Queued,
                BatchId = batch,
            });
        }
        // One unrelated run that must NOT show up in the batch list.
        await _runs.CreateAsync(new E2eRun
        {
            Id = Guid.NewGuid().ToString("N"),
            TestCaseId = tcId,
            Status = E2eRunStatus.Queued,
            BatchId = "other",
        });

        var listed = new List<E2eRun>();
        await foreach (var r in _runs.ListByBatchAsync(batch)) listed.Add(r);
        Assert.Equal(3, listed.Count);
        Assert.All(listed, r => Assert.Equal(batch, r.BatchId));
    }

    [Fact]
    public async Task RunStore_batch_counts_cover_every_reported_status()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var batch = Guid.NewGuid().ToString("N");
        foreach (var status in new[]
                 {
                     E2eRunStatus.Queued,
                     E2eRunStatus.Running,
                     E2eRunStatus.Passed,
                     E2eRunStatus.Failed,
                     E2eRunStatus.Error,
                     E2eRunStatus.Canceled,
                 })
        {
            await _runs.CreateAsync(new E2eRun
            {
                Id = Guid.NewGuid().ToString("N"),
                TestCaseId = tcId,
                Status = status,
                BatchId = batch,
            });
        }

        var counts = await _runs.GetBatchCountsAsync(batch);

        Assert.NotNull(counts);
        Assert.Equal(6, counts.Total);
        Assert.Equal(1, counts.Queued);
        Assert.Equal(1, counts.Running);
        Assert.Equal(1, counts.Passed);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(1, counts.Error);
        Assert.Equal(1, counts.Canceled);
        Assert.False(counts.Complete);
    }

    [Fact]
    public async Task RunStore_bulk_create_rolls_back_entire_batch_on_write_failure()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var batch = Guid.NewGuid().ToString("N");
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<SqliteException>(() => _runs.BulkCreateAsync(
        [
            new E2eRun
            {
                Id = firstId,
                TestCaseId = tcId,
                Status = E2eRunStatus.Queued,
                BatchId = batch,
            },
            new E2eRun
            {
                Id = secondId,
                TestCaseId = "missing-test-case",
                Status = E2eRunStatus.Queued,
                BatchId = batch,
            },
        ]));

        Assert.Null(await _runs.GetAsync(firstId));
        Assert.Null(await _runs.GetBatchCountsAsync(batch));
    }

    [Fact]
    public async Task RunStore_cancel_is_a_no_op_on_terminal_runs()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var run = new E2eRun { Id = Guid.NewGuid().ToString("N"), TestCaseId = tcId, Status = E2eRunStatus.Queued };
        await _runs.CreateAsync(run);
        Assert.True(await _runs.CancelAsync(run.Id));

        // Re-cancel: already terminal, returns false.
        Assert.False(await _runs.CancelAsync(run.Id));
        var fetched = await _runs.GetAsync(run.Id);
        Assert.NotNull(fetched);
        Assert.Equal(E2eRunStatus.Canceled, fetched.Status);
    }

    // --------------------------------------------------------------------
    // Pool concurrency / clone-per-test / isolation
    // --------------------------------------------------------------------

    [Fact]
    public async Task LocalPool_caps_concurrent_leases_at_MaxConcurrent()
    {
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 2 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);

        var slot1 = await pool.LeaseAsync();
        var slot2 = await pool.LeaseAsync();
        Assert.Equal(2, pool.InFlight);

        var thirdTask = pool.LeaseAsync(); // should block until a slot is released
        Assert.False(thirdTask.IsCompleted);

        await slot1.DisposeAsync();
        var slot3 = await thirdTask;
        Assert.Equal(2, pool.InFlight);

        await slot2.DisposeAsync();
        await slot3.DisposeAsync();
        Assert.Equal(0, pool.InFlight);
        // Provider was hit once per lease — clone-per-test, NOT slot reuse.
        Assert.Equal(3, provider.CreateCount);
        // Every leased sandbox was disposed exactly once.
        Assert.True(provider.AllSandboxesDisposed);
    }

    [Fact]
    public async Task LocalPool_builds_spec_from_e2e_options_and_falls_back_to_global_image()
    {
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            MaxConcurrent = 1,
            NetworkProfile = "e2e-net",
            BaselineImageRef = "baseline-e2e",
        });
        var pool = new LocalE2eExecutionPool(
            provider,
            monitor,
            NullLogger<LocalE2eExecutionPool>.Instance,
            fallbackImageReference: () => "global-image");

        await using var slot = await pool.LeaseAsync();

        var spec = Assert.Single(provider.Specs);
        Assert.Equal("global-image", spec.ImageReference);
        Assert.Equal("baseline-e2e", spec.BaselineImageRef);
        Assert.Equal("e2e-net", spec.Network.ProfileName);
    }

    [Fact]
    public async Task LocalPool_uses_explicit_image_and_denies_network_when_profile_unset()
    {
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            MaxConcurrent = 1,
            SandboxImageReference = "e2e-image",
        });
        var pool = new LocalE2eExecutionPool(
            provider,
            monitor,
            NullLogger<LocalE2eExecutionPool>.Instance,
            fallbackImageReference: () => "global-image");

        await using var slot = await pool.LeaseAsync();

        var spec = Assert.Single(provider.Specs);
        Assert.Equal("e2e-image", spec.ImageReference);
        Assert.Equal(SandboxNetworkPolicy.Denied, spec.Network);
    }

    [Fact]
    public void LocalPool_resizes_when_options_monitor_fires_change()
    {
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);

        monitor.Set(new E2eExecutionOptions { MaxConcurrent = 3 });

        Assert.Equal(3, pool.MaxConcurrent);
    }

    [Fact]
    public async Task LocalPool_releases_gate_when_provider_throws_during_lease()
    {
        var provider = new CountingSandboxProvider { ThrowOnCreate = true };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.LeaseAsync());
        Assert.Equal(0, pool.InFlight);

        // Recover — the next lease must NOT block on the orphaned slot.
        provider.ThrowOnCreate = false;
        var slot = await pool.LeaseAsync();
        await slot.DisposeAsync();
    }

    [Fact]
    public async Task MultiHostPool_leases_across_hosts_enforces_caps_and_releases_slots()
    {
        var hostA = new CountingSandboxProvider();
        var hostB = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            MaxConcurrent = 3,
            NetworkProfile = "e2e-net",
            BaselineImageRef = "baseline-e2e",
        });
        var pool = new MultiHostE2eExecutionPool(
            [
                new E2eExecutionHost("a", hostA, 1),
                new E2eExecutionHost("b", hostB, 2),
            ],
            monitor,
            NullLogger<MultiHostE2eExecutionPool>.Instance,
            fallbackImageReference: () => "global-image");

        var slot1 = await pool.LeaseAsync();
        var slot2 = await pool.LeaseAsync();
        var slot3 = await pool.LeaseAsync();
        Assert.Equal(3, pool.InFlight);
        Assert.Equal(3, hostA.CreateCount + hostB.CreateCount);
        Assert.True(hostA.MaxConcurrentSeen <= 1);
        Assert.True(hostB.MaxConcurrentSeen <= 2);

        var fourth = pool.LeaseAsync();
        await Task.Delay(50);
        Assert.False(fourth.IsCompleted);

        await slot1.DisposeAsync();
        var slot4 = await fourth.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, pool.InFlight);

        await slot2.DisposeAsync();
        await slot3.DisposeAsync();
        await slot4.DisposeAsync();
        Assert.Equal(0, pool.InFlight);
        Assert.Equal(4, hostA.CreateCount + hostB.CreateCount);
        Assert.All(hostA.Specs.Concat(hostB.Specs), spec =>
        {
            Assert.Equal("global-image", spec.ImageReference);
            Assert.Equal("baseline-e2e", spec.BaselineImageRef);
            Assert.Equal("e2e-net", spec.Network.ProfileName);
        });
    }

    [Fact]
    public async Task MultiHostPool_releases_host_and_global_gates_when_provider_throws_during_lease()
    {
        var host = new CountingSandboxProvider { ThrowOnCreate = true };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 1 });
        var pool = new MultiHostE2eExecutionPool(
            [new E2eExecutionHost("remote-a", host, 1)],
            monitor,
            NullLogger<MultiHostE2eExecutionPool>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.LeaseAsync());
        Assert.Equal(0, pool.InFlight);

        host.ThrowOnCreate = false;
        await using var slot = await pool.LeaseAsync();
        Assert.Equal(1, pool.InFlight);
    }

    [Fact]
    public async Task CompositeLifecycleProvider_aggregates_lists_and_fans_out_dispose()
    {
        var local = new ManagedProviderDouble(
            "local",
            [new ManagedSandboxInfo("local-vm", DateTimeOffset.UtcNow, null, IsTrackedActive: false)])
        {
            ThrowOnDispose = true,
        };
        var remote = new ManagedProviderDouble(
            "remote",
            [new ManagedSandboxInfo("remote-vm", DateTimeOffset.UtcNow, null, IsTrackedActive: false)]);
        var composite = new CompositeManagedSandboxProvider([local, remote]);

        var listed = await composite.ListAllManagedAsync(CancellationToken.None);
        await composite.DisposeLeakedAsync("remote-vm", CancellationToken.None);

        Assert.Equal(["local-vm", "remote-vm"], listed.Select(vm => vm.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Empty(local.DisposedNames);
        Assert.Equal(["remote-vm"], remote.DisposedNames);
    }

    [Fact]
    public async Task CompositeLifecycleProvider_disposes_provider_scoped_snapshot_only()
    {
        var local = new ManagedProviderDouble(
            "pool",
            [new ManagedSandboxInfo("codeybox-r-collision", DateTimeOffset.UtcNow, null, IsTrackedActive: false)]);
        var remote = new ManagedProviderDouble(
            "pool",
            [new ManagedSandboxInfo("codeybox-r-collision", DateTimeOffset.UtcNow, null, IsTrackedActive: false)]);
        var composite = new CompositeManagedSandboxProvider([local, remote]);
        var listed = await composite.ListAllManagedAsync(CancellationToken.None);
        var remoteSnapshot = listed.Single(info => info.LifecycleProviderId == "pool#2");

        await composite.DisposeLeakedAsync(remoteSnapshot, CancellationToken.None);

        Assert.Empty(local.DisposedNames);
        Assert.Equal(["codeybox-r-collision"], remote.DisposedNames);
    }

    [Fact]
    public async Task CompositeLifecycleProvider_throws_when_every_list_provider_fails()
    {
        var first = new ManagedProviderDouble("first", []) { ThrowOnList = true };
        var second = new ManagedProviderDouble("second", []) { ThrowOnList = true };
        var composite = new CompositeManagedSandboxProvider([first, second]);

        await Assert.ThrowsAsync<AggregateException>(() => composite.ListAllManagedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CompositeLifecycleProvider_reports_partial_inventory_when_one_provider_fails()
    {
        var local = new ManagedProviderDouble(
            "local",
            [new ManagedSandboxInfo("local-vm", DateTimeOffset.UtcNow, null, IsTrackedActive: false)]);
        var remote = new ManagedProviderDouble("remote", []) { ThrowOnList = true };
        var composite = new CompositeManagedSandboxProvider([local, remote]);

        var inventory = await composite.ListManagedInventoryAsync(CancellationToken.None);

        var info = Assert.Single(inventory);
        Assert.Equal("local-vm", info.Name);
        Assert.False(inventory.IsComplete);
    }

    [Fact]
    public async Task CompositeLifecycleProvider_throws_when_unscoped_dispose_cannot_be_routed()
    {
        var local = new ManagedProviderDouble(
            "pool",
            [new ManagedSandboxInfo("codeybox-r-collision", DateTimeOffset.UtcNow, null, IsTrackedActive: false)]);
        var remote = new ManagedProviderDouble(
            "pool",
            [new ManagedSandboxInfo("codeybox-r-collision", DateTimeOffset.UtcNow, null, IsTrackedActive: false)]);
        var composite = new CompositeManagedSandboxProvider([local, remote]);
        await composite.ListAllManagedAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            composite.DisposeLeakedAsync("codeybox-r-collision", CancellationToken.None));
        Assert.Empty(local.DisposedNames);
        Assert.Empty(remote.DisposedNames);
    }

    [Fact]
    public async Task CompositeLifecycleProvider_surfaces_scoped_dispose_failure()
    {
        var provider = new ManagedProviderDouble(
            "remote",
            [new ManagedSandboxInfo("codeybox-r-failed", DateTimeOffset.UtcNow, null, IsTrackedActive: false)])
        {
            ThrowOnDispose = true,
        };
        var composite = new CompositeManagedSandboxProvider([provider]);
        var snapshot = Assert.Single(await composite.ListAllManagedAsync(CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            composite.DisposeLeakedAsync(snapshot, CancellationToken.None));
        Assert.Equal(["codeybox-r-failed"], provider.DisposedNames);
    }

    [Fact]
    public async Task LocalPool_does_not_reference_the_coding_fleet_WorkerPool()
    {
        // ARCHITECTURAL CONTRACT — the brief requires E2E load NEVER to compete with the
        // coding-worker fleet for sandbox slots. The pool's constructor takes only an
        // ISandboxProvider + IOptionsMonitor + ILogger. Reflection asserts there is no
        // hidden static / property dependency on WorkerPool or its options. If a future
        // refactor reintroduces a dependency on the coding fleet, this test fails and the
        // brief's hard rule is re-surfaced before the change lands.
        var deps = typeof(LocalE2eExecutionPool).GetConstructors().SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType).ToList();
        Assert.DoesNotContain(deps, t => t.Name == "WorkerPool");
        Assert.DoesNotContain(deps, t => t.Name == "WorkerPoolOptions");
        Assert.DoesNotContain(deps, t => t.Name == "IWorkerPoolOccupancy");

        // Same contract on the dispatcher.
        var dispDeps = typeof(E2eRunDispatcher).GetConstructors().SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType).ToList();
        Assert.DoesNotContain(dispDeps, t => t.Name == "WorkerPool");
        Assert.DoesNotContain(dispDeps, t => t.Name == "IWorkerPoolOccupancy");

        await Task.CompletedTask;
    }

    // --------------------------------------------------------------------
    // Dispatcher / parallelism end-to-end
    // --------------------------------------------------------------------

    [Fact]
    public async Task Dispatcher_enabled_false_does_not_claim_or_lease()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = false, MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.False(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        Assert.Equal(0, provider.CreateCount);
        var stored = await _runs.GetAsync(runId);
        Assert.NotNull(stored);
        Assert.Equal(E2eRunStatus.Queued, stored.Status);
    }

    [Fact]
    public async Task Dispatcher_idle_queue_does_not_lease_sandbox()
    {
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = true, MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.False(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task Dispatcher_runs_many_replays_concurrently_across_the_pool()
    {
        const int total = 8;
        const int max = 4;
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var batch = Guid.NewGuid().ToString("N");
        for (var i = 0; i < total; i++)
        {
            await _runs.CreateAsync(new E2eRun
            {
                Id = Guid.NewGuid().ToString("N"),
                TestCaseId = tcId,
                Status = E2eRunStatus.Queued,
                BatchId = batch,
            });
        }

        // 100ms per replay step lets us observe parallelism without making the test slow.
        var perStepDelay = TimeSpan.FromMilliseconds(100);
        var provider = new CountingSandboxProvider(perStepDelay);
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = max,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(30),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);
        var dispatcher = new E2eRunDispatcher(_runs, pool, runtime, _testCases, monitor, new E2eRunCancellationRegistry(), Admission(monitor), NullLogger<E2eRunDispatcher>.Instance);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < total; i++)
        {
            // Drive the dispatcher one step at a time until everything is in-flight or terminal.
            var dispatched = await dispatcher.TryDispatchOneAsync(CancellationToken.None);
            if (!dispatched)
            {
                await Task.Delay(20);
                i--; // retry — pool busy, no claim yet
            }
        }

        // Wait for all runs to terminalise.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var terminal = 0;
            await foreach (var r in _runs.ListByBatchAsync(batch))
            {
                if (r.Status is E2eRunStatus.Passed or E2eRunStatus.Failed or E2eRunStatus.Error or E2eRunStatus.Canceled)
                    terminal++;
            }
            if (terminal == total) break;
            await Task.Delay(25);
        }
        sw.Stop();

        var results = new List<E2eRun>();
        await foreach (var r in _runs.ListByBatchAsync(batch)) results.Add(r);
        Assert.Equal(total, results.Count);
        Assert.All(results, r => Assert.Equal(E2eRunStatus.Passed, r.Status));
        await WaitForDispatcherIdleAsync(dispatcher);

        // Parallelism proof: sequential = total * perStepDelay (~800ms). With max=4 the
        // ideal is two waves (~200ms); give it a generous ceiling so CI jitter doesn't
        // flake — but well below the sequential bound.
        var sequentialBound = total * perStepDelay.TotalMilliseconds;
        Assert.True(sw.Elapsed.TotalMilliseconds < sequentialBound,
            $"Dispatcher took {sw.Elapsed.TotalMilliseconds:F0}ms; sequential would be ~{sequentialBound:F0}ms — parallelism appears broken.");

        // Max observed in-flight on the provider must NOT exceed the configured cap.
        Assert.True(provider.MaxConcurrentSeen <= max,
            $"Observed concurrency {provider.MaxConcurrentSeen} exceeded configured cap {max}.");
    }

    [Fact]
    public async Task Dispatcher_starts_vm_clone_leases_in_parallel()
    {
        const int total = 4;
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        for (var i = 0; i < total; i++)
        {
            await _runs.CreateAsync(new E2eRun
            {
                Id = Guid.NewGuid().ToString("N"),
                TestCaseId = tcId,
                Status = E2eRunStatus.Queued,
            });
        }

        var provider = new CountingSandboxProvider { CreateDelay = TimeSpan.FromMilliseconds(250) };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = total,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        for (var i = 0; i < total; i++)
            Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (provider.MaxConcurrentSeen < total && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(total, provider.MaxConcurrentSeen);
        await WaitForDispatcherIdleAsync(dispatcher);
    }

    [Fact]
    public async Task Dispatcher_records_replay_driver_unavailable_as_error()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider { ExecResult = new SandboxExecResult(127, string.Empty, "missing driver") };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        var terminal = await WaitForRunStatusAsync(runId, E2eRunStatus.Error);
        await WaitForDispatcherIdleAsync(dispatcher);
        Assert.Contains("ReplayDriverUnavailable", terminal.Result);
    }

    [Fact]
    public async Task Dispatcher_records_deterministic_replay_failure_as_failed()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider
        {
            ExecResult = DriverResult(FailedDriverResult("StepFailed", 0, "button missing"), exitCode: 1),
        };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        var terminal = await WaitForRunStatusAsync(runId, E2eRunStatus.Failed);
        await WaitForDispatcherIdleAsync(dispatcher);
        Assert.Contains("StepFailed", terminal.Result);
    }

    [Fact]
    public async Task Dispatcher_updates_test_case_last_run_after_pass()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(new CountingSandboxProvider(), monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));
        await WaitForRunStatusAsync(runId, E2eRunStatus.Passed);
        await WaitForDispatcherIdleAsync(dispatcher);

        var testCase = await _testCases.GetAsync(tcId);
        Assert.NotNull(testCase);
        Assert.True(testCase.LastRunPassed);
        Assert.NotNull(testCase.LastRunAt);
        Assert.Contains("steps", testCase.LastRunResult);
    }

    [Fact]
    public async Task Dispatcher_updates_test_case_last_run_for_invalid_artifact()
    {
        var emptyArtifact = JsonSerializer.Serialize(new E2eReplayArtifact());
        var tcId = await SeedE2eTestCaseAsync(emptyArtifact);
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(new CountingSandboxProvider(), monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(_runs, pool, new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance), _testCases, monitor, new E2eRunCancellationRegistry(), Admission(monitor), NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));
        await WaitForRunStatusAsync(runId, E2eRunStatus.Error);
        await WaitForDispatcherIdleAsync(dispatcher);

        var testCase = await _testCases.GetAsync(tcId);
        Assert.NotNull(testCase);
        Assert.False(testCase.LastRunPassed);
        Assert.NotNull(testCase.LastRunAt);
        Assert.Contains("artifact must include", testCase.LastRunResult);
    }

    [Fact]
    public async Task Dispatcher_records_per_run_timeout_as_error()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider { BlockExecUntilCanceled = true };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromMilliseconds(50),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(_runs, pool, new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance), _testCases, monitor, new E2eRunCancellationRegistry(), Admission(monitor), NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        var terminal = await WaitForRunStatusAsync(runId, E2eRunStatus.Error);
        await WaitForDispatcherIdleAsync(dispatcher);
        Assert.Contains("PerRunTimeout", terminal.Result);
    }

    [Fact]
    public async Task Dispatcher_running_cancel_signals_active_replay_and_preserves_canceled_status()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider { BlockExecUntilCanceled = true };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(30),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var registry = new E2eRunCancellationRegistry();
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            registry,
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));
        await provider.ExecStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(registry.Cancel(runId));
        Assert.True(await _runs.CancelAsync(runId));

        var terminal = await WaitForRunStatusAsync(runId, E2eRunStatus.Canceled);
        await WaitForDispatcherIdleAsync(dispatcher);
        Assert.Equal(E2eRunStatus.Canceled, terminal.Status);
    }

    [Fact]
    public async Task Dispatcher_records_shutdown_cancellation_as_distinct_canceled_result()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider { BlockExecUntilCanceled = true };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(30),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        using var shutdown = new CancellationTokenSource();
        Assert.True(await dispatcher.TryDispatchOneAsync(shutdown.Token));
        await provider.ExecStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await shutdown.CancelAsync();

        var terminal = await WaitForRunStatusAsync(runId, E2eRunStatus.Canceled);
        await WaitForDispatcherIdleAsync(dispatcher);
        Assert.Contains("ShutdownCancel", terminal.Result);
    }

    [Fact]
    public void CancellationRegistry_duplicate_register_and_unregister_paths_are_deterministic()
    {
        var registry = new E2eRunCancellationRegistry();
        using var cts = registry.Register("run-1");

        var duplicate = Assert.Throws<InvalidOperationException>(() => registry.Register("run-1"));
        Assert.Contains("already registered", duplicate.Message);

        Assert.True(registry.Cancel("run-1"));
        Assert.True(cts.IsCancellationRequested);
        registry.Unregister("run-1", cts);

        Assert.False(registry.Cancel("run-1"));

        using var stale = new CancellationTokenSource();
        registry.Unregister("run-1", stale);
        Assert.True(stale.IsCancellationRequested is false);
    }

    [Fact]
    public async Task Dispatcher_records_error_for_artifact_with_no_steps_and_no_assertions()
    {
        var emptyArtifact = JsonSerializer.Serialize(new E2eReplayArtifact());
        var tcId = await SeedE2eTestCaseAsync(emptyArtifact);

        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });

        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);
        var dispatcher = new E2eRunDispatcher(_runs, pool, runtime, _testCases, monitor, new E2eRunCancellationRegistry(), Admission(monitor), NullLogger<E2eRunDispatcher>.Instance);

        var dispatched = await dispatcher.TryDispatchOneAsync(CancellationToken.None);
        Assert.True(dispatched);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        E2eRun? terminal = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            terminal = await _runs.GetAsync(runId);
            if (terminal is { Status: E2eRunStatus.Passed or E2eRunStatus.Failed or E2eRunStatus.Error })
                break;
            await Task.Delay(20);
        }
        Assert.NotNull(terminal);
        Assert.Equal(E2eRunStatus.Error, terminal.Status);
        await WaitForDispatcherIdleAsync(dispatcher);
        Assert.Contains("EmptyArtifact", terminal.Result);
    }

    public static IEnumerable<object?[]> DispatcherArtifactLoadFailures()
    {
        yield return [AutomationKind.Unit, MakeTrivialPassArtifact(), "WrongAutomationKind"];
        yield return [AutomationKind.E2eReplay, (string?)null, "MissingArtifact"];
        yield return [AutomationKind.E2eReplay, new string('x', E2eReplayArtifactValidation.MaxArtifactJsonBytes + 1), "ArtifactTooLarge"];
        yield return [AutomationKind.E2eReplay, "{ not-json", "ArtifactParseError"];
    }

    [Theory]
    [MemberData(nameof(DispatcherArtifactLoadFailures))]
    public async Task Dispatcher_records_terminal_errors_for_already_queued_artifact_load_failures(
        AutomationKind kind,
        string? artifactJson,
        string expectedFailureKind)
    {
        var testCaseId = await SeedTestCaseAsync(kind, artifactJson);
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = testCaseId, Status = E2eRunStatus.Queued });
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(new CountingSandboxProvider(), monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        var terminal = await WaitForRunStatusAsync(runId, E2eRunStatus.Error);
        await WaitForDispatcherIdleAsync(dispatcher);
        Assert.Contains(expectedFailureKind, terminal.Result);
    }

    [Fact]
    public async Task Dispatcher_records_error_for_claimed_run_with_missing_test_case()
    {
        var runId = Guid.NewGuid().ToString("N");
        var store = new ClaimFailureRunStore
        {
            Queued = true,
            ClaimedRun = new E2eRun
            {
                Id = runId,
                TestCaseId = "missing-test-case",
                Status = E2eRunStatus.Running,
            },
        };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = true, MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(new CountingSandboxProvider(), monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            store,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));
        await WaitForDispatcherIdleAsync(dispatcher);

        Assert.Equal(E2eRunStatus.Error, store.UpdatedStatus);
        Assert.Contains("MissingTestCase", store.UpdatedResult);
    }

    [Fact]
    public async Task Dispatcher_startup_requeues_running_runs_before_poll_loop()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun
        {
            Id = runId,
            TestCaseId = tcId,
            Status = E2eRunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            SandboxId = "orphaned-sandbox",
        });
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = false,
            PollInterval = TimeSpan.FromHours(1),
        });
        var pool = new LocalE2eExecutionPool(new CountingSandboxProvider(), monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new CountingReplayRuntime(),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            E2eRun? recovered;
            do
            {
                recovered = await _runs.GetAsync(runId);
                if (recovered?.Status == E2eRunStatus.Queued)
                    break;
                await Task.Delay(20);
            } while (DateTimeOffset.UtcNow < deadline);

            Assert.NotNull(recovered);
            Assert.Equal(E2eRunStatus.Queued, recovered.Status);
            Assert.Null(recovered.StartedAt);
            Assert.Null(recovered.SandboxId);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dispatcher.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public async Task Dispatcher_records_error_releases_slot_and_updates_last_run_when_runtime_throws()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            Enabled = true,
            MaxConcurrent = 1,
            PollInterval = TimeSpan.FromMilliseconds(5),
            PerRunTimeout = TimeSpan.FromSeconds(10),
        });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var runtime = new CountingReplayRuntime { ThrowOnExecute = true };
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            runtime,
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        var terminal = await WaitForRunStatusAsync(runId, E2eRunStatus.Error);
        await WaitForDispatcherIdleAsync(dispatcher);
        Assert.Contains("Exception", terminal.Result);
        Assert.Equal(0, pool.InFlight);
        Assert.True(provider.AllSandboxesDisposed);
        var testCase = await _testCases.GetAsync(tcId);
        Assert.NotNull(testCase);
        Assert.False(testCase.LastRunPassed);
        Assert.Contains("runtime exploded", testCase.LastRunResult);
    }

    [Fact]
    public async Task Dispatcher_does_not_dispatch_when_pool_is_full()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        await _runs.CreateAsync(new E2eRun { Id = Guid.NewGuid().ToString("N"), TestCaseId = tcId, Status = E2eRunStatus.Queued });
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = true, MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        await using var held = await pool.LeaseAsync();
        var dispatcher = new E2eRunDispatcher(
            _runs,
            pool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.False(await dispatcher.TryDispatchOneAsync(CancellationToken.None));
        Assert.Equal(1, provider.CreateCount);
    }

    [Fact]
    public async Task Dispatcher_releases_slot_and_skips_replay_when_sandbox_assignment_loses_race()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        var store = new ClaimFailureRunStore
        {
            Queued = true,
            ClaimedRun = new E2eRun
            {
                Id = runId,
                TestCaseId = tcId,
                Status = E2eRunStatus.Running,
            },
            AssignSandboxResult = false,
            GetRun = new E2eRun
            {
                Id = runId,
                TestCaseId = tcId,
                Status = E2eRunStatus.Canceled,
            },
        };
        var provider = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = true, MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var runtime = new CountingReplayRuntime();
        var dispatcher = new E2eRunDispatcher(
            store,
            pool,
            runtime,
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));
        await WaitForDispatcherIdleAsync(dispatcher);

        Assert.Equal(0, pool.InFlight);
        Assert.True(provider.AllSandboxesDisposed);
        Assert.Equal(0, runtime.ExecuteCount);
        Assert.Null(store.UpdatedStatus);
        Assert.Equal(1, store.AssignSandboxCalls);
    }

    [Fact]
    public async Task Dispatcher_releases_backoff_paths_for_lease_and_claim_failures()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var leaseFailureRunId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = leaseFailureRunId, TestCaseId = tcId, Status = E2eRunStatus.Queued });
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = true, MaxConcurrent = 1 });
        var throwingProvider = new CountingSandboxProvider { ThrowOnCreate = true };
        var throwingPool = new LocalE2eExecutionPool(throwingProvider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var leaseDispatcher = new E2eRunDispatcher(
            _runs,
            throwingPool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await leaseDispatcher.TryDispatchOneAsync(CancellationToken.None));
        await WaitForDispatcherIdleAsync(leaseDispatcher);
        Assert.Equal(0, throwingPool.InFlight);
        var failedLeaseRun = await _runs.GetAsync(leaseFailureRunId);
        Assert.NotNull(failedLeaseRun);
        Assert.Equal(E2eRunStatus.Error, failedLeaseRun.Status);
        Assert.Contains("PoolLeaseFailed", failedLeaseRun.Result);

        var claimStore = new ClaimFailureRunStore { Queued = true, ThrowOnClaim = true };
        var claimProvider = new CountingSandboxProvider();
        var claimPool = new LocalE2eExecutionPool(claimProvider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var claimDispatcher = new E2eRunDispatcher(
            claimStore,
            claimPool,
            new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await claimDispatcher.TryDispatchOneAsync(CancellationToken.None));
        await WaitForDispatcherIdleAsync(claimDispatcher);
        Assert.Equal(0, claimPool.InFlight);
        Assert.True(claimProvider.AllSandboxesDisposed);

        claimStore.ThrowOnClaim = false;
        claimStore.ReturnNullClaim = true;
        Assert.True(await claimDispatcher.TryDispatchOneAsync(CancellationToken.None));
        await WaitForDispatcherIdleAsync(claimDispatcher);
        Assert.Equal(0, claimPool.InFlight);
        Assert.True(claimProvider.AllSandboxesDisposed);
    }

    // --------------------------------------------------------------------
    // Infrastructure-vs-deterministic failure classification map
    // --------------------------------------------------------------------

    [Theory]
    // Every infrastructure kind the runtime/driver can emit -> Error.
    [InlineData("ReadinessProbe", true)]
    [InlineData("ReadinessUrlRejected", true)]
    [InlineData("NavigationUrlRejected", true)]
    [InlineData("ExecException", true)]
    [InlineData("ReplayDriverFailed", true)]
    [InlineData("ReplayDriverProtocolError", true)]
    [InlineData("ReplayDriverUnavailable", true)]
    [InlineData("ReplayEgressFirewallUnavailable", true)]
    [InlineData("ReplayEgressOriginRejected", true)]
    [InlineData("ReplayEgressResolutionFailed", true)]
    [InlineData("OutputLimitExceeded", true)]
    // Deterministic test failures the driver emits -> Failed (NOT infrastructure).
    [InlineData("StepFailed", false)]
    [InlineData("AssertionFailed", false)]
    // Unclassified / absent -> Failed.
    [InlineData("", false)]
    [InlineData(null, false)]
    // "AssertionException" was never emitted anywhere; it must NOT be treated as
    // infrastructure (else genuine assertion failures would be mislabeled Error).
    [InlineData("AssertionException", false)]
    public void IsInfrastructureFailure_classifies_every_known_failure_kind(string? failureKind, bool expected)
    {
        Assert.Equal(expected, E2eRunDispatcher.IsInfrastructureFailure(failureKind));
    }

    // --------------------------------------------------------------------
    // PersistResultAsync no-rows outcomes (throw vs canceled-race skip)
    // --------------------------------------------------------------------

    [Fact]
    public async Task Dispatcher_throws_affected_no_rows_when_persist_updates_nothing_and_run_not_canceled()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        var store = new ClaimFailureRunStore
        {
            Queued = true,
            ClaimedRun = new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Running },
            UpdateStatusResult = false, // update affects no rows...
            GetRun = null,              // ...and the run is NOT Canceled -> safety throw must fire
        };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = true, MaxConcurrent = 1 });
        var pool = new LocalE2eExecutionPool(new CountingSandboxProvider(), monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var logger = new CapturingLogger<E2eRunDispatcher>();
        var dispatcher = new E2eRunDispatcher(
            store,
            pool,
            new CountingReplayRuntime(),
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            logger);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));

        var crashed = await logger.WaitForEntryAsync(
            e => e.Exception is InvalidOperationException && e.Exception.Message.Contains("affected no rows"),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(crashed.Exception);
        Assert.NotNull(store.UpdatedStatus); // a persist WAS attempted before the throw
        var testCase = await _testCases.GetAsync(tcId);
        Assert.NotNull(testCase);
        Assert.Null(testCase.LastRunAt); // no last-run stamp when the persist reports no rows
    }

    [Fact]
    public async Task Dispatcher_canceled_race_on_persist_skips_last_run_stamp()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        var runId = Guid.NewGuid().ToString("N");
        var store = new ClaimFailureRunStore
        {
            Queued = true,
            ClaimedRun = new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Running },
            UpdateStatusResult = false, // update affects no rows because...
            GetRun = new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Canceled }, // ...it was canceled
        };
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { Enabled = true, MaxConcurrent = 1 });
        var provider = new CountingSandboxProvider();
        var pool = new LocalE2eExecutionPool(provider, monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        var runtime = new CountingReplayRuntime();
        var dispatcher = new E2eRunDispatcher(
            store,
            pool,
            runtime,
            _testCases,
            monitor,
            new E2eRunCancellationRegistry(),
            Admission(monitor),
            NullLogger<E2eRunDispatcher>.Instance);

        Assert.True(await dispatcher.TryDispatchOneAsync(CancellationToken.None));
        await WaitForDispatcherIdleAsync(dispatcher);

        Assert.Equal(1, runtime.ExecuteCount);                  // the replay actually ran
        Assert.Equal(E2eRunStatus.Passed, store.UpdatedStatus); // persist attempted with the real status
        Assert.Equal(0, pool.InFlight);
        Assert.True(provider.AllSandboxesDisposed);
        var testCase = await _testCases.GetAsync(tcId);
        Assert.NotNull(testCase);
        Assert.Null(testCase.LastRunAt); // canceled race -> last-run NOT stamped
    }

    // --------------------------------------------------------------------
    // Secret redaction of the persisted driver result
    // --------------------------------------------------------------------

    [Fact]
    public async Task Replay_redacts_secret_shaped_strings_in_persisted_driver_result()
    {
        const string secret = "sk-ant-SECRETTOKEN0123456789";
        var driverResult = new E2eRunResult
        {
            Passed = false,
            FailureKind = "StepFailed",
            Summary = $"driver failed leaking {secret} token",
            FailedStepIndex = 0,
            StepResults =
            [
                new E2eStepResult { ExitCode = 1, Passed = false, StdoutTail = $"out {secret}", StderrTail = $"err {secret}" },
            ],
            AssertionResults =
            [
                new E2eAssertionResult { Passed = false, Detail = $"detail {secret}" },
            ],
        };
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "203.0.113.10 STREAM app.local\n", string.Empty);
        sandbox.Programs["node"] = _ => DriverResult(driverResult, exitCode: 1);
        var artifact = new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }] };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.False(result.Passed);
        Assert.DoesNotContain(secret, result.Summary);
        Assert.Contains("***", result.Summary);
        Assert.DoesNotContain(secret, result.StepResults[0].StdoutTail);
        Assert.DoesNotContain(secret, result.StepResults[0].StderrTail);
        Assert.DoesNotContain(secret, result.AssertionResults[0].Detail);
    }

    // --------------------------------------------------------------------
    // Metadata-address (SSRF) blocking — IPv4 and the IPv6 branch
    // --------------------------------------------------------------------

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("fd00:ec2::254", true)]        // GCP link-local IPv6
    [InlineData("fe80::a9fe:a9fe", true)]      // link-local metadata alias
    [InlineData("::ffff:169.254.169.254", true)] // IPv4-mapped
    [InlineData("::169.254.169.254", true)]    // IPv4-compatible
    [InlineData("2001:db8::1", false)]
    [InlineData("fd00:ec2::255", false)]
    [InlineData("203.0.113.10", false)]
    public void IsBlockedMetadataIp_flags_ipv4_and_ipv6_metadata_forms(string address, bool expected)
    {
        var ip = System.Net.IPAddress.Parse(address);
        Assert.Equal(expected, E2eReplayOriginPolicy.IsBlockedMetadataIp(ip));
    }

    // --------------------------------------------------------------------
    // IPv6 egress endpoint resolution + ip6tables emission
    // --------------------------------------------------------------------

    [Fact]
    public async Task Replay_emits_ip6tables_rules_for_ipv6_allowed_origin()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "2001:db8::1 STREAM app.local\n", string.Empty);
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0));
        var artifact = new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }] };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed, result.Summary);
        var install = sandbox.ExecRequests.Single(exec =>
            exec.Argv.SequenceEqual(["sh", "-s"]) && exec.Stdin!.Contains("iptables -I OUTPUT", StringComparison.Ordinal));
        // family '6' endpoint lines drive the ip6tables RETURN rules.
        Assert.Contains("6 2001:db8::1 80", install.Stdin, StringComparison.Ordinal);
        Assert.Contains("6 2001:db8::1 443", install.Stdin, StringComparison.Ordinal);
        Assert.Contains("ip6tables", install.Stdin, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_normalizes_ipv4_mapped_origin_address_to_ipv4_firewall_rule()
    {
        var sandbox = new FakeSandbox();
        sandbox.Programs["getent"] = _ => new SandboxExecResult(0, "::ffff:203.0.113.10 STREAM app.local\n", string.Empty);
        sandbox.Programs["node"] = _ => DriverResult(PassedDriverResult(1, 0));
        var artifact = new E2eReplayArtifact { Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }] };
        var runtime = new E2eReplayRuntime(NullLogger<E2eReplayRuntime>.Instance);

        var result = await runtime.ExecuteAsync(artifact, sandbox);

        Assert.True(result.Passed, result.Summary);
        var install = sandbox.ExecRequests.Single(exec =>
            exec.Argv.SequenceEqual(["sh", "-s"]) && exec.Stdin!.Contains("iptables -I OUTPUT", StringComparison.Ordinal));
        // The IPv4-mapped address is unwrapped to a plain IPv4 (family '4') rule.
        Assert.Contains("4 203.0.113.10 80", install.Stdin, StringComparison.Ordinal);
        Assert.DoesNotContain("::ffff:203.0.113.10", install.Stdin, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------
    // SqliteE2eRunStore queue predicates against the real store
    // --------------------------------------------------------------------

    [Fact]
    public async Task HasQueuedAsync_reflects_queued_rows_in_the_real_store()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        Assert.False(await _runs.HasQueuedAsync());

        var runId = Guid.NewGuid().ToString("N");
        await _runs.CreateAsync(new E2eRun { Id = runId, TestCaseId = tcId, Status = E2eRunStatus.Queued });
        Assert.True(await _runs.HasQueuedAsync());

        var claimed = await _runs.ClaimNextQueuedAsync(sandboxId: null);
        Assert.NotNull(claimed);
        Assert.False(await _runs.HasQueuedAsync()); // only a Running row remains
    }

    [Fact]
    public async Task ClaimNextQueuedAsync_claims_each_queued_run_at_most_once_under_concurrency()
    {
        var tcId = await SeedE2eTestCaseAsync(MakeTrivialPassArtifact());
        const int queued = 5;
        for (var i = 0; i < queued; i++)
            await _runs.CreateAsync(new E2eRun { Id = Guid.NewGuid().ToString("N"), TestCaseId = tcId, Status = E2eRunStatus.Queued });

        // More claimers than queued rows: the surplus must lose the race and get null,
        // and no run may be handed to two claimers.
        var claims = await Task.WhenAll(Enumerable.Range(0, queued + 3)
            .Select(_ => Task.Run(() => _runs.ClaimNextQueuedAsync(sandboxId: null))));

        var claimedIds = claims.Where(r => r is not null).Select(r => r!.Id).ToArray();
        Assert.Equal(queued, claimedIds.Length);
        Assert.Equal(queued, claimedIds.Distinct(StringComparer.Ordinal).Count()); // no double-claim
        Assert.Equal(3, claims.Count(r => r is null));
        Assert.False(await _runs.HasQueuedAsync());
    }

    // --------------------------------------------------------------------
    // Pool MaxConcurrent clamping and the multi-host min() crossover
    // --------------------------------------------------------------------

    [Theory]
    [InlineData(0, E2eExecutionOptions.MinimumMaxConcurrent)]
    [InlineData(-5, E2eExecutionOptions.MinimumMaxConcurrent)]
    [InlineData(E2eExecutionOptions.MaximumMaxConcurrent + 1, E2eExecutionOptions.MaximumMaxConcurrent)]
    public void LocalPool_clamps_MaxConcurrent_into_configured_bounds(int configured, int expected)
    {
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = configured });
        var pool = new LocalE2eExecutionPool(new CountingSandboxProvider(), monitor, NullLogger<LocalE2eExecutionPool>.Instance);
        Assert.Equal(expected, pool.MaxConcurrent);
    }

    [Fact]
    public void MultiHostPool_clamps_out_of_range_host_and_global_caps_up_to_the_floor()
    {
        // Host cap 0 must clamp UP to the floor (1), not leave the gate at 0
        // (which would deadlock). Global is high so the host cap is what shows.
        var hostFloor = new MultiHostE2eExecutionPool(
            [new E2eExecutionHost("a", new CountingSandboxProvider(), 0)],
            new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 5 }),
            NullLogger<MultiHostE2eExecutionPool>.Instance);
        Assert.Equal(E2eExecutionOptions.MinimumMaxConcurrent, hostFloor.MaxConcurrent); // min(5, clamp(0)=1)

        // Global cap 0 must clamp UP to the floor (1); host sum is higher.
        var globalFloor = new MultiHostE2eExecutionPool(
            [new E2eExecutionHost("a", new CountingSandboxProvider(), 3)],
            new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 0 }),
            NullLogger<MultiHostE2eExecutionPool>.Instance);
        Assert.Equal(E2eExecutionOptions.MinimumMaxConcurrent, globalFloor.MaxConcurrent); // min(clamp(0)=1, 3)
    }

    [Fact]
    public void MultiHostPool_MaxConcurrent_is_min_of_global_cap_and_host_sum()
    {
        // Global cap BELOW the host sum -> the global cap binds.
        var globalBinds = new MultiHostE2eExecutionPool(
            [
                new E2eExecutionHost("a", new CountingSandboxProvider(), 4),
                new E2eExecutionHost("b", new CountingSandboxProvider(), 4),
            ],
            new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 2 }),
            NullLogger<MultiHostE2eExecutionPool>.Instance);
        Assert.Equal(2, globalBinds.MaxConcurrent); // min(2, 8)

        // Host sum BELOW the global cap -> the host sum binds.
        var hostSumBinds = new MultiHostE2eExecutionPool(
            [
                new E2eExecutionHost("a", new CountingSandboxProvider(), 1),
                new E2eExecutionHost("b", new CountingSandboxProvider(), 2),
            ],
            new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 10 }),
            NullLogger<MultiHostE2eExecutionPool>.Instance);
        Assert.Equal(3, hostSumBinds.MaxConcurrent); // min(10, 3)
    }

    [Fact]
    public async Task MultiHostPool_uses_explicit_image_and_denies_network_when_profile_unset()
    {
        // The production remote-ssh pool's BuildSpec must honour an explicit
        // SandboxImageReference over the global fallback AND default the network
        // to Denied when no profile is set — the same contract the dev-only
        // LocalPool is tested for. A swapped image precedence or a forgotten
        // Denied default (open egress for replays) must fail here.
        var host = new CountingSandboxProvider();
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions
        {
            MaxConcurrent = 1,
            SandboxImageReference = "e2e-image",
        });
        var pool = new MultiHostE2eExecutionPool(
            [new E2eExecutionHost("remote-a", host, 1)],
            monitor,
            NullLogger<MultiHostE2eExecutionPool>.Instance,
            fallbackImageReference: () => "global-image");

        await using var slot = await pool.LeaseAsync();

        var spec = Assert.Single(host.Specs);
        Assert.Equal("e2e-image", spec.ImageReference);
        Assert.Equal(SandboxNetworkPolicy.Denied, spec.Network);
    }

    [Fact]
    public void MultiHostPool_resizes_global_gate_when_options_monitor_fires_change()
    {
        // MaxConcurrent is documented as hot-reloadable on the production pool.
        // Keep the host sum (5) above the global cap so MaxConcurrent reflects
        // the global gate both before and after the reload; a handler that
        // resizes the wrong gate (or ignores the change) fails this assertion.
        var monitor = new SimpleOptionsMonitor<E2eExecutionOptions>(new E2eExecutionOptions { MaxConcurrent = 1 });
        var pool = new MultiHostE2eExecutionPool(
            [new E2eExecutionHost("a", new CountingSandboxProvider(), 5)],
            monitor,
            NullLogger<MultiHostE2eExecutionPool>.Instance);
        Assert.Equal(1, pool.MaxConcurrent); // min(1, 5)

        monitor.Set(new E2eExecutionOptions { MaxConcurrent = 3 });

        Assert.Equal(3, pool.MaxConcurrent); // min(3, 5) -> the resized global gate binds
    }

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

    private async Task<string> SeedE2eTestCaseAsync(string artifactJson)
        => await SeedTestCaseAsync(AutomationKind.E2eReplay, artifactJson);

    private async Task<string> SeedTestCaseAsync(AutomationKind automationKind, string? artifactJson)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "e2e test fixture",
            Prompt = "n/a",
        };
        await _itemStore.CreateAsync(item);

        var id = Guid.NewGuid().ToString("N");
        var tc = new TestCase
        {
            Id = id,
            Name = "fixture",
            Description = "fixture",
            SourceWorkItemId = item.Id.ToString(),
            AutomationKind = automationKind,
            ExecutableArtifactJson = artifactJson,
        };
        await _testCases.CreateAsync(tc);
        return id;
    }

    private async Task<E2eRun> WaitForRunStatusAsync(string runId, E2eRunStatus status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        E2eRun? current = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            current = await _runs.GetAsync(runId);
            if (current?.Status == status)
                return current;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Run {runId} did not reach {status}; current status={current?.Status}");
    }

    private static async Task WaitForDispatcherIdleAsync(E2eRunDispatcher dispatcher)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await dispatcher.WaitForIdleAsync(cts.Token);
    }

    private static E2eReplayArtifactAdmissionValidator Admission(IOptionsMonitor<E2eExecutionOptions> monitor)
        => new(monitor);

    private static string MakeTrivialPassArtifact()
        => JsonSerializer.Serialize(new E2eReplayArtifact
        {
            Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }],
            Assertions = [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#root" }],
        });

    private static SandboxExecResult DriverResult(E2eRunResult result, int exitCode = 0)
        => new(exitCode, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n", string.Empty);

    private static E2eRunResult PassedDriverResult(int steps, int assertions)
        => new()
        {
            Passed = true,
            Summary = $"{steps} steps, {assertions} assertions",
            StepResults = Enumerable.Range(0, steps)
                .Select(_ => new E2eStepResult { ExitCode = 0, Passed = true })
                .ToArray(),
            AssertionResults = Enumerable.Range(0, assertions)
                .Select(_ => new E2eAssertionResult { Passed = true, Detail = "ok" })
                .ToArray(),
        };

    private static E2eRunResult FailedDriverResult(string failureKind, int failedIndex, string detail)
        => new()
        {
            Passed = false,
            Summary = detail,
            FailureKind = failureKind,
            FailedStepIndex = failedIndex,
            StepResults = [new E2eStepResult { ExitCode = 1, Passed = false, StderrTail = detail }],
            AssertionResults = [],
        };

    // --------------------------------------------------------------------
    // Test doubles
    // --------------------------------------------------------------------

    private sealed class SimpleOptionsMonitor<T> : IOptionsMonitor<T> where T : class
    {
        private readonly List<Action<T, string?>> _listeners = new();
        private T _currentValue;

        public SimpleOptionsMonitor(T value) { _currentValue = value; }
        public T CurrentValue => _currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return null;
        }

        public void Set(T value)
        {
            _currentValue = value;
            foreach (var listener in _listeners)
                listener(value, null);
        }
    }

    /// <summary>
    /// Sandbox that resolves commands by argv[0] against an in-memory dictionary.
    /// Sufficient for asserting replay engine semantics without standing up a real VM.
    /// </summary>
    private sealed class FakeSandbox : ISandbox
    {
        public Dictionary<string, Func<CancellationToken, SandboxExecResult>> Programs { get; } = new(StringComparer.Ordinal);
        public string Id { get; } = "fake-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        public List<IReadOnlyList<string>> ExecLog { get; } = new();
        public List<SandboxExec> ExecRequests { get; } = new();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            ExecLog.Add(exec.Argv);
            ExecRequests.Add(exec);
            if (exec.Argv.Count == 0)
                return Task.FromResult(new SandboxExecResult(127, string.Empty, "empty"));
            var programName = DriverProgramName(exec.Argv);
            if (Programs.TryGetValue(programName, out var handler))
                return Task.FromResult(handler(ct));
            if (exec.Argv[0] == "getent" && exec.Argv.Count >= 3 && exec.Argv[1] == "ahosts" && exec.Argv[2] == "app.local")
                return Task.FromResult(new SandboxExecResult(0, "127.0.0.1 STREAM app.local\n", string.Empty));
            if (exec.Argv[0] == "sh" && exec.Argv.SequenceEqual(["sh", "-s"]))
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            return Task.FromResult(new SandboxExecResult(127, string.Empty, $"unknown: {exec.Argv[0]}"));
        }

        public ValueTask DisposeAsync() => default;

        private static string DriverProgramName(IReadOnlyList<string> argv)
        {
            if (argv.Count > 0 && argv[0] == "sudo")
            {
                var nodeIndex = argv.ToList().IndexOf("node");
                if (nodeIndex >= 0)
                    return argv[nodeIndex];
            }

            return argv[0];
        }
    }

    private sealed class LocalNodeSandbox : ISandbox
    {
        private readonly string _root;

        public LocalNodeSandbox()
        {
            _root = Path.Combine(Path.GetTempPath(), $"codeybox-node-driver-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_root, "node_modules", "playwright"));
            File.WriteAllText(Path.Combine(_root, "node_modules", "playwright", "index.js"), PlaywrightStub);
            File.WriteAllText(Path.Combine(_root, "dns-hook.js"), DnsHook);
        }

        public string Id { get; } = "local-node-" + Guid.NewGuid().ToString("N")[..8];

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count >= 3 && exec.Argv[0] == "getent" && exec.Argv[1] == "ahosts" && exec.Argv[2] == "app.local")
                return new SandboxExecResult(0, "127.0.0.1 STREAM app.local\n", string.Empty);
            if (exec.Argv.SequenceEqual(["sh", "-s"]))
                return new SandboxExecResult(0, string.Empty, string.Empty);

            var argv = StripReplayDriverWrapper(exec.Argv);
            var psi = new ProcessStartInfo(argv[0])
            {
                WorkingDirectory = _root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in argv.Skip(1))
                psi.ArgumentList.Add(arg);
            psi.Environment["NODE_PATH"] = Path.Combine(_root, "node_modules");
            psi.Environment["NODE_OPTIONS"] = $"--require {Path.Combine(_root, "dns-hook.js")}";

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start node");
            if (exec.Stdin is not null)
            {
                await process.StandardInput.WriteAsync(exec.Stdin.AsMemory(), ct);
            }
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            return new SandboxExecResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }

        public ValueTask DisposeAsync()
        {
            TestTempArtifacts.DeleteDirectory(_root);
            return default;
        }

        private static IReadOnlyList<string> StripReplayDriverWrapper(IReadOnlyList<string> argv)
        {
            if (argv.Count == 0 || argv[0] != "sudo")
                return argv;

            var nodeIndex = argv.ToList().IndexOf("node");
            return nodeIndex >= 0 ? argv.Skip(nodeIndex).ToArray() : argv;
        }

        private const string PlaywrightStub =
            """
            let pageInstance;

            function makeLocator(selector) {
              return {
                first() { return this; },
                async isVisible() { return selector !== '#hidden'; },
                async textContent() {
                  if (selector === '#message') return 'Welcome Ada';
                  return '';
                },
                async click() {
                  if (selector === '#missing') throw new Error('missing selector');
                },
                async dblclick() {},
                async fill(value) { pageInstance.values[selector] = value; },
                async press() {},
                async selectOption() {},
                async check() {},
                async uncheck() {},
                async hover() {},
                async waitFor() {}
              };
            }

            function makePage(routes, webSocketRoutes) {
              return pageInstance = {
                currentUrl: 'about:blank',
                values: {},
                locator: makeLocator,
                async goto(url) {
                  for (const handler of routes) {
                    let aborted = false;
                    await handler({
                      request: () => ({ url: () => url }),
                      continue: async () => {},
                      abort: async () => { aborted = true; }
                    });
                    if (aborted) throw new Error('request blocked');
                    if (url.includes('/blocked-subresource')) {
                      aborted = false;
                      await handler({
                        request: () => ({ url: () => 'http://evil.local/pixel.png' }),
                        continue: async () => {},
                        abort: async () => { aborted = true; }
                      });
                      if (aborted) throw new Error('request blocked');
                    }
                  }
                  if (url.includes('/websocket-off-origin')) {
                    if (webSocketRoutes.length === 0) throw new Error('websocket route missing');
                    let closed = false;
                    let connected = false;
                    await webSocketRoutes[0]({
                      url: () => 'ws://evil.local/socket',
                      connectToServer: () => { connected = true; },
                      close: () => { closed = true; }
                    });
                    if (closed && !connected) throw new Error('websocket blocked');
                  }
                  this.currentUrl = url.includes('/redirect-off-origin') ? 'http://evil.local/' : url;
                },
                url() { return this.currentUrl; },
                async title() { return 'Dashboard'; },
                async waitForTimeout(ms) {
                  if (ms > 60000) throw new Error('uncapped wait');
                }
              };
            }

            exports.chromium = {
              async launch() {
                const routes = [];
                const webSocketRoutes = [];
                return {
                  async newContext(options) {
                    if (!options || options.serviceWorkers !== 'block') throw new Error('service workers not blocked');
                    return {
                      async route(_pattern, handler) { routes.push(handler); },
                      async routeWebSocket(_pattern, handler) { webSocketRoutes.push(handler); },
                      async newPage() { return makePage(routes, webSocketRoutes); }
                    };
                  },
                  async close() {}
                };
              }
            };
            """;

        private const string DnsHook =
            """
            const dns = require('dns');
            const originalLookup = dns.promises.lookup.bind(dns.promises);
            dns.promises.lookup = async function(host, options) {
              if (host === 'app.local') {
                if (options && options.all) return [{ address: '127.0.0.1', family: 4 }];
                return { address: '127.0.0.1', family: 4 };
              }
              return originalLookup(host, options);
            };
            """;
    }

    private sealed class CountingSandboxProvider : ISandboxProvider
    {
        private readonly TimeSpan _execDelay;
        private readonly List<CountingSandbox> _all = new();
        private int _inFlight;
        public bool ThrowOnCreate { get; set; }
        public TimeSpan CreateDelay { get; set; }
        public int CreateCount;
        public int MaxConcurrentSeen;
        public List<SandboxSpec> Specs { get; } = new();
        public SandboxExecResult? ExecResult { get; set; }
        public bool BlockExecUntilCanceled { get; set; }
        public TaskCompletionSource ExecStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CountingSandboxProvider() : this(TimeSpan.Zero) { }
        public CountingSandboxProvider(TimeSpan execDelay) { _execDelay = execDelay; }

        public string Name => "fake-counting";

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            if (ThrowOnCreate) throw new InvalidOperationException("test forced");
            Specs.Add(spec);
            Interlocked.Increment(ref CreateCount);
            var current = Interlocked.Increment(ref _inFlight);
            UpdateMax(current);
            try
            {
                if (CreateDelay > TimeSpan.Zero)
                    await Task.Delay(CreateDelay, ct);
                var sb = new CountingSandbox(this, _execDelay);
                lock (_all) _all.Add(sb);
                return sb;
            }
            catch
            {
                Interlocked.Decrement(ref _inFlight);
                throw;
            }
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public bool AllSandboxesDisposed { get { lock (_all) return _all.All(s => s.Disposed); } }

        internal void ReleaseSandbox() => Interlocked.Decrement(ref _inFlight);

        private void UpdateMax(int current)
        {
            int existing;
            do { existing = Volatile.Read(ref MaxConcurrentSeen); }
            while (current > existing
                && Interlocked.CompareExchange(ref MaxConcurrentSeen, current, existing) != existing);
        }
    }

    private sealed class ManagedProviderDouble(string name, IReadOnlyList<ManagedSandboxInfo> sandboxes) : ISandboxProvider
    {
        public List<string> DisposedNames { get; } = new();
        public bool ThrowOnList { get; set; }
        public bool ThrowOnDispose { get; set; }
        public string Name => name;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        {
            if (ThrowOnList)
                throw new InvalidOperationException("list failed");
            return Task.FromResult(sandboxes);
        }

        public Task DisposeLeakedAsync(string sandboxName, CancellationToken ct)
        {
            DisposedNames.Add(sandboxName);
            if (ThrowOnDispose)
                throw new InvalidOperationException("dispose failed");
            return Task.CompletedTask;
        }
    }

    private sealed class CountingReplayRuntime : IE2eReplayRuntime
    {
        public int ExecuteCount { get; private set; }
        public bool ThrowOnExecute { get; init; }

        public Task<E2eRunResult> ExecuteAsync(E2eReplayArtifact artifact, ISandbox sandbox, CancellationToken ct = default)
        {
            ExecuteCount++;
            if (ThrowOnExecute)
                throw new InvalidOperationException("runtime exploded");
            return Task.FromResult(new E2eRunResult
            {
                Passed = true,
                Summary = "ok",
            });
        }
    }

    private sealed class ClaimFailureRunStore : IE2eRunStore
    {
        public bool Queued { get; set; }
        public bool ThrowOnClaim { get; set; }
        public bool ReturnNullClaim { get; set; }
        public bool AssignSandboxResult { get; set; } = true;
        public bool UpdateStatusResult { get; set; } = true;
        public E2eRun? ClaimedRun { get; set; }
        public E2eRun? GetRun { get; set; }
        public E2eRunStatus? UpdatedStatus { get; private set; }
        public string? UpdatedResult { get; private set; }
        public int AssignSandboxCalls { get; private set; }

        public Task CreateAsync(E2eRun run, CancellationToken ct = default) => Task.CompletedTask;

        public Task BulkCreateAsync(IReadOnlyList<E2eRun> runs, CancellationToken ct = default) => Task.CompletedTask;

        public Task<E2eRun?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(GetRun);

        public IAsyncEnumerable<E2eRun> ListAsync(int offset = 0, int limit = E2eExecutionOptions.DefaultListPageSize, CancellationToken ct = default) => Empty();

        public IAsyncEnumerable<E2eRun> ListByTestCaseAsync(string testCaseId, int offset = 0, int limit = E2eExecutionOptions.DefaultListPageSize, CancellationToken ct = default) => Empty();

        public IAsyncEnumerable<E2eRun> ListByBatchAsync(string batchId, int offset = 0, int limit = E2eExecutionOptions.DefaultListPageSize, CancellationToken ct = default) => Empty();

        public Task<E2eRunBatchCounts?> GetBatchCountsAsync(string batchId, CancellationToken ct = default) => Task.FromResult<E2eRunBatchCounts?>(null);

        public Task<bool> HasQueuedAsync(CancellationToken ct = default) => Task.FromResult(Queued);

        public Task<E2eRun?> ClaimNextQueuedAsync(string? sandboxId, CancellationToken ct = default)
        {
            if (ThrowOnClaim)
                throw new InvalidOperationException("claim failed");
            if (ReturnNullClaim)
                return Task.FromResult<E2eRun?>(null);
            if (ClaimedRun is not null)
                return Task.FromResult<E2eRun?>(ClaimedRun with { SandboxId = sandboxId, Status = E2eRunStatus.Running });
            return Task.FromResult<E2eRun?>(new E2eRun
            {
                Id = "claimed",
                TestCaseId = "tc",
                Status = E2eRunStatus.Running,
                SandboxId = sandboxId,
                StartedAt = DateTimeOffset.UtcNow,
            });
        }

        public Task<bool> AssignSandboxAsync(string id, string sandboxId, CancellationToken ct = default)
        {
            AssignSandboxCalls++;
            return Task.FromResult(AssignSandboxResult);
        }

        public Task<int> RequeueRunningAsync(DateTimeOffset startedBefore, CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> UpdateStatusAsync(string id, E2eRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, string? result, CancellationToken ct = default)
        {
            UpdatedStatus = status;
            UpdatedResult = result;
            return Task.FromResult(UpdateStatusResult);
        }

        public Task<bool> CancelAsync(string id, CancellationToken ct = default) => Task.FromResult(true);

        private static async IAsyncEnumerable<E2eRun> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CountingSandbox : ISandbox
    {
        private readonly CountingSandboxProvider _owner;
        private readonly TimeSpan _execDelay;
        public bool Disposed;

        public CountingSandbox(CountingSandboxProvider owner, TimeSpan execDelay)
        {
            _owner = owner;
            _execDelay = execDelay;
            Id = "counting-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public string Id { get; }

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "getent")
                return new SandboxExecResult(0, "203.0.113.10 STREAM app.local\n", string.Empty);
            if (exec.Argv.SequenceEqual(["sh", "-s"]))
                return new SandboxExecResult(0, string.Empty, string.Empty);
            if (_execDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(_execDelay, ct); }
                catch (OperationCanceledException) { throw; }
            }
            if (_owner.BlockExecUntilCanceled)
            {
                _owner.ExecStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return _owner.ExecResult ?? DriverResult(PassedDriverResult(1, 1));
        }

        public ValueTask DisposeAsync()
        {
            if (!Disposed)
            {
                Disposed = true;
                _owner.ReleaseSandbox();
            }
            return default;
        }
    }
}
