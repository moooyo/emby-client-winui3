#include "pch.h"
#include "SubtitleProviderLibass.h"
#include "EmbySubtitleApi.h"
#include "ReferenceCue.h"
#include <algorithm>
#include <cstring>
#include <limits>
#include <utility>

using namespace winrt::Windows::Media::Core;
using namespace winrt::Windows::Media::Playback;
using namespace winrt::Windows::Graphics::Imaging;
using namespace winrt::Windows::Foundation;

namespace EmbyNativeSubtitles
{
    namespace
    {
        // Use the existing subtitle provider's infinite-cue sentinel. Explicit ownership, not
        // a guessed frame tolerance, ends this cue on the next image change or invalidation.
        constexpr int64_t InfiniteCueDurationTicks = 0xFFFF'FFFFLL * 10'000;
        std::atomic<uint64_t> NextTrackIdentity{ 1 };

        int64_t AddTicks(int64_t left, int64_t right)
        {
            if ((right > 0 && left > (std::numeric_limits<int64_t>::max)() - right)
                || (right < 0 && left < (std::numeric_limits<int64_t>::min)() - right))
                winrt::throw_hresult(E_INVALIDARG);
            return left + right;
        }

        void ValidateDelay(int64_t delayTicks)
        {
            // The player exposes a bounded real timing adjustment, not a new media time base.
            constexpr int64_t maximumDelay = 24LL * 60 * 60 * 10'000'000;
            if (delayTicks < -maximumDelay || delayTicks > maximumDelay) winrt::throw_hresult(E_INVALIDARG);
        }

        uint64_t NonzeroBitmapAlphaPixels(SoftwareBitmap const& bitmap)
        {
            if (!bitmap) winrt::throw_hresult(E_UNEXPECTED);
            const auto width = bitmap.PixelWidth();
            const auto height = bitmap.PixelHeight();
            if (bitmap.BitmapPixelFormat() != BitmapPixelFormat::Bgra8
                || bitmap.BitmapAlphaMode() != BitmapAlphaMode::Premultiplied
                || width <= 0 || height <= 0)
                winrt::throw_hresult(E_UNEXPECTED);
            auto buffer = bitmap.LockBuffer(BitmapBufferAccessMode::Read);
            auto reference = buffer.CreateReference();
            auto pixels = reference.data();
            const auto plane = buffer.GetPlaneDescription(0);
            const auto rowBytes = static_cast<size_t>(width) * 4;
            if (plane.StartIndex < 0 || plane.Stride < 0 || static_cast<size_t>(plane.Stride) < rowBytes)
                winrt::throw_hresult(E_UNEXPECTED);
            uint64_t result = 0;
            for (int row = 0; row < height; ++row)
            {
                auto input = pixels + plane.StartIndex + static_cast<size_t>(row) * plane.Stride;
                for (int column = 0; column < width; ++column)
                    if (input[static_cast<size_t>(column) * 4 + 3] != 0) ++result;
            }
            return result;
        }
    }

    LibassSubtitleTrack::LibassSubtitleTrack(TimedMetadataTrack const& track,
        int64_t timelineOffsetTicks, int64_t delayTicks, bool embedded)
        : m_track(track), m_trackIdentity(NextTrackIdentity.fetch_add(1)),
        m_timelineOffsetTicks(timelineOffsetTicks), m_delayTicks(delayTicks), m_embedded(embedded)
    {
        if (!track || track.TimedMetadataKind() != TimedMetadataKind::ImageSubtitle || timelineOffsetTicks < 0)
            winrt::throw_hresult(E_INVALIDARG);
        ValidateDelay(delayTicks);
        if (m_trackIdentity == 0) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
    }

    std::shared_ptr<LibassSubtitleTrack> LibassSubtitleTrack::CreateDocument(std::span<const uint8_t> utf8,
        winrt::hstring const& label, int64_t timelineOffsetTicks, int64_t delayTicks,
        std::span<const AssFontAttachment> fonts)
    {
        auto track = TimedMetadataTrack(L"LibassDocument", L"", TimedMetadataKind::ImageSubtitle);
        track.Label(label);
        auto result = std::shared_ptr<LibassSubtitleTrack>(new LibassSubtitleTrack(track,
            timelineOffsetTicks, delayTicks, false));
        for (auto const& font : fonts) result->m_renderer.AddFont(font);
        result->m_renderer.LoadDocument(utf8);
        result->SubscribeTrack();
        return result;
    }

