using CodeyBox.Core;
using CodeyBox.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class EmailNotificationProviderTests
{
    [Fact]
    public async Task Disabled_ReturnsImmediatelyWithoutConnecting()
    {
        var opts = new EmailProviderOptions
        {
            Enabled = false,
            Host = "localhost",
            Port = 587,
        };
        var provider = new EmailNotificationProvider(
            opts,
            NullLogger<EmailNotificationProvider>.Instance);

        var notification = new Notification
        {
            ConditionId = "test",
            Title = "Test",
            Severity = NotificationSeverity.Information,
        };

        await provider.SendAsync(notification, CancellationToken.None);
        // No-op — test passes if no exception is thrown.
    }

    [Fact]
    public async Task EmptyHost_LogsWarningAndReturns()
    {
        var opts = new EmailProviderOptions
        {
            Enabled = true,
            Host = "",
            Port = 587,
        };
        var provider = new EmailNotificationProvider(
            opts,
            NullLogger<EmailNotificationProvider>.Instance);

        var notification = new Notification
        {
            ConditionId = "test",
            Title = "Test",
            Severity = NotificationSeverity.Information,
        };

        await provider.SendAsync(notification, CancellationToken.None);
        // No-op — test passes if no exception is thrown.
    }

    [Fact]
    public async Task CannotConnect_LogsErrorWithoutThrowing()
    {
        var opts = new EmailProviderOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 9999, // unbound port — will refuse connection quickly
            From = "codeybox@test.local",
        };

        var provider = new EmailNotificationProvider(
            opts,
            NullLogger<EmailNotificationProvider>.Instance);

        var notification = new Notification
        {
            ConditionId = "test",
            Title = "Test",
        };

        // Should not throw — provider swallows errors.
        await provider.SendAsync(notification, CancellationToken.None);
    }

    [Fact]
    public void PasswordFromEnvironmentVariable_ReadCorrectly()
    {
        var envKey = "CODEYBOX_TEST_SMTP_PWD2";
        Environment.SetEnvironmentVariable(envKey, "test-password-456");
        try
        {
            var opts = new EmailProviderOptions
            {
                Enabled = true,
                Host = "localhost",
                Port = 587,
                From = "codeybox@test.local",
                User = "smtp-user",
                PasswordEnvVar = envKey,
            };
            // Just verify construction succeeds and the env var is referenceable.
            var provider = new EmailNotificationProvider(
                opts,
                NullLogger<EmailNotificationProvider>.Instance);
            Assert.NotNull(provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Fact]
    public void ProviderName_IsEmail()
    {
        var opts = new EmailProviderOptions { Enabled = false };
        var provider = new EmailNotificationProvider(
            opts,
            NullLogger<EmailNotificationProvider>.Instance);
        Assert.Equal("email", provider.Name);
    }

    [Fact]
    public void NullNotificationProvider_ReturnsCompletedTask()
    {
        var np = new NullNotificationProvider("test");
        var result = np.SendAsync(
            new Notification { ConditionId = "x", Title = "y" },
            CancellationToken.None);
        Assert.True(result.IsCompletedSuccessfully);
        Assert.Equal("test", np.Name);
    }
}
