using CommunityToolkit.Mvvm.ComponentModel;
using EmbyClient.Api;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;
using System.Globalization;

namespace EmbyClient.App.ViewModels;

public sealed partial class MediaInformationViewModel : ObservableObject
{
    private MediaSourceViewModel? _selectedSource;

    public MediaInformationViewModel(BaseItemDto item) => ApplyItem(item);

    public ObservableCollection<MediaSourceViewModel> Sources { get; } = [];
    public MediaSourceViewModel? SelectedSource
    {
        get => _selectedSource;
        set => SetProperty(ref _selectedSource, value);
    }

    public Visibility Visibility => Sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SourceSelectorVisibility => Sources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public void ApplyItem(BaseItemDto item)
    {
        var selectedKey = SelectedSource?.Key;
        var previous = Sources.GroupBy(source => source.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sources = (item.MediaSources ?? []).Select((source, index) => new MediaSourceViewModel(source, index))
            .Where(source => source.HasInformation).ToList();
        // Item-level streams describe the primary source and never replace its supplied streams.
        var primarySource = item.MediaSources?.FirstOrDefault() ?? new MediaSourceInfo();
        if (!new MediaSourceViewModel(primarySource, 0).HasStreams && item.MediaStreams is { Length: > 0 } streams)
        {
            var fallback = new MediaSourceViewModel(primarySource with { MediaStreams = streams }, 0);
            if (fallback.HasInformation)
            {
                if (sources.Count > 0 && sources[0].Key == fallback.Key) sources[0] = fallback;
                else sources.Insert(0, fallback);
            }
        }
        Sources.Clear();
        foreach (var source in sources)
        {
            if (previous.TryGetValue(source.Key, out var old)) source.RestoreDisclosureState(old);
            Sources.Add(source);
        }
        SelectedSource = Sources.FirstOrDefault(source => source.Key == selectedKey)
            ?? Sources.FirstOrDefault(source => source.HasStreams) ?? Sources.FirstOrDefault();
        OnPropertyChanged(nameof(Visibility));
        OnPropertyChanged(nameof(SourceSelectorVisibility));
    }

    public void RestoreDisclosureState(MediaInformationViewModel previous)
    {
        foreach (var source in Sources)
            if (previous.Sources.FirstOrDefault(candidate => candidate.Key == source.Key) is { } old) source.RestoreDisclosureState(old);
        SelectedSource = Sources.FirstOrDefault(source => source.Key == previous.SelectedSource?.Key) ?? SelectedSource;
    }
}

public sealed partial class MediaSourceViewModel
{
    public MediaSourceViewModel(MediaSourceInfo source, int sourceIndex)
    {
        Key = !string.IsNullOrWhiteSpace(source.Id) ? source.Id : $"source-{sourceIndex}";
        var fileName = GetFileName(source.Path);
        Name = FirstValue(source.Name, fileName) ?? $"Media source {sourceIndex + 1}";
        var fields = new List<string>();
        if (!string.IsNullOrWhiteSpace(fileName) && !string.Equals(Name, fileName, StringComparison.Ordinal)) fields.Add(fileName);
        if (!string.IsNullOrWhiteSpace(source.Container)) fields.Add(source.Container.ToUpperInvariant());
        if (source.Size is > 0) fields.Add(FormatSize(source.Size.Value));
        if (source.Bitrate is > 0) fields.Add(FormatBitrate(source.Bitrate.Value));
        Summary = string.Join(" · ", fields);
        Video = CreateGroup(source, "Video", "Video", "\uE714");
        Audio = CreateGroup(source, "Audio", "Audio", "\uE767");
        Subtitles = CreateGroup(source, "Subtitle", "Subtitles", "\uE8F2");
        HasInformation = HasStreams || !string.IsNullOrWhiteSpace(fileName)
            || !string.IsNullOrWhiteSpace(source.Container) || source.Size is > 0 || source.Bitrate is > 0;
    }

    public string Key { get; }
    public string Name { get; }
    public string Summary { get; }
    public bool HasInformation { get; }
    public bool HasStreams => Video.Tracks.Count + Audio.Tracks.Count + Subtitles.Tracks.Count > 0;
    public MediaStreamGroupViewModel Video { get; }
    public MediaStreamGroupViewModel Audio { get; }
    public MediaStreamGroupViewModel Subtitles { get; }
    public Visibility SummaryVisibility => Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void RestoreDisclosureState(MediaSourceViewModel previous)
    {
        Video.RestoreDisclosureState(previous.Video);
        Audio.RestoreDisclosureState(previous.Audio);
        Subtitles.RestoreDisclosureState(previous.Subtitles);
    }