    std::shared_ptr<LibassSubtitleTrack> LibassSubtitleTrack::CreateEmbedded(TimedMetadataTrack const& track,
        std::span<const uint8_t> codecPrivate, std::span<const AssFontAttachment> fonts)
    {
        auto result = std::shared_ptr<LibassSubtitleTrack>(new LibassSubtitleTrack(track, 0, 0, true));
        for (auto const& font : fonts) result->m_renderer.AddFont(font);
        result->m_renderer.InitializeEmbedded(codecPrivate);
        result->SubscribeTrack();
        return result;
    }

    LibassSubtitleTrack::~LibassSubtitleTrack() { Close(); }

    void LibassSubtitleTrack::SubscribeTrack()
    {
        auto weak = weak_from_this();
        m_imageEnteredToken = m_track.CueEntered([weak](auto const& sender, auto const& args)
        {
            if (auto self = weak.lock()) self->OnImageCueEntered(sender, args);
        });
        m_imageExitedToken = m_track.CueExited([weak](auto const& sender, auto const& args)
        {
            if (auto self = weak.lock()) self->OnImageCueExited(sender, args);
        });
    }

    TimedMetadataTrack LibassSubtitleTrack::Track() const
    {
        std::lock_guard lock(m_mutex);
        return m_track;
    }

    uint32_t LibassSubtitleTrack::EventCount() const
    {
        std::lock_guard lock(m_mutex);
        return m_renderer.EventCount();
    }

    void LibassSubtitleTrack::EnsureOpen() const
    {
        if (m_closed) winrt::throw_hresult(RO_E_CLOSED);
        if (const auto failure = m_failure.load(); FAILED(failure)) winrt::throw_hresult(failure);
    }

    void LibassSubtitleTrack::SetFailureReporter(std::function<void(HRESULT)> reporter)
    {
        std::lock_guard lock(m_mutex);
        m_failureReporter = std::move(reporter);
    }

    void LibassSubtitleTrack::SetFrameReporter(std::function<void()> reporter)
    {
        std::lock_guard lock(m_mutex);
        m_frameReporter = std::move(reporter);
    }

    void LibassSubtitleTrack::AttachPlaybackItem(MediaPlaybackItem const& item)
    {
        std::lock_guard lock(m_mutex);
        if (m_closed) winrt::throw_hresult(RO_E_CLOSED);
        if (m_item.get() == item) return;
        DetachPlaybackItem();
        if (!item) return;
        m_item = item;
        GUID id{};
        winrt::check_hresult(CoCreateGuid(&id));
        m_referenceTrack = TimedMetadataTrack(L"LibassClock_" + winrt::to_hstring(winrt::guid(id)),
            L"", TimedMetadataKind::Custom);
        auto weak = weak_from_this();
        m_referenceEnteredToken = m_referenceTrack.CueEntered([weak](auto const& sender, auto const& args)
        {
            if (auto self = weak.lock()) self->OnReferenceCueEntered(sender, args);
        });
        m_tracksChangedToken = item.TimedMetadataTracksChanged([weak](auto const&, auto const&)
        {
            if (auto self = weak.lock())
            {
                std::lock_guard callbackLock(self->m_mutex);
                if (!self->m_closed) self->ActivateReferenceTrack();
            }
        });
        item.Source().ExternalTimedMetadataTracks().Append(m_referenceTrack);
        ActivateReferenceTrack();
    }

    void LibassSubtitleTrack::ActivateReferenceTrack()
    {
        auto item = m_item.get();
        if (!item || !m_referenceTrack || m_closed) return;
        auto tracks = item.TimedMetadataTracks();
        for (uint32_t index = 0; index < tracks.Size(); ++index)
        {
            if (tracks.GetAt(index) == m_referenceTrack)
            {
                if (tracks.GetPresentationMode(index) != TimedMetadataTrackPresentationMode::Hidden)
                    tracks.SetPresentationMode(index, TimedMetadataTrackPresentationMode::Hidden);
                break;
            }
        }
    }

