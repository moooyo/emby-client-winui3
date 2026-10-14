using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace EmbyClient.App.Services;

/// <summary>Shows opt-in Windows notifications for episodes received while this application is connected.</summary>
internal sealed class EpisodeNotificationService : IAsyncDisposable
{
    private const int MaximumHeroImageBytes = 3 * 1024 * 1024;
    private const int MaximumRetainedHeroImages = 32;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly Queue<(uint NotificationId, string Path)> _heroImages = new();
    private readonly string _artworkDirectory = Path.Combine(AppDataPaths.RootDirectory, "episode-notification-artwork");
    private string? _serverId;
    private string? _userId;
    private bool _online;
    private bool _fullScreen;
    private bool _enabled;
    private long _deliveryRevision;
    private bool _disposed;

    public long DeliveryRevision { get { lock (_sync) return _deliveryRevision; } }

    /// <summary>Retires in-flight lookups whenever account, connection, fullscreen, or opt-in state changes.</summary>
    public long UpdateContext(string? serverId, string? userId, bool isOnline, bool isFullScreen, bool isEnabled)
    {
        lock (_sync)
        {
            if (_disposed) return _deliveryRevision;
            if (!string.Equals(_serverId, serverId, StringComparison.Ordinal)
                || !string.Equals(_userId, userId, StringComparison.Ordinal)
                || _online != isOnline || _fullScreen != isFullScreen || _enabled != isEnabled)
                _deliveryRevision++;
            _serverId = serverId;
            _userId = userId;
            _online = isOnline;
            _fullScreen = isFullScreen;
            _enabled = isEnabled;
            return _deliveryRevision;
        }
    }

    public bool CanDeliver(EpisodeNotificationTarget target, long expectedRevision)
    {
        lock (_sync) return CanDeliverCore(target, expectedRevision);
    }

    /// <summary>Invalidates delivery immediately on a background connection event without changing account ownership.</summary>
    public void SuspendContext(string? serverId, string? userId)
    {
        lock (_sync)
        {
            if (_disposed || !string.Equals(serverId, _serverId, StringComparison.Ordinal)
                || !string.Equals(userId, _userId, StringComparison.Ordinal)) return;
            _online = false;
            _deliveryRevision++;
        }
    }

    private bool CanDeliverCore(EpisodeNotificationTarget target, long expectedRevision) =>
        !_disposed && _enabled && _online && !_fullScreen && expectedRevision == _deliveryRevision
        && target.IsValid && target.Matches(_serverId, _userId);

    /// <summary>The caller captures the delivery revision before its authenticated episode lookup.</summary>
    public async Task<bool> TryShowAsync(EpisodeNotificationTarget target, EpisodeNotificationContent content,
        byte[]? heroImageBytes, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(content);
        if (!CanDeliver(target, expectedRevision)) return false;
        await _presentationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? imagePath = null;
        var shown = false;
        try
        {
            if (!CanDeliver(target, expectedRevision)) return false;
            if (heroImageBytes is { Length: > 0 and <= MaximumHeroImageBytes })
            {
                try
                {
                    await PruneHeroImagesAsync().ConfigureAwait(false);
                    if (_heroImages.Count < MaximumRetainedHeroImages)
                    {
                        Directory.CreateDirectory(_artworkDirectory);
                        imagePath = Path.Combine(_artworkDirectory, $"{Guid.NewGuid():N}.jpg");
                        await File.WriteAllBytesAsync(imagePath, heroImageBytes, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (IsStorageFailure(exception))
                {
                    if (imagePath is not null) DeleteArtwork(imagePath);
                    imagePath = null;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!CanDeliverCore(target, expectedRevision)) return false;
                var notification = Build(target, content, imagePath is null ? null : new Uri(imagePath));
                AppNotificationManager.Default.Show(notification);
                shown = notification.Id != 0;
                if (shown && imagePath is not null) _heroImages.Enqueue((notification.Id, imagePath));
                return shown;
            }
        }
        catch (Exception exception) when (IsNotificationFailure(exception)) { return false; }
        finally
        {
            if (!shown && imagePath is not null) DeleteArtwork(imagePath);
            _presentationGate.Release();
        }
    }

    // Retained for callers without artwork. The same online/fullscreen guard applies.
    public bool TryShow(EpisodeNotificationTarget target, string title, string description)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            lock (_sync)
            {
                if (!CanDeliverCore(target, _deliveryRevision)) return false;
                var content = new EpisodeNotificationContent(title, description,
                    LumenText.Get("Play now"), LumenText.Get("Remind later"));
                var notification = Build(target, content, null);
                AppNotificationManager.Default.Show(notification);
                return notification.Id != 0;
            }
        }
        catch (Exception exception) when (IsNotificationFailure(exception)) { return false; }
    }

    private static AppNotification Build(EpisodeNotificationTarget target, EpisodeNotificationContent content, Uri? heroImage)
    {
        // WindowsActivationService registers the shared manager before reading launch arguments.
        var builder = new AppNotificationBuilder()
            .AddArgument("action", EpisodeNotificationTarget.Action)
            .AddArgument("serverId", target.ServerId)
            .AddArgument("userId", target.UserId)
            .AddArgument("itemId", target.ItemId)
            .AddText(content.Title)
            .AddText(content.Description)
            .AddButton(Button(content.PlayNowLabel, target with { Intent = EpisodeNotificationIntent.PlayNow }))
            .AddButton(Button(content.RemindLaterLabel, target with { Intent = EpisodeNotificationIntent.RemindLater }));
        if (heroImage is not null) builder.SetHeroImage(heroImage, content.Title);
        var notification = builder.BuildNotification();
        notification.Tag = Hash(target.ItemId)[..32];
        notification.Group = Hash($"{target.ServerId}\n{target.UserId}")[..32];
        return notification;
    }

    private static AppNotificationButton Button(string label, EpisodeNotificationTarget target) =>
        new AppNotificationButton(label)
            .AddArgument("action", target.ActivationAction)
            .AddArgument("serverId", target.ServerId)
            .AddArgument("userId", target.UserId)
            .AddArgument("itemId", target.ItemId);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task PruneHeroImagesAsync()
    {
        while (_heroImages.Count >= MaximumRetainedHeroImages)
        {
            var oldest = _heroImages.Peek();
            try { await AppNotificationManager.Default.RemoveByIdAsync(oldest.NotificationId); }
            catch (Exception exception) when (IsNotificationFailure(exception)) { return; }
            _heroImages.Dequeue();
            DeleteArtwork(oldest.Path);
        }
    }

    public async Task ClearAsync()
    {
        lock (_sync) _deliveryRevision++;
        await _presentationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await AppNotificationManager.Default.RemoveAllAsync(); }
            catch (Exception exception) when (IsNotificationFailure(exception)) { }
            while (_heroImages.TryDequeue(out var image)) DeleteArtwork(image.Path);
            try { Directory.Delete(_artworkDirectory, recursive: false); }
            catch (Exception exception) when (IsStorageFailure(exception)) { }
        }
        finally { _presentationGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _deliveryRevision++;
        }
        await ClearAsync();
    }

    private static void DeleteArtwork(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (IsStorageFailure(exception)) { }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private static bool IsNotificationFailure(Exception exception) => exception is COMException
        or InvalidOperationException or ArgumentException or UnauthorizedAccessException or TypeLoadException or NotSupportedException;
}
