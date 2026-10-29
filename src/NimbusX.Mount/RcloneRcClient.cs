using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Logging;

namespace NimbusX.Mount;

/// <summary>
/// Typed wrapper around rclone's HTTP remote control API.
/// </summary>
/// <remarks>
/// Every mount gets its own rc endpoint bound to 127.0.0.1 with a random user name and password that are passed
/// to rclone through environment variables (never a command line) and are only ever sent here in an HTTP Basic
/// header. Responses are parsed as JSON; errors are surfaced with rclone's own message so the UI can show the
/// real reason instead of a generic failure.
/// </remarks>
public sealed class RcloneRcClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue _auth;
    private readonly IAppLogger _logger;
    private readonly int _port;
    private readonly bool _ownsHttpClient;

    public RcloneRcClient(int port, string userName, string password, IAppLogger? logger = null, HttpMessageHandler? handler = null)
    {
        Guard.Port(port, nameof(port));
        UserName = Guard.NotNullOrWhiteSpace(userName, nameof(userName));
        Password = Guard.NotNullOrWhiteSpace(password, nameof(password));
        _port = port;
        _logger = logger ?? NullLogger.Instance;
        _auth = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(UserName + ":" + Password)));
        _ownsHttpClient = true;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public int Port => _port;

    public string UserName { get; }

    internal string Password { get; }

    public Uri BaseAddress => new("http://127.0.0.1:" + _port + "/");

    /// <summary>True when rclone answers <c>core/version</c>.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            JsonObject result = await CallAsync("core/version", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return RcJson.String(result, "version") is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>rclone version as reported over rc.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            JsonObject result = await CallAsync("core/version", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return RcJson.String(result, "version");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Calls an rc method and returns its JSON object result.</summary>
    public async Task<JsonObject> CallAsync(
        string method,
        JsonObject? parameters,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(method, nameof(method));
        Uri uri = new(BaseAddress, method);
        JsonObject payload = parameters is null ? new JsonObject() : (JsonObject)parameters.DeepClone();

        using HttpRequestMessage request = new(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = _auth;

        using CancellationTokenSource timeoutCts = new(timeout ?? DefaultTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        string body;
        int status;
        try
        {
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new RcloneRcException(method, 0, "rclone did not answer '" + method + "' within " +
                                                   (int)(timeout ?? DefaultTimeout).TotalSeconds + " s.");
        }
        catch (HttpRequestException exception)
        {
            throw new RcloneRcException(method, 0, "rclone is not reachable on 127.0.0.1:" + _port + " (" + exception.Message + ").", exception);
        }

        JsonObject? node = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                node = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException exception)
            {
                throw new RcloneRcException(method, status, "rclone returned invalid JSON for '" + method + "'.", exception);
            }
        }

        if (status < 200 || status > 299)
        {
            string message = node is null ? body : (RcJson.String(node, "error") ?? body);
            throw new RcloneRcException(method, status, SecretRedactor.Redact(message).Trim());
        }

        return node ?? new JsonObject();
    }

    /// <summary>
    /// Calls a long running rc method asynchronously and polls <c>job/status</c> until it finishes.
    /// This is how OAuth connects and 20-minute copies avoid HTTP timeouts.
    /// </summary>
    public Task<JsonObject> CallAsJobAsync(
        string method,
        JsonObject parameters,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken) =>
        CallAsJobAsync(method, parameters, totalTimeout, null, cancellationToken);

    /// <summary>
    /// Like <see cref="CallAsJobAsync(string, JsonObject, TimeSpan, CancellationToken)"/> but calls
    /// <paramref name="onTick"/> while the job runs, which is how the OAuth flow discovers the authorization URL
    /// while rclone is blocked waiting for the browser.
    /// </summary>
    public async Task<JsonObject> CallAsJobAsync(
        string method,
        JsonObject parameters,
        TimeSpan totalTimeout,
        Func<CancellationToken, Task>? onTick,
        CancellationToken cancellationToken)
    {
        JsonObject start = (JsonObject)parameters.DeepClone();
        start["_async"] = true;
        JsonObject started = await CallAsync(method, start, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        long jobId = RcJson.Long(started, "jobid") ?? throw new RcloneRcException(method, 0, "rclone did not return a job id.");

        DateTimeOffset deadline = DateTimeOffset.UtcNow + totalTimeout;
        while (true)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new RcloneRcException(method, 0, "'" + method + "' did not finish within " + (int)totalTimeout.TotalMinutes + " minutes.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            if (onTick is not null)
            {
                try
                {
                    await onTick(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Debug("Job tick callback failed: " + exception.Message, "Rc");
                }
            }

            JsonObject status = await CallAsync(
                "job/status",
                new JsonObject { ["jobid"] = jobId },
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);

            if (!RcJson.Bool(status, "finished"))
            {
                continue;
            }

            if (!RcJson.Bool(status, "success"))
            {
                string error = RcJson.String(status, "error") ?? "unknown error";
                throw new RcloneRcException(method, 0, SecretRedactor.Redact(error));
            }

            return RcJson.Object(status, "output") ?? new JsonObject();
        }
    }

    /// <summary>Mounts a remote at a mount point (drive letter on Windows).</summary>
    public async Task<string> MountAsync(
        string fs,
        string mountPoint,
        JsonObject? vfsOptions,
        JsonObject? mountOptions,
        CancellationToken cancellationToken)
    {
        JsonObject parameters = new()
        {
            ["fs"] = fs,
            ["mountPoint"] = mountPoint,
            ["mountType"] = "mount"
        };

        if (vfsOptions is not null)
        {
            parameters["vfsOpt"] = vfsOptions;
        }

        if (mountOptions is not null)
        {
            parameters["mountOpt"] = mountOptions;
        }

        JsonObject result = await CallAsync("mount/mount", parameters, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return RcJson.String(result, "mountPoint") ?? mountPoint;
    }

    /// <summary>Active mount points.</summary>
    public async Task<IReadOnlyList<RcMountPoint>> ListMountsAsync(CancellationToken cancellationToken)
    {
        JsonObject result = await CallAsync("mount/listmounts", null, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        JsonArray? array = RcJson.Array(result, "mountPoints");
        List<RcMountPoint> mounts = new();
        if (array is null)
        {
            return mounts;
        }

        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject entry)
            {
                continue;
            }

            mounts.Add(new RcMountPoint(
                RcJson.String(entry, "MountPoint") ?? RcJson.String(entry, "mountPoint") ?? string.Empty,
                RcJson.String(entry, "Fs") ?? RcJson.String(entry, "fs"),
                RcJson.String(entry, "MountType") ?? RcJson.String(entry, "mountType")));
        }

        return mounts;
    }

    /// <summary>Unmounts one mount point.</summary>
    public async Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        await CallAsync(
            "mount/unmount",
            new JsonObject { ["mountPoint"] = mountPoint },
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Quota for a remote. Throws <see cref="RcloneRcException"/> when the backend cannot answer.</summary>
    public async Task<RemoteUsage> AboutAsync(string fs, CancellationToken cancellationToken)
    {
        JsonObject result = await CallAsync(
            "operations/about",
            new JsonObject { ["fs"] = fs },
            TimeSpan.FromSeconds(45),
            cancellationToken).ConfigureAwait(false);

        return new RemoteUsage(
            fs,
            RcJson.Long(result, "used") ?? RcJson.Long(result, "Used"),
            RcJson.Long(result, "total") ?? RcJson.Long(result, "Total"),
            RcJson.Long(result, "free") ?? RcJson.Long(result, "Free"),
            DateTimeOffset.UtcNow);
    }

    /// <summary>Asks rclone to exit cleanly.</summary>
    public async Task QuitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync("core/quit", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneRcException exception) when (exception.IsUnreachable)
        {
            // The process is already gone; quitting is idempotent.
        }
    }

    /// <summary>VFS cache statistics for a mounted remote.</summary>
    public async Task<JsonObject?> VfsStatsAsync(string? fs, CancellationToken cancellationToken)
    {
        JsonObject parameters = new();
        if (!string.IsNullOrWhiteSpace(fs))
        {
            parameters["fs"] = fs;
        }

        return await CallAsync("vfs/stats", parameters, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Transfer statistics (bytes in/out, errors, retries).</summary>
    public async Task<JsonObject?> CoreStatsAsync(CancellationToken cancellationToken) =>
        await CallAsync("core/stats", new JsonObject(), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

    /// <summary>Whether rclone's OAuth callback server is running, and the URL to open.</summary>
    public async Task<RcOAuthStatus> OAuthStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            JsonObject result = await CallAsync("config/oauthstatus", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            string? status = RcJson.String(result, "status");
            return new RcOAuthStatus(
                string.Equals(status, "running", StringComparison.OrdinalIgnoreCase),
                RcJson.String(result, "authUrl"));
        }
        catch (RcloneRcException)
        {
            return new RcOAuthStatus(false, null);
        }
    }

    /// <summary>Stops a pending OAuth flow.</summary>
    public async Task StopOAuthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync("config/oauthstop", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneRcException exception) when (exception.IsUnreachable || exception.StatusCode >= 400)
        {
            // nothing running
        }
    }

    /// <summary>Creates a remote in the (encrypted) rclone config.</summary>
    public Task<JsonObject> ConfigCreateAsync(
        string name,
        string type,
        JsonObject parameters,
        JsonObject? options,
        CancellationToken cancellationToken)
    {
        JsonObject body = new()
        {
            ["name"] = name,
            ["type"] = type,
            ["parameters"] = parameters
        };

        if (options is not null)
        {
            body["opt"] = options;
        }

        return CallAsync("config/create", body, TimeSpan.FromMinutes(2), cancellationToken);
    }

    /// <summary>Reads a remote from the config. Throws when the remote does not exist.</summary>
    public async Task<JsonObject?> ConfigGetAsync(string name, CancellationToken cancellationToken) =>
        await CallAsync("config/get", new JsonObject { ["name"] = name }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

    /// <summary>Lists remote names defined in the config file and environment.</summary>
    public async Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken cancellationToken)
    {
        JsonObject result = await CallAsync("config/listremotes", null, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        JsonArray? array = RcJson.Array(result, "remotes");
        List<string> remotes = new();
        if (array is null)
        {
            return remotes;
        }

        foreach (JsonNode? item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name))
            {
                remotes.Add(name!);
            }
        }

        return remotes;
    }

    /// <summary>Deletes a remote from the config.</summary>
    public async Task DeleteRemoteAsync(string name, CancellationToken cancellationToken) =>
        await CallAsync("config/delete", new JsonObject { ["name"] = name }, TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    internal IAppLogger Logger => _logger;
}

/// <summary>Small helpers for defensive JSON access.</summary>
public static class RcJson
{
    public static string? String(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out JsonNode? value) && value is JsonValue jsonValue &&
        jsonValue.TryGetValue(out string? text)
            ? text
            : null;

    public static long? Long(JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out JsonNode? value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out long number))
            {
                return number;
            }

            if (jsonValue.TryGetValue(out double doubleValue))
            {
                return (long)doubleValue;
            }

            if (jsonValue.TryGetValue(out string? text) && long.TryParse(text, out long parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static double? Double(JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out JsonNode? value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out double number))
            {
                return number;
            }

            if (jsonValue.TryGetValue(out string? text) && double.TryParse(text, out double parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static bool Bool(JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out JsonNode? value) || value is null)
        {
            return false;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out bool flag))
            {
                return flag;
            }

            if (jsonValue.TryGetValue(out string? text) && bool.TryParse(text, out bool parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    public static JsonObject? Object(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out JsonNode? value) ? value as JsonObject : null;

    public static JsonArray? Array(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out JsonNode? value) ? value as JsonArray : null;
}
