using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;

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
}
