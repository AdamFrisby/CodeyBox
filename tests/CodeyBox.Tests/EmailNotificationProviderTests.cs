using CodeyBox.Core;
using CodeyBox.Notifications;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CodeyBox.Tests;

public sealed class EmailNotificationProviderTests
{
    private static Notification MakeNotification(string conditionId = "test", string title = "Test",
        NotificationSeverity severity = NotificationSeverity.Information,
        IReadOnlyList<string>? recipients = null)
        => new()
        {
            ConditionId = conditionId,
            Title = title,
            Severity = severity,
            Recipients = recipients,
        };

    [Fact]
    public async Task Disabled_ReturnsImmediatelyWithoutConnecting()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        var factoryCalled = false;
        var opts = new EmailProviderOptions
        {
            Enabled = false,
            Host = "localhost",
            Port = 587,
        };
        var provider = new EmailNotificationProvider(
            opts,
            logger,
            () => { factoryCalled = true; return new SmtpClient(); });

        await provider.SendAsync(MakeNotification(), CancellationToken.None);
        Assert.False(factoryCalled);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task EmptyHost_LogsWarningAndReturns()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        var factoryCalled = false;
        var opts = new EmailProviderOptions
        {
            Enabled = true,
            Host = "",
            Port = 587,
        };
        var provider = new EmailNotificationProvider(
            opts,
            logger,
            () => { factoryCalled = true; return new SmtpClient(); });

