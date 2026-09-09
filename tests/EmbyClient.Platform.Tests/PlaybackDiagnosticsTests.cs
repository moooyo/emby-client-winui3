using System.Text;
using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackDiagnosticsTests
{
    [Fact]
    public void Only_owned_local_ids_and_whitelisted_categories_reach_logs_or_snapshots()
    {
        using var fixture = new TemporaryAccountStore();
        var diagnostics = CreateDiagnostics(fixture);
        var foreignServerSession = Guid.NewGuid();
        Assert.False(diagnostics.Record(foreignServerSession, PlaybackDiagnosticEvent.Failed));
        var localId = diagnostics.CreateLocalPlaybackId();
        string[] sensitive =
        [
            "https://private.synthetic/stream?api_key=synthetic-secret",
            "X-Emby-Token: synthetic-token", "synthetic-username", "Synthetic Private Movie Title",
            "C:\\SyntheticPrivate\\movie.mkv", "synthetic server response", "synthetic exception message"
        ];
        var media = PlaybackDiagnosticMedia.FromCategories(sensitive[0], sensitive[1], sensitive[2], sensitive[3], sensitive[4]);

        Assert.True(diagnostics.Record(localId, PlaybackDiagnosticEvent.Failed, PlaybackDiagnosticError.NativePlayback, media));

        var snapshotBytes = diagnostics.CreateSnapshot();
        var snapshotText = Encoding.UTF8.GetString(snapshotBytes);
        var storedText = string.Join("\n", Directory.GetFiles(diagnostics.DirectoryPath, "*.jsonl").Select(File.ReadAllText));
        foreach (var value in sensitive.Append(foreignServerSession.ToString()))
        {
            Assert.DoesNotContain(value, snapshotText);
            Assert.DoesNotContain(value, storedText);
        }
        Assert.DoesNotContain(fixture.DirectoryPath, snapshotText);
        using var snapshot = JsonDocument.Parse(snapshotBytes);
        var record = Assert.Single(snapshot.RootElement.GetProperty("Events").EnumerateArray());
        Assert.Equal(localId, record.GetProperty("LocalPlaybackId").GetGuid());
        Assert.Equal("NativePlayback", record.GetProperty("Error").GetString());
        Assert.Equal("7.8.9.10", record.GetProperty("Versions").GetProperty("Application").GetString());
        Assert.Equal("3.4.5.6", record.GetProperty("Versions").GetProperty("EngineVersion").GetString());
        Assert.All(record.GetProperty("Media").EnumerateObject(), property => Assert.Equal("Unknown", property.Value.GetString()));
        Assert.True(snapshot.RootElement.GetProperty("LocalOnly").GetBoolean());
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact]
    public void Known_categories_remain_useful_and_undefined_enum_values_are_normalized()
    {
        using var fixture = new TemporaryAccountStore();
        var diagnostics = CreateDiagnostics(fixture);
        var id = diagnostics.CreateLocalPlaybackId();
        var categories = PlaybackDiagnosticMedia.FromCategories("DirectStream", "mp4", "h264", "aac", "vtt");
        Assert.True(diagnostics.Record(id, PlaybackDiagnosticEvent.Started, PlaybackDiagnosticError.None, categories));
        Assert.True(diagnostics.Record(id, (PlaybackDiagnosticEvent)int.MaxValue, (PlaybackDiagnosticError)int.MaxValue,
            new PlaybackDiagnosticMedia((PlaybackDiagnosticDelivery)int.MaxValue, (PlaybackDiagnosticContainer)int.MaxValue,
                (PlaybackDiagnosticVideo)int.MaxValue, (PlaybackDiagnosticAudio)int.MaxValue, (PlaybackDiagnosticSubtitle)int.MaxValue)));

        using var snapshot = JsonDocument.Parse(diagnostics.CreateSnapshot());
        var records = snapshot.RootElement.GetProperty("Events");
        var firstMedia = records[0].GetProperty("Media");
        Assert.Equal("DirectStream", firstMedia.GetProperty("Delivery").GetString());
        Assert.Equal("Mp4", firstMedia.GetProperty("Container").GetString());
        Assert.Equal("H264", firstMedia.GetProperty("Video").GetString());
        Assert.Equal("Aac", firstMedia.GetProperty("Audio").GetString());
        Assert.Equal("WebVtt", firstMedia.GetProperty("Subtitle").GetString());
        Assert.Equal("Unknown", records[1].GetProperty("Event").GetString());
        Assert.Equal("Unknown", records[1].GetProperty("Error").GetString());
        Assert.All(records[1].GetProperty("Media").EnumerateObject(), property => Assert.Equal("Unknown", property.Value.GetString()));
    }

    [Fact]
    public void Rotation_and_snapshot_retention_bound_actual_files_and_preserve_recent_records_after_restart()
    {
        using var fixture = new TemporaryAccountStore();
        var diagnostics = CreateDiagnostics(fixture);
        var id = diagnostics.CreateLocalPlaybackId();
        for (var index = 0; index < 800; index++)
            Assert.True(diagnostics.Record(id, PlaybackDiagnosticEvent.ReportFailed, PlaybackDiagnosticError.Network));

        var files = Directory.GetFiles(diagnostics.DirectoryPath, "*.jsonl").Select(path => new FileInfo(path)).ToArray();
        Assert.Equal(PlaybackDiagnostics.RetainedFileCount, files.Length);
        Assert.All(files, file => Assert.InRange(file.Length, 1, PlaybackDiagnostics.MaximumFileBytes));
        Assert.InRange(Directory.GetFiles(diagnostics.DirectoryPath).Sum(path => new FileInfo(path).Length),
            1, PlaybackDiagnostics.RetainedFileCount * PlaybackDiagnostics.MaximumFileBytes);
        var bytes = diagnostics.CreateSnapshot();
        Assert.InRange(bytes.Length, 1, PlaybackDiagnostics.MaximumSnapshotBytes);
        using (var snapshot = JsonDocument.Parse(bytes))
        {
            var records = snapshot.RootElement.GetProperty("Events");
            Assert.Equal(PlaybackDiagnostics.MaximumSnapshotEvents, records.GetArrayLength());
            Assert.Equal(800, records[records.GetArrayLength() - 1].GetProperty("Sequence").GetInt64());
            Assert.True(records[0].GetProperty("Sequence").GetInt64() > 1);
        }
        var restored = CreateDiagnostics(fixture);
        using var restoredSnapshot = JsonDocument.Parse(restored.CreateSnapshot());
        var restoredRecords = restoredSnapshot.RootElement.GetProperty("Events");
        Assert.InRange(restoredRecords.GetArrayLength(), 1, PlaybackDiagnostics.MaximumSnapshotEvents);
        Assert.Equal(800, restoredRecords[restoredRecords.GetArrayLength() - 1].GetProperty("Sequence").GetInt64());
        Assert.Equal(PlaybackDiagnosticStorageIssue.None, restored.LastIssue);
    }

    [Fact]
    public void Corrupt_rows_and_unsafe_extra_fields_cannot_enter_a_reconstructed_snapshot()
    {
        using var fixture = new TemporaryAccountStore();
        var diagnostics = CreateDiagnostics(fixture);
        Assert.True(diagnostics.Record(diagnostics.CreateLocalPlaybackId(), PlaybackDiagnosticEvent.Started));
        var path = Assert.Single(Directory.GetFiles(diagnostics.DirectoryPath, "*.jsonl"));
        var original = File.ReadAllText(path);
        const string secret = "synthetic-private-token-and-title";
        using (var originalDocument = JsonDocument.Parse(original))
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in originalDocument.RootElement.EnumerateObject()) property.WriteTo(writer);
                writer.WriteString("ServerUrl", "https://private.synthetic/?api_key=" + secret);
                writer.WriteString("ExceptionMessage", secret);
                writer.WriteString("Headers", secret);
                writer.WriteString("UserName", secret);
                writer.WriteString("Title", secret);
                writer.WriteString("Path", secret);
                writer.WriteString("ServerResponse", secret);
                writer.WriteEndObject();
            }
            File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()) + "\n{malformed " + secret);
        }

        var restored = CreateDiagnostics(fixture);
        var snapshot = restored.CreateSnapshot();

        Assert.Equal(PlaybackDiagnosticStorageIssue.CorruptLog, restored.LastIssue);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(snapshot));
        Assert.DoesNotContain("ServerUrl", Encoding.UTF8.GetString(snapshot));
        Assert.DoesNotContain("ExceptionMessage", Encoding.UTF8.GetString(snapshot));
        using var document = JsonDocument.Parse(snapshot);
        Assert.Equal(1, document.RootElement.GetProperty("Events").GetArrayLength());
        Assert.True(document.RootElement.GetProperty("DiscardedRecordCount").GetInt32() > 0);
        Assert.True(restored.Record(restored.CreateLocalPlaybackId(), PlaybackDiagnosticEvent.Paused));
        using var recovered = JsonDocument.Parse(CreateDiagnostics(fixture).CreateSnapshot());
        Assert.Equal(2, recovered.RootElement.GetProperty("Events").GetArrayLength());
        Assert.DoesNotContain(secret, recovered.RootElement.GetRawText());
    }

    [Fact]
    public void A_locked_log_does_not_break_recording_or_destroy_the_previous_file_and_snapshot_still_works()
    {
        using var fixture = new TemporaryAccountStore();
        var diagnostics = CreateDiagnostics(fixture);
        var id = diagnostics.CreateLocalPlaybackId();
        Assert.True(diagnostics.Record(id, PlaybackDiagnosticEvent.Started));
        var path = Assert.Single(Directory.GetFiles(diagnostics.DirectoryPath, "*.jsonl"));
        var before = File.ReadAllBytes(path);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(diagnostics.Record(id, PlaybackDiagnosticEvent.Failed, PlaybackDiagnosticError.NativePlayback));
            Assert.Equal(PlaybackDiagnosticStorageIssue.WriteFailed, diagnostics.LastIssue);
            using var snapshot = JsonDocument.Parse(diagnostics.CreateSnapshot());
            Assert.Equal(2, snapshot.RootElement.GetProperty("Events").GetArrayLength());
            Assert.Equal("WriteFailed", snapshot.RootElement.GetProperty("StorageIssue").GetString());
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(diagnostics.Record(id, PlaybackDiagnosticEvent.Stopped));
        Assert.Equal(PlaybackDiagnosticStorageIssue.None, diagnostics.LastIssue);
        Assert.Equal(2, File.ReadLines(path).Count());
    }

    [Fact]
    public void An_unavailable_log_directory_leaves_a_bounded_local_snapshot_without_exposing_IO_details()
    {
        using var fixture = new TemporaryAccountStore();
        var blocked = Path.Combine(fixture.DirectoryPath, "synthetic-private-directory");
        File.WriteAllText(blocked, "synthetic directory blocker");

        var diagnostics = new PlaybackDiagnostics(blocked, new Version(7, 8, 9, 10), new Version(3, 4, 5, 6));
        Assert.Equal(PlaybackDiagnosticStorageIssue.ReadFailed, diagnostics.LastIssue);
        Assert.False(diagnostics.Record(diagnostics.CreateLocalPlaybackId(), PlaybackDiagnosticEvent.Failed, PlaybackDiagnosticError.Persistence));

        Assert.Equal(PlaybackDiagnosticStorageIssue.WriteFailed, diagnostics.LastIssue);
        var bytes = diagnostics.CreateSnapshot();
        Assert.InRange(bytes.Length, 1, PlaybackDiagnostics.MaximumSnapshotBytes);
        Assert.DoesNotContain(blocked, Encoding.UTF8.GetString(bytes));
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(1, document.RootElement.GetProperty("Events").GetArrayLength());
    }

    private static PlaybackDiagnostics CreateDiagnostics(TemporaryAccountStore fixture) => new(
        Path.Combine(fixture.DirectoryPath, "diagnostics"), new Version(7, 8, 9, 10), new Version(3, 4, 5, 6));
}
