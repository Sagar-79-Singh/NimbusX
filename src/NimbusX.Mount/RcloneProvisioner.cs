using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Io;
using NimbusX.Core.Json;
using NimbusX.Core.Logging;
using NimbusX.Core.Processes;

namespace NimbusX.Mount;

/// <summary>Outcome of making an rclone binary available.</summary>
public sealed class ProvisionResult
{
    public ProvisionResult(bool success, RcloneBinaryInfo info, string? message, string? manualInstructions)
    {
        Success = success;
        Info = info;
        Message = message;
        ManualInstructions = manualInstructions;
    }

    public bool Success { get; }

    public RcloneBinaryInfo Info { get; }

    /// <summary>Short user facing message (success or the real failure reason).</summary>
    public string? Message { get; }

    /// <summary>Steps the user can follow when the app cannot download the binary itself.</summary>
    public string? ManualInstructions { get; }

    public override string ToString() => (Success ? "ok: " : "failed: ") + (Message ?? string.Empty);
}

/// <summary>Records the hash of the executable NimbusX extracted and verified.</summary>
public sealed class BinaryLockEntry
{
    public string Sha256 { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public DateTimeOffset VerifiedUtc { get; set; }
}

/// <summary>
/// Installs and verifies the bundled rclone binaries.
/// </summary>
/// <remarks>
/// NimbusX never runs an rclone it did not verify: the archive SHA-256 must match the pinned digest in
/// <see cref="DependencyVersions"/>, and the extracted executable's SHA-256 is recorded in
/// <c>tools/binaries.json</c> so a later swap is detected. Downloads happen either through
/// <c>scripts/fetch-deps.ps1</c> (which places archives under <c>deps/</c>) or through
/// <see cref="EnsureAsync"/> with <c>allowDownload</c> set.
/// </remarks>
public sealed class RcloneProvisioner
{
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly IProcessRunner _runner;
    private readonly Func<HttpClient> _httpFactory;
    private readonly object _gate = new();
    private readonly Dictionary<RcloneFlavor, string> _versionCache = new();

    public RcloneProvisioner(AppPaths paths, IAppLogger logger, IProcessRunner runner, Func<HttpClient>? httpFactory = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger.Instance;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _httpFactory = httpFactory ?? CreateHttpClient;
    }

    public string DownloadsDirectory => Path.Combine(_paths.Root, "downloads");

    private string LockFilePath => Path.Combine(_paths.ToolsDirectory, "binaries.json");

    /// <summary>Path the binary must live at.</summary>
    public string ExecutablePathFor(RcloneFlavor flavor) =>
        flavor == RcloneFlavor.TeraBox ? _paths.TeraboxExecutable : _paths.RcloneExecutable;

    /// <summary>Inspects the installed binary without touching the network.</summary>
    public async Task<RcloneBinaryInfo> InspectAsync(RcloneFlavor flavor, CancellationToken cancellationToken)
    {
        string path = ExecutablePathFor(flavor);
        if (!File.Exists(path))
        {
            return new RcloneBinaryInfo(flavor, path, exists: false, checksumVerified: false, version: null, sha256: null, error: null);
        }

        string? sha = Hashing.TrySha256File(path);
        BinaryLockEntry? entry = LoadLock().TryGetValue(KeyFor(flavor), out BinaryLockEntry? locked) ? locked : null;
        bool verified = entry is not null && Hashing.Matches(entry.Sha256, sha);
        if (!verified)
        {
            return new RcloneBinaryInfo(
                flavor,
                path,
                exists: true,
                checksumVerified: false,
                version: null,
                sha256: sha,
                error: entry is null
                    ? "no recorded checksum; reinstall the binary with 'cpx deps repair'"
                    : "recorded checksum " + entry.Sha256[..Math.Min(12, entry.Sha256.Length)] + "... does not match");
        }

        string? version = await GetVersionAsync(flavor, cancellationToken).ConfigureAwait(false);
        return new RcloneBinaryInfo(flavor, path, exists: true, checksumVerified: true, version: version, sha256: sha, error: null);
    }

