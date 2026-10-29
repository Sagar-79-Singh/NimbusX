using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NimbusX.Core.Logging;

namespace NimbusX.Core.Security;

/// <summary>
/// Stores each secret in its own DPAPI protected file under the application data folder.
/// </summary>
/// <remarks>
/// The encryption key is the Windows user's DPAPI key (CurrentUser), optionally bound to the secret key name
/// through DPAPI entropy, so copying a secret file to another user or machine makes it useless.
/// </remarks>
public sealed class FileSecretStore : ISecretStore
{
    private const byte FormatVersion = 1;
    private const string Extension = ".nxs";
    private readonly ISecretProtector _protector;
    private readonly string _directory;
    private readonly IAppLogger _logger;

    public FileSecretStore(ISecretProtector protector, string directory, IAppLogger logger)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _directory = Guard.NotNullOrWhiteSpace(directory, nameof(directory));
        _logger = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(_directory);
    }

    public void Save(string key, string value)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        if (!_protector.IsSupported)
        {
            throw new SecretProtectionException("Secret protection (DPAPI) is not available on this machine.");
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(value);
        byte[] protectedBytes = _protector.Protect(plaintext, DpapiSecretProtector.EntropyFor("secret", key));
        byte[] payload = new byte[protectedBytes.Length + 1];
        payload[0] = FormatVersion;
        Buffer.BlockCopy(protectedBytes, 0, payload, 1, protectedBytes.Length);

        string path = PathFor(key);
        string temp = path + ".tmp";
        System.IO.Directory.CreateDirectory(_directory);
        File.WriteAllBytes(temp, payload);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }

        _logger.Debug("Stored secret '" + key + "'.", "Secrets");
    }

    public string? TryLoad(string key)
    {
        ValidateKey(key);
        string path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] payload = File.ReadAllBytes(path);
            if (payload.Length < 2 || payload[0] != FormatVersion)
            {
                _logger.Warn("Secret '" + key + "' has an unsupported format and was ignored.", "Secrets");
                return null;
            }

            byte[] protectedBytes = new byte[payload.Length - 1];
            Buffer.BlockCopy(payload, 1, protectedBytes, 0, protectedBytes.Length);
            byte[] plaintext = _protector.Unprotect(protectedBytes, DpapiSecretProtector.EntropyFor("secret", key));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception exception)
        {
            _logger.Warn("Secret '" + key + "' could not be decrypted: " + exception.Message, "Secrets");
            return null;
        }
    }

    public bool Contains(string key)
    {
        ValidateKey(key);
        return File.Exists(PathFor(key));
    }

    public void Delete(string key)
    {
        ValidateKey(key);
        string path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.Debug("Deleted secret '" + key + "'.", "Secrets");
        }
    }

    public IReadOnlyList<string> ListKeys()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return Array.Empty<string>();
        }

        return System.IO.Directory
            .EnumerateFiles(_directory, "*" + Extension)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string PathFor(string key) => Path.Combine(_directory, key + Extension);

    private static void ValidateKey(string key)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));
        if (key.Length > 120)
        {
            throw new ArgumentException("Secret key is too long.", nameof(key));
        }

        foreach (char c in key)
        {
            bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
            if (!valid)
            {
                throw new ArgumentException("Secret keys may only contain letters, digits, '-', '_' and '.'.", nameof(key));
            }
        }
    }
}
