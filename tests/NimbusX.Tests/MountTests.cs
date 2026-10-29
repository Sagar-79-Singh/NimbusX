using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NimbusX.Mount;
using NimbusX.Core.Security;
using Xunit;

namespace NimbusX.Tests;

public sealed class RcloneFlagCatalogTests
{
    private const string HelpText = @"
Usage:
  rclone mount remote:path /path/to/mountpoint [flags]

Flags:
      --allow-non-empty                   Allow mounting over a non-empty directory
      --attr-timeout Duration             Time for which file/directory attributes are cached (default 1s)
      --buffer-size SizeSuffix            In memory buffer size when reading files for each --transfer (default 16Mi)
      --daemon                            Run mount in background and exit parent process
      --dir-cache-time Duration           Time to cache directory entries for (default 5m0s)
      --vfs-cache-max-age Duration        Max time since last access of objects in the cache (default 1h0m0s)
      --vfs-cache-mode CacheMode          Cache mode off|minimal|writes|full (default off)
";

    [Fact]
    public void Parse_finds_every_flag_in_help_output()
    {
        RcloneFlagCatalog catalog = RcloneFlagCatalog.Parse(HelpText);

        Assert.True(catalog.Supports("--attr-timeout"));
        Assert.True(catalog.Supports("--vfs-cache-mode"));
        Assert.True(catalog.Supports("vfs-cache-max-age"));
        Assert.False(catalog.Supports("--no-such-flag"));
    }

    [Fact]
    public void Parse_returns_an_empty_catalog_for_missing_help()
    {
        RcloneFlagCatalog catalog = RcloneFlagCatalog.Parse(null);
        Assert.False(catalog.Supports("--buffer-size"));
        Assert.Empty(catalog.SupportedFlags);
    }

    [Fact]
    public void Validate_accepts_a_supported_flag()
    {
        FlagValidationResult result = RcloneFlagCatalog.Parse(HelpText).Validate(new[] { "--attr-timeout=2s" });
        Assert.True(result.IsValid);
        Assert.Single(result.Accepted);
    }

