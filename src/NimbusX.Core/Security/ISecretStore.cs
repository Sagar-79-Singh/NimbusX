using System.Collections.Generic;

namespace NimbusX.Core.Security;

/// <summary>Namespaced store for secrets protected with <see cref="ISecretProtector"/>.</summary>
public interface ISecretStore
{
    /// <summary>Persists or replaces a secret. The value must never be logged or written anywhere else.</summary>
    void Save(string key, string value);

    /// <summary>Loads a secret, or null when it does not exist or can no longer be decrypted.</summary>
    string? TryLoad(string key);

    bool Contains(string key);

    void Delete(string key);

    IReadOnlyList<string> ListKeys();
}
