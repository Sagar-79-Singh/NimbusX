namespace NimbusX.Core.Security;

/// <summary>Canonical secret key names so the same secret is never stored twice under different names.</summary>
public static class SecretKeys
{
    /// <summary>Key encrypting the whole rclone.conf file.</summary>
    public const string RcloneConfigPassword = "rclone-config-password";

    /// <summary>Random password for a mount's rclone rc endpoint: <c>rc-&lt;accountId&gt;</c>.</summary>
    public static string RcPassword(string accountId) => "rc-" + Sanitize(accountId);

    /// <summary>TeraBox session cookie: <c>terabox-cookie-&lt;accountId&gt;</c>.</summary>
    public static string TeraboxCookie(string accountId) => "terabox-cookie-" + Sanitize(accountId);

    /// <summary>MEGA password: <c>mega-pass-&lt;accountId&gt;</c>.</summary>
    public static string MegaPassword(string accountId) => "mega-pass-" + Sanitize(accountId);

    /// <summary>Generic provider password (WebDAV/SFTP/Koofr): <c>provider-pass-&lt;accountId&gt;</c>.</summary>
    public static string ProviderPassword(string accountId) => "provider-pass-" + Sanitize(accountId);

    /// <summary>Custom OAuth client secret: <c>oauth-client-secret-&lt;providerId&gt;</c>.</summary>
    public static string OAuthClientSecret(string providerId) => "oauth-client-secret-" + Sanitize(providerId);

    /// <summary>Custom OAuth client id: <c>oauth-client-id-&lt;providerId&gt;</c>.</summary>
    public static string OAuthClientId(string providerId) => "oauth-client-id-" + Sanitize(providerId);

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        char[] buffer = value.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < buffer.Length; i++)
        {
            char c = buffer[i];
            bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
            if (!valid)
            {
                buffer[i] = '-';
            }
        }

        return new string(buffer);
    }
}
