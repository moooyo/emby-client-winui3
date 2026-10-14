using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;

namespace EmbyClient.App.Services;

public sealed record WindowsActivationRegistration(bool Redirected, WindowsActivationRoute? Route,
    IReadOnlyDictionary<string, string>? NotificationArguments = null);

/// <summary>Routes Windows App SDK activation to one process without retaining raw activation arguments.</summary>
public sealed partial class WindowsActivationService : IDisposable
{
    private AppInstance? _instance;
    private bool _notificationsRegistered;
    private bool _disposed;

    /// <summary>May arrive off the UI thread. The window must marshal the known route onto its dispatcher.</summary>
    public event EventHandler<WindowsActivationRoute?>? ActivationRequested;
    public event EventHandler<IReadOnlyDictionary<string, string>>? EpisodeNotificationActivationRequested;
    public bool IsSingleInstanceAvailable { get; private set; }
    public bool AreAppNotificationsAvailable => _notificationsRegistered && !_disposed;
    public int? LastNativeError { get; private set; }

    public async Task<WindowsActivationRegistration> RegisterAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsActivationService));
        var fallback = WindowsActivationRoute.TryParseArguments(Environment.GetCommandLineArgs().Skip(1).ToArray(), out var initial)
            ? initial : null;
        try
        {
            RegisterAppNotifications();
            var arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
            var instance = AppInstance.FindOrRegisterForKey(CreateInstanceKey());
            if (!instance.IsCurrent)
            {
                _ = AllowSetForegroundWindow(instance.ProcessId);
                // RedirectActivationToAsync must finish before this process exits. Awaiting on a worker keeps
                // the WinUI STA responsive to the marshalling used by Windows App SDK redirection.
                await Task.Run(async () => await instance.RedirectActivationToAsync(arguments).AsTask()).ConfigureAwait(false);
                UnregisterAppNotifications();
                IsSingleInstanceAvailable = true;
                return new(true, null);
            }
            _instance = instance;
            instance.Activated += InstanceActivated;
            IsSingleInstanceAvailable = true;
            LastNativeError = null;
            return new(false, ReadRoute(arguments) ?? fallback, ReadEpisodeNotification(arguments));
        }
        catch (Exception exception)
        {
            IsSingleInstanceAvailable = false;
            LastNativeError = exception.HResult;
            System.Diagnostics.Trace.TraceWarning("Windows activation registration failed (0x{0:X8}).", exception.HResult);
            return new(false, fallback);
        }
    }

    private void RegisterAppNotifications()
    {
        if (_notificationsRegistered) return;
        try
        {
            // Windows App SDK requires notification registration before GetActivatedEventArgs.
            // A cold notification launch can arrive through NotificationInvoked after ordinary Launch activation.
            AppNotificationManager.Default.NotificationInvoked += AppNotificationInvoked;
            try { AppNotificationManager.Default.Register(); }
            catch
            {
                AppNotificationManager.Default.NotificationInvoked -= AppNotificationInvoked;
                throw;
            }
            _notificationsRegistered = true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Windows notification registration failed (0x{0:X8}).", exception.HResult);
        }
    }

    private void AppNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs arguments)
    {
        if (_disposed) return;
        try
        {
            if (ReadEpisodeNotification(arguments) is { } notification)
                EpisodeNotificationActivationRequested?.Invoke(this, notification);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Windows notification activation failed (0x{0:X8}).", exception.HResult);
        }
    }

    private void InstanceActivated(object? sender, AppActivationArguments arguments)
    {
        if (_disposed) return;
        try
        {
            if (ReadEpisodeNotification(arguments) is { } notification)
                EpisodeNotificationActivationRequested?.Invoke(this, notification);
            else
                // An ordinary second launch brings the existing window forward without changing its content.
                ActivationRequested?.Invoke(this, ReadRoute(arguments));
        }
        catch (Exception exception)
        {
            LastNativeError = exception.HResult;
            System.Diagnostics.Trace.TraceWarning("Windows activation delivery failed (0x{0:X8}).", exception.HResult);
        }
    }

    private static IReadOnlyDictionary<string, string>? ReadEpisodeNotification(AppActivationArguments arguments)
    {
        if (arguments.Kind != ExtendedActivationKind.AppNotification
            || arguments.Data is not AppNotificationActivatedEventArgs notification) return null;
        return ReadEpisodeNotification(notification);
    }

    private static IReadOnlyDictionary<string, string>? ReadEpisodeNotification(AppNotificationActivatedEventArgs notification)
    {
        var values = notification.Arguments.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (!EpisodeNotificationTarget.TryParse(values, out var target)) return null;
        // Keep only the declared schema even if another producer supplies unrelated activation arguments.
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = target!.ActivationAction,
            ["serverId"] = target!.ServerId,
            ["userId"] = target.UserId,
            ["itemId"] = target.ItemId
        };
    }

    private static WindowsActivationRoute? ReadRoute(AppActivationArguments arguments)
    {
        if (arguments.Data is IProtocolActivatedEventArgs protocol)
            return WindowsActivationRoute.TryParseUri(protocol.Uri, out var route) ? route : null;
        var commandLine = arguments.Data switch
        {
            ICommandLineActivatedEventArgs command => command.Operation.Arguments,
            ILaunchActivatedEventArgs launch => launch.Arguments,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Length > 32 * 1024) return null;
        var tokens = SplitCommandLine(commandLine);
        if (tokens.Length > 0 && !tokens[0].StartsWith("--", StringComparison.Ordinal)) tokens = tokens[1..];
        return WindowsActivationRoute.TryParseArguments(tokens, out var parsed) ? parsed : null;
    }

    private static unsafe string[] SplitCommandLine(string commandLine)
    {
        int count = 0;
        nint arguments;
        // A synthetic executable makes the parser's special argv[0] rules irrelevant for launch arguments.
        var text = commandLine.StartsWith("--", StringComparison.Ordinal) ? $"lumen.exe {commandLine}" : commandLine;
        fixed (char* source = text) arguments = CommandLineToArgvW(source, &count);
        if (arguments == 0) return [];
        try
        {
            if (count is < 1 or > 8) return [];
            var result = new string[count];
            var pointers = (nint*)arguments;
            for (var index = 0; index < count; index++) result[index] = Marshal.PtrToStringUni(pointers[index]) ?? string.Empty;
            return result;
        }
        finally { _ = LocalFree(arguments); }
    }

    private static string CreateInstanceKey()
    {
        // Opt-in test profiles and separately configured data roots cannot redirect into another account store.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppDataPaths.RootDirectory.ToUpperInvariant())));
        return $"EmbyClient.Windows.Lumen.{hash}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAppNotifications();
        if (_instance is { } instance)
        {
            try
            {
                instance.Activated -= InstanceActivated;
                instance.UnregisterKey();
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                System.Diagnostics.Trace.TraceWarning("Windows activation cleanup failed (0x{0:X8}).", exception.HResult);
            }
        }
        _instance = null;
    }

    private void UnregisterAppNotifications()
    {
        if (!_notificationsRegistered) return;
        _notificationsRegistered = false;
        try
        {
            AppNotificationManager.Default.NotificationInvoked -= AppNotificationInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Windows notification cleanup failed (0x{0:X8}).", exception.HResult);
        }
    }

    [LibraryImport("shell32.dll")]
    private static unsafe partial nint CommandLineToArgvW(char* commandLine, int* argumentCount);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("user32.dll")]
    private static partial int AllowSetForegroundWindow(uint processId);
}
