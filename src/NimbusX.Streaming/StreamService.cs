using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using NimbusX.Core;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Security;
using NimbusX.Mount;

namespace NimbusX.Streaming;

/// <summary>
/// Turns a path on a mounted NimbusX drive into a loopback HTTP URL with Range support.
/// </summary>
/// <remarks>
/// <para>
/// The file is read through the rclone mount, so the VFS cache and its chunk prefetching stay in charge of the
/// actual download. Nothing is copied out of the cache and no second connection to the cloud is opened.
/// </para>
/// <para>
/// Only ready mounts are served. A degraded mount still works if the drive letter exists, but a stopped one
/// produces a real error with a fix instead of a URL that would 404 later.
/// </para>
/// </remarks>
public sealed class StreamService : IStreamService, IDisposable
{
    private readonly IMountManager _mounts;
    private readonly IAppLogger _logger;
    private readonly StreamHttpServer _server;
    private readonly Dictionary<string, string> _tokensBySession = new(StringComparer.Ordinal);
    private readonly List<StreamSession> _sessions = new();
    private readonly object _gate = new();
    private bool _disposed;

    public StreamService(IMountManager mounts, IAppLogger? logger = null, StreamHttpServer? server = null)
    {
        _mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        _logger = logger ?? NullLogger.Instance;
        _server = server ?? new StreamHttpServer(_logger);
    }

    public event EventHandler<StreamSession>? SessionChanged;

    public IReadOnlyList<StreamSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.ToArray();
            }
        }
    }

    public string BaseUrl => _server.BaseUrl;

    public int ActiveConnections => _server.ActiveConnections;

    public StreamOpenResult Open(string accountId, string relativePath)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));

        string normalized = PinnedRelative(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return StreamOpenResult.Fail(
                "No file was selected.",
                "Pick a video or audio file inside the mounted drive.");
        }

        MountSnapshot? snapshot = _mounts.GetSnapshot(accountId);
        if (snapshot is null)
        {
            return StreamOpenResult.Fail(
                "That cloud drive is not mounted.",
                "Mount the drive from the Dashboard, then try streaming the file again.");
        }

        if (snapshot.Phase is not (MountPhase.Ready or MountPhase.Degraded))
        {
            return StreamOpenResult.Fail(
                "The drive is " + snapshot.Phase + ", so its files cannot be read yet.",
                "Wait until the drive shows Ready (or press Repair on the dashboard) and try again.");
        }

        string localPath;
        try
        {
            localPath = PathGuard.CombineUnder(snapshot.DriveRoot, normalized);
        }
        catch (Exception exception)
        {
            return StreamOpenResult.Fail(
                "The path is not valid inside " + snapshot.DriveLetter + ": " + exception.Message,
                "Choose the file again from the drive; links that point outside the drive are refused.");
        }

        if (!PathGuard.IsUnder(localPath, snapshot.DriveRoot))
        {
            return StreamOpenResult.Fail(
                "The path points outside " + snapshot.DriveLetter + ":.",
                "Choose the file again from the drive; paths that escape the mount are refused.");
        }

        MediaInfo media;
        try
        {
            media = MediaProbe.Detect(localPath, _logger);
        }
        catch (FileNotFoundException)
        {
            return StreamOpenResult.Fail(
                "The file '" + normalized + "' is not on " + snapshot.DriveLetter + ":.",
                "Refresh the drive (F5 in Explorer) and check that the file still exists in the cloud.");
        }
        catch (Exception exception)
        {
            return StreamOpenResult.Fail(
                "Could not open '" + normalized + "': " + exception.Message,
                "Check the rclone log for this account, then try again.");
        }

        string sessionId = RandomSecrets.Hex(6);
        string token = RandomSecrets.Password(32);
        string contentType = MediaProbe.ContentTypeFor(media);

        try
        {
            _server.Start();
        }
        catch (Exception exception)
        {
            _logger.Error("Could not start the streaming proxy.", "Streaming", exception);
            return StreamOpenResult.Fail(
                "The streaming proxy could not start: " + exception.Message,
                "Ports may be blocked by security software. Allow NimbusX on the loopback interface and try again.");
        }

        string url = _server.BaseUrl + "s/" + token;
        StreamSession session = new(
            sessionId,
            snapshot.AccountId,
            snapshot.DisplayName,
            snapshot.DriveLetter,
            normalized,
            localPath,
            url,
            media,
            DateTimeOffset.UtcNow);

        _server.Map(token, new StreamTarget(session, media, contentType));

        lock (_gate)
        {
            _sessions.Add(session);
            _tokensBySession[session.Id] = token;
        }

        _logger.Info(
            "Streaming " + normalized + " from " + snapshot.DisplayName + " (" + media.Container + ", " + media.SizeText + ") as session " + session.Id + ".",
            "Streaming");

        SessionChanged?.Invoke(this, session);
        return StreamOpenResult.Ok(session);
    }

    public MediaInfo? Probe(string accountId, string relativePath, out string? error)
    {
        error = null;
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));

        MountSnapshot? snapshot = _mounts.GetSnapshot(accountId);
        if (snapshot is null)
        {
            error = "That cloud drive is not mounted.";
            return null;
        }

        string normalized = PinnedRelative(relativePath);
        string localPath;
        try
        {
            localPath = PathGuard.CombineUnder(snapshot.DriveRoot, normalized);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }

        try
        {
            return MediaProbe.Detect(localPath, _logger);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    public bool Close(string sessionId)
    {
        Guard.NotNullOrWhiteSpace(sessionId, nameof(sessionId));

        StreamSession? session;
        string? token;
        lock (_gate)
        {
            session = _sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
            _tokensBySession.TryGetValue(sessionId, out token);
            if (session is not null)
            {
                _sessions.Remove(session);
                _tokensBySession.Remove(sessionId);
            }
        }

        if (session is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            _server.Unmap(token!);
        }

        session.Close(DateTimeOffset.UtcNow);
        if (_sessions.Count == 0)
        {
            _server.Stop();
        }

        _logger.Info("Stopped streaming session " + session.Id + " (" + session.ServedText + ").", "Streaming");
        SessionChanged?.Invoke(this, session);
        return true;
    }

    public void CloseAll()
    {
        foreach (StreamSession session in Sessions)
        {
            Close(session.Id);
        }
    }

    public string? BuildPlaylistLine(string sessionId)
    {
        StreamSession? session;
        lock (_gate)
        {
            session = _sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
        }

        if (session is null)
        {
            return null;
        }

        // A minimal M3U entry: players accept EXTINF + URL and the URL is loopback-only.
        string title = Path.GetFileName(session.RelativePath);
        string duration = session.Media.DurationSeconds is null
            ? "-1"
            : ((int)Math.Round(session.Media.DurationSeconds.Value)).ToString(CultureInfo.InvariantCulture);
        return "#EXTINF:" + duration + "," + title + Environment.NewLine + session.Url;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseAll();
        _server.Dispose();
    }

    private static string PinnedRelative(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/').Trim('/');
}