    /// <summary>
    /// Makes sure a usable binary exists, extracting a verified local archive and optionally downloading one.
    /// </summary>
    public async Task<ProvisionResult> EnsureAsync(
        RcloneFlavor flavor,
        bool allowDownload,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        RcloneBinaryInfo current = await InspectAsync(flavor, cancellationToken).ConfigureAwait(false);
        if (current.IsUsable)
        {
            return new ProvisionResult(true, current, current.Describe(), null);
        }

        DependencyAsset asset = DependencyVersions.ForFlavor(flavor);
        string? archive = FindLocalArchive(asset);

        if (archive is null && allowDownload)
        {
            try
            {
                archive = await DownloadAsync(asset, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error("Downloading " + asset.Name + " failed.", "Deps", exception);
                return new ProvisionResult(
                    false,
                    current,
                    "Could not download " + asset.Name + ": " + exception.Message,
                    ManualInstructions(asset));
            }
        }

        if (archive is null)
        {
            return new ProvisionResult(
                false,
                current,
                DependencyVersions.DisplayName(flavor) + " is missing. NimbusX needs it to mount cloud drives.",
                ManualInstructions(asset));
        }

        string archiveHash = Hashing.TrySha256File(archive) ?? string.Empty;
        if (!Hashing.Matches(asset.Sha256, archiveHash))
        {
            string message = "The " + asset.Name + " archive at " + archive +
                             " has the wrong SHA-256 (expected " + asset.Sha256 + ", got " + archiveHash + "). " +
                             "NimbusX refused to install it.";
            _logger.Error(message, "Deps");
            return new ProvisionResult(false, current, message, ManualInstructions(asset));
        }

        try
        {
            string executable = ExtractExecutable(archive, ExecutablePathFor(flavor));
            string exeHash = Hashing.Sha256File(executable);
            Dictionary<string, BinaryLockEntry> lockFile = LoadLock();
            lockFile[KeyFor(flavor)] = new BinaryLockEntry
            {
                Sha256 = exeHash,
                Version = asset.Version,
                Source = Path.GetFileName(archive) + " (sha256 " + archiveHash + ")",
                VerifiedUtc = DateTimeOffset.UtcNow
            };
            SaveLock(lockFile);
            lock (_gate)
            {
                _versionCache.Remove(flavor);
            }

            _logger.Info("Installed " + asset.Name + " " + asset.Version + " from " + Path.GetFileName(archive) + ".", "Deps");

            RcloneBinaryInfo installed = await InspectAsync(flavor, cancellationToken).ConfigureAwait(false);
            if (!installed.IsUsable)
            {
                return new ProvisionResult(
                    false,
                    installed,
                    "The extracted " + asset.Name + " could not be started: " + (installed.Error ?? "unknown error"),
                    ManualInstructions(asset));
            }

            return new ProvisionResult(true, installed, installed.Describe(), null);
        }
        catch (Exception exception)
        {
            _logger.Error("Extracting " + asset.Name + " failed.", "Deps", exception);
            return new ProvisionResult(false, current, "Could not extract " + asset.Name + ": " + exception.Message, ManualInstructions(asset));
        }
    }

    /// <summary>Runs <c>rclone version</c> and returns the version token (cached).</summary>
    public async Task<string?> GetVersionAsync(RcloneFlavor flavor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_versionCache.TryGetValue(flavor, out string? cached))
            {
                return cached;
            }
        }

        string path = ExecutablePathFor(flavor);
        if (!File.Exists(path))
        {
            return null;
        }

        ProcessResult result = await _runner
            .RunAsync(ProcessSpec.Create(path, "version"), TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return null;
        }

        string? version = ParseVersion(result.StandardOutput);
        if (version is not null)
        {
            lock (_gate)
            {
                _versionCache[flavor] = version;
            }
        }

        return version;
    }

