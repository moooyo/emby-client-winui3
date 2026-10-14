#include "pch.h"
#include "AssRenderer.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

namespace EmbyNativeSubtitles
{
    namespace
    {
        struct MatrixCoefficients
        {
            double Red;
            double Blue;
            bool Limited;
            bool Valid;
        };

        MatrixCoefficients ScriptMatrix(ASS_YCbCrMatrix matrix) noexcept
        {
            switch (matrix)
            {
            case YCBCR_NONE: return { 0, 0, false, false };
            case YCBCR_BT601_PC: return { 0.299, 0.114, false, true };
            case YCBCR_BT709_TV: return { 0.2126, 0.0722, true, true };
            case YCBCR_BT709_PC: return { 0.2126, 0.0722, false, true };
            case YCBCR_SMPTE240M_TV: return { 0.212, 0.087, true, true };
            case YCBCR_SMPTE240M_PC: return { 0.212, 0.087, false, true };
            case YCBCR_FCC_TV: return { 0.30, 0.11, true, true };
            case YCBCR_FCC_PC: return { 0.30, 0.11, false, true };
            case YCBCR_DEFAULT:
            case YCBCR_UNKNOWN:
            case YCBCR_BT601_TV:
            default: return { 0.299, 0.114, true, true };
            }
        }

        MatrixCoefficients VideoMatrix(AssVideoGeometry const& geometry) noexcept
        {
            const bool limited = geometry.ColorRange == AVCOL_RANGE_MPEG;
            if (geometry.ColorRange != AVCOL_RANGE_MPEG && geometry.ColorRange != AVCOL_RANGE_JPEG)
                return { 0, 0, false, false };
            switch (geometry.ColorSpace)
            {
            case AVCOL_SPC_BT709: return { 0.2126, 0.0722, limited, true };
            case AVCOL_SPC_BT470BG:
            case AVCOL_SPC_SMPTE170M: return { 0.299, 0.114, limited, true };
            case AVCOL_SPC_SMPTE240M: return { 0.212, 0.087, limited, true };
            case AVCOL_SPC_FCC: return { 0.30, 0.11, limited, true };
            case AVCOL_SPC_BT2020_NCL: return { 0.2627, 0.0593, limited, true };
            default: return { 0, 0, false, false };
            }
        }

        uint8_t Quantize(double value) noexcept
        {
            return static_cast<uint8_t>(std::clamp(std::lround(value * 255.0), 0L, 255L));
        }

