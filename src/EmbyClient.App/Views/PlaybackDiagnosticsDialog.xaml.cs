using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace EmbyClient.App.Views;

public sealed partial class PlaybackDiagnosticsDialog : ContentDialog
{
    private readonly PlaybackDiagnostics _diagnostics;
    private readonly Func<VideoDecodingSnapshot?> _videoDecoding;
    private byte[] _snapshot = [];
    private bool _saving;
    private XamlRoot? _dialogRoot;

    public ObservableCollection<PlaybackDiagnosticEventRow> RecentEvents { get; } = [];

    internal PlaybackDiagnosticsDialog(PlaybackDiagnostics diagnostics, Func<VideoDecodingSnapshot?> videoDecoding)
    {
        _diagnostics = diagnostics;
        _videoDecoding = videoDecoding;
        RegisterVideoDecodingText();
        InitializeComponent();
        VideoDecodingHeadingText.Text = LumenText.Get("Video decoding");
        Opened += (_, _) =>
        {
            _dialogRoot = XamlRoot;
            if (_dialogRoot is null) return;
            _dialogRoot.Changed += DialogRootChanged;
            UpdateDialogSize();
        };
        Closed += (_, _) =>
        {
            if (_dialogRoot is not null) _dialogRoot.Changed -= DialogRootChanged;
            _dialogRoot = null;
        };
        RefreshSnapshot();
    }

    private void DialogRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateDialogSize();
    private void FeedbackSizeChanged(object sender, SizeChangedEventArgs args) => UpdateDialogSize();

    private void UpdateDialogSize()
    {
        if (_dialogRoot is not { } root) return;
        DialogContent.Width = Math.Clamp(root.Size.Width - 112, 180, 480);
        // The native dialog buttons remain outside this scrollable content region.
        var feedbackHeight = (ResultNotice.IsOpen ? ResultNotice.ActualHeight + 12 : 0)
            + (StatusText.Visibility == Visibility.Visible ? StatusText.ActualHeight + 12 : 0);
        ContentScroller.MaxHeight = Math.Clamp(root.Size.Height - 260 - feedbackHeight, 96, 520);
    }

    private void RefreshSnapshot()
    {
        StatusText.Text = string.Empty;
        StatusText.Visibility = Visibility.Collapsed;
        RefreshVideoDecoding();
        try
        {
            _snapshot = _diagnostics.CreateSnapshot();
            using var document = JsonDocument.Parse(_snapshot);
            var root = document.RootElement;
            var versions = root.GetProperty("Versions");
            ApplicationText.Text = versions.GetProperty("Application").GetString();
            OperatingSystemText.Text = versions.GetProperty("OperatingSystem").GetString();
            RuntimeText.Text = $".NET {versions.GetProperty("Runtime").GetString()}";
            var baseline = versions.GetProperty("EngineVersionKind").GetString() == "WindowsOperatingSystemBaseline";
            EngineVersionText.Text = baseline
                ? $"Player engine baseline: Windows {versions.GetProperty("EngineVersion").GetString()}. This is the operating system baseline, not a separately verified engine version."
                : $"Player engine version: {versions.GetProperty("EngineVersion").GetString()}";
            var events = root.GetProperty("Events");
            var count = events.GetArrayLength();
            UpdatePlaybackSummary(events);
            CountText.Text = count > 5 ? $"Latest 5 of {count} retained events" : $"{count} retained {(count == 1 ? "event" : "events")}";
            RecentEvents.Clear();
            foreach (var entry in events.EnumerateArray().Reverse().Take(5))
                RecentEvents.Add(CreateEventRow(entry));
            EmptyEventsText.Text = "No playback events yet. Events appear here when playback starts.";
            EmptyEventsText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RecentEventsList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            // Reformat only the service's allowlisted snapshot; never open or display raw log files.
            using var formatted = new MemoryStream();
            using (var writer = new Utf8JsonWriter(formatted, new JsonWriterOptions { Indented = true }))
            {
                root.WriteTo(writer);
            }
            SummaryText.Text = Encoding.UTF8.GetString(formatted.ToArray());
            ResultNotice.IsOpen = false;
            if (_diagnostics.LastIssue != PlaybackDiagnosticStorageIssue.None)
                ShowIssue("Some local diagnostic records could not be read or saved. The safe in-memory snapshot is still available.");
        }
        catch (JsonException)
        {
            _snapshot = [];
            PlaybackStatusText.Text = "Snapshot unavailable";
            PlaybackStatusDetailText.Text = "Refresh to try loading the playback status again.";
            RecentFailurePanel.Visibility = Visibility.Collapsed;
            ApplicationText.Text = "Unavailable";
            OperatingSystemText.Text = "Unavailable";
            RuntimeText.Text = "Unavailable";
            EngineVersionText.Text = "Engine information is unavailable.";
            RecentEvents.Clear();
            RecentEventsList.Visibility = Visibility.Collapsed;
            EmptyEventsText.Text = "The diagnostic snapshot is unavailable.";
            EmptyEventsText.Visibility = Visibility.Visible;
            SummaryText.Text = "The diagnostic snapshot is unavailable.";
            CountText.Text = "No snapshot available";
            ShowIssue("The diagnostic snapshot is unavailable. Try refreshing it.");
        }
        UpdateActions();
    }

