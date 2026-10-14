using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EmbyClient.App.Services;

/// <summary>
/// Matches only a playing fullscreen window's logical display. All native work runs on a serial background pump.
/// Call UpdateAsync for playback, preference, presenter, window-position, and display-topology changes.
/// Call RestoreAsync on retirement and await DisposeAsync before destroying the window.
/// </summary>
public sealed partial class ContentRefreshRateService : IAsyncDisposable
{
    private static readonly ContentRefreshRateRequest EmptyRequest = new(0, false, false, new(null, false, null));
    private readonly object _requestGate = new();
    private readonly IContentRefreshRateDisplayBackend _backend;
    private ContentRefreshRateRequest _desired = EmptyRequest;
    private ContentRefreshRateStatus _status = new(ContentRefreshRateState.Disabled, ContentRefreshRateReason.SettingDisabled);
    private Task<ContentRefreshRateStatus>? _pump;
    private long _revision;
    private bool _closed;
    private bool _restorePending;
    private ContentRefreshRateOwnership? _ownership;
    private FailedAttempt? _failedAttempt;
    private Activation? _externalChange;

    public ContentRefreshRateService() : this(new WindowsContentRefreshRateDisplayBackend()) { }

    internal ContentRefreshRateService(IContentRefreshRateDisplayBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    public ContentRefreshRateStatus Status => Volatile.Read(ref _status);
    public bool NeedsRestoreRetry => Volatile.Read(ref _restorePending);

    /// <summary>Raised on the background pump. A UI consumer must dispatch and read the current Status again.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Coalesces requests without executing native work or awaiting a native lock on the calling thread.</summary>
    public Task<ContentRefreshRateStatus> UpdateAsync(ContentRefreshRateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Playback);
        return QueueRequest(request, close: false);
    }

    /// <summary>Restores only this service's mode, with a fresh ownership check before the native write.</summary>
    public Task<ContentRefreshRateStatus> RestoreAsync() => QueueRequest(null, close: false);

    /// <summary>Prevents every later update from reacquiring. A repeated cleanup call retries a failed restoration.</summary>
    public ValueTask DisposeAsync() => new(QueueRequest(null, close: true));

    private Task<ContentRefreshRateStatus> QueueRequest(ContentRefreshRateRequest? request, bool close)
    {
        lock (_requestGate)
        {
            _closed |= close;
            _desired = request is null || _closed
                ? (request ?? _desired) with { Enabled = false } : request;
            ++_revision;
            return _pump ??= Task.Run(DrainRequests);
        }
    }

    private ContentRefreshRateStatus DrainRequests()
    {
        while (true)
        {
            ContentRefreshRateRequest request;
            long revision;
            bool closed;
            lock (_requestGate) { request = _desired; revision = _revision; closed = _closed; }
            try { Reconcile(request, revision, closed); }
            catch (Exception exception)
            {
                // Display control is optional and must never fault the player or the cleanup task.
                FailAndRestore(request,
                    exception is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException
                        ? ContentRefreshRateReason.PlatformUnavailable : ContentRefreshRateReason.ChangeFailed);
            }
            lock (_requestGate)
            {
                if (revision != _revision) continue;
                _pump = null;
                return Status;
            }
        }
    }