    [Fact]
    public void Validate_refuses_flags_the_installed_rclone_does_not_have()
    {
        FlagValidationResult result = RcloneFlagCatalog.Parse(HelpText).Validate(new[] { "--made-up-flag" });
        Assert.False(result.IsValid);
        Assert.Single(result.Rejected);
        Assert.Contains("made-up-flag", result.Explain(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--rc")]
    [InlineData("--rc-addr=127.0.0.1:5572")]
    [InlineData("--config=C:\\other.conf")]
    [InlineData("--cache-dir=C:\\cache")]
    [InlineData("--log-file=C:\\log.txt")]
    [InlineData("--password-command=cmd /c echo hi")]
    [InlineData("--daemon")]
    [InlineData("--vfs-cache-dir=D:\\x")]
    [InlineData("--use-json-log")]
    [InlineData("--log-format=json")]
    public void Validate_refuses_dangerous_flags_even_when_rclone_supports_them(string flag)
    {
        // Pretend the installed rclone supports everything, so only the security filter can reject the flag.
        RcloneFlagCatalog permissive = RcloneFlagCatalog.Parse("      --rc  Enable the remote control server\n      --config string  Config file\n      --daemon  Run in the background\n");
        FlagValidationResult result = permissive.Validate(new[] { flag });

        Assert.False(result.IsValid);
        Assert.Empty(result.Accepted);
    }

    [Fact]
    public void Validate_refuses_extra_arguments_that_are_not_flags()
    {
        FlagValidationResult result = RcloneFlagCatalog.Parse(HelpText).Validate(new[] { "gc", "about" });
        Assert.False(result.IsValid);
        Assert.Equal(2, result.Rejected.Count);
    }

    [Fact]
    public void Core_mount_flags_come_in_name_value_pairs()
    {
        IReadOnlyList<string> flags = RcloneFlagCatalog.CoreMountFlags();

        Assert.Equal(0, flags.Count % 2);
        for (int i = 0; i < flags.Count; i += 2)
        {
            Assert.StartsWith("--", flags[i], StringComparison.Ordinal);
            Assert.False(flags[i + 1].StartsWith("--", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Core_mount_flags_full_cache_and_disable_the_shared_rc_listener()
    {
        IReadOnlyList<string> flags = RcloneFlagCatalog.CoreMountFlags();
        int mode = flags.ToList().IndexOf("--vfs-cache-mode");

        Assert.True(mode >= 0);
        Assert.Equal("full", flags[mode + 1]);

        // The rc address is added per mount with a random port and password, never as a shared default.
        Assert.DoesNotContain("--rc-addr", flags);
        Assert.DoesNotContain("--rc", flags);
    }

    [Fact]
    public void Core_mount_flags_never_mention_a_cache_or_log_path()
    {
        IReadOnlyList<string> flags = RcloneFlagCatalog.CoreMountFlags();

        Assert.DoesNotContain("--cache-dir", flags);
        Assert.DoesNotContain("--log-file", flags);
        Assert.DoesNotContain("--config", flags);
    }
}

public sealed class PinnedDigestTests
{
    [Fact]
    public void Pinned_rclone_version_is_the_reviewed_one() =>
        Assert.Equal("1.75.2", DependencyVersions.RequiredRcloneVersion);

    [Fact]
    public void Every_pinned_artifact_has_a_full_lower_case_sha256()
    {
        foreach (DependencyAsset asset in new[] { DependencyVersions.Rclone, DependencyVersions.TeraBoxFork, DependencyVersions.WinFsp })
        {
            Assert.Equal(64, asset.Sha256.Length);
            Assert.All(asset.Sha256, character => Assert.True(Uri.IsHexDigit(character)));
            Assert.Equal(asset.Sha256.ToLowerInvariant(), asset.Sha256);
            Assert.StartsWith("https://github.com/", asset.DownloadUrl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_terabox_fork_is_pinned_separately_from_upstream_rclone()
    {
        Assert.NotEqual(DependencyVersions.Rclone.Sha256, DependencyVersions.TeraBoxFork.Sha256);
        Assert.NotEqual(DependencyVersions.Rclone.DownloadUrl, DependencyVersions.TeraBoxFork.DownloadUrl);
        Assert.Same(DependencyVersions.TeraBoxFork, DependencyVersions.ForFlavor(RcloneFlavor.TeraBox));
        Assert.Same(DependencyVersions.Rclone, DependencyVersions.ForFlavor(RcloneFlavor.Standard));
    }

    [Fact]
    public void The_winfsp_floor_is_below_the_pinned_installer()
    {
        Version floor = new(DependencyVersions.MinimumWinFspVersion);
        Version pinned = new(DependencyVersions.WinFsp.Version);
        Assert.True(pinned >= floor, "The pinned WinFsp installer must satisfy the minimum version NimbusX accepts.");
    }
}

public sealed class MountCommandLineTests
{
    [Fact]
    public void Mount_arguments_never_carry_a_secret()
    {
        // rclone reads the obscured config values from its own config file; nothing sensitive is ever an argument.
        IReadOnlyList<string> flags = RcloneFlagCatalog.CoreMountFlags();
        Assert.DoesNotContain(flags, flag => flag.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(flags, flag => flag.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rc_password_secrets_have_a_stable_per_account_name()
    {
        string first = SecretKeys.RcPassword("account-1");
        string second = SecretKeys.RcPassword("account-1");
        string other = SecretKeys.RcPassword("account-2");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.DoesNotContain("account-1", first.Split('-')[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Random_secrets_are_long_and_unique()
    {
        string first = RandomSecrets.Password();
        string second = RandomSecrets.Password();

        Assert.Equal(40, first.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Remote_names_are_safe_for_an_rclone_config_section()
    {
        for (int i = 0; i < 20; i++)
        {
            string name = RandomSecrets.RemoteName();
            Assert.StartsWith("nx_", name, StringComparison.Ordinal);
            Assert.All(name, character => Assert.True(char.IsLetterOrDigit(character) || character == '_' || character == '-'));
        }
    }
}
