using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NimbusX.Core.Io;

/// <summary>SHA-256 helpers used to verify bundled binaries.</summary>
public static class Hashing
{
    /// <summary>Lower-case hex SHA-256 of a file.</summary>
    public static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(Guard.NotNullOrWhiteSpace(path, nameof(path)));
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return ToHex(hash);
    }

    /// <summary>Lower-case hex SHA-256 of a file, or null when it cannot be read.</summary>
    public static string? TrySha256File(string path)
    {
        try
        {
            return File.Exists(path) ? Sha256File(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Lower-case hex SHA-256 of a string (UTF-8).</summary>
    public static string Sha256Text(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        byte[] hash = SHA256.HashData(bytes);
        return ToHex(hash);
    }

    /// <summary>Case-insensitive comparison of two hex digests.</summary>
    public static bool Matches(string? expected, string? actual) =>
        !string.IsNullOrWhiteSpace(expected) &&
        !string.IsNullOrWhiteSpace(actual) &&
        string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string ToHex(byte[] bytes)
    {
        StringBuilder builder = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
    }
}
