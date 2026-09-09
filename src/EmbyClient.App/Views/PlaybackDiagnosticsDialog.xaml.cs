using System.Text;
using System.Text.Json;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
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
                ShowResult("Some local diagnostic records could not be read or saved. The safe in-memory snapshot is still available.", InfoBarSeverity.Warning);
        }
        catch (Exception)
        {
            _snapshot = [];
            ShowResult("The diagnostic snapshot is unavailable. Try refreshing it.", InfoBarSeverity.Warning);
        }
        UpdateActions();
    }

    private void CopyClicked(object sender, RoutedEventArgs args)
    {
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
            ShowResult(copied ? "Safe snapshot copied. Clipboard history and roaming are disabled for this copy."
                : "The clipboard is unavailable. Try again.", copied ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception) { ShowResult("The clipboard is unavailable. Try again.", InfoBarSeverity.Warning); }
    }

    private async void SaveClicked(object sender, RoutedEventArgs args)
    {
        if (_saving || _snapshot.Length == 0) return;
        _saving = true;
        UpdateActions();
        try
        {
            Directory.CreateDirectory(_diagnostics.DirectoryPath);
            await File.WriteAllBytesAsync(Path.Combine(_diagnostics.DirectoryPath, "snapshot.json"), _snapshot);
            ShowResult("Saved snapshot.json in %LOCALAPPDATA%\\EmbyClient.Windows\\diagnostics. This replaces the previous snapshot.", InfoBarSeverity.Success);
        }
        catch (Exception) { ShowResult("The local snapshot could not be saved. Check available storage and try again.", InfoBarSeverity.Warning); }
        finally { _saving = false; UpdateActions(); }
    }

    private void RefreshClicked(object sender, RoutedEventArgs args) => RefreshSnapshot();

    private void UpdateActions()
    {
        CopyButton.IsEnabled = !_saving && _snapshot.Length > 0;
        SaveButton.IsEnabled = !_saving && _snapshot.Length > 0;
        RefreshButton.IsEnabled = !_saving;
    }

    private void ShowResult(string message, InfoBarSeverity severity)
    {
        ResultNotice.Message = message;
        ResultNotice.Severity = severity;
        ResultNotice.IsOpen = true;
    }
}