    /// <summary>True when the binary reports the given backend (e.g. <c>terabox</c>).</summary>
    public async Task<bool> HasBackendAsync(RcloneFlavor flavor, string backendName, CancellationToken cancellationToken)
    {
        string path = ExecutablePathFor(flavor);
        if (!File.Exists(path))
        {
            return false;
        }

        ProcessResult result = await _runner
            .RunAsync(ProcessSpec.Create(path, "config", "providers"), TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return false;
        }

        return result.StandardOutput.Contains("\"" + backendName + "\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses the version token out of <c>rclone version</c> output ("rclone v1.75.2").</summary>
    public static string? ParseVersion(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
        {
            return null;
        }

        foreach (string rawLine in versionOutput!.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("rclone ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string token = line["rclone ".Length..].Trim();
            if (token.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                token = token[1..];
            }

            return token.Length == 0 ? null : token;
        }

        return null;
    }

    /// <summary>Directories searched for an already downloaded archive.</summary>
    public IReadOnlyList<string> ArchiveSearchDirectories()
    {
        List<string> directories = new()
        {
            DownloadsDirectory,
            Path.Combine(AppContext.BaseDirectory, "deps", "rclone"),
            Path.Combine(AppContext.BaseDirectory, "deps", "terabox"),
            Path.Combine(AppContext.BaseDirectory, "deps"),
            Path.Combine(_paths.Root, "deps")
        };

        return directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string? FindLocalArchive(DependencyAsset asset)
    {
        foreach (string directory in ArchiveSearchDirectories())
        {
            string candidate = Path.Combine(directory, asset.FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Downloads the asset into the downloads folder and returns the archive path.</summary>
    public async Task<string> DownloadAsync(DependencyAsset asset, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DownloadsDirectory);
        string target = Path.Combine(DownloadsDirectory, asset.FileName);
        string temp = target + ".part";

        using HttpClient client = _httpFactory();
        using HttpResponseMessage response = await client
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using (FileStream file = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (total is > 0)
                {
                    progress?.Report(Math.Clamp((double)copied / total.Value, 0d, 1d));
                }
            }
        }

        AtomicFile.Replace(temp, target);
        return target;
    }

    /// <summary>Extracts rclone.exe from a release ZIP.</summary>
    public static string ExtractExecutable(string archivePath, string destinationPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory!);
        }

        using FileStream file = File.OpenRead(archivePath);
        using ZipArchive archive = new(file, ZipArchiveMode.Read);

        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
            e.Name.Equals("rclone.exe", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.Replace('\\', '/').Split('/').Length == 2);

        entry ??= archive.Entries.FirstOrDefault(e => e.Name.Equals("rclone.exe", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new InvalidDataException("The archive " + Path.GetFileName(archivePath) + " does not contain rclone.exe.");
        }

        string temp = destinationPath + ".new";
        entry.ExtractToFile(temp, overwrite: true);
        AtomicFile.Replace(temp, destinationPath);
        return destinationPath;
    }

    /// <summary>Manual install steps shown when automatic provisioning is not possible.</summary>
    public static string ManualInstructions(DependencyAsset asset)
    {
        return "Download " + asset.FileName + " from " + asset.DownloadUrl + Environment.NewLine +
               "Expected SHA-256: " + asset.Sha256 + Environment.NewLine +
               "Either let NimbusX download it (Cloud Drives -> Repair dependencies) or place the ZIP in " +
               "scripts/deps and run: powershell -ExecutionPolicy Bypass -File scripts/fetch-deps.ps1";
    }

    private static string KeyFor(RcloneFlavor flavor) => flavor.ToString();

    private Dictionary<string, BinaryLockEntry> LoadLock()
    {
        return AtomicFile.ReadJson(LockFilePath, new Dictionary<string, BinaryLockEntry>(StringComparer.OrdinalIgnoreCase))
               ?? new Dictionary<string, BinaryLockEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveLock(Dictionary<string, BinaryLockEntry> entries)
    {
        Directory.CreateDirectory(_paths.ToolsDirectory);
        AtomicFile.WriteJson(LockFilePath, entries);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return client;
    }
}