    private void RefreshVideoDecoding()
    {
        var decoding = _videoDecoding();
        var decoder = decoding?.ActualApi switch
        {
            "D3D11" => "Actual decoder API: D3D11VA",
            "IntelQsv" => "Actual decoder API: Intel VPL / QSV",
            "AmdAmf" => "Actual decoder API: AMD AMF",
            "NvidiaNvdec" => "Actual decoder API: NVIDIA NVDEC",
            "Software" => "Actual decoder API: Software",
            _ => "Actual decoder API: Pending"
        };
        VideoDecodingStatusText.Text = LumenText.Get(decoder);
        var requested = decoding is null ? "Start playback, then refresh to see the decoder." : decoding.RequestedApi switch
        {
            "Auto" => "Requested decoder API: Automatic (D3D11VA)",
            "D3D11" => "Requested decoder API: D3D11VA",
            "IntelQsv" => "Requested decoder API: Intel VPL / QSV",
            "AmdAmf" => "Requested decoder API: AMD AMF",
            "NvidiaNvdec" => "Requested decoder API: NVIDIA NVDEC",
            "Software" => "Requested decoder API: Software",
            _ => "Requested decoder API: Unknown"
        };
        VideoDecodingRequestedText.Text = LumenText.Get(requested);
        var fallback = decoding is { ActualApi: "Software", HardwareFallback: true };
        var fallbackReason = decoding?.FallbackReason switch
        {
            "DecoderApiUnavailable" => "The requested decoder API is unavailable; using software.",
            "CodecUnsupported" => "The requested decoder does not support this stream; using software.",
            "DeviceUnavailable" => "The requested decoder device is unavailable; using software.",
            "DecoderInitializationFailed" => "The requested decoder could not be initialized; using software.",
            _ => "The requested decoder could not be used; using software."
        };
        VideoDecodingFallbackText.Text = fallback
            ? LumenText.Get(fallbackReason) : string.Empty;
        VideoDecodingFallbackText.Visibility = fallback ? Visibility.Visible : Visibility.Collapsed;
        var gpuName = decoding is { ActualApi: "D3D11" or "IntelQsv" or "AmdAmf" or "NvidiaNvdec", GpuName: { } name }
            ? string.Concat(name.Where(character => !char.IsControl(character))).Trim() : string.Empty;
        if (gpuName.Length > 120) gpuName = gpuName[..117] + "...";
        VideoDecodingGpuText.Text = gpuName.Length > 0 ? LumenText.Get("Decoder GPU: {0}", gpuName) : string.Empty;
        VideoDecodingGpuText.Visibility = gpuName.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void RegisterVideoDecodingText() => LumenText.Register(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Video decoding"] = "\u89c6\u9891\u89e3\u7801",
        ["Actual decoder API: D3D11VA"] = "\u5b9e\u9645\u89e3\u7801 API\uff1aD3D11VA",
        ["Actual decoder API: Intel VPL / QSV"] = "\u5b9e\u9645\u89e3\u7801 API\uff1aIntel VPL / QSV",
        ["Actual decoder API: AMD AMF"] = "\u5b9e\u9645\u89e3\u7801 API\uff1aAMD AMF",
        ["Actual decoder API: NVIDIA NVDEC"] = "\u5b9e\u9645\u89e3\u7801 API\uff1aNVIDIA NVDEC",
        ["Actual decoder API: Software"] = "\u5b9e\u9645\u89e3\u7801 API\uff1a\u8f6f\u4ef6\u89e3\u7801",
        ["Actual decoder API: Pending"] = "\u5b9e\u9645\u89e3\u7801 API\uff1a\u7b49\u5f85\u64ad\u653e\u786e\u8ba4",
        ["Requested decoder API: Automatic (D3D11VA)"] = "\u8bf7\u6c42\u89e3\u7801 API\uff1a\u81ea\u52a8\uff08D3D11VA\uff09",
        ["Requested decoder API: D3D11VA"] = "\u8bf7\u6c42\u89e3\u7801 API\uff1aD3D11VA",
        ["Requested decoder API: Intel VPL / QSV"] = "\u8bf7\u6c42\u89e3\u7801 API\uff1aIntel VPL / QSV",
        ["Requested decoder API: AMD AMF"] = "\u8bf7\u6c42\u89e3\u7801 API\uff1aAMD AMF",
        ["Requested decoder API: NVIDIA NVDEC"] = "\u8bf7\u6c42\u89e3\u7801 API\uff1aNVIDIA NVDEC",
        ["Requested decoder API: Software"] = "\u8bf7\u6c42\u89e3\u7801 API\uff1a\u8f6f\u4ef6\u89e3\u7801",
        ["Requested decoder API: Unknown"] = "\u8bf7\u6c42\u89e3\u7801 API\uff1a\u672a\u77e5",
        ["Start playback, then refresh to see the decoder."] = "\u5f00\u59cb\u64ad\u653e\u540e\u5237\u65b0\u4ee5\u67e5\u770b\u89e3\u7801\u5668\u3002",
        ["The requested decoder API is unavailable; using software."] = "\u8bf7\u6c42\u7684\u89e3\u7801 API \u4e0d\u53ef\u7528\uff0c\u5df2\u56de\u9000\u8f6f\u4ef6\u89e3\u7801\u3002",
        ["The requested decoder does not support this stream; using software."] = "\u8bf7\u6c42\u7684\u89e3\u7801\u5668\u4e0d\u652f\u6301\u5f53\u524d\u89c6\u9891\u6d41\uff0c\u5df2\u56de\u9000\u8f6f\u4ef6\u89e3\u7801\u3002",
        ["The requested decoder device is unavailable; using software."] = "\u8bf7\u6c42\u7684\u89e3\u7801\u8bbe\u5907\u4e0d\u53ef\u7528\uff0c\u5df2\u56de\u9000\u8f6f\u4ef6\u89e3\u7801\u3002",
        ["The requested decoder could not be initialized; using software."] = "\u8bf7\u6c42\u7684\u89e3\u7801\u5668\u521d\u59cb\u5316\u5931\u8d25\uff0c\u5df2\u56de\u9000\u8f6f\u4ef6\u89e3\u7801\u3002",
        ["The requested decoder could not be used; using software."] = "\u8bf7\u6c42\u7684\u89e3\u7801\u5668\u65e0\u6cd5\u4f7f\u7528\uff0c\u5df2\u56de\u9000\u8f6f\u4ef6\u89e3\u7801\u3002",
        ["Decoder GPU: {0}"] = "\u89e3\u7801 GPU\uff1a{0}"
    });

