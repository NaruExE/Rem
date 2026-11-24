using System.Security.Cryptography;
using System.Text;

namespace REM.Services;

public static class PasswordHasher
{
    private const int SaltSizeBytes = 16;

    public static (string SaltBase64, string HashBase64) HashPassword(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hashBytes = ComputeSaltedSha256(saltBytes, password);
        return (Convert.ToBase64String(saltBytes), Convert.ToBase64String(hashBytes));
    }

    public static bool VerifyPassword(string password, string saltBase64, string hashBase64)
    {
        if (string.IsNullOrEmpty(saltBase64) || string.IsNullOrEmpty(hashBase64))
        {
            return false;
        }

        byte[] saltBytes;
        byte[] expectedHash;
        try
        {
            saltBytes = Convert.FromBase64String(saltBase64);
            expectedHash = Convert.FromBase64String(hashBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = ComputeSaltedSha256(saltBytes, password);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static byte[] ComputeSaltedSha256(byte[] saltBytes, string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var combined = new byte[saltBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(saltBytes, 0, combined, 0, saltBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, combined, saltBytes.Length, passwordBytes.Length);
        return SHA256.HashData(combined);
    }
}

