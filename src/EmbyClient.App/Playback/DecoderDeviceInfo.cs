using System.Runtime.InteropServices;
using Windows.Media.Core;
using WinRT;

namespace EmbyClient.App.Playback;

/// <summary>Identifies the DXGI adapter attached to the actual media decoding device.</summary>
internal sealed record DecoderDeviceInfo(string Name, uint VendorId, long AdapterLuid)
{
    private static readonly Guid DeviceManagerSourceId = new("20bc074b-7a8d-4609-8c3b-64a0a3b5d7ce");
    private static readonly Guid D3D11DeviceId = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>
    /// Reads the device supplied to MediaStreamSource by Media Foundation after playback starts.
    /// The result is unavailable before device initialization or when that source has no DXGI device.
    /// </summary>
    public static unsafe DecoderDeviceInfo? Read(MediaStreamSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        nint sourcePointer = 0;
        nint managerSource = 0;
        nint manager = 0;
        nint device = 0;
        nint dxgiDevice = 0;
        nint adapter = 0;
        nint deviceHandle = 0;
        try
        {
            // CsWinRT unwraps the projected object; built-in COM marshalling is unavailable in NativeAOT.
            sourcePointer = MarshalInspectable<MediaStreamSource>.FromManaged(source);
            if (sourcePointer == 0 || QueryInterface(sourcePointer, DeviceManagerSourceId, out managerSource) < 0
                || managerSource == 0)
                return null;

            var getManager = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(managerSource, 3);
            if (getManager(managerSource, &manager) < 0 || manager == 0) return null;

            var openHandle = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(manager, 6);
            if (openHandle(manager, &deviceHandle) < 0 || deviceHandle == 0) return null;

            // GetVideoService returns the device without acquiring a LockDevice lock.
            var getVideoService = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)GetMethod(manager, 4);
            var deviceId = D3D11DeviceId;
            if (getVideoService(manager, deviceHandle, &deviceId, &device) < 0 || device == 0)
                return null;
            if (QueryInterface(device, DxgiDeviceId, out dxgiDevice) < 0 || dxgiDevice == 0)
                return null;

            var getAdapter = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(dxgiDevice, 7);
            if (getAdapter(dxgiDevice, &adapter) < 0 || adapter == 0) return null;

            var description = default(AdapterDescription);
            var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)GetMethod(adapter, 8);
            if (getDescription(adapter, &description) < 0) return null;

            var length = 0;
            while (length < 128 && description.Description[length] != '\0') length++;
            var name = new string(description.Description, 0, length).Trim();
            if (name.Length == 0) return null;
            var luid = ((long)description.LuidHighPart << 32) | description.LuidLowPart;
            return new DecoderDeviceInfo(name, description.VendorId, luid);
        }
        catch (Exception)
        {
            // Diagnostic discovery must never interrupt playback when a native device is unavailable.
            return null;
        }
        finally
        {
            Release(adapter);
            Release(dxgiDevice);
            Release(device);
            if (manager != 0 && deviceHandle != 0)
            {
                var closeHandle = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetMethod(manager, 3);
                _ = closeHandle(manager, deviceHandle);
            }
            Release(manager);
            Release(managerSource);
            Release(sourcePointer);
        }
    }

    private static unsafe int QueryInterface(nint instance, Guid interfaceId, out nint result)
    {
        nint queried = 0;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetMethod(instance, 0);
        var status = queryInterface(instance, &interfaceId, &queried);
        result = queried;
        return status;
    }

    private static unsafe nint GetMethod(nint instance, int slot) => (*(nint**)instance)[slot];

    private static unsafe void Release(nint instance)
    {
        if (instance == 0) return;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)GetMethod(instance, 2);
        _ = release(instance);
    }

    // DXGI_ADAPTER_DESC uses pointer-sized memory fields and a signed high LUID part.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
    }
}
