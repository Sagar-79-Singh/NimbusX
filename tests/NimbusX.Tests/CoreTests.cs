using System;
using System.Collections.Generic;
using System.IO;
using NimbusX.Core;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;
using NimbusX.Core.Windows;
using Xunit;

namespace NimbusX.Tests;

public sealed class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1.00 KB")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(1048576L, "1.00 MB")]
    [InlineData(1073741824L, "1.00 GB")]
    [InlineData(10737418240L, "10.0 GB")]
    public void Format_uses_binary_units(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Format_rate_appends_per_second() =>
        Assert.EndsWith("/s", ByteSize.FormatRate(1048576), StringComparison.Ordinal);

    [Theory]
    [InlineData("10 GB", 10737418240L)]
    [InlineData("512 MiB", 536870912L)]
    [InlineData("1.5g", 1610612736L)]
    public void TryParse_accepts_common_forms(string text, long expected)
    {
        Assert.True(ByteSize.TryParse(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("12 lightyears")]
    public void TryParse_rejects_garbage(string text) =>
        Assert.False(ByteSize.TryParse(text, out _));
}

public sealed class DriveLetterAllocatorTests
{
    [Fact]
    public void Allocate_skips_letters_already_in_use()
    {
        DriveLetterAllocator allocator = new(() => new[] { 'D', 'E', 'F' });
        DriveLetter letter = allocator.Allocate(null);
        Assert.NotEqual('D', letter.Letter);
        Assert.NotEqual('E', letter.Letter);
        Assert.NotEqual('F', letter.Letter);
        Assert.True(letter.IsValid);
    }

    [Fact]
    public void Allocate_honours_the_preferred_letter_when_it_is_free()
    {
        DriveLetterAllocator allocator = new(() => new[] { 'D' });
        Assert.Equal('M', allocator.Allocate(new DriveLetter('M')).Letter);
    }

    [Fact]
    public void Allocate_avoids_a_letter_reserved_by_another_mount()
    {
        DriveLetterAllocator allocator = new(() => Array.Empty<char>());
        DriveLetter letter = allocator.Allocate(new DriveLetter('M'), new[] { new DriveLetter('M') });
        Assert.NotEqual('M', letter.Letter);
    }

    [Fact]
    public void AllocateMany_returns_distinct_letters()
    {
        DriveLetterAllocator allocator = new(() => new[] { 'D' });
        var letters = allocator.AllocateMany(null, null, 3);
        Assert.Equal(3, letters.Count);
        Assert.Equal(3, new HashSet<char>(System.Linq.Enumerable.Select(letters, l => l.Letter)).Count);
    }

    [Theory]
    [InlineData("M:", 'M')]
    // Drive letters are upper case everywhere in NimbusX; a lower case letter is accepted and normalised.
    [InlineData("m", 'M')]
    [InlineData("M:\\", 'M')]
    // A system drive parses like any other letter; the allocator is what refuses letters in use.
    [InlineData("A:", 'A')]
    public void TryParse_accepts_the_usual_shapes(string text, char expected)
    {
        Assert.True(DriveLetter.TryParse(text, out DriveLetter letter));
        Assert.Equal(expected, letter.Letter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB")]
    [InlineData("1")]
    [InlineData("M:\\extra")]
    [InlineData("MM:")]
    public void TryParse_rejects_invalid_letters(string text) =>
        Assert.False(DriveLetter.TryParse(text, out _));
}

public sealed class PathGuardTests
{
    [Fact]
    public void IsUnder_accepts_a_child_folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "nimbusx-guard");
        Assert.True(PathGuard.IsUnder(Path.Combine(root, "games", "bin"), root));
    }

    [Fact]
    public void IsUnder_rejects_traversal()
    {
        string root = Path.Combine(Path.GetTempPath(), "nimbusx-guard");
        string escaping = Path.Combine(root, "..", "elsewhere");
        Assert.False(PathGuard.IsUnder(escaping, root));
    }

    [Fact]
    public void IsUnder_rejects_a_sibling_with_the_same_prefix()
    {
        string root = Path.Combine(Path.GetTempPath(), "nimbusx-guard");
        Assert.False(PathGuard.IsUnder(root + "-other", root));
    }

    [Fact]
    public void CombineUnder_refuses_to_escape_the_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "nimbusx-guard");
        Assert.ThrowsAny<Exception>(() => PathGuard.CombineUnder(root, "..\\..\\windows\\system32"));
    }

    [Fact]
    public void CombineUnder_keeps_a_safe_relative_path()
    {
        string root = Path.Combine(Path.GetTempPath(), "nimbusx-guard");
        string combined = PathGuard.CombineUnder(root, "games/level1.bin");
        Assert.StartsWith(root, combined, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("level1.bin", combined, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("\"access_token\": \"ya29.a0AfH6SMBxxxxxxxxxxxxxxxx\"", "a0AfH6SMBxxxxxxxxxxxxxxxx")]
    [InlineData("cookie: ndus=abcdef123456", "abcdef123456")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwxyz")]
    [InlineData("https://user:s3cret@example.com/dav", "s3cret")]
    public void Redact_removes_the_secret_but_keeps_the_shape(string text, string secret)
    {
        string redacted = SecretRedactor.Redact(text);

        Assert.Contains(SecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_keeps_ordinary_text_untouched()
    {
        const string text = "Mounted drive M: for account My Drive";
        Assert.Equal(text, SecretRedactor.Redact(text));
    }

    [Fact]
    public void Redact_hides_rclone_token_blobs()
    {
        const string text = "token = {\"access_token\":\"abc\"}";
        Assert.DoesNotContain("abc", SecretRedactor.Redact(text), StringComparison.Ordinal);
    }
}

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteJson_then_Read_round_trips()
    {
        using TempFolder folder = new("atomic");
        string path = folder.Combine("settings.json");

        AtomicFile.WriteJson(path, new Sample { Name = "NimbusX", Count = 7 });
        Sample? read = AtomicFile.ReadJson<Sample>(path);

        Assert.NotNull(read);
        Assert.Equal("NimbusX", read!.Name);
        Assert.Equal(7, read.Count);
    }

    [Fact]
    public void ReadJson_returns_null_for_a_corrupt_file()
    {
        using TempFolder folder = new("atomic");
        string path = folder.Write("broken.json", "not json at all");
        Assert.Null(AtomicFile.ReadJson<Sample>(path));
    }

    private sealed class Sample
    {
        public string Name { get; set; } = string.Empty;

        public int Count { get; set; }
    }
}

public sealed class GuardTests
{
    [Fact]
    public void NotNullOrWhiteSpace_rejects_blank() =>
        Assert.ThrowsAny<ArgumentException>(() => Guard.NotNullOrWhiteSpace("   ", "value"));

    [Fact]
    public void Port_rejects_zero_and_oversized_values()
    {
        Assert.ThrowsAny<ArgumentException>(() => Guard.Port(0, "port"));
        Assert.ThrowsAny<ArgumentException>(() => Guard.Port(70000, "port"));
        Assert.Equal(53621, Guard.Port(53621, "port"));
    }

    [Fact]
    public void NotNegative_rejects_negative() =>
        Assert.ThrowsAny<ArgumentException>(() => Guard.NotNegative(-1, "bytes"));
}

public sealed class FormattingTests
{
    [Fact]
    public void TextFormat_shortens_long_messages_with_an_ellipsis()
    {
        string shortened = TextFormat.Truncate(new string('a', 400), 40);
        Assert.True(shortened.Length <= 40);
        Assert.EndsWith("…", shortened, StringComparison.Ordinal);
    }

    [Fact]
    public void TextFormat_keeps_short_messages_intact()
    {
        const string text = "Drive M: mounted";
        Assert.Equal(text, TextFormat.Truncate(text, 60));
    }

    [Fact]
    public void TextFormat_formats_durations()
    {
        string text = TextFormat.Duration(TimeSpan.FromSeconds(75));
        Assert.Contains("1", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "game", "games")]
    [InlineData(1, "game", "game")]
    [InlineData(2, "game", "games")]
    [InlineData(2, "box", "boxes")]
    [InlineData(2, "drive", "drives")]
    [InlineData(3, "library", "libraries")]
    [InlineData(3, "day", "days")]
    public void TextFormat_pluralizes_a_counted_noun(int count, string singular, string expected)
    {
        Assert.Equal(expected, TextFormat.Pluralize(count, singular));
    }
}
