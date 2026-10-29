using System;
using System.IO;
using System.Text;
using NimbusX.Core.Json;

namespace NimbusX.Core.Io;

/// <summary>
/// Read/write helpers that never leave a half written configuration file behind.
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes text via a temporary file plus rename/replace.</summary>
    public static void WriteAllText(string path, string contents)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        Replace(temp, path);
    }

    /// <summary>Serialises <paramref name="value"/> as indented JSON and writes it atomically.</summary>
    public static void WriteJson<T>(string path, T value)
    {
        WriteAllText(path, JsonDefaults.Serialize(value));
    }

    /// <summary>Reads JSON, returning <paramref name="fallback"/> when the file is missing or unreadable.</summary>
    public static T? ReadJson<T>(string path, T? fallback = default)
    {
        T? value = ReadJson(path, fallback, out _);
        return value;
    }

    /// <summary>
    /// Reads JSON like <see cref="ReadJson{T}(string, T?)"/>, and reports why the fallback was used.
    /// </summary>
    /// <param name="problem">
    /// Null when the value was read; otherwise a sentence describing the unreadable file, so the caller can warn
    /// the user instead of silently replacing their settings with defaults.
    /// </param>
    public static T? ReadJson<T>(string path, T? fallback, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path))
            {
                problem = "The file does not exist.";
                return fallback;
            }

            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                problem = "The file is empty.";
                return fallback;
            }

            T? value = JsonDefaults.Deserialize<T>(text);
            if (value is null)
            {
                problem = "The file does not contain the expected object.";
                return fallback;
            }

            return value;
        }
        catch (Exception exception)
        {
            problem = exception.GetType().Name + ": " + exception.Message;
            return fallback;
        }
    }

    /// <summary>Reads text, or null when the file is missing.</summary>
    public static string? ReadAllTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Replaces <paramref name="destination"/> with <paramref name="source"/>.</summary>
    public static void Replace(string source, string destination)
    {
        if (File.Exists(destination))
        {
            try
            {
                File.Replace(source, destination, null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException)
            {
                // fall through to move-over
            }
            catch (PlatformNotSupportedException)
            {
                // fall through to move-over
            }

            File.Delete(destination);
        }

        File.Move(source, destination);
    }

    /// <summary>Best effort delete that never throws.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return false;
    }
}
