#include "pch.h"
#include "PassthroughAudioSampleProvider.h"
#include "FFmpegReader.h"

PassthroughAudioSampleProvider::PassthroughAudioSampleProvider(std::shared_ptr<FFmpegReader> reader,
    AVFormatContext* formatContext, AVCodecContext* codecContext,
    winrt::FFmpegInteropX::MediaSourceConfig const& config, int streamIndex)
    : UncompressedAudioSampleProvider(reader, formatContext, codecContext, config, streamIndex)
{
    emptyObservation.Size = sizeof(AudioOutputObservation);
    emptyObservation.Version = 1;
    emptyObservation.StreamIndex = streamIndex;
    emptyObservation.State = static_cast<int32_t>(EmbyAudioOutputState::Pcm);
    emptyObservation.Reason = static_cast<int32_t>(EmbyAudioFallbackReason::PolicyOff);
}

PassthroughAudioSampleProvider::~PassthroughAudioSampleProvider()
{
    // Explicit source close drains the worker outside shared locks. Destruction
    // only requests cancellation; the worker owns its copied codec parameters.
    if (auto current = CopyOutput()) current->RequestStop();
}

std::shared_ptr<WasapiBitstreamOutput> PassthroughAudioSampleProvider::CopyOutput() const noexcept
{
    std::lock_guard<std::mutex> lock(outputMutex);
    return output;
}

HRESULT PassthroughAudioSampleProvider::TryOpenBitstream(int32_t mode)
{
    if (mode < 0 || mode > 2) return E_INVALIDARG;
    if (CopyOutput()) return E_UNEXPECTED;
    AVCodecParameters* parameters = avcodec_parameters_alloc();
    if (!parameters) return E_OUTOFMEMORY;
    auto result = avcodec_parameters_from_context(parameters, m_pAvCodecCtx);
    if (result < 0)
    {
        avcodec_parameters_free(&parameters);
        return E_FAIL;
    }
    std::shared_ptr<WasapiBitstreamOutput> candidate;
    try
    {
        std::weak_ptr<FFmpegReader> reader = m_pReader;
        candidate = std::make_shared<WasapiBitstreamOutput>(parameters, m_streamIndex,
            static_cast<EmbyAudioPassthroughMode>(mode), [reader]
            {
                if (auto current = reader.lock()) current->NotifyConsumerProgress();
            });
    }
    catch (...)
    {
        avcodec_parameters_free(&parameters);
        throw;
    }
    avcodec_parameters_free(&parameters);
    {
        std::lock_guard<std::mutex> lock(outputMutex);
        output = candidate;
    }
    auto hr = candidate->TryOpen();
    externallyRouted.store(hr == S_OK, std::memory_order_release);
    if (hr == S_OK) candidate->SetDelay(streamDelay.count());
    return hr;
}

bool PassthroughAudioSampleProvider::IsBitstreamActive() const noexcept
{
    auto current = CopyOutput();
    return externallyRouted.load(std::memory_order_acquire) && current && current->OwnsOutput();
}

AudioOutputObservation PassthroughAudioSampleProvider::GetAudioObservation() const noexcept
{
    if (auto current = CopyOutput()) return current->Snapshot();
    return emptyObservation;
}

void PassthroughAudioSampleProvider::BindPlaybackSession(winrt::Windows::Media::Playback::MediaPlaybackSession const& session) noexcept
{
    if (auto current = CopyOutput()) current->BindPlaybackSession(session);
}

void PassthroughAudioSampleProvider::PrepareSeek(int64_t targetTicks) noexcept
{
    if (auto current = CopyOutput()) current->PrepareSeek(targetTicks);
}

void PassthroughAudioSampleProvider::SuspendOutput() noexcept
{
    if (auto current = CopyOutput()) current->Suspend();
}

HRESULT PassthroughAudioSampleProvider::CloseOutput() noexcept
{
    // Once a video-only MSS exists, its caller must reopen the source for PCM.
    // Before MSS creation this restores the ordinary uncompressed provider.
    auto current = CopyOutput();
    auto result = current ? current->Close() : S_OK;
    externallyRouted.store(false, std::memory_order_release);
    return result;
}

bool PassthroughAudioSampleProvider::IsBufferFull()
{
    if (externallyRouted.load(std::memory_order_acquire))
    {
        auto current = CopyOutput();
        return current && current->IsSoftFull();
    }
    return UncompressedAudioSampleProvider::IsBufferFull();
}

void PassthroughAudioSampleProvider::NotifyEndOfStream() noexcept
{
    if (auto current = CopyOutput()) current->NotifyEndOfStream();
}

void PassthroughAudioSampleProvider::FailOutput(EmbyAudioFallbackReason reason) noexcept
{
    if (auto current = CopyOutput()) current->RequestFallback(reason);
}

void PassthroughAudioSampleProvider::QueuePacket(AVPacket* packet)
{
    if (!packet) return;
    if (!m_isEnabled)
    {
        av_packet_free(&packet);
        return;
    }
    if (!externallyRouted.load(std::memory_order_acquire))
    {
        UncompressedAudioSampleProvider::QueuePacket(packet);
        return;
    }
    auto current = CopyOutput();
    if (!current)
    {
        av_packet_free(&packet);
        return;
    }
    // Do not invent timestamps for an independent output. DTS is a valid
    // fallback only for packets whose source PTS is unavailable.
    auto pts = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
    auto ticks = pts == AV_NOPTS_VALUE ? INT64_MIN : LONGLONG(timeBaseFactor * pts) - m_startOffset;
    auto duration = packet->duration > 0 ? ConvertDuration(packet->duration).count() : 0;
    current->QueuePacket(packet, ticks, duration);
}

void PassthroughAudioSampleProvider::Flush(bool flushBuffers)
{
    UncompressedAudioSampleProvider::Flush(flushBuffers);
    if (auto current = CopyOutput()) current->Flush();
}

void PassthroughAudioSampleProvider::SetStreamDelay(winrt::Windows::Foundation::TimeSpan const& delay)
{
    UncompressedAudioSampleProvider::SetStreamDelay(delay);
    if (auto current = CopyOutput()) current->SetDelay(delay.count());
}

void PassthroughAudioSampleProvider::Detach()
{
    if (auto current = CopyOutput()) current->RequestStop();
    UncompressedAudioSampleProvider::Detach();
}
