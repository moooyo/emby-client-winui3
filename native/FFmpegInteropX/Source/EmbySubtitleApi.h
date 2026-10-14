#pragma once

#include <stdint.h>
#include <unknwn.h>
#include <wchar.h>

// Numeric observations describe owned renderer work, never successful on-screen presentation.
#pragma pack(push, 8)
typedef struct AssSubtitleObservation
{
    uint32_t Size;
    uint32_t Version;
    int32_t Failure;
    uint32_t OwnedTrackCount;
    uint32_t PlatformPresentedTrackCount;
    uint32_t EnabledTrackCount;
    uint32_t CurrentImageCount;
    uint32_t PlatformActiveImageCount;
    uint32_t HiddenReferenceTrackCount;
    uint32_t VideoWidth;
    uint32_t VideoHeight;
    uint32_t Reserved;
    uint64_t VideoSampleCount;
    uint64_t RefreshCount;
    uint64_t ScheduledFrameCount;
    uint64_t ReferenceCueEnteredCount;
    uint64_t RenderCallCount;
    uint64_t ImageFrameCount;
    uint64_t ImageCueAddedCount;
    uint64_t ImageCueEnteredCount;
    uint64_t ImageCueExitedCount;
    uint64_t ClearCount;
    uint64_t StaleFrameCount;
    uint64_t PendingScheduledFrameCount;
    uint64_t LastBitmapNonzeroAlphaPixels;
    int64_t LastSamplePositionTicks;
    int64_t LastRenderPositionTicks;
    int64_t LastScriptPositionTicks;
    int32_t LastImageX;
    int32_t LastImageY;
    int32_t LastImageWidth;
    int32_t LastImageHeight;
} AssSubtitleObservation;

// Source-scoped latest-frame metadata. PresentationMode is 0=none, 1=platform, 2=application.
// Rectangle coordinates are raw video pixels; no platform subtitle padding is applied.
// TrackId is an internal owner identity, not a server media ID. Bitmap tracks use their
// actual cue start time and ScriptPositionTicks=-1. Snapshot storage may differ from the
// target rectangle; the consumer must scale the real pixels without assuming equal sizes.
typedef struct AssSubtitleFrame
{
    uint32_t Size;
    uint32_t Version;
    uint32_t HasImage;
    uint32_t PresentationMode;
    uint64_t TrackId;
    uint64_t Generation;
    uint64_t Sequence;
    int64_t MediaPositionTicks;
    int64_t ScriptPositionTicks;
    int32_t VideoWidth;
    int32_t VideoHeight;
    double PixelAspectRatio;
    int32_t X;
    int32_t Y;
    int32_t Width;
    int32_t Height;
    uint64_t NonzeroAlphaPixels;
} AssSubtitleFrame;
#pragma pack(pop)

#ifdef __cplusplus
#include <memory>
#include <winrt/Windows.Media.Core.h>
#include <winrt/Windows.Media.Playback.h>
#include <winrt/FFmpegInteropX.h>

class SubtitleProviderLibass;
class SubtitleProviderBitmap;
struct EmbyAssSubtitleState;
struct AVFormatContext;
struct AVCodecParameters;

static_assert(sizeof(AssSubtitleObservation) == 192, "The subtitle observation ABI must remain stable.");
static_assert(sizeof(AssSubtitleFrame) == 96, "The subtitle frame ABI must remain stable.");

// The source owns this state. No new projected WinRT interface is required.
std::shared_ptr<EmbyAssSubtitleState> EmbyCreateAssSubtitleState();
void EmbySetAssSourceAttachments(std::shared_ptr<EmbyAssSubtitleState> const& state, AVFormatContext* format);
void EmbyAttachAssMediaStreamSource(std::shared_ptr<EmbyAssSubtitleState> const& state,
    winrt::Windows::Media::Core::MediaStreamSource const& source);
void EmbyRegisterEmbeddedAssSubtitle(std::shared_ptr<EmbyAssSubtitleState> const& state,
    std::shared_ptr<SubtitleProviderLibass> const& provider);
void EmbyRegisterBitmapSubtitle(std::shared_ptr<EmbyAssSubtitleState> const& state,
    std::shared_ptr<SubtitleProviderBitmap> const& provider);
void EmbyAttachAssPlaybackItem(std::shared_ptr<EmbyAssSubtitleState> const& state,
    winrt::Windows::Media::Playback::MediaPlaybackItem const& item);
void EmbyAttachAssPlaybackSession(std::shared_ptr<EmbyAssSubtitleState> const& state,
    winrt::Windows::Media::Playback::MediaPlaybackSession const& session);
void EmbySetAssPresentationMode(std::shared_ptr<EmbyAssSubtitleState> const& state,
    winrt::Windows::Media::Core::TimedMetadataTrack const& track,
    winrt::Windows::Media::Playback::TimedMetadataTrackPresentationMode mode);
