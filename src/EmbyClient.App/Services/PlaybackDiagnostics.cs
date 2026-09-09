using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;

namespace EmbyClient.App.Services;

public enum PlaybackDiagnosticEvent
{
    Unknown, Created, Negotiating, Opening, Started, Paused, Resumed, Seeking, Seeked,
    Stopped, Ended, Failed, ReportFailed, ResourceReleased
}

public enum PlaybackDiagnosticError
{
    Unknown, None, Authentication, Permission, Network, Timeout, Protocol,
    UnsupportedMedia, NativePlayback, ResourceCleanup, Persistence
}

public enum PlaybackDiagnosticDelivery { Unknown, DirectPlay, DirectStream, Remux, Transcode }
public enum PlaybackDiagnosticContainer { Unknown, Mp4, Matroska, MpegTs, Hls, WebM, Avi, Mov }
public enum PlaybackDiagnosticVideo { Unknown, None, H264, Hevc, Av1, Vp8, Vp9, Mpeg2 }
public enum PlaybackDiagnosticAudio { Unknown, None, Aac, Mp3, Ac3, Eac3, Dts, TrueHd, Flac, Opus, Pcm }
public enum PlaybackDiagnosticSubtitle { Unknown, None, Srt, WebVtt, Ass, Ssa, Pgs, VobSub }
public enum PlaybackDiagnosticStorageIssue { None, InvalidRecord, CorruptLog, ReadFailed, WriteFailed }

public sealed record PlaybackDiagnosticMedia(
    PlaybackDiagnosticDelivery Delivery = PlaybackDiagnosticDelivery.Unknown,
    PlaybackDiagnosticContainer Container = PlaybackDiagnosticContainer.Unknown,
    PlaybackDiagnosticVideo Video = PlaybackDiagnosticVideo.Unknown,
    PlaybackDiagnosticAudio Audio = PlaybackDiagnosticAudio.Unknown,
    PlaybackDiagnosticSubtitle Subtitle = PlaybackDiagnosticSubtitle.Unknown)
{
    /// <summary>Maps complete category names only; unrecognized strings are discarded, never retained.</summary>
    public static PlaybackDiagnosticMedia FromCategories(string? delivery, string? container,
        string? video, string? audio, string? subtitle) => new(
        Map(delivery, ("directplay", PlaybackDiagnosticDelivery.DirectPlay), ("directstream", PlaybackDiagnosticDelivery.DirectStream),
            ("remux", PlaybackDiagnosticDelivery.Remux), ("transcode", PlaybackDiagnosticDelivery.Transcode)),
        Map(container, ("mp4", PlaybackDiagnosticContainer.Mp4), ("mkv", PlaybackDiagnosticContainer.Matroska),
            ("matroska", PlaybackDiagnosticContainer.Matroska), ("ts", PlaybackDiagnosticContainer.MpegTs),
            ("mpegts", PlaybackDiagnosticContainer.MpegTs), ("hls", PlaybackDiagnosticContainer.Hls),
            ("webm", PlaybackDiagnosticContainer.WebM), ("avi", PlaybackDiagnosticContainer.Avi), ("mov", PlaybackDiagnosticContainer.Mov)),
        Map(video, ("none", PlaybackDiagnosticVideo.None), ("h264", PlaybackDiagnosticVideo.H264),
            ("hevc", PlaybackDiagnosticVideo.Hevc), ("h265", PlaybackDiagnosticVideo.Hevc), ("av1", PlaybackDiagnosticVideo.Av1),
            ("vp8", PlaybackDiagnosticVideo.Vp8), ("vp9", PlaybackDiagnosticVideo.Vp9), ("mpeg2video", PlaybackDiagnosticVideo.Mpeg2)),
        Map(audio, ("none", PlaybackDiagnosticAudio.None), ("aac", PlaybackDiagnosticAudio.Aac),
            ("mp3", PlaybackDiagnosticAudio.Mp3), ("ac3", PlaybackDiagnosticAudio.Ac3), ("eac3", PlaybackDiagnosticAudio.Eac3),
            ("dts", PlaybackDiagnosticAudio.Dts), ("truehd", PlaybackDiagnosticAudio.TrueHd), ("flac", PlaybackDiagnosticAudio.Flac),
            ("opus", PlaybackDiagnosticAudio.Opus), ("pcm", PlaybackDiagnosticAudio.Pcm)),
        Map(subtitle, ("none", PlaybackDiagnosticSubtitle.None), ("srt", PlaybackDiagnosticSubtitle.Srt),
            ("subrip", PlaybackDiagnosticSubtitle.Srt), ("vtt", PlaybackDiagnosticSubtitle.WebVtt),
            ("webvtt", PlaybackDiagnosticSubtitle.WebVtt), ("ass", PlaybackDiagnosticSubtitle.Ass),
            ("ssa", PlaybackDiagnosticSubtitle.Ssa), ("pgs", PlaybackDiagnosticSubtitle.Pgs),
            ("hdmv_pgs_subtitle", PlaybackDiagnosticSubtitle.Pgs), ("vobsub", PlaybackDiagnosticSubtitle.VobSub),
            ("dvd_subtitle", PlaybackDiagnosticSubtitle.VobSub)));

    internal PlaybackDiagnosticMedia Normalize() => new(Defined(Delivery), Defined(Container), Defined(Video), Defined(Audio), Defined(Subtitle));

    private static T Map<T>(string? value, params (string Name, T Category)[] categories) where T : struct, Enum
    {
        foreach (var category in categories)
            if (string.Equals(value, category.Name, StringComparison.OrdinalIgnoreCase)) return category.Category;
        return default;
    }

    internal static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : default;
}

