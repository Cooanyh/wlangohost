using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PadP2P.App.Services;

internal static class DpapiProtector
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PadP2P.Settings.v1");

    public static byte[] Protect(byte[] plaintext)
    {
        var input = CreateBlob(plaintext);
        var entropy = CreateBlob(Entropy);
        try
        {
            if (!CryptProtectData(ref input, "PadP2P Wi-Fi Direct settings", ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not protect the Wi-Fi password.");
            try { return CopyBlob(output); }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    public static byte[] Unprotect(byte[] ciphertext)
    {
        var input = CreateBlob(ciphertext);
        var entropy = CreateBlob(Entropy);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not read the saved Wi-Fi password for this user.");
            try { return CopyBlob(output); }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    private static DataBlob CreateBlob(byte[] value)
    {
        var pointer = Marshal.AllocHGlobal(Math.Max(1, value.Length));
        if (value.Length > 0) Marshal.Copy(value, 0, pointer, value.Length);
        return new DataBlob { Length = value.Length, Data = pointer };
    }

    private static byte[] CopyBlob(DataBlob value)
    {
        var result = new byte[value.Length];
        if (value.Length > 0) Marshal.Copy(value.Data, result, 0, value.Length);
        return result;
    }

    private static void FreeBlob(DataBlob value)
    {
        if (value.Data != IntPtr.Zero)
        {
            if (value.Length > 0)
            {
                var zeros = new byte[value.Length];
                Marshal.Copy(zeros, 0, value.Data, zeros.Length);
            }
            Marshal.FreeHGlobal(value.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