    void LibassSubtitleTrack::SetEnabled(bool enabled)
    {
        HRESULT failure = S_OK;
        std::function<void(HRESULT)> reporter;
        {
            std::lock_guard lock(m_mutex);
            if (m_closed)
            {
                if (!enabled) return;
                winrt::throw_hresult(RO_E_CLOSED);
            }
            failure = enabled ? m_failure.load() : S_OK;
            if (FAILED(failure)) reporter = m_failureReporter;
            else
            {
                if (m_enabled == enabled)
                {
                    if (enabled) ActivateReferenceTrack();
                    return;
                }
                m_enabled = enabled;
                ++m_generation;
                ClearFrames(true);
                m_renderer.InvalidateImage();
                if (enabled) ActivateReferenceTrack();
                return;
            }
        }
        // A disabled malformed track never interrupts a movie; selecting it reports a real failure.
        if (reporter) reporter(failure);
        winrt::throw_hresult(failure);
    }

    void LibassSubtitleTrack::SetDelay(int64_t delayTicks)
    {
        ValidateDelay(delayTicks);
        std::lock_guard lock(m_mutex);
        EnsureOpen();
        if (m_delayTicks == delayTicks) return;
        std::vector<ScheduledFrame> future;
        future.reserve(m_scheduled.size());
        for (auto const& entry : m_scheduled) future.push_back(entry.second);
        std::sort(future.begin(), future.end(), [](auto const& left, auto const& right)
        {
            return left.PositionTicks < right.PositionTicks;
        });
        m_delayTicks = delayTicks;
        ++m_generation;
        ClearFrames(true);
        m_renderer.InvalidateImage();
        // Buffered video samples still contain real timestamps after a timing edit.
        for (auto const& frame : future) Schedule(frame.PositionTicks, frame.DurationTicks, frame.Geometry);
    }

    void LibassSubtitleTrack::FeedEvent(std::span<const char> event, int64_t startTicks, int64_t durationTicks)
    {
        std::lock_guard lock(m_mutex);
        EnsureOpen();
        m_renderer.FeedEvent(event, startTicks, durationTicks);
    }

    void LibassSubtitleTrack::Schedule(int64_t positionTicks, int64_t durationTicks, AssVideoGeometry const& geometry)
    {
        std::lock_guard lock(m_mutex);
        if (!m_enabled || positionTicks < 0) return;
        EnsureOpen();
        if (durationTicks <= 0 || positionTicks > (std::numeric_limits<int64_t>::max)() - durationTicks)
            winrt::throw_hresult(E_INVALIDARG);
        if (!m_referenceTrack) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        if (m_scheduled.size() >= MaximumScheduledFrames) winrt::throw_hresult(E_OUTOFMEMORY);
        const auto id = winrt::to_hstring(m_generation) + L":" + winrt::to_hstring(m_nextFrame++);
        auto reference = winrt::make<ReferenceCue>(TimeSpan{ positionTicks }, TimeSpan{ durationTicks }, nullptr);
        reference.Id(id);
        ScheduledFrame frame{ m_generation, positionTicks, durationTicks, geometry, reference };
        auto key = std::wstring(id.data(), id.size());
        m_scheduled.emplace(key, frame);
        try { m_referenceTrack.AddCue(reference); ++m_scheduledFrameCount; }
        catch (...) { m_scheduled.erase(key); throw; }
    }

    void LibassSubtitleTrack::RenderImmediate(int64_t positionTicks, int64_t durationTicks,
        AssVideoGeometry const& geometry)
    {
        std::lock_guard lock(m_mutex);
        if (!m_enabled) return;
        EnsureOpen();
        if (positionTicks < 0 || durationTicks <= 0
            || positionTicks > (std::numeric_limits<int64_t>::max)() - durationTicks)
            winrt::throw_hresult(E_INVALIDARG);
        RenderImage(ScheduledFrame{ m_generation, positionTicks, durationTicks, geometry, nullptr });
    }

