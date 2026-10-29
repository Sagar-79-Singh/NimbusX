using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core.Io;

namespace NimbusX.SimCloud;

/// <summary>
/// A deliberately small but real WebDAV server (classes 1 and 2 subset) on 127.0.0.1.
/// </summary>
/// <remarks>
/// <para>
/// It implements exactly the verbs rclone's WebDAV backend uses: OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE, MKCOL,
/// MOVE and COPY. That is enough for NimbusX to mount it, browse it, read with Range requests, cache files and
/// verify uploads, which makes the whole stack testable on one machine.
/// </para>
/// <para>
/// Path traversal is refused: every request path is resolved with <see cref="PathGuard"/>, so <c>..</c> cannot escape
/// the served folder. Basic auth is always required.
/// </para>
/// </remarks>
public sealed class WebDavServer : IDisposable
{
    private const int MaxRequestHeaderBytes = 32 * 1024;
    private const int CopyBufferSize = 128 * 1024;

    private readonly SimCloudOptions _options;
    private readonly string _root;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private bool _disposed;

    public WebDavServer(SimCloudOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Directory.CreateDirectory(_options.Root);
        _root = Path.GetFullPath(_options.Root);
        _listener = new TcpListener(IPAddress.Loopback, Math.Clamp(_options.Port, 0, 65535));
    }

    public int Port { get; private set; }

