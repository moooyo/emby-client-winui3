#pragma once

#include "SubtitleProvider.h"
#include <limits>
#include <memory>
#include <winrt/FFmpegInteropX.h>

using namespace winrt::Windows::Graphics::Imaging;
using namespace winrt::Windows::Media::Core;

class SubtitleProviderBitmap : public SubtitleProvider
{

public:
    enum class PgsSeekPacketKind { Invalid, Dependent, Reset, Clear };

    // A container key flag is not a PGS restart guarantee. Inspect the real segment
    // envelope and PCS state before reusing this decoder for an indexed replay.
    static PgsSeekPacketKind InspectIndexedPgsPacket(AVPacket const* packet) noexcept
    {
        if (!packet || !packet->data || packet->size <= 0) return PgsSeekPacketKind::Invalid;
        bool reset = false;
        bool clear = false;
        bool pcs = false;
        bool end = false;
        size_t offset = 0;
        const auto size = static_cast<size_t>(packet->size);
        while (offset < size)
        {
            if (size - offset < 3) return PgsSeekPacketKind::Invalid;
            const auto type = packet->data[offset];
            const auto length = (static_cast<size_t>(packet->data[offset + 1]) << 8) | packet->data[offset + 2];
            offset += 3;
            if (length > size - offset || end) return PgsSeekPacketKind::Invalid;
            auto data = packet->data + offset;
            if (type == 0x16)
            {
                if (pcs || length < 11) return PgsSeekPacketKind::Invalid;
                pcs = true;
                const auto state = data[7] >> 6;
                if (state > 2) return PgsSeekPacketKind::Invalid;
                reset = state != 0;
                clear = data[10] == 0;
                size_t objects = 11;
                for (unsigned index = 0; index < data[10]; ++index)
                {
                    if (length - objects < 8) return PgsSeekPacketKind::Invalid;
                    const auto cropped = (data[objects + 3] & 0x80) != 0;
                    objects += 8;
                    if (cropped)
                    {
                        if (length - objects < 8) return PgsSeekPacketKind::Invalid;
                        objects += 8;
                    }
                }
                if (objects != length) return PgsSeekPacketKind::Invalid;
            }
            else if (type == 0x14)
            {
                if (length < 2 || (length - 2) % 5 != 0) return PgsSeekPacketKind::Invalid;
            }
            else if (type == 0x15)
            {
                if (length < 4) return PgsSeekPacketKind::Invalid;
                if ((data[3] & 0x80) != 0)
                {
                    if (length < 11) return PgsSeekPacketKind::Invalid;
                    const auto declared = (static_cast<size_t>(data[4]) << 16)
                        | (static_cast<size_t>(data[5]) << 8) | data[6];
                    const auto width = (static_cast<unsigned>(data[7]) << 8) | data[8];
                    const auto height = (static_cast<unsigned>(data[9]) << 8) | data[10];
                    if (declared < 4 || width == 0 || height == 0 || width > 16'384 || height > 16'384
                        || length - 7 > declared || (data[3] & 0x40) != 0 && length - 7 != declared)
                        return PgsSeekPacketKind::Invalid;
                }
            }
            else if (type == 0x17)
            {
                if (length < 1 || length != 1 + static_cast<size_t>(data[0]) * 9) return PgsSeekPacketKind::Invalid;
            }
            else if (type == 0x80)
            {
                if (length != 0) return PgsSeekPacketKind::Invalid;
                end = true;
            }
            else return PgsSeekPacketKind::Invalid;
            offset += length;
        }
        if (reset) return PgsSeekPacketKind::Reset;
        if (pcs && clear && end) return PgsSeekPacketKind::Clear;
        return PgsSeekPacketKind::Dependent;
    }

    void BeginIndexedPgsPrime()
    {
        std::lock_guard lock(mutex);
        Flush(true);
        indexedPgsPrimeActive = true;
        indexedPgsPrimeHasReset = false;
        indexedPgsPrimeHasCompleted = false;
        indexedPgsPrimeLastSourceTicks = -1;
        indexedPgsPrimeLastClear = false;
        indexedPgsPrimeLastCue = {};
        indexedPgsPrimeHasPending = false;
        indexedPgsPrimeCompositionKnown = false;
        indexedPgsPrimeCompositionObjectCount = 0;
    }