    void LibassSubtitleTrack::RenderImage(ScheduledFrame const& frame)
    {
        if (m_closed || !m_enabled || frame.Generation != m_generation) return;
        if (frame.PositionTicks < m_lastRenderedMediaPositionTicks)
        {
            // A paused refresh can precede callbacks for older buffered samples in this generation.
            ++m_staleFrameCount;
            return;
        }
        const auto sourcePosition = AddTicks(AddTicks(frame.PositionTicks, m_timelineOffsetTicks), -m_delayTicks);
        m_lastFrameGeometry = frame.Geometry;
        m_lastRenderedMediaPositionTicks = frame.PositionTicks;
        m_lastRenderedScriptPositionTicks = sourcePosition;
        ++m_renderCallCount;
        auto bitmap = m_renderer.Render(sourcePosition, frame.Geometry);
        if (!bitmap.HasImage)
        {
            ClearFrames(false);
            return;
        }
        ++m_imageFrameCount;
        // Replacing an unchanged image every video frame makes the asynchronous presenter
        // continually retire its own visual. Keep the current cue until libass changes it.
        if (!bitmap.Changed && m_lastBitmap && m_currentImage) return;
        if (bitmap.Changed || !m_lastBitmap)
        {
            if (bitmap.PremultipliedBgra.empty() || bitmap.Width <= 0 || bitmap.Height <= 0)
                winrt::throw_hresult(E_UNEXPECTED);
            auto image = SoftwareBitmap(BitmapPixelFormat::Bgra8, bitmap.Width, bitmap.Height,
                BitmapAlphaMode::Premultiplied);
            {
                auto buffer = image.LockBuffer(BitmapBufferAccessMode::Write);
                auto reference = buffer.CreateReference();
                auto destination = reference.data();
                const auto plane = buffer.GetPlaneDescription(0);
                const auto rowBytes = static_cast<size_t>(bitmap.Width) * 4;
                if (plane.Stride < 0 || static_cast<size_t>(plane.Stride) < rowBytes || plane.StartIndex < 0)
                    winrt::throw_hresult(E_UNEXPECTED);
                for (int row = 0; row < bitmap.Height; ++row)
                    std::memcpy(destination + plane.StartIndex + static_cast<size_t>(row) * plane.Stride,
                        bitmap.PremultipliedBgra.data() + static_cast<size_t>(row) * rowBytes, rowBytes);
            }
            m_lastBitmap = image;
            m_lastX = bitmap.X;
            m_lastY = bitmap.Y;
            m_lastWidth = bitmap.Width;
            m_lastHeight = bitmap.Height;
            // Observe the actual WinRT bitmap buffer after unlocking the write, not the libass input vector.
            m_lastBitmapNonzeroAlphaPixels = NonzeroBitmapAlphaPixels(m_lastBitmap);
        }
        auto imageCue = ImageCue();
        imageCue.Id(winrt::to_hstring(m_generation) + L":image:" + winrt::to_hstring(m_nextFrame++));
        imageCue.StartTime(TimeSpan{ frame.PositionTicks });
        // Rendering begins at the real sample clock, but presenting the resulting image can
        // finish after that sample. Hold it until a later real clock result replaces or clears it.
        imageCue.Duration(TimeSpan{ (std::min)(InfiniteCueDurationTicks,
            (std::numeric_limits<int64_t>::max)() - frame.PositionTicks) });
        TimedTextPoint position;
        position.Unit = TimedTextUnit::Percentage;
        position.X = static_cast<double>(m_lastX) * 100 / frame.Geometry.Width;
        // The same Windows ImageSubtitle presenter adds five percent vertical padding to image cues.
        position.Y = static_cast<double>(m_lastY) * 100 / frame.Geometry.Height - 5.0;
        TimedTextSize extent;
        extent.Unit = TimedTextUnit::Percentage;
        extent.Width = static_cast<double>(m_lastWidth) * 100 / frame.Geometry.Width;
        extent.Height = static_cast<double>(m_lastHeight) * 100 / frame.Geometry.Height;
        if (m_currentImage)
        {
            auto previous = m_currentImage;
            m_currentImage = nullptr;
            m_track.RemoveCue(previous);
        }
        m_currentImage = imageCue;
        m_currentImagePosition = position;
        m_currentImageExtent = extent;
        try { m_track.AddCue(imageCue); ++m_imageCueAddedCount; PublishFrameChange(); }
        catch (...) { m_currentImage = nullptr; throw; }
    }

