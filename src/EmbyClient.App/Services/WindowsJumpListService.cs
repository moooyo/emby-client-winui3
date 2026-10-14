using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EmbyClient.App.Services;

/// <summary>Publishes real Windows shell tasks for packaged and unpackaged desktop installations.</summary>
public sealed unsafe partial class WindowsJumpListService : IDisposable
{
    private static readonly Guid DestinationListClassId = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
    private static readonly Guid DestinationListInterfaceId = new("6332debf-87b5-4670-90c0-5e57b408a49e");
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-c000-000000000046");
    private static readonly Guid ShellLinkInterfaceId = new("000214f9-0000-0000-c000-000000000046");
    private static readonly Guid CollectionClassId = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
    private static readonly Guid CollectionInterfaceId = new("5632b1a4-e38a-400a-928a-d4cd63230295");
    private static readonly Guid ObjectArrayInterfaceId = new("92ca9dcd-5622-4bba-a805-5e9f541bd8c9");
    private static readonly Guid PropertyStoreInterfaceId = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly PropertyKey TitleKey = new(new Guid("f29f85e0-4ff9-1068-ab91-08002b27b3d9"), 2);
    private static readonly string? PackagedAppId = ReadPackagedAppId();
    private static bool _processIdentityAvailable;
    private static int? _processIdentityError;
    private readonly WindowsPackagedJumpListPublisher _packagedPublisher = new();
    private string? _publishedFingerprint;
    private bool _disposed;

    public static string ProcessAppId { get; } = CreateAppId();
    public static bool IsProcessIdentityAvailable => _processIdentityAvailable;
    public bool IsAvailable { get; private set; }
    public bool AreContinueItemsAvailable { get; private set; }
    public int? LastNativeError { get; private set; }

    /// <summary>Must run before window creation so the taskbar and destination list share the same identity.</summary>
    public static bool TryInitializeProcessIdentity(out int? nativeError)
    {
        if (PackagedAppId is not null)
        {
            nativeError = null;
            _processIdentityAvailable = true;
            _processIdentityError = null;
            return true;
        }
        try
        {
            int result;
            fixed (char* appId = ProcessAppId) result = SetCurrentProcessExplicitAppUserModelID(appId);
            nativeError = result < 0 ? result : null;
            _processIdentityAvailable = result >= 0;
            _processIdentityError = nativeError;
            return result >= 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            nativeError = exception.HResult;
            _processIdentityAvailable = false;
            _processIdentityError = nativeError;
            return false;
        }
    }