    private void UpdatePlaybackSummary(JsonElement events)
    {
        PlaybackStatusText.Text = "No playback status recorded";
        PlaybackStatusDetailText.Text = "Start playback, then refresh to see its recorded status.";
        RecentFailurePanel.Visibility = Visibility.Collapsed;
        var statusFound = false;
        foreach (var entry in events.EnumerateArray().Reverse())
        {
            var eventName = entry.GetProperty("Event").GetString();
            var error = entry.GetProperty("Error").GetString();
            if (!statusFound && eventName is not (null or "Unknown" or "ReportFailed" or "ResourceReleased"))
            {
                PlaybackStatusText.Text = EventTitle(eventName);
                PlaybackStatusDetailText.Text = $"{EventTimestamp(entry)} · {MediaSummary(entry.GetProperty("Media"))}";
                statusFound = true;
            }
            if (RecentFailurePanel.Visibility == Visibility.Collapsed
                && (eventName is "Failed" or "ReportFailed" || error is not (null or "None")))
            {
                RecentFailureText.Text = error is null or "None"
                    ? EventTitle(eventName) : $"{EventTitle(eventName)} · Error category: {error}";
                RecentFailureTimeText.Text = EventTimestamp(entry);
                RecentFailurePanel.Visibility = Visibility.Visible;
            }
            if (statusFound && RecentFailurePanel.Visibility == Visibility.Visible) break;
        }
    }

    private static PlaybackDiagnosticEventRow CreateEventRow(JsonElement entry)
    {
        var media = entry.GetProperty("Media");
        var error = entry.GetProperty("Error").GetString();
        return new PlaybackDiagnosticEventRow(
            EventTitle(entry.GetProperty("Event").GetString()),
            EventTimestamp(entry),
            MediaSummary(media),
            error is "None" ? string.Empty : $"Error category: {error}",
            error is "None" ? Visibility.Collapsed : Visibility.Visible);
    }

