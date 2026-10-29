using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core.Logging;

namespace NimbusX.Streaming;

/// <summary>
/// Identifies a streamed file from its first bytes and, where cheap, reads the real container duration.
/// </summary>
/// <remarks>
/// Probing reads at most <see cref="ProbeBytes"/> plus a small window for the MP4/Matroska headers, so opening a
/// 90 GB game image costs a few hundred kilobytes, not a download. Windows Media Foundation is not used: it would
/// add codec dependencies that NimbusX does not ship.
/// </remarks>
public static class MediaProbe
{
    public const int ProbeBytes = 64 * 1024;

    /// <summary>How far into the file to look for an MP4 <c>moov</c> atom when it is not at the front.</summary>
    public const long MaxMoovSearchBytes = 4L * 1024 * 1024;

    public static MediaInfo Detect(string path, IAppLogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        FileInfo info = new(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The file was not found on the mounted drive.", path);
        }

        long size = info.Length;
        byte[] head;
        try
        {
            using FileStream stream = OpenRead(path);
            head = ReadAtMost(stream, ProbeBytes);
        }
        catch (Exception exception)
        {
            logger.Warn("Could not read the first bytes of '" + path + "': " + exception.Message, "Streaming");
            return new MediaInfo(MediaKind.Unknown, "Unknown", size, null, false, "The file could not be opened: " + exception.Message);
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        Signature signature = Identify(head, extension);
        double? duration = null;
        if (signature.Container == "MP4" || signature.Container == "Matroska")
        {
            duration = TryReadDuration(path, signature.Container, logger);
        }

        return new MediaInfo(
            signature.Kind,
            signature.Container,
            size,
            duration,
            signature.Seekable,
            signature.Evidence);
    }

    public static bool IsProbablyMedia(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".m4v" or ".wmv" or ".flv"
            or ".mp3" or ".flac" or ".wav" or ".aac" or ".ogg" or ".opus" or ".m4a"
            or ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp"
            or ".iso" or ".img";
    }

    /// <summary>Content-Type a media player expects for this container.</summary>
    public static string ContentTypeFor(MediaInfo info)
    {
        string container = info.Container.ToLowerInvariant();
        return container switch
        {
            "mp4" => "video/mp4",
            "matroska" => info.Kind == MediaKind.Audio ? "audio/x-matroska" : "video/x-matroska",
            "webm" => "video/webm",
            "avi" => "video/x-msvideo",
            "mov" => "video/quicktime",
            "mpeg-ts" => "video/mp2t",
            "flac" => "audio/flac",
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "png" => "image/png",
            "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "iso 9660" => "application/x-iso9660-image",
            "zip" => "application/zip",
            "7-zip" => "application/x-7z-compressed",
            "rar" => "application/vnd.rar",
            _ => "application/octet-stream"
        };
    }

    private static Signature Identify(byte[] head, string extension)
    {
        if (head.Length >= 0x8006 && Matches(head, 0x8001, "CD001"))
        {
            return new Signature(MediaKind.Iso, "ISO 9660", true, "ISO 9660 volume descriptor ('CD001') at offset 0x8001.");
        }

        if (head.Length >= 8 && Matches(head, 4, "ftyp"))
        {
            string brand = head.Length >= 12 ? Encoding.ASCII.GetString(head, 8, 4).Trim() : string.Empty;
            bool audioOnly = extension is ".m4a" or ".m4b" || brand is "M4A " or "M4B " or "M4A" or "M4B";
            MediaKind kind = audioOnly ? MediaKind.Audio : MediaKind.Video;
            return new Signature(kind, "MP4", true, "ISO base media box ('ftyp', brand '" + brand + "') at offset 4.");
        }

        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
        {
            return new Signature(MediaKind.Video, "Matroska", true, "EBML header (Matroska/WebM).");
        }

        if (head.Length >= 12 && Matches(head, 0, "RIFF") && Matches(head, 8, "AVI "))
        {
            return new Signature(MediaKind.Video, "AVI", true, "RIFF container with 'AVI ' form type.");
        }

        if (head.Length >= 12 && Matches(head, 0, "RIFF") && Matches(head, 8, "WAVE"))
        {
            return new Signature(MediaKind.Audio, "WAV", true, "RIFF container with 'WAVE' form type.");
        }

        if (head.Length >= 4 && Matches(head, 0, "fLaC"))
        {
            return new Signature(MediaKind.Audio, "FLAC", true, "'fLaC' stream marker.");
        }

        if (head.Length >= 3 && Matches(head, 0, "ID3"))
        {
            return new Signature(MediaKind.Audio, "MP3", true, "'ID3' tag header.");
        }

        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
        {
            return new Signature(MediaKind.Audio, "MP3", true, "MPEG audio frame sync.");
        }

        if (head.Length >= 8 && Matches(head, 0, "OggS"))
        {
            return new Signature(MediaKind.Audio, "OGG", true, "'OggS' capture pattern.");
        }

        if (head.Length >= 3 && head[0] == 0x89 && Matches(head, 1, "PNG"))
        {
            return new Signature(MediaKind.Image, "PNG", true, "PNG signature.");
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return new Signature(MediaKind.Image, "JPEG", true, "JPEG SOI marker.");
        }

        if (head.Length >= 6 && (Matches(head, 0, "GIF87a") || Matches(head, 0, "GIF89a")))
        {
            return new Signature(MediaKind.Image, "GIF", true, "GIF header.");
        }

        if (head.Length >= 12 && Matches(head, 0, "RIFF") && Matches(head, 8, "WEBP"))
        {
            return new Signature(MediaKind.Image, "WEBP", true, "RIFF container with 'WEBP' form type.");
        }

        if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
        {
            return new Signature(MediaKind.Archive, "ZIP", true, "'PK\\x03\\x04' local file header.");
        }

        if (head.Length >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
        {
            return new Signature(MediaKind.Archive, "7-Zip", true, "7z signature.");
        }

        if (head.Length >= 7 && Matches(head, 0, "Rar!\u001a\u0007"))
        {
            return new Signature(MediaKind.Archive, "RAR", true, "RAR signature.");
        }

        if (head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z')
        {
            return new Signature(MediaKind.Executable, "PE", true, "'MZ' DOS header.");
        }

        if (head.Length >= 4 && head[0] == 0x7F && Matches(head, 1, "ELF"))
        {
            return new Signature(MediaKind.Executable, "ELF", true, "ELF magic.");
        }

        Signature byExtension = FromExtension(extension);
        if (byExtension.Kind != MediaKind.Unknown)
        {
            return byExtension;
        }

        return new Signature(MediaKind.Unknown, "Unknown", false, "No signature matched and the extension is not a known media type.");
    }

    private static Signature FromExtension(string extension) => extension switch
    {
        ".mp4" or ".m4v" => new Signature(MediaKind.Video, "MP4", true, "File extension only; signature not recognised."),
        ".mkv" => new Signature(MediaKind.Video, "Matroska", true, "File extension only; signature not recognised."),
        ".webm" => new Signature(MediaKind.Video, "Matroska", true, "File extension only; signature not recognised."),
        ".avi" => new Signature(MediaKind.Video, "AVI", true, "File extension only; signature not recognised."),
        ".mov" => new Signature(MediaKind.Video, "QuickTime", true, "File extension only; signature not recognised."),
        ".ts" => new Signature(MediaKind.Video, "MPEG-TS", true, "File extension only; signature not recognised."),
        ".mp3" => new Signature(MediaKind.Audio, "MP3", true, "File extension only; signature not recognised."),
        ".flac" => new Signature(MediaKind.Audio, "FLAC", true, "File extension only; signature not recognised."),
        ".wav" => new Signature(MediaKind.Audio, "WAV", true, "File extension only; signature not recognised."),
        ".ogg" or ".opus" => new Signature(MediaKind.Audio, "OGG", true, "File extension only; signature not recognised."),
        ".m4a" or ".aac" => new Signature(MediaKind.Audio, "MP4", true, "File extension only; signature not recognised."),
        ".jpg" or ".jpeg" => new Signature(MediaKind.Image, "JPEG", true, "File extension only; signature not recognised."),
        ".png" => new Signature(MediaKind.Image, "PNG", true, "File extension only; signature not recognised."),
        ".gif" => new Signature(MediaKind.Image, "GIF", true, "File extension only; signature not recognised."),
        ".webp" => new Signature(MediaKind.Image, "WEBP", true, "File extension only; signature not recognised."),
        ".iso" or ".img" => new Signature(MediaKind.Iso, "ISO 9660", true, "File extension only; volume descriptor not read."),
        ".zip" => new Signature(MediaKind.Archive, "ZIP", true, "File extension only; signature not recognised."),
        _ => new Signature(MediaKind.Unknown, "Unknown", false, "Unknown extension and no signature.")
    };

    /// <summary>
    /// Reads a real duration out of the container. Returns null when the container does not expose it cheaply.
    /// </summary>
    private static double? TryReadDuration(string path, string container, IAppLogger logger)
    {
        try
        {
            using FileStream stream = OpenRead(path);
            return container == "MP4" ? ReadMp4Duration(stream) : ReadMatroskaDuration(stream);
        }
        catch (Exception exception)
        {
            logger.Debug("Could not read the duration of '" + path + "': " + exception.Message, "Streaming");
            return null;
        }
    }

    private static double? ReadMp4Duration(FileStream stream)
    {
        long length = stream.Length;
        long offset = 0;
        int boxes = 0;

        while (offset < length && boxes < 512 && offset < MaxMoovSearchBytes)
        {
            stream.Position = offset;
            byte[] header = new byte[16];
            int read = stream.Read(header, 0, header.Length);
            if (read < 8)
            {
                return null;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(header, 4, 4);
            long headerSize = 8;

            if (size == 1)
            {
                if (read < 16)
                {
                    return null;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - offset;
            }

            if (size < headerSize)
            {
                return null;
            }

            if (type is "moov")
            {
                return ReadMvhd(stream, offset + headerSize, Math.Min(size - headerSize, 32L * 1024 * 1024));
            }

            // 'mdat' can be enormous; skip it instead of reading through it.
            offset += size;
            boxes++;
        }

        return null;
    }

    private static double? ReadMvhd(FileStream stream, long start, long length)
    {
        const int chunk = 256 * 1024;
        byte[] buffer = new byte[chunk];
        long position = start;
        long end = start + length;

        while (position < end)
        {
            int want = (int)Math.Min(chunk, end - position);
            stream.Position = position;
            int read = stream.Read(buffer, 0, want);
            if (read < 8)
            {
                return null;
            }

            int index = 0;
            while (index + 8 <= read)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(index, 4));
                string type = Encoding.ASCII.GetString(buffer, index + 4, 4);

                if (type == "mvhd")
                {
                    int body = index + 8;
                    if (body + 20 > read)
                    {
                        return null;
                    }

                    int version = buffer[body];
                    if (version == 1)
                    {
                        if (body + 32 > read)
                        {
                            return null;
                        }

                        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(body + 20, 4));
                        ulong duration = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(body + 24, 8));
                        return timescale == 0 ? null : (double)duration / timescale;
                    }

                    uint ts32 = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(body + 12, 4));
                    uint dur32 = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(body + 16, 4));
                    return ts32 == 0 ? null : (double)dur32 / ts32;
                }

                if (size < 8 || size > read - index)
                {
                    break;
                }

                index += (int)size;
            }

            if (read < chunk)
            {
                return null;
            }

            position += read - 7;
        }

