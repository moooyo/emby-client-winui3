#pragma once

#include <stdint.h>
#include <wchar.h>
#include <unknwn.h>

// The observation describes an opened endpoint and submitted IEC 61937 data.
// A source codec or requested policy alone never establishes bitstream output.
#pragma pack(push, 8)
typedef struct AudioOutputObservation
{
    uint32_t Size;
    uint32_t Version;
    int32_t RequestedMode;
    int32_t State;
    int32_t Codec;
    int32_t Reason;
    int32_t StreamIndex;
    int32_t TransportSampleRate;
    uint32_t TransportChannels;
    uint32_t ContentSampleRate;
    uint32_t ContentChannels;
    uint32_t ActualFormatConfirmed;
    uint64_t Generation;
    uint64_t SubmittedFrameCount;
    int64_t DriftTicks;
    int64_t ClockPositionTicks;
    wchar_t EndpointName[128];
    wchar_t SourceCodecName[64];
    wchar_t OutputFormatName[64];
} AudioOutputObservation;
#pragma pack(pop)

#ifdef __cplusplus
enum class EmbyAudioPassthroughMode : int32_t
{
    Off = 0,
    DolbyDts = 1,
    All = 2,
};

enum class EmbyAudioOutputState : int32_t
{
    Off = 0,
    Pending = 1,
    Bitstream = 2,
    Pcm = 3,
    FallbackRequired = 4,
    Stopped = 5,
};

enum class EmbyAudioCodec : int32_t
{
    Unknown = 0,
    Ac3 = 1,
    Eac3 = 2,
    Dts = 3,
    TrueHd = 4,
    DtsHd = 5,
};

enum class EmbyAudioFallbackReason : int32_t
{
    None = 0,
    PolicyOff = 1,
    CodecUnsupported = 2,
    MetadataUnavailable = 3,
    EndpointUnavailable = 4,
    FormatUnsupported = 5,
    ExclusiveUnavailable = 6,
    MuxUnavailable = 7,
    MuxFailed = 8,
    QueueOverflow = 9,
    DeviceInvalidated = 10,
    ClockUnavailable = 11,
    RateUnsupported = 12,
    FormatChanged = 13,
    TimingDiscontinuity = 14,
    SourceRetired = 15,
    EndpointChanged = 16,
};

static_assert(sizeof(wchar_t) == 2, "The audio observation ABI requires UTF-16 characters.");
static_assert(sizeof(AudioOutputObservation) == 592, "The audio observation ABI must remain stable.");

#define EMBY_AUDIO_NOEXCEPT noexcept
extern "C"
{
#else
#define EMBY_AUDIO_NOEXCEPT
#endif
    __declspec(dllexport) uint32_t __cdecl EmbyAudioApiVersion(void) EMBY_AUDIO_NOEXCEPT;
    __declspec(dllexport) HRESULT __cdecl EmbyTryOpenBitstream(void* sourceIUnknown, int32_t mode, int32_t streamIndex) EMBY_AUDIO_NOEXCEPT;
    __declspec(dllexport) HRESULT __cdecl EmbyGetAudioObservation(void* sourceIUnknown, AudioOutputObservation* observation, uint32_t callerSize) EMBY_AUDIO_NOEXCEPT;
    __declspec(dllexport) HRESULT __cdecl EmbySuspendAudioOutput(void* sourceIUnknown) EMBY_AUDIO_NOEXCEPT;
    __declspec(dllexport) HRESULT __cdecl EmbyCloseAudioOutput(void* sourceIUnknown) EMBY_AUDIO_NOEXCEPT;
#ifdef __cplusplus
}
#endif
#undef EMBY_AUDIO_NOEXCEPT
