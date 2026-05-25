using CodeyBox.Api;
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