/// <summary>Stores local-only, bounded playback diagnostics without accepting server or exception text.</summary>
/// <remarks>
/// Record accepts only IDs issued by CreateLocalPlaybackId. Keep this ID separate from every Emby identifier.
/// The owned files are playback-0.jsonl through playback-2.jsonl and a zero-byte writer lock.
/// CreateSnapshot reconstructs permitted fields; it never copies raw log lines into an export.
/// No network operation, automatic upload, or extra snapshot file is created by this service.
/// </remarks>
public sealed class PlaybackDiagnostics
{
    public const int MaximumFileBytes = 32 * 1024;
    public const int RetainedFileCount = 3;
    public const int MaximumSnapshotEvents = 128;
    public const int MaximumSnapshotBytes = 128 * 1024;
    private const int MaximumRecordCharacters = 2048;
    private readonly object _gate = new();
    private readonly Queue<DiagnosticRecord> _records = new();
    private readonly Queue<Guid> _localIds = new();
    private readonly HashSet<Guid> _knownIds = [];
    private readonly VersionData _versions;
    private long _sequence;
    private int _discardedRecords;
    private PlaybackDiagnosticStorageIssue _lastIssue;

    public PlaybackDiagnostics(string? directory = null, Version? applicationVersion = null, Version? engineVersion = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbyClient.Windows", "diagnostics"));
        _versions = new VersionData(
            applicationVersion ?? typeof(PlaybackDiagnostics).Assembly.GetName().Version ?? new Version(0, 0),
            Environment.Version, Environment.OSVersion.Version, engineVersion ?? Environment.OSVersion.Version,
            engineVersion is null ? EngineVersionKind.WindowsOperatingSystemBaseline : EngineVersionKind.ExplicitVersion,
            OperatingSystem.IsWindows() ? OperatingSystemKind.Windows : OperatingSystemKind.Other);
        LoadExisting();
    }

    public string DirectoryPath { get; }
    public PlaybackDiagnosticStorageIssue LastIssue { get { lock (_gate) return _lastIssue; } }

    public Guid CreateLocalPlaybackId()
    {
        lock (_gate)
        {
            var id = Guid.NewGuid();
            while (_localIds.Count >= MaximumSnapshotEvents) _knownIds.Remove(_localIds.Dequeue());
            _localIds.Enqueue(id);
            _knownIds.Add(id);
            return id;
        }
    }

