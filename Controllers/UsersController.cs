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
[Authorize(Roles = SystemRoles.Admin)]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AuthService _authService;
    private readonly AuditLogService _auditLogService;

    public UsersController(
        AppDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        AuthService authService,
        AuditLogService auditLogService)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _authService = authService;
        _auditLogService = auditLogService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<UserDto>>>> GetAll()
    {
        var users = await _userManager.Users
            .OrderBy(user => user.FullName)
            .ToListAsync();

        var result = new List<UserDto>(users.Count);

        foreach (var user in users)
        {
            result.Add(await MapUserAsync(user));
        }

        return Ok(ApiResponse<IReadOnlyCollection<UserDto>>.Ok(result, "Users retrieved successfully."));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<UserDto>>> GetById(string id)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(user => user.Id == id);

        if (user is null)
        {
            return NotFound(ApiResponse<UserDto>.Fail("User not found."));
        }

        return Ok(ApiResponse<UserDto>.Ok(await MapUserAsync(user), "User retrieved successfully."));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<UserDto>>> Create([FromBody] RegisterDto registerDto)
    {
        var result = await _authService.RegisterAsync(registerDto);

        if (!result.Success || result.User is null)
        {
            return BadRequest(ApiResponse<UserDto>.Fail(result.Message, result.Errors));
        }

        return CreatedAtAction(nameof(GetById), new { id = result.User.Id }, ApiResponse<UserDto>.Ok(result.User, result.Message));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<UserDto>>> Update(string id, [FromBody] UpdateUserDto updateUserDto)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(user => user.Id == id);

        if (user is null)
        {
            return NotFound(ApiResponse<UserDto>.Fail("User not found."));
        }

        var normalizedRole = NormalizeRole(updateUserDto.Role);

        if (normalizedRole is null)
        {
            return BadRequest(ApiResponse<UserDto>.Fail("Invalid role supplied.", ["Role must be Admin, Staff, Accountant, or Auditor."]));
        }

        var username = updateUserDto.Username.Trim();
        var email = updateUserDto.Email.Trim();
        var fullName = updateUserDto.FullName.Trim();

        // Ensure email/backup uniqueness
        var existingEmailOwner = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email || u.BackupEmail == email);

        if (existingEmailOwner is not null && existingEmailOwner.Id != id)
        {
            return Conflict(ApiResponse<UserDto>.Fail("A different user already uses that email address."));
        }

        var existingUsernameOwner = await _userManager.FindByNameAsync(username);

        if (existingUsernameOwner is not null && existingUsernameOwner.Id != id)
        {
            return Conflict(ApiResponse<UserDto>.Fail("A different user already uses that username."));
        }

        user.UserName = username;
        user.NormalizedUserName = _userManager.NormalizeName(username);
        user.Email = email;
        user.NormalizedEmail = _userManager.NormalizeEmail(email);
        user.FullName = fullName;
        user.IsActive = updateUserDto.IsActive;

        // Optional backup email
        user.BackupEmail = string.IsNullOrWhiteSpace(updateUserDto.BackupEmail) ? null : updateUserDto.BackupEmail.Trim();

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return BadRequest(ApiResponse<UserDto>.Fail("Failed to update user.", updateResult.Errors.Select(e => e.Description)));
        }

        if (!string.IsNullOrWhiteSpace(updateUserDto.Password))
        {
            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var resetPasswordResult = await _userManager.ResetPasswordAsync(user, resetToken, updateUserDto.Password);

            if (!resetPasswordResult.Succeeded)
            {
                return BadRequest(ApiResponse<UserDto>.Fail("Failed to reset the user password.", resetPasswordResult.Errors.Select(error => error.Description)));
            }
        }

        var currentRoles = await _userManager.GetRolesAsync(user);

        if (!currentRoles.Contains(normalizedRole, StringComparer.OrdinalIgnoreCase))
        {
            if (currentRoles.Count > 0)
            {
                var removeRolesResult = await _userManager.RemoveFromRolesAsync(user, currentRoles);

                if (!removeRolesResult.Succeeded)
                {
                    return BadRequest(ApiResponse<UserDto>.Fail("Failed to update the user role.", removeRolesResult.Errors.Select(error => error.Description)));
                }
            }

            var addRoleResult = await _userManager.AddToRoleAsync(user, normalizedRole);

            if (!addRoleResult.Succeeded)
            {
                return BadRequest(ApiResponse<UserDto>.Fail("Failed to update the user role.", addRoleResult.Errors.Select(error => error.Description)));
            }
        }

        await _auditLogService.LogAsync("UserUpdated", "User", user.Id, $"Updated user {user.UserName} to role {normalizedRole}");
        return Ok(ApiResponse<UserDto>.Ok(await MapUserAsync(user), "User updated successfully."));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<object?>>> Delete(string id)
    {
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.Equals(currentUserId, id, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(ApiResponse<object?>.Fail("You cannot delete the currently signed-in administrator account."));
        }

        var user = await _userManager.FindByIdAsync(id);

        if (user is null)
        {
            return NotFound(ApiResponse<object?>.Fail("User not found."));
        }

        var deleteResult = await _userManager.DeleteAsync(user);

        if (!deleteResult.Succeeded)
        {
            return BadRequest(ApiResponse<object?>.Fail("Failed to delete the user.", deleteResult.Errors.Select(error => error.Description)));
        }

        await _auditLogService.LogAsync("UserDeleted", "User", user.Id, $"Deleted user {user.UserName}");
        return Ok(ApiResponse<object?>.Ok(null, "User deleted successfully."));
    }

    private async Task<UserDto> MapUserAsync(ApplicationUser user)
    {
        var roles = await _userManager.GetRolesAsync(user);

        return new UserDto
        {
            Id = user.Id,
            Username = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            BackupEmail = user.BackupEmail,
            FullName = user.FullName,
            Role = roles.FirstOrDefault() ?? string.Empty,
            IsActive = user.IsActive,
            CreatedAtUtc = user.CreatedAtUtc,
        };
    }

    private static string? NormalizeRole(string role)
    {
        return SystemRoles.All.FirstOrDefault(candidate =>
            string.Equals(candidate, role.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is MySqlException mysqlException
            && mysqlException.Number == 1062;
    }
}