        return null;
    }

    private static double? ReadMatroskaDuration(FileStream stream)
    {
        // Segment > Info > Duration: EBML float, in TimecodeScale units (default 1 000 000 ns = 1 ms).
        byte[] buffer = new byte[256 * 1024];
        stream.Position = 0;
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read < 16)
        {
            return null;
        }

        int infoIndex = IndexOf(buffer, read, new byte[] { 0x15, 0x49, 0xA9, 0x66 });
        if (infoIndex < 0)
        {
            return null;
        }

        int? sizeLength = VintLength(buffer, infoIndex + 4);
        if (sizeLength is null)
        {
            return null;
        }

        long infoSize = VintValue(buffer, infoIndex + 4, sizeLength.Value);
        if (infoSize <= 0)
        {
            return null;
        }

        int infoStart = infoIndex + 4 + sizeLength.Value;
        int infoEnd = (int)Math.Min(read, infoStart + infoSize);

        double scale = 1_000_000d;
        double? duration = null;

        int cursor = infoStart;
        while (cursor < infoEnd)
        {
            int idLen = VintLength(buffer, cursor) ?? -1;
            if (idLen <= 0)
            {
                break;
            }

            int id = (int)VintValueKeepMarker(buffer, cursor, idLen);
            int sizeLen = VintLength(buffer, cursor + idLen) ?? -1;
            if (sizeLen <= 0)
            {
                break;
            }

            long valueSize = VintValue(buffer, cursor + idLen, sizeLen);
            int valueStart = cursor + idLen + sizeLen;
            if (valueSize <= 0 || valueStart + valueSize > infoEnd)
            {
                break;
            }

            if (id == 0x2AD7B1 && valueSize <= 8)
            {
                scale = ReadUnsigned(buffer, valueStart, (int)valueSize);
            }
            else if (id == 0x4489)
            {
                duration = valueSize switch
                {
                    4 => (double)BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(valueStart, 4)),
                    8 => BinaryPrimitives.ReadDoubleBigEndian(buffer.AsSpan(valueStart, 8)),
                    _ => (double?)null
                };
            }

            cursor = valueStart + (int)valueSize;
        }

        if (duration is null or <= 0)
        {
            return null;
        }

        // Duration is expressed in TimecodeScale units; scale is nanoseconds per tick.
        return duration.Value * scale / 1_000_000_000d;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);

    private static byte[] ReadAtMost(FileStream stream, int bytes)
    {
        byte[] buffer = new byte[bytes];
        int total = 0;
        int read;
        while (total < bytes && (read = stream.Read(buffer, total, bytes - total)) > 0)
        {
            total += read;
        }

        if (total == bytes)
        {
            return buffer;
        }

        byte[] trimmed = new byte[total];
        Array.Copy(buffer, trimmed, total);
        return trimmed;
    }

    private static bool Matches(byte[] data, int offset, string ascii)
    {
        if (offset < 0 || data.Length < offset + ascii.Length)
        {
            return false;
        }

        for (int i = 0; i < ascii.Length; i++)
        {
            if (data[offset + i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(byte[] haystack, int length, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= length; i++)
        {
            bool found = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    private static int? VintLength(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
        {
            return null;
        }

        byte first = data[offset];
        for (int length = 1; length <= 8; length++)
        {
            if ((first & (0x80 >> (length - 1))) != 0)
            {
                return length;
            }
        }

        return null;
    }

    /// <summary>Length value with the marker bit removed (used for sizes).</summary>
    private static long VintValue(byte[] data, int offset, int length)
    {
        long value = data[offset] & (0xFF >> length);
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | data[offset + i];
        }

        return value;
    }

    /// <summary>Element id: the marker bit belongs to the id.</summary>
    private static long VintValueKeepMarker(byte[] data, int offset, int length)
    {
        long value = data[offset];
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | data[offset + i];
        }

        return value;
    }

    private static long ReadUnsigned(byte[] data, int offset, int length)
    {
        long value = 0;
        for (int i = 0; i < length; i++)
        {
            value = (value << 8) | data[offset + i];
        }

        return value;
    }

    private readonly struct Signature
    {
        public Signature(MediaKind kind, string container, bool seekable, string evidence)
        {
            Kind = kind;
            Container = container;
            Seekable = seekable;
            Evidence = evidence;
        }

        public MediaKind Kind { get; }

        public string Container { get; }

        public bool Seekable { get; }

        public string Evidence { get; }
    }
}