    /// <summary>Returns whether the record reached disk; the bounded in-memory snapshot survives a write failure.</summary>
    public bool Record(Guid localPlaybackId, PlaybackDiagnosticEvent eventName,
        PlaybackDiagnosticError error = PlaybackDiagnosticError.None, PlaybackDiagnosticMedia? media = null)
    {
        lock (_gate)
        {
            if (!_knownIds.Contains(localPlaybackId))
            {
                _lastIssue = PlaybackDiagnosticStorageIssue.InvalidRecord;
                return false;
            }
            _sequence = _sequence == long.MaxValue ? 1 : _sequence + 1;
            var record = new DiagnosticRecord(_sequence, DateTimeOffset.UtcNow, localPlaybackId,
                PlaybackDiagnosticMedia.Defined(eventName), PlaybackDiagnosticMedia.Defined(error),
                (media ?? new PlaybackDiagnosticMedia()).Normalize(), _versions);
            Remember(record);
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                using var writerLock = AcquireWriterLock();
                PruneOversizedFiles();
                var bytes = SerializeRecord(record);
                if (bytes.Length + 2 > MaximumFileBytes)
                {
                    _lastIssue = PlaybackDiagnosticStorageIssue.InvalidRecord;
                    return false;
                }
                var currentPath = LogPath(0);
                if (File.Exists(currentPath) && new FileInfo(currentPath).Length + bytes.Length + 2 > MaximumFileBytes)
                    RotateFiles();
                using var output = new FileStream(currentPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (output.Length > 0)
                {
                    output.Position = output.Length - 1;
                    if (output.ReadByte() != '\n')
                    {
                        output.Position = output.Length;
                        output.WriteByte((byte)'\n');
                    }
                }
                output.Position = output.Length;
                output.Write(bytes);
                output.WriteByte((byte)'\n');
                output.Flush();
                _lastIssue = PlaybackDiagnosticStorageIssue.None;
                return true;
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                _lastIssue = PlaybackDiagnosticStorageIssue.WriteFailed;
                return false;
            }
        }
    }

