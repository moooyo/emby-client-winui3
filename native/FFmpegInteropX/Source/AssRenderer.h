#pragma once

#include <cstdint>
#include <span>
#include <string>
#include <unordered_map>
#include <vector>

extern "C"
{
#include <ass/ass.h>
#include <libavutil/pixfmt.h>
}

namespace EmbyNativeSubtitles
{
    struct AssVideoGeometry
    {
        int Width = 0;
        int Height = 0;
        double PixelAspectRatio = 1.0;
        AVColorSpace ColorSpace = AVCOL_SPC_UNSPECIFIED;
        AVColorRange ColorRange = AVCOL_RANGE_UNSPECIFIED;
        bool Hdr = false;
    };

    struct AssFontAttachment
    {
        std::string Name;
        std::vector<uint8_t> Bytes;
    };

    struct AssRenderedBitmap
    {
        bool Changed = false;
        bool HasImage = false;
        int X = 0;
        int Y = 0;
        int Width = 0;
        int Height = 0;
        std::vector<uint8_t> PremultipliedBgra;
    };

    // A real libass renderer. The owning subtitle track serializes every operation.
    class AssRenderer final
    {
    public:
        static constexpr size_t MaximumDocumentBytes = 4u * 1024u * 1024u;
        static constexpr size_t MaximumEventBytes = 1024u * 1024u;
        static constexpr size_t MaximumReplayBytes = 16u * 1024u * 1024u;
        static constexpr size_t MaximumFontBytes = 16u * 1024u * 1024u;
        static constexpr size_t MaximumTotalFontBytes = 64u * 1024u * 1024u;
        static constexpr size_t MaximumFontCount = 128;
        static constexpr int MaximumEvents = 50'000;
        static constexpr int MaximumStyles = 4096;
        static constexpr size_t MaximumBitmapBytes = 64u * 1024u * 1024u;

        AssRenderer();
        ~AssRenderer();
        AssRenderer(AssRenderer const&) = delete;
        AssRenderer& operator=(AssRenderer const&) = delete;

        void LoadDocument(std::span<const uint8_t> utf8);
        void InitializeEmbedded(std::span<const uint8_t> codecPrivate);
        void AddFont(AssFontAttachment const& attachment);
        void FeedEvent(std::span<const char> event, int64_t startTicks, int64_t durationTicks);
        AssRenderedBitmap Render(int64_t scriptPositionTicks, AssVideoGeometry const& geometry);
        void FlushEmbeddedEvents();
        void InvalidateImage() noexcept;
        uint32_t EventCount() const noexcept;

    private:
        struct EventChunk
        {
            std::string Text;
            int64_t StartMilliseconds;
            int64_t DurationMilliseconds;
        };

        void EnsureRenderer();
        void ValidateTrack(bool requireEvents) const;
        uint32_t MapColor(uint32_t rgba, AssVideoGeometry const& geometry) const noexcept;

        ASS_Library* m_library = nullptr;
        ASS_Renderer* m_renderer = nullptr;
        ASS_Track* m_track = nullptr;
        AssVideoGeometry m_geometry{};
        bool m_forceImage = true;
        bool m_geometrySet = false;
        bool m_embedded = false;
        size_t m_fontBytes = 0;
        size_t m_fontCount = 0;
        size_t m_replayBytes = 0;
        std::vector<EventChunk> m_replay;
        std::unordered_map<uint64_t, std::vector<size_t>> m_replayIndex;
    };
}