    private void Reconcile(ContentRefreshRateRequest request, long revision, bool closed)
    {
        if (closed || !ContentRefreshRatePolicy.IsEligible(request))
        {
            _failedAttempt = null;
            _externalChange = null;
            var restoration = TryRestoreOwnedMode();
            if (restoration.Result == RestoreResult.Pending) { PublishRestorePending(request, restoration); return; }
            var (state, reason) = InactiveStatus(request, closed);
            Publish(state, reason, request, restoration.Ownership?.DisplayName);
            return;
        }

        if (!_backend.IsSupported)
        {
            FailAndRestore(request, ContentRefreshRateReason.PlatformUnavailable);
            return;
        }
        if (!_backend.TryGetWindowDisplay(request.WindowHandle, out var display))
        {
            FailAndRestore(request, ContentRefreshRateReason.MonitorUnavailable);
            return;
        }
        var activation = new Activation(request.WindowHandle, request.Playback.PlaybackId!.Value, display.DisplayName);
        var frameRate = request.Playback.FramesPerSecond!.Value;

        if (_ownership is { } ownership)
        {
            var sameDisplay = SameDisplay(ownership.DisplayName, display.DisplayName);
            var samePlayback = ownership.PlaybackId == activation.PlaybackId;
            if (!_restorePending && sameDisplay && samePlayback && ownership.FramesPerSecond == frameRate)
            {
                if (display.Mode == ownership.AppliedMode)
                {
                    Publish(ContentRefreshRateState.Matched, ContentRefreshRateReason.None,
                        request, display.DisplayName, display.Mode.DisplayFrequency);
                    return;
                }
                // An external writer can also restore the original mode. Do not reapply while the same clip is active.
                AbandonOwnership();
                _externalChange = activation;
                PublishExternalChange(request, display.DisplayName);
                return;
            }

            var restoration = TryRestoreOwnedMode();
            if (restoration.Result == RestoreResult.Pending) { PublishRestorePending(request, restoration); return; }
            if (restoration.Result == RestoreResult.ExternalChange && sameDisplay && samePlayback)
            {
                _externalChange = activation;
                PublishExternalChange(request, display.DisplayName);
                return;
            }
            if (!IsCurrent(revision)) return;
            if (!_backend.TryGetWindowDisplay(request.WindowHandle, out display))
            {
                FailAndRestore(request, ContentRefreshRateReason.MonitorUnavailable);
                return;
            }
            activation = activation with { DisplayName = display.DisplayName };
        }

        if (_externalChange is { } changed && changed == activation)
        {
            PublishExternalChange(request, display.DisplayName);
            return;
        }
        _externalChange = null;
        var attempt = new Attempt(activation, display.Mode, frameRate);
        if (_failedAttempt is { } failed && failed.Attempt == attempt)
        {
            SetStatus(failed.Status);
            return;
        }

        var modes = _backend.GetModes(display.DisplayName);
        var candidates = ContentRefreshRatePolicy.SelectCandidates(display.Mode, modes, frameRate);
        if (candidates.Count == 0)
        {
            RememberFailure(attempt, request, ContentRefreshRateReason.NoCompatibleMode, display.DisplayName);
            return;
        }
        ContentDisplayMode? selected = null;
        int? rejection = null;
        foreach (var candidate in candidates)
        {
            if (candidate == display.Mode)
            {
                _failedAttempt = null;
                Publish(ContentRefreshRateState.Matched, ContentRefreshRateReason.None,
                    request, display.DisplayName, display.Mode.DisplayFrequency);
                return;
            }
            if (!IsCurrent(revision) || !CheckTarget(request, display, activation)) return;
            var test = _backend.TryChangeMode(display.DisplayName, display.Mode, candidate, testOnly: true);
            if (test.ExternalChange)
            {
                _externalChange = activation;
                PublishExternalChange(request, display.DisplayName);
                return;
            }
            if (test.Success) { selected = candidate; break; }
            rejection = test.NativeResult;
        }
        if (selected is not { } target)
        {
            RememberFailure(attempt, request, ContentRefreshRateReason.ModeRejected, display.DisplayName, rejection);
            return;
        }
        if (!IsCurrent(revision) || !CheckTarget(request, display, activation)) return;

        // Record the attempted mode before writing so an exception after a partial native change is recoverable.
        _ownership = new(display.DisplayName, display.Mode, target, activation.PlaybackId, frameRate);
        var change = _backend.TryChangeMode(display.DisplayName, display.Mode, target, testOnly: false);
        if (change.ExternalChange)
        {
            AbandonOwnership();
            _externalChange = activation;
            PublishExternalChange(request, display.DisplayName);
            return;
        }
        if (!change.Success)
        {
            var restoration = TryRestoreOwnedMode();
            if (restoration.Result == RestoreResult.Pending) { PublishRestorePending(request, restoration); return; }
            if (restoration.Result == RestoreResult.ExternalChange)
            {
                _externalChange = activation;
                PublishExternalChange(request, display.DisplayName);
                return;
            }
            RememberFailure(attempt, request, ContentRefreshRateReason.ChangeFailed, display.DisplayName, change.NativeResult);
            return;
        }
        if (!_backend.TryReadMode(display.DisplayName, out var actual) || actual != target)
        {
            var restoration = TryRestoreOwnedMode();
            if (restoration.Result == RestoreResult.Pending) { PublishRestorePending(request, restoration); return; }
            if (restoration.Result == RestoreResult.ExternalChange)
            {
                _externalChange = activation;
                PublishExternalChange(request, display.DisplayName);
                return;
            }
            // A driver can report success without retaining the mode. Do not repeat a visible switch every polling tick.
            RememberFailure(attempt, request, ContentRefreshRateReason.ReadbackFailed, display.DisplayName, change.NativeResult);
            return;
        }
        if (!IsCurrent(revision))
        {
            // The change itself raises WM_DISPLAYCHANGE and may enqueue the same eligible snapshot.
            // Let the next iteration decide whether to retain or restore; retirement still completes before the pump returns.
            return;
        }
        _failedAttempt = null;
        Publish(ContentRefreshRateState.Matched, ContentRefreshRateReason.None,
            request, display.DisplayName, actual.DisplayFrequency, change.NativeResult);
    }

