using Microsoft.AspNetCore.Identity;
using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public class BCryptPasswordHasher : IPasswordHasher<ApplicationUser>
{
    private readonly PasswordHasher<ApplicationUser> _fallbackHasher = new();

    public string HashPassword(ApplicationUser user, string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password);
    }

    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        if (string.IsNullOrWhiteSpace(hashedPassword))
        {
            return PasswordVerificationResult.Failed;
        }

        if (hashedPassword.StartsWith("$2", StringComparison.Ordinal))
        {
            return BCrypt.Net.BCrypt.Verify(providedPassword, hashedPassword)
                ? PasswordVerificationResult.Success
                : PasswordVerificationResult.Failed;
        }

        var fallbackResult = _fallbackHasher.VerifyHashedPassword(user, hashedPassword, providedPassword);

        return fallbackResult == PasswordVerificationResult.Success
            ? PasswordVerificationResult.SuccessRehashNeeded
            : fallbackResult;
    }
}
