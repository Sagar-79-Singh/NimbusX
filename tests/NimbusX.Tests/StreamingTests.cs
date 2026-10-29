using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NimbusX.Streaming;
using Xunit;

namespace NimbusX.Tests;

public sealed class HttpRangeTests
{
    [Fact]
    public void A_plain_request_means_the_whole_file()
    {
        Assert.True(HttpRange.TryParse(null, 1000, out long start, out long end, out string? error));
        Assert.Null(error);
        Assert.Equal(0, start);
        Assert.Equal(999, end);
    }

    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=500-", 500, 999)]
    [InlineData("bytes=-100", 900, 999)]
    [InlineData("bytes=900-2000", 900, 999)]
    public void Explicit_ranges_are_clamped_to_the_file(string header, long expectedStart, long expectedEnd)
    {
        Assert.True(HttpRange.TryParse(header, 1000, out long start, out long end, out string? error));
        Assert.Null(error);
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Theory]
    [InlineData("items=0-10")]
    [InlineData("bytes=0-10,20-30")]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=1000-1200")]
    public void Unsupported_or_impossible_ranges_are_refused(string header)
    {
        Assert.False(HttpRange.TryParse(header, 1000, out _, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void An_empty_file_cannot_be_ranged()
    {
        Assert.False(HttpRange.TryParse("bytes=0-10", 0, out _, out _, out string? error));
        Assert.Contains("empty", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsRangeRequest_recognises_the_header()
    {
        Assert.True(HttpRange.IsRangeRequest(" bytes=0-1"));
        Assert.False(HttpRange.IsRangeRequest("items=0-1"));
        Assert.False(HttpRange.IsRangeRequest(null));
    }
}

public sealed class MediaProbeTests
{
    [Fact]
    public void A_png_is_detected_from_its_signature()
    {
        using TempFolder folder = new("media");
        byte[] png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        string path = folder.WriteBytes("cover.png", png);

        MediaInfo info = MediaProbe.Detect(path);

        Assert.Equal(MediaKind.Image, info.Kind);
        Assert.Equal("PNG", info.Container);
        Assert.Equal("image/png", MediaProbe.ContentTypeFor(info));
    }

    [Fact]
    public void A_text_file_is_reported_as_unknown_not_as_media()
    {
        using TempFolder folder = new("media");
        string path = folder.Write("notes.txt", "these are notes, not a video");

        MediaInfo info = MediaProbe.Detect(path);

        Assert.Equal(MediaKind.Unknown, info.Kind);
        Assert.False(string.IsNullOrWhiteSpace(info.Detail));
        Assert.Null(info.DurationSeconds);
    }

    [Fact]
    public void A_missing_file_raises_a_real_error() =>
        Assert.Throws<FileNotFoundException>(() => MediaProbe.Detect(Path.Combine(Path.GetTempPath(), "nimbusx-missing.mp4")));

    [Theory]
    [InlineData("movie.mp4", true)]
    [InlineData("song.FLAC", true)]
    [InlineData("save.dat", false)]
    public void IsProbablyMedia_uses_the_extension(string name, bool expected) =>
        Assert.Equal(expected, MediaProbe.IsProbablyMedia(name));
}

public sealed class StreamRequestLineTests
{
    [Fact]
    public void A_normal_request_line_is_split()
    {
        Assert.True(StreamHttpServer.TryParseRequestLine("GET /s/token123 HTTP/1.1", out string method, out string path, out string? query));
        Assert.Equal("GET", method);
        Assert.Equal("/s/token123", path);
        Assert.Null(query);
    }

    [Fact]
    public void A_query_string_is_separated()
    {
        Assert.True(StreamHttpServer.TryParseRequestLine("HEAD /health?verbose=1 HTTP/1.1", out _, out string path, out string? query));
        Assert.Equal("/health", path);
        Assert.Equal("verbose=1", query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GET")]
    [InlineData("   ")]
    public void Malformed_request_lines_are_refused(string line) =>
        Assert.False(StreamHttpServer.TryParseRequestLine(line, out _, out _, out _));
}
