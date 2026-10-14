#include "pch.h"
#include "EmbySubtitleApi.h"
#include "SubtitleProviderLibass.h"
#include "SubtitleProviderBitmap.h"
#include "FFmpegMediaSource.h"
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <cwchar>
#include <map>
#include <limits>
#include <optional>
#include <span>

using namespace EmbyNativeSubtitles;
using namespace winrt::Windows::Media::Core;
using namespace winrt::Windows::Media::Playback;
using namespace winrt::Windows::Graphics::Imaging;

namespace
{
    std::atomic<uint64_t> NextLocalOwner{ 1 };
    std::atomic<uint64_t> NextBitmapTrackIdentity{ 0x8000'0000'0000'0001ULL };

    uint64_t BitmapAlphaPixels(SoftwareBitmap const& bitmap)
    {
        if (!bitmap) return 0;
        const auto width = bitmap.PixelWidth();
        const auto height = bitmap.PixelHeight();
        if (width <= 0 || height <= 0 || width > 16'384 || height > 16'384
            || static_cast<size_t>(width) * static_cast<size_t>(height) > AssRenderer::MaximumBitmapBytes / 4
            || bitmap.BitmapPixelFormat() != winrt::Windows::Graphics::Imaging::BitmapPixelFormat::Bgra8
            || bitmap.BitmapAlphaMode() != winrt::Windows::Graphics::Imaging::BitmapAlphaMode::Premultiplied)
            winrt::throw_hresult(E_INVALIDARG);
        auto buffer = bitmap.LockBuffer(winrt::Windows::Graphics::Imaging::BitmapBufferAccessMode::Read);
        auto reference = buffer.CreateReference();
        auto pixels = reference.data();
        const auto plane = buffer.GetPlaneDescription(0);
        if (plane.StartIndex < 0 || plane.Stride < 0 || static_cast<size_t>(plane.Stride) < static_cast<size_t>(width) * 4)
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

    double StablePixelCoordinate(double value)
    {
        // Percentage conversions can move an exact integer by a few representational ULPs.
        const auto nearest = std::round(value);
        const auto tolerance = 8 * std::numeric_limits<double>::epsilon() * (std::max)(1.0, std::abs(value));
        return std::abs(value - nearest) <= tolerance ? nearest : value;
    }

    void BitmapVideoRectangle(TimedTextPoint const& position, TimedTextSize const& extent,
        AssVideoGeometry const& video, AssSubtitleFrame& frame)
    {
        if (video.Width <= 0 || video.Height <= 0 || video.Width > 16'384 || video.Height > 16'384
            || position.Unit != TimedTextUnit::Percentage || extent.Unit != TimedTextUnit::Percentage)
            winrt::throw_hresult(E_INVALIDARG);
        const double left = StablePixelCoordinate(position.X * video.Width / 100.0);
        // SubtitleProviderBitmap subtracts five percent for the platform caption padding.
        // An application image has no such padding: undo that exact adjustment once.
        const double top = StablePixelCoordinate((position.Y + 5.0) * video.Height / 100.0);
        const double right = StablePixelCoordinate(left + extent.Width * video.Width / 100.0);
        const double bottom = StablePixelCoordinate(top + extent.Height * video.Height / 100.0);
        if (!std::isfinite(left) || !std::isfinite(top) || !std::isfinite(right) || !std::isfinite(bottom)
            || extent.Width <= 0 || extent.Height <= 0
            || left < 0 || top < 0 || right > video.Width || bottom > video.Height)
            winrt::throw_hresult(E_INVALIDARG);
        // Preserve visible pixel coverage when a higher-resolution subtitle canvas maps a
        // narrow real bitmap to a fraction of one video pixel. Its storage stays unchanged.
        frame.X = static_cast<int32_t>(std::floor(left));
        frame.Y = static_cast<int32_t>(std::floor(top));
        frame.Width = static_cast<int32_t>(std::ceil(right)) - frame.X;
        frame.Height = static_cast<int32_t>(std::ceil(bottom)) - frame.Y;
        if (frame.X < 0 || frame.Y < 0 || frame.Width <= 0 || frame.Height <= 0
            || frame.Width > video.Width - frame.X || frame.Height > video.Height - frame.Y)
            winrt::throw_hresult(E_INVALIDARG);
    }

    winrt::FFmpegInteropX::FFmpegMediaSource CopySource(void* sourceIUnknown)
    {
        if (!sourceIUnknown) winrt::throw_hresult(E_POINTER);
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, sourceIUnknown);
        auto projected = object.as<winrt::FFmpegInteropX::FFmpegMediaSource>();
        if (winrt::get_class_name(projected) != L"FFmpegInteropX.FFmpegMediaSource")
            winrt::throw_hresult(E_NOINTERFACE);
        return projected;
    }

    std::span<const uint8_t> DocumentBytes(uint8_t const* data, uint32_t bytes)
    {
        if (!data || bytes == 0 || bytes > AssRenderer::MaximumDocumentBytes)
            winrt::throw_hresult(E_INVALIDARG);
        return { data, bytes };
    }

    bool FontAttachment(AVStream const* stream)
    {
        if (!stream || !stream->codecpar || stream->codecpar->codec_type != AVMEDIA_TYPE_ATTACHMENT) return false;
        if (stream->codecpar->codec_id == AV_CODEC_ID_TTF || stream->codecpar->codec_id == AV_CODEC_ID_OTF) return true;
        auto mime = av_dict_get(stream->metadata, "mimetype", nullptr, 0);
        if (!mime || !mime->value) return false;
        constexpr char const* fontMimes[] = { "application/x-truetype-font", "application/x-font-ttf",
            "application/vnd.ms-opentype", "application/font-sfnt", "font/ttf", "font/otf" };
        for (auto value : fontMimes) if (_stricmp(mime->value, value) == 0) return true;
        return false;
    }
}

struct EmbyAssSubtitleState final : std::enable_shared_from_this<EmbyAssSubtitleState>
{
    mutable std::recursive_mutex Mutex;
    bool Closed = false;
    std::atomic<HRESULT> Failure{ S_OK };
    std::atomic<uint64_t> FrameSequence{ 1 };
    winrt::weak_ref<MediaPlaybackItem> Item;
    winrt::weak_ref<MediaPlaybackSession> PlaybackSession;
    winrt::weak_ref<MediaStreamSource> StreamSource;
    std::vector<std::weak_ptr<SubtitleProviderLibass>> Embedded;
    std::vector<std::weak_ptr<SubtitleProviderBitmap>> Bitmaps;
    std::map<uint64_t, std::shared_ptr<LibassSubtitleTrack>> Documents;
    std::shared_ptr<const std::vector<AssFontAttachment>> Fonts;
    AssVideoGeometry Geometry;
    int64_t LastDurationTicks = 0;
    int64_t LastSamplePositionTicks = -1;
    uint64_t VideoSampleCount = 0;
    uint64_t RefreshCount = 0;
    int64_t LastUndatedPositionTicks = -1;
    AssVideoGeometry LastUndatedGeometry;
    TimedMetadataTrack BitmapTrack{ nullptr };
    ImageCue BitmapCue{ nullptr };
    SoftwareBitmap BitmapImage{ nullptr };
    AssSubtitleFrame BitmapFrame{};
    uint64_t BitmapImageAlphaPixels = 0;
    uint64_t BitmapTrackId = 0;
    uint64_t BitmapGeneration = 1;
    bool BitmapSelected = false;

