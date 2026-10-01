#pragma once

#include <stdint.h>
#include <wchar.h>
#include <unknwn.h>

#pragma pack(push, 8)
typedef struct DecoderObservation
{
    uint32_t Size;
    uint32_t Version;
    int32_t RequestedApi;
    int32_t ActualApi;
    int32_t FallbackReason;
    uint32_t VendorId;
    int64_t AdapterLuid;
    uint64_t DecodedFrameCount;
    uint64_t HardwareFrameCount;
    wchar_t GpuName[128];
    wchar_t CodecName[64];
} DecoderObservation;
#pragma pack(pop)

#ifdef __cplusplus
#include <memory>
#include <mutex>

enum class EmbyDecoderApi : int32_t
{
    Auto = 0,
    D3D11 = 1,
    IntelQsv = 2,
    AmdAmf = 3,
    NvidiaNvdec = 4,
    Software = 5,
};

enum class EmbyDecoderFallbackReason : int32_t
{
    None = 0,
    ApiUnavailable = 1,
    CodecUnsupported = 2,
    DeviceUnavailable = 3,
    InitializationFailed = 4,
    DecodeFailed = 5,
};

static_assert(sizeof(wchar_t) == 2, "The decoder observation ABI requires UTF-16 characters.");
static_assert(sizeof(DecoderObservation) == 432, "The decoder observation ABI must remain stable.");

namespace winrt::FFmpegInteropX::implementation
{
    struct EmbyDecoderState
    {
        EmbyDecoderState()
        {
            Observation.Size = sizeof(DecoderObservation);
            Observation.Version = 1;
            Observation.RequestedApi = static_cast<int32_t>(EmbyDecoderApi::Auto);
            Observation.ActualApi = -1;
            Observation.FallbackReason = static_cast<int32_t>(EmbyDecoderFallbackReason::None);
        }

        DecoderObservation Snapshot() const
        {
            std::lock_guard<std::mutex> lock(Mutex);
            return Observation;
        }

        mutable std::mutex Mutex;
        DecoderObservation Observation{};
    };
}

#define EMBY_DECODER_NOEXCEPT noexcept
extern "C"
{
#else
#define EMBY_DECODER_NOEXCEPT
#endif

    __declspec(dllexport) uint32_t __cdecl EmbyDecoderApiVersion(void) EMBY_DECODER_NOEXCEPT;
    __declspec(dllexport) HRESULT __cdecl EmbyConfigureDecoderApi(void* configIUnknown, int32_t api) EMBY_DECODER_NOEXCEPT;
    __declspec(dllexport) HRESULT __cdecl EmbyGetDecoderObservation(void* sourceIUnknown, DecoderObservation* observation, uint32_t callerSize) EMBY_DECODER_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#undef EMBY_DECODER_NOEXCEPT
