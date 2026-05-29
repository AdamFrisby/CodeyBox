using CodeyBox.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CodeyBox.Notifications;

/// <summary>
/// Delivers notifications via SMTP using MailKit. SMTP credentials are
/// read from the environment variable named by
/// <see cref="EmailProviderOptions.PasswordEnvVar"/> — never hardcoded.
/// Unconfigured providers are safe no-ops: if <c>Enabled</c> is false,
/// <see cref="SendAsync"/> returns immediately.
/// </summary>
public sealed class EmailNotificationProvider : INotificationProvider, IDisposable
{
    private readonly EmailProviderOptions _opts;
    private readonly ILogger<EmailNotificationProvider> _log;
    private readonly Func<SmtpClient> _smtpClientFactory;

    public string Name => "email";

    public EmailNotificationProvider(
        EmailProviderOptions opts,
        ILogger<EmailNotificationProvider> log,
        Func<SmtpClient>? smtpClientFactory = null)
    {
        _opts = opts;
        _log = log;
        _smtpClientFactory = smtpClientFactory ?? (() => new SmtpClient());
    }

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        if (!_opts.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(_opts.Host))
        {
            _log.LogWarning("EmailNotificationProvider: Host is not configured; skipping notification {Condition}",
                notification.ConditionId);
            return;
        }

        string? password = null;
        if (!string.IsNullOrEmpty(_opts.PasswordEnvVar))
        {
            password = Environment.GetEnvironmentVariable(_opts.PasswordEnvVar);
            if (password is null)
                _log.LogWarning(
                    "EmailNotificationProvider: PasswordEnvVar '{EnvVar}' is configured but not set in environment",
                    _opts.PasswordEnvVar);
        }

        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress("CodeyBox", _opts.From));
        message.To.Add(new MailboxAddress("Operator", _opts.From));
        message.Subject = $"[CodeyBox/{notification.Severity}] {notification.Title}";

        var body = new TextPart("plain")
        {
            Text = notification.Body
                ?? notification.Summary
                ?? notification.Title,
        };
        message.Body = body;

        // Add structured fields as X- headers for threading/filtering.
        message.Headers.Add("X-CodeyBox-Condition", notification.ConditionId);
        message.Headers.Add("X-CodeyBox-Severity", notification.Severity.ToString());
        if (notification.Fields is not null)
        {
            foreach (var (key, value) in notification.Fields)
                message.Headers.Add($"X-CodeyBox-{key.Replace(' ', '-')}", value);
        }

        using var client = _smtpClientFactory();

        try
        {
            if (_opts.IgnoreCertificateErrors)
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;

            var secureOption = _opts.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(_opts.Host, _opts.Port, secureOption, ct);

            if (!string.IsNullOrEmpty(_opts.User) && password is not null)
                await client.AuthenticateAsync(_opts.User, password, ct);

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            _log.LogInformation("EmailNotificationProvider: sent notification {Condition} ({Severity})",
                notification.ConditionId, notification.Severity);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "EmailNotificationProvider: failed to send notification {Condition}",
                notification.ConditionId);
        }
    }

    public void Dispose()
    {
    }
}
