using Windows.UI.StartScreen;

namespace EmbyClient.App.Services;

/// <summary>Publishes the package's real shell list while preserving the window's synchronization context.</summary>
internal sealed partial class WindowsPackagedJumpListPublisher : IDisposable
{
    private readonly HashSet<string> _removedArguments = new(StringComparer.Ordinal);
    private PendingPresentation? _pending;
    private bool _publishing;
    private bool _disposed;

    public void Update(WindowsJumpListPresentation presentation, Action<string, bool, int?> completed)
    {
        if (_disposed) return;
        _pending = new(presentation, completed);
        if (!_publishing) _ = PublishPendingAsync();
    }

    private async Task PublishPendingAsync()
    {
        _publishing = true;
        try
        {
            while (!_disposed && _pending is { } pending)
            {
                _pending = null;
                var fingerprint = pending.Presentation.Fingerprint;
                try
                {
                    if (!JumpList.IsSupported()) throw new PlatformNotSupportedException("Windows JumpList is unavailable.");
                    var list = await JumpList.LoadCurrentAsync();
                    if (_disposed) return;
                    if (_pending is { } newer && newer.Presentation.Fingerprint != fingerprint) continue;
                    foreach (var removed in list.Items.Where(item => item.RemovedByUser))
                        _removedArguments.Add(removed.Arguments);
                    list.SystemGroupKind = JumpListSystemGroupKind.None;
                    list.Items.Clear();
                    foreach (var item in pending.Presentation.GetContinueItems())
                    {
                        var arguments = item.Route.ToArguments();
                        if (_removedArguments.Contains(arguments)) continue;
                        var destination = JumpListItem.CreateWithArguments(arguments, WindowsJumpListPresentation.ItemTitle(item));
                        destination.GroupName = LastWatchedItem.DisplayText(pending.Presentation.ContinueTitle, "Continue watching");
                        destination.Description = item.Subtitle;
                        // Use the package's default app logo. Emby HTTP images are not valid JumpList Logo URIs.
                        list.Items.Add(destination);
                    }
                    AddTask(list, pending.Presentation.SearchTitle, WindowsActivationDestination.Search);
                    AddTask(list, pending.Presentation.SettingsTitle, WindowsActivationDestination.Settings);
                    AddTask(list, pending.Presentation.SwitchAccountTitle, WindowsActivationDestination.SwitchAccount);
                    await list.SaveAsync();
                    if (!_disposed) pending.Completed(fingerprint, true, null);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceWarning("Windows packaged JumpList update failed (0x{0:X8}).", exception.HResult);
                    if (!_disposed) pending.Completed(fingerprint, false, exception.HResult);
                }
            }
        }
        finally { _publishing = false; }
    }

    private void AddTask(JumpList list, string title, WindowsActivationDestination destination)
    {
        var arguments = new WindowsActivationRoute(destination).ToArguments();
        if (_removedArguments.Contains(arguments)) return;
        var task = JumpListItem.CreateWithArguments(arguments, LastWatchedItem.DisplayText(title, "Lumen"));
        task.GroupName = string.Empty;
        task.Description = LastWatchedItem.DisplayText(title);
        list.Items.Add(task);
    }

    public void Dispose() { _disposed = true; _pending = null; }

    private sealed record PendingPresentation(WindowsJumpListPresentation Presentation, Action<string, bool, int?> Completed);
}