    private static string EventTimestamp(JsonElement entry) =>
        DateTimeOffset.TryParse(entry.GetProperty("TimestampUtc").GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value)
            ? value.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) : "Time unavailable";

    private static string MediaSummary(JsonElement media)
    {
        var parts = new List<string>();
        var delivery = media.GetProperty("Delivery").GetString();
        if (delivery is not (null or "Unknown")) parts.Add(DeliveryTitle(delivery));
        foreach (var category in new[] { "Container", "Video", "Audio" })
        {
            var value = media.GetProperty(category).GetString();
            if (value is not (null or "Unknown" or "None")) parts.Add(value);
        }
        return parts.Count == 0 ? "Source metadata not recorded" : string.Join(" · ", parts);
    }

    private static string EventTitle(string? eventName) => eventName switch
    {
        "Created" => "Playback session created",
        "Negotiating" => "Selecting playback method",
        "Opening" => "Opening media",
        "Started" => "Playback started",
        "Paused" => "Playback paused",
        "Resumed" => "Playback resumed",
        "Seeking" => "Seeking",
        "Seeked" => "Seek completed",
        "Stopped" => "Playback stopped",
        "Ended" => "Playback ended",
        "Failed" => "Playback failed",
        "ReportFailed" => "Playback progress report failed",
        "ResourceReleased" => "Playback resources released",
        _ => "Unclassified playback event"
    };

    private static string DeliveryTitle(string? delivery) => delivery switch
    {
        "DirectPlay" => "Direct play",
        "DirectStream" => "Direct stream",
        "Remux" => "Remux",
        "Transcode" => "Transcode",
        _ => "Delivery not recorded"
    };

    private void CopyClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_snapshot.Length == 0) return;
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(Encoding.UTF8.GetString(_snapshot));
            var copied = Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false
            });
            ShowStatus(copied ? "Safe snapshot copied. Clipboard history and roaming are disabled for this copy."
                : "The clipboard is unavailable. Try again.");
        }
        catch (Exception) { ShowStatus("The clipboard is unavailable. Try again."); }
    }

    private async void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_saving || _snapshot.Length == 0) return;
        var deferral = args.GetDeferral();
        _saving = true;
        UpdateActions();
        try
        {
            var root = XamlRoot ?? throw new InvalidOperationException("The dialog window is unavailable.");
            var picker = new FileSavePicker(root.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "emby-playback-diagnostics-"
                    + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture),
                DefaultFileExtension = ".json"
            };
            picker.FileTypeChoices.Add("JSON diagnostic snapshot", new List<string> { ".json" });
            var result = await picker.PickSaveFileAsync();
            if (result is null)
            {
                ShowStatus("Save canceled. No snapshot was exported.");
                return;
            }
            await File.WriteAllBytesAsync(result.Path, _snapshot);
            ShowStatus("Safe snapshot saved to the location you chose.");
        }
        catch (Exception) { ShowStatus("The snapshot could not be saved. Try another location or check available storage."); }
        finally
        {
            _saving = false;
            UpdateActions();
            deferral.Complete();
        }
    }

    private void RefreshClicked(object sender, RoutedEventArgs args)
    {
        RefreshSnapshot();
        if (_snapshot.Length > 0) ShowStatus($"Refreshed. {CountText.Text}.");
    }

    private void UpdateActions()
    {
        IsPrimaryButtonEnabled = !_saving && _snapshot.Length > 0;
        IsSecondaryButtonEnabled = !_saving && _snapshot.Length > 0;
        RefreshButton.IsEnabled = !_saving;
    }

    private void ShowIssue(string message)
    {
        ResultNotice.IsOpen = false;
        ResultNotice.Message = message;
        ResultNotice.Severity = InfoBarSeverity.Warning;
        ResultNotice.IsOpen = true;
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        (FrameworkElementAutomationPeer.FromElement(StatusText)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(StatusText))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}

public sealed partial class PlaybackDiagnosticEventRow
{
    internal PlaybackDiagnosticEventRow(string title, string timestamp, string media, string error, Visibility errorVisibility)
    {
        Title = title;
        Timestamp = timestamp;
        Media = media;
        Error = error;
        ErrorVisibility = errorVisibility;
    }

    public string Title { get; }
    public string Timestamp { get; }
    public string Media { get; }
    public string Error { get; }
    public Visibility ErrorVisibility { get; }
}