    void EnsureOpen() const
    {
        if (Closed) winrt::throw_hresult(RO_E_CLOSED);
    }

    std::vector<std::shared_ptr<LibassSubtitleTrack>> Tracks()
    {
        std::vector<std::shared_ptr<LibassSubtitleTrack>> result;
        result.reserve(Embedded.size() + Documents.size());
        for (auto iterator = Embedded.begin(); iterator != Embedded.end();)
        {
            if (auto provider = iterator->lock())
            {
                if (auto track = provider->LibassTrack()) result.push_back(std::move(track));
                ++iterator;
            }
            else iterator = Embedded.erase(iterator);
        }
        for (auto const& entry : Documents) result.push_back(entry.second);
        return result;
    }

    void ReportFailure(HRESULT failure) noexcept
    {
        if (SUCCEEDED(failure)) return;
        winrt::Windows::Media::Core::MediaStreamSource source{ nullptr };
        {
            std::lock_guard lock(Mutex);
            if (Closed || FAILED(Failure.load())) return;
            Failure.store(failure);
            source = StreamSource.get();
        }
        // A renderer failure is a real media failure. It cannot silently present an empty subtitle lane.
        try { if (source) source.NotifyError(MediaStreamSourceErrorStatus::DecodeError); }
        catch (...) { }
    }