    /// <summary>Call on the window STA. Packaged publishing is asynchronous and coalesces newer account state.</summary>
    public void Update(WindowsJumpListPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (_disposed || _publishedFingerprint == presentation.Fingerprint) return;
        if (!_processIdentityAvailable)
        {
            IsAvailable = false;
            AreContinueItemsAvailable = false;
            LastNativeError = _processIdentityError;
            return;
        }
        if (PackagedAppId is not null)
        {
            _packagedPublisher.Update(presentation, PackagedPublished);
            return;
        }
        nint list = 0;
        nint removed = 0;
        nint collection = 0;
        nint tasks = 0;
        nint destinations = 0;
        nint destinationArray = 0;
        var begun = false;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("The executable path is unavailable.");
            list = CreateObject(DestinationListClassId, DestinationListInterfaceId);
            fixed (char* appId = ProcessAppId)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Method(list, 3))(list, appId));
            var arrayInterface = ObjectArrayInterfaceId;
            uint maximumSlots = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, Guid*, nint*, int>)Method(list, 4))
                (list, &maximumSlots, &arrayInterface, &removed));
            begun = true;
            collection = CreateObject(CollectionClassId, CollectionInterfaceId);
            AddTask(collection, executable, presentation.SearchTitle, presentation.SearchTitle,
                new(WindowsActivationDestination.Search));
            AddTask(collection, executable, presentation.SettingsTitle, presentation.SettingsTitle,
                new(WindowsActivationDestination.Settings));
            AddTask(collection, executable, presentation.SwitchAccountTitle, presentation.SwitchAccountTitle,
                new(WindowsActivationDestination.SwitchAccount));
            tasks = QueryInterface(collection, ObjectArrayInterfaceId);
            // User tasks remain available when Windows disables recent-document tracking for privacy.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(list, 7))(list, tasks));
            var removedArguments = ReadRemovedArguments(removed);
            var continueItems = presentation.GetContinueItems()
                .Where(item => !removedArguments.Contains(item.Route.ToArguments()))
                .Take((int)Math.Min(maximumSlots, (uint)WindowsJumpListPresentation.MaximumContinueItems)).ToArray();
            var continueAvailable = true;
            if (continueItems.Length > 0)
            {
                destinations = CreateObject(CollectionClassId, CollectionInterfaceId);
                foreach (var item in continueItems)
                    AddTask(destinations, executable, WindowsJumpListPresentation.ItemTitle(item), item.Subtitle, item.Route);
                destinationArray = QueryInterface(destinations, ObjectArrayInterfaceId);
                int result;
                fixed (char* category = LastWatchedItem.DisplayText(presentation.ContinueTitle, "Continue watching"))
                    result = ((delegate* unmanaged[Stdcall]<nint, char*, nint, int>)Method(list, 5))(list, category, destinationArray);
                // A privacy policy may deny destinations while still allowing the independent Tasks group.
                if (result == unchecked((int)0x80070005)) continueAvailable = false;
                else Check(result);
            }
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(list, 8))(list));
            begun = false;
            _publishedFingerprint = presentation.Fingerprint;
            IsAvailable = true;
            AreContinueItemsAvailable = continueAvailable;
            LastNativeError = null;
        }
        catch (Exception exception)
        {
            IsAvailable = false;
            AreContinueItemsAvailable = false;
            LastNativeError = exception.HResult;
            if (begun && list != 0) _ = ((delegate* unmanaged[Stdcall]<nint, int>)Method(list, 11))(list);
            System.Diagnostics.Trace.TraceWarning("Windows JumpList update failed (0x{0:X8}).", exception.HResult);
        }
        finally
        {
            Release(destinationArray);
            Release(destinations);
            Release(tasks);
            Release(collection);
            Release(removed);
            Release(list);
        }
    }

    private void PackagedPublished(string fingerprint, bool continueAvailable, int? nativeError)
    {
        if (_disposed) return;
        IsAvailable = nativeError is null;
        AreContinueItemsAvailable = nativeError is null && continueAvailable;
        LastNativeError = nativeError;
        if (nativeError is null) _publishedFingerprint = fingerprint;
    }

    private static HashSet<string> ReadRemovedArguments(nint array)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (array == 0) return result;
        uint count = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(array, 3))(array, &count));
        // The shell owns removed destinations; accept only their bounded argument strings for comparison.
        for (uint index = 0; index < count; index++)
        {
            nint link = 0;
            var linkInterface = ShellLinkInterfaceId;
            var get = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Method(array, 4))
                (array, index, &linkInterface, &link);
            if (get < 0 || link == 0) continue;
            try
            {
                var buffer = new char[1024];
                fixed (char* text = buffer)
                {
                    if (((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Method(link, 10))(link, text, buffer.Length) >= 0)
                        result.Add(new string(text));
                }
            }
            finally { Release(link); }
        }
        return result;
    }

    private static void AddTask(nint collection, string executable, string title, string description,
        WindowsActivationRoute route)
    {
        nint link = 0;
        nint properties = 0;
        try
        {
            link = CreateObject(ShellLinkClassId, ShellLinkInterfaceId);
            fixed (char* path = executable)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Method(link, 20))(link, path));
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Method(link, 17))(link, path, 0));
            }
            fixed (char* folder = Path.GetDirectoryName(executable)!)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Method(link, 9))(link, folder));
            fixed (char* arguments = route.ToArguments())
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Method(link, 11))(link, arguments));
            fixed (char* tooltip = description)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Method(link, 7))(link, tooltip));
            Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Method(link, 15))(link, 1));
            properties = QueryInterface(link, PropertyStoreInterfaceId);
            var key = TitleKey;
            var text = Marshal.StringToCoTaskMemUni(LastWatchedItem.DisplayText(title, "Lumen"));
            try
            {
                var value = new PropVariant { VariantType = 31, Text = text };
                Check(((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)Method(properties, 6))
                    (properties, &key, &value));
                Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(properties, 7))(properties));
            }
            finally { Marshal.FreeCoTaskMem(text); }
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(collection, 5))(collection, link));
        }
        finally { Release(properties); Release(link); }
    }

    private static string CreateAppId()
    {
        // Packaged notifications use the package AUMID. Reuse it for taskbar grouping and shell tasks.
        if (PackagedAppId is not null) return PackagedAppId;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable)))
            return "EmbyClient.Windows.Lumen";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppDataPaths.RootDirectory.ToUpperInvariant())));
        return $"EmbyClient.Windows.Lumen.Profile.{hash[..16]}";
    }

    private static string? ReadPackagedAppId()
    {
        try
        {
            uint characters = 0;
            if (GetCurrentApplicationUserModelId(&characters, null) != 122 || characters is < 2 or > 512) return null;
            var buffer = new char[characters];
            fixed (char* appId = buffer)
            {
                if (GetCurrentApplicationUserModelId(&characters, appId) != 0 || characters > buffer.Length) return null;
                return new string(appId);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private static nint CreateObject(Guid classId, Guid interfaceId)
    {
        nint result = 0;
        Check(CoCreateInstance(&classId, 0, 1, &interfaceId, &result));
        return result;
    }

    private static nint QueryInterface(nint instance, Guid interfaceId)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(instance, 0))(instance, &interfaceId, &result));
        return result;
    }

    private static nint Method(nint instance, int slot) => (*(nint**)instance)[slot];
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    private static void Release(nint instance)
    {
        if (instance != 0) _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Method(instance, 2))(instance);
    }

    // A committed JumpList survives normal application shutdown. Account retirement publishes a clean task set first.
    public void Dispose() { _disposed = true; _publishedFingerprint = null; _packagedPublisher.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public nint Text;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SetCurrentProcessExplicitAppUserModelID(char* appId);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* classId, nint outer, uint context, Guid* interfaceId, nint* instance);

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentApplicationUserModelId(uint* appIdLength, char* appId);
}
