using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public class EmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<EmailSettings> settings, ILogger<EmailService> logger)
    {
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SendEmailAsync(string toEmail, string subject, string htmlBody, string? textBody = null, string? debugOtp = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("OTP EMAIL STARTED");

        if (string.IsNullOrWhiteSpace(toEmail)) throw new ArgumentException("Recipient email is required.", nameof(toEmail));
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("Email subject is required.", nameof(subject));
        if (string.IsNullOrWhiteSpace(htmlBody)) throw new ArgumentException("Email body is required.", nameof(htmlBody));

        if (string.IsNullOrWhiteSpace(_settings.Host) || _settings.Port <= 0)
        {
            _logger.LogError("SMTP host or port is not configured. Host={Host} Port={Port}", _settings.Host, _settings.Port);
            throw new InvalidOperationException("SMTP host or port is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_settings.Email) || string.IsNullOrWhiteSpace(_settings.Password))
        {
            _logger.LogWarning("SMTP credentials are missing. Email sends will fail until EmailSettings__Email and EmailSettings__Password are set.");
            throw new InvalidOperationException("SMTP credentials are not configured.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.DisplayName?.Trim() ?? _settings.Email.Trim(), _settings.Email.Trim()));
        message.To.Add(MailboxAddress.Parse(toEmail.Trim()));
        message.Subject = subject.Trim();

        var bodyBuilder = new BodyBuilder
        {
            HtmlBody = htmlBody,
            TextBody = textBody ?? StripHtml(htmlBody),
        };

        message.Body = bodyBuilder.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            _logger.LogInformation("SMTP CONNECTING");
            await client.ConnectAsync(_settings.Host.Trim(), _settings.Port, SecureSocketOptions.StartTls, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("SMTP CONNECTED");

            _logger.LogInformation("AUTHENTICATING");
            await client.AuthenticateAsync(_settings.Email.Trim(), _settings.Password, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("SMTP AUTH SUCCESS");

            if (!string.IsNullOrWhiteSpace(debugOtp))
            {
                _logger.LogInformation("OTP GENERATED: {Otp}", debugOtp);
            }

            _logger.LogInformation("SENDING EMAIL");
            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("EMAIL SENT SUCCESSFULLY");

            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("SMTP DISCONNECTED");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EMAIL SERVICE FAILURE while sending to {Recipient} with subject {Subject}", toEmail, subject);
            try { if (client.IsConnected) await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false); } catch { /* best-effort */ }
            throw;
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
    }
}