    void ConfigureTrack(std::shared_ptr<LibassSubtitleTrack> const& track)
    {
        if (!track) return;
        auto weak = weak_from_this();
        track->SetFailureReporter([weak](HRESULT failure)
        {
            if (auto self = weak.lock()) self->ReportFailure(failure);
        });
        track->SetFrameReporter([weak]
        {
            // Track callbacks hold the track mutex. Publish only through an atomic here;
            // acquiring the source mutex would invert the normal source-to-track lock order.
            if (auto self = weak.lock()) self->FrameSequence.fetch_add(1, std::memory_order_release);
        });
        if (auto item = Item.get()) track->AttachPlaybackItem(item);
    }

    void SetAttachments(AVFormatContext const* format)
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        if (!format) winrt::throw_hresult(E_POINTER);
        if (!Documents.empty()) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        std::vector<AssFontAttachment> fonts;
        size_t fontBytes = 0;
        for (unsigned int index = 0; index < format->nb_streams; ++index)
        {
            auto stream = format->streams[index];
            if (!FontAttachment(stream)) continue;
            auto codec = stream->codecpar;
            if (!codec->extradata || codec->extradata_size <= 0) continue;
            const auto length = static_cast<size_t>(codec->extradata_size);
            if (length > AssRenderer::MaximumFontBytes || length > AssRenderer::MaximumTotalFontBytes - fontBytes
                || fonts.size() >= AssRenderer::MaximumFontCount)
                winrt::throw_hresult(E_INVALIDARG);
            auto filename = av_dict_get(stream->metadata, "filename", nullptr, 0);
            AssFontAttachment font;
            if (filename && filename->value && filename->value[0] != '\0')
                font.Name = filename->value;
            else font.Name = "attachment-" + std::to_string(index)
                + (codec->codec_id == AV_CODEC_ID_OTF ? ".otf" : ".ttf");
            if (font.Name.size() > 1024) winrt::throw_hresult(E_INVALIDARG);
            font.Bytes.assign(codec->extradata, codec->extradata + length);
            fontBytes += length;
            fonts.push_back(std::move(font));
        }
        Fonts = std::make_shared<const std::vector<AssFontAttachment>>(std::move(fonts));
    }