    private bool CheckTarget(ContentRefreshRateRequest request, ContentDisplaySnapshot expected, Activation activation)
    {
        if (!_backend.TryGetWindowDisplay(request.WindowHandle, out var actual))
        {
            FailAndRestore(request, ContentRefreshRateReason.MonitorUnavailable);
            return false;
        }
        if (!SameDisplay(actual.DisplayName, expected.DisplayName))
        {
            // A fresh window-position update will select the new monitor; never apply the old monitor's candidate.
            Publish(ContentRefreshRateState.Inactive, ContentRefreshRateReason.MonitorUnavailable, request, actual.DisplayName);
            return false;
        }
        if (actual.Mode == expected.Mode) return true;
        _externalChange = activation;
        PublishExternalChange(request, actual.DisplayName);
        return false;
    }

    private Restoration TryRestoreOwnedMode()
    {
        var ownership = _ownership;
        if (ownership is null) { Volatile.Write(ref _restorePending, false); return new(RestoreResult.Released); }
        try
        {
            if (!_backend.TryReadMode(ownership.DisplayName, out var current)) return Pending(ownership);
            switch (ContentRefreshRatePolicy.GetRestoreDecision(ownership, current))
            {
                case ContentRefreshRateRestoreDecision.AlreadyRestored:
                    AbandonOwnership();
                    return new(RestoreResult.Released, ownership);
                case ContentRefreshRateRestoreDecision.AbandonExternalChange:
                    AbandonOwnership();
                    return new(RestoreResult.ExternalChange, ownership);
            }
            var test = _backend.TryChangeMode(ownership.DisplayName, current, ownership.OriginalMode, testOnly: true);
            if (test.ExternalChange) { AbandonOwnership(); return new(RestoreResult.ExternalChange, ownership); }
            if (!test.Success) return Pending(ownership, test.NativeResult);
            var change = _backend.TryChangeMode(ownership.DisplayName, current, ownership.OriginalMode, testOnly: false);
            if (change.ExternalChange) { AbandonOwnership(); return new(RestoreResult.ExternalChange, ownership); }
            if (!_backend.TryReadMode(ownership.DisplayName, out var actual)) return Pending(ownership, change.NativeResult);
            if (actual == ownership.OriginalMode)
            {
                AbandonOwnership();
                return new(RestoreResult.Released, ownership, change.NativeResult);
            }
            if (actual != ownership.AppliedMode)
            {
                AbandonOwnership();
                return new(RestoreResult.ExternalChange, ownership, change.NativeResult);
            }
            return Pending(ownership, change.NativeResult);
        }
        catch (Exception) { return Pending(ownership); }
    }

    private Restoration Pending(ContentRefreshRateOwnership ownership, int? nativeResult = null)
    {
        Volatile.Write(ref _restorePending, true);
        return new(RestoreResult.Pending, ownership, nativeResult);
    }

    private void AbandonOwnership()
    {
        _ownership = null;
        Volatile.Write(ref _restorePending, false);
    }