    void LibassSubtitleTrack::OnReferenceCueEntered(TimedMetadataTrack const&, MediaCueEventArgs const& args)
    {
        HRESULT failure = S_OK;
        {
            std::lock_guard lock(m_mutex);
            try
            {
                if (m_closed) return;
                ++m_referenceCueEnteredCount;
                auto cue = args.Cue();
                auto id = cue.Id();
                auto found = m_scheduled.find(std::wstring(id.data(), id.size()));
                if (found == m_scheduled.end()) return;
                auto frame = found->second;
                m_scheduled.erase(found);
                if (m_referenceTrack) m_referenceTrack.RemoveCue(cue);
                EnsureOpen();
                RenderImage(frame);
            }
            catch (...) { failure = winrt::to_hresult(); }
        }
        if (FAILED(failure)) MarkFailed(failure);
    }

    void LibassSubtitleTrack::OnImageCueEntered(TimedMetadataTrack const&, MediaCueEventArgs const& args)
    {
        HRESULT failure = S_OK;
        {
            std::lock_guard lock(m_mutex);
            try
            {
                if (m_closed) return;
                ++m_imageCueEnteredCount;
                auto cue = args.Cue().try_as<ImageCue>();
                if (!cue || cue != m_currentImage || !m_lastBitmap) return;
                EnsureOpen();
                // Match the native bitmap subtitle provider: populate the actual image when
                // Windows enters its cue, before the platform presenter consumes its properties.
                cue.SoftwareBitmap(m_lastBitmap);
                cue.Position(m_currentImagePosition);
                cue.Extent(m_currentImageExtent);
                m_lastBitmapNonzeroAlphaPixels = NonzeroBitmapAlphaPixels(cue.SoftwareBitmap());
            }
            catch (...) { failure = winrt::to_hresult(); }
        }
        if (FAILED(failure)) MarkFailed(failure);
    }

    void LibassSubtitleTrack::OnImageCueExited(TimedMetadataTrack const&, MediaCueEventArgs const& args)
    {
        std::lock_guard lock(m_mutex);
        try
        {
            if (m_closed) return;
            ++m_imageCueExitedCount;
            auto image = args.Cue().try_as<ImageCue>();
            if (!image || image != m_currentImage) return;
            // A genuine exit of the owned cue must not leave a false reusable-image state.
            ClearFrames(false);
            m_renderer.InvalidateImage();
        }
        catch (...) { }
    }

    void LibassSubtitleTrack::ClearFrames(bool clearPending)
    {
        const bool publishClear = clearPending || m_currentImage || m_lastBitmap;
        if (clearPending)
        {
            m_lastRenderedMediaPositionTicks = -1;
            m_lastRenderedScriptPositionTicks = -1;
            m_scheduled.clear();
            if (m_referenceTrack)
            {
                while (m_referenceTrack.Cues().Size() > 0)
                    m_referenceTrack.RemoveCue(m_referenceTrack.Cues().GetAt(m_referenceTrack.Cues().Size() - 1));
            }
        }
        if (m_currentImage || m_lastBitmap) ++m_clearCount;
        if (m_currentImage)
        {
            auto image = m_currentImage;
            m_currentImage = nullptr;
            if (m_track) m_track.RemoveCue(image);
        }
        m_lastBitmap = nullptr;
        m_currentImagePosition = {};
        m_currentImageExtent = {};
        m_lastX = m_lastY = m_lastWidth = m_lastHeight = 0;
        m_lastBitmapNonzeroAlphaPixels = 0;
        if (publishClear) PublishFrameChange();
    }

    void LibassSubtitleTrack::PublishFrameChange()
    {
        // The reporter only advances a source-owned atomic sequence. It must not take the
        // source mutex while this track is locked or call back into media presentation.
        if (m_frameReporter) m_frameReporter();
    }

    void LibassSubtitleTrack::FlushForSeek()
    {
        std::lock_guard lock(m_mutex);
        if (m_closed) return;
        if (m_enabled) EnsureOpen();
        ++m_generation;
        ClearFrames(true);
        if (m_embedded) m_renderer.FlushEmbeddedEvents();
        else m_renderer.InvalidateImage();
    }

