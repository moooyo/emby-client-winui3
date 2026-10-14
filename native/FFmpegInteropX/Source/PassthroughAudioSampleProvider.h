#pragma once

#include "UncompressedAudioSampleProvider.h"
#include "WasapiBitstreamOutput.h"
#include <atomic>
#include <mutex>

// Retains the existing decoder and PCM descriptor for fallback before MSS
// creation. An active bitstream source must omit all audio descriptors from MSS.
class PassthroughAudioSampleProvider final : public UncompressedAudioSampleProvider
{
public:
    PassthroughAudioSampleProvider(std::shared_ptr<FFmpegReader> reader,
        AVFormatContext* formatContext, AVCodecContext* codecContext,
        winrt::FFmpegInteropX::MediaSourceConfig const& config, int streamIndex);
    ~PassthroughAudioSampleProvider() override;

    HRESULT TryOpenBitstream(int32_t mode);
    bool IsBitstreamActive() const noexcept;
    AudioOutputObservation GetAudioObservation() const noexcept;
    void BindPlaybackSession(winrt::Windows::Media::Playback::MediaPlaybackSession const& session) noexcept;
    void PrepareSeek(int64_t targetTicks) noexcept;
    void SuspendOutput() noexcept;
    // Must run outside source and reader locks. Clears the early-opening gate.
    HRESULT CloseOutput() noexcept;
    bool IsBufferFull() override;
    void NotifyEndOfStream() noexcept override;
    void FailOutput(EmbyAudioFallbackReason reason) noexcept;
    void QueuePacket(AVPacket* packet) override;
    void Flush(bool flushBuffers) override;
    void SetStreamDelay(winrt::Windows::Foundation::TimeSpan const& delay) override;
    void Detach() override;

private:
    std::shared_ptr<WasapiBitstreamOutput> CopyOutput() const noexcept;
    mutable std::mutex outputMutex;
    std::shared_ptr<WasapiBitstreamOutput> output;
    std::atomic<bool> externallyRouted{ false };
    AudioOutputObservation emptyObservation{};
};
