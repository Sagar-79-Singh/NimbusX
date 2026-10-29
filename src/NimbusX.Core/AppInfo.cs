using System;
using System.Reflection;

namespace NimbusX.Core;

/// <summary>
/// Static product identity used by the app, the CLI, diagnostics and the installer.
/// </summary>
public static class AppInfo
{
    /// <summary>Product name. Used everywhere (UI, installer, docs, user agent).</summary>
    public const string Name = "NimbusX";

    /// <summary>CLI executable name.</summary>
    public const string CliName = "cpx";

    /// <summary>Telemetry named pipe prefix used by game-aware mode.</summary>
    public const string TelemetryPipePrefix = "nimbusx-telemetry-";

    /// <summary>Marker folder/file name placed inside every managed cache root.</summary>
    public const string CacheMarkerName = ".nimbusx-cache";

    /// <summary>Env var that carries the rclone config encryption key (never a command line).</summary>
    public const string ConfigPassEnvVar = "RCLONE_CONFIG_PASS";

    /// <summary>Window title used by the desktop shell: "NimbusX 0.7.0".</summary>
    public static string WindowTitle => Name + " " + Version;

    /// <summary>Version string from the assembly, or 0.0.0 when unknown.</summary>
    public static string Version
    {
        get
        {
            Assembly assembly = typeof(AppInfo).Assembly;
            AssemblyInformationalVersionAttribute? info =
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info is not null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                int plus = info.InformationalVersion.IndexOf('+');
                return plus > 0 ? info.InformationalVersion[..plus] : info.InformationalVersion;
            }

            Version? v = assembly.GetName().Version;
            return v is null ? "0.0.0" : v.ToString(3);
        }
    }

    /// <summary>Short user agent for outbound HTTP calls.</summary>
    public static string UserAgent => Name + "/" + Version + " (Windows; +https://github.com/cloudplayx/nimbusx)";
}
