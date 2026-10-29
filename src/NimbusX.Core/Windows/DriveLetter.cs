using System;

namespace NimbusX.Core.Windows;

/// <summary>A Windows drive letter such as <c>H:</c>.</summary>
public readonly struct DriveLetter : IEquatable<DriveLetter>, IComparable<DriveLetter>
{
    public DriveLetter(char letter)
    {
        Letter = char.ToUpperInvariant(letter);
    }

    public char Letter { get; }

    /// <summary>True for C..Z (A and B are reserved for floppy drives).</summary>
    public bool IsValid => Letter >= 'C' && Letter <= 'Z';

    /// <summary><c>H:</c></summary>
    public string Display => Letter + ":";

    /// <summary><c>H:\</c></summary>
    public string Root => Letter + ":\\";

    /// <summary>Parses "H", "H:", "H:\", "h:" and rejects anything else.</summary>
    public static bool TryParse(string? text, out DriveLetter letter)
    {
        letter = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text!.Trim();
        if (trimmed.Length < 1)
        {
            return false;
        }

        char candidate = char.ToUpperInvariant(trimmed[0]);
        if (candidate < 'A' || candidate > 'Z')
        {
            return false;
        }

        if (trimmed.Length > 1 && trimmed[1] != ':')
        {
            return false;
        }

        if (trimmed.Length > 2)
        {
            string rest = trimmed[2..].Trim();
            if (rest.Length > 0 && rest != "\\" && rest != "/")
            {
                return false;
            }
        }

        letter = new DriveLetter(candidate);
        return true;
    }

    public static DriveLetter Parse(string text) =>
        TryParse(text, out DriveLetter letter) ? letter : throw new FormatException("'" + text + "' is not a drive letter.");

    public static bool TryFromIndex(int index, out DriveLetter letter)
    {
        letter = default;
        if (index < 0 || index > 25)
        {
            return false;
        }

        letter = new DriveLetter((char)('A' + index));
        return true;
    }

    public int Index => Letter - 'A';

    public bool Equals(DriveLetter other) => other.Letter == Letter;

    public override bool Equals(object? obj) => obj is DriveLetter other && Equals(other);

    public override int GetHashCode() => Letter.GetHashCode();

    public int CompareTo(DriveLetter other) => Letter.CompareTo(other.Letter);

    public override string ToString() => Display;

    public static bool operator ==(DriveLetter left, DriveLetter right) => left.Equals(right);

    public static bool operator !=(DriveLetter left, DriveLetter right) => !left.Equals(right);
}
