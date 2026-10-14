using System.Runtime.InteropServices;
using EmbyClient.App.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinUIEx.Messaging;

namespace EmbyClient.App.Services;

/// <summary>Contains only the playback identity and capabilities needed by the Windows taskbar.</summary>
public sealed record TaskbarPlaybackState(Guid? PlaybackId, bool IsPlaying, bool CanToggle,
    bool CanPrevious, bool CanNext, bool Enabled);

/// <summary>Owns native thumbnail buttons and a static playback overlay for one application window.</summary>
public sealed unsafe partial class TaskbarPlaybackService : IDisposable
{
    private static readonly Guid TaskbarClassId = new("56fdf344-fd6d-11d0-958a-006097c9a090");
    private static readonly Guid TaskbarInterfaceId = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");
    private static readonly TaskbarPlaybackState EmptyState = new(null, false, false, false, false, false);
    private const uint PreviousButtonId = 0x4C01;
    private const uint ToggleButtonId = 0x4C02;
    private const uint NextButtonId = 0x4C03;
    private const uint CommandMessage = 0x0111;
    private const uint ThumbnailButtonClicked = 0x1800;
    private const uint ButtonDisabled = 0x1;
    private const uint ButtonHidden = 0x8;
    private const uint ButtonMask = 0x2 | 0x4 | 0x8;
    private readonly DispatcherQueue _dispatcher;
    private readonly nint _windowHandle;
    private readonly uint _taskbarCreatedMessage;
    private readonly WindowMessageMonitor _monitor;
    private readonly UISettings _uiSettings = new();
    private readonly nint[] _icons = new nint[5];
    private TaskbarPlaybackState _state = EmptyState;
    private TaskbarPlaybackState? _publishedState;
    private nint _taskbar;
    private int _iconSize;
    private int _overlaySize;
    private Color _iconForeground;
    private bool _taskbarReady;
    private bool _buttonsAdded;
    private bool _overlayVisible;
    private bool _disposed;

    /// <summary>Raised on the window dispatcher. The receiver must recheck PlaybackId before executing a command.</summary>
    public event EventHandler<NativeMediaCommandEventArgs>? CommandRequested;

    public bool IsAvailable => !_disposed && _buttonsAdded;
    public int? LastNativeError { get; private set; }

    public TaskbarPlaybackService(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _dispatcher = window.DispatcherQueue;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        fixed (char* name = "TaskbarButtonCreated") _taskbarCreatedMessage = RegisterWindowMessage(name);
        _monitor = new WindowMessageMonitor(window);
        _monitor.WindowMessageReceived += WindowMessageReceived;
        if (_taskbarCreatedMessage == 0) LastNativeError = Marshal.GetLastPInvokeError();
    }

