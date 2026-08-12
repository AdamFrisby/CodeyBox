using CodeyBox.Core;
using CodeyBox.Notifications;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using System.Net;
using System.Text;

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

    [Fact]
    public async Task IgnoreCertificateErrors_Development_LogsWarning()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();

        var provider = new EmailNotificationProvider(
            () => new EmailProviderOptions
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 587,
                From = "bot@test.local",
                IgnoreCertificateErrors = true,
            },
            logger,
            () => new CaptureSmtpClient(),
            isDevelopment: true);

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("IgnoreCertificateErrors")
            && e.Message.Contains("disabled"));
    }

    [Fact]
    public async Task IgnoreCertificateErrors_NotDevelopment_LogsErrorAndRefuses()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();

        var provider = new EmailNotificationProvider(
            () => new EmailProviderOptions
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 587,
                From = "bot@test.local",
                IgnoreCertificateErrors = true,
            },
            logger,
            () => new CaptureSmtpClient(),
            isDevelopment: false);

        await provider.SendAsync(MakeNotification(), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error
            && e.Message.Contains("IgnoreCertificateErrors")
            && e.Message.Contains("non-Development"));
    }

    [Fact]
    public async Task AuthenticateAsync_CalledWithCorrectCredentials()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        string? capturedUser = null;
        string? capturedPassword = null;

        var client = new CaptureSmtpClient(authCaptor: (user, pwd) =>
        {
            capturedUser = user;
            capturedPassword = pwd;
        });

        Environment.SetEnvironmentVariable("CODEYBOX_SMTP_TEST_PWD", "test-password");
        try
        {
            var provider = new EmailNotificationProvider(
                new EmailProviderOptions
                {
                    Enabled = true,
                    Host = "smtp.test",
                    Port = 587,
                    From = "bot@test.local",
                    User = "smtp-user",
                    PasswordEnvVar = "CODEYBOX_SMTP_TEST_PWD",
                },
                logger,
                () => client);

            await provider.SendAsync(MakeNotification(), CancellationToken.None);

            Assert.Equal("smtp-user", capturedUser);
            Assert.Equal("test-password", capturedPassword);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_SMTP_TEST_PWD", null);
        }
    }

    [Fact]
    public async Task OperationCanceledException_Rethrows()
    {
        var logger = new CapturingLogger<EmailNotificationProvider>();
        var oce = new OperationCanceledException("shutdown");

        var provider = new EmailNotificationProvider(
            new EmailProviderOptions
            {
                Enabled = true,
                Host = "smtp.test",
                Port = 587,
                From = "bot@test.local",
            },
            logger,
            () => new CaptureSmtpClient(sendCaptor: _ => throw oce));

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.SendAsync(MakeNotification(), CancellationToken.None));
        Assert.Same(oce, ex);
    }

    [Fact]
    public async Task HeaderValues_StripControlCharacters()
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

        var notification = new Notification
        {
            ConditionId = "test",
            Title = "Test",
            Severity = NotificationSeverity.Information,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["field_with_crlf"] = "value\r\nwith\r\ncontrol\rchars",
            },
        };

        await provider.SendAsync(notification, CancellationToken.None);

        var snap = client.Snapshot;
        Assert.NotNull(snap);
        Assert.Equal("valuewithcontrolchars", snap!.Headers["X-CodeyBox-field_with_crlf"]);
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
    private readonly Action<string, string>? _authCaptor;

    public sealed record CapturedSnapshot(
        string Subject,
        IReadOnlyList<string> ToAddresses,
        string FromAddress,
        string? BodyText,
        IReadOnlyDictionary<string, string> Headers);

    public CapturedSnapshot? Snapshot { get; private set; }

    public CaptureSmtpClient(
        Action<MimeMessage>? sendCaptor = null,
        Action<string, int, SecureSocketOptions>? connectCaptor = null,
        Action<string, string>? authCaptor = null)
    {
        _sendCaptor = sendCaptor;
        _connectCaptor = connectCaptor;
        _authCaptor = authCaptor;
    }

    public override Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
    {
        _connectCaptor?.Invoke(host, port, options);
        return Task.CompletedTask;
    }

    public override Task AuthenticateAsync(Encoding encoding, ICredentials credentials, CancellationToken cancellationToken = default)
    {
        if (credentials is NetworkCredential nc)
            _authCaptor?.Invoke(nc.UserName, nc.Password);
        return Task.CompletedTask;
    }

    public override Task AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken = default)
    {
        if (mechanism is SaslMechanismPlain plain)
            _authCaptor?.Invoke(plain.Credentials.UserName ?? string.Empty, plain.Credentials.Password ?? string.Empty);
        return Task.CompletedTask;
    }

    public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken = default, ITransferProgress? progress = null)
    {
        var toAddresses = message.To.Mailboxes.Select(mb => mb.Address).ToList();
        var fromAddress = message.From.Mailboxes.First().Address;
        var bodyText = message.Body is TextPart tp ? tp.Text : null;
        var headers = message.Headers.ToDictionary(h => h.Field, h => h.Value, StringComparer.Ordinal);
        Snapshot = new CapturedSnapshot(message.Subject ?? string.Empty, toAddresses, fromAddress, bodyText, headers);
        _sendCaptor?.Invoke(message);
        return Task.FromResult("ok");
    }

    public override Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