    void AddEmbedded(std::shared_ptr<SubtitleProviderLibass> const& provider)
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        if (!provider) winrt::throw_hresult(E_POINTER);
        for (auto const& entry : Embedded) if (entry.lock() == provider) return;
        provider->SetSourceFonts(Fonts);
        Embedded.push_back(provider);
        ConfigureTrack(provider->LibassTrack());
    }

    void AddBitmap(std::shared_ptr<SubtitleProviderBitmap> const& provider)
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        if (!provider) winrt::throw_hresult(E_POINTER);
        for (auto const& entry : Bitmaps) if (entry.lock() == provider) return;
        Bitmaps.push_back(provider);
    }

    std::shared_ptr<SubtitleProviderBitmap> BitmapProvider(TimedMetadataTrack const& track)
    {
        for (auto iterator = Bitmaps.begin(); iterator != Bitmaps.end();)
        {
            if (auto provider = iterator->lock())
            {
                if (provider->SubtitleTrack == track) return provider;
                ++iterator;
            }
            else iterator = Bitmaps.erase(iterator);
        }
        return {};
    }

    void AttachItem(MediaPlaybackItem const& item)
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        Item = item;
        for (auto const& track : Tracks()) ConfigureTrack(track);
        FrameSequence.fetch_add(1, std::memory_order_release);
    }

    std::optional<TimedMetadataTrackPresentationMode> PresentationMode(TimedMetadataTrack const& track) const
    {
        auto item = Item.get();
        if (!item || !track) return std::nullopt;
        auto tracks = item.TimedMetadataTracks();
        for (uint32_t index = 0; index < tracks.Size(); ++index)
            if (tracks.GetAt(index) == track) return tracks.GetPresentationMode(index);
        return std::nullopt;
    }

    void SynchronizeSelection(std::shared_ptr<LibassSubtitleTrack> const& track,
        TimedMetadataTrackPresentationMode fallback = TimedMetadataTrackPresentationMode::Disabled)
    {
        const auto mode = PresentationMode(track->Track()).value_or(fallback);
        track->SetEnabled(mode == TimedMetadataTrackPresentationMode::PlatformPresented
            || mode == TimedMetadataTrackPresentationMode::ApplicationPresented);
    }

    void SetPresentationMode(TimedMetadataTrack const& track, TimedMetadataTrackPresentationMode mode)
    {
        std::lock_guard lock(Mutex);
        if (Closed || !track) return;
        for (auto const& candidate : Tracks())
        {
            if (candidate->Track() == track)
            {
                // A delayed mode notification must not overwrite a newer actual selection.
                try { SynchronizeSelection(candidate, mode); }
                catch (...) { ReportFailure(winrt::to_hresult()); throw; }
                FrameSequence.fetch_add(1, std::memory_order_release);
                return;
            }
        }
        if (BitmapProvider(track))
        {
            // Read the actual item mode: a delayed Disabled callback must not hide a newer
            // selection. Re-selection invalidates a prior snapshot even for identical pixels.
            const auto actual = PresentationMode(track).value_or(mode);
            if (BitmapTrack == track)
                ClearBitmapFrame(actual != TimedMetadataTrackPresentationMode::ApplicationPresented, true);
            else FrameSequence.fetch_add(1, std::memory_order_release);
        }
    }

    uint64_t CreateDocument(std::span<const uint8_t> utf8, winrt::hstring const& name,
        int64_t timelineOffsetTicks, int64_t delayTicks, TimedMetadataTrack& result)
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        // Loading is transactional: one active document and one prepared replacement are sufficient.
        if (Documents.size() >= 2) winrt::throw_hresult(E_OUTOFMEMORY);
        auto owner = NextLocalOwner.fetch_add(1);
        if (owner == 0) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        auto track = LibassSubtitleTrack::CreateDocument(utf8, name, timelineOffsetTicks, delayTicks,
            Fonts ? std::span<const AssFontAttachment>(*Fonts) : std::span<const AssFontAttachment>{});
        ConfigureTrack(track);
        result = track->Track();
        Documents.emplace(owner, std::move(track));
        FrameSequence.fetch_add(1, std::memory_order_release);
        return owner;
    }

    void SetDocumentDelay(uint64_t owner, int64_t delayTicks)
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        auto found = Documents.find(owner);
        if (found == Documents.end()) winrt::throw_hresult(E_INVALIDARG);
        found->second->SetDelay(delayTicks);
    }

    void RemoveDocument(uint64_t owner)
    {
        std::shared_ptr<LibassSubtitleTrack> released;
        {
            std::lock_guard lock(Mutex);
            auto found = Documents.find(owner);
            if (found == Documents.end()) return;
            released = found->second;
            Documents.erase(found);
            FrameSequence.fetch_add(1, std::memory_order_release);
        }
        released->Close();
    }

    void ScheduleFrame(int64_t positionTicks, int64_t durationTicks, AssVideoGeometry const& geometry)
    {
        for (auto const& track : Tracks())
        {
            ConfigureTrack(track);
            SynchronizeSelection(track);
            track->Schedule(positionTicks, durationTicks, geometry);
        }
    }

    void RenderSample(MediaStreamSample const& sample, winrt::FFmpegInteropX::VideoStreamInfo const& video,
        AVCodecParameters const* parameters)
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        if (!sample || !video) return;
        if (FAILED(Failure.load())) winrt::throw_hresult(Failure.load());
        Geometry.Width = video.PixelWidth();
        Geometry.Height = video.PixelHeight();
        if (Geometry.Width <= 0 || Geometry.Height <= 0) winrt::throw_hresult(E_INVALIDARG);
        const auto aspect = video.DisplayAspectRatio();
        Geometry.PixelAspectRatio = std::isfinite(aspect) && aspect > 0
            ? aspect * Geometry.Height / Geometry.Width : 1.0;
        Geometry.Hdr = video.HasHdrMetadata() || video.IsHdrActive();
        Geometry.ColorSpace = parameters ? parameters->color_space : AVCOL_SPC_UNSPECIFIED;
        Geometry.ColorRange = parameters ? parameters->color_range : AVCOL_RANGE_UNSPECIFIED;
        const auto position = sample.Timestamp().count();
        LastSamplePositionTicks = position;
        ++VideoSampleCount;
        const auto duration = sample.Duration().count();
        if (LastUndatedPositionTicks >= 0 && position > LastUndatedPositionTicks)
        {
            // A zero-duration sample is completed by the next actual timestamp, without guessing a cadence.
            const auto observed = position - LastUndatedPositionTicks;
            ScheduleFrame(LastUndatedPositionTicks, observed, LastUndatedGeometry);
            LastDurationTicks = observed;
        }
        LastUndatedPositionTicks = -1;
        if (duration > 0)
        {
            LastDurationTicks = duration;
            ScheduleFrame(position, duration, Geometry);
        }
        else
        {
            LastUndatedPositionTicks = position;
            LastUndatedGeometry = Geometry;
        }
    }

    void Refresh(int64_t mediaPositionTicks, int64_t frameDurationTicks)
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        if (FAILED(Failure.load())) winrt::throw_hresult(Failure.load());
        if (mediaPositionTicks < 0 || frameDurationTicks < 0) winrt::throw_hresult(E_INVALIDARG);
        const auto duration = frameDurationTicks > 0 ? frameDurationTicks : LastDurationTicks;
        if (duration <= 0 || Geometry.Width <= 0 || Geometry.Height <= 0) winrt::throw_hresult(E_PENDING);
        ++RefreshCount;
        for (auto const& track : Tracks())
        {
            // SetPresentationMode updates the item before its native callback is necessarily delivered.
            // A paused import must use that real mode now rather than silently skip its only refresh.
            SynchronizeSelection(track);
            track->RenderImmediate(mediaPositionTicks, duration, Geometry);
        }
    }

    AssSubtitleObservation Observe()
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        AssSubtitleObservation result{};
        result.Size = sizeof(result);
        result.Version = 1;
        result.Failure = Failure.load();
        result.VideoWidth = Geometry.Width > 0 ? static_cast<uint32_t>(Geometry.Width) : 0;
        result.VideoHeight = Geometry.Height > 0 ? static_cast<uint32_t>(Geometry.Height) : 0;
        result.VideoSampleCount = VideoSampleCount;
        result.RefreshCount = RefreshCount;
        result.LastSamplePositionTicks = LastSamplePositionTicks;
        result.LastRenderPositionTicks = -1;
        result.LastScriptPositionTicks = -1;
        auto ownedTracks = Tracks();
        result.OwnedTrackCount = static_cast<uint32_t>(ownedTracks.size());
        for (auto const& track : ownedTracks)
        {
            if (PresentationMode(track->Track()) == TimedMetadataTrackPresentationMode::PlatformPresented)
                ++result.PlatformPresentedTrackCount;
            track->AppendObservation(result);
        }
        return result;
    }

    void ClearBitmapFrame(bool clearSelection, bool force = false)
    {
        const bool changed = BitmapCue || BitmapImage || BitmapFrame.HasImage
            || (clearSelection && BitmapSelected);
        BitmapCue = nullptr;
        BitmapImage = nullptr;
        BitmapFrame = {};
        BitmapImageAlphaPixels = 0;
        if (clearSelection) BitmapSelected = false;
        if (changed || force)
        {
            ++BitmapGeneration;
            FrameSequence.fetch_add(1, std::memory_order_release);
        }
    }

    bool ReadBitmapFrame(std::shared_ptr<SubtitleProviderBitmap> const& provider,
        TimedMetadataTrack const& track, AssSubtitleFrame& result,
        uint64_t expectedSequence, SoftwareBitmap* snapshot)
    {
        if (BitmapTrack != track || !BitmapSelected)
        {
            ClearBitmapFrame(false, true);
            if (BitmapTrack != track)
            {
                BitmapTrack = track;
                BitmapTrackId = NextBitmapTrackIdentity.fetch_add(1);
                if (BitmapTrackId == 0) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
            }
            BitmapSelected = true;
        }
        ImageCue current{ nullptr };
        SoftwareBitmap image{ nullptr };
        TimedTextPoint position{};
        TimedTextSize extent{};
        winrt::Windows::Foundation::TimeSpan startTime{ -1 };
        if (auto session = PlaybackSession.get())
        {
            // Video samples are decoded ahead of presentation and cannot identify a paused cue.
            // Resolve only this provider's known timeline against the real Windows media clock.
            current = provider->ReadImageCueAtPosition(session.Position(), image, position, extent, startTime);
        }
        const bool samePixels = current == BitmapCue && image == BitmapImage;
        const auto alpha = image ? samePixels ? BitmapImageAlphaPixels : BitmapAlphaPixels(image) : 0;
        auto next = result;
        next.PresentationMode = 2;
        next.TrackId = BitmapTrackId;
        next.MediaPositionTicks = startTime.count();
        // Bitmap subtitle tracks have a media cue clock, not an ASS script clock.
        next.ScriptPositionTicks = -1;
        if (alpha > 0 && Geometry.Width > 0 && Geometry.Height > 0)
        {
            BitmapVideoRectangle(position, extent, Geometry, next);
            next.HasImage = 1;
            next.NonzeroAlphaPixels = alpha;
        }
        if (current && current == BitmapCue && next.MediaPositionTicks != BitmapFrame.MediaPositionTicks)
            ++BitmapGeneration;
        next.Generation = BitmapGeneration;
        const bool changed = !samePixels || next.HasImage != BitmapFrame.HasImage
            || next.Generation != BitmapFrame.Generation || next.MediaPositionTicks != BitmapFrame.MediaPositionTicks
            || next.VideoWidth != BitmapFrame.VideoWidth || next.VideoHeight != BitmapFrame.VideoHeight
            || next.PixelAspectRatio != BitmapFrame.PixelAspectRatio || next.X != BitmapFrame.X || next.Y != BitmapFrame.Y
            || next.Width != BitmapFrame.Width || next.Height != BitmapFrame.Height;
        BitmapCue = current;
        BitmapImage = image;
        BitmapImageAlphaPixels = alpha;
        BitmapFrame = next;
        if (changed) FrameSequence.fetch_add(1, std::memory_order_release);
        next.Sequence = FrameSequence.load(std::memory_order_acquire);
        result = next;
        if (!snapshot) return true;
        *snapshot = nullptr;
        if (result.Sequence != expectedSequence) return false;
        if (result.HasImage)
        {
            // Copy only an observed real bitmap. Storage dimensions may differ from the
            // target video rectangle for DVD canvas sizes and anamorphic material.
            const auto storageWidth = BitmapImage.PixelWidth();
            const auto storageHeight = BitmapImage.PixelHeight();
            if (storageWidth <= 0 || storageHeight <= 0 || storageWidth > 16'384 || storageHeight > 16'384
                || static_cast<size_t>(storageWidth) * static_cast<size_t>(storageHeight) > AssRenderer::MaximumBitmapBytes / 4)
                winrt::throw_hresult(E_INVALIDARG);
            auto copied = SoftwareBitmap::Copy(BitmapImage);
            *snapshot = copied.GetReadOnlyView();
        }
        return true;
    }

    bool ReadFrame(AssSubtitleFrame& result, uint64_t expectedSequence, SoftwareBitmap* snapshot)
    {
        std::lock_guard lock(Mutex);
        EnsureOpen();
        if (FAILED(Failure.load())) winrt::throw_hresult(Failure.load());
        result = {};
        result.Size = sizeof(result);
        result.Version = 1;
        result.MediaPositionTicks = -1;
        result.ScriptPositionTicks = -1;
        result.VideoWidth = Geometry.Width;
        result.VideoHeight = Geometry.Height;
        result.PixelAspectRatio = Geometry.PixelAspectRatio;
        if (snapshot) *snapshot = nullptr;
        std::shared_ptr<LibassSubtitleTrack> selected;
        for (auto const& track : Tracks())
        {
            const auto mode = PresentationMode(track->Track());
            const bool platform = mode == TimedMetadataTrackPresentationMode::PlatformPresented;
            const bool application = mode == TimedMetadataTrackPresentationMode::ApplicationPresented;
            if (!platform && !application) continue;
            // A frame cannot silently choose between two independently selected scripts.
            if (selected) winrt::throw_hresult(E_UNEXPECTED);
            selected = track;
            result.PresentationMode = application ? 2 : 1;
        }
        if (selected)
        {
            ClearBitmapFrame(true);
            return selected->ReadFrame(result, FrameSequence, expectedSequence, snapshot);
        }
        TimedMetadataTrack bitmapTrack{ nullptr };
        std::shared_ptr<SubtitleProviderBitmap> bitmapProvider;
        if (auto item = Item.get())
        {
            auto tracks = item.TimedMetadataTracks();
            for (uint32_t index = 0; index < tracks.Size(); ++index)
            {
                auto track = tracks.GetAt(index);
                if (track.TimedMetadataKind() != TimedMetadataKind::ImageSubtitle
                    || tracks.GetPresentationMode(index) != TimedMetadataTrackPresentationMode::ApplicationPresented)
                    continue;
                auto provider = BitmapProvider(track);
                if (!provider) continue;
                if (bitmapTrack) winrt::throw_hresult(E_UNEXPECTED);
                bitmapTrack = track;
                bitmapProvider = std::move(provider);
            }
        }
        if (bitmapTrack) return ReadBitmapFrame(bitmapProvider, bitmapTrack, result, expectedSequence, snapshot);
        ClearBitmapFrame(true);
        // Absence of a selected owner is a real clear state, even if a mode callback is pending.
        result.Sequence = FrameSequence.load(std::memory_order_acquire);
        return !snapshot || result.Sequence == expectedSequence;
    }

    void Flush()
    {
        std::lock_guard lock(Mutex);
        if (Closed) return;
        LastUndatedPositionTicks = -1;
        if (BitmapTrack) ClearBitmapFrame(false, true);
        for (auto const& track : Tracks()) track->FlushForSeek();
    }

    void Close() noexcept
    {
        std::lock_guard lock(Mutex);
        if (Closed) return;
        FrameSequence.fetch_add(1, std::memory_order_release);
        Closed = true;
        ClearBitmapFrame(true, BitmapTrack != nullptr);
        BitmapTrack = nullptr;
        // Retirement does not allocate a snapshot, even under memory pressure.
        for (auto const& weak : Embedded)
            if (auto provider = weak.lock())
                if (auto track = provider->LibassTrack()) track->Close();
        for (auto const& entry : Documents) entry.second->Close();
        Embedded.clear();
        Bitmaps.clear();
        Documents.clear();
        Fonts.reset();
        Item = nullptr;
        PlaybackSession = nullptr;
        StreamSource = nullptr;
        LastDurationTicks = 0;
        LastSamplePositionTicks = -1;
        LastUndatedPositionTicks = -1;
    }
};