    public void Update(TaskbarPlaybackState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => Update(state));
            return;
        }
        if (_state == state) return;
        _state = state;
        Publish();
    }

    private void WindowMessageReceived(object? sender, WindowMessageEventArgs args)
    {
        if (_disposed) return;
        try
        {
            var message = args.Message.MessageId;
            if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
            {
                // Explorer recreates taskbar buttons after a restart. Register the fixed toolbar again for that lifetime.
                ReleaseTaskbar();
                _taskbarReady = true;
                _buttonsAdded = false;
                _overlayVisible = false;
                _publishedState = null;
                Publish();
            }
            else if (message == CommandMessage)
            {
                var parameter = unchecked((nuint)args.Message.WParam);
                if (((parameter >> 16) & 0xffff) != ThumbnailButtonClicked) return;
                var id = (uint)(parameter & 0xffff);
                if (id is not (PreviousButtonId or ToggleButtonId or NextButtonId)) return;
                args.Handled = true;
                args.Result = 0;
                QueueCommand(id);
            }
            else if (message is 0x001A or 0x031A or 0x02E0)
            {
                // Setting, theme, and DPI notifications refresh system-facing icon contrast and resolution.
                if (_taskbarReady) Publish(forceImages: true);
            }
        }
        catch (Exception exception)
        {
            LastNativeError = exception.HResult;
            _publishedState = null;
        }
    }

    private void QueueCommand(uint buttonId)
    {
        var shown = _publishedState;
        if (shown?.PlaybackId is not { } playbackId || !shown.Enabled || !CanInvoke(shown, buttonId)
            || _state.PlaybackId != playbackId || !_state.Enabled || !CanInvoke(_state, buttonId)) return;
        var command = buttonId switch
        {
            PreviousButtonId => NativeMediaCommand.Previous,
            NextButtonId => NativeMediaCommand.Next,
            _ => shown.IsPlaying ? NativeMediaCommand.Pause : NativeMediaCommand.Play
        };
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _state.PlaybackId != playbackId || !_state.Enabled || !CanInvoke(_state, buttonId)) return;
            CommandRequested?.Invoke(this, new NativeMediaCommandEventArgs(playbackId, command));
        });
    }

    private static bool CanInvoke(TaskbarPlaybackState state, uint buttonId) => buttonId switch
    {
        PreviousButtonId => state.CanPrevious,
        NextButtonId => state.CanNext,
        _ => state.CanToggle
    };

    private void Publish(bool forceImages = false)
    {
        if (_disposed || !_taskbarReady) return;
        try
        {
            if (!EnsureTaskbar()) return;
            var imagesChanged = EnsureIcons();
            var visible = _state.Enabled && _state.PlaybackId.HasValue;
            var buttons = stackalloc ThumbnailButton[3];
            buttons[0] = CreateButton(PreviousButtonId, _icons[0], "\u4e0a\u4e00\u96c6", visible, _state.CanPrevious);
            buttons[1] = CreateButton(ToggleButtonId, _icons[_state.IsPlaying ? 2 : 1],
                _state.IsPlaying ? "\u6682\u505c" : "\u64ad\u653e", visible, _state.CanToggle);
            buttons[2] = CreateButton(NextButtonId, _icons[3], "\u4e0b\u4e00\u96c6", visible, _state.CanNext);

            if (!_buttonsAdded || _publishedState != _state || imagesChanged || forceImages)
            {
                // ITaskbarList3 inherits IUnknown, ITaskbarList, and ITaskbarList2: Add/Update occupy slots 15/16.
                var update = (delegate* unmanaged[Stdcall]<nint, nint, uint, ThumbnailButton*, int>)GetMethod(_taskbar, _buttonsAdded ? 16 : 15);
                var result = update(_taskbar, _windowHandle, 3, buttons);
                if (!Succeeded(result)) { _publishedState = null; return; }
                _buttonsAdded = true;
                _publishedState = _state;
            }
            var overlayVisible = visible && _state.IsPlaying;
            if (_overlayVisible != overlayVisible || imagesChanged || forceImages)
            {
                if (!SetOverlay(overlayVisible ? _icons[4] : 0)) return;
                _overlayVisible = overlayVisible;
            }
            LastNativeError = null;
        }
        catch (Exception exception)
        {
            // The taskbar is optional integration. Native failures never alter playback or window availability.
            LastNativeError = exception.HResult;
            _publishedState = null;
        }
    }

    private bool EnsureTaskbar()
    {
        if (_taskbar != 0) return true;
        var classId = TaskbarClassId;
        var interfaceId = TaskbarInterfaceId;
        nint instance = 0;
        if (!Succeeded(CoCreateInstance(&classId, 0, 1, &interfaceId, &instance)) || instance == 0) return false;
        _taskbar = instance;
        var initialize = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(_taskbar, 3);
        if (Succeeded(initialize(_taskbar))) return true;
        ReleaseTaskbar();
        return false;
    }

    private static ThumbnailButton CreateButton(uint id, nint icon, string label, bool visible, bool enabled)
    {
        var button = new ThumbnailButton
        {
            Mask = ButtonMask, Id = id, Icon = icon,
            Flags = !visible ? ButtonHidden | ButtonDisabled : enabled ? 0 : ButtonDisabled
        };
        for (var index = 0; index < Math.Min(label.Length, 259); index++) button.Tooltip[index] = label[index];
        return button;
    }

    private bool SetOverlay(nint icon)
    {
        var setOverlay = (delegate* unmanaged[Stdcall]<nint, nint, nint, char*, int>)GetMethod(_taskbar, 18);
        fixed (char* description = "\u6b63\u5728\u64ad\u653e")
            return Succeeded(setOverlay(_taskbar, _windowHandle, icon, icon == 0 ? null : description));
    }

    private bool Succeeded(int result)
    {
        if (result >= 0) return true;
        LastNativeError = result;
        return false;
    }

    private bool EnsureIcons()
    {
        var dpi = GetDpiForWindow(_windowHandle);
        if (dpi == 0) dpi = 96;
        var iconSize = Math.Clamp(GetSystemMetricsForDpi(11, dpi), 16, 96);
        var overlaySize = Math.Clamp((int)Math.Round(16 * dpi / 96d), 16, 48);
        var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
        if (_icons[0] != 0 && _iconSize == iconSize && _overlaySize == overlaySize && _iconForeground == foreground) return false;
        var replacement = new nint[5];
        try
        {
            for (var index = 0; index < replacement.Length; index++)
            {
                replacement[index] = CreateGlyphIcon(index, index == 4 ? overlaySize : iconSize, foreground);
                if (replacement[index] == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
            for (var index = 0; index < replacement.Length; index++)
            {
                if (_icons[index] != 0) DestroyIcon(_icons[index]);
                _icons[index] = replacement[index];
                replacement[index] = 0;
            }
            _iconSize = iconSize;
            _overlaySize = overlaySize;
            _iconForeground = foreground;
            return true;
        }
        finally
        {
            foreach (var icon in replacement) if (icon != 0) DestroyIcon(icon);
        }
    }

    private static nint CreateGlyphIcon(int glyph, int size, Color foreground)
    {
        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32 }
        };
        nint pixels = 0;
        var colorBitmap = CreateDIBSection(0, &bitmapInfo, 0, &pixels, 0, 0);
        if (colorBitmap == 0) return 0;
        if (pixels == 0) { DeleteObject(colorBitmap); return 0; }
        nint maskBitmap = 0;
        try
        {
            var mask = new byte[((size + 15) / 16) * 2 * size];
            fixed (byte* maskBits = mask) maskBitmap = CreateBitmap(size, size, 1, 1, maskBits);
            if (maskBitmap == 0) return 0;
            var destination = (uint*)pixels;
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var red = 0;
                    var green = 0;
                    var blue = 0;
                    var alpha = 0;
                    for (var sampleY = 0; sampleY < 4; sampleY++)
                        for (var sampleX = 0; sampleX < 4; sampleX++)
                        {
                            var pointX = (x + (sampleX + .5) / 4) / size;
                            var pointY = (y + (sampleY + .5) / 4) / size;
                            var color = GlyphPixel(glyph, pointX, pointY, foreground);
                            red += color.R * color.A / 255;
                            green += color.G * color.A / 255;
                            blue += color.B * color.A / 255;
                            alpha += color.A;
                        }
                    destination[y * size + x] = (uint)((alpha / 16 << 24) | (red / 16 << 16) | (green / 16 << 8) | blue / 16);
                }
            var iconInfo = new IconInfo { IsIcon = 1, ColorBitmap = colorBitmap, MaskBitmap = maskBitmap };
            return CreateIconIndirect(&iconInfo);
        }
        finally
        {
            if (maskBitmap != 0) DeleteObject(maskBitmap);
            DeleteObject(colorBitmap);
        }
    }

    private static Color GlyphPixel(int glyph, double x, double y, Color foreground)
    {
        if (glyph == 4)
        {
            if (Math.Pow(x - .5, 2) + Math.Pow(y - .5, 2) > .46 * .46) return default;
            return InTriangle(x, y, .39, .74, .22)
                ? Color.FromArgb(255, 31, 22, 6) : Color.FromArgb(255, 226, 184, 107);
        }
        var inside = glyph switch
        {
            0 => InNextGlyph(1 - x, y),
            1 => InTriangle(x, y, .33, .77, .28),
            2 => y >= .24 && y <= .76 && (x >= .29 && x <= .43 || x >= .57 && x <= .71),
            _ => InNextGlyph(x, y)
        };
        return inside ? Color.FromArgb(255, foreground.R, foreground.G, foreground.B) : default;
    }

    private static bool InNextGlyph(double x, double y) =>
        InTriangle(x, y, .25, .64, .25) || x >= .68 && x <= .77 && y >= .25 && y <= .75;

    private static bool InTriangle(double x, double y, double left, double right, double halfHeight) =>
        x >= left && x <= right && Math.Abs(y - .5) <= halfHeight * (right - x) / (right - left);

    private static nint GetMethod(nint instance, int slot) => (*(nint**)instance)[slot];

    private void ReleaseTaskbar()
    {
        if (_taskbar == 0) return;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)GetMethod(_taskbar, 2);
        _ = release(_taskbar);
        _taskbar = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(Dispose);
            return;
        }
        _state = EmptyState;
        Publish();
        _disposed = true;
        _publishedState = null;
        _monitor.WindowMessageReceived -= WindowMessageReceived;
        _monitor.Dispose();
        ReleaseTaskbar();
        for (var index = 0; index < _icons.Length; index++)
        {
            if (_icons[index] != 0) DestroyIcon(_icons[index]);
            _icons[index] = 0;
        }
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailButton
    {
        public uint Mask;
        public uint Id;
        public uint BitmapIndex;
        public nint Icon;
        public fixed char Tooltip[260];
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint ColorTable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint XHotspot;
        public uint YHotspot;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* classId, nint outer, uint context, Guid* interfaceId, nint* instance);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true)]
    private static partial uint RegisterWindowMessage(char* name);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateDIBSection(nint deviceContext, BitmapInfo* bitmapInfo, uint usage, nint* pixels, nint section, uint offset);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, void* bits);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateIconIndirect(IconInfo* iconInfo);

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint value);
}
