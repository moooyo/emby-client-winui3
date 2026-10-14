#pragma once

#include "SubtitleProvider.h"
#include "AssRenderer.h"
#include <atomic>
#include <functional>
#include <map>
#include <memory>
#include <span>
#include <string>
#include <vector>

struct AssSubtitleObservation;
struct AssSubtitleFrame;

namespace EmbyNativeSubtitles
{
    // Actual video samples schedule clock cues. One image is owned until its content or lifetime changes.
    class LibassSubtitleTrack final : public std::enable_shared_from_this<LibassSubtitleTrack>
    {
    public:
        static std::shared_ptr<LibassSubtitleTrack> CreateDocument(std::span<const uint8_t> utf8,
            winrt::hstring const& label, int64_t timelineOffsetTicks, int64_t delayTicks,
            std::span<const AssFontAttachment> fonts);
        static std::shared_ptr<LibassSubtitleTrack> CreateEmbedded(
            winrt::Windows::Media::Core::TimedMetadataTrack const& track,
            std::span<const uint8_t> codecPrivate, std::span<const AssFontAttachment> fonts);

        ~LibassSubtitleTrack();
        winrt::Windows::Media::Core::TimedMetadataTrack Track() const;
        uint32_t EventCount() const;
        void SetFailureReporter(std::function<void(HRESULT)> reporter);
        void SetFrameReporter(std::function<void()> reporter);
        void AttachPlaybackItem(winrt::Windows::Media::Playback::MediaPlaybackItem const& item);
        void SetEnabled(bool enabled);
        void SetDelay(int64_t delayTicks);
        void FeedEvent(std::span<const char> event, int64_t startTicks, int64_t durationTicks);
        void Schedule(int64_t positionTicks, int64_t durationTicks, AssVideoGeometry const& geometry);
        void RenderImmediate(int64_t positionTicks, int64_t durationTicks, AssVideoGeometry const& geometry);
        void FlushForSeek();
        void Close() noexcept;
        void MarkFailed(HRESULT failure) noexcept;
        HRESULT Failure() const noexcept;
        void AppendObservation(AssSubtitleObservation& observation) const;
        bool ReadFrame(AssSubtitleFrame& frame, std::atomic<uint64_t> const& sequence,
            uint64_t expectedSequence, winrt::Windows::Graphics::Imaging::SoftwareBitmap* snapshot) const;

    private:
        struct ScheduledFrame
        {
            uint64_t Generation;
            int64_t PositionTicks;
            int64_t DurationTicks;
            AssVideoGeometry Geometry;
            winrt::Windows::Media::Core::IMediaCue Reference{ nullptr };
        };

        explicit LibassSubtitleTrack(winrt::Windows::Media::Core::TimedMetadataTrack const& track,
            int64_t timelineOffsetTicks, int64_t delayTicks, bool embedded);
        void SubscribeTrack();
        void EnsureOpen() const;
        void ActivateReferenceTrack();
        void ClearFrames(bool clearPending);
        void PublishFrameChange();
        void RenderImage(ScheduledFrame const& frame);
        void OnReferenceCueEntered(winrt::Windows::Media::Core::TimedMetadataTrack const&,
            winrt::Windows::Media::Core::MediaCueEventArgs const& args);
        void OnImageCueEntered(winrt::Windows::Media::Core::TimedMetadataTrack const&,
            winrt::Windows::Media::Core::MediaCueEventArgs const& args);
        void OnImageCueExited(winrt::Windows::Media::Core::TimedMetadataTrack const&,
            winrt::Windows::Media::Core::MediaCueEventArgs const& args);
        void DetachPlaybackItem() noexcept;

        static constexpr size_t MaximumScheduledFrames = 1024;
        mutable std::recursive_mutex m_mutex;
        AssRenderer m_renderer;
        winrt::Windows::Media::Core::TimedMetadataTrack m_track{ nullptr };
        winrt::Windows::Media::Core::TimedMetadataTrack m_referenceTrack{ nullptr };
        winrt::weak_ref<winrt::Windows::Media::Playback::MediaPlaybackItem> m_item;
        winrt::event_token m_referenceEnteredToken{};
        winrt::event_token m_imageEnteredToken{};
        winrt::event_token m_imageExitedToken{};
        winrt::event_token m_tracksChangedToken{};
        std::map<std::wstring, ScheduledFrame> m_scheduled;
        winrt::Windows::Media::Core::ImageCue m_currentImage{ nullptr };
        winrt::Windows::Media::Core::TimedTextPoint m_currentImagePosition{};
        winrt::Windows::Media::Core::TimedTextSize m_currentImageExtent{};
        winrt::Windows::Graphics::Imaging::SoftwareBitmap m_lastBitmap{ nullptr };
        int m_lastX = 0, m_lastY = 0, m_lastWidth = 0, m_lastHeight = 0;
        AssVideoGeometry m_lastFrameGeometry;
        int64_t m_lastRenderedMediaPositionTicks = -1;
        int64_t m_lastRenderedScriptPositionTicks = -1;
        uint64_t m_trackIdentity = 0;
        int64_t m_timelineOffsetTicks;
        int64_t m_delayTicks;
        uint64_t m_generation = 1;
        uint64_t m_nextFrame = 1;
        uint64_t m_scheduledFrameCount = 0;
        uint64_t m_referenceCueEnteredCount = 0;
        uint64_t m_renderCallCount = 0;
        uint64_t m_imageFrameCount = 0;
        uint64_t m_imageCueAddedCount = 0;
        uint64_t m_imageCueEnteredCount = 0;
        uint64_t m_imageCueExitedCount = 0;
        uint64_t m_clearCount = 0;
        uint64_t m_staleFrameCount = 0;
        uint64_t m_lastBitmapNonzeroAlphaPixels = 0;
        bool m_embedded;
        bool m_enabled = false;
        bool m_closed = false;
        std::atomic<HRESULT> m_failure{ S_OK };
        std::function<void(HRESULT)> m_failureReporter;
        std::function<void()> m_frameReporter;
    };
}

// The native demuxer still owns decoding and time-base conversion; libass owns all script semantics.
class SubtitleProviderLibass final : public SubtitleProvider
{
public:
    SubtitleProviderLibass(std::shared_ptr<FFmpegReader> reader, AVFormatContext* format,
        AVCodecContext* codec, winrt::FFmpegInteropX::MediaSourceConfig const& config,
        int index);
    void SetSourceFonts(std::shared_ptr<const std::vector<EmbyNativeSubtitles::AssFontAttachment>> fonts);
    HRESULT Initialize() override;
    void QueuePacket(AVPacket* packet) override;
    void SetStreamDelay(winrt::Windows::Foundation::TimeSpan const& delay) override;
    void EnableStream() override;
    void DisableStream() override;
    void Flush(bool flushBuffers) override;
    winrt::Windows::Media::Core::IMediaCue CreateCue(AVPacket*,
        winrt::Windows::Foundation::TimeSpan*, winrt::Windows::Foundation::TimeSpan*) override;
    std::shared_ptr<EmbyNativeSubtitles::LibassSubtitleTrack> LibassTrack() const noexcept;

private:
    std::shared_ptr<const std::vector<EmbyNativeSubtitles::AssFontAttachment>> m_sourceFonts;
    std::shared_ptr<EmbyNativeSubtitles::LibassSubtitleTrack> m_libassTrack;
};
