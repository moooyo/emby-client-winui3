#pragma once

#include "EmbyAudioApi.h"
#include <functional>
#include <memory>
#include <winrt/Windows.Media.Playback.h>

extern "C"
{
#include <libavcodec/codec_par.h>
#include <libavcodec/packet.h>
}

// Consumes packets already demuxed by FFmpegReader. The worker never reads or
// seeks the demuxer. MediaPlaybackSession is the only playback transport clock.
class WasapiBitstreamOutput final
{
public:
    WasapiBitstreamOutput(const AVCodecParameters* parameters, int32_t streamIndex,
        EmbyAudioPassthroughMode mode, std::function<void()> consumerWake = {});
    ~WasapiBitstreamOutput();
    WasapiBitstreamOutput(const WasapiBitstreamOutput&) = delete;
    WasapiBitstreamOutput& operator=(const WasapiBitstreamOutput&) = delete;

    // S_OK means the exact encoded format has been initialized in exclusive
    // mode. The observation remains Pending until a real burst is submitted.
    HRESULT TryOpen() noexcept;
    bool OwnsOutput() const noexcept;

    // Takes packet ownership on every path, including rejection and shutdown.
    void QueuePacket(AVPacket* packet, int64_t presentationTicks, int64_t durationTicks) noexcept;
    bool IsSoftFull() const noexcept;
    void NotifyEndOfStream() noexcept;
    void RequestFallback(EmbyAudioFallbackReason reason) noexcept;
    void BindPlaybackSession(winrt::Windows::Media::Playback::MediaPlaybackSession const& session) noexcept;
    void PrepareSeek(int64_t targetTicks) noexcept;
    void Flush() noexcept;
    void Suspend() noexcept;
    void RequestStop() noexcept;
    // Call only after releasing source and demuxer locks.
    HRESULT Close() noexcept;
    void SetDelay(int64_t delayTicks) noexcept;
    AudioOutputObservation Snapshot() const noexcept;

private:
    struct State;
    std::shared_ptr<State> state;
};
