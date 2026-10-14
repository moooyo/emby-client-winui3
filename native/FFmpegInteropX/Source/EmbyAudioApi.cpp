#include "pch.h"
#include "EmbyAudioApi.h"
#include "FFmpegMediaSource.h"

namespace
{
    winrt::FFmpegInteropX::FFmpegMediaSource CopyAudioSource(void* unknownAbi)
    {
        winrt::Windows::Foundation::IInspectable unknown{ nullptr };
        winrt::copy_from_abi(unknown, unknownAbi);
        auto source = unknown.as<winrt::FFmpegInteropX::FFmpegMediaSource>();
        if (winrt::get_class_name(source) != L"FFmpegInteropX.FFmpegMediaSource")
        {
            winrt::throw_hresult(E_NOINTERFACE);
        }
        return source;
    }
}

extern "C" uint32_t __cdecl EmbyAudioApiVersion(void) noexcept
{
    return 1;
}

extern "C" HRESULT __cdecl EmbyTryOpenBitstream(void* sourceIUnknown, int32_t mode, int32_t streamIndex) noexcept
{
    if (!sourceIUnknown) return E_POINTER;
    if (mode < 0 || mode > 2 || streamIndex < 0) return E_INVALIDARG;
    try
    {
        auto projected = CopyAudioSource(sourceIUnknown);
        auto source = winrt::get_self<winrt::FFmpegInteropX::implementation::FFmpegMediaSource>(projected);
        return source->TryOpenEmbyBitstream(mode, streamIndex);
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}

extern "C" HRESULT __cdecl EmbyGetAudioObservation(void* sourceIUnknown, AudioOutputObservation* observation, uint32_t callerSize) noexcept
{
    if (!sourceIUnknown || !observation) return E_POINTER;
    if (callerSize < sizeof(AudioOutputObservation)) return E_INVALIDARG;
    try
    {
        auto projected = CopyAudioSource(sourceIUnknown);
        auto source = winrt::get_self<winrt::FFmpegInteropX::implementation::FFmpegMediaSource>(projected);
        *observation = source->GetEmbyAudioObservation();
        return S_OK;
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}

extern "C" HRESULT __cdecl EmbySuspendAudioOutput(void* sourceIUnknown) noexcept
{
    if (!sourceIUnknown) return E_POINTER;
    try
    {
        auto projected = CopyAudioSource(sourceIUnknown);
        auto source = winrt::get_self<winrt::FFmpegInteropX::implementation::FFmpegMediaSource>(projected);
        source->SuspendEmbyAudioOutput();
        return S_OK;
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}

extern "C" HRESULT __cdecl EmbyCloseAudioOutput(void* sourceIUnknown) noexcept
{
    if (!sourceIUnknown) return E_POINTER;
    try
    {
        auto projected = CopyAudioSource(sourceIUnknown);
        auto source = winrt::get_self<winrt::FFmpegInteropX::implementation::FFmpegMediaSource>(projected);
        return source->CloseEmbyAudioOutput();
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}
