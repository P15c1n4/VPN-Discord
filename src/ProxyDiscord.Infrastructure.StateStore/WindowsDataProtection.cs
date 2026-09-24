using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ProxyDiscord.Infrastructure.StateStore;

internal static class WindowsDataProtection
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x00000001;

    public static string Protect(string value)
    {
        var clearBytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var encryptedBytes = Transform(clearBytes, protect: true);
            return Convert.ToBase64String(encryptedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public static string Unprotect(string encryptedValue)
    {
        var encryptedBytes = Convert.FromBase64String(encryptedValue);
        var clearBytes = Transform(encryptedBytes, protect: false);
        try
        {
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            CryptographicOperations.ZeroMemory(encryptedBytes);
        }
    }

    private static byte[] Transform(byte[] inputBytes, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(inputBytes.Length);
        var inputBlob = new DataBlob(inputBytes.Length, inputPointer);
        DataBlob outputBlob = default;
        IntPtr description = IntPtr.Zero;

        try
        {
            Marshal.Copy(inputBytes, 0, inputPointer, inputBytes.Length);
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out outputBlob)
                : CryptUnprotectData(ref inputBlob, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out outputBlob);

            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var outputBytes = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, outputBytes, 0, outputBlob.Size);
            return outputBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            Marshal.Copy(new byte[inputBytes.Length], 0, inputPointer, inputBytes.Length);
            Marshal.FreeHGlobal(inputPointer);
            if (outputBlob.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[outputBlob.Size], 0, outputBlob.Data, outputBlob.Size);
                LocalFree(outputBlob.Data);
            }

            if (description != IntPtr.Zero)
            {
                LocalFree(description);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob(int size, IntPtr data)
    {
        public int Size = size;
        public IntPtr Data = data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