    // Ownership is always consumed, including invalid input and decoder failures.
    HRESULT DecodeIndexedPgsPrimePacket(AVPacket* packet)
    {
        std::unique_ptr<AVPacket, decltype(&ReleasePrimePacket)> owned(packet, ReleasePrimePacket);
        std::lock_guard lock(mutex);
        const auto kind = InspectIndexedPgsPacket(owned.get());
        if (!indexedPgsPrimeActive || kind == PgsSeekPacketKind::Invalid
            || !indexedPgsPrimeHasReset && kind != PgsSeekPacketKind::Reset)
        {
            indexedPgsPrimeHasCompleted = false;
            return E_INVALIDARG;
        }
        if (kind == PgsSeekPacketKind::Reset)
        {
            indexedPgsPrimeHasCompleted = false;
            indexedPgsPrimeLastSourceTicks = -1;
            indexedPgsPrimeLastCue = {};
        }
        for (size_t offset = 0; offset < static_cast<size_t>(owned->size);)
        {
            const auto type = owned->data[offset];
            const auto length = (static_cast<size_t>(owned->data[offset + 1]) << 8) | owned->data[offset + 2];
            offset += 3;
            if (type == 0x16)
            {
                indexedPgsPrimeCompositionKnown = true;
                indexedPgsPrimeCompositionObjectCount = owned->data[offset + 10];
            }
            offset += length;
        }
        std::unique_ptr<AVSubtitle, decltype(&ReleasePrimeSubtitle)> decoded(
            static_cast<AVSubtitle*>(av_mallocz(sizeof(AVSubtitle))), ReleasePrimeSubtitle);
        if (!decoded) { indexedPgsPrimeHasCompleted = false; return E_OUTOFMEMORY; }
        int gotSubtitle = 0;
        const auto result = avcodec_decode_subtitle2(m_pAvCodecCtx, decoded.get(), &gotSubtitle, owned.get());
        if (result < 0) { indexedPgsPrimeHasCompleted = false; return E_FAIL; }
        if (kind == PgsSeekPacketKind::Reset) indexedPgsPrimeHasReset = true;
        if (!gotSubtitle)
        {
            // Partial display sets update the same decoder, but must not pass through
            // the base queue's clear/infinite-duration boundary until END completes.
            indexedPgsPrimeHasPending = true;
            return S_FALSE;
        }
        if (!indexedPgsPrimeCompositionKnown || decoded->num_rects != indexedPgsPrimeCompositionObjectCount)
        {
            indexedPgsPrimeHasCompleted = false;
            return E_FAIL;
        }
        indexedPgsPrimePacketActive = true;
        indexedPgsPrimePacketResult = E_FAIL;
        indexedPgsPrimePacketClear = false;
        indexedPgsPrimePacketSourceTicks = -1;
        indexedPgsPrimePacketCue = {};
        indexedPgsPrimePreparedSubtitle = decoded.release();
        // Only a real completed decoder result reaches the base queue. CreateCue
        // consumes the prepared result without invoking a second subtitle decoder.
        SubtitleProvider::QueuePacket(owned.release());
        indexedPgsPrimePacketActive = false;
        if (indexedPgsPrimePreparedSubtitle)
        {
            ReleasePrimeSubtitle(indexedPgsPrimePreparedSubtitle);
            indexedPgsPrimePreparedSubtitle = nullptr;
            indexedPgsPrimePacketResult = E_FAIL;
        }
        if (FAILED(indexedPgsPrimePacketResult))
        {
            indexedPgsPrimeHasCompleted = false;
            return indexedPgsPrimePacketResult;
        }
        if (indexedPgsPrimePacketSourceTicks < 0)
        {
            indexedPgsPrimeHasCompleted = false;
            return E_FAIL;
        }
        if (!indexedPgsPrimePacketClear)
        {
            const auto found = map.find(indexedPgsPrimePacketCue);
            if (found == map.end() || !found->second.Cue || found->second.Cue.Duration().count() <= 0)
            {
                indexedPgsPrimeHasCompleted = false;
                return E_FAIL;
            }
        }
        indexedPgsPrimeHasCompleted = true;
        indexedPgsPrimeHasPending = false;
        indexedPgsPrimeLastSourceTicks = indexedPgsPrimePacketSourceTicks;
        indexedPgsPrimeLastClear = indexedPgsPrimePacketClear;
        indexedPgsPrimeLastCue = indexedPgsPrimePacketCue;
        return S_OK;
    }

