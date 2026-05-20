using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PropertyTax.API.Models;
using PropertyTax.API.Services;

namespace PropertyTax.API.Controllers;

[ApiController]
[Route("api/debug")]
public class DebugController : ControllerBase
{
    private readonly IEmailService _emailService;
    private readonly UserManager<ApplicationUser> _userManager;

    public DebugController(IEmailService emailService, UserManager<ApplicationUser> userManager)
    {
        _emailService = emailService;
        _userManager = userManager;
    }

    // Authenticated users can retrieve their current JWT claims for debugging role/claim issues.
    [Authorize]
    [HttpGet("claims")]
    public ActionResult<object> GetClaims()
    {
        var claims = User.Claims.Select(c => new { c.Type, c.Value }).ToList();
        return Ok(new { success = true, claims });
    }

    // Admin-only endpoint to send a test SMTP email to the specified email.
    [Authorize(Roles = SystemRoles.Admin)]
    [HttpPost("send-test-email")]
    public async Task<IActionResult> SendTestEmail([FromBody] TestEmailRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { success = false, message = "Email is required." });
        }

        // If the email belongs to an existing user, use that user record for personalization.
        var user = await _userManager.FindByEmailAsync(request.Email.Trim());

        var tempUser = user ?? new ApplicationUser
        {
            Email = request.Email.Trim(),
            FullName = request.Name ?? request.Email.Trim(),
        };

        var subject = "Property Taxation SMTP Test";
        var htmlBody = $"""
            <div style="font-family:Arial,Helvetica,sans-serif;line-height:1.6;color:#0f172a;">
              <p>Hello {System.Net.WebUtility.HtmlEncode(tempUser.FullName)},</p>
              <p>This is a test email from Property Taxation to confirm SMTP delivery is working.</p>
              <p style="color:#475569;">If you received this message, the MailKit + Brevo SMTP configuration is working.</p>
            </div>
            """;
        var textBody = $"Hello {tempUser.FullName},\n\nThis is a test email from Property Taxation to confirm SMTP delivery is working.";

        try
        {
            await _emailService.SendEmailAsync(tempUser.Email!, subject, htmlBody, textBody);
            return Ok(new { success = true, message = "Test email sent (check inbox or logs)." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = "Failed to send test email.", details = ex.Message });
        }
    }
}

public class TestEmailRequest
{
    public string Email { get; set; } = string.Empty;
    public string? Name { get; set; }
}