namespace
{
    std::shared_ptr<EmbyAssSubtitleState> SourceState(void* sourceIUnknown)
    {
        auto projected = CopySource(sourceIUnknown);
        auto source = winrt::get_self<winrt::FFmpegInteropX::implementation::FFmpegMediaSource>(projected);
        auto state = source->AssSubtitleState;
        if (!state) winrt::throw_hresult(E_NOINTERFACE);
        return state;
    }
}

std::shared_ptr<EmbyAssSubtitleState> EmbyCreateAssSubtitleState()
{
    return std::make_shared<EmbyAssSubtitleState>();
}

void EmbySetAssSourceAttachments(std::shared_ptr<EmbyAssSubtitleState> const& state, AVFormatContext* format)
{
    if (state) state->SetAttachments(format);
}

void EmbyAttachAssMediaStreamSource(std::shared_ptr<EmbyAssSubtitleState> const& state, MediaStreamSource const& source)
{
    if (!state) return;
    std::lock_guard lock(state->Mutex);
    state->EnsureOpen();
    state->StreamSource = source;
}

void EmbyRegisterEmbeddedAssSubtitle(std::shared_ptr<EmbyAssSubtitleState> const& state,
    std::shared_ptr<SubtitleProviderLibass> const& provider)
{
    if (state) state->AddEmbedded(provider);
}

