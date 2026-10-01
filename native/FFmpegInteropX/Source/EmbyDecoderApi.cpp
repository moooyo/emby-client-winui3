#include "pch.h"
#include "EmbyDecoderApi.h"
#include "FFmpegMediaSource.h"
#include "MediaSourceConfig.h"

namespace
{
    template<typename TProjected>
    TProjected CopyProjectedObject(void* unknownAbi)
    {
        winrt::Windows::Foundation::IInspectable unknown{ nullptr };
        winrt::copy_from_abi(unknown, unknownAbi);
        return unknown.as<TProjected>();
    }
}

extern "C" uint32_t __cdecl EmbyDecoderApiVersion(void) noexcept
{
    return 1;
}

extern "C" HRESULT __cdecl EmbyConfigureDecoderApi(void* configIUnknown, int32_t api) noexcept
{
    if (!configIUnknown)
    {
        return E_POINTER;
    }
    if (api < static_cast<int32_t>(EmbyDecoderApi::Auto) || api > static_cast<int32_t>(EmbyDecoderApi::Software))
    {
        return E_INVALIDARG;
    }

    try
    {
        auto projected = CopyProjectedObject<winrt::FFmpegInteropX::MediaSourceConfig>(configIUnknown);
        if (winrt::get_class_name(projected) != L"FFmpegInteropX.MediaSourceConfig")
        {
            return E_NOINTERFACE;
        }
        auto config = winrt::get_self<winrt::FFmpegInteropX::implementation::MediaSourceConfig>(projected);
        config->RequestedDecoderApi.store(api);
        return S_OK;
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}

extern "C" HRESULT __cdecl EmbyGetDecoderObservation(void* sourceIUnknown, DecoderObservation* observation, uint32_t callerSize) noexcept
{
    if (!sourceIUnknown || !observation)
    {
        return E_POINTER;
    }
    if (callerSize < sizeof(DecoderObservation))
    {
        return E_INVALIDARG;
    }

    try
    {
        auto projected = CopyProjectedObject<winrt::FFmpegInteropX::FFmpegMediaSource>(sourceIUnknown);
        if (winrt::get_class_name(projected) != L"FFmpegInteropX.FFmpegMediaSource")
        {
            return E_NOINTERFACE;
        }
        auto source = winrt::get_self<winrt::FFmpegInteropX::implementation::FFmpegMediaSource>(projected);
        *observation = source->GetEmbyDecoderObservation();
        return S_OK;
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}