    bool HasIndexedPgsPrimeState(TimeSpan target)
    {
        std::lock_guard lock(mutex);
        if (!indexedPgsPrimeActive || !indexedPgsPrimeHasReset || !indexedPgsPrimeHasCompleted || indexedPgsPrimeHasPending
            || indexedPgsPrimeLastSourceTicks < 0) return false;
        const auto delay = streamDelay.count();
        const auto position = target.count();
        if (delay > 0 && position < (std::numeric_limits<int64_t>::min)() + delay
            || delay < 0 && position > (std::numeric_limits<int64_t>::max)() + delay)
            return false;
        const auto sourcePosition = position - delay;
        if (indexedPgsPrimeLastSourceTicks > sourcePosition) return false;
        if (indexedPgsPrimeLastClear)
        {
            // A swallowed platform duration-update failure cannot turn a still
            // active decoded composition into a successful native clear.
            for (auto const& entry : map)
            {
                const auto& value = entry.second;
                const auto duration = value.SourceEndTicks >= value.SourceStartTicks
                    ? value.SourceEndTicks - value.SourceStartTicks : value.Cue.Duration().count();
                if (duration > 0 && sourcePosition >= value.SourceStartTicks
                    && static_cast<uint64_t>(sourcePosition) - static_cast<uint64_t>(value.SourceStartTicks)
                        < static_cast<uint64_t>(duration)) return false;
            }
            return true;
        }
        const auto found = map.find(indexedPgsPrimeLastCue);
        return found != map.end() && found->second.Cue && found->second.Cue.Duration().count() > 0;
    }

    SubtitleProviderBitmap(std::shared_ptr<FFmpegReader> reader,
        AVFormatContext* avFormatCtx,
        AVCodecContext* avCodecCtx,
        MediaSourceConfig const& config,
        int index)
        : SubtitleProvider(reader, avFormatCtx, avCodecCtx, config, index, TimedMetadataKind::ImageSubtitle)
    {
    }

    ~SubtitleProviderBitmap() override
    {
        ReleaseDecodedSubtitles();
    }

    virtual HRESULT Initialize() override
    {
        auto hr = SubtitleProvider::Initialize();

        if (SUCCEEDED(hr))
        {
            SubtitleTrack.CueEntered(weak_handler(this, &SubtitleProviderBitmap::OnCueEntered));
            SubtitleTrack.CueExited(weak_handler(this, &SubtitleProviderBitmap::OnCueExited));
        }

        return S_OK;
    }

    virtual void NotifyVideoFrameSize(int width, int height, double aspectRatio) override
    {
        std::lock_guard lock(mutex);
        videoWidth = width;
        videoHeight = height;
        if (isnormal(aspectRatio) && aspectRatio > 0)
        {
            videoAspectRatio = aspectRatio;
        }
        else
        {
            videoAspectRatio = (double)width / height;
        }
    }

    void QueuePacket(AVPacket* packet) override
    {
        // A seek must not clear a newly decoded composition between its map insertion and
        // the base provider's addition of the corresponding timed cue.
        std::lock_guard lock(mutex);
        SubtitleProvider::QueuePacket(packet);
    }

