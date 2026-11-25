using System.Security.Cryptography;

namespace REM.Services;

/// <summary>
/// Verifies auto-downloaded Windows PE binaries (minimum size, MZ magic header, and SHA-256 digest).
/// </summary>
public static class ToolIntegrityHelper
{
    public static (bool Valid, string Sha256Hex, string? Error) ValidateExecutableAndHash(
        string filePath,
        string? expectedSha256Hex = null,
        long minBytes = 65_536)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return (false, string.Empty, "Executable file does not exist.");
        }

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length < minBytes)
            {
                return (false, string.Empty, $"File too small ({info.Length} bytes).");
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[2];
            if (stream.Read(header) < 2 || header[0] != 0x4D || header[1] != 0x5A)
            {
                return (false, string.Empty, "Invalid Windows PE executable header (missing MZ signature).");
            }

            stream.Position = 0;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(expectedSha256Hex) &&
                !string.Equals(hex, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return (false, hex, $"SHA-256 mismatch (got {hex}, expected {expectedSha256Hex}).");
            }

            return (true, hex, null);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}