    void LibassSubtitleTrack::DetachPlaybackItem() noexcept
    {
        try
        {
            if (auto item = m_item.get())
            {
                if (m_tracksChangedToken.value != 0) item.TimedMetadataTracksChanged(m_tracksChangedToken);
                if (m_referenceTrack)
                {
                    auto tracks = item.Source().ExternalTimedMetadataTracks();
                    uint32_t index = 0;
                    if (tracks.IndexOf(m_referenceTrack, index)) tracks.RemoveAt(index);
                }
            }
            if (m_referenceTrack && m_referenceEnteredToken.value != 0)
                m_referenceTrack.CueEntered(m_referenceEnteredToken);
        }
        catch (...) { }
        m_referenceEnteredToken = {};
        m_tracksChangedToken = {};
        m_referenceTrack = nullptr;
        m_item = nullptr;
    }

    void LibassSubtitleTrack::Close() noexcept
    {
        std::lock_guard lock(m_mutex);
        if (m_closed) return;
        m_closed = true;
        m_enabled = false;
        ++m_generation;
        m_failureReporter = {};
        try { ClearFrames(true); }
        catch (...) { m_scheduled.clear(); m_currentImage = nullptr; m_lastBitmap = nullptr; }
        m_frameReporter = {};
        DetachPlaybackItem();
        try { if (m_track && m_imageEnteredToken.value != 0) m_track.CueEntered(m_imageEnteredToken); }
        catch (...) { }
        try { if (m_track && m_imageExitedToken.value != 0) m_track.CueExited(m_imageExitedToken); }
        catch (...) { }
        m_imageEnteredToken = {};
        m_imageExitedToken = {};
        m_track = nullptr;
    }

    void LibassSubtitleTrack::MarkFailed(HRESULT failure) noexcept
    {
        if (SUCCEEDED(failure)) return;
        std::function<void(HRESULT)> reporter;
        {
            std::lock_guard lock(m_mutex);
            if (m_closed || FAILED(m_failure.load())) return;
            m_failure.store(failure);
            ++m_generation;
            try { ClearFrames(true); } catch (...) { }
            reporter = m_enabled ? m_failureReporter : std::function<void(HRESULT)>{};
        }
        try { if (reporter) reporter(failure); }
        catch (...) { }
    }

    HRESULT LibassSubtitleTrack::Failure() const noexcept { return m_failure.load(); }

    void LibassSubtitleTrack::AppendObservation(AssSubtitleObservation& observation) const
    {
        std::lock_guard lock(m_mutex);
        if (m_enabled) ++observation.EnabledTrackCount;
        if (m_currentImage)
        {
            ++observation.CurrentImageCount;
            if (m_track)
            {
                for (auto const& cue : m_track.ActiveCues())
                    if (cue == m_currentImage.as<IMediaCue>()) ++observation.PlatformActiveImageCount;
            }
        }
        if (auto item = m_item.get(); item && m_referenceTrack)
        {
            auto tracks = item.TimedMetadataTracks();
            for (uint32_t index = 0; index < tracks.Size(); ++index)
            {
                if (tracks.GetAt(index) == m_referenceTrack
                    && tracks.GetPresentationMode(index) == TimedMetadataTrackPresentationMode::Hidden)
                {
                    ++observation.HiddenReferenceTrackCount;
                    break;
                }
            }
        }
        observation.ScheduledFrameCount += m_scheduledFrameCount;
        observation.ReferenceCueEnteredCount += m_referenceCueEnteredCount;
        observation.RenderCallCount += m_renderCallCount;
        observation.ImageFrameCount += m_imageFrameCount;
        observation.ImageCueAddedCount += m_imageCueAddedCount;
        observation.ImageCueEnteredCount += m_imageCueEnteredCount;
        observation.ImageCueExitedCount += m_imageCueExitedCount;
        observation.ClearCount += m_clearCount;
        observation.StaleFrameCount += m_staleFrameCount;
        observation.PendingScheduledFrameCount += m_scheduled.size();
        if (m_lastRenderedMediaPositionTicks >= observation.LastRenderPositionTicks)
        {
            observation.LastRenderPositionTicks = m_lastRenderedMediaPositionTicks;
            observation.LastScriptPositionTicks = m_lastRenderedScriptPositionTicks;
            observation.LastBitmapNonzeroAlphaPixels = m_lastBitmapNonzeroAlphaPixels;
            observation.LastImageX = m_lastX;
            observation.LastImageY = m_lastY;
            observation.LastImageWidth = m_lastWidth;
            observation.LastImageHeight = m_lastHeight;
        }
    }

