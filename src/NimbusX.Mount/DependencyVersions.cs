using System;

namespace NimbusX.Mount;

/// <summary>Which rclone build a mount uses.</summary>
public enum RcloneFlavor
{
    /// <summary>Official rclone 1.75.2 (all providers except TeraBox).</summary>
    Standard = 0,

    /// <summary>Community build with the TeraBox backend, used only for TeraBox mounts.</summary>
    TeraBox = 1
}

/// <summary>A pinned external download.</summary>
public sealed class DependencyAsset
{
    public DependencyAsset(string name, string version, string downloadUrl, string sha256, string notes)
    {
        Name = name;
        Version = version;
        DownloadUrl = downloadUrl;
        Sha256 = sha256;
        Notes = notes;
    }

    public string Name { get; }

    public string Version { get; }

    public string DownloadUrl { get; }

    /// <summary>SHA-256 of the downloaded archive, lower case hex.</summary>
    public string Sha256 { get; }

    public string Notes { get; }

    public string FileName
    {
        get
        {
            int slash = DownloadUrl.LastIndexOf('/');
            return slash >= 0 ? DownloadUrl[(slash + 1)..] : DownloadUrl;
        }
    }

    public override string ToString() => Name + " " + Version;
}

/// <summary>
/// Every external binary NimbusX pins, with the SHA-256 that must match before it is allowed to run.
/// </summary>
/// <remarks>
/// The digests below are the SHA-256 values published by GitHub for each release asset
/// (see docs/VERIFIED_EXTERNALS.md). NimbusX refuses to launch a binary whose checksum does not match, and
/// records the hash of the extracted executable the first time it verifies one, so later tampering is detected.
/// </remarks>
public static class DependencyVersions
{
    /// <summary>Official rclone 1.75.2 for Windows x64.</summary>
    public static readonly DependencyAsset Rclone = new(
        "rclone",
        "1.75.2",
        "https://github.com/rclone/rclone/releases/download/v1.75.2/rclone-v1.75.2-windows-amd64.zip",
        "8a9db2bad1dc79a65b3348fd857069598e2aab7cf4d118b77b1df23055aff818",
        "Official build. Used for every provider except TeraBox.");

    /// <summary>
    /// Community rclone build that contains the TeraBox backend (gulp79/rclone-extra, the maintained fork of
    /// x1arch/rclone branch backend/terabox). Only ever used for TeraBox mounts.
    /// </summary>
    public static readonly DependencyAsset TeraBoxFork = new(
        "rclone-extra (TeraBox fork)",
        "1.75.2-extra",
        "https://github.com/gulp79/rclone-extra/releases/download/v1.75.2-extra/rclone-windows-amd64.zip",
        "d5a5d9383a607ccb1af9a871d4cb92906121f32cf111497adb94958b99315c69",
        "Unofficial build with the terabox backend. Auth is the 'ndus' session cookie.");

    /// <summary>WinFsp is installed by the user; NimbusX only detects it and offers the official installer.</summary>
    public static readonly DependencyAsset WinFsp = new(
        "WinFsp",
        "2.1.25156",
        "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi",
        "073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a",
        "Official WinFsp installer (MSI). Required by rclone mount on Windows.");

    /// <summary>Minimum WinFsp version NimbusX has tested against.</summary>
    public const string MinimumWinFspVersion = "1.12";

    /// <summary>rclone version NimbusX is built and tested against.</summary>
    public const string RequiredRcloneVersion = "1.75.2";

    /// <summary>The pinned asset for a flavor.</summary>
    public static DependencyAsset ForFlavor(RcloneFlavor flavor) =>
        flavor == RcloneFlavor.TeraBox ? TeraBoxFork : Rclone;

    public static string DisplayName(RcloneFlavor flavor) =>
        flavor == RcloneFlavor.TeraBox ? "rclone-extra (TeraBox)" : "rclone";
}