    public void Start()
    {
        _listener.Start(64);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // stopping anyway
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();

                Request? request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                if (!IsAuthorized(request))
                {
                    await WriteStatusAsync(stream, 401, "Unauthorized", "WWW-Authenticate: Basic realm=\"NimbusX SimCloud\"\r\n", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!TryResolve(request.Path, out string local, out bool isRoot))
                {
                    await WriteStatusAsync(stream, 403, "Forbidden", string.Empty, cancellationToken).ConfigureAwait(false);
                    return;
                }

                switch (request.Method)
                {
                    case "OPTIONS":
                        await WriteStatusAsync(stream, 200, "OK", "DAV: 1,2\r\nAllow: OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, MKCOL, MOVE, COPY\r\nMS-Author-Via: DAV\r\n", cancellationToken).ConfigureAwait(false);
                        return;

                    case "PROPFIND":
                        await HandlePropFindAsync(stream, request, local, cancellationToken).ConfigureAwait(false);
                        return;

                    case "GET" or "HEAD":
                        await HandleGetAsync(stream, request, local, cancellationToken).ConfigureAwait(false);
                        return;

                    case "PUT":
                        if (!_options.ReadOnly)
                        {
                            await HandlePutAsync(stream, request, local, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        break;

                    case "MKCOL":
                        if (!_options.ReadOnly)
                        {
                            await HandleMkColAsync(stream, local, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        break;

                    case "DELETE":
                        if (!_options.ReadOnly)
                        {
                            await HandleDeleteAsync(stream, local, isRoot, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        break;

                    case "MOVE" or "COPY":
                        if (!_options.ReadOnly)
                        {
                            await HandleMoveOrCopyAsync(stream, request, local, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        break;
                }

                await WriteStatusAsync(stream, 405, "Method Not Allowed", "Allow: OPTIONS, GET, HEAD, PROPFIND\r\n", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (IOException)
            {
                // client disconnected
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("SimCloud request failed: " + exception.Message);
            }
        }
    }

    private bool IsAuthorized(Request request)
    {
        if (!request.Headers.TryGetValue("authorization", out string? header) ||
            !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim()));
            int colon = decoded.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return false;
            }

            string user = decoded.Substring(0, colon);
            string password = decoded.Substring(colon + 1);
            return string.Equals(user, _options.User, StringComparison.Ordinal) &&
                   string.Equals(password, _options.Password, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private bool TryResolve(string requestPath, out string local, out bool isRoot)
    {
        local = string.Empty;
        isRoot = false;

        string relative = Uri.UnescapeDataString(requestPath).TrimStart('/');
        try
        {
            local = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception)
        {
            return false;
        }

        // PathGuard refuses anything that resolves outside the served folder (".." and friends).
        isRoot = PathGuard.IsSamePath(local, _root);
        return isRoot || PathGuard.IsUnder(local, _root);
    }

    private async Task HandlePropFindAsync(NetworkStream stream, Request request, string local, CancellationToken cancellationToken)
    {
        if (!File.Exists(local) && !Directory.Exists(local))
        {
            await WriteStatusAsync(stream, 404, "Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        int depth = request.Headers.TryGetValue("depth", out string? depthHeader) && int.TryParse(depthHeader, out int parsed) ? parsed : 1;
        List<string> entries = new() { local };

        if (Directory.Exists(local) && depth > 0)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(local))
            {
                entries.Add(entry);
            }
        }

        StringBuilder xml = new();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        xml.Append("<D:multistatus xmlns:D=\"DAV:\">\n");
        foreach (string entry in entries)
        {
            AppendResponse(xml, entry);
        }

        xml.Append("</D:multistatus>\n");

        byte[] payload = Encoding.UTF8.GetBytes(xml.ToString());
        string headers = "HTTP/1.1 207 Multi-Status\r\n" +
                         "Content-Type: application/xml; charset=utf-8\r\n" +
                         "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                         "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
        if (request.Method != "HEAD")
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private void AppendResponse(StringBuilder xml, string local)
    {
        bool isDirectory = Directory.Exists(local);
        string href = "/" + Path.GetRelativePath(_root, local).Replace('\\', '/');
        if (href == "/.")
        {
            href = "/";
        }

        if (isDirectory && !href.EndsWith('/'))
        {
            href += "/";
        }

        long size = 0;
        DateTime modified = DateTime.UtcNow;
        try
        {
            if (!isDirectory)
            {
                FileInfo info = new(local);
                size = info.Length;
                modified = info.LastWriteTimeUtc;
            }
            else
            {
                modified = Directory.GetLastWriteTimeUtc(local);
            }
        }
        catch (Exception)
        {
            // keep the defaults; the client still sees the resource
        }

        xml.Append("  <D:response>\n");
        xml.Append("    <D:href>").Append(XmlEscape(href)).Append("</D:href>\n");
        xml.Append("    <D:propstat>\n      <D:prop>\n");
        xml.Append(isDirectory ? "        <D:resourcetype><D:collection/></D:resourcetype>\n" : "        <D:resourcetype/>\n");
        if (!isDirectory)
        {
            xml.Append("        <D:getcontentlength>").Append(size.ToString(CultureInfo.InvariantCulture)).Append("</D:getcontentlength>\n");
            xml.Append("        <D:getcontenttype>").Append(XmlEscape(ContentTypeOf(local))).Append("</D:getcontenttype>\n");
        }

        xml.Append("        <D:getlastmodified>")
           .Append(modified.ToString("R", CultureInfo.InvariantCulture))
           .Append("</D:getlastmodified>\n");
        xml.Append("        <D:displayname>").Append(XmlEscape(Path.GetFileName(local))).Append("</D:displayname>\n");
        xml.Append("      </D:prop>\n      <D:status>HTTP/1.1 200 OK</D:status>\n    </D:propstat>\n");
        xml.Append("  </D:response>\n");
    }

    private async Task HandleGetAsync(NetworkStream stream, Request request, string local, CancellationToken cancellationToken)
    {
        if (Directory.Exists(local))
        {
            await WriteStatusAsync(stream, 405, "Method Not Allowed", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(local))
        {
            await WriteStatusAsync(stream, 404, "Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        long size = new FileInfo(local).Length;
        long start = 0;
        long end = size > 0 ? size - 1 : 0;
        bool partial = false;

        if (request.Headers.TryGetValue("range", out string? range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            string spec = range.Substring(6).Trim();
            int dash = spec.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0 &&
                long.TryParse(spec.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out long from) &&
                from >= 0 && from < size)
            {
                start = from;
                string tail = spec.Substring(dash + 1).Trim();
                if (tail.Length > 0 &&
                    long.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out long to) && to >= from)
                {
                    end = Math.Min(to, size - 1);
                }

                partial = true;
            }
        }

        long length = end - start + 1;
        StringBuilder headers = new();
        headers.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        headers.Append("Content-Type: ").Append(ContentTypeOf(local)).Append("\r\n");
        headers.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        headers.Append("Accept-Ranges: bytes\r\n");
        headers.Append("Last-Modified: ").Append(File.GetLastWriteTimeUtc(local).ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
        if (partial)
        {
            headers.Append("Content-Range: bytes ").Append(start.ToString(CultureInfo.InvariantCulture)).Append('-')
                   .Append(end.ToString(CultureInfo.InvariantCulture)).Append('/')
                   .Append(size.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        headers.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken).ConfigureAwait(false);

        if (request.Method == "HEAD" || length <= 0)
        {
            return;
        }

        using FileStream file = new(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBufferSize, FileOptions.Asynchronous);
        file.Seek(start, SeekOrigin.Begin);

        byte[] buffer = new byte[CopyBufferSize];
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
            remaining -= read;
        }
    }

    private async Task HandlePutAsync(NetworkStream stream, Request request, string local, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue("content-length", out string? lengthText) ||
            !long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long length))
        {
            await WriteStatusAsync(stream, 411, "Length Required", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? directory = Path.GetDirectoryName(local);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory!);
        }

        bool existed = File.Exists(local);
        await using (FileStream file = new(local, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
        {
            byte[] buffer = new byte[CopyBufferSize];
            long remaining = length;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = await request.Stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }

        await WriteStatusAsync(stream, existed ? 200 : 201, existed ? "OK" : "Created", string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleMkColAsync(NetworkStream stream, string local, CancellationToken cancellationToken)
    {
        if (Directory.Exists(local) || File.Exists(local))
        {
            await WriteStatusAsync(stream, 405, "Method Not Allowed", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(local);
        await WriteStatusAsync(stream, 201, "Created", string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDeleteAsync(NetworkStream stream, string local, bool isRoot, CancellationToken cancellationToken)
    {
        if (isRoot)
        {
            await WriteStatusAsync(stream, 403, "Forbidden", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (File.Exists(local))
        {
            File.Delete(local);
            await WriteStatusAsync(stream, 204, "No Content", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(local))
        {
            Directory.Delete(local, recursive: true);
            await WriteStatusAsync(stream, 204, "No Content", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteStatusAsync(stream, 404, "Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleMoveOrCopyAsync(NetworkStream stream, Request request, string local, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue("destination", out string? destination) || string.IsNullOrWhiteSpace(destination))
        {
            await WriteStatusAsync(stream, 400, "Bad Request", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        string destinationPath = destination;
        if (Uri.TryCreate(destination, UriKind.Absolute, out Uri? uri))
        {
            destinationPath = uri.AbsolutePath;
        }

        if (!TryResolve(destinationPath, out string target, out bool targetIsRoot) || targetIsRoot)
        {
            await WriteStatusAsync(stream, 403, "Forbidden", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool overwrite = !request.Headers.TryGetValue("overwrite", out string? overwriteHeader) ||
                         !string.Equals(overwriteHeader, "F", StringComparison.OrdinalIgnoreCase);

        if ((File.Exists(target) || Directory.Exists(target)) && !overwrite)
        {
            await WriteStatusAsync(stream, 412, "Precondition Failed", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(local) && !Directory.Exists(local))
        {
            await WriteStatusAsync(stream, 404, "Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent!);
        }

        if (request.Method == "MOVE")
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }

            if (Directory.Exists(local))
            {
                Directory.Move(local, target);
            }
            else
            {
                File.Move(local, target, overwrite: true);
            }

            await WriteStatusAsync(stream, 201, "Created", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(local))
        {
            await WriteStatusAsync(stream, 501, "Not Implemented", string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        File.Copy(local, target, overwrite: true);
        await WriteStatusAsync(stream, 201, "Created", string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteStatusAsync(NetworkStream stream, int status, string reason, string extraHeaders, CancellationToken cancellationToken)
    {
        string headers = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n" +
                         extraHeaders +
                         "Content-Length: 0\r\n" +
                         "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
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
                string text = Encoding.UTF8.GetString(buffer, 0, total - 4);
                return Request.Parse(text, stream);
            }
        }

        return null;
    }

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".md" => "text/plain",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    private static string XmlEscape(string value) => SecurityElementEscape(value);

    private static string SecurityElementEscape(string value)
    {
        StringBuilder builder = new(value.Length + 8);
        foreach (char character in value)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&apos;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>One parsed HTTP request.</summary>
    private sealed class Request
    {
        private Request(string method, string path, Dictionary<string, string> headers, NetworkStream stream)
        {
            Method = method;
            Path = path;
            Headers = headers;
            Stream = stream;
        }

        public string Method { get; }

        public string Path { get; }

        public Dictionary<string, string> Headers { get; }

        public NetworkStream Stream { get; }

        public static Request? Parse(string text, NetworkStream stream)
        {
            string[] lines = text.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }

            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2)
            {
                return null;
            }

            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            return new Request(parts[0].ToUpperInvariant(), parts[1], headers, stream);
        }
    }
}
