using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Core;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;

namespace NimbusX.Providers;

/// <summary>Persists connected accounts as JSON (no secrets).</summary>
public sealed class AccountStore
{
    private readonly string _path;
    private readonly IAppLogger _logger;

    public AccountStore(string accountsFilePath, IAppLogger? logger = null)
    {
        _path = Guard.NotNullOrWhiteSpace(accountsFilePath, nameof(accountsFilePath));
        _logger = logger ?? NullLogger.Instance;
    }

    public string FilePath => _path;

    public IReadOnlyList<CloudAccount> Load()
    {
        List<CloudAccount>? accounts = AtomicFile.ReadJson<List<CloudAccount>>(_path);
        if (accounts is null)
        {
            return Array.Empty<CloudAccount>();
        }

        List<CloudAccount> valid = new();
        foreach (CloudAccount account in accounts)
        {
            if (!account.IsValid)
            {
                _logger.Warn("Ignoring an account entry with unknown provider or remote name in accounts.json.", "Accounts");
                continue;
            }

            valid.Add(account);
        }

        return valid;
    }

    public void Save(IEnumerable<CloudAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        try
        {
            AtomicFile.WriteJson(_path, accounts.OrderBy(a => a.CreatedUtc).ToList());
        }
        catch (Exception exception)
        {
            _logger.Error("Could not save accounts.json.", "Accounts", exception);
            throw;
        }
    }
}
