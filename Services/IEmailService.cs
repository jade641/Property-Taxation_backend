namespace PropertyTax.API.Services;

public interface IEmailService
{
    Task SendEmailAsync(string toEmail, string subject, string htmlBody, string? textBody = null, string? debugOtp = null, CancellationToken cancellationToken = default);
}
