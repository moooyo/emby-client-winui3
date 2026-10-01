using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpegInteropX;
using WinRT;

namespace EmbyClient.App.Playback;

/// <summary>Session-scoped decoder selection and actual frame observations from the native decoder extension.</summary>
internal static unsafe partial class VendorDecoderInterop
{
    internal static bool TryConfigure(MediaSourceConfig config, string api)
    {
        nint pointer = 0;
        try
        {
            if (ApiVersion() != 1) return false;
            pointer = MarshalInspectable<MediaSourceConfig>.FromManaged(config);
            return Configure(pointer, ApiId(api)) >= 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
        finally
        {
            if (pointer != 0) MarshalInspectable<MediaSourceConfig>.DisposeAbi(pointer);
        }
    }

    internal static VendorDecoderObservation? Read(FFmpegMediaSource source)
    {
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            var observation = new Observation { Size = (uint)sizeof(Observation), Version = 1 };
            if (GetObservation(pointer, &observation, (uint)sizeof(Observation)) < 0
                || observation.Size != sizeof(Observation) || observation.Version != 1
                || observation.DecodedFrameCount == 0) return null;
            var actual = ApiName(observation.ActualApi);
            var name = ReadText(observation.GpuName, 128);
            var codec = ReadText(observation.CodecName, 64);
            return new(actual, FallbackName(observation.FallbackReason), codec,
                name.Length == 0 ? null : name, observation.VendorId == 0 ? null : observation.VendorId,
                observation.AdapterLuid == 0 ? null : observation.AdapterLuid,
                observation.DecodedFrameCount, observation.HardwareFrameCount);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    internal static bool IsKnownApi(string? api) => api is "Auto" or "D3D11" or "IntelQsv" or "AmdAmf" or "NvidiaNvdec" or "Software";
    internal static bool IsVendorApi(string api) => api is "IntelQsv" or "AmdAmf" or "NvidiaNvdec";

    private static int ApiId(string api) => api switch
    {
        "Auto" => 0, "D3D11" => 1, "IntelQsv" => 2, "AmdAmf" => 3, "NvidiaNvdec" => 4, "Software" => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(api))
    };

    private static string ApiName(int api) => api switch
    {
        1 => "D3D11", 2 => "IntelQsv", 3 => "AmdAmf", 4 => "NvidiaNvdec", 5 => "Software", _ => "Pending"
    };

    private static string? FallbackName(int reason) => reason switch
    {
        0 => null, 1 => "DecoderApiUnavailable", 2 => "CodecUnsupported", 3 => "DeviceUnavailable",
        4 => "DecoderInitializationFailed", 5 => "DecoderRuntimeFailed", _ => "DecoderInitializationFailed"
    };

    private static string ReadText(char* value, int capacity)
    {
        var length = 0;
        while (length < capacity && value[length] != '\0') length++;
        return new string(value, 0, length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Observation
    {
        public uint Size;
        public uint Version;
        public int RequestedApi;
        public int ActualApi;
        public int FallbackReason;
        public uint VendorId;
        public long AdapterLuid;
        public ulong DecodedFrameCount;
        public ulong HardwareFrameCount;
        public fixed char GpuName[128];
        public fixed char CodecName[64];
    }

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyDecoderApiVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint ApiVersion();

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyConfigureDecoderApi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Configure(nint config, int api);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyGetDecoderObservation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetObservation(nint source, Observation* observation, uint size);
}

internal sealed record VendorDecoderObservation(string ActualApi, string? FallbackReason, string CodecName,
    string? GpuName, uint? VendorId, long? AdapterLuid, ulong DecodedFrameCount, ulong HardwareFrameCount);
