using System;

namespace NimbusX.Core.Security;

/// <summary>Protects small secrets at rest (passwords, cookies, the rclone config key).</summary>
public interface ISecretProtector
{
    /// <summary>True when the current machine/user can protect and unprotect secrets.</summary>
    bool IsSupported { get; }

    byte[] Protect(byte[] plaintext, byte[]? entropy);

    byte[] Unprotect(byte[] ciphertext, byte[]? entropy);
}

/// <summary>Thrown when a secret cannot be protected or un-protected.</summary>
public sealed class SecretProtectionException : Exception
{
    public SecretProtectionException(string message)
        : base(message)
    {
    }

    public SecretProtectionException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