void EmbyRegisterBitmapSubtitle(std::shared_ptr<EmbyAssSubtitleState> const& state,
    std::shared_ptr<SubtitleProviderBitmap> const& provider)
{
    if (state) state->AddBitmap(provider);
}

void EmbyAttachAssPlaybackItem(std::shared_ptr<EmbyAssSubtitleState> const& state, MediaPlaybackItem const& item)
{
    if (state) state->AttachItem(item);
}

void EmbyAttachAssPlaybackSession(std::shared_ptr<EmbyAssSubtitleState> const& state, MediaPlaybackSession const& session)
{
    if (!state) return;
    std::lock_guard lock(state->Mutex);
    if (state->Closed) return;
    state->PlaybackSession = session;
    state->ClearBitmapFrame(true, state->BitmapTrack != nullptr);
}

void EmbySetAssPresentationMode(std::shared_ptr<EmbyAssSubtitleState> const& state, TimedMetadataTrack const& track,
    TimedMetadataTrackPresentationMode mode)
{
    if (state) state->SetPresentationMode(track, mode);
}

HRESULT EmbyRenderAssVideoSample(std::shared_ptr<EmbyAssSubtitleState> const& state, MediaStreamSample const& sample,
    winrt::FFmpegInteropX::VideoStreamInfo const& video, AVCodecParameters const* videoParameters) noexcept
{
    try { if (state) state->RenderSample(sample, video, videoParameters); return S_OK; }
    catch (...) { const auto failure = winrt::to_hresult(); if (state) state->ReportFailure(failure); return failure; }
}