    private static MediaStreamGroupViewModel CreateGroup(MediaSourceInfo source, string type, string title, string icon) =>
        new(type, title, icon, (source.MediaStreams ?? []).Where(stream => string.Equals(stream.Type, type, StringComparison.OrdinalIgnoreCase))
            .Where(MediaStreamTrackViewModel.HasUsefulInformation).Select(stream => new MediaStreamTrackViewModel(stream,
                type == "Audio" && source.DefaultAudioStreamIndex is { } audioIndex ? audioIndex == stream.Index
                : type == "Subtitle" && source.DefaultSubtitleStreamIndex is { } subtitleIndex ? subtitleIndex == stream.Index
                : stream.IsDefault == true)));

    private static string? GetFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Remote paths can contain credentials or access tokens; they are never display metadata.
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) return null;
        var name = path.Replace('\\', '/').Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    internal static string? FirstValue(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    internal static string FormatBitrate(long bitrate) => bitrate >= 1_000_000
        ? $"{(bitrate / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture)} Mbps"
        : $"{(bitrate / 1_000d).ToString("0.##", CultureInfo.InvariantCulture)} kbps";
    private static string FormatSize(long bytes) => bytes >= 1_000_000_000
        ? $"{(bytes / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture)} GB"
        : $"{(bytes / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture)} MB";
}

public sealed partial class MediaStreamGroupViewModel : ObservableObject
{
    private bool _isExpanded;

    public MediaStreamGroupViewModel(string type, string title, string icon, IEnumerable<MediaStreamTrackViewModel> tracks)
    {
        Type = type;
        Title = title;
        Icon = icon;
        Tracks = tracks.ToArray();
        for (var index = 0; index < Tracks.Count; index++) Tracks[index].HasDivider = index > 0;
        UpdateVisibleTracks();
    }

    public string Type { get; }
    public string Title { get; }
    public string Icon { get; }
    public IReadOnlyList<MediaStreamTrackViewModel> Tracks { get; }
    public ObservableCollection<MediaStreamTrackViewModel> VisibleTracks { get; } = [];
    public string CountLabel => Tracks.Count > 1 ? $"{Tracks.Count} tracks" : string.Empty;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            UpdateVisibleTracks();
            OnPropertyChanged(nameof(ToggleLabel));
        }
    }
    public string ToggleLabel => IsExpanded ? $"Show fewer {Title.ToLowerInvariant()} tracks" : $"Show all {Tracks.Count} {Title.ToLowerInvariant()} tracks";
    public Visibility Visibility => Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToggleVisibility => Tracks.Count > 2 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateVisibleTracks()
    {
        var count = IsExpanded ? Tracks.Count : Math.Min(2, Tracks.Count);
        while (VisibleTracks.Count > count) VisibleTracks.RemoveAt(VisibleTracks.Count - 1);
        while (VisibleTracks.Count < count) VisibleTracks.Add(Tracks[VisibleTracks.Count]);
    }

    public void RestoreDisclosureState(MediaStreamGroupViewModel previous)
    {
        IsExpanded = previous.IsExpanded;
        foreach (var track in Tracks)
            track.IsExpanded = previous.Tracks.FirstOrDefault(candidate => candidate.Key == track.Key)?.IsExpanded == true;
    }
}

public sealed partial class MediaStreamTrackViewModel : ObservableObject
{
    private bool _isExpanded;