    // Presentation-mode changes need not emit CueEntered while paused. Only a composition
    // already decoded by this provider may be restored, at the real playback-session clock.
    ImageCue ReadImageCueAtPosition(TimeSpan mediaPosition, SoftwareBitmap& bitmap, TimedTextPoint& position,
        TimedTextSize& extent, TimeSpan& startTime)
    {
        std::lock_guard lock(mutex);
        bitmap = nullptr;
        startTime = TimeSpan{ -1 };
        if (indexedPgsPrimeActive && (!indexedPgsPrimeHasReset || !indexedPgsPrimeHasCompleted || indexedPgsPrimeHasPending))
            return nullptr;
        ImageCue current{ nullptr };
        DecodedSubtitle const* composition = nullptr;
        int64_t currentStartTicks = (std::numeric_limits<int64_t>::min)();
        // Live embedded cues first enter the hidden reference track. A paused seek may
        // decode a current composition without delivering that track's CueEntered event.
        // Query the provider-owned decoded cue, including its actual mutable duration,
        // rather than requiring the platform to have moved it into the visible track.
        for (auto const& item : map)
        {
            auto cue = item.second.Cue;
            if (!cue) continue;
            if (item.second.SourceStartTicks < 0) continue;
            const auto sourceStart = item.second.SourceStartTicks;
            const auto delay = streamDelay.count();
            // The platform can defer applying a new delay until a reference cue enters.
            // Keep the same decoded source timestamp and apply the current requested delay once.
            const auto shiftedStart = delay > 0 && sourceStart > (std::numeric_limits<int64_t>::max)() - delay
                ? (std::numeric_limits<int64_t>::max)() : sourceStart + delay;
            const auto duration = item.second.SourceEndTicks >= sourceStart
                ? item.second.SourceEndTicks - sourceStart : cue.Duration().count();
            if (duration <= 0 || mediaPosition.count() < 0 || mediaPosition.count() < shiftedStart)
                continue;
            // Preserve the unclipped start when testing expiry. Moving a negative start to zero
            // must not extend that cue. Unsigned subtraction also handles the full signed range.
            const auto elapsed = static_cast<uint64_t>(mediaPosition.count()) - static_cast<uint64_t>(shiftedStart);
            if (elapsed >= static_cast<uint64_t>(duration)) continue;
            if (!current || shiftedStart >= currentStartTicks)
            {
                current = cue;
                composition = &item.second;
                currentStartTicks = shiftedStart;
                startTime = TimeSpan{ (std::max)(int64_t{ 0 }, shiftedStart) };
            }
        }
        if (!current) return nullptr;
        bitmap = current.SoftwareBitmap();
        if (!bitmap || bitmap == dummyBitmap)
        {
            // CueExited releases its pixels when Off is selected. Materialize the same decoded
            // composition again; do not read packets, seek, or run another subtitle decoder.
            CreateSubtitleImage(current, composition->Subtitle);
            bitmap = current.SoftwareBitmap();
        }
        if (bitmap == dummyBitmap) bitmap = nullptr;
        position = current.Position();
        extent = current.Extent();
        return current;
    }