HRESULT EmbyRenderAssVideoSample(std::shared_ptr<EmbyAssSubtitleState> const& state,
    winrt::Windows::Media::Core::MediaStreamSample const& sample,
    winrt::FFmpegInteropX::VideoStreamInfo const& video,
    AVCodecParameters const* videoParameters = nullptr) noexcept;
void EmbyFlushAssSubtitles(std::shared_ptr<EmbyAssSubtitleState> const& state) noexcept;
void EmbyCloseAssSubtitles(std::shared_ptr<EmbyAssSubtitleState> const& state) noexcept;

#define EMBY_SUBTITLE_NOEXCEPT noexcept
extern "C"
{
#else
#define EMBY_SUBTITLE_NOEXCEPT
#endif

    __declspec(dllexport) uint32_t __cdecl EmbySubtitleApiVersion(void) EMBY_SUBTITLE_NOEXCEPT;
    __declspec(dllexport) uint32_t __cdecl EmbyLibassVersion(void) EMBY_SUBTITLE_NOEXCEPT;

    // Parsing preserves the complete script, including its styles, effects and font records.
    __declspec(dllexport) HRESULT __cdecl EmbyValidateAssSubtitleDocument(
        const uint8_t* utf8, uint32_t bytes, uint32_t* eventCount) EMBY_SUBTITLE_NOEXCEPT;

    // The returned track owns one ABI reference. The caller attaches it to the current MediaSource.
    // ownerId is valid only for sourceIUnknown; source retirement releases every local owner.
    __declspec(dllexport) HRESULT __cdecl EmbyCreateLocalAssSubtitle(void* sourceIUnknown,
        const uint8_t* utf8, uint32_t bytes, const wchar_t* name,
        int64_t timelineOffsetTicks, int64_t delayTicks,
        uint64_t* ownerId, void** trackIUnknown) EMBY_SUBTITLE_NOEXCEPT;

    __declspec(dllexport) HRESULT __cdecl EmbySetLocalAssSubtitleDelay(void* sourceIUnknown,
        uint64_t ownerId, int64_t delayTicks) EMBY_SUBTITLE_NOEXCEPT;
    __declspec(dllexport) HRESULT __cdecl EmbyRemoveLocalAssSubtitle(void* sourceIUnknown,
        uint64_t ownerId) EMBY_SUBTITLE_NOEXCEPT;

    // Refreshes an imported or delayed track while paused, using the actual engine clock.
    __declspec(dllexport) HRESULT __cdecl EmbyRefreshAssSubtitles(void* sourceIUnknown,
        int64_t mediaPositionTicks, int64_t frameDurationTicks) EMBY_SUBTITLE_NOEXCEPT;

    // Asynchronous image-render failures remain observable and cannot masquerade as missing subtitles.
    __declspec(dllexport) HRESULT __cdecl EmbyGetAssSubtitleFailure(void* sourceIUnknown,
        HRESULT* subtitleFailure) EMBY_SUBTITLE_NOEXCEPT;

    // Reading never selects a track, generates a bitmap, or changes the media clock.
    __declspec(dllexport) HRESULT __cdecl EmbyGetAssSubtitleObservation(void* sourceIUnknown,
        AssSubtitleObservation* observation, uint32_t callerSize) EMBY_SUBTITLE_NOEXCEPT;

    __declspec(dllexport) uint32_t __cdecl EmbyAssSubtitleFrameApiVersion(void) EMBY_SUBTITLE_NOEXCEPT;

    // Independent capability: frame API v1 alone does not promise paused bitmap re-selection.
    __declspec(dllexport) uint32_t __cdecl EmbyBitmapSubtitleSelectionApiVersion(void) EMBY_SUBTITLE_NOEXCEPT;

    // Reads never select tracks or advance the clock. HasImage=0 is an explicit clear state.
    __declspec(dllexport) HRESULT __cdecl EmbyGetAssSubtitleFrame(void* sourceIUnknown,
        AssSubtitleFrame* frame, uint32_t callerSize) EMBY_SUBTITLE_NOEXCEPT;

    // S_FALSE returns current metadata with a null bitmap if expectedSequence has changed.
    // S_OK with HasImage=1 returns one ABI reference to an independent, read-only SoftwareBitmap
    // copy. The caller owns that snapshot and may close it without affecting the renderer cache.
    __declspec(dllexport) HRESULT __cdecl EmbyAcquireAssSubtitleFrame(void* sourceIUnknown,
        uint64_t expectedSequence, AssSubtitleFrame* frame, uint32_t callerSize,
        void** bitmapIUnknown) EMBY_SUBTITLE_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#undef EMBY_SUBTITLE_NOEXCEPT
