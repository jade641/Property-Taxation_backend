using System.Net;
using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public class AuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly TokenService _tokenService;
    private readonly AuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly IPasswordHasher<ApplicationUser> _passwordHasher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        TokenService tokenService,
        AuditLogService auditLogService,
        IEmailService emailService,
        IPasswordHasher<ApplicationUser> passwordHasher,
        IConfiguration configuration,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _tokenService = tokenService;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<(bool Success, string Message, AuthResponseDto? Response, IEnumerable<string>? Errors)> LoginAsync(LoginDto loginDto)
    {
        var identifier = loginDto.Username.Trim();
        var user = await FindByUsernameOrEmailAsync(identifier);

        if (user is null)
        {
            await _auditLogService.LogAsync("LoginFailed", "Authentication", null, $"Failed login attempt for {identifier}", false, username: identifier);
            return (false, "Invalid username or password.", null, null);
        }

        if (!user.IsActive)
        {
            await _auditLogService.LogAsync("LoginBlocked", "Authentication", user.Id, $"Inactive account attempted sign-in for {identifier}", false, user.Id, user.UserName, null);
            return (false, "This user account is inactive.", null, null);
        }

        var passwordIsValid = await _userManager.CheckPasswordAsync(user, loginDto.Password);

        if (!passwordIsValid)
        {
            await _auditLogService.LogAsync("LoginFailed", "Authentication", user.Id, $"Failed login attempt for {identifier}", false, user.Id, user.UserName, null);
            return (false, "Invalid username or password.", null, null);
        }

        var roles = await _userManager.GetRolesAsync(user);
        var role = roles.FirstOrDefault() ?? SystemRoles.Staff;
        var token = await _tokenService.CreateTokenAsync(user);

        await _auditLogService.LogAsync("LoginSucceeded", "Authentication", user.Id, $"Successful login for {identifier}", true, user.Id, user.UserName, role);

        return (true, "Login successful.", new AuthResponseDto
        {
            Token = token,
            UserId = user.Id,
            Username = user.UserName ?? user.Email ?? user.Id,
            DisplayName = user.FullName,
            Email = user.Email ?? string.Empty,
            Role = role,
        }, null);
    }

    public async Task<(bool Success, string Message, UserDto? User, IEnumerable<string>? Errors)> RegisterAsync(RegisterDto registerDto)
    {
        var normalizedRole = NormalizeRole(registerDto.Role);

        if (normalizedRole is null)
        {
            return (false, "Invalid role supplied.", null, ["Role must be Admin, Staff, Accountant, or Auditor."]);
        }

        if (!await _roleManager.RoleExistsAsync(normalizedRole))
        {
            await _roleManager.CreateAsync(new IdentityRole(normalizedRole));
        }

        if (await FindByUsernameOrEmailAsync(registerDto.Username.Trim()) is not null)
        {
            return (false, "A user with the supplied username already exists.", null, ["Username is already in use."]);
        }

        if (await _userManager.FindByEmailAsync(registerDto.Email.Trim()) is not null)
        {
            return (false, "A user with the supplied email already exists.", null, ["Email address is already in use."]);
        }

        var user = new ApplicationUser
        {
            UserName = registerDto.Username.Trim(),
            Email = registerDto.Email.Trim(),
            FullName = registerDto.FullName.Trim(),
            EmailConfirmed = true,
            IsActive = registerDto.IsActive,
        };

        var createResult = await _userManager.CreateAsync(user, registerDto.Password);

        if (!createResult.Succeeded)
        {
            var errors = createResult.Errors.Select(error => error.Description).ToArray();
            return (false, "User registration failed.", null, errors);
        }

        var roleResult = await _userManager.AddToRoleAsync(user, normalizedRole);

        if (!roleResult.Succeeded)
        {
            var errors = roleResult.Errors.Select(error => error.Description).ToArray();
            await _userManager.DeleteAsync(user);
            return (false, "Role assignment failed.", null, errors);
        }

        await _auditLogService.LogAsync("UserCreated", "User", user.Id, $"Created user {user.UserName} with role {normalizedRole}");

        return (true, "User created successfully.", MapUser(user, normalizedRole), null);
    }

    public async Task<string> ForgotPasswordAsync(ForgotPasswordRequest forgotPasswordRequest)
    {
        var email = forgotPasswordRequest.Email.Trim();
        var genericMessage = "If an account exists for that email, an OTP has been sent.";

        _logger.LogInformation("FORGOT PASSWORD START");
        _logger.LogInformation("API HIT RECEIVED for forgot-password");
        _logger.LogInformation("FORGOT PASSWORD REQUEST RECEIVED for {Email}", email);

        ApplicationUser? user = null;

        try
        {
            user = await _userManager.FindByEmailAsync(email);
            _logger.LogInformation("USER LOOKUP RESULT: {Found}", user is not null);

            await _auditLogService.LogAsync(
                "ForgotPasswordRequested",
                "Authentication",
                user?.Id,
                $"Password reset requested for {email}",
                true,
                username: email);

            if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.Email))
            {
                _logger.LogInformation("FORGOT PASSWORD END - NO ELIGIBLE USER");
                return genericMessage;
            }

            var otp = SecureOtpGenerator.GenerateSixDigitOtp();
            var otpExpiry = DateTime.UtcNow.AddMinutes(10);

            user.PasswordResetOtp = otp;
            user.PasswordResetOtpExpiry = otpExpiry;
            user.PasswordResetAttempts = 0;
            await _userManager.UpdateAsync(user);

            _logger.LogInformation("OTP GENERATED: {Otp}", otp);
            _logger.LogInformation("OTP SAVED TO DATABASE for {Email} with expiry {ExpiryUtc}", user.Email, otpExpiry);

            var safeName = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName);
            var textBody = $"Hello {user.FullName ?? user.Email},\n\nYour OTP code is: {otp}\n\nValid for 10 minutes only.\n\nIf you did not request this, you can safely ignore this email.";
            var htmlBody = BuildOtpEmailHtml(safeName, otp);

            _logger.LogInformation("EMAIL SENDING TRIGGERED");

            try
            {
                await _emailService.SendEmailAsync(user.Email, "Your OTP Code for Password Reset", htmlBody, textBody, otp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FULL ERROR STACKTRACE while sending OTP email for {Email}", user.Email);
            }

            await _auditLogService.LogAsync(
                "PasswordResetOtpSent",
                "Authentication",
                user.Id,
                $"Password reset OTP processed for {user.Email}",
                true,
                user.Id,
                user.UserName,
                null);

            _logger.LogInformation("FORGOT PASSWORD END");
            return genericMessage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FULL ERROR STACKTRACE while processing forgot password for {Email}", email);

            await _auditLogService.LogAsync(
                "ForgotPasswordFailed",
                "Authentication",
                user?.Id,
                $"Forgot password processing failed for {email}",
                false,
                username: email);

            return genericMessage;
        }
    }

    public async Task<(bool Success, string Message, string? ResetSessionToken)> VerifyOtpAsync(VerifyOtpRequest verifyOtpRequest)
    {
        try
        {
            _logger.LogInformation("RESET OTP START");
            _logger.LogInformation("API HIT RECEIVED for verify-otp");
            var email = verifyOtpRequest.Email.Trim();
            var otp = verifyOtpRequest.Otp.Trim();
            
            var user = await _userManager.FindByEmailAsync(email);
            _logger.LogInformation("USER LOOKUP RESULT: {Found}", user is not null);

            if (user is null || !user.IsActive)
            {
                return (false, "Invalid email address.", null);
            }

            if (user.PasswordResetAttempts >= 3)
            {
                _logger.LogWarning("OTP LOCKOUT ACTIVE for {Email}", email);
                return (false, "Too many invalid OTP attempts. Please request a new code.", null);
            }

            if (string.IsNullOrWhiteSpace(user.PasswordResetOtp) || user.PasswordResetOtp != otp)
            {
                user.PasswordResetAttempts += 1;

                if (user.PasswordResetAttempts >= 3)
                {
                    user.PasswordResetOtp = null;
                    user.PasswordResetOtpExpiry = null;
                }

                await _userManager.UpdateAsync(user);

                await _auditLogService.LogAsync(
                    "PasswordResetOtpVerificationFailed",
                    "Authentication",
                    user.Id,
                    $"Invalid OTP provided for {email}",
                    false,
                    user.Id,
                    user.UserName,
                    null);
                
                _logger.LogInformation("OTP VERIFICATION RESULT: INVALID");
                return (false, user.PasswordResetAttempts >= 3 ? "Too many invalid OTP attempts. Please request a new code." : "Invalid reset code. Please try again.", null);
            }

            if (user.PasswordResetOtpExpiry is null || DateTime.UtcNow > user.PasswordResetOtpExpiry)
            {
                user.PasswordResetOtp = null;
                user.PasswordResetOtpExpiry = null;
                user.PasswordResetAttempts = 0;
                await _userManager.UpdateAsync(user);

                await _auditLogService.LogAsync(
                    "PasswordResetOtpExpired",
                    "Authentication",
                    user.Id,
                    $"OTP expired for {email}",
                    false,
                    user.Id,
                    user.UserName,
                    null);

                _logger.LogInformation("OTP VERIFICATION RESULT: EXPIRED");
                return (false, "Reset code has expired. Please request a new one.", null);
            }

            user.PasswordResetAttempts = 0;
            await _userManager.UpdateAsync(user);

            var resetSessionToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

            await _auditLogService.LogAsync(
                "PasswordResetOtpVerified",
                "Authentication",
                user.Id,
                $"OTP verified for {email}",
                true,
                user.Id,
                user.UserName,
                null);

            _logger.LogInformation("OTP VERIFICATION RESULT: SUCCESS");
            return (true, "Code verified successfully.", resetSessionToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify OTP for {Email}.", verifyOtpRequest.Email);
            return (false, "An error occurred while verifying the code.", null);
        }
    }

    public async Task<(bool Success, string Message, IEnumerable<string>? Errors)> ResetPasswordAsync(ResetPasswordRequest resetPasswordRequest)
    {
        var email = resetPasswordRequest.Email.Trim();

        try
        {
            _logger.LogInformation("RESET PASSWORD START");
            _logger.LogInformation("API HIT RECEIVED for reset-password");
            var otp = resetPasswordRequest.Otp.Trim();
            var newPassword = resetPasswordRequest.NewPassword;

            var user = await _userManager.FindByEmailAsync(email);
            _logger.LogInformation("USER LOOKUP RESULT: {Found}", user is not null);

            if (user is null || !user.IsActive)
            {
                _logger.LogWarning("RESET PASSWORD FAILED - USER NOT FOUND OR INACTIVE for {Email}", email);
                await _auditLogService.LogAsync("PasswordResetFailed", "Authentication", null, $"Password reset failed for {email}", false, username: email);
                return (false, "Invalid password reset request.", new[] { "The reset code is invalid or expired." });
            }

            if (user.PasswordResetAttempts >= 3)
            {
                return (false, "Too many invalid OTP attempts. Please request a new code.", new[] { "The reset code has been locked." });
            }

            if (string.IsNullOrWhiteSpace(user.PasswordResetOtp) || user.PasswordResetOtp != otp)
            {
                user.PasswordResetAttempts += 1;

                if (user.PasswordResetAttempts >= 3)
                {
                    user.PasswordResetOtp = null;
                    user.PasswordResetOtpExpiry = null;
                }

                await _userManager.UpdateAsync(user);

                _logger.LogWarning("PASSWORD RESET FAILED - INVALID OTP for {Email}", email);
                await _auditLogService.LogAsync("PasswordResetFailed", "Authentication", user.Id, $"Password reset failed for {email}", false, user.Id, user.UserName, null);
                return (false, user.PasswordResetAttempts >= 3 ? "Too many invalid OTP attempts. Please request a new code." : "Invalid reset code.", new[] { "The reset code is invalid." });
            }

            if (user.PasswordResetOtpExpiry is null || DateTime.UtcNow > user.PasswordResetOtpExpiry)
            {
                user.PasswordResetOtp = null;
                user.PasswordResetOtpExpiry = null;
                user.PasswordResetAttempts = 0;
                await _userManager.UpdateAsync(user);

                await _auditLogService.LogAsync("PasswordResetFailed", "Authentication", user.Id, $"Password reset failed - expired OTP for {email}", false, user.Id, user.UserName, null);
                return (false, "Reset code has expired.", new[] { "The reset code has expired. Please request a new one." });
            }

            user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.PasswordResetOtp = null;
            user.PasswordResetOtpExpiry = null;
            user.PasswordResetAttempts = 0;
            await _userManager.UpdateAsync(user);

            await _auditLogService.LogAsync("PasswordResetSucceeded", "Authentication", user.Id, $"Password reset successful for {email}", true, user.Id, user.UserName, null);
            _logger.LogInformation("PASSWORD RESET SUCCESS");

            return (true, "Password reset successful.", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FULL ERROR STACKTRACE while resetting password for {Email}", email);
            await _auditLogService.LogAsync("PasswordResetFailed", "Authentication", null, $"Password reset processing failed for {email}", false, username: email);
            return (false, "An error occurred while resetting the password.", new[] { "An unexpected error occurred." });
        }
    }

        private static string BuildOtpEmailHtml(string safeName, string otp)
    {
        return $"""
            <div style="margin:0;padding:0;background:#f8fafc;font-family:Arial,Helvetica,sans-serif;color:#0f172a;">
              <div style="max-width:640px;margin:0 auto;padding:32px 20px;">
                <div style="background:#ffffff;border:1px solid #e2e8f0;border-radius:16px;overflow:hidden;box-shadow:0 12px 30px rgba(15,23,42,0.08);">
                  <div style="background:linear-gradient(135deg,#0f172a,#1d4ed8);padding:28px 32px;color:#ffffff;">
                    <div style="font-size:20px;font-weight:700;letter-spacing:0.02em;">Property Taxation</div>
                                        <div style="margin-top:8px;font-size:14px;opacity:0.92;">Your OTP Code for Password Reset</div>
                  </div>
                  <div style="padding:32px;line-height:1.65;">
                    <p style="margin:0 0 16px 0;font-size:16px;">Hello {safeName},</p>
                                        <p style="margin:0 0 18px 0;color:#334155;">We received a request to reset your password. Use the OTP code below in the app to continue.</p>
                                        <div style="text-align:center;margin:28px 0;">
                                            <div style="display:inline-block;background:#f8fafc;border:2px solid #dbeafe;padding:20px 28px;border-radius:14px;min-width:240px;">
                                                <div style="font-size:13px;font-weight:700;color:#1d4ed8;letter-spacing:0.08em;text-transform:uppercase;margin-bottom:10px;">Your OTP Code is</div>
                                                <div style="font-size:40px;font-weight:800;letter-spacing:0.35em;color:#0f172a;font-family:'Courier New',monospace;">{otp}</div>
                                            </div>
                    </div>
                                        <p style="margin:0 0 10px 0;color:#334155;text-align:center;font-weight:600;">Valid for 10 minutes only</p>
                                        <p style="margin:0;color:#64748b;font-size:13px;">Do not share this code with anyone. If you did not request this, you can safely ignore this email.</p>
                  </div>
                </div>
                <div style="padding:18px 6px 0;color:#94a3b8;font-size:12px;text-align:center;">
                  Property Taxation password reset notification
                </div>
              </div>
            </div>
            """;
    }

    public async Task<(bool Success, string Message, IEnumerable<string>? Errors)> ChangePasswordAsync(string userId, ChangePasswordDto changePasswordDto)
    {
        var user = await _userManager.FindByIdAsync(userId);

        if (user is null)
        {
            return (false, "Authenticated user not found.", null);
        }

        if (!user.IsActive)
        {
            return (false, "This user account is inactive.", null);
        }

        if (changePasswordDto.CurrentPassword == changePasswordDto.NewPassword)
        {
            return (false, "New password must be different from the current password.", null);
        }

        var changePasswordResult = await _userManager.ChangePasswordAsync(
            user,
            changePasswordDto.CurrentPassword,
            changePasswordDto.NewPassword);

        if (!changePasswordResult.Succeeded)
        {
            var errors = changePasswordResult.Errors.Select(error => error.Description).ToArray();
            await _auditLogService.LogAsync("PasswordChangeFailed", "Authentication", user.Id, $"Password change failed for {user.UserName}", false, user.Id, user.UserName, null);
            return (false, "Password change failed.", errors);
        }

        await _auditLogService.LogAsync("PasswordChanged", "Authentication", user.Id, $"Password changed for {user.UserName}", true, user.Id, user.UserName, null);
        return (true, "Password updated successfully.", null);
    }

    private async Task<ApplicationUser?> FindByUsernameOrEmailAsync(string identifier)
    {
        var user = await _userManager.FindByNameAsync(identifier);

        if (user is not null)
        {
            return user;
        }

        var byEmail = await _userManager.FindByEmailAsync(identifier);
        if (byEmail is not null)
        {
            return byEmail;
        }

        return await _userManager.Users.FirstOrDefaultAsync(u => u.BackupEmail == identifier);
    }

    private static string? NormalizeRole(string role)
    {
        return SystemRoles.All.FirstOrDefault(candidate =>
            string.Equals(candidate, role.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static UserDto MapUser(ApplicationUser user, string role)
    {
        return new UserDto
        {
            Id = user.Id,
            Username = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            BackupEmail = user.BackupEmail,
            FullName = user.FullName,
            Role = role,
            IsActive = user.IsActive,
            CreatedAtUtc = user.CreatedAtUtc,
        };
    }
}