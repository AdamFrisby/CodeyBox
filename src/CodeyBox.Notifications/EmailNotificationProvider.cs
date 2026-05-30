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
public sealed class EmailNotificationProvider : INotificationProvider
{
    private const int SmtpsPort = 465;

    private readonly Func<EmailProviderOptions> _optsAccessor;
    private readonly ILogger<EmailNotificationProvider> _log;
    private readonly Func<SmtpClient> _smtpClientFactory;
    private readonly bool _isDevelopment;

    public string Name => "email";

    public EmailNotificationProvider(
        EmailProviderOptions opts,
        ILogger<EmailNotificationProvider> log,
        Func<SmtpClient>? smtpClientFactory = null,
        bool isDevelopment = false)
    {
        _optsAccessor = () => opts;
        _log = log;
        _smtpClientFactory = smtpClientFactory ?? (() => new SmtpClient());
        _isDevelopment = isDevelopment;
    }

    public EmailNotificationProvider(
        Func<EmailProviderOptions> optsAccessor,
        ILogger<EmailNotificationProvider> log,
        Func<SmtpClient>? smtpClientFactory = null,
        bool isDevelopment = false)
    {
        _optsAccessor = optsAccessor;
        _log = log;
        _smtpClientFactory = smtpClientFactory ?? (() => new SmtpClient());
        _isDevelopment = isDevelopment;
    }

    private static string SanitizeHeaderValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;
        return value.Replace("\r", "").Replace("\n", "").Replace("\0", "");
    }

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var opts = _optsAccessor();
        if (!opts.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(opts.Host))
        {
            _log.LogWarning("EmailNotificationProvider: Host is not configured; skipping notification {Condition}",
                notification.ConditionId);
            return;
        }

        string? password = null;
        if (!string.IsNullOrEmpty(opts.PasswordEnvVar))
        {
            password = Environment.GetEnvironmentVariable(opts.PasswordEnvVar);
            if (password is null)
                _log.LogWarning(
                    "EmailNotificationProvider: PasswordEnvVar '{EnvVar}' is configured but not set in environment",
                    opts.PasswordEnvVar);
        }

        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress("CodeyBox", opts.From));

        var recipients = notification.Recipients;
        if (recipients is { Count: > 0 })
        {
            foreach (var recipient in recipients)
                message.To.Add(MailboxAddress.Parse(recipient));
        }
        else
        {
            message.To.Add(new MailboxAddress("Operator", opts.From));
        }
        message.Subject = $"[CodeyBox/{notification.Severity}] {notification.Title}";

        var body = new TextPart("plain")
        {
            Text = notification.Body
                ?? notification.Summary
                ?? notification.Title,
        };
        message.Body = body;

        message.Headers.Add("X-CodeyBox-Condition", notification.ConditionId);
        message.Headers.Add("X-CodeyBox-Severity", notification.Severity.ToString());
        if (notification.Fields is not null)
        {
            foreach (var (key, value) in notification.Fields)
            {
                var safeKey = SanitizeHeaderValue(key).Replace(' ', '-');
                var safeValue = SanitizeHeaderValue(value);
                message.Headers.Add($"X-CodeyBox-{safeKey}", safeValue);
            }
        }

        using var client = _smtpClientFactory();

        try
        {
            if (opts.IgnoreCertificateErrors)
            {
                if (!_isDevelopment)
                {
                    _log.LogError(
                        "EmailNotificationProvider: IgnoreCertificateErrors is enabled in a non-Development environment. " +
                        "Refusing to disable TLS certificate validation. The notification will be sent with normal TLS verification.");
                }
                else
                {
                    _log.LogWarning("EmailNotificationProvider: IgnoreCertificateErrors is enabled — TLS certificate validation is disabled");
                    client.ServerCertificateValidationCallback = (_, _, _, _) => true;
                }
            }

            var secureOption = opts.Port == SmtpsPort
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(opts.Host, opts.Port, secureOption, ct);

            if (!string.IsNullOrEmpty(opts.User) && password is not null)
                await client.AuthenticateAsync(opts.User, password, ct);

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            _log.LogInformation("EmailNotificationProvider: sent notification {Condition} ({Severity})",
                notification.ConditionId, notification.Severity);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "EmailNotificationProvider: failed to send notification {Condition}",
                notification.ConditionId);
        }
    }
}
