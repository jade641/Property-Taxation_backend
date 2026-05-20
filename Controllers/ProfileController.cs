using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Services;

namespace PropertyTax.API.Controllers;

[ApiController]
[Authorize]
[Route("api/profile")]
public class ProfileController : ControllerBase
{
    private const string PositionClaimType = "profile:position";
    private const string MunicipalityClaimType = "profile:municipality";

    private readonly AppDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AuditLogService _auditLogService;

    public ProfileController(AppDbContext dbContext, UserManager<ApplicationUser> userManager, AuditLogService auditLogService)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _auditLogService = auditLogService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<ProfileDto>>> GetProfile()
    {
        var user = await GetCurrentUserAsync();

        if (user is null)
        {
            return Unauthorized(ApiResponse<ProfileDto>.Fail("Authenticated user not found."));
        }

        var claims = await _userManager.GetClaimsAsync(user);
        var profile = MapProfile(user, claims);

        return Ok(ApiResponse<ProfileDto>.Ok(profile, "Profile retrieved successfully."));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<ProfileDto>>> UpdateProfile([FromBody] UpdateProfileDto updateProfileDto)
    {
        var user = await GetCurrentUserAsync();

        if (user is null)
        {
            return Unauthorized(ApiResponse<ProfileDto>.Fail("Authenticated user not found."));
        }

        var fullName = updateProfileDto.FullName.Trim();
        var email = updateProfileDto.Email.Trim();
        var phoneNumber = NormalizeOptional(updateProfileDto.PhoneNumber);
        var position = NormalizeOptional(updateProfileDto.Position);
        var municipality = NormalizeOptional(updateProfileDto.Municipality);

        var oldEmail = user.Email ?? string.Empty;
        var shouldUpdateUsername = string.IsNullOrWhiteSpace(user.UserName)
            || string.Equals(user.UserName, oldEmail, StringComparison.OrdinalIgnoreCase);
        var normalizedEmail = _userManager.NormalizeEmail(email);
        var nextUsername = shouldUpdateUsername ? email : user.UserName ?? string.Empty;
        var normalizedUsername = shouldUpdateUsername
            ? _userManager.NormalizeName(nextUsername)
            : user.NormalizedUserName;

        // Ensure the new email and backup email (if provided) are not used by another account
        var existingEmailOwner = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email || u.BackupEmail == email);

        if (existingEmailOwner is not null && existingEmailOwner.Id != user.Id)
        {
            return Conflict(ApiResponse<ProfileDto>.Fail("A different user already uses that email address."));
        }

        if (shouldUpdateUsername)
        {
            var existingUsernameOwner = await _userManager.FindByNameAsync(nextUsername);

            if (existingUsernameOwner is not null && existingUsernameOwner.Id != user.Id)
            {
                return Conflict(ApiResponse<ProfileDto>.Fail("A different user already uses that username."));
            }
        }

        if (!string.Equals(user.FullName, fullName, StringComparison.Ordinal))
        {
            user.FullName = fullName;
        }

        var emailChanged = !string.Equals(oldEmail, email, StringComparison.OrdinalIgnoreCase);

        if (emailChanged)
        {
            user.Email = email;
            user.NormalizedEmail = normalizedEmail;
        }

        if (shouldUpdateUsername)
        {
            user.UserName = nextUsername;
            user.NormalizedUserName = normalizedUsername;
        }

        if (!string.Equals(user.PhoneNumber, phoneNumber, StringComparison.Ordinal))
        {
            user.PhoneNumber = phoneNumber;
        }

        // Backup email logic: optional; ensure uniqueness
        var backup = NormalizeOptional(updateProfileDto.BackupEmail);
        if (!string.IsNullOrWhiteSpace(backup))
        {
            var existingBackupOwner = await _dbContext.Users.FirstOrDefaultAsync(u => u.BackupEmail == backup || u.Email == backup);

            if (existingBackupOwner is not null && existingBackupOwner.Id != user.Id)
            {
                return Conflict(ApiResponse<ProfileDto>.Fail("A different user already uses that backup email address."));
            }

            user.BackupEmail = backup;
        }

        // Persist via UserManager to ensure identity invariants are respected
        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return Conflict(ApiResponse<ProfileDto>.Fail("Failed to update profile.", updateResult.Errors.Select(e => e.Description)));
        }

        var claims = await _userManager.GetClaimsAsync(user);

        var claimResult = await UpsertClaimAsync(user, claims, PositionClaimType, position);

        if (!claimResult.Succeeded)
        {
            return BadRequest(ApiResponse<ProfileDto>.Fail(
                "Failed to update position.",
                claimResult.Errors.Select(error => error.Description)));
        }

        claimResult = await UpsertClaimAsync(user, claims, MunicipalityClaimType, municipality);

        if (!claimResult.Succeeded)
        {
            return BadRequest(ApiResponse<ProfileDto>.Fail(
                "Failed to update municipality.",
                claimResult.Errors.Select(error => error.Description)));
        }

        await _auditLogService.LogAsync(
            "ProfileUpdated",
            "User",
            user.Id,
            $"Updated profile for {user.UserName}",
            true,
            user.Id,
            user.UserName,
            null);

        var updatedClaims = await _userManager.GetClaimsAsync(user);
        var profile = MapProfile(user, updatedClaims);

        return Ok(ApiResponse<ProfileDto>.Ok(profile, "Profile updated successfully."));
    }

    private async Task<ApplicationUser?> GetCurrentUserAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return await _dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId);
    }

    private static ProfileDto MapProfile(ApplicationUser user, IEnumerable<Claim> claims)
    {
        return new ProfileDto
        {
            Id = user.Id,
            Username = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            BackupEmail = user.BackupEmail,
            FullName = user.FullName,
            PhoneNumber = user.PhoneNumber,
            Position = GetClaimValue(claims, PositionClaimType),
            Municipality = GetClaimValue(claims, MunicipalityClaimType),
        };
    }

    private static string? GetClaimValue(IEnumerable<Claim> claims, string claimType)
    {
        return claims.FirstOrDefault(claim => string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is MySqlException mysqlException
            && mysqlException.Number == 1062;
    }

    private async Task<IdentityResult> UpsertClaimAsync(
        ApplicationUser user,
        IList<Claim> claims,
        string claimType,
        string? value)
    {
        var existing = claims.FirstOrDefault(claim => string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(value))
        {
            return existing is null
                ? IdentityResult.Success
                : await _userManager.RemoveClaimAsync(user, existing);
        }

        var updatedClaim = new Claim(claimType, value);

        if (existing is null)
        {
            return await _userManager.AddClaimAsync(user, updatedClaim);
        }

        if (string.Equals(existing.Value, value, StringComparison.Ordinal))
        {
            return IdentityResult.Success;
        }

        return await _userManager.ReplaceClaimAsync(user, existing, updatedClaim);
    }
}