    bool LibassSubtitleTrack::ReadFrame(AssSubtitleFrame& frame, std::atomic<uint64_t> const& sequence,
        uint64_t expectedSequence, SoftwareBitmap* snapshot) const
    {
        std::lock_guard lock(m_mutex);
        frame.TrackId = m_trackIdentity;
        frame.Generation = m_generation;
        frame.HasImage = !m_closed && m_enabled && !FAILED(m_failure.load()) && m_lastBitmap ? 1 : 0;
        frame.MediaPositionTicks = m_lastRenderedMediaPositionTicks;
        frame.ScriptPositionTicks = m_lastRenderedScriptPositionTicks;
        frame.VideoWidth = m_lastFrameGeometry.Width;
        frame.VideoHeight = m_lastFrameGeometry.Height;
        frame.PixelAspectRatio = m_lastFrameGeometry.PixelAspectRatio;
        if (frame.HasImage)
        {
            frame.X = m_lastX;
            frame.Y = m_lastY;
            frame.Width = m_lastWidth;
            frame.Height = m_lastHeight;
            frame.NonzeroAlphaPixels = m_lastBitmapNonzeroAlphaPixels;
        }
        // Load under the same track lock as the pixels: a frame callback cannot label an
        // old bitmap with the sequence of a newer selected-track image.
        frame.Sequence = sequence.load(std::memory_order_acquire);
        if (!snapshot) return true;
        *snapshot = nullptr;
        if (frame.Sequence != expectedSequence) return false;
        if (frame.HasImage)
        {
            const auto storageWidth = m_lastBitmap.PixelWidth();
            const auto storageHeight = m_lastBitmap.PixelHeight();
            const auto pixels = static_cast<size_t>(storageWidth) * static_cast<size_t>(storageHeight);
            if (storageWidth <= 0 || storageHeight <= 0 || storageWidth > 16'384 || storageHeight > 16'384
                || pixels > AssRenderer::MaximumBitmapBytes / 4
                || storageWidth != m_lastWidth || storageHeight != m_lastHeight)
                winrt::throw_hresult(E_UNEXPECTED);
            // An independent read-only view lets the consumer close its owned snapshot
            // promptly, without closing or mutating the renderer's current bitmap.
            auto copied = SoftwareBitmap::Copy(m_lastBitmap);
            *snapshot = copied.GetReadOnlyView();
        }
        return true;
    }
}

SubtitleProviderLibass::SubtitleProviderLibass(std::shared_ptr<FFmpegReader> reader, AVFormatContext* format,
    AVCodecContext* codec, winrt::FFmpegInteropX::MediaSourceConfig const& config, int index)
    : SubtitleProvider(reader, format, codec, config, index, TimedMetadataKind::ImageSubtitle)
{
}

void SubtitleProviderLibass::SetSourceFonts(
    std::shared_ptr<const std::vector<EmbyNativeSubtitles::AssFontAttachment>> fonts)
{
    std::lock_guard lock(mutex);
    if (m_libassTrack) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
    m_sourceFonts = std::move(fonts);
}

