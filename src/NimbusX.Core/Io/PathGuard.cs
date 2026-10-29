using System;
using System.IO;

namespace NimbusX.Core.Io;

/// <summary>
/// Path containment checks. Cache clearing and deletion code must never operate on a path it did not build,
/// so every destructive operation is validated with <see cref="IsUnder"/> first.
/// </summary>
public static class PathGuard
{
    /// <summary>Full path with a trailing separator.</summary>
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>Normalises a directory path and ensures exactly one trailing separator.</summary>
    public static string EnsureTrailingSeparator(string path)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        string trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0)
        {
            return path;
        }

        return trimmed + Path.DirectorySeparatorChar;
    }

    /// <summary>True when <paramref name="candidate"/> is the same as, or inside, <paramref name="root"/>.</summary>
    public static bool IsUnder(string? candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            string normalizedRoot = Normalize(root);
            string normalizedCandidate = Normalize(candidate);
            if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when both paths point at the same file or folder.</summary>
    public static bool IsSamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\', '/'),
                Path.GetFullPath(right).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Combines and verifies that the result stays inside <paramref name="root"/>.</summary>
    public static string CombineUnder(string root, string relative)
    {
        Guard.NotNullOrWhiteSpace(root, nameof(root));
        string combined = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsUnder(combined, root))
        {
            throw new InvalidOperationException("Path '" + combined + "' escapes the permitted root '" + root + "'.");
        }

        return combined;
    }

    /// <summary>True when the path is a drive root such as <c>D:\</c>.</summary>
    public static bool IsDriveRoot(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            return root is not null && string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when the path looks like a mapped drive letter path.</summary>
    public static bool IsDriveLetterPath(string path) =>
        !string.IsNullOrEmpty(path) && path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]);

    /// <summary>Rejects file names that Windows cannot store.</summary>
    public static bool IsValidFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name!.Length > 200)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c < 32 || c == '<' || c == '>' || c == ':' || c == '"' || c == '/' || c == '\\' || c == '|' || c == '?' || c == '*')
            {
                return false;
            }
        }

        string trimmed = name.Trim();
        return trimmed.Length > 0 && !trimmed.EndsWith(".", StringComparison.Ordinal);
    }
}