    virtual IMediaCue CreateCue(AVPacket* packet, TimeSpan* position, TimeSpan* duration) override
    {
        std::lock_guard lock(mutex);
        AVSubtitle* subtitle = indexedPgsPrimePacketActive ? indexedPgsPrimePreparedSubtitle
            : static_cast<AVSubtitle*>(av_mallocz(sizeof(AVSubtitle)));
        if (indexedPgsPrimePacketActive) indexedPgsPrimePreparedSubtitle = nullptr;
        if (!subtitle)
        {
            if (indexedPgsPrimePacketActive) indexedPgsPrimePacketResult = E_OUTOFMEMORY;
            return nullptr;
        }

        int gotSubtitle = indexedPgsPrimePacketActive ? 1 : 0;
        auto result = indexedPgsPrimePacketActive ? 1 : avcodec_decode_subtitle2(m_pAvCodecCtx, subtitle, &gotSubtitle, packet);
        if (indexedPgsPrimePacketActive)
        {
            indexedPgsPrimePacketResult = result < 0 ? E_FAIL : S_FALSE;
            indexedPgsPrimePacketSourceTicks = position->count();
        }
        if (result > 0 && gotSubtitle)
        {
            if (subtitle->start_display_time > 0)
            {
                *position = TimeSpan{ position->count() + (long long)10000 * subtitle->start_display_time };
            }
            *duration = TimeSpan{ (long long)10000 * subtitle->end_display_time };

            // Keep the unshifted source end independently of a platform cue whose
            // negative start may have been clipped to zero before a later clear.
            for (auto& entry : map)
                if (entry.second.SourceInfinite && entry.second.SourceStartTicks <= position->count())
                {
                    entry.second.SourceEndTicks = position->count();
                    entry.second.SourceInfinite = false;
                }

            if (subtitle->num_rects <= 0)
            {
                if (indexedPgsPrimePacketActive)
                {
                    indexedPgsPrimePacketResult = S_OK;
                    indexedPgsPrimePacketClear = true;
                    indexedPgsPrimePacketSourceTicks = position->count();
                }
                // return empty cue to force clear previous
                avsubtitle_free(subtitle);
                av_freep(&subtitle);
                return nullptr;
            }
            else
            {
                int width, height, offsetX, offsetY;
                TimedTextSize cueSize;
                TimedTextPoint cuePosition;
                if (subtitle->num_rects > 0 && CheckSize(subtitle, width, height, offsetX, offsetY, cueSize, cuePosition))
                {
                    bool retained = false;
                    try
                    {
                        auto id = winrt::to_hstring(nextId++);
                        ImageCue cue;
                        cue.Id(id);
                        map[id] = { subtitle, position->count(), cue, -1,
                            duration->count() >= 0xFFFFFFFFLL * 10000 };
                        retained = true;
                        if (indexedPgsPrimePacketActive)
                        {
                            indexedPgsPrimePacketResult = S_OK;
                            indexedPgsPrimePacketSourceTicks = position->count();
                            indexedPgsPrimePacketCue = id;
                        }
                        return cue;
                    }
                    catch (...)
                    {
                        if (!retained) ReleasePrimeSubtitle(subtitle);
                        if (indexedPgsPrimePacketActive) indexedPgsPrimePacketResult = E_FAIL;
                        throw;
                    }
                }
                else
                {
                    if (indexedPgsPrimePacketActive) indexedPgsPrimePacketResult = E_INVALIDARG;
                    const auto hadRects = subtitle->num_rects > 0;
                    avsubtitle_free(subtitle);
                    av_freep(&subtitle);
                    if (hadRects)
                    {
                        OutputDebugString(L"Error: Invalid subtitle size received.");
                    }
                }
            }
        }
        else
        {
            avsubtitle_free(subtitle);
            av_freep(&subtitle);
            OutputDebugString(L"Failed to decode subtitle.");
        }
        return nullptr;
    }

public:

    void Flush(bool flushBuffers) override
    {
        std::lock_guard lock(mutex);
        SubtitleProvider::Flush(flushBuffers);

        if (!m_config.as<implementation::MediaSourceConfig>()->IsExternalSubtitleParser && flushBuffers)
        {
            ReleaseDecodedSubtitles();
            indexedPgsPrimeActive = false;
            indexedPgsPrimePacketActive = false;
            indexedPgsPrimeHasReset = false;
            indexedPgsPrimeHasCompleted = false;
            indexedPgsPrimeHasPending = false;
        }
    }

private:

    void OnCueEntered(TimedMetadataTrack sender, MediaCueEventArgs args)
    {
        UNREFERENCED_PARAMETER(sender);
        std::lock_guard lock(mutex);
        try
        {
            auto cue = args.Cue().try_as<ImageCue>();
            if (cue && !cue.Id().empty())
            {
                auto image = cue.SoftwareBitmap();
                if (!image || image == GetDummyBitmap())
                {
                    auto subtitle = map.find(cue.Id());
                    if (subtitle != map.end())
                    {
                        CreateSubtitleImage(cue, subtitle->second.Subtitle);
                    }
                }
            }
        }
        catch (...)
        {
            OutputDebugString(L"Failed to render image cue.");
        }
    }