        int64_t FloorMilliseconds(int64_t ticks) noexcept
        {
            const auto result = ticks / 10'000;
            return result - (ticks < 0 && ticks % 10'000 != 0 ? 1 : 0);
        }

        uint64_t ChunkHash(std::span<const char> text, int64_t start, int64_t duration) noexcept
        {
            uint64_t result = 14695981039346656037ULL;
            for (const auto character : text)
            {
                result ^= static_cast<uint8_t>(character);
                result *= 1099511628211ULL;
            }
            for (const auto value : { static_cast<uint64_t>(start), static_cast<uint64_t>(duration) })
            {
                for (unsigned int shift = 0; shift < 64; shift += 8)
                {
                    result ^= (value >> shift) & 255;
                    result *= 1099511628211ULL;
                }
            }
            return result;
        }

        void RejectInvalidDocument(std::span<const uint8_t> data)
        {
            if (data.empty() || data.size() > AssRenderer::MaximumDocumentBytes
                || std::find(data.begin(), data.end(), uint8_t{ 0 }) != data.end())
                winrt::throw_hresult(E_INVALIDARG);
            if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                reinterpret_cast<char const*>(data.data()), static_cast<int>(data.size()), nullptr, 0) <= 0)
                winrt::throw_hresult(E_INVALIDARG);
        }
    }

    AssRenderer::AssRenderer()
    {
        m_library = ass_library_init();
        if (!m_library) winrt::throw_hresult(E_OUTOFMEMORY);
        // ASS [Fonts] records are handled by libass and remain in memory.
        ass_set_extract_fonts(m_library, 1);
    }

    AssRenderer::~AssRenderer()
    {
        if (m_track) ass_free_track(m_track);
        if (m_renderer) ass_renderer_done(m_renderer);
        if (m_library) ass_library_done(m_library);
    }

    void AssRenderer::EnsureRenderer()
    {
        if (m_renderer) return;
        m_renderer = ass_renderer_init(m_library);
        if (!m_renderer) winrt::throw_hresult(E_OUTOFMEMORY);
        ass_set_cache_limits(m_renderer, 4096, 16);
        // DirectWrite resolves installed fonts; actual attachments have already been registered.
        ass_set_fonts(m_renderer, nullptr, "Arial", ASS_FONTPROVIDER_DIRECTWRITE, nullptr, 1);
        ass_set_hinting(m_renderer, ASS_HINTING_NONE);
    }

    void AssRenderer::ValidateTrack(bool requireEvents) const
    {
        if (!m_track || m_track->n_styles <= 0 || m_track->n_styles > MaximumStyles
            || m_track->n_events < 0 || m_track->n_events > MaximumEvents
            || (requireEvents && m_track->n_events == 0))
            winrt::throw_hresult(E_INVALIDARG);
        for (int index = 0; index < m_track->n_events; ++index)
        {
            auto const& item = m_track->events[index];
            if (item.Duration <= 0 || item.Start > (std::numeric_limits<int64_t>::max)() / 10'000
                || item.Start < (std::numeric_limits<int64_t>::min)() / 10'000
                || item.Duration > (std::numeric_limits<int64_t>::max)() / 10'000
                || item.Start > (std::numeric_limits<int64_t>::max)() / 10'000 - item.Duration)
                winrt::throw_hresult(E_INVALIDARG);
        }
    }

    void AssRenderer::LoadDocument(std::span<const uint8_t> utf8)
    {
        RejectInvalidDocument(utf8);
        if (m_track) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        // libass owns parsed events. The original document and all override commands are preserved.
        auto owned = std::vector<char>(utf8.begin(), utf8.end());
        m_track = ass_read_memory(m_library, owned.data(), owned.size(), "UTF-8");
        ValidateTrack(true);
        m_embedded = false;
        m_forceImage = true;
    }

    void AssRenderer::InitializeEmbedded(std::span<const uint8_t> codecPrivate)
    {
        while (!codecPrivate.empty() && codecPrivate.back() == 0)
            codecPrivate = codecPrivate.first(codecPrivate.size() - 1);
        RejectInvalidDocument(codecPrivate);
        if (m_track) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_STATE));
        m_track = ass_new_track(m_library);
        if (!m_track) winrt::throw_hresult(E_OUTOFMEMORY);
        ass_process_codec_private(m_track, reinterpret_cast<char const*>(codecPrivate.data()),
            static_cast<int>(codecPrivate.size()));
        ValidateTrack(false);
        ass_set_check_readorder(m_track, 1);
        // Rendering can prune old libass events. The bounded raw chunk cache restores admitted events on seek.
        ass_configure_prune(m_track, 60'000);
        m_embedded = true;
        m_forceImage = true;
    }

    void AssRenderer::AddFont(AssFontAttachment const& attachment)
    {
        if (attachment.Name.empty() || attachment.Name.size() > 1024 || attachment.Bytes.empty()
            || attachment.Bytes.size() > MaximumFontBytes || m_fontCount >= MaximumFontCount
            || attachment.Bytes.size() > MaximumTotalFontBytes - m_fontBytes || m_renderer)
            winrt::throw_hresult(E_INVALIDARG);
        ass_add_font(m_library, attachment.Name.c_str(), reinterpret_cast<char const*>(attachment.Bytes.data()),
            static_cast<int>(attachment.Bytes.size()));
        m_fontBytes += attachment.Bytes.size();
        ++m_fontCount;
    }

    void AssRenderer::FeedEvent(std::span<const char> event, int64_t startTicks, int64_t durationTicks)
    {
        if (!m_embedded || !m_track || event.empty() || event.size() > MaximumEventBytes
            || std::find(event.begin(), event.end(), '\0') != event.end() || durationTicks <= 0)
            winrt::throw_hresult(E_INVALIDARG);
        const auto start = FloorMilliseconds(startTicks);
        const auto duration = durationTicks / 10'000 + (durationTicks % 10'000 == 0 ? 0 : 1);
        const auto hash = ChunkHash(event, start, duration);
        if (const auto found = m_replayIndex.find(hash); found != m_replayIndex.end())
        {
            // Hashes only narrow the lookup. Actual content and timestamps determine equality.
            for (auto index : found->second)
            {
                auto const& previous = m_replay[index];
                if (previous.StartMilliseconds == start && previous.DurationMilliseconds == duration
                    && previous.Text.size() == event.size()
                    && std::equal(event.begin(), event.end(), previous.Text.begin())) return;
            }
        }
        if (m_replay.size() >= MaximumEvents || m_track->n_events >= MaximumEvents
            || event.size() > MaximumReplayBytes - m_replayBytes)
            winrt::throw_hresult(E_OUTOFMEMORY);
        EventChunk admitted{ std::string(event.data(), event.size()), start, duration };
        const auto countBefore = m_track->n_events;
        ass_process_chunk(m_track, event.data(), static_cast<int>(event.size()), start, duration);
        if (m_track->n_events != countBefore + 1 || m_track->n_events > MaximumEvents || m_track->n_styles > MaximumStyles)
            winrt::throw_hresult(E_INVALIDARG);
        const auto index = m_replay.size();
        m_replay.push_back(std::move(admitted));
        m_replayIndex[hash].push_back(index);
        m_replayBytes += event.size();
    }

    uint32_t AssRenderer::MapColor(uint32_t rgba, AssVideoGeometry const& geometry) const noexcept
    {
        // libass deliberately delegates ASS YCbCr Matrix compatibility to the presenter.
        // HDR subtitle images remain SDR; Windows composes them separately from the P010 video.
        if (!m_track || geometry.Hdr || geometry.ColorSpace == AVCOL_SPC_RGB) return rgba;
        const auto script = ScriptMatrix(m_track->YCbCrMatrix);
        const auto video = VideoMatrix(geometry);
        if (!script.Valid || !video.Valid) return rgba;
        const double r = ((rgba >> 24) & 255) / 255.0;
        const double g = ((rgba >> 16) & 255) / 255.0;
        const double b = ((rgba >> 8) & 255) / 255.0;
        const double y = script.Red * r + (1.0 - script.Red - script.Blue) * g + script.Blue * b;
        const double cb = (b - y) / (2.0 * (1.0 - script.Blue));
        const double cr = (r - y) / (2.0 * (1.0 - script.Red));
        const double encodedY = script.Limited ? 16.0 + 219.0 * y : 255.0 * y;
        const double encodedCb = 128.0 + (script.Limited ? 224.0 : 255.0) * cb;
        const double encodedCr = 128.0 + (script.Limited ? 224.0 : 255.0) * cr;
        const double decodedY = (encodedY - (video.Limited ? 16.0 : 0.0)) / (video.Limited ? 219.0 : 255.0);
        const double decodedCb = (encodedCb - 128.0) / (video.Limited ? 224.0 : 255.0);
        const double decodedCr = (encodedCr - 128.0) / (video.Limited ? 224.0 : 255.0);
        const double outR = decodedY + 2.0 * (1.0 - video.Red) * decodedCr;
        const double outB = decodedY + 2.0 * (1.0 - video.Blue) * decodedCb;
        const double outG = (decodedY - video.Red * outR - video.Blue * outB)
            / (1.0 - video.Red - video.Blue);
        return uint32_t{ Quantize(outR) } << 24 | uint32_t{ Quantize(outG) } << 16
            | uint32_t{ Quantize(outB) } << 8 | (rgba & 255);
    }

    AssRenderedBitmap AssRenderer::Render(int64_t scriptPositionTicks, AssVideoGeometry const& geometry)
    {
        if (!m_track || geometry.Width <= 0 || geometry.Height <= 0 || geometry.Width > 16'384
            || geometry.Height > 16'384 || !std::isfinite(geometry.PixelAspectRatio)
            || geometry.PixelAspectRatio <= 0 || geometry.PixelAspectRatio > 100)
            winrt::throw_hresult(E_INVALIDARG);
        EnsureRenderer();
        const bool geometryChanged = !m_geometrySet || geometry.Width != m_geometry.Width
            || geometry.Height != m_geometry.Height || geometry.PixelAspectRatio != m_geometry.PixelAspectRatio
            || geometry.ColorSpace != m_geometry.ColorSpace || geometry.ColorRange != m_geometry.ColorRange
            || geometry.Hdr != m_geometry.Hdr;
        if (geometryChanged)
        {
            ass_set_frame_size(m_renderer, geometry.Width, geometry.Height);
            ass_set_storage_size(m_renderer, geometry.Width, geometry.Height);
            ass_set_pixel_aspect(m_renderer, geometry.PixelAspectRatio);
            m_geometry = geometry;
            m_geometrySet = true;
        }
        int changed = 0;
        ASS_Image* images = ass_render_frame(m_renderer, m_track, FloorMilliseconds(scriptPositionTicks), &changed);
        AssRenderedBitmap result;
        result.Changed = changed != 0 || m_forceImage || geometryChanged;
        int left = geometry.Width, top = geometry.Height, right = 0, bottom = 0;
        for (auto image = images; image; image = image->next)
        {
            if (image->w <= 0 || image->h <= 0) continue;
            if (!image->bitmap || image->stride < image->w || image->dst_x < 0 || image->dst_y < 0
                || image->w > geometry.Width - image->dst_x || image->h > geometry.Height - image->dst_y)
                winrt::throw_hresult(E_INVALIDARG);
            left = (std::min)(left, image->dst_x);
            top = (std::min)(top, image->dst_y);
            right = (std::max)(right, image->dst_x + image->w);
            bottom = (std::max)(bottom, image->dst_y + image->h);
        }
        result.HasImage = right > left && bottom > top;
        m_forceImage = false;
        if (!result.HasImage || !result.Changed) return result;
        result.X = left;
        result.Y = top;
        result.Width = right - left;
        result.Height = bottom - top;
        const auto pixels = static_cast<size_t>(result.Width) * static_cast<size_t>(result.Height);
        if (pixels > MaximumBitmapBytes / 4) winrt::throw_hresult(E_OUTOFMEMORY);
        result.PremultipliedBgra.resize(pixels * 4, 0);
        // libass returns ordered character/outline/shadow masks with inverted color alpha.
        for (auto image = images; image; image = image->next)
        {
            if (image->w <= 0 || image->h <= 0) continue;
            const auto color = MapColor(image->color, geometry);
            const uint32_t channels[3] = { (color >> 8) & 255, (color >> 16) & 255, (color >> 24) & 255 };
            const uint32_t opacity = 255 - (color & 255);
            for (int y = 0; y < image->h; ++y)
            {
                auto input = image->bitmap + static_cast<size_t>(y) * image->stride;
                auto output = result.PremultipliedBgra.data()
                    + (static_cast<size_t>(image->dst_y - top + y) * result.Width + image->dst_x - left) * 4;
                for (int x = 0; x < image->w; ++x, output += 4)
                {
                    const uint32_t alpha = (uint32_t{ input[x] } * opacity + 127) / 255;
                    if (alpha == 0) continue;
                    const uint32_t inverse = 255 - alpha;
                    for (int channel = 0; channel < 3; ++channel)
                        output[channel] = static_cast<uint8_t>((channels[channel] * alpha + output[channel] * inverse + 127) / 255);
                    output[3] = static_cast<uint8_t>(alpha + (uint32_t{ output[3] } * inverse + 127) / 255);
                }
            }
        }
        return result;
    }

    void AssRenderer::FlushEmbeddedEvents()
    {
        if (m_embedded && m_track)
        {
            ass_flush_events(m_track);
            // Reuse only chunks decoded through normal playback. No second media reader or whole-file scan is opened.
            for (auto const& event : m_replay)
                ass_process_chunk(m_track, event.Text.data(), static_cast<int>(event.Text.size()),
                    event.StartMilliseconds, event.DurationMilliseconds);
            if (m_track->n_events != static_cast<int>(m_replay.size())) winrt::throw_hresult(E_UNEXPECTED);
        }
        m_forceImage = true;
    }

    void AssRenderer::InvalidateImage() noexcept { m_forceImage = true; }

    uint32_t AssRenderer::EventCount() const noexcept
    {
        return m_track && m_track->n_events > 0 ? static_cast<uint32_t>(m_track->n_events) : 0;
    }
}
