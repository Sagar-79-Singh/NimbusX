using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Core;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;

namespace NimbusX.Mount;

/// <summary>Persists the mount records NimbusX needs to re-attach after a restart.</summary>
public sealed class MountStore
{
    private readonly string _path;
    private readonly IAppLogger _logger;

    public MountStore(string mountsFilePath, IAppLogger? logger = null)
    {
        _path = Guard.NotNullOrWhiteSpace(mountsFilePath, nameof(mountsFilePath));
        _logger = logger ?? NullLogger.Instance;
    }

    public string FilePath => _path;

    public IReadOnlyList<MountRecord> Load()
    {
        List<MountRecord>? records = AtomicFile.ReadJson<List<MountRecord>>(_path);
        return records is null
            ? Array.Empty<MountRecord>()
            : records.Where(r => !string.IsNullOrWhiteSpace(r.AccountId)).ToList();
    }

    public void Save(IEnumerable<MountRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        List<MountRecord> list = records.ToList();
        try
        {
            AtomicFile.WriteJson(_path, list);
        }
        catch (Exception exception)
        {
            _logger.Error("Could not save the mount state file.", "Mount", exception);
        }
    }
}
