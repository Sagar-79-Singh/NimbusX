using System;
using System.Collections.Generic;
using System.IO;

namespace NimbusX.Core.Config;

/// <summary>
/// Every file and folder NimbusX owns. Rooted in %LOCALAPPDATA%\NimbusX by default, or next to the executable
/// when a <c>portable.flag</c> file is present (portable ZIP build).
/// </summary>
public sealed class AppPaths
{
    public const string PortableFlagName = "portable.flag";

    public AppPaths(string root)
    {
        Root = Path.GetFullPath(Guard.NotNullOrWhiteSpace(root, nameof(root)));
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string AccountsFile => Path.Combine(Root, "accounts.json");

    public string MountsFile => Path.Combine(Root, "mounts.json");

    public string GamesFile => Path.Combine(Root, "games.json");

    public string PoolsFile => Path.Combine(Root, "pools.json");

    public string CachePinsFile => Path.Combine(Root, "cache-pins.json");

    public string SecretsDirectory => Path.Combine(Root, "secrets");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string RcloneLogsDirectory => Path.Combine(Root, "rclone-logs");

    public string ToolsDirectory => Path.Combine(Root, "tools");

    public string RcloneDirectory => Path.Combine(ToolsDirectory, "rclone");

    public string RcloneExecutable => Path.Combine(RcloneDirectory, "rclone.exe");

    public string TeraboxDirectory => Path.Combine(ToolsDirectory, "terabox");

    public string TeraboxExecutable => Path.Combine(TeraboxDirectory, "rclone.exe");

    public string RcloneConfigFile => Path.Combine(Root, "rclone.conf");

    public string LaunchDiagnosticsDirectory => Path.Combine(Root, "launches");

    public string ExportsDirectory => Path.Combine(Root, "exports");

    public string DatabaseFile => Path.Combine(Root, "nimbusx.db");

    public string LockFile => Path.Combine(Root, "nimbusx.lock");

    public string CacheMarkerFileIn(string cacheDirectory) => Path.Combine(cacheDirectory, AppInfo.CacheMarkerName);

    /// <summary>Builds the default paths, honouring portable mode.</summary>
    public static AppPaths CreateDefault()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string portableFlag = Path.Combine(baseDirectory, PortableFlagName);
        if (File.Exists(portableFlag))
        {
            return new AppPaths(Path.Combine(baseDirectory, "data"));
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = baseDirectory;
        }

        return new AppPaths(Path.Combine(localAppData, AppInfo.Name));
    }

    public IReadOnlyList<string> DirectoriesToCreate() => new[]
    {
        Root,
        SecretsDirectory,
        LogsDirectory,
        RcloneLogsDirectory,
        ToolsDirectory,
        RcloneDirectory,
        TeraboxDirectory,
        LaunchDiagnosticsDirectory,
        ExportsDirectory
    };

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        foreach (string directory in DirectoriesToCreate())
        {
            Directory.CreateDirectory(directory);
        }
    }

    public override string ToString() => Root;
}