    private void FailAndRestore(ContentRefreshRateRequest request, ContentRefreshRateReason reason,
        string? displayName = null, int? nativeResult = null)
    {
        var restoration = TryRestoreOwnedMode();
        if (restoration.Result == RestoreResult.Pending) { PublishRestorePending(request, restoration); return; }
        if (restoration.Result == RestoreResult.ExternalChange && restoration.Ownership is { } prior)
        {
            _externalChange = new(request.WindowHandle, prior.PlaybackId, prior.DisplayName);
            PublishExternalChange(request, prior.DisplayName);
            return;
        }
        Publish(ContentRefreshRateState.Unsupported, reason, request,
            displayName ?? restoration.Ownership?.DisplayName, nativeResult: nativeResult);
    }

    private void RememberFailure(Attempt attempt, ContentRefreshRateRequest request,
        ContentRefreshRateReason reason, string displayName, int? nativeResult = null)
    {
        Publish(ContentRefreshRateState.Unsupported, reason, request, displayName, nativeResult: nativeResult);
        _failedAttempt = new(attempt, Status);
    }

    private void PublishRestorePending(ContentRefreshRateRequest request, Restoration restoration) =>
        Publish(ContentRefreshRateState.RestorePending, ContentRefreshRateReason.RestoreFailed, request,
            restoration.Ownership?.DisplayName, restoration.Ownership?.AppliedMode.DisplayFrequency, restoration.NativeResult);

    private void PublishExternalChange(ContentRefreshRateRequest request, string displayName) =>
        Publish(ContentRefreshRateState.ExternalChange, ContentRefreshRateReason.ExternalDisplayChange, request, displayName);

    private void Publish(ContentRefreshRateState state, ContentRefreshRateReason reason,
        ContentRefreshRateRequest request, string? displayName = null, uint? refreshRate = null, int? nativeResult = null) =>
        SetStatus(new(state, reason, displayName, request.Playback.FramesPerSecond, refreshRate,
            HasOwnedMode: _ownership is not null, NativeResult: nativeResult));

