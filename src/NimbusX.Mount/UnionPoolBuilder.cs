using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core.Logging;

namespace NimbusX.Mount;

/// <summary>
/// Creates the optional "Pool" remote that merges several accounts into one big drive with rclone's union backend.
/// </summary>
/// <remarks>
/// Pool mode is off by default: each account keeps its own letter. When the user turns it on, NimbusX creates a
/// <c>union</c> remote whose upstreams are the selected accounts and mounts it on its own letter with its own
/// cache folder, so a pool never disturbs the individual drives.
/// </remarks>
public sealed class UnionPoolBuilder
{
    private readonly RcloneRcClient _rc;
    private readonly IAppLogger _logger;

    public UnionPoolBuilder(RcloneRcClient rc, IAppLogger? logger = null)
    {
        _rc = rc ?? throw new ArgumentNullException(nameof(rc));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Creates or updates the union remote.</summary>
    public async Task<string> CreateOrUpdateAsync(
        string poolRemoteName,
        IEnumerable<string> upstreams,
        CancellationToken cancellationToken)
    {
        List<string> list = upstreams.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count < 2)
        {
            throw new InvalidOperationException("A pool needs at least two cloud accounts.");
        }

        foreach (string upstream in list)
        {
            if (!IsValidUpstream(upstream))
            {
                throw new InvalidOperationException("'" + upstream + "' is not a valid rclone upstream (expected 'remote:path').");
            }
        }

        JsonObject parameters = BuildUnionParameters(list);
        await _rc.ConfigCreateAsync(
            poolRemoteName,
            "union",
            parameters,
            new JsonObject { ["nonInteractive"] = true },
            cancellationToken).ConfigureAwait(false);

        _logger.Info("Pool remote '" + poolRemoteName + "' now merges " + list.Count + " accounts.", "Mount");
        return poolRemoteName;
    }

    /// <summary>Removes the union remote.</summary>
    public async Task DeleteAsync(string poolRemoteName, CancellationToken cancellationToken)
    {
        try
        {
            await _rc.DeleteRemoteAsync(poolRemoteName, cancellationToken).ConfigureAwait(false);
            _logger.Info("Removed pool remote '" + poolRemoteName + "'.", "Mount");
        }
        catch (RcloneRcException exception)
        {
            _logger.Debug("Pool remote removal failed: " + exception.Message, "Mount");
        }
    }

    /// <summary>
    /// Union options used for a pool: new files go to the upstream with the most free space, reads search
    /// upstreams in order, deletes hit every upstream that has the file.
    /// </summary>
    public static JsonObject BuildUnionParameters(IEnumerable<string> upstreams)
    {
        string joined = string.Join(" ", upstreams);
        return new JsonObject
        {
            ["upstreams"] = joined,
            ["action_policy"] = "epall",
            ["create_policy"] = "epmfs",
            ["search_policy"] = "ff",
            ["cache_time"] = "30"
        };
    }

    /// <summary>True when the string looks like <c>remote:path</c> or <c>remote:</c>.</summary>
    public static bool IsValidUpstream(string? upstream)
    {
        if (string.IsNullOrWhiteSpace(upstream))
        {
            return false;
        }

        int colon = upstream!.IndexOf(':');
        if (colon <= 0 || colon > 64)
        {
            return false;
        }

        for (int i = 0; i < colon; i++)
        {
            char c = upstream[i];
            bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}