    void OnCueExited(TimedMetadataTrack sender, MediaCueEventArgs args)
    {
        UNREFERENCED_PARAMETER(sender);
        std::lock_guard lock(mutex);
        try
        {
            auto cue = args.Cue().try_as<ImageCue>();
            if (cue && !cue.Id().empty() && cue.SoftwareBitmap())
            {
                cue.SoftwareBitmap(GetDummyBitmap());
            }
        }
        catch (...)
        {
            OutputDebugString(L"Failed to clear image cue.");
        }
    }

    void CreateSubtitleImage(ImageCue cue, AVSubtitle* subtitle)
    {
        int width, height, offsetX, offsetY;
        TimedTextSize cueSize;
        TimedTextPoint cuePosition;
        if (subtitle->num_rects > 0 && CheckSize(subtitle, width, height, offsetX, offsetY, cueSize, cuePosition))
        {
            auto bitmap = SoftwareBitmap(BitmapPixelFormat::Bgra8, width, height, BitmapAlphaMode::Straight);
            {
                auto buffer = bitmap.LockBuffer(BitmapBufferAccessMode::Write);
                auto reference = buffer.CreateReference();
                BYTE* pixels = reference.data();

                auto plane = buffer.GetPlaneDescription(0);

                for (unsigned int i = 0; i < subtitle->num_rects; i++)
                {
                    auto rect = subtitle->rects[i];

                    for (int y = 0; y < rect->h; y++)
                    {
                        for (int x = 0; x < rect->w; x++)
                        {
                            auto inPointer = rect->data[0] + y * rect->linesize[0] + x;
                            auto color = inPointer[0];
                            if (color < rect->nb_colors)
                            {
                                auto rgba = ((uint32_t*)rect->data[1])[color];
                                auto outPointer = pixels + plane.StartIndex + plane.Stride * ((y + rect->y) - offsetY) + 4 * ((x + rect->x) - offsetX);
                                ((uint32_t*)outPointer)[0] = rgba;
                            }
                            else
                            {
                                OutputDebugString(L"Error: Illegal subtitle color.");
                            }
                        }
                    }
                }
            }

            auto converted = SoftwareBitmap::Convert(bitmap, BitmapPixelFormat::Bgra8, BitmapAlphaMode::Premultiplied);
            cue.SoftwareBitmap(converted);
            cue.Position(cuePosition);
            cue.Extent(cueSize);
        }
        else if (subtitle->num_rects > 0)
        {
            OutputDebugString(L"Error: Invalid subtitle size received.");
        }
    }

    SoftwareBitmap GetDummyBitmap()
    {
        if (!dummyBitmap)
        {
            dummyBitmap = SoftwareBitmap(BitmapPixelFormat::Bgra8, 16, 16, BitmapAlphaMode::Premultiplied);
        }

        return dummyBitmap;
    }

    bool CheckSize(AVSubtitle* subtitle, int& width, int& height, int& offsetX, int& offsetY,TimedTextSize& cueSize,TimedTextPoint& cuePosition)
    {
        if (!GetInitialSize())
        {
            return false;
        }

        // get actual extent of subtitle rects
        int minX = subtitleWidth, minY = subtitleHeight, maxW = 0, maxH = 0;
        for (unsigned int i = 0; i < subtitle->num_rects; i++)
        {
            auto rect = subtitle->rects[i];
            minX = min(minX, rect->x);
            minY = min(minY, rect->y);
            maxW = max(maxW, rect->x + rect->w);
            maxH = max(maxH, rect->y + rect->h);
        }

        // sanity check
        if (minX < 0 || minY < 0 || maxW > subtitleWidth || maxH > subtitleHeight)
        {
            return false;
        }

        offsetX = minX;
        offsetY = minY;
        width = maxW - minX;
        height = maxH - minY;

        // try to fit into actual video frame aspect ratio, if aspect of sub is different from video
        int heightOffset = 0;
        int targetHeight = subtitleHeight;
        if (optimalHeight)
        {
            heightOffset = (subtitleHeight - optimalHeight) / 2;
            targetHeight = optimalHeight;

            // if subtitle does not fit into optimal height, fall back to normal height
            if (maxH > optimalHeight + heightOffset || minY < heightOffset)
            {
                optimalHeight = 0;
                heightOffset = 0;
                targetHeight = subtitleHeight;
            }
        }

        cueSize.Unit =TimedTextUnit::Percentage;
        cueSize.Width = (double)width * 100 / subtitleWidth;
        cueSize.Height = (double)height * 100 / targetHeight;

        // for some reason, all bitmap cues are moved down by 5% by uwp. we need to compensate for that.
        cuePosition.Unit =TimedTextUnit::Percentage;
        cuePosition.X = (double)offsetX * 100 / subtitleWidth;
        cuePosition.Y = ((double)(offsetY - heightOffset) * 100 / targetHeight) - 5;

        return true;
    }

