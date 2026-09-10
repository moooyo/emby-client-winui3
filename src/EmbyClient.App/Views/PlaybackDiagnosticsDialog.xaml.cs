using System.Text;
using System.Text.Json;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace EmbyClient.App.Views;

public sealed partial class PlaybackDiagnosticsDialog : ContentDialog
{
    private readonly PlaybackDiagnostics _diagnostics;
    private byte[] _snapshot = [];
    private bool _saving;

    internal PlaybackDiagnosticsDialog(PlaybackDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
        InitializeComponent();
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        StatusText.Text = string.Empty;
        try
        {
            _snapshot = _diagnostics.CreateSnapshot();
            using var document = JsonDocument.Parse(_snapshot);
            var root = document.RootElement;
            var versions = root.GetProperty("Versions");
            VersionText.Text = $"App {versions.GetProperty("Application").GetString()} · .NET {versions.GetProperty("Runtime").GetString()}\n"
                + $"Windows {versions.GetProperty("OperatingSystem").GetString()} · Windows MediaPlayer\n"
                + $"Engine baseline: {versions.GetProperty("EngineVersion").GetString()} ({versions.GetProperty("EngineVersionKind").GetString()})";
            var events = root.GetProperty("Events");
            CountText.Text = $"{events.GetArrayLength()} retained local events";
            var summary = new StringBuilder();
            foreach (var entry in events.EnumerateArray())
            {
                var media = entry.GetProperty("Media");
                summary.AppendLine($"{entry.GetProperty("TimestampUtc").GetString()}  {entry.GetProperty("Event").GetString()} / {entry.GetProperty("Error").GetString()}");
                summary.AppendLine($"  Delivery: {media.GetProperty("Delivery").GetString()} · Source: {media.GetProperty("Container").GetString()} · {media.GetProperty("Video").GetString()} / {media.GetProperty("Audio").GetString()} / {media.GetProperty("Subtitle").GetString()}");
            }
            SummaryText.Text = summary.Length == 0 ? "No playback events have been recorded." : summary.ToString();
            ResultNotice.IsOpen = false;
            if (_diagnostics.LastIssue != PlaybackDiagnosticStorageIssue.None)
                ShowIssue("Some local diagnostic records could not be read or saved. The safe in-memory snapshot is still available.");
        }
        catch (Exception)
        {
            _snapshot = [];
            SummaryText.Text = "The diagnostic snapshot is unavailable.";
            CountText.Text = "No snapshot available";
            ShowIssue("The diagnostic snapshot is unavailable. Try refreshing it.");
        }
        UpdateActions();
    }

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
        _saving = true;
        UpdateActions();
        try
        {
            Directory.CreateDirectory(_diagnostics.DirectoryPath);
            await File.WriteAllBytesAsync(Path.Combine(_diagnostics.DirectoryPath, "snapshot.json"), _snapshot);
            ShowStatus("Saved snapshot.json in %LOCALAPPDATA%\\EmbyClient.Windows\\diagnostics. This replaces the previous snapshot.");
        }
        catch (Exception) { ShowStatus("The local snapshot could not be saved. Check available storage and try again."); }
        finally { _saving = false; UpdateActions(); }
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
        (FrameworkElementAutomationPeer.FromElement(StatusText)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(StatusText))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
