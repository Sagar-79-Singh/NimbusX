using System;
using System.Security.Cryptography;
using System.Text;

namespace NimbusX.Core.Security;

/// <summary>Cryptographically strong random values for rc credentials, config keys and account ids.</summary>
public static class RandomSecrets
{
    private const string UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>Random URL-safe password (default 40 characters, ~240 bits).</summary>
    public static string Password(int length = 40)
    {
        Guard.InRange(length, 16, 256, nameof(length));
        byte[] bytes = RandomNumberGenerator.GetBytes(length);
        StringBuilder builder = new(length);
        for (int i = 0; i < length; i++)
        {
            builder.Append(UrlAlphabet[bytes[i] % UrlAlphabet.Length]);
        }

        return builder.ToString();
    }

    /// <summary>Random lower-case hex string of <paramref name="byteCount"/> bytes.</summary>
    public static string Hex(int byteCount = 8)
    {
        Guard.InRange(byteCount, 4, 64, nameof(byteCount));
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
    }

    /// <summary>Random rclone remote identifier such as <c>nx_9f3ac1b2</c>.</summary>
    public static string RemoteName(string prefix = "nx_") => prefix + Hex(6);

    /// <summary>Random account identifier (GUID string, lower case).</summary>
    public static string AccountId() => Guid.NewGuid().ToString("d");
}
