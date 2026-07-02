using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;
using System.Net;

namespace CodeyBox.Tests;

public sealed class CodeyBoxOptionsValidatorTests
{
    [Theory]
    [InlineData("retention", "CodeyBox:AuditLog:RetainedDays must be >= 1")]
    [InlineData("path", "CodeyBox:AuditLog:Path must be non-empty")]
    [InlineData("audit-path", "CodeyBox:AuditLog:AuditPath must be non-empty")]
    public void Validate_RejectsInvalidAuditLogOptions(string scenario, string expectedFailure)
    {
        var options = ValidCodeyBoxOptions();
        ApplyInvalidAuditLogScenario(options.AuditLog, scenario);

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedFailure, result.FailureMessage);
    }

    [Theory]
    [InlineData("retention", "CodeyBox:AuditLog:RetainedDays must be >= 1")]
    [InlineData("path", "CodeyBox:AuditLog:Path must be non-empty")]
    [InlineData("audit-path", "CodeyBox:AuditLog:AuditPath must be non-empty")]
    public void ValidateAndPrepare_RejectsInvalidAuditLogOptionsAtStartup(
        string scenario,
        string expectedFailure)
    {
        var options = ValidAuditLogOptions();
        ApplyInvalidAuditLogScenario(options, scenario);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AuditLogStartup.ValidateAndPrepare(options));

        Assert.Contains(expectedFailure, ex.Message);
    }

    [Theory]
    [InlineData("null", "CodeyBox:AuditLog:ConsoleLog must not be null")]
    [InlineData("path", "CodeyBox:AuditLog:ConsoleLog:Path must be non-empty")]
    [InlineData("retention", "CodeyBox:AuditLog:ConsoleLog:RetainedFileCountLimit must be >= 1")]
    [InlineData("size", "CodeyBox:AuditLog:ConsoleLog:MaxFileSizeBytes must be >= 1048576")]
    public void Validate_RejectsInvalidConsoleLogOptions(string scenario, string expected)
    {
        var options = ValidCodeyBoxOptions();
        ApplyInvalidConsoleLogScenario(options.AuditLog, scenario);

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expected, result.FailureMessage);
    }

    [Fact]
    public void Validate_AllowsInvalidConsoleLogFieldsWhenDisabled()
    {
        // The Enabled flag short-circuits ConsoleLog field validation:
        // operators who keep their own out-of-process run-log capture should
        // not have to set Path / RetainedFileCountLimit / MaxFileSizeBytes
        // away from sentinel-zero values just to satisfy the validator.
        var options = ValidCodeyBoxOptions();
        options.AuditLog.ConsoleLog.Enabled = false;
        options.AuditLog.ConsoleLog.Path = "";
        options.AuditLog.ConsoleLog.RetainedFileCountLimit = 0;
        options.AuditLog.ConsoleLog.MaxFileSizeBytes = 0;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Fact]
    public void ValidateAndPrepare_CreatesConsoleLogDirectoryWhenEnabled()
    {
        var root = Directory.CreateTempSubdirectory("codeybox-console-log-prepare-").FullName;
        try
        {
            var consoleLogDir = Path.Combine(root, "console-logs", "nested");
            Assert.False(Directory.Exists(consoleLogDir));

            var options = ValidAuditLogOptions();
            options.Path = Path.Combine(root, "codeybox-.json");
            options.AuditPath = Path.Combine(root, "audit-.json");
            options.ConsoleLog.Enabled = true;
            options.ConsoleLog.Path = Path.Combine(consoleLogDir, "codeybox-console-.log");

            AuditLogStartup.ValidateAndPrepare(options);

            Assert.True(Directory.Exists(consoleLogDir));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ValidateAndPrepare_SkipsConsoleLogDirectoryWhenDisabled()
    {
        // Mirror image of the previous test: when ConsoleLog is off, the
        // operator's blank/bogus Path must not be touched. This is the
        // contract that lets an operator turn the rolling run-log off and
        // forget about the field entirely.
        var root = Directory.CreateTempSubdirectory("codeybox-console-log-skip-").FullName;
        try
        {
            var blocker = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blocker, "x");

            var options = ValidAuditLogOptions();
            options.Path = Path.Combine(root, "codeybox-.json");
            options.AuditPath = Path.Combine(root, "audit-.json");
            options.ConsoleLog.Enabled = false;
            // A path that would fail PrepareDirectory if visited.
            options.ConsoleLog.Path = Path.Combine(blocker, "console-.log");

            AuditLogStartup.ValidateAndPrepare(options); // must not throw
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ConsoleLogOptions_HasBoundedDefaults()
    {
        var defaults = new ConsoleLogOptions();

        Assert.True(defaults.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(defaults.Path));
        Assert.True(defaults.RetainedFileCountLimit >= 1);
        Assert.True(defaults.MaxFileSizeBytes >= 1L * 1024 * 1024);

        // Peak disk = RetainedFileCountLimit * MaxFileSizeBytes. The point of
        // the rotation work is that this product is bounded — pin a sanity
        // ceiling so a future bump to either default that breaches multi-GiB
        // shows up here, not in production storage exhaustion.
        var peakBytes = (long)defaults.RetainedFileCountLimit * defaults.MaxFileSizeBytes;
        Assert.True(peakBytes <= 10L * 1024 * 1024 * 1024,
            $"Default ConsoleLog peak disk {peakBytes} bytes exceeds 10 GiB sanity ceiling.");
    }

    [Fact]
    public void ValidateAndPrepare_RejectsLogPathWhoseDirectoryCannotBeCreated()
    {
        var root = Directory.CreateTempSubdirectory("codeybox-audit-log-validation-").FullName;
        try
        {
            var blocker = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blocker, "x");

            var options = ValidAuditLogOptions();
            options.Path = Path.Combine(blocker, "main-.json");
            options.AuditPath = Path.Combine(root, "audit-.json");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                AuditLogStartup.ValidateAndPrepare(options));

            Assert.Contains("not writable", ex.Message);
            Assert.Contains(blocker, ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CodeyBoxOptions.MaximumMaxTemplateChecks + 1)]
    public void Validate_RejectsInvalidMaxTemplateChecks(int maxTemplateChecks)
    {
        var options = ValidCodeyBoxOptions();
        options.MaxTemplateChecks = maxTemplateChecks;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:MaxTemplateChecks", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CodeyBoxOptions.MaximumMaxBulkItems + 1)]
    public void Validate_RejectsOutOfRangeMaxBulkItems(int maxBulk)
    {
        var options = ValidCodeyBoxOptions();
        options.MaxBulkItems = maxBulk;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:MaxBulkItems", result.FailureMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(CodeyBoxOptions.DefaultMaxBulkItems)]
    [InlineData(CodeyBoxOptions.MaximumMaxBulkItems)]
    public void Validate_AcceptsMaxBulkItemsBoundaries(int maxBulk)
    {
        var options = ValidCodeyBoxOptions();
        options.MaxBulkItems = maxBulk;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Theory]
    [InlineData(0, "CodeyBox:E2eExecution:MaxConcurrent")]
    [InlineData(E2eExecutionOptions.MaximumMaxConcurrent + 1, "CodeyBox:E2eExecution:MaxConcurrent")]
    public void Validate_RejectsInvalidE2eMaxConcurrent(int value, string expected)
    {
        var options = ValidCodeyBoxOptions();
        options.E2eExecution.MaxConcurrent = value;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expected, result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsInvalidE2eTimingAndPoolKind()
    {
        var options = ValidCodeyBoxOptions();
        options.E2eExecution.PollInterval = TimeSpan.FromMilliseconds(-1);
        options.E2eExecution.PerRunTimeout = TimeSpan.Zero;
        options.E2eExecution.PoolKind = "bogus";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:E2eExecution:PollInterval", result.FailureMessage);
        Assert.Contains("CodeyBox:E2eExecution:PerRunTimeout", result.FailureMessage);
        Assert.Contains("CodeyBox:E2eExecution:PoolKind", result.FailureMessage);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("remote-ssh")]
    public void Validate_AcceptsValidE2ePoolKinds(string poolKind)
    {
        var options = ValidCodeyBoxOptions();
        options.E2eExecution.MaxConcurrent = E2eExecutionOptions.MaximumMaxConcurrent;
        options.E2eExecution.PollInterval = TimeSpan.Zero;
        options.E2eExecution.PerRunTimeout = TimeSpan.FromSeconds(1);
        options.E2eExecution.PoolKind = poolKind;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://app.local")]
    [InlineData("http://app.local/path")]
    [InlineData("http://app.local?x=1")]
    [InlineData("http://app.local#frag")]
    [InlineData("http://user@app.local")]
    public void Validate_RejectsInvalidE2eAllowedReadinessOrigins(string origin)
    {
        var options = ValidCodeyBoxOptions();
        options.E2eExecution.AllowedReadinessOrigins = [origin];

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("AllowedReadinessOrigins", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEmptyE2eAllowedReadinessOrigins()
    {
        var options = ValidCodeyBoxOptions();
        options.E2eExecution.AllowedReadinessOrigins = [];

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("AllowedReadinessOrigins", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEnabledRemoteE2ePrerequisiteFailures()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "coding@example" };
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "coding@example" };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "cb-e2e";
        options.E2eExecution.NetworkProfile = "unsupported";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("NetworkProfile", result.FailureMessage);
        Assert.Contains("different SSH host", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEnabledRemoteE2eWhenSameHostUsesDifferentSshUser()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "coding@remote.example" };
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "e2e@remote.example" };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "cb-e2e";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("different SSH host", result.FailureMessage);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("codeybox@127.0.0.1")]
    [InlineData("codeybox@[::1]")]
    public void Validate_RejectsEnabledRemoteE2eWhenTargetIsLoopbackOrLocalhost(string target)
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = null;
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = target };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "cb-e2e";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("dedicated remote SSH host", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEnabledRemoteE2eWhenTargetIsOrchestratorHostName()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = null;
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = $"e2e@{Dns.GetHostName()}" };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "cb-e2e";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("dedicated remote SSH host", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEnabledRemoteE2eWhenAliasHostNameResolvesLocal()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = null;
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            SshTarget = "e2e-local-alias",
            ExtraSshOptions = ["HostName=127.0.0.1"],
        };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "cb-e2e";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("dedicated", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEnabledRemoteE2eWhenOpenSshConfigAliasResolvesLocal()
    {
        var runner = new RecordingProcessRunner(
            new ProcessRunResult(0, "user e2e\nhostname 127.0.0.1\nport 2200\n", string.Empty));
        var validator = new CodeyBoxOptionsValidator(new E2eRemotePoolConfigValidation(
            new E2eRemoteHostValidation(new OpenSshConfigResolver(runner))));
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = null;
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            SshBinary = "/usr/bin/ssh-custom",
            SshTarget = "e2e-local-alias",
            SshPort = 2200,
            ExtraSshOptions = ["IdentityFile=/tmp/e2e_key", "UserKnownHostsFile=/tmp/e2e_known_hosts"],
        };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "cb-e2e";

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("dedicated", result.FailureMessage);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(
            [
                "/usr/bin/ssh-custom",
                "-G",
                "-p",
                "2200",
                "-o",
                "IdentityFile=/tmp/e2e_key",
                "-o",
                "UserKnownHostsFile=/tmp/e2e_known_hosts",
                "e2e-local-alias",
            ],
            call.Argv);
    }

    [Fact]
    public void Validate_RejectsEnabledRemoteE2eWhenAliasHostNameMatchesCodingFleetAddress()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            SshTarget = "coding-alias",
            ExtraSshOptions = ["HostName=198.51.100.10"],
        };
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            SshTarget = "e2e-alias",
            ExtraSshOptions = ["HostName=198.51.100.10"],
        };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "cb-e2e";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("different SSH host", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsInvalidE2eRemoteHostCapacity()
    {
        var options = ValidCodeyBoxOptions();
        options.E2eMultipassRemoteSandboxes =
        [
            new E2eMultipassRemoteHostConfig
            {
                SshTarget = "e2e@example",
                MaxConcurrent = E2eExecutionOptions.MaximumMaxConcurrent + 1,
            },
        ];

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("E2eMultipassRemoteSandboxes:0:MaxConcurrent", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsConfiguredRemoteE2eLifecycleOverlapEvenWhenDisabled()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "coding@remote.example" };
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "e2e@remote.example" };
        options.E2eExecution.Enabled = false;
        options.E2eExecution.PoolKind = "remote-ssh";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("different SSH host", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsDuplicateConfiguredRemoteE2eHostAndVmPrefix()
    {
        var options = ValidCodeyBoxOptions();
        options.E2eMultipassRemoteSandboxes =
        [
            new E2eMultipassRemoteHostConfig { SshTarget = "e2e-a@remote.example", VmNamePrefix = "codeybox-r-" },
            new E2eMultipassRemoteHostConfig { SshTarget = "e2e-b@remote.example", VmNamePrefix = "codeybox-r-" },
        ];
        options.E2eExecution.Enabled = false;
        options.E2eExecution.PoolKind = "remote-ssh";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("same SSH host with the same VmNamePrefix", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsInvalidSandboxResumeMode()
    {
        var options = ValidCodeyBoxOptions();
        options.Shutdown.SandboxResumeMode = (SandboxResumeMode)42;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:Shutdown:SandboxResumeMode", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsInvalidSandboxTeardownMode()
    {
        var options = ValidCodeyBoxOptions();
        options.Shutdown.SandboxTeardownMode = (SandboxTeardownMode)42;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:Shutdown:SandboxTeardownMode", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsNonPositiveSandboxResumeTimeout(int seconds)
    {
        var options = ValidCodeyBoxOptions();
        options.Shutdown.SandboxResumeTimeout = TimeSpan.FromSeconds(seconds);

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:Shutdown:SandboxResumeTimeout", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsOversizedSandboxResumeTimeout()
    {
        var options = ValidCodeyBoxOptions();
        options.Shutdown.SandboxResumeTimeout =
            SandboxStartupResumePolicy.MaximumResumeTimeout + TimeSpan.FromTicks(1);

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:Shutdown:SandboxResumeTimeout", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsNonPositiveSandboxAdoptionDeadline(int seconds)
    {
        var options = ValidCodeyBoxOptions();
        options.Shutdown.SandboxAdoptionDeadlineSeconds = seconds;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:Shutdown:SandboxAdoptionDeadlineSeconds", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsOversizedSandboxAdoptionDeadline()
    {
        var options = ValidCodeyBoxOptions();
        options.Shutdown.SandboxAdoptionDeadlineSeconds =
            (int)SandboxStartupResumePolicy.MaximumAdoptionDeadline.TotalSeconds + 1;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:Shutdown:SandboxAdoptionDeadlineSeconds", result.FailureMessage);
    }

    [Fact]
    public void Validate_AcceptsMaximumStartupResumeBoundaries()
    {
        var options = ValidCodeyBoxOptions();
        options.Shutdown.SandboxResumeTimeout = SandboxStartupResumePolicy.MaximumResumeTimeout;
        options.Shutdown.SandboxAdoptionDeadlineSeconds =
            (int)SandboxStartupResumePolicy.MaximumAdoptionDeadline.TotalSeconds;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsInvalidWorkerPoolHealthWatchdogOptions()
    {
        var options = ValidCodeyBoxOptions();
        options.WorkerPoolHealthWatchdog.MaxHealthCheckCandidateScan = 0;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:WorkerPoolHealthWatchdog:MaxHealthCheckCandidateScan", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEmptyAgentPausesKey()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentPauses[" "] = new CodeyBox.Api.AgentPauseConfig
        {
            Paused = true,
            Reason = "reserve quota",
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:AgentPauses keys must not be empty", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsPausedTrueWithoutReason()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentPauses["claude"] = new CodeyBox.Api.AgentPauseConfig
        {
            Paused = true,
            Reason = "   ",
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:AgentPauses:claude:Reason", result.FailureMessage);
    }

    [Fact]
    public void Validate_AcceptsPausedFalseWithoutReason()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentPauses["claude"] = new CodeyBox.Api.AgentPauseConfig
        {
            Paused = false,
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsPausedFalseWithControlCharacterReason()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentPauses["claude"] = new CodeyBox.Api.AgentPauseConfig
        {
            Paused = false,
            Reason = "bad\x01reason",
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:AgentPauses:claude:Reason", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Validate_RejectsNonPositiveAgentPauseDuration(int seconds)
    {
        var options = ValidCodeyBoxOptions();
        options.AgentPauses["claude"] = new CodeyBox.Api.AgentPauseConfig
        {
            Paused = true,
            Reason = "reserve quota",
            DurationSeconds = seconds,
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:AgentPauses:claude:DurationSeconds must be positive", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsAgentPauseWithBothDurationAndExpiresAt()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentPauses["claude"] = new CodeyBox.Api.AgentPauseConfig
        {
            Paused = true,
            Reason = "reserve quota",
            DurationSeconds = 3600,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            "CodeyBox:AgentPauses:claude must provide either DurationSeconds or ExpiresAt",
            result.FailureMessage);
    }

    [Theory]
    [InlineData(-1, "must be between 0 and 100")]
    [InlineData(101, "must be between 0 and 100")]
    public void Validate_RejectsInvalidCodexRequestMaxRetries(int value, string expectedMessage)
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance["codex"] = new AgentNetworkToleranceOptions
        {
            RequestMaxRetries = value
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedMessage, result.FailureMessage);
    }

    [Theory]
    [InlineData(-1, "must be between 0 and 100")]
    [InlineData(101, "must be between 0 and 100")]
    public void Validate_RejectsInvalidCodexStreamMaxRetries(int value, string expectedMessage)
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance["codex"] = new AgentNetworkToleranceOptions
        {
            StreamMaxRetries = value
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedMessage, result.FailureMessage);
    }

    [Theory]
    [InlineData(-1, "must be between 0 and")]
    [InlineData(AgentNetworkToleranceOptions.CodexMaximumStreamIdleTimeoutMs + 1, "must be between 0 and")]
    public void Validate_RejectsInvalidCodexStreamIdleTimeoutMs(int value, string expectedMessage)
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance["codex"] = new AgentNetworkToleranceOptions
        {
            StreamIdleTimeoutMs = value
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedMessage, result.FailureMessage);
    }

    [Theory]
    [InlineData(-1, "must be between 0 and")]
    [InlineData(AgentNetworkToleranceOptions.ClaudeMaximumApiTimeoutMs + 1, "must be between 0 and")]
    public void Validate_RejectsInvalidClaudeApiTimeoutMs(int value, string expectedMessage)
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance["claude"] = new AgentNetworkToleranceOptions
        {
            ApiTimeoutMs = value
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedMessage, result.FailureMessage);
    }

    [Fact]
    public void Validate_AcceptsValidAgentNetworkTolerance()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance["codex"] = new AgentNetworkToleranceOptions
        {
            RequestMaxRetries = 5,
            StreamMaxRetries = 10,
            StreamIdleTimeoutMs = 120000,
            Provider = "azure"
        };
        options.AgentNetworkTolerance["claude"] = new AgentNetworkToleranceOptions
        {
            ApiTimeoutMs = 30000
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEmptyAgentNetworkToleranceKey()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance[" "] = new AgentNetworkToleranceOptions();

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:AgentNetworkTolerance keys must not be empty", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsNullAgentNetworkToleranceBlock()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance["codex"] = null;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:AgentNetworkTolerance:codex must not be null", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsBlankCodexProvider()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance["codex"] = new AgentNetworkToleranceOptions
        {
            Provider = " ",
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:AgentNetworkTolerance:codex:Provider must not be empty", result.FailureMessage);
    }

    [Theory]
    [InlineData("open.ai")]
    [InlineData("openai=evil")]
    [InlineData("open ai")]
    [InlineData("openai\nnext")]
    public void Validate_RejectsInvalidCodexProviderId(string provider)
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance["codex"] = new AgentNetworkToleranceOptions
        {
            Provider = provider,
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:AgentNetworkTolerance:codex:Provider must match [A-Za-z0-9_-]+", result.FailureMessage);
    }

    [Fact]
    public void Validate_AcceptsNetworkToleranceTimeoutBoundaries()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentNetworkTolerance["codex"] = new AgentNetworkToleranceOptions
        {
            StreamIdleTimeoutMs = AgentNetworkToleranceOptions.CodexMaximumStreamIdleTimeoutMs,
            Provider = "azure_openai-1",
        };
        options.AgentNetworkTolerance["claude"] = new AgentNetworkToleranceOptions
        {
            ApiTimeoutMs = AgentNetworkToleranceOptions.ClaudeMaximumApiTimeoutMs,
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEnabledSharedMirrorWithEmptyDirectory()
    {
        var options = ValidCodeyBoxOptions();
        options.EnableSharedUpstreamMirror = true;
        options.SharedUpstreamMirrorDirectory = " ";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:SharedUpstreamMirrorDirectory", result.FailureMessage);
    }

    [Fact]
    public void Validate_AllowsEmptySharedMirrorDirectoryWhenMirrorDisabled()
    {
        var options = ValidCodeyBoxOptions();
        options.EnableSharedUpstreamMirror = false;
        options.SharedUpstreamMirrorDirectory = " ";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Fact]
    public void Validate_AcceptsValidAgentPauseEntry()
    {
        var options = ValidCodeyBoxOptions();
        options.AgentPauses["gemini"] = new CodeyBox.Api.AgentPauseConfig
        {
            Paused = true,
            Reason = "provider outage",
            DurationSeconds = 21600,
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsInvalidSandboxReuseOptions()
    {
        var options = ValidCodeyBoxOptions();
        options.PipelineTuning.MaxSandboxReuses = 0;
        options.PipelineTuning.MaxSandboxLifetime = TimeSpan.Zero;
        options.PipelineTuning.SandboxPressureThreshold = -0.5;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:PipelineTuning:MaxSandboxReuses must be >= 1", result.FailureMessage);
        Assert.Contains("CodeyBox:PipelineTuning:MaxSandboxLifetime must be a positive TimeSpan", result.FailureMessage);
        Assert.Contains("CodeyBox:PipelineTuning:SandboxPressureThreshold must be between 0.0 and 1.0 inclusive", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsNaNOrInfinitySandboxPressureThreshold()
    {
        var options = ValidCodeyBoxOptions();
        options.PipelineTuning.SandboxPressureThreshold = double.NaN;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:PipelineTuning:SandboxPressureThreshold must be between 0.0 and 1.0 inclusive", result.FailureMessage);

        options.PipelineTuning.SandboxPressureThreshold = double.PositiveInfinity;
        result = new CodeyBoxOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:PipelineTuning:SandboxPressureThreshold must be between 0.0 and 1.0 inclusive", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsNegativeAuditorIdleTimeout()
    {
        var options = ValidCodeyBoxOptions();
        options.PipelineTuning.AuditorIdleTimeout = TimeSpan.FromSeconds(-1);

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:PipelineTuning:AuditorIdleTimeout must be non-negative", result.FailureMessage);
    }

    [Theory]
    [InlineData("MaxPromptChars")]
    [InlineData("MaxOutputBufferChars")]
    [InlineData("MaxInjectionChars")]
    [InlineData("InjectionQueueCapacity")]
    [InlineData("CompletedSessionRetentionSeconds")]
    [InlineData("MaxSessions")]
    [InlineData("RetainedCommandsPerSession")]
    [InlineData("DefaultListPageSize")]
    [InlineData("MaxListPageSize")]
    public void Validate_PropagatesAgentSupervisionFailures(string scenario)
    {
        var options = ValidCodeyBoxOptions();
        options.AgentSupervision = new AgentSupervisionOptions();
        switch (scenario)
        {
            case "MaxPromptChars": options.AgentSupervision.MaxPromptChars = 0; break;
            case "MaxOutputBufferChars": options.AgentSupervision.MaxOutputBufferChars = 0; break;
            case "MaxInjectionChars": options.AgentSupervision.MaxInjectionChars = 0; break;
            case "InjectionQueueCapacity": options.AgentSupervision.InjectionQueueCapacity = 0; break;
            case "CompletedSessionRetentionSeconds": options.AgentSupervision.CompletedSessionRetentionSeconds = -1; break;
            case "MaxSessions": options.AgentSupervision.MaxSessions = 0; break;
            case "RetainedCommandsPerSession": options.AgentSupervision.RetainedCommandsPerSession = -1; break;
            case "DefaultListPageSize": options.AgentSupervision.DefaultListPageSize = 0; break;
            case "MaxListPageSize":
                options.AgentSupervision.DefaultListPageSize = 64;
                options.AgentSupervision.MaxListPageSize = 16;
                break;
        }

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(scenario, result.FailureMessage);
    }

    private static CodeyBoxOptions ValidCodeyBoxOptions()
        => new() { AuditLog = ValidAuditLogOptions() };

    private static AuditLogOptions ValidAuditLogOptions()
        => new()
        {
            RetainedDays = 30,
            Path = Path.Combine("logs", "codeybox-.json"),
            AuditPath = Path.Combine("logs", "audit-.json"),
        };

    private static void ApplyInvalidAuditLogScenario(AuditLogOptions options, string scenario)
    {
        switch (scenario)
        {
            case "retention":
                options.RetainedDays = 0;
                break;
            case "path":
                options.Path = " ";
                break;
            case "audit-path":
                options.AuditPath = " ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void ApplyInvalidConsoleLogScenario(AuditLogOptions options, string scenario)
    {
        switch (scenario)
        {
            case "null":
                options.ConsoleLog = null!;
                break;
            case "path":
                options.ConsoleLog.Enabled = true;
                options.ConsoleLog.Path = " ";
                break;
            case "retention":
                options.ConsoleLog.Enabled = true;
                options.ConsoleLog.RetainedFileCountLimit = 0;
                break;
            case "size":
                options.ConsoleLog.Enabled = true;
                options.ConsoleLog.MaxFileSizeBytes = 1024;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;

        public RecordingProcessRunner(ProcessRunResult result)
        {
            _result = result;
        }

        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true)
        {
            Calls.Add(new ProcessCall(argv.ToArray(), stdin, maxStdoutBytes, maxStderrBytes));
            return Task.FromResult(_result);
        }
    }

    private sealed record ProcessCall(
        IReadOnlyList<string> Argv,
        string? Stdin,
        int? MaxStdoutBytes,
        int? MaxStderrBytes);
}
