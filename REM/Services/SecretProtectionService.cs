using System.Runtime.InteropServices;
using System.Text;

namespace REM.Services;

/// <summary>
/// Encrypts and decrypts local API tokens/secrets using Windows DPAPI (CurrentUser scope)
/// with zero extra NuGet packages and transparent migration of existing plaintext values.
/// </summary>
public static class SecretProtectionService
{
    private const string Prefix = "dpapi:";
    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        if (IsProtected(plaintext) || !OperatingSystem.IsWindows())
        {
            return plaintext;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var inPtr = Marshal.AllocHGlobal(plainBytes.Length);
        DataBlob outBlob = default;
        try
        {
            Marshal.Copy(plainBytes, 0, inPtr, plainBytes.Length);
            var inBlob = new DataBlob { cbData = plainBytes.Length, pbData = inPtr };
            if (!CryptProtectData(
                ref inBlob,
                "REM Secret",
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out outBlob))
            {
                return plaintext;
            }

            var protectedBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, protectedBytes, 0, outBlob.cbData);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return plaintext;
        }
        finally
        {
            Array.Clear(plainBytes);
            Marshal.FreeHGlobal(inPtr);
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
        }
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        if (!IsProtected(stored) || !OperatingSystem.IsWindows())
        {
            return stored;
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch
        {
            return string.Empty;
        }

        var inPtr = Marshal.AllocHGlobal(protectedBytes.Length);
        DataBlob outBlob = default;
        try
        {
            Marshal.Copy(protectedBytes, 0, inPtr, protectedBytes.Length);
            var inBlob = new DataBlob { cbData = protectedBytes.Length, pbData = inPtr };
            if (!CryptUnprotectData(
                ref inBlob,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out outBlob))
            {
                return string.Empty;
            }

            var plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
            try
            {
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                Array.Clear(plainBytes);
            }
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
        }
    }
}
