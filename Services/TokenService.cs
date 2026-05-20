using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public class TokenService
{
    private readonly IConfiguration _configuration;
    private readonly UserManager<ApplicationUser> _userManager;

    public TokenService(IConfiguration configuration, UserManager<ApplicationUser> userManager)
    {
        _configuration = configuration;
        _userManager = userManager;
    }

    public async Task<string> CreateTokenAsync(ApplicationUser user)
    {
        var roles = await _userManager.GetRolesAsync(user);
        var primaryRole = roles.FirstOrDefault() ?? SystemRoles.Staff;
        var issuer = _configuration["Jwt:Issuer"] ?? "PropertyTax.API";
        var audience = _configuration["Jwt:Audience"] ?? "PropertyTax.Client";
        var secretKey = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT signing key is not configured.");

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName ?? user.Email ?? user.Id),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new("preferred_username", user.UserName ?? user.Email ?? user.Id),
            new("userId", user.Id),
            new("username", user.UserName ?? user.Email ?? user.Id),
            new("role", primaryRole),
        };

        foreach (var role in roles.DefaultIfEmpty(primaryRole).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            SecurityAlgorithms.HmacSha256);

        var expiresInMinutes = int.TryParse(_configuration["Jwt:ExpiresInMinutes"], out var configuredMinutes)
            ? configuredMinutes
            : 120;

        if (expiresInMinutes <= 0)
        {
            throw new InvalidOperationException("JWT expiration must be greater than zero minutes.");
        }

        var issuedAtUtc = DateTime.UtcNow;

        var tokenDescriptor = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            notBefore: issuedAtUtc,
            expires: issuedAtUtc.AddMinutes(expiresInMinutes),
            signingCredentials: signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);
    }
}