void EmbyFlushAssSubtitles(std::shared_ptr<EmbyAssSubtitleState> const& state) noexcept
{
    try { if (state) state->Flush(); }
    catch (...) { if (state) state->ReportFailure(winrt::to_hresult()); }
}

void EmbyCloseAssSubtitles(std::shared_ptr<EmbyAssSubtitleState> const& state) noexcept
{
    if (state) state->Close();
}

extern "C" uint32_t __cdecl EmbySubtitleApiVersion(void) noexcept { return 1; }
extern "C" uint32_t __cdecl EmbyLibassVersion(void) noexcept { return static_cast<uint32_t>(ass_library_version()); }

extern "C" HRESULT __cdecl EmbyValidateAssSubtitleDocument(uint8_t const* utf8, uint32_t bytes,
    uint32_t* eventCount) noexcept
{
    if (!eventCount) return E_POINTER;
    *eventCount = 0;
    try
    {
        AssRenderer renderer;
        renderer.LoadDocument(DocumentBytes(utf8, bytes));
        *eventCount = renderer.EventCount();
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" HRESULT __cdecl EmbyCreateLocalAssSubtitle(void* sourceIUnknown, uint8_t const* utf8, uint32_t bytes,
    wchar_t const* name, int64_t timelineOffsetTicks, int64_t delayTicks,
    uint64_t* ownerId, void** trackIUnknown) noexcept
{
    if (!ownerId || !trackIUnknown) return E_POINTER;
    *ownerId = 0;
    *trackIUnknown = nullptr;
    try
    {
        if (!name || wcsnlen_s(name, 1025) > 1024) return E_INVALIDARG;
        auto state = SourceState(sourceIUnknown);
        TimedMetadataTrack track{ nullptr };
        const auto owner = state->CreateDocument(DocumentBytes(utf8, bytes), winrt::hstring(name),
            timelineOffsetTicks, delayTicks, track);
        *trackIUnknown = winrt::detach_abi(track);
        *ownerId = owner;
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" HRESULT __cdecl EmbySetLocalAssSubtitleDelay(void* sourceIUnknown, uint64_t ownerId,
    int64_t delayTicks) noexcept
{
    try { SourceState(sourceIUnknown)->SetDocumentDelay(ownerId, delayTicks); return S_OK; }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" HRESULT __cdecl EmbyRemoveLocalAssSubtitle(void* sourceIUnknown, uint64_t ownerId) noexcept
{
    try { SourceState(sourceIUnknown)->RemoveDocument(ownerId); return S_OK; }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" HRESULT __cdecl EmbyRefreshAssSubtitles(void* sourceIUnknown, int64_t mediaPositionTicks,
    int64_t frameDurationTicks) noexcept
{
    try { SourceState(sourceIUnknown)->Refresh(mediaPositionTicks, frameDurationTicks); return S_OK; }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" HRESULT __cdecl EmbyGetAssSubtitleFailure(void* sourceIUnknown, HRESULT* subtitleFailure) noexcept
{
    if (!subtitleFailure) return E_POINTER;
    *subtitleFailure = S_OK;
    try { *subtitleFailure = SourceState(sourceIUnknown)->Failure.load(); return S_OK; }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" HRESULT __cdecl EmbyGetAssSubtitleObservation(void* sourceIUnknown,
    AssSubtitleObservation* observation, uint32_t callerSize) noexcept
{
    if (!observation) return E_POINTER;
    if (callerSize != sizeof(AssSubtitleObservation)
        || observation->Size != callerSize || observation->Version != 1) return E_INVALIDARG;
    try { *observation = SourceState(sourceIUnknown)->Observe(); return S_OK; }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" uint32_t __cdecl EmbyAssSubtitleFrameApiVersion(void) noexcept { return 1; }

extern "C" uint32_t __cdecl EmbyBitmapSubtitleSelectionApiVersion(void) noexcept { return 1; }

extern "C" HRESULT __cdecl EmbyGetAssSubtitleFrame(void* sourceIUnknown,
    AssSubtitleFrame* frame, uint32_t callerSize) noexcept
{
    if (!frame) return E_POINTER;
    if (callerSize != sizeof(AssSubtitleFrame) || frame->Size != callerSize || frame->Version != 1)
        return E_INVALIDARG;
    try { SourceState(sourceIUnknown)->ReadFrame(*frame, 0, nullptr); return S_OK; }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" HRESULT __cdecl EmbyAcquireAssSubtitleFrame(void* sourceIUnknown,
    uint64_t expectedSequence, AssSubtitleFrame* frame, uint32_t callerSize, void** bitmapIUnknown) noexcept
{
    if (!bitmapIUnknown) return E_POINTER;
    *bitmapIUnknown = nullptr;
    if (!frame) return E_POINTER;
    if (callerSize != sizeof(AssSubtitleFrame) || frame->Size != callerSize || frame->Version != 1)
        return E_INVALIDARG;
    try
    {
        SoftwareBitmap snapshot{ nullptr };
        if (!SourceState(sourceIUnknown)->ReadFrame(*frame, expectedSequence, &snapshot)) return S_FALSE;
        if (snapshot) *bitmapIUnknown = winrt::detach_abi(snapshot);
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}