    public byte[] CreateSnapshot()
    {
        lock (_gate)
        {
            var records = _records.ToArray();
            for (var skip = 0; ; skip++)
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("SchemaVersion", 1);
                    writer.WriteBoolean("LocalOnly", true);
                    writer.WriteString("StorageIssue", _lastIssue.ToString());
                    writer.WriteNumber("DiscardedRecordCount", _discardedRecords);
                    WriteVersions(writer, _versions);
                    writer.WriteStartArray("Events");
                    foreach (var record in records.Skip(skip)) WriteRecord(writer, record);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                if (stream.Length <= MaximumSnapshotBytes) return stream.ToArray();
            }
        }
    }

    private void LoadExisting()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            using var writerLock = AcquireWriterLock();
            PruneOversizedFiles();
            for (var index = RetainedFileCount - 1; index >= 0; index--)
            {
                var path = LogPath(index);
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (TryReadRecord(line, out var record))
                    {
                        Remember(record!);
                        _sequence = Math.Max(_sequence, record!.Sequence);
                    }
                    else
                    {
                        DiscardRecord();
                    }
                }
            }
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            _lastIssue = PlaybackDiagnosticStorageIssue.ReadFailed;
        }
    }

    private void Remember(DiagnosticRecord record)
    {
        while (_records.Count >= MaximumSnapshotEvents) _records.Dequeue();
        _records.Enqueue(record);
    }

    private FileStream AcquireWriterLock()
    {
        var stream = new FileStream(Path.Combine(DirectoryPath, "writer.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            stream.SetLength(0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private string LogPath(int index) => Path.Combine(DirectoryPath, $"playback-{index}.jsonl");

    private void PruneOversizedFiles()
    {
        for (var index = 0; index < RetainedFileCount; index++)
        {
            var path = LogPath(index);
            if (File.Exists(path) && new FileInfo(path).Length > MaximumFileBytes)
            {
                File.Delete(path);
                DiscardRecord();
            }
        }
    }

    private void RotateFiles()
    {
        File.Delete(LogPath(RetainedFileCount - 1));
        for (var index = RetainedFileCount - 2; index >= 0; index--)
            if (File.Exists(LogPath(index))) File.Move(LogPath(index), LogPath(index + 1), overwrite: true);
    }

    private void DiscardRecord()
    {
        if (_discardedRecords < int.MaxValue) _discardedRecords++;
        _lastIssue = PlaybackDiagnosticStorageIssue.CorruptLog;
    }

    private static byte[] SerializeRecord(DiagnosticRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteRecord(writer, record);
        return stream.ToArray();
    }

    private static void WriteRecord(Utf8JsonWriter writer, DiagnosticRecord record)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Sequence", record.Sequence);
        writer.WriteString("TimestampUtc", record.TimestampUtc);
        writer.WriteString("LocalPlaybackId", record.LocalPlaybackId);
        writer.WriteString("Event", record.Event.ToString());
        writer.WriteString("Error", record.Error.ToString());
        WriteVersions(writer, record.Versions);
        writer.WriteStartObject("Media");
        writer.WriteString("Delivery", record.Media.Delivery.ToString());
        writer.WriteString("Container", record.Media.Container.ToString());
        writer.WriteString("Video", record.Media.Video.ToString());
        writer.WriteString("Audio", record.Media.Audio.ToString());
        writer.WriteString("Subtitle", record.Media.Subtitle.ToString());
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteVersions(Utf8JsonWriter writer, VersionData versions)
    {
        writer.WriteStartObject("Versions");
        writer.WriteString("Application", versions.Application.ToString());
        writer.WriteString("Runtime", versions.Runtime.ToString());
        writer.WriteString("OperatingSystem", versions.OperatingSystem.ToString());
        writer.WriteString("OperatingSystemKind", versions.OperatingSystemKind.ToString());
        writer.WriteString("Engine", "WindowsMediaPlayer");
        writer.WriteString("EngineVersion", versions.Engine.ToString());
        writer.WriteString("EngineVersionKind", versions.EngineVersionKind.ToString());
        writer.WriteEndObject();
    }

    private static bool TryReadRecord(string line, out DiagnosticRecord? record)
    {
        record = null;
        if (line.Length == 0 || line.Length > MaximumRecordCharacters) return false;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Sequence", out var sequenceElement) || !sequenceElement.TryGetInt64(out var sequence) || sequence <= 0
                || !ReadString(root, "TimestampUtc", out var timestampValue)
                || !DateTimeOffset.TryParse(timestampValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
                || !ReadString(root, "LocalPlaybackId", out var idValue) || !Guid.TryParseExact(idValue, "D", out var id) || id == Guid.Empty
                || !ReadEnum<PlaybackDiagnosticEvent>(root, "Event", out var eventName)
                || !ReadEnum<PlaybackDiagnosticError>(root, "Error", out var error)
                || !root.TryGetProperty("Versions", out var versions) || !TryReadVersions(versions, out var versionData)
                || !root.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Object
                || !ReadEnum<PlaybackDiagnosticDelivery>(media, "Delivery", out var delivery)
                || !ReadEnum<PlaybackDiagnosticContainer>(media, "Container", out var container)
                || !ReadEnum<PlaybackDiagnosticVideo>(media, "Video", out var video)
                || !ReadEnum<PlaybackDiagnosticAudio>(media, "Audio", out var audio)
                || !ReadEnum<PlaybackDiagnosticSubtitle>(media, "Subtitle", out var subtitle)) return false;
            record = new DiagnosticRecord(sequence, timestamp.ToUniversalTime(), id, eventName, error,
                new PlaybackDiagnosticMedia(delivery, container, video, audio, subtitle), versionData!);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool TryReadVersions(JsonElement element, out VersionData? versions)
    {
        versions = null;
        if (element.ValueKind != JsonValueKind.Object
            || !ReadString(element, "Application", out var appText) || !Version.TryParse(appText, out var app)
            || !ReadString(element, "Runtime", out var runtimeText) || !Version.TryParse(runtimeText, out var runtime)
            || !ReadString(element, "OperatingSystem", out var osText) || !Version.TryParse(osText, out var os)
            || !ReadString(element, "EngineVersion", out var engineText) || !Version.TryParse(engineText, out var engine)
            || !ReadString(element, "Engine", out var engineName) || engineName != "WindowsMediaPlayer"
            || !ReadEnum<EngineVersionKind>(element, "EngineVersionKind", out var kind)
            || !ReadEnum<OperatingSystemKind>(element, "OperatingSystemKind", out var osKind)) return false;
        versions = new VersionData(app, runtime, os, engine, kind, osKind);
        return true;
    }

    private static bool ReadString(JsonElement root, string property, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return value is { Length: > 0 and <= 64 };
    }

    private static bool ReadEnum<T>(JsonElement root, string property, out T value) where T : struct, Enum
    {
        value = default;
        return ReadString(root, property, out var text) && Enum.TryParse(text, ignoreCase: false, out value) && Enum.IsDefined(value);
    }

    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;

    private enum EngineVersionKind { WindowsOperatingSystemBaseline, ExplicitVersion }
    private enum OperatingSystemKind { Windows, Other }
    private sealed record VersionData(Version Application, Version Runtime, Version OperatingSystem, Version Engine,
        EngineVersionKind EngineVersionKind, OperatingSystemKind OperatingSystemKind);
    private sealed record DiagnosticRecord(long Sequence, DateTimeOffset TimestampUtc, Guid LocalPlaybackId,
        PlaybackDiagnosticEvent Event, PlaybackDiagnosticError Error, PlaybackDiagnosticMedia Media, VersionData Versions);
}
