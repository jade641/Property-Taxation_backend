using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Services;

namespace PropertyTax.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly AuditLogService _auditLogService;

    public AuthController(AuthService authService, AuditLogService auditLogService)
    {
        _authService = authService;
        _auditLogService = auditLogService;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<AuthResponseDto>>> Login([FromBody] LoginDto loginDto)
    {
        var result = await _authService.LoginAsync(loginDto);

        if (!result.Success || result.Response is null)
        {
            return Unauthorized(ApiResponse<AuthResponseDto>.Fail(result.Message, result.Errors));
        }

        return Ok(ApiResponse<AuthResponseDto>.Ok(result.Response, result.Message));
    }

    [Authorize(Roles = SystemRoles.Admin)]
    [HttpPost("register")]
    public async Task<ActionResult<ApiResponse<UserDto>>> Register([FromBody] RegisterDto registerDto)
    {
        var result = await _authService.RegisterAsync(registerDto);

        if (!result.Success || result.User is null)
        {
            return BadRequest(ApiResponse<UserDto>.Fail(result.Message, result.Errors));
        }

        return Ok(ApiResponse<UserDto>.Ok(result.User, result.Message));
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<ActionResult<ApiResponse<object?>>> ForgotPassword([FromBody] ForgotPasswordRequest forgotPasswordRequest)
    {
        var message = await _authService.ForgotPasswordAsync(forgotPasswordRequest);

        return Ok(ApiResponse<object?>.Ok(null, message));
    }

    [AllowAnonymous]
    [HttpPost("verify-otp")]
    public async Task<ActionResult<ApiResponse<object?>>> VerifyOtp([FromBody] VerifyOtpRequest verifyOtpRequest)
    {
        var result = await _authService.VerifyOtpAsync(verifyOtpRequest);

        if (!result.Success)
        {
            return BadRequest(ApiResponse<object?>.Fail(result.Message));
        }

        return Ok(ApiResponse<object?>.Ok(new { resetSessionToken = result.ResetSessionToken }, result.Message));
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<ActionResult<ApiResponse<object?>>> ResetPassword([FromBody] ResetPasswordRequest resetPasswordRequest)
    {
        var result = await _authService.ResetPasswordAsync(resetPasswordRequest);

        if (!result.Success)
        {
            return BadRequest(ApiResponse<object?>.Fail(result.Message, result.Errors));
        }

        return Ok(ApiResponse<object?>.Ok(null, result.Message));
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<ActionResult<ApiResponse<object?>>> ChangePassword([FromBody] ChangePasswordDto changePasswordDto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(ApiResponse<object?>.Fail("Authenticated user not found."));
        }

        var result = await _authService.ChangePasswordAsync(userId, changePasswordDto);

        if (!result.Success)
        {
            return BadRequest(ApiResponse<object?>.Fail(result.Message, result.Errors));
        }

        return Ok(ApiResponse<object?>.Ok(null, result.Message));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<ActionResult<ApiResponse<object?>>> Logout()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditLogService.LogAsync("Logout", "Authentication", userId, "User logged out.");
        return Ok(ApiResponse<object?>.Ok(null, "Logout recorded successfully."));
    }
}