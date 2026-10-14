using System.Text;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackSubtitleFileTests
{
    private const string AbcContentSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Theory]
    [InlineData(".srt")]
    [InlineData(".SRT")]
    [InlineData(".ass")]
    [InlineData(".ASS")]
    [InlineData(".ssa")]
    [InlineData(".SSA")]
    [InlineData(".vtt")]
    [InlineData(".VTT")]
    [InlineData(".sub")]
    [InlineData(".SUB")]
    public void Supported_extensions_capture_a_canonical_path_and_the_exact_content_fingerprint(string extension)
    {
        using var fixture = new TemporaryAccountStore();
        var expectedPath = Path.Combine(fixture.DirectoryPath, "captions" + extension);
        var sourcePath = Path.Combine(fixture.DirectoryPath, "nested", "..", ".", "captions" + extension);
        var data = Encoding.ASCII.GetBytes("abc");

        var subtitle = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(sourcePath, data));

        Assert.Equal(expectedPath, PlaybackSubtitleFile.NormalizePath(sourcePath));
        Assert.Equal(expectedPath, subtitle.Path);
        Assert.Equal(3, subtitle.Length);
        Assert.Equal(AbcContentSha256, subtitle.ContentSha256);
        Assert.Equal(Encoding.ASCII.GetBytes("abc"), data);
        Assert.False(File.Exists(expectedPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, "nested")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("captions.srt")]
    [InlineData("./captions.srt")]
    [InlineData("../captions.srt")]
    public void Missing_blank_and_relative_paths_are_rejected(string? path)
    {
        Assert.Null(PlaybackSubtitleFile.NormalizePath(path));
        Assert.Null(PlaybackSubtitleFile.Capture(path, [1]));
    }

    [Theory]
    [InlineData("captions")]
    [InlineData("captions.txt")]
    [InlineData("captions.srt.bak")]
    [InlineData("captions.png")]
    public void Unsupported_file_extensions_are_rejected(string fileName)
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "folder.srt", fileName);

        Assert.Null(PlaybackSubtitleFile.NormalizePath(path));
        Assert.Null(PlaybackSubtitleFile.Capture(path, [1]));
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\n")]
    [InlineData("\t")]
    public void Control_characters_in_an_absolute_path_are_rejected(string character)
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "captions" + character + ".srt");

        Assert.Null(PlaybackSubtitleFile.NormalizePath(path));
        Assert.Null(PlaybackSubtitleFile.Capture(path, [1]));
    }

    [Fact]
    public void The_path_character_limit_accepts_the_boundary_and_rejects_the_next_character()
    {
        using var fixture = new TemporaryAccountStore();
        var acceptedPath = PathWithLength(fixture, PlaybackSubtitleFile.MaximumPathCharacters);
        var rejectedPath = PathWithLength(fixture, PlaybackSubtitleFile.MaximumPathCharacters + 1);

        Assert.Equal(PlaybackSubtitleFile.MaximumPathCharacters, acceptedPath.Length);
        Assert.Equal(PlaybackSubtitleFile.MaximumPathCharacters + 1, rejectedPath.Length);
        Assert.Equal(acceptedPath, PlaybackSubtitleFile.NormalizePath(acceptedPath));
        Assert.Equal(acceptedPath, Assert.IsType<LocalSubtitlePreference>(
            PlaybackSubtitleFile.Capture(acceptedPath, [1])).Path);
        Assert.Null(PlaybackSubtitleFile.NormalizePath(rejectedPath));
        Assert.Null(PlaybackSubtitleFile.Capture(rejectedPath, [1]));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.DirectoryPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PlaybackSubtitleFile.MaximumBytes + 1)]
    public void Empty_and_oversized_content_is_not_captured(int length)
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "captions.srt");

        Assert.Null(PlaybackSubtitleFile.Capture(path, new byte[length]));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Capture_rejects_a_null_content_buffer()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "captions.srt");

        var error = Assert.Throws<ArgumentNullException>(() => PlaybackSubtitleFile.Capture(path, null!));

        Assert.Equal("data", error.ParamName);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(PlaybackSubtitleFile.MaximumBytes)]
    public async Task Files_at_both_valid_size_boundaries_restore_only_the_original_bytes(int length)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "captions.srt");
        var data = new byte[length];
        data[0] = 0x61;
        data[^1] = 0x7f;
        await File.WriteAllBytesAsync(path, data, token);
        var subtitle = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(path, data));

        var restored = await PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, token);

        Assert.Equal(length, subtitle.Length);
        Assert.Equal(length, restored.Length);
        Assert.Equal(data, restored);
        Assert.Equal(data, await File.ReadAllBytesAsync(path, token));
    }

    [Fact]
    public async Task Restore_preserves_the_BOM_Unicode_and_line_endings_without_reencoding()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "captions.VTT");
        byte[] data = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(
            "WEBVTT\r\n\r\n00:00.000 --> 00:01.000\r\nCaf\u00e9 \U0001F3AC\r\n")];
        await File.WriteAllBytesAsync(path, data, token);
        var subtitle = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(path, data));
        var original = subtitle with { };

        var restored = await PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, token);

        Assert.Equal(data, restored);
        Assert.Equal(original, subtitle);
        Assert.Equal(data, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(new[] { path }, Directory.GetFiles(fixture.DirectoryPath));
    }

    [Fact]
    public async Task A_replacement_with_the_same_path_name_and_length_is_rejected_by_its_SHA256()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "captions.srt");
        var original = Encoding.ASCII.GetBytes("abc");
        var replacement = Encoding.ASCII.GetBytes("abd");
        await File.WriteAllBytesAsync(path, original, token);
        var subtitle = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(path, original));
        await File.WriteAllBytesAsync(path, replacement, token);

        var error = await Assert.ThrowsAsync<LocalSubtitleRestoreException>(() =>
            PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, token));

        Assert.Equal(subtitle.Length, replacement.Length);
        Assert.Equal(LocalSubtitleRestoreIssue.Changed, error.Issue);
        Assert.Equal(AbcContentSha256, subtitle.ContentSha256);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(path, token));
    }

    [Fact]
    public async Task An_identical_same_named_file_in_another_directory_never_replaces_the_saved_path()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var firstDirectory = Path.Combine(fixture.DirectoryPath, "first");
        var secondDirectory = Path.Combine(fixture.DirectoryPath, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstPath = Path.Combine(firstDirectory, "captions.srt");
        var secondPath = Path.Combine(secondDirectory, "captions.srt");
        var data = Encoding.ASCII.GetBytes("abc");
        await File.WriteAllBytesAsync(firstPath, data, token);
        await File.WriteAllBytesAsync(secondPath, data, token);
        var first = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(firstPath, data));
        var second = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(secondPath, data));

        Assert.Equal(Path.GetFileName(first.Path), Path.GetFileName(second.Path));
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(first.ContentSha256, second.ContentSha256);
        Assert.Equal(data, await PlaybackSubtitleFile.ReadVerifiedAsync(first, token));
        Assert.Equal(data, await PlaybackSubtitleFile.ReadVerifiedAsync(second, token));

        await File.WriteAllBytesAsync(firstPath, Encoding.ASCII.GetBytes("abd"), token);
        var changed = await Assert.ThrowsAsync<LocalSubtitleRestoreException>(() =>
            PlaybackSubtitleFile.ReadVerifiedAsync(first, token));
        Assert.Equal(LocalSubtitleRestoreIssue.Changed, changed.Issue);

        File.Delete(firstPath);
        var missing = await Assert.ThrowsAsync<LocalSubtitleRestoreException>(() =>
            PlaybackSubtitleFile.ReadVerifiedAsync(first, token));
        Assert.Equal(LocalSubtitleRestoreIssue.Unavailable, missing.Issue);
        Assert.Equal(data, await PlaybackSubtitleFile.ReadVerifiedAsync(second, token));
        Assert.False(File.Exists(firstPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_file_or_parent_is_unavailable_and_is_not_created(bool missingParent)
    {
        using var fixture = new TemporaryAccountStore();
        var directory = missingParent ? Path.Combine(fixture.DirectoryPath, "missing") : fixture.DirectoryPath;
        var path = Path.Combine(directory, "captions.srt");
        var subtitle = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(path, [1, 2, 3]));

        var error = await Assert.ThrowsAsync<LocalSubtitleRestoreException>(() =>
            PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, TestContext.Current.CancellationToken));

        Assert.Equal(LocalSubtitleRestoreIssue.Unavailable, error.Issue);
        Assert.False(File.Exists(path));
        Assert.Equal(!missingParent, Directory.Exists(directory));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.DirectoryPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(PlaybackSubtitleFile.MaximumBytes + 1)]
    public async Task Empty_shortened_grown_and_oversized_files_are_reported_as_changed(int replacementLength)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "captions.srt");
        byte[] data = [1, 2, 3];
        await File.WriteAllBytesAsync(path, data, token);
        var subtitle = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(path, data));
        var replacement = new byte[replacementLength];
        await File.WriteAllBytesAsync(path, replacement, token);

        var error = await Assert.ThrowsAsync<LocalSubtitleRestoreException>(() =>
            PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, token));

        Assert.Equal(LocalSubtitleRestoreIssue.Changed, error.Issue);
        Assert.Equal(3, subtitle.Length);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(path, token));
    }

    [Theory]
    [MemberData(nameof(InvalidFingerprints))]
    public async Task Invalid_fingerprints_are_rejected_before_opening_the_saved_path(string? fingerprint)
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "captions.srt");
        var captured = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(path, [1, 2, 3]));
        var subtitle = captured with { ContentSha256 = fingerprint! };

        var error = await Assert.ThrowsAsync<LocalSubtitleRestoreException>(() =>
            PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, TestContext.Current.CancellationToken));

        Assert.Equal(LocalSubtitleRestoreIssue.InvalidData, error.Issue);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.DirectoryPath));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(PlaybackSubtitleFile.MaximumBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_saved_lengths_are_rejected_before_opening_the_saved_path(int length)
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "captions.srt");
        var captured = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(path, [1, 2, 3]));
        var subtitle = captured with { Length = length };

        var error = await Assert.ThrowsAsync<LocalSubtitleRestoreException>(() =>
            PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, TestContext.Current.CancellationToken));

        Assert.Equal(LocalSubtitleRestoreIssue.InvalidData, error.Issue);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_saved_path_must_already_be_canonical_even_if_it_points_to_the_same_file()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "captions.srt");
        byte[] data = [1, 2, 3];
        await File.WriteAllBytesAsync(path, data, token);
        var captured = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(path, data));
        var noncanonicalPath = Path.Combine(fixture.DirectoryPath, ".", "captions.srt");
        var subtitle = captured with { Path = noncanonicalPath };

        Assert.Equal(path, PlaybackSubtitleFile.NormalizePath(noncanonicalPath));
        var error = await Assert.ThrowsAsync<LocalSubtitleRestoreException>(() =>
            PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, token));

        Assert.Equal(LocalSubtitleRestoreIssue.InvalidData, error.Issue);
        Assert.Equal(data, await PlaybackSubtitleFile.ReadVerifiedAsync(captured, token));
        Assert.Equal(data, await File.ReadAllBytesAsync(path, token));
    }

    [Fact]
    public async Task Restore_rejects_a_null_saved_record()
    {
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            PlaybackSubtitleFile.ReadVerifiedAsync(null!, TestContext.Current.CancellationToken));

        Assert.Equal("subtitle", error.ParamName);
    }

    [Fact]
    public async Task A_precanceled_restore_publishes_no_bytes_preserves_the_file_and_can_be_retried()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "captions.srt");
        var data = Encoding.ASCII.GetBytes("abc");
        await File.WriteAllBytesAsync(path, data, token);
        var subtitle = Assert.IsType<LocalSubtitlePreference>(PlaybackSubtitleFile.Capture(path, data));
        var original = subtitle with { };
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
        canceled.Cancel();
        byte[]? restored = null;

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            restored = await PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, canceled.Token);
        });

        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.Null(restored);
        Assert.Equal(original, subtitle);
        Assert.Equal(data, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(new[] { path }, Directory.GetFiles(fixture.DirectoryPath));
        Assert.Equal(data, await PlaybackSubtitleFile.ReadVerifiedAsync(subtitle, token));
    }

    public static IEnumerable<object?[]> InvalidFingerprints()
    {
        yield return [null];
        yield return [string.Empty];
        yield return [new string('a', 63)];
        yield return [new string('a', 65)];
        yield return [new string('F', 64)];
        yield return [new string('g', 64)];
        yield return [new string('0', 63) + "\0"];
    }

    private static string PathWithLength(TemporaryAccountStore fixture, int length)
    {
        const string extension = ".srt";
        var components = new List<string> { fixture.DirectoryPath };
        var remaining = length - fixture.DirectoryPath.Length - 1;
        while (remaining > 128)
        {
            components.Add(new string('a', 64));
            remaining -= 65;
        }
        components.Add(new string('b', remaining - extension.Length) + extension);
        return Path.Combine(components.ToArray());
    }
}