HRESULT SubtitleProviderLibass::Initialize()
{
    try
    {
        if (!m_pAvCodecCtx || (m_pAvCodecCtx->codec_id != AV_CODEC_ID_ASS && m_pAvCodecCtx->codec_id != AV_CODEC_ID_SSA)
            || !m_pAvCodecCtx->subtitle_header || m_pAvCodecCtx->subtitle_header_size <= 0)
            return E_INVALIDARG;
        auto result = SubtitleProvider::Initialize();
        if (FAILED(result)) return result;
        // The source filters real font attachments once, including stable names for unnamed TTF/OTF files.
        // Share that immutable list so non-font attachments never enter font limits or get read again.
        const auto fonts = m_sourceFonts && m_config.Subtitles().UseEmbeddedSubtitleFonts()
            ? std::span<const EmbyNativeSubtitles::AssFontAttachment>(*m_sourceFonts)
            : std::span<const EmbyNativeSubtitles::AssFontAttachment>{};
        m_libassTrack = EmbyNativeSubtitles::LibassSubtitleTrack::CreateEmbedded(SubtitleTrack,
            std::span<const uint8_t>(m_pAvCodecCtx->subtitle_header, m_pAvCodecCtx->subtitle_header_size), fonts);
        m_sourceFonts.reset();
        m_libassTrack->SetDelay(streamDelay.count());
        m_libassTrack->SetEnabled(IsEnabled());
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

void SubtitleProviderLibass::QueuePacket(AVPacket* packet)
{
    if (!packet) return;
    try
    {
        if (!m_libassTrack) winrt::throw_hresult(E_UNEXPECTED);
        AVSubtitle decoded{};
        int present = 0;
        const int result = avcodec_decode_subtitle2(m_pAvCodecCtx, &decoded, &present, packet);
        try
        {
            if (result < 0) winrt::throw_hresult(E_FAIL);
            if (present)
            {
                // Convert the native packet time base directly. libass applies the stream delay at render time.
                const auto timestamp = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
                if (timestamp == AV_NOPTS_VALUE) winrt::throw_hresult(E_INVALIDARG);
                const auto rescaled = av_rescale_q(timestamp, m_pAvStream->time_base, AVRational{ 1, 10'000'000 });
                if ((m_startOffset > 0 && rescaled < (std::numeric_limits<int64_t>::min)() + m_startOffset)
                    || (m_startOffset < 0 && rescaled > (std::numeric_limits<int64_t>::max)() + m_startOffset))
                    winrt::throw_hresult(E_INVALIDARG);
                auto start = rescaled - m_startOffset;
                if (decoded.start_display_time > 0)
                {
                    const auto offset = static_cast<int64_t>(decoded.start_display_time) * 10'000;
                    if (start > (std::numeric_limits<int64_t>::max)() - offset) winrt::throw_hresult(E_INVALIDARG);
                    start += offset;
                }
                auto duration = av_rescale_q(packet->duration, m_pAvStream->time_base, AVRational{ 1, 10'000'000 });
                if (decoded.end_display_time > decoded.start_display_time)
                    duration = static_cast<int64_t>(decoded.end_display_time - decoded.start_display_time) * 10'000;
                if (duration <= 0) winrt::throw_hresult(E_INVALIDARG);
                for (unsigned int index = 0; index < decoded.num_rects; ++index)
                {
                    auto rect = decoded.rects[index];
                    if (!rect || !rect->ass) continue;
                    const auto length = strnlen_s(rect->ass, EmbyNativeSubtitles::AssRenderer::MaximumEventBytes + 1);
                    m_libassTrack->FeedEvent(std::span<const char>(rect->ass, length), start, duration);
                }
            }
        }
        catch (...) { avsubtitle_free(&decoded); throw; }
        avsubtitle_free(&decoded);
    }
    catch (...) { if (m_libassTrack) m_libassTrack->MarkFailed(winrt::to_hresult()); }
    av_packet_free(&packet);
}

void SubtitleProviderLibass::SetStreamDelay(TimeSpan const& delay)
{
    std::lock_guard lock(mutex);
    streamDelay = delay;
    if (m_libassTrack) m_libassTrack->SetDelay(delay.count());
}

void SubtitleProviderLibass::EnableStream()
{
    SubtitleProvider::EnableStream();
    if (m_libassTrack) m_libassTrack->SetEnabled(true);
}

void SubtitleProviderLibass::DisableStream()
{
    SubtitleProvider::DisableStream();
    if (m_libassTrack) m_libassTrack->SetEnabled(false);
}

void SubtitleProviderLibass::Flush(bool flushBuffers)
{
    CompressedSampleProvider::Flush(flushBuffers);
    if (flushBuffers && m_libassTrack) m_libassTrack->FlushForSeek();
}

IMediaCue SubtitleProviderLibass::CreateCue(AVPacket*, TimeSpan*, TimeSpan*)
{
    // Packet ingestion and timed bitmap presentation are deliberately separate.
    return nullptr;
}

std::shared_ptr<EmbyNativeSubtitles::LibassSubtitleTrack> SubtitleProviderLibass::LibassTrack() const noexcept
{
    return m_libassTrack;
}