        await provider.SendAsync(MakeNotification(), CancellationToken.None);
        Assert.False(factoryCalled);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("Host is not configured"));
    }

    [Fact]
    public async Task CannotConnect_LogsErrorWithoutThrowing()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        var opts = new EmailProviderOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 9999, // unbound port
            From = "codeybox@test.local",
        };

        var provider = new EmailNotificationProvider(
            opts,
            logger);

        await provider.SendAsync(MakeNotification(), CancellationToken.None);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error
            && e.Message.Contains("failed to send notification"));
    }

    [Fact]
    public async Task SendsToConfiguredRecipientsWhenPresent()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        var client = new CaptureSmtpClient();

        var provider = new EmailNotificationProvider(
            new EmailProviderOptions
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 587,
                From = "bot@test.local",
            },
            logger,
            () => client);

        await provider.SendAsync(
            MakeNotification(recipients: ["alice@test.local", "bob@test.local"]),
            CancellationToken.None);

        var snap = client.Snapshot;
        Assert.NotNull(snap);
        Assert.Equal(2, snap!.ToAddresses.Count);
        Assert.Contains("alice@test.local", snap.ToAddresses);
        Assert.Contains("bob@test.local", snap.ToAddresses);
        Assert.Equal("bot@test.local", snap.FromAddress);
    }

    [Fact]
    public async Task FallsBackToFromWhenNoRecipients()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        var client = new CaptureSmtpClient();

        var provider = new EmailNotificationProvider(
            new EmailProviderOptions
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 587,
                From = "bot@test.local",
            },
            logger,
            () => client);

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        var snap = client.Snapshot;
        Assert.NotNull(snap);
        Assert.Single(snap!.ToAddresses);
        Assert.Equal("bot@test.local", snap.ToAddresses[0]);
    }

    [Fact]
    public async Task MockedSmtp_VerifiesMessageConstruction()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        var client = new CaptureSmtpClient();

        var provider = new EmailNotificationProvider(
            new EmailProviderOptions
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 587,
                From = "codeybox@test.local",
            },
            logger,
            () => client);

        var notification = new Notification
        {
            ConditionId = "queue_empty",
            Title = "Queue is empty",
            Body = "All work items have been processed.",
            Summary = "No work items remaining.",
            Severity = NotificationSeverity.Warning,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["active_count"] = "0",
            },
            Recipients = ["ops@test.local"],
        };

        await provider.SendAsync(notification, CancellationToken.None);

        var snap = client.Snapshot;
        Assert.NotNull(snap);
        Assert.Equal("[CodeyBox/Warning] Queue is empty", snap!.Subject);
        Assert.Single(snap.ToAddresses);
        Assert.Equal("ops@test.local", snap.ToAddresses[0]);
        Assert.Equal("codeybox@test.local", snap.FromAddress);
        Assert.Equal("All work items have been processed.", snap.BodyText);
        Assert.Equal("queue_empty", snap.Headers["X-CodeyBox-Condition"]);
        Assert.Equal("Warning", snap.Headers["X-CodeyBox-Severity"]);
        Assert.Equal("0", snap.Headers["X-CodeyBox-active_count"]);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("sent notification"));
    }

    [Fact]
    public async Task PasswordEnvVarUnset_LogsWarning()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();

        var provider = new EmailNotificationProvider(
            new EmailProviderOptions
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 587,
                From = "bot@test.local",
                User = "smtp-user",
                PasswordEnvVar = "CODEYBOX_MISSING_ENV_VAR_FOR_TEST",
            },
            logger,
            () => new CaptureSmtpClient());

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("PasswordEnvVar")
            && e.Message.Contains("not set in environment"));
    }

    [Fact]
    public async Task ConnectUsesSslOnConnectForPort465()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        string? capturedHost = null;
        int capturedPort = 0;
        SecureSocketOptions capturedOptions = SecureSocketOptions.Auto;

        var provider = new EmailNotificationProvider(
            new EmailProviderOptions
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 465,
                From = "bot@test.local",
            },
            logger,
            () => new CaptureSmtpClient(connectCaptor: (host, port, options) =>
            {
                capturedHost = host;
                capturedPort = port;
                capturedOptions = options;
            }));

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Equal("smtp.test", capturedHost);
        Assert.Equal(465, capturedPort);
        Assert.Equal(SecureSocketOptions.SslOnConnect, capturedOptions);
    }

    [Fact]
    public async Task ConnectUsesStartTlsForPort587()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        SecureSocketOptions capturedOptions = SecureSocketOptions.Auto;

        var provider = new EmailNotificationProvider(
            new EmailProviderOptions
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 587,
                From = "bot@test.local",
            },
            logger,
            () => new CaptureSmtpClient(connectCaptor: (_, _, options) =>
            {
                capturedOptions = options;
            }));

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Equal(SecureSocketOptions.StartTls, capturedOptions);
    }

    [Fact]
    public void ProviderName_IsEmail()
    {
        var opts = new EmailProviderOptions { Enabled = false };
        var provider = new EmailNotificationProvider(
            opts,
            new CapturingLogger<EmailNotificationProvider>());
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

/// <summary>
/// Test-only SmtpClient subclass that captures method calls without real I/O.
/// Overrides the virtual methods on MailKit's SmtpClient to record arguments.
/// Message content is snapshotted during SendAsync so tests can assert after
/// the provider disposes the MimeMessage.
/// </summary>
internal sealed class CaptureSmtpClient : SmtpClient
{
    private readonly Action<MimeMessage>? _sendCaptor;
    private readonly Action<string, int, SecureSocketOptions>? _connectCaptor;

    public sealed record CapturedSnapshot(
        string Subject,
        IReadOnlyList<string> ToAddresses,
        string FromAddress,
        string? BodyText,
        IReadOnlyDictionary<string, string> Headers);

    public CapturedSnapshot? Snapshot { get; private set; }

    public CaptureSmtpClient(
        Action<MimeMessage>? sendCaptor = null,
        Action<string, int, SecureSocketOptions>? connectCaptor = null)
    {
        _sendCaptor = sendCaptor;
        _connectCaptor = connectCaptor;
    }

    public override Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
    {
        _connectCaptor?.Invoke(host, port, options);
        return Task.CompletedTask;
    }

    public override Task<string?> SendAsync(MimeMessage message, CancellationToken cancellationToken = default, ITransferProgress? progress = null)
    {
        var toAddresses = message.To.Mailboxes.Select(mb => mb.Address).ToList();
        var fromAddress = message.From.Mailboxes.First().Address;
        var bodyText = message.Body is TextPart tp ? tp.Text : null;
        var headers = message.Headers.ToDictionary(h => h.Field, h => h.Value, StringComparer.Ordinal);
        Snapshot = new CapturedSnapshot(message.Subject, toAddresses, fromAddress, bodyText, headers);
        _sendCaptor?.Invoke(message);
        return Task.FromResult<string?>("ok");
    }

    public override Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
