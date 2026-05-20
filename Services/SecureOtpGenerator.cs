using System.Security.Cryptography;

namespace PropertyTax.API.Services;

public static class SecureOtpGenerator
{
    public static string GenerateSixDigitOtp()
    {
        var value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D6");
    }
}