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
    [InlineData("acquisition", "CodeyBox:SqliteWriteGate:AcquisitionTimeout must be positive")]
    [InlineData("acquisition-max", "CodeyBox:SqliteWriteGate:AcquisitionTimeout must be <=")]
    [InlineData("hold", "CodeyBox:SqliteWriteGate:MaxHoldDuration must be positive")]
    [InlineData("hold-max", "CodeyBox:SqliteWriteGate:MaxHoldDuration must be <=")]
    [InlineData("waiters", "CodeyBox:SqliteWriteGate:MaxQueuedWaiters must be positive")]
    [InlineData("waiters-max", "CodeyBox:SqliteWriteGate:MaxQueuedWaiters must be <=")]
    [InlineData("reads", "CodeyBox:SqliteWriteGate:MaxConcurrentReadConnections must be positive")]
    [InlineData("reads-max", "CodeyBox:SqliteWriteGate:MaxConcurrentReadConnections must be <=")]
    public void Validate_RejectsInvalidSqliteWriteGateOptions(
        string scenario,
        string expectedFailure)
    {
        var options = ValidCodeyBoxOptions();
        switch (scenario)
        {
            case "acquisition":
                options.SqliteWriteGate.AcquisitionTimeout = TimeSpan.Zero;
                break;
            case "acquisition-max":
                options.SqliteWriteGate.AcquisitionTimeout = SqliteWriteGateOptions.MaximumAcquisitionTimeout.Add(TimeSpan.FromMilliseconds(1));
                break;
            case "hold":
                options.SqliteWriteGate.MaxHoldDuration = TimeSpan.Zero;
                break;
            case "hold-max":
                options.SqliteWriteGate.MaxHoldDuration = SqliteWriteGateOptions.MaximumAllowedHoldDuration.Add(TimeSpan.FromMilliseconds(1));
                break;
            case "waiters":
                options.SqliteWriteGate.MaxQueuedWaiters = 0;
                break;
            case "waiters-max":
                options.SqliteWriteGate.MaxQueuedWaiters = SqliteWriteGateOptions.MaximumQueuedWaiters + 1;
                break;
            case "reads":
                options.SqliteWriteGate.MaxConcurrentReadConnections = 0;
                break;
            case "reads-max":
                options.SqliteWriteGate.MaxConcurrentReadConnections = SqliteWriteGateOptions.MaximumConcurrentReadConnections + 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedFailure, result.FailureMessage);
    }

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
    public void Validate_AutoRequeueOnAgentRestore_DefaultsEnabled()
    {
        var options = ValidCodeyBoxOptions();

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(options.AutoRequeueOnAgentRestore.Enabled);
        Assert.False(result.Failed, result.FailureMessage);
    }

    [Theory]
    [InlineData("lookback", "CodeyBox:AutoRequeueOnAgentRestore:LookbackGrace")]
    [InlineData("post-margin", "CodeyBox:AutoRequeueOnAgentRestore:PostRestoreMargin")]
    public void Validate_RejectsInvalidAutoRequeueOnAgentRestoreOptions(
        string scenario,
        string expectedFailure)
    {
        var options = ValidCodeyBoxOptions();
        switch (scenario)
        {
            case "lookback":
                options.AutoRequeueOnAgentRestore.LookbackGrace = "not-a-timespan";
                break;
            case "post-margin":
                options.AutoRequeueOnAgentRestore.PostRestoreMargin = "-00:00:01";
                break;
        }

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedFailure, result.FailureMessage);
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

    [Fact]
    public void Validate_RejectsNegativeCSharpTestPassAuditorIdleTimeout()
    {
        var options = ValidCodeyBoxOptions();
        options.PipelineTuning.CSharpTestPassAuditorIdleTimeout = TimeSpan.FromSeconds(-1);

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            "CodeyBox:PipelineTuning:CSharpTestPassAuditorIdleTimeout must be non-negative when set",
            result.FailureMessage);
    }

    [Fact]
    public void Validate_AllowsZeroCSharpTestPassAuditorIdleTimeout()
    {
        // The idle knob uses '< Zero' (zero disables the guard), unlike blame-hang.
        var options = ValidCodeyBoxOptions();
        options.PipelineTuning.CSharpTestPassAuditorIdleTimeout = TimeSpan.Zero;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validate_RejectsNonPositiveCSharpTestPassBlameHangTimeout()
    {
        // The blame-hang knob uses '<= Zero', so zero is rejected too.
        var options = ValidCodeyBoxOptions();
        options.PipelineTuning.CSharpTestPassBlameHangTimeout = TimeSpan.Zero;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            "CodeyBox:PipelineTuning:CSharpTestPassBlameHangTimeout must be positive when set",
            result.FailureMessage);

        options.PipelineTuning.CSharpTestPassBlameHangTimeout = TimeSpan.FromSeconds(-1);
        result = new CodeyBoxOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            "CodeyBox:PipelineTuning:CSharpTestPassBlameHangTimeout must be positive when set",
            result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsNegativeEmptyReworkEscalationRetries()
    {
        var options = ValidCodeyBoxOptions();
        options.PipelineTuning.EmptyReworkEscalationRetries = -1;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:PipelineTuning:EmptyReworkEscalationRetries must be non-negative", result.FailureMessage);
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

    [Fact]
    public void Validate_RejectsMissingMultipassRemoteSectionWhenProviderSelected()
    {
        var options = ValidCodeyBoxOptions();
        options.SandboxProvider = "multipass-remote";
        options.MultipassRemoteSandbox = null;

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:MultipassRemoteSandbox section is required", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsInvalidMultipassRemotePoolFields()
    {
        var options = ValidCodeyBoxOptions();
        options.SandboxProvider = "multipass-remote";
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            PlacementRecheckIn = TimeSpan.Zero,
            RuntimeUnhealthyBackoff = TimeSpan.FromSeconds(-1),
            StageOutMaxArchiveBytes = 0,
            StageOutMaxEntries = 0,
            StageOutMaxExpansionRatio = 0.5d,
            ExecutorHosts =
            [
                new MultipassRemoteExecutorHostConfig
                {
                    Id = "",
                    MaxConcurrentSandboxes = 0,
                    ServerAliveIntervalSeconds = 0,
                    ServerAliveCountMax = -1,
                    ConnectTimeoutSeconds = 0,
                    StageOutMaxArchiveBytes = -1,
                    StageOutMaxEntries = -1,
                    StageOutMaxExpansionRatio = double.NaN,
                    VmStartTimeout = TimeSpan.Zero,
                    VmStopTimeout = TimeSpan.Zero,
                    VmStateCheckInterval = TimeSpan.Zero,
                },
            ],
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("PlacementRecheckIn must be positive", result.FailureMessage);
        Assert.Contains("RuntimeUnhealthyBackoff must be positive", result.FailureMessage);
        Assert.Contains("StageOutMaxArchiveBytes must be > 0", result.FailureMessage);
        Assert.Contains("StageOutMaxEntries must be > 0", result.FailureMessage);
        Assert.Contains("StageOutMaxExpansionRatio must be >= 1", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:Id is required", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:SshTarget is required", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:MaxConcurrentSandboxes must be > 0", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:ServerAliveIntervalSeconds must be > 0", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:ServerAliveCountMax must be > 0", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:ConnectTimeoutSeconds must be > 0", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:StageOutMaxArchiveBytes must be > 0", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:StageOutMaxEntries must be > 0", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:StageOutMaxExpansionRatio must be >= 1", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:VmStartTimeout must be positive", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:VmStopTimeout must be positive", result.FailureMessage);
        Assert.Contains("ExecutorHosts:0:VmStateCheckInterval must be positive", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsTopLevelMultipassRemoteMaxConcurrentSandboxes()
    {
        var options = ValidCodeyBoxOptions();
        options.SandboxProvider = "multipass-remote";
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            SshTarget = "ubuntu@default",
            MaxConcurrentSandboxes = 0,
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CodeyBox:MultipassRemoteSandbox:MaxConcurrentSandboxes must be > 0", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsMissingTopLevelSshTargetWhenExecutorHostsAreEmpty()
    {
        var options = ValidCodeyBoxOptions();
        options.SandboxProvider = "multipass-remote";
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            SshTarget = " ",
            ExecutorHosts = [],
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("SshTarget is required when SandboxProvider=multipass-remote and ExecutorHosts is empty", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsDuplicateMultipassRemoteExecutorHostIds()
    {
        var options = ValidCodeyBoxOptions();
        options.SandboxProvider = "multipass-remote";
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            SshTarget = "ubuntu@default",
            ExecutorHosts =
            [
                new MultipassRemoteExecutorHostConfig { Id = "dup", SshTarget = "ubuntu@a" },
                new MultipassRemoteExecutorHostConfig { Id = " dup ", SshTarget = "ubuntu@b" },
            ],
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ExecutorHosts:1:Id duplicates another executor host id ('dup')", result.FailureMessage);
    }

    [Fact]
    public void Validate_AllowsBlankHostSshTargetWhenTopLevelTargetIsConfigured()
    {
        var options = ValidCodeyBoxOptions();
        options.SandboxProvider = "multipass-remote";
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig
        {
            SshTarget = "ubuntu@default",
            ExecutorHosts =
            [
                new MultipassRemoteExecutorHostConfig { Id = "a", SshTarget = "   ", MaxConcurrentSandboxes = 1 },
            ],
        };

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Fact]
    public void MultipassRemoteOptionsMapper_maps_executor_hosts_from_bound_config()
    {
        var cfg = new MultipassRemoteSandboxConfig
        {
            SshTarget = "ubuntu@default",
            SshBinary = "/usr/bin/ssh",
            SshPort = 2222,
            SshKeyPath = "/keys/default",
            ExtraSshOptions = ["Compression=yes"],
            AcceptUnknownHostKeys = true,
            ServerAliveIntervalSeconds = 21,
            ServerAliveCountMax = 22,
            ConnectTimeoutSeconds = 23,
            LocalTarBinary = "/usr/local/bin/tar",
            StageOutMaxArchiveBytes = 123_456,
            StageOutMaxEntries = 123,
            StageOutMaxExpansionRatio = 1.25d,
            RemoteMultipassPath = "/remote/multipass",
            RemoteStagingRoot = "/stage/default",
            DefaultImage = "22.04",
            VmStartTimeout = TimeSpan.FromSeconds(24),
            VmStopTimeout = TimeSpan.FromSeconds(25),
            VmStateCheckInterval = TimeSpan.FromSeconds(26),
            VmNamePrefix = "cb-default-",
            MaxConcurrentSandboxes = 9,
            Cordoned = true,
            Healthy = false,
            AllowedNetworkProfiles = ["default-work"],
            PlacementRecheckIn = TimeSpan.FromSeconds(7),
            RuntimeUnhealthyBackoff = TimeSpan.FromSeconds(8),
            ExecutorHosts =
            [
                new MultipassRemoteExecutorHostConfig
                {
                    Id = "a",
                    SshTarget = "ubuntu@a",
                    SshBinary = "/custom/ssh",
                    SshPort = 2201,
                    SshKeyPath = "/keys/a",
                    ExtraSshOptions = ["BatchMode=yes"],
                    AcceptUnknownHostKeys = true,
                    ServerAliveIntervalSeconds = 11,
                    ServerAliveCountMax = 12,
                    ConnectTimeoutSeconds = 13,
                    LocalTarBinary = "/bin/tar",
                    StageOutMaxArchiveBytes = 654_321,
                    StageOutMaxEntries = 321,
                    StageOutMaxExpansionRatio = 1.75d,
                    RemoteMultipassPath = "/usr/bin/multipass",
                    RemoteStagingRoot = "/stage/a",
                    DefaultImage = "24.04",
                    VmStartTimeout = TimeSpan.FromSeconds(14),
                    VmStopTimeout = TimeSpan.FromSeconds(15),
                    VmStateCheckInterval = TimeSpan.FromSeconds(16),
                    VmNamePrefix = "cb-a-",
                    MaxConcurrentSandboxes = 2,
                    Cordoned = true,
                    Healthy = false,
                    AllowedNetworkProfiles = ["work", "audit"],
                },
            ],
        };

        var mapped = MultipassRemoteOptionsMapper.Map(
            cfg,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["work"] = "cb-work",
                ["audit"] = "cb-audit",
            });
        var host = Assert.Single(mapped.ExecutorHosts);

        Assert.Equal("ubuntu@default", mapped.SshTarget);
        Assert.Equal("/usr/bin/ssh", mapped.SshBinary);
        Assert.Equal(2222, mapped.SshPort);
        Assert.Equal("/keys/default", mapped.SshKeyPath);
        Assert.Equal(["Compression=yes"], mapped.ExtraSshOptions);
        Assert.True(mapped.AcceptUnknownHostKeys);
        Assert.Equal(21, mapped.ServerAliveIntervalSeconds);
        Assert.Equal(22, mapped.ServerAliveCountMax);
        Assert.Equal(23, mapped.ConnectTimeoutSeconds);
        Assert.Equal("/usr/local/bin/tar", mapped.LocalTarBinary);
        Assert.Equal(123_456, mapped.StageOutMaxArchiveBytes);
        Assert.Equal(123, mapped.StageOutMaxEntries);
        Assert.Equal(1.25d, mapped.StageOutMaxExpansionRatio);
        Assert.Equal("/remote/multipass", mapped.RemoteMultipassPath);
        Assert.Equal("/stage/default", mapped.RemoteStagingRoot);
        Assert.Equal("22.04", mapped.DefaultImage);
        Assert.Equal(TimeSpan.FromSeconds(24), mapped.VmStartTimeout);
        Assert.Equal(TimeSpan.FromSeconds(25), mapped.VmStopTimeout);
        Assert.Equal(TimeSpan.FromSeconds(26), mapped.VmStateCheckInterval);
        Assert.Equal("cb-default-", mapped.VmNamePrefix);
        Assert.Equal(9, mapped.MaxConcurrentSandboxes);
        Assert.True(mapped.Cordoned);
        Assert.False(mapped.Healthy);
        Assert.Equal(["default-work"], mapped.AllowedNetworkProfiles);
        Assert.Equal("cb-work", mapped.NetworkProfiles["work"]);
        Assert.Equal("cb-audit", mapped.NetworkProfiles["audit"]);
        Assert.Equal(TimeSpan.FromSeconds(7), mapped.PlacementRecheckIn);
        Assert.Equal(TimeSpan.FromSeconds(8), mapped.RuntimeUnhealthyBackoff);
        Assert.Equal("a", host.Id);
        Assert.Equal("ubuntu@a", host.SshTarget);
        Assert.Equal("/custom/ssh", host.SshBinary);
        Assert.Equal(2201, host.SshPort);
        Assert.Equal("/keys/a", host.SshKeyPath);
        Assert.Equal(["BatchMode=yes"], host.ExtraSshOptions);
        Assert.True(host.AcceptUnknownHostKeys);
        Assert.Equal(11, host.ServerAliveIntervalSeconds);
        Assert.Equal(12, host.ServerAliveCountMax);
        Assert.Equal(13, host.ConnectTimeoutSeconds);
        Assert.Equal("/bin/tar", host.LocalTarBinary);
        Assert.Equal(654_321, host.StageOutMaxArchiveBytes);
        Assert.Equal(321, host.StageOutMaxEntries);
        Assert.Equal(1.75d, host.StageOutMaxExpansionRatio);
        Assert.Equal("/usr/bin/multipass", host.RemoteMultipassPath);
        Assert.Equal("/stage/a", host.RemoteStagingRoot);
        Assert.Equal("24.04", host.DefaultImage);
        Assert.Equal(TimeSpan.FromSeconds(14), host.VmStartTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), host.VmStopTimeout);
        Assert.Equal(TimeSpan.FromSeconds(16), host.VmStateCheckInterval);
        Assert.Equal("cb-a-", host.VmNamePrefix);
        Assert.Equal(2, host.MaxConcurrentSandboxes);
        Assert.True(host.Cordoned);
        Assert.False(host.Healthy);
        Assert.Equal(["work", "audit"], host.AllowedNetworkProfiles);
    }

    // ----- Enabled remote-ssh E2E: BaselineImageRef + fail-closed DNS -----

    [Fact]
    public void Validate_AcceptsFullyValidEnabledRemoteE2eConfig()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = null;
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "e2e@198.51.100.10" };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "cb-e2e";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.False(result.Failed, result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEnabledRemoteE2eWhenBaselineImageRefIsBlank()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = null;
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "e2e@198.51.100.10" };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "   "; // nothing to clone per run

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("BaselineImageRef is required", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsEnabledRemoteE2eWhenSshTargetHostIsUnresolvable()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = null;
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "e2e@codeybox-e2e-nonexistent.invalid" };
        options.E2eExecution.Enabled = true;
        options.E2eExecution.PoolKind = "remote-ssh";
        options.E2eExecution.BaselineImageRef = "cb-e2e";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("dedicated resolvable remote SSH host", result.FailureMessage);
    }

    [Fact]
    public void Validate_RejectsConfiguredRemoteE2eWhenCodingHostIsUnresolvable()
    {
        var options = ValidCodeyBoxOptions();
        options.MultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "coding@codeybox-coding-nonexistent.invalid" };
        options.E2eMultipassRemoteSandbox = new MultipassRemoteSandboxConfig { SshTarget = "e2e@198.51.100.10" };
        options.E2eExecution.Enabled = false; // lifecycle-isolation check runs even when disabled
        options.E2eExecution.PoolKind = "remote-ssh";

        var result = new CodeyBoxOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("must be resolvable to verify fleet isolation", result.FailureMessage);
    }

    // ----- OpenSshConfigResolver.TryResolveHostName branch coverage -----

    [Fact]
    public void OpenSshConfigResolver_returns_false_for_blank_ssh_target()
    {
        var resolver = new OpenSshConfigResolver(new RecordingProcessRunner(new ProcessRunResult(0, "hostname h\n", string.Empty)));

        Assert.False(resolver.TryResolveHostName(new MultipassRemoteSandboxConfig { SshTarget = "   " }, out var host));
        Assert.Null(host);
    }

    [Fact]
    public void OpenSshConfigResolver_returns_false_when_ssh_exits_nonzero()
    {
        var resolver = new OpenSshConfigResolver(new RecordingProcessRunner(new ProcessRunResult(255, "hostname h\n", "boom")));

        Assert.False(resolver.TryResolveHostName(new MultipassRemoteSandboxConfig { SshTarget = "e2e@host" }, out var host));
        Assert.Null(host);
    }

    [Fact]
    public void OpenSshConfigResolver_returns_false_when_stdout_limit_exceeded()
    {
        var resolver = new OpenSshConfigResolver(
            new RecordingProcessRunner(new ProcessRunResult(0, "hostname h\n", string.Empty, StdoutLimitExceeded: true)));

        Assert.False(resolver.TryResolveHostName(new MultipassRemoteSandboxConfig { SshTarget = "e2e@host" }, out var host));
        Assert.Null(host);
    }

    [Fact]
    public void OpenSshConfigResolver_returns_false_when_runner_throws()
    {
        var resolver = new OpenSshConfigResolver(new ThrowingProcessRunner());

        Assert.False(resolver.TryResolveHostName(new MultipassRemoteSandboxConfig { SshTarget = "e2e@host" }, out var host));
        Assert.Null(host);
    }

    [Fact]
    public void OpenSshConfigResolver_returns_false_when_no_hostname_line_present()
    {
        var resolver = new OpenSshConfigResolver(new RecordingProcessRunner(new ProcessRunResult(0, "user e2e\nport 22\n", string.Empty)));

        Assert.False(resolver.TryResolveHostName(new MultipassRemoteSandboxConfig { SshTarget = "e2e@host" }, out var host));
        Assert.Null(host);
    }

    [Fact]
    public void OpenSshConfigResolver_parses_hostname_line_on_success()
    {
        var resolver = new OpenSshConfigResolver(
            new RecordingProcessRunner(new ProcessRunResult(0, "user e2e\nhostname resolved.example\nport 22\n", string.Empty)));

        Assert.True(resolver.TryResolveHostName(new MultipassRemoteSandboxConfig { SshTarget = "e2e@alias" }, out var host));
        Assert.Equal("resolved.example", host);
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

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
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
            => Task.FromException<ProcessRunResult>(new InvalidOperationException("ssh -G launch failed"));
    }
}
