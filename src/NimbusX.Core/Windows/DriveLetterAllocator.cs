using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NimbusX.Core.Windows;

/// <summary>
/// Assigns one drive letter per mount and guarantees two mounts never share a letter.
/// </summary>
/// <remarks>
/// The allocator asks the OS which letters are already visible (real disks, CD-ROMs, other mounts) and the
/// caller which letters NimbusX has reserved, then picks the preferred letter when free or the next free one
/// scanning forward from it and wrapping Z to C. A and B are never used.
/// </remarks>
public sealed class DriveLetterAllocator
{
    private readonly Func<IReadOnlyList<char>> _inUseProvider;
    private readonly object _gate = new();

    public DriveLetterAllocator(Func<IReadOnlyList<char>>? inUseProvider = null)
    {
        _inUseProvider = inUseProvider ?? GetVisibleLetters;
    }

    /// <summary>Letters currently visible to the OS.</summary>
    public IReadOnlyList<char> InUse() => _inUseProvider();

    /// <summary>
    /// Allocates a letter. <paramref name="reserved"/> are letters NimbusX has already promised to other mounts
    /// or the pool.
    /// </summary>
    public DriveLetter Allocate(DriveLetter? preferred, IEnumerable<DriveLetter>? reserved = null)
    {
        lock (_gate)
        {
            HashSet<char> taken = BuildTaken(reserved);
            char start = preferred.HasValue && preferred.Value.IsValid ? preferred.Value.Letter : 'G';

            for (int offset = 0; offset < 24; offset++)
            {
                char candidate = (char)('C' + ((start - 'C' + offset) % 24));
                if (!taken.Contains(candidate))
                {
                    return new DriveLetter(candidate);
                }
            }

            throw new InvalidOperationException("No free drive letter is available. Unmount a drive first.");
        }
    }

    /// <summary>Allocates <paramref name="count"/> distinct letters, preferring <paramref name="preferred"/>.</summary>
    public IReadOnlyList<DriveLetter> AllocateMany(DriveLetter? preferred, IEnumerable<DriveLetter>? reserved, int count)
    {
        Guard.Positive(count, nameof(count));
        lock (_gate)
        {
            HashSet<char> taken = BuildTaken(reserved);
            List<DriveLetter> result = new(count);
            char start = preferred.HasValue && preferred.Value.IsValid ? preferred.Value.Letter : 'G';

            for (int offset = 0; offset < 24 && result.Count < count; offset++)
            {
                char candidate = (char)('C' + ((start - 'C' + offset) % 24));
                if (taken.Contains(candidate))
                {
                    continue;
                }

                taken.Add(candidate);
                result.Add(new DriveLetter(candidate));
            }

            if (result.Count < count)
            {
                throw new InvalidOperationException(
                    "Only " + result.Count + " free drive letter(s) are available but " + count + " were requested.");
            }

            return result;
        }
    }

    /// <summary>True when the letter is free for NimbusX to use.</summary>
    public bool IsFree(DriveLetter letter, IEnumerable<DriveLetter>? reserved = null)
    {
        if (!letter.IsValid)
        {
            return false;
        }

        return !BuildTaken(reserved).Contains(letter.Letter);
    }

    private HashSet<char> BuildTaken(IEnumerable<DriveLetter>? reserved)
    {
        HashSet<char> taken = new();
        foreach (char letter in _inUseProvider())
        {
            taken.Add(char.ToUpperInvariant(letter));
        }

        if (reserved is not null)
        {
            foreach (DriveLetter letter in reserved)
            {
                taken.Add(letter.Letter);
            }
        }

        return taken;
    }

    /// <summary>Letters that Windows currently reports as drive roots.</summary>
    public static IReadOnlyList<char> GetVisibleLetters()
    {
        List<char> letters = new();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string name = drive.Name;
                if (name.Length >= 2 && char.IsLetter(name[0]))
                {
                    letters.Add(char.ToUpperInvariant(name[0]));
                }
            }
        }
        catch (Exception)
        {
            // If enumeration fails the allocator falls back to "everything is free", and the mount engine
            // will report the real conflict when rclone tries to bind the letter.
        }

        return letters;
    }

    /// <summary>True when a drive root currently exists and can be opened (used for the ready check).</summary>
    public static bool IsVisible(DriveLetter letter)
    {
        if (!letter.IsValid)
        {
            return false;
        }

        try
        {
            return Directory.Exists(letter.Root);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
