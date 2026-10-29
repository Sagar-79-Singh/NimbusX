using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Logging;

namespace NimbusX.Streaming;

/// <summary>What the server is allowed to serve under one token.</summary>
public sealed class StreamTarget
{
    public StreamTarget(StreamSession session, MediaInfo media, string contentType)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Media = media ?? throw new ArgumentNullException(nameof(media));
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
    }

    public StreamSession Session { get; }

    public MediaInfo Media { get; }

    public string ContentType { get; }
}

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 that serves a mounted file to a media player with Range support.
/// </summary>
/// <remarks>
/// <para>
/// A raw <see cref="TcpListener"/> is used instead of <c>HttpListener</c> because HttpListener goes through
/// http.sys and needs a URL reservation (an admin step), while NimbusX must install and run as <c>asInvoker</c>.
/// </para>
/// <para>
/// The listener binds to the loopback address only, picks an ephemeral port, and requires an unguessable token in
/// the path. Requests for unknown tokens get a plain 404 that never reveals whether a file exists.
/// </para>
/// </remarks>
public sealed class StreamHttpServer : IDisposable
{
    public const int MaxRequestHeaderBytes = 16 * 1024;
    public const int MaxConcurrentConnections = 16;
    public const int CopyBufferSize = 128 * 1024;
    public const int ClientIdleTimeoutSeconds = 120;

    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, StreamTarget> _targets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private long _nextConnectionId;
    private bool _disposed;

