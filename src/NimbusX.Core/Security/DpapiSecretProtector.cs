using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NimbusX.Core.Security;

/// <summary>
/// Windows DPAPI (CurrentUser scope) implementation of <see cref="ISecretProtector"/>.
/// Uses crypt32 directly so NimbusX needs no extra runtime package and never touches the machine scope.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public bool IsSupported => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] plaintext, byte[]? entropy)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Transform(plaintext, entropy, protect: true);
    }

    public byte[] Unprotect(byte[] ciphertext, byte[]? entropy)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return Transform(ciphertext, entropy, protect: false);
    }

    private static byte[] Transform(byte[] data, byte[]? entropy, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        DataBlob input = DataBlob.FromBytes(data);
        DataBlob entropyBlob = DataBlob.FromBytes(entropy);
        DataBlob output = default;
        try
        {
            bool ok;
            if (protect)
            {
                ok = CryptProtectData(
                    ref input,
                    "NimbusX secret",
                    entropy is null || entropy.Length == 0 ? IntPtr.Zero : entropyBlob.Pointer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output);
            }
            else
            {
                ok = CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    entropy is null || entropy.Length == 0 ? IntPtr.Zero : entropyBlob.Pointer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output);
            }

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                throw new SecretProtectionException(
                    (protect ? "CryptProtectData" : "CryptUnprotectData") + " failed with Win32 error " + error + ".");
            }

            return output.ToArray();
        }
        finally
        {
            input.Free();
            entropyBlob.Free();
            output.Free();
        }
    }

    /// <summary>Entropy helper: binds a secret to a purpose and optional owner id.</summary>
    public static byte[] EntropyFor(string purpose, string? ownerId = null)
    {
        string text = "NimbusX|" + purpose + "|" + (ownerId ?? string.Empty);
        return Encoding.UTF8.GetBytes(text);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;

        public IntPtr Pointer => Data;

        public static DataBlob FromBytes(byte[]? bytes)
        {
            if (bytes is null || bytes.Length == 0)
            {
                return default;
            }

            DataBlob blob = new()
            {
                Size = bytes.Length,
                Data = Marshal.AllocHGlobal(bytes.Length)
            };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        public byte[] ToArray()
        {
            if (Data == IntPtr.Zero || Size <= 0)
            {
                return Array.Empty<byte>();
            }

            byte[] result = new byte[Size];
            Marshal.Copy(Data, result, 0, Size);
            return result;
        }

        public void Free()
        {
            if (Data != IntPtr.Zero)
            {
                LocalFree(Data);
                Data = IntPtr.Zero;
                Size = 0;
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptProtectData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptUnprotectData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