    public MediaStreamTrackViewModel(MediaStream stream, bool isDefault)
    {
        Key = $"{stream.Type}:{stream.Index}";
        var language = MediaSourceViewModel.FirstValue(stream.DisplayLanguage, stream.Language);
        var codec = stream.Codec?.ToUpperInvariant();
        Title = MediaSourceViewModel.FirstValue(stream.DisplayTitle, stream.Title,
            string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase) ? codec : language,
            codec) ?? $"{stream.Type} track {stream.Index + 1}";
        var tags = new List<string>();
        if (isDefault) tags.Add("Default");
        if (stream.IsForced == true) tags.Add("Forced");
        if (stream.IsHearingImpaired == true) tags.Add(string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase) ? "SDH" : "Hearing impaired");
        if (!string.IsNullOrWhiteSpace(stream.ExtendedVideoSubTypeDescription)) tags.Add(stream.ExtendedVideoSubTypeDescription);
        else if (!string.IsNullOrWhiteSpace(stream.VideoRange)) tags.Add(stream.VideoRange);
        Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var summary = new List<string>();
        if (!string.IsNullOrWhiteSpace(stream.Title) && stream.Title != Title) summary.Add(stream.Title);
        if (!string.IsNullOrWhiteSpace(language) && language != Title) summary.Add(language);
        if (string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase) && stream.IsExternal is { } external)
            summary.Add(external ? "External" : "Embedded");
        Summary = string.Join(" · ", summary.Distinct(StringComparer.OrdinalIgnoreCase));

        var fields = new List<MediaInformationField>();
        Add(fields, "Codec", codec);
        if (string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase))
        {
            Add(fields, "Resolution", stream.Width is > 0 && stream.Height is > 0 ? $"{stream.Width} × {stream.Height}"
                : stream.Height is > 0 ? $"{stream.Height}p" : stream.Width is > 0 ? $"{stream.Width} px wide" : null);
            Add(fields, "Aspect ratio", stream.AspectRatio);
            var frameRate = stream.AverageFrameRate is > 0 ? stream.AverageFrameRate : stream.RealFrameRate;
            Add(fields, "Frame rate", frameRate is > 0 ? $"{frameRate.Value.ToString("0.###", CultureInfo.InvariantCulture)} fps" : null);
        }
        else if (string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase))
        {
            Add(fields, "Channels", MediaSourceViewModel.FirstValue(stream.ChannelLayout, stream.Channels is > 0 ? $"{stream.Channels} channels" : null));
            Add(fields, "Sample rate", stream.SampleRate is > 0 ? $"{(stream.SampleRate.Value / 1_000d).ToString("0.###", CultureInfo.InvariantCulture)} kHz" : null);
        }
        Fields = fields;

        var technical = new List<MediaInformationField>();
        Add(technical, "Bitrate", stream.BitRate is > 0 ? MediaSourceViewModel.FormatBitrate(stream.BitRate.Value) : null);
        Add(technical, "Profile", stream.Profile);
        Add(technical, "Level", stream.Level is > 0 ? stream.Level.Value.ToString("0.##", CultureInfo.InvariantCulture) : null);
        Add(technical, "Bit depth", stream.BitDepth is > 0 ? $"{stream.BitDepth} bit" : null);
        Add(technical, "Pixel format", stream.PixelFormat);
        Add(technical, "Color space", stream.ColorSpace);
        Add(technical, "Color transfer", stream.ColorTransfer);
        Add(technical, "Color primaries", stream.ColorPrimaries);
        Add(technical, "Scan type", stream.IsInterlaced is { } interlaced ? interlaced ? "Interlaced" : "Progressive" : null);
        Add(technical, "Codec tag", stream.CodecTag);
        Add(technical, "Reference frames", stream.RefFrames is > 0 ? stream.RefFrames.Value.ToString(CultureInfo.InvariantCulture) : null);
        Add(technical, "Delivery method", stream.DeliveryMethod);
        TechnicalFields = technical;
    }

    public string Key { get; }
    public string Title { get; }
    public string Summary { get; }
    public IReadOnlyList<string> Tags { get; }
    public string TagsLabel => string.Join(" · ", Tags);
    public IReadOnlyList<MediaInformationField> Fields { get; }
    public IReadOnlyList<MediaInformationField> TechnicalFields { get; }
    public bool HasDivider { get; set; }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public string TechnicalAutomationName => $"Technical details for {string.Join(" ", new[] { Title }.Concat(Tags))}, {Key}";
    public Visibility DividerVisibility => HasDivider ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TagsVisibility => Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SummaryVisibility => Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TechnicalVisibility => TechnicalFields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    internal static bool HasUsefulInformation(MediaStream stream) =>
        new[] { stream.Codec, stream.DisplayTitle, stream.Title, stream.Language, stream.DisplayLanguage, stream.ChannelLayout,
            stream.Profile, stream.VideoRange, stream.PixelFormat, stream.ColorSpace, stream.ColorTransfer, stream.ColorPrimaries,
            stream.AspectRatio, stream.CodecTag, stream.ExtendedVideoSubTypeDescription, stream.DeliveryMethod }.Any(value => !string.IsNullOrWhiteSpace(value))
        || stream.Width is > 0 || stream.Height is > 0 || stream.Channels is > 0 || stream.BitRate is > 0
        || stream.SampleRate is > 0 || stream.AverageFrameRate is > 0 || stream.RealFrameRate is > 0
        || stream.BitDepth is > 0 || stream.RefFrames is > 0 || stream.Level is > 0 || stream.IsInterlaced.HasValue
        || string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase) && stream.IsExternal.HasValue;

    private static void Add(ICollection<MediaInformationField> fields, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) fields.Add(new(label, value));
    }
}

public sealed partial record MediaInformationField(string Label, string Value);