    private void SetStatus(ContentRefreshRateStatus status)
    {
        if (Status == status) return;
        Volatile.Write(ref _status, status);
        try { StatusChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { /* An optional UI subscriber cannot interrupt display cleanup. */ }
    }

    private bool IsCurrent(long revision) { lock (_requestGate) return revision == _revision; }

    private static bool SameDisplay(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static (ContentRefreshRateState, ContentRefreshRateReason) InactiveStatus(ContentRefreshRateRequest request, bool closed) =>
        closed ? (ContentRefreshRateState.Closed, ContentRefreshRateReason.Closed)
        : !request.Enabled ? (ContentRefreshRateState.Disabled, ContentRefreshRateReason.SettingDisabled)
        : !request.IsFullscreen ? (ContentRefreshRateState.Inactive, ContentRefreshRateReason.NotFullscreen)
        : !request.Playback.PlaybackId.HasValue || !request.Playback.IsPlayingVideo
            ? (ContentRefreshRateState.Inactive, ContentRefreshRateReason.NoPlayingVideo)
        : (ContentRefreshRateState.WaitingForFrameRate, ContentRefreshRateReason.MissingFrameRate);

    private readonly record struct Activation(nint WindowHandle, Guid PlaybackId, string DisplayName);
    private readonly record struct Attempt(Activation Activation, ContentDisplayMode OriginalMode, double FramesPerSecond);
    private sealed record FailedAttempt(Attempt Attempt, ContentRefreshRateStatus Status);
    private enum RestoreResult { Released, ExternalChange, Pending }
    private readonly record struct Restoration(
        RestoreResult Result, ContentRefreshRateOwnership? Ownership = null, int? NativeResult = null);

    /// <summary>Uses documented USER32 display APIs without registry writes or runtime-generated marshalling.</summary>
    private sealed unsafe partial class WindowsContentRefreshRateDisplayBackend : IContentRefreshRateDisplayBackend
    {
        private const uint EnumCurrentSettings = uint.MaxValue;
        private const uint MonitorDefaultToNearest = 2;
        private const uint CdsTest = 0x00000002;
        private const uint CdsFullscreen = 0x00000004;
        private const uint DmPosition = 0x00000020;
        private const uint DmDisplayOrientation = 0x00000080;
        private const uint DmBitsPerPel = 0x00040000;
        private const uint DmPelsWidth = 0x00080000;
        private const uint DmPelsHeight = 0x00100000;
        private const uint DmDisplayFlags = 0x00200000;
        private const uint DmDisplayFrequency = 0x00400000;
        private const uint DmDisplayFixedOutput = 0x20000000;
        private const uint RequiredModeFields = DmBitsPerPel | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        private const uint PreservedModeFields = DmPosition | DmDisplayOrientation | DmDisplayFlags | DmDisplayFixedOutput;
        private const uint MaximumEnumeratedModes = 4096;

        public bool IsSupported => OperatingSystem.IsWindows();

        public bool TryGetWindowDisplay(nint windowHandle, out ContentDisplaySnapshot display)
        {
            display = null!;
            if (!IsSupported || windowHandle == 0) return false;
            var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
            if (monitor == 0) return false;
            var enumeration = new MonitorEnumeration { Target = monitor };
            // The stack state contains no managed references, and the callback never throws or enters managed delegates.
            if (EnumDisplayMonitors(0, null, &FindTargetMonitor, (nint)(&enumeration)) == 0 || enumeration.Found == 0)
                return false;
            var info = new MonitorInfoEx { Size = 104 };
            if (GetMonitorInfo(monitor, &info) == 0) return false;
            var deviceName = new string(info.Device, 0, 32).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(deviceName) || !TryReadMode(deviceName, out var current)) return false;
            display = new(deviceName, current);
            return true;
        }

        public bool TryReadMode(string displayName, out ContentDisplayMode mode)
        {
            mode = default;
            if (!IsSupported || !TryReadNativeMode(displayName, out var native)) return false;
            mode = ToMode(native);
            return mode.Width > 0 && mode.Height > 0 && mode.BitsPerPixel > 0;
        }

        public IReadOnlyList<ContentDisplayMode> GetModes(string displayName)
        {
            var modes = new List<ContentDisplayMode>();
            if (!IsSupported) return modes;
            fixed (char* deviceName = displayName)
            {
                // Index zero initializes the device's mode cache; flags zero excludes unsupported raw modes.
                for (uint index = 0; index < MaximumEnumeratedModes; index++)
                {
                    var native = new DisplayDevMode { Size = 220 };
                    if (EnumDisplaySettingsEx(deviceName, index, &native, 0) == 0) break;
                    modes.Add(ToMode(native));
                }
            }
            return modes;
        }

        public ContentDisplayChangeResult TryChangeMode(
            string displayName, ContentDisplayMode expectedCurrent, ContentDisplayMode desired, bool testOnly)
        {
            if (!IsSupported || !TryReadNativeMode(displayName, out var native)) return new(false);
            if (ToMode(native) != expectedCurrent) return new(false, ExternalChange: true);
            if (!expectedCurrent.HasSamePresentation(desired) || expectedCurrent.X != desired.X || expectedCurrent.Y != desired.Y)
                return new(false);
            native.DisplayFrequency = desired.DisplayFrequency;
            // Explicit dimensions retain the resolution. Keep supported placement/orientation fields from current mode.
            native.Fields = RequiredModeFields | (native.Fields & PreservedModeFields);
            fixed (char* deviceName = displayName)
            {
                // hwnd and lParam are reserved NULL parameters. CDS_FULLSCREEN applies a temporary mode;
                // never use CDS_UPDATEREGISTRY, a NULL device name, or a NULL DEVMODE restoration.
                var result = ChangeDisplaySettingsEx(deviceName, &native, 0, testOnly ? CdsTest : CdsFullscreen, 0);
                return new(result == 0, NativeResult: result);
            }
        }

        private static bool TryReadNativeMode(string displayName, out DisplayDevMode mode)
        {
            mode = new() { Size = 220 };
            fixed (char* deviceName = displayName)
            fixed (DisplayDevMode* native = &mode)
                return EnumDisplaySettingsEx(deviceName, EnumCurrentSettings, native, 0) != 0
                    && (mode.Fields & RequiredModeFields) == RequiredModeFields;
        }

        private static ContentDisplayMode ToMode(DisplayDevMode mode) => new(
            mode.PelsWidth, mode.PelsHeight, mode.BitsPerPel, mode.DisplayFrequency,
            mode.PositionX, mode.PositionY, mode.DisplayOrientation, mode.DisplayFlags, mode.DisplayFixedOutput);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int FindTargetMonitor(nint monitor, nint deviceContext, NativeRect* bounds, nint data)
        {
            var state = (MonitorEnumeration*)data;
            if (monitor == state->Target) state->Found = 1;
            return 1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorEnumeration { public nint Target; public int Found; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        // MONITORINFOEXW: cbSize + RECT + RECT + DWORD + WCHAR[32], 104 bytes.
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-monitorinfoexw
        [StructLayout(LayoutKind.Sequential, Size = 104)]
        private struct MonitorInfoEx
        {
            public uint Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
            public fixed char Device[32];
        }

        // DEVMODEW is 220 bytes, including its 16-byte printer/display union and 4-byte flags/N-up union.
        // The display union uses two LONG coordinates followed by orientation and fixed output DWORDs.
        // https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-devmodew
        [StructLayout(LayoutKind.Explicit, Size = 220)]
        private struct DisplayDevMode
        {
            [FieldOffset(0)] public fixed char DeviceName[32];
            [FieldOffset(64)] public ushort SpecVersion;
            [FieldOffset(66)] public ushort DriverVersion;
            [FieldOffset(68)] public ushort Size;
            [FieldOffset(70)] public ushort DriverExtra;
            [FieldOffset(72)] public uint Fields;
            [FieldOffset(76)] public int PositionX;
            [FieldOffset(80)] public int PositionY;
            [FieldOffset(84)] public uint DisplayOrientation;
            [FieldOffset(88)] public uint DisplayFixedOutput;
            [FieldOffset(92)] public short Color;
            [FieldOffset(94)] public short Duplex;
            [FieldOffset(96)] public short YResolution;
            [FieldOffset(98)] public short TrueTypeOption;
            [FieldOffset(100)] public short Collate;
            [FieldOffset(102)] public fixed char FormName[32];
            [FieldOffset(166)] public ushort LogPixels;
            [FieldOffset(168)] public uint BitsPerPel;
            [FieldOffset(172)] public uint PelsWidth;
            [FieldOffset(176)] public uint PelsHeight;
            [FieldOffset(180)] public uint DisplayFlags;
            [FieldOffset(184)] public uint DisplayFrequency;
            [FieldOffset(188)] public uint IcmMethod;
            [FieldOffset(192)] public uint IcmIntent;
            [FieldOffset(196)] public uint MediaType;
            [FieldOffset(200)] public uint DitherType;
            [FieldOffset(204)] public uint Reserved1;
            [FieldOffset(208)] public uint Reserved2;
            [FieldOffset(212)] public uint PanningWidth;
            [FieldOffset(216)] public uint PanningHeight;
        }

        [LibraryImport("user32.dll", EntryPoint = "MonitorFromWindow")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private static partial nint MonitorFromWindow(nint window, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "EnumDisplayMonitors")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private static partial int EnumDisplayMonitors(nint deviceContext, NativeRect* clip,
            delegate* unmanaged[Stdcall]<nint, nint, NativeRect*, nint, int> callback, nint data);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private static partial int GetMonitorInfo(nint monitor, MonitorInfoEx* info);

        [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private static partial int EnumDisplaySettingsEx(char* deviceName, uint modeNumber, DisplayDevMode* mode, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private static partial int ChangeDisplaySettingsEx(char* deviceName, DisplayDevMode* mode,
            nint reservedWindow, uint flags, nint reservedParameter);
    }
}

internal readonly record struct ContentDisplayChangeResult(bool Success, bool ExternalChange = false, int? NativeResult = null);

internal interface IContentRefreshRateDisplayBackend
{
    bool IsSupported { get; }
    bool TryGetWindowDisplay(nint windowHandle, out ContentDisplaySnapshot display);
    bool TryReadMode(string displayName, out ContentDisplayMode mode);
    IReadOnlyList<ContentDisplayMode> GetModes(string displayName);
    ContentDisplayChangeResult TryChangeMode(
        string displayName, ContentDisplayMode expectedCurrent, ContentDisplayMode desired, bool testOnly);
}