    bool GetInitialSize()
    {
        if (!hasSize)
        {
            // initially get size information
            subtitleWidth = m_pAvCodecCtx->width;
            subtitleHeight = m_pAvCodecCtx->height;

            if (subtitleWidth == 0 && subtitleHeight == 0)
            {
                OutputDebugString(L"Warning: No subtitle size received. Assuming equal to video size.\n");
                subtitleWidth = videoWidth;
                subtitleHeight = videoHeight;
            }

            if (subtitleWidth > 0 && subtitleHeight > 0)
            {
                if (subtitleWidth != videoWidth || subtitleHeight != videoHeight || (videoAspectRatio > 0 && videoAspectRatio != 1))
                {
                    auto height = (int)(subtitleWidth / videoAspectRatio);
                    if (height < subtitleHeight)
                    {
                        optimalHeight = height;
                    }
                }

                hasSize = true;
            }
        }
        return hasSize;
    }

private:
    struct DecodedSubtitle
    {
        AVSubtitle* Subtitle;
        int64_t SourceStartTicks;
        ImageCue Cue{ nullptr };
        int64_t SourceEndTicks = -1;
        bool SourceInfinite = false;
    };

    void ReleaseDecodedSubtitles() noexcept
    {
        if (indexedPgsPrimePreparedSubtitle)
        {
            ReleasePrimeSubtitle(indexedPgsPrimePreparedSubtitle);
            indexedPgsPrimePreparedSubtitle = nullptr;
        }
        for (const auto& entry : map)
        {
            auto subtitle = entry.second.Subtitle;
            avsubtitle_free(subtitle);
            av_freep(&subtitle);
        }
        map.clear();
    }

    int videoWidth = 0;
    int videoHeight = 0;
    double videoAspectRatio = 0.0;
    bool hasSize = false;
    int subtitleWidth = 0;
    int subtitleHeight = 0;
    int optimalHeight = 0;
    SoftwareBitmap dummyBitmap = { nullptr };
    std::map<winrt::hstring, DecodedSubtitle> map;
    int nextId = 0;
    static void ReleasePrimePacket(AVPacket* value) noexcept { av_packet_free(&value); }
    static void ReleasePrimeSubtitle(AVSubtitle* value) noexcept
    {
        if (!value) return;
        avsubtitle_free(value);
        av_freep(&value);
    }
    bool indexedPgsPrimeActive = false;
    bool indexedPgsPrimeHasReset = false;
    bool indexedPgsPrimeHasCompleted = false;
    bool indexedPgsPrimePacketActive = false;
    bool indexedPgsPrimeHasPending = false;
    bool indexedPgsPrimeCompositionKnown = false;
    unsigned indexedPgsPrimeCompositionObjectCount = 0;
    AVSubtitle* indexedPgsPrimePreparedSubtitle = nullptr;
    HRESULT indexedPgsPrimePacketResult = E_FAIL;
    bool indexedPgsPrimePacketClear = false;
    int64_t indexedPgsPrimePacketSourceTicks = -1;
    winrt::hstring indexedPgsPrimePacketCue{};
    bool indexedPgsPrimeLastClear = false;
    int64_t indexedPgsPrimeLastSourceTicks = -1;
    winrt::hstring indexedPgsPrimeLastCue{};
};
