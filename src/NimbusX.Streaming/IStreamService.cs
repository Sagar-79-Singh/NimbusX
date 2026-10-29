using System;
using System.Collections.Generic;

namespace NimbusX.Streaming;

/// <summary>Serves files that live on a NimbusX cloud drive to media players over a loopback URL.</summary>
public interface IStreamService
{
    /// <summary>Raised when a session opens or closes.</summary>
    event EventHandler<StreamSession>? SessionChanged;

    IReadOnlyList<StreamSession> Sessions { get; }

    /// <summary>Loopback base URL of the proxy (empty until it has a session).</summary>
    string BaseUrl { get; }

    /// <summary>Opens <paramref name="relativePath"/> on an already-mounted drive for streaming.</summary>
    StreamOpenResult Open(string accountId, string relativePath);

    /// <summary>Reads container and duration facts without serving anything.</summary>
    MediaInfo? Probe(string accountId, string relativePath, out string? error);

    bool Close(string sessionId);

    void CloseAll();

    /// <summary>Formats a shareable-but-local playlist entry (.m3u line) or null when the session is gone.</summary>
    string? BuildPlaylistLine(string sessionId);
}
