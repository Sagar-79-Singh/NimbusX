namespace NimbusX.Mount;

/// <summary>Result of inspecting one rclone executable on disk.</summary>
public sealed class RcloneBinaryInfo
{
    public RcloneBinaryInfo(
        RcloneFlavor flavor,
        string path,
        bool exists,
        bool checksumVerified,
        string? version,
        string? sha256,
        string? error)
    {
        Flavor = flavor;
        Path = path;
        Exists = exists;
        ChecksumVerified = checksumVerified;
        Version = version;
        Sha256 = sha256;
        Error = error;
    }

    public RcloneFlavor Flavor { get; }

    public string Path { get; }

    public bool Exists { get; }

    /// <summary>True when the binary matches the recorded checksum of a verified download.</summary>
    public bool ChecksumVerified { get; }

    /// <summary>Version reported by <c>rclone version</c>, or null when it could not be started.</summary>
    public string? Version { get; }

    public string? Sha256 { get; }

    public string? Error { get; }

    /// <summary>Ready to be used for mounts.</summary>
    public bool IsUsable => Exists && ChecksumVerified && !string.IsNullOrWhiteSpace(Version);

    public string Describe()
    {
        if (!Exists)
        {
            return DependencyVersions.DisplayName(Flavor) + " is not installed in the NimbusX tools folder.";
        }

        if (!ChecksumVerified)
        {
            return "The " + DependencyVersions.DisplayName(Flavor) + " binary at " + Path +
                   " does not match its recorded SHA-256 and will not be used (" + (Error ?? "checksum mismatch") + ").";
        }

        return DependencyVersions.DisplayName(Flavor) + " " + (Version ?? "?") + " at " + Path;
    }

    public override string ToString() => Describe();
}