    public StreamHttpServer(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    public string BaseUrl => Port > 0 ? "http://127.0.0.1:" + Port + "/" : string.Empty;

    public int ActiveConnections => _clients.Count;

    public int TargetCount => _targets.Count;

    /// <summary>Starts the listener on a random loopback port. Safe to call twice.</summary>
    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(64);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));

        _logger.Info("Media streaming proxy listening on " + BaseUrl + " (loopback only).", "Streaming");
    }

    /// <summary>Registers a token and returns the URL a player should open.</summary>
    public string Map(string token, StreamTarget target)
    {
        Guard.NotNullOrWhiteSpace(token, nameof(token));
        ArgumentNullException.ThrowIfNull(target);

        if (!IsRunning)
        {
            Start();
        }

        _targets[token] = target;
        return BaseUrl + "s/" + token;
    }

    /// <summary>Removes a token so its URL stops working immediately.</summary>
    public bool Unmap(string token) =>
        !string.IsNullOrWhiteSpace(token) && _targets.TryRemove(token, out _);

    public void Stop()
    {
        if (_listener is null)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already gone
        }

        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // shutting down anyway
        }

        foreach (KeyValuePair<int, TcpClient> client in _clients)
        {
            try
            {
                client.Value.Close();
            }
            catch (Exception)
            {
                // ignore
            }
        }

        _clients.Clear();
        _listener = null;
        _acceptLoop = null;
        _cts?.Dispose();
        _cts = null;
        _logger.Info("Media streaming proxy stopped.", "Streaming");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _slots.Dispose();
    }

    /// <summary>Parses a request line such as <c>GET /s/abc HTTP/1.1</c>.</summary>
    public static bool TryParseRequestLine(string line, out string method, out string path, out string? query)
    {
        method = string.Empty;
        path = string.Empty;
        query = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        method = parts[0].ToUpperInvariant();
        string target = parts[1];

        int question = target.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0)
        {
            query = target.Substring(question + 1);
            target = target.Substring(0, question);
        }

        path = Uri.UnescapeDataString(target);
        return method is "GET" or "HEAD";
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                _logger.Debug("Streaming accept failed: " + exception.Message, "Streaming");
                return;
            }

            int id = (int)Interlocked.Increment(ref _nextConnectionId);
            _clients[id] = client;
            _ = Task.Run(() => ServeClientAsync(id, client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeClientAsync(int id, TcpClient client, CancellationToken cancellationToken)
    {
        bool acquired = false;
        try
        {
            acquired = await _slots.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                await WriteSimpleAsync(client, 503, "Service Unavailable", "Too many concurrent streams.\n", cancellationToken).ConfigureAwait(false);
                return;
            }

            using (client)
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();

                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(ClientIdleTimeoutSeconds));

                string? headerBlock = await ReadHeaderBlockAsync(stream, timeout.Token).ConfigureAwait(false);
                if (headerBlock is null)
                {
                    return;
                }

                string[] lines = headerBlock.Split("\r\n", StringSplitOptions.None);
                if (!TryParseRequestLine(lines[0], out string method, out string path, out _))
                {
                    await WriteSimpleAsync(client, 400, "Bad Request", "Only GET and HEAD are supported.\n", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (path == "/health")
                {
                    await WriteSimpleAsync(client, 200, "OK", "NimbusX streaming proxy is running.\n", cancellationToken).ConfigureAwait(false);
                    return;
                }

                StreamTarget? target = ResolveTarget(path);
                if (target is null)
                {
                    await WriteSimpleAsync(client, 404, "Not Found", "Unknown stream session.\n", cancellationToken).ConfigureAwait(false);
                    return;
                }

                string? rangeHeader = HeaderValue(lines, "Range");
                await ServeFileAsync(stream, method, target, rangeHeader, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // client went away or the server is stopping
        }
        catch (IOException exception)
        {
            _logger.Debug("Streaming connection ended: " + exception.Message, "Streaming");
        }
        catch (Exception exception)
        {
            _logger.Warn("Streaming request failed: " + exception.Message, "Streaming");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            if (acquired)
            {
                _slots.Release();
            }
        }
    }

    private StreamTarget? ResolveTarget(string path)
    {
        // Accepted shapes: /s/<token> and /s/<token>/<file name>
        if (!path.StartsWith("/s/", StringComparison.Ordinal))
        {
            return null;
        }

        string rest = path.Substring(3);
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        string token = slash >= 0 ? rest.Substring(0, slash) : rest;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return _targets.TryGetValue(token, out StreamTarget? target) ? target : null;
    }

    private async Task ServeFileAsync(
        NetworkStream stream,
        string method,
        StreamTarget target,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        StreamSession session = target.Session;
        long size = target.Media.SizeBytes;

        if (size <= 0)
        {
            await WriteSimpleAsync(stream, 404, "Not Found", "The file is empty or not readable.\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        bool isRange = HttpRange.IsRangeRequest(rangeHeader);
        bool parsed = HttpRange.TryParse(rangeHeader, size, out long start, out long end, out string? rangeError);
        if (!parsed)
        {
            string body = (rangeError ?? "The requested range is not satisfiable.") + "\n";
            string headers = "HTTP/1.1 416 Range Not Satisfiable\r\n" +
                             "Content-Range: bytes */" + size.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                             "Content-Type: text/plain; charset=utf-8\r\n" +
                             "Content-Length: " + Encoding.UTF8.GetByteCount(body).ToString(CultureInfo.InvariantCulture) + "\r\n" +
                             "Connection: close\r\n\r\n";
            await WriteRawAsync(stream, headers, cancellationToken).ConfigureAwait(false);
            await WriteRawAsync(stream, body, cancellationToken).ConfigureAwait(false);
            return;
        }

        long length = end - start + 1;
        int status = isRange ? 206 : 200;
        string etag = "\"" + size.ToString(CultureInfo.InvariantCulture) + "-" + session.Id + "\"";

        StringBuilder response = new();
        response.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(isRange ? " Partial Content" : " OK").Append("\r\n");
        response.Append("Content-Type: ").Append(target.ContentType).Append("\r\n");
        response.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        response.Append("Accept-Ranges: bytes\r\n");
        response.Append("ETag: ").Append(etag).Append("\r\n");
        response.Append("Cache-Control: no-store\r\n");
        response.Append("X-Content-Type-Options: nosniff\r\n");
        response.Append("X-NimbusX-Session: ").Append(session.Id).Append("\r\n");
        if (isRange)
        {
            response.Append("Content-Range: bytes ")
                .Append(start.ToString(CultureInfo.InvariantCulture)).Append('-')
                .Append(end.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(size.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        response.Append("Connection: close\r\n\r\n");
        session.RecordRequest(isRange);

        await WriteRawAsync(stream, response.ToString(), cancellationToken).ConfigureAwait(false);

        if (method == "HEAD")
        {
            return;
        }

        byte[] buffer = new byte[CopyBufferSize];
        try
        {
            using FileStream file = new(
                session.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            file.Seek(start, SeekOrigin.Begin);
            long remaining = length;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = await file.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                session.AddBytes(read);
                remaining -= read;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The drive mirror can return a transient error (rclone restarted, network blip). The session stays
            // usable and the player will retry the same range; the log records the real error.
            _logger.Warn("Streaming read failed for session " + session.Id + ": " + exception.Message, "Streaming");
        }
    }

    private static async Task<string?> ReadHeaderBlockAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxRequestHeaderBytes];
        int total = 0;

        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            total += read;
            if (total >= 4 &&
                buffer[total - 4] == (byte)'\r' && buffer[total - 3] == (byte)'\n' &&
                buffer[total - 2] == (byte)'\r' && buffer[total - 1] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, total - 4);
            }
        }

        return null;
    }

    private static string? HeaderValue(string[] lines, string name)
    {
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            if (string.Equals(lines[i].Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return lines[i].Substring(colon + 1).Trim();
            }
        }

        return null;
    }

    private static Task WriteRawAsync(NetworkStream stream, string text, CancellationToken cancellationToken) =>
        WriteRawAsync(stream, Encoding.ASCII.GetBytes(text), cancellationToken);

    private static async Task WriteRawAsync(NetworkStream stream, byte[] bytes, CancellationToken cancellationToken) =>
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);

    private static async Task WriteSimpleAsync(TcpClient client, int status, string reason, string body, CancellationToken cancellationToken)
    {
        if (client is null || !client.Connected)
        {
            return;
        }

        byte[] payload = Encoding.UTF8.GetBytes(body);
        string headers = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n" +
                         "Content-Type: text/plain; charset=utf-8\r\n" +
                         "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                         "Connection: close\r\n\r\n";

        NetworkStream stream = client.GetStream();
        await WriteRawAsync(stream, headers, cancellationToken).ConfigureAwait(false);
        await WriteRawAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSimpleAsync(NetworkStream stream, int status, string reason, string body, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string headers = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n" +
                         "Content-Type: text/plain; charset=utf-8\r\n" +
                         "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                         "Connection: close\r\n\r\n";

        await WriteRawAsync(stream, headers, cancellationToken).ConfigureAwait(false);
        await WriteRawAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }
}
