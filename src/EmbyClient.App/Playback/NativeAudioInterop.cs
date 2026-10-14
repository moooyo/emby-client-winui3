using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EmbyClient.Playback;
using FFmpegInteropX;
using WinRT;

namespace EmbyClient.App.Playback;

/// <summary>Reads real exclusive-output observations from the selected native demux provider.</summary>
internal static unsafe partial class NativeAudioInterop
{
    private const uint SupportedVersion = 1;
    private static readonly Lazy<bool> Availability = new(CheckAvailability);

    internal static bool IsAvailable => Availability.Value;

    internal static NativeAudioOpenResult TryOpenBitstream(FFmpegMediaSource source,
        AudioPassthroughMode mode, int streamIndex)
    {
        if (!IsAvailable) return new(false, "NativeAudioApiUnavailable");
        nint pointer = 0;
        var opened = false;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            var result = OpenBitstream(pointer, (int)mode, streamIndex);
            // Only S_FALSE guarantees that the failed native opening has finished restoring PCM.
            // A timeout or another failure may still own an exclusive-opening worker.
            if (result is not (0 or 1)) throw new PlaybackException("AudioOutputOpenFailed");
            opened = result == 0;
            var observation = ReadObservation(pointer);
            if (!opened)
                return new(false, observation?.FallbackReason ?? "AudioOutputOpenFailed", observation);
            if (observation is null || observation.RequestedMode != mode || observation.StreamIndex != streamIndex
                || observation.State is not (AudioOutputState.Pending or AudioOutputState.Bitstream)
                || observation.Generation == 0)
            {
                // Before CreateMediaPlaybackItem, closing restores the ordinary PCM provider and descriptor.
                if (CloseOutput(pointer) < 0) throw new PlaybackException("AudioOutputRollbackFailed");
                opened = false;
                return new(false, "AudioObservationUnavailable", observation);
            }
            return new(true, null, observation);
        }
        catch (Exception exception) when (IsInteropFailure(exception))
        {
            if (opened && pointer != 0)
            {
                var restored = false;
                try { restored = CloseOutput(pointer) >= 0; }
                catch (Exception closeException) when (IsInteropFailure(closeException)) { }
                if (!restored) throw new PlaybackException("AudioOutputRollbackFailed");
            }
            return new(false, "AudioOutputOpenFailed");
        }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    internal static NativeAudioObservation? Read(FFmpegMediaSource source)
    {
        if (!IsAvailable) return null;
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            return ReadObservation(pointer);
        }
        catch (Exception exception) when (IsInteropFailure(exception)) { return null; }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    internal static bool Suspend(FFmpegMediaSource source) => ControlOutput(source, suspend: true);
    internal static bool Close(FFmpegMediaSource source) => ControlOutput(source, suspend: false);

    private static bool ControlOutput(FFmpegMediaSource source, bool suspend)
    {
        if (!IsAvailable) return false;
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            return (suspend ? SuspendOutput(pointer) : CloseOutput(pointer)) >= 0;
        }
        catch (Exception exception) when (IsInteropFailure(exception)) { return false; }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    private static bool CheckAvailability()
    {
        try { return ApiVersion() == SupportedVersion; }
        catch (Exception exception) when (IsInteropFailure(exception)) { return false; }
    }

    private static NativeAudioObservation? ReadObservation(nint pointer)
    {
        var observation = new Observation { Size = (uint)sizeof(Observation), Version = SupportedVersion };
        if (GetObservation(pointer, &observation, (uint)sizeof(Observation)) < 0
            || observation.Size != sizeof(Observation) || observation.Version != SupportedVersion
            || observation.State is < 0 or > 5 || observation.RequestedMode is < 0 or > 2)
            return null;
        var codecName = ReadText(observation.SourceCodecName, 64);
        if (codecName.Length == 0)
            codecName = observation.Codec switch
            {
                1 => "AC3", 2 => "EAC3", 3 => "DTS", 4 => "TrueHD", 5 => "DTS-HD", _ => "Unknown"
            };
        return new((AudioPassthroughMode)observation.RequestedMode, (AudioOutputState)observation.State,
            observation.StreamIndex, codecName, OptionalText(observation.OutputFormatName, 64),
            OptionalText(observation.EndpointName, 128), observation.TransportSampleRate,
            observation.TransportChannels, observation.ContentSampleRate, observation.ContentChannels,
            observation.ActualFormatConfirmed != 0, observation.Generation, observation.SubmittedFrameCount,
            observation.DriftTicks, observation.ClockPositionTicks, ReasonName(observation.Reason));
    }

    private static string? ReasonName(int reason) => reason switch
    {
        0 => null, 1 => "PolicyOff", 2 => "CodecUnsupported", 3 => "MetadataUnavailable",
        4 => "EndpointUnavailable", 5 => "FormatUnsupported", 6 => "ExclusiveUnavailable",
        7 => "MuxUnavailable", 8 => "MuxFailed", 9 => "QueueOverflow", 10 => "DeviceInvalidated",
        11 => "ClockUnavailable", 12 => "RateUnsupported", 13 => "FormatChanged",
        14 => "TimingDiscontinuity", 15 => "SourceRetired", 16 => "EndpointChanged",
        _ => "AudioOutputFailure"
    };

    private static string? OptionalText(char* value, int capacity)
    {
        var text = ReadText(value, capacity);
        return text.Length == 0 ? null : text;
    }

    private static string ReadText(char* value, int capacity)
    {
        var length = 0;
        while (length < capacity && value[length] != '\0') length++;
        return new string(value, 0, length);
    }

    private static bool IsInteropFailure(Exception exception) => exception is DllNotFoundException
        or EntryPointNotFoundException or BadImageFormatException or COMException or ObjectDisposedException;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct Observation
    {
        public uint Size;
        public uint Version;
        public int RequestedMode;
        public int State;
        public int Codec;
        public int Reason;
        public int StreamIndex;
        public int TransportSampleRate;
        public uint TransportChannels;
        public uint ContentSampleRate;
        public uint ContentChannels;
        public uint ActualFormatConfirmed;
        public ulong Generation;
        public ulong SubmittedFrameCount;
        public long DriftTicks;
        public long ClockPositionTicks;
        public fixed char EndpointName[128];
        public fixed char SourceCodecName[64];
        public fixed char OutputFormatName[64];
    }

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyAudioApiVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint ApiVersion();

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyTryOpenBitstream")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int OpenBitstream(nint source, int mode, int streamIndex);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyGetAudioObservation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetObservation(nint source, Observation* observation, uint size);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbySuspendAudioOutput")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int SuspendOutput(nint source);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyCloseAudioOutput")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int CloseOutput(nint source);
}

internal sealed record NativeAudioOpenResult(bool ExclusiveOpened, string? FallbackReason,
    NativeAudioObservation? Observation = null);

internal sealed record NativeAudioObservation(AudioPassthroughMode RequestedMode, AudioOutputState State,
    int StreamIndex, string SourceCodecName, string? OutputFormatName, string? EndpointName,
    int TransportSampleRate, uint TransportChannels, uint ContentSampleRate, uint ContentChannels,
    bool ActualFormatConfirmed, ulong Generation, ulong SubmittedFrameCount,
    long DriftTicks, long ClockPositionTicks, string? FallbackReason);
