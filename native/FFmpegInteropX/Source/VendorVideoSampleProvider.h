#pragma once
#include "pch.h"
#include "EmbyDecoderApi.h"
#include "UncompressedVideoSampleProvider.h"
#include "VendorDecoderDevice.h"
#include "VideoFilterFactory.h"
#include <algorithm>
#include <deque>
#include <iterator>
#include <limits>
#include <thread>

inline const AVCodec* FindEmbySoftwareVideoDecoder(AVCodecID codecId)
{
    auto codec = codecId == AV_CODEC_ID_AV1 ? avcodec_find_decoder_by_name("libdav1d") : nullptr;
    if (!codec) codec = avcodec_find_decoder(codecId);
    if (codec && !(codec->capabilities & AV_CODEC_CAP_HARDWARE)) return codec;

    void* iterator = nullptr;
    while ((codec = av_codec_iterate(&iterator)))
    {
        if (av_codec_is_decoder(codec) && codec->id == codecId
            && !(codec->capabilities & AV_CODEC_CAP_HARDWARE)) return codec;
    }
    return nullptr;
}

inline AVPixelFormat EmbySoftwarePixelFormat(AVCodecContext*, const AVPixelFormat* formats)
{
    AVPixelFormat selected = AV_PIX_FMT_NONE;
    for (auto format = formats; *format != AV_PIX_FMT_NONE; ++format)
    {
        const auto descriptor = av_pix_fmt_desc_get(*format);
        if (!descriptor || (descriptor->flags & AV_PIX_FMT_FLAG_HWACCEL)) continue;
        if (selected == AV_PIX_FMT_NONE || (*format == AV_PIX_FMT_NV12 && selected != AV_PIX_FMT_YUVA420P))
            selected = *format;
    }
    return selected;
}

inline AVPixelFormat EmbyVendorPixelFormat(AVCodecContext* context, const AVPixelFormat* formats)
{
    if (!context->hw_device_ctx) return AV_PIX_FMT_NONE;
    const auto type = reinterpret_cast<AVHWDeviceContext*>(context->hw_device_ctx->data)->type;
    const auto requested = type == AV_HWDEVICE_TYPE_QSV ? AV_PIX_FMT_QSV
        : type == AV_HWDEVICE_TYPE_AMF ? AV_PIX_FMT_AMF_SURFACE
        : type == AV_HWDEVICE_TYPE_CUDA ? AV_PIX_FMT_CUDA : AV_PIX_FMT_NONE;
    for (auto format = formats; *format != AV_PIX_FMT_NONE; ++format)
        if (*format == requested) return requested;
    // A vendor decoder must negotiate its own hardware format or fail explicitly.
    return AV_PIX_FMT_NONE;
}

inline int OpenEmbySoftwareVideoContext(AVStream* stream, MediaSourceConfig const& config,
    AVCodecContext** result)
{
    *result = nullptr;
    const auto codec = FindEmbySoftwareVideoDecoder(stream->codecpar->codec_id);
    if (!codec) return AVERROR_DECODER_NOT_FOUND;
    auto context = avcodec_alloc_context3(codec);
    if (!context) return AVERROR(ENOMEM);
    auto error = avcodec_parameters_to_context(context, stream->codecpar);
    if (error >= 0)
    {
        context->pkt_timebase = stream->time_base;
        context->get_format = EmbySoftwarePixelFormat;
        const auto maximum = config.Video().MaxDecoderThreads();
        const auto available = static_cast<int>(std::thread::hardware_concurrency());
        context->thread_count = maximum == 0 ? available
            : static_cast<int>((std::min)(static_cast<uint32_t>(available), maximum));
        context->thread_type = config.as<winrt::FFmpegInteropX::implementation::MediaSourceConfig>()->IsFrameGrabber
            ? FF_THREAD_SLICE : FF_THREAD_FRAME | FF_THREAD_SLICE;
        error = avcodec_open2(context, codec, nullptr);
    }
    if (error < 0) avcodec_free_context(&context);
    else *result = context;
    return error;
}

class VendorVideoSampleProvider final : public UncompressedVideoSampleProvider
{
public:
    VendorVideoSampleProvider(std::shared_ptr<FFmpegReader> reader, AVFormatContext* format,
        AVCodecContext* context, MediaSourceConfig const& config, int streamIndex, bool applyHdr,
        EmbyDecoderApi actualApi, EmbyDecoderFallbackReason initialFallback,
        std::shared_ptr<winrt::FFmpegInteropX::implementation::EmbyDecoderState> observation,
        const VendorDecoderDevice& decodeDevice)
        : UncompressedVideoSampleProvider(reader, format, context, config, streamIndex,
            actualApi == EmbyDecoderApi::Software ? HardwareDecoderStatus::NotAvailable
                : HardwareDecoderStatus::Available, applyHdr),
          currentApi(actualApi), fallbackReason(initialFallback), state(std::move(observation)), vendorId(decodeDevice.VendorId),
          adapterLuid(decodeDevice.AdapterLuid)
    {
        std::copy(std::begin(decodeDevice.GpuName), std::end(decodeDevice.GpuName), std::begin(gpuName));
    }

    ~VendorVideoSampleProvider() override
    {
        ClearPackets(cache);
        ClearPackets(replay);
        av_packet_free(&pendingPacket);
    }

    IMediaStreamDescriptor CreateStreamDescriptor() override
    {
        auto descriptor = UncompressedVideoSampleProvider::CreateStreamDescriptor();
        InstallFrameObserver();
        return descriptor;
    }

    void Flush(bool flushBuffers) override
    {
        UncompressedVideoSampleProvider::Flush(flushBuffers);
        ClearPackets(cache);
        ClearPackets(replay);
        av_packet_free(&pendingPacket);
        cacheBytes = 0;
        hasCachedKeyframe = false;
        cacheStartPts = AV_NOPTS_VALUE;
        drainSent = false;
        hasNextPts = false;
        waitForKeyframe = false;
        lastDeliveredPts = AV_NOPTS_VALUE;
        dropUntilPts = AV_NOPTS_VALUE;
        preparedApi = -1;
        fatalDecodeFailure = false;
    }

    HRESULT CreateNextSampleBuffer(IBuffer* buffer, int64_t& pts, int64_t& duration,
        IDirect3DSurface* surface) override
    {
        struct FrameOwner
        {
            AVFrame* Frame = av_frame_alloc();
            ~FrameOwner() { av_frame_free(&Frame); }
        } frame;
        if (!frame.Frame) return E_OUTOFMEMORY;
        unsigned errors = 0;
        preparedApi = -1;

        for (;;)
        {
            auto error = frameProvider->GetFrame(&frame.Frame);
            if (error == AVERROR(EAGAIN))
            {
                error = FeedNextPacket();
                if (error >= 0) continue;
            }
            else if (error == AVERROR_EOF)
            {
                if (drainSent || currentApi == EmbyDecoderApi::Software) return S_FALSE;
                // Unexpected hardware EOF is a decoder failure, not media EOF.
                error = AVERROR_EXTERNAL;
            }
            else if (error >= 0)
            {
                pts = frame.Frame->pts != AV_NOPTS_VALUE ? frame.Frame->pts : nextPts;
                if (frame.Frame->best_effort_timestamp != AV_NOPTS_VALUE)
                    pts = frame.Frame->best_effort_timestamp;
                duration = frame.Frame->duration;
                if (duration <= 0)
                {
                    const auto rate = av_guess_frame_rate(m_pAvFormatCtx, m_pAvStream, frame.Frame);
                    if (rate.num > 0 && rate.den > 0)
                        duration = av_rescale_q(1, av_inv_q(rate), m_pAvStream->time_base);
                }
                nextPts = pts + duration;
                hasNextPts = true;

                if (dropUntilPts != AV_NOPTS_VALUE && pts <= dropUntilPts)
                {
                    // Replay reconstructs decoder references without emitting duplicate samples.
                    av_frame_unref(frame.Frame);
                    continue;
                }
                dropUntilPts = AV_NOPTS_VALUE;

                if (currentApi != EmbyDecoderApi::Software && !observedHardwareFrame)
                    error = AVERROR_EXTERNAL;
                else
                {
                    if (UncompressedFrameProvider::IsHardwareFrame(frame.Frame))
                        error = frameProvider->ConvertHwToSwFrame(&frame.Frame);
                    if (error >= 0)
                        error = UncompressedVideoSampleProvider::CreateBufferFromFrame(
                            buffer, surface, frame.Frame, pts, duration);
                    if (error == S_OK)
                    {
                        preparedApi = static_cast<int32_t>(currentApi);
                        preparedPts = pts;
                        return S_OK;
                    }
                    if (error == S_FALSE)
                    {
                        av_frame_unref(frame.Frame);
                        continue;
                    }
                }
            }

            av_frame_unref(frame.Frame);
            if (currentApi != EmbyDecoderApi::Software && SwitchToSoftware() >= 0)
            {
                errors = 0;
                continue;
            }
            if (errors++ < m_config.General().SkipErrors())
            {
                m_isDiscontinuous = true;
                continue;
            }
            fatalDecodeFailure = true;
            return E_FAIL;
        }
    }

    // Called only after the sample has been accepted by MediaStreamSource.
    void NotifySampleDelivered()
    {
        if (preparedApi < 0) return;
        lastDeliveredPts = preparedPts;
        std::lock_guard lock(state->Mutex);
        auto& observation = state->Observation;
        observation.ActualApi = preparedApi;
        observation.FallbackReason = static_cast<int32_t>(fallbackReason);
        ++observation.DecodedFrameCount;
        if (preparedApi != static_cast<int32_t>(EmbyDecoderApi::Software))
        {
            ++observation.HardwareFrameCount;
            observation.VendorId = vendorId;
            observation.AdapterLuid = adapterLuid;
            std::copy(std::begin(gpuName), std::end(gpuName), std::begin(observation.GpuName));
        }
        else
        {
            observation.VendorId = 0;
            observation.AdapterLuid = 0;
            std::fill(std::begin(observation.GpuName), std::end(observation.GpuName), L'\0');
        }
        const auto codecName = winrt::to_hstring(m_pAvCodecCtx->codec->name);
        const auto count = (std::min)(codecName.size(), static_cast<uint32_t>(std::size(observation.CodecName) - 1));
        std::fill(std::begin(observation.CodecName), std::end(observation.CodecName), L'\0');
        std::copy_n(codecName.c_str(), count, observation.CodecName);
        preparedApi = -1;
    }

    bool HasFatalDecodeFailure() const { return fatalDecodeFailure; }

private:
    static void ClearPackets(std::deque<AVPacket*>& packets)
    {
        for (auto packet : packets) av_packet_free(&packet);
        packets.clear();
    }

    void InstallFrameObserver()
    {
        observedHardwareFrame = false;
        frameProvider->SetFrameObserver([this](const AVFrame* frame)
        {
            if (currentApi == EmbyDecoderApi::Software) return;
            const auto expectedFormat = currentApi == EmbyDecoderApi::IntelQsv ? AV_PIX_FMT_QSV
                : currentApi == EmbyDecoderApi::AmdAmf ? AV_PIX_FMT_AMF_SURFACE : AV_PIX_FMT_CUDA;
            const auto expectedDevice = currentApi == EmbyDecoderApi::IntelQsv ? AV_HWDEVICE_TYPE_QSV
                : currentApi == EmbyDecoderApi::AmdAmf ? AV_HWDEVICE_TYPE_AMF : AV_HWDEVICE_TYPE_CUDA;
            observedHardwareFrame = frame->format == expectedFormat && frame->hw_frames_ctx
                && reinterpret_cast<AVHWFramesContext*>(frame->hw_frames_ctx->data)->device_ctx->type == expectedDevice;
        });
    }

    void CachePacket(const AVPacket* packet)
    {
        if (currentApi == EmbyDecoderApi::Software) return;
        if (packet->flags & AV_PKT_FLAG_KEY)
        {
            ClearPackets(cache);
            cacheBytes = 0;
            hasCachedKeyframe = true;
            cacheStartPts = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
        }
        if (!hasCachedKeyframe) return;
        constexpr uint64_t MaximumCacheBytes = 64ULL * 1024 * 1024;
        constexpr size_t MaximumCachePackets = 4096;
        const auto timestamp = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
        const bool exceedsDuration = timestamp != AV_NOPTS_VALUE && cacheStartPts != AV_NOPTS_VALUE
            && ConvertDuration(timestamp - cacheStartPts).count() > 600000000;
        if (cacheBytes + static_cast<uint64_t>((std::max)(packet->size, 0)) > MaximumCacheBytes
            || cache.size() >= MaximumCachePackets || exceedsDuration)
        {
            ClearPackets(cache);
            cacheBytes = 0;
            hasCachedKeyframe = false;
            return;
        }
        auto retained = av_packet_clone(packet);
        if (!retained)
        {
            ClearPackets(cache);
            cacheBytes = 0;
            hasCachedKeyframe = false;
            return;
        }
        cache.push_back(retained);
        cacheBytes += static_cast<uint64_t>((std::max)(packet->size, 0));
    }

    int FeedNextPacket()
    {
        for (;;)
        {
            if (!pendingPacket)
            {
                if (!replay.empty())
                {
                    pendingPacket = replay.front();
                    replay.pop_front();
                    if (!hasNextPts)
                    {
                        const auto timestamp = pendingPacket->pts != AV_NOPTS_VALUE
                            ? pendingPacket->pts : pendingPacket->dts;
                        if (timestamp != AV_NOPTS_VALUE) nextPts = timestamp;
                        hasNextPts = true;
                    }
                }
                else
                {
                    LONGLONG pts = 0;
                    LONGLONG duration = 0;
                    const auto result = GetNextPacket(&pendingPacket, pts, duration);
                    if (result == S_FALSE)
                    {
                        if (waitForKeyframe) return AVERROR_INVALIDDATA;
                        if (drainSent) return AVERROR_EOF;
                        const auto drained = avcodec_send_packet(m_pAvCodecCtx, nullptr);
                        if (drained == AVERROR(EAGAIN)) return 0;
                        if (drained >= 0 || drained == AVERROR_EOF) drainSent = true;
                        return drained == AVERROR_EOF ? 0 : drained;
                    }
                    if (FAILED(result)) return AVERROR_EXTERNAL;
                    if (!hasNextPts)
                    {
                        nextPts = pts;
                        hasNextPts = true;
                    }
                    CachePacket(pendingPacket);
                }
            }
            if (waitForKeyframe)
            {
                if (!(pendingPacket->flags & AV_PKT_FLAG_KEY))
                {
                    av_packet_free(&pendingPacket);
                    continue;
                }
                waitForKeyframe = false;
            }
            const auto sent = avcodec_send_packet(m_pAvCodecCtx, pendingPacket);
            if (sent == AVERROR(EAGAIN)) return 0;
            av_packet_free(&pendingPacket);
            return sent;
        }
    }

    int SwitchToSoftware()
    {
        fallbackReason = EmbyDecoderFallbackReason::DecodeFailed;
        {
            std::lock_guard lock(state->Mutex);
            state->Observation.FallbackReason = static_cast<int32_t>(fallbackReason);
        }
        AVCodecContext* softwareContext = nullptr;
        const auto error = OpenEmbySoftwareVideoContext(m_pAvStream, m_config, &softwareContext);
        if (error < 0) return error;
        std::shared_ptr<UncompressedFrameProvider> replacement;
        try
        {
            // Finish all allocating work before replacing the live codec and filter factory.
            const auto filters = frameProvider->GetCurrentFilters();
            replacement = std::make_shared<UncompressedFrameProvider>(m_pAvFormatCtx, softwareContext,
                std::make_shared<VideoFilterFactory>(softwareContext, m_pAvStream));
            if (!filters.empty()) replacement->UpdateFilter(filters);
        }
        catch (...)
        {
            avcodec_free_context(&softwareContext);
            return AVERROR(ENOMEM);
        }
        auto previousContext = m_pAvCodecCtx;
        m_pAvCodecCtx = softwareContext;
        frameProvider = std::move(replacement);
        avcodec_free_context(&previousContext);
        currentApi = EmbyDecoderApi::Software;
        decoder = DecoderEngine::FFmpegSoftwareDecoder;
        VideoInfo().as<winrt::FFmpegInteropX::implementation::VideoStreamInfo>()->decoderEngine = decoder;
        av_packet_free(&pendingPacket);
        ClearPackets(replay);
        replay.swap(cache);
        waitForKeyframe = !hasCachedKeyframe || replay.empty();
        cacheBytes = 0;
        hasCachedKeyframe = false;
        drainSent = false;
        hasNextPts = false;
        dropUntilPts = lastDeliveredPts;
        observedHardwareFrame = false;
        m_isDiscontinuous = true;
        return 0;
    }

    EmbyDecoderApi currentApi;
    EmbyDecoderFallbackReason fallbackReason;
    std::shared_ptr<winrt::FFmpegInteropX::implementation::EmbyDecoderState> state;
    uint32_t vendorId = 0;
    int64_t adapterLuid = 0;
    wchar_t gpuName[128]{};
    std::deque<AVPacket*> cache;
    std::deque<AVPacket*> replay;
    AVPacket* pendingPacket = nullptr;
    uint64_t cacheBytes = 0;
    int64_t cacheStartPts = AV_NOPTS_VALUE;
    bool hasCachedKeyframe = false;
    bool waitForKeyframe = false;
    bool drainSent = false;
    bool hasNextPts = false;
    bool observedHardwareFrame = false;
    int64_t nextPts = 0;
    int64_t lastDeliveredPts = AV_NOPTS_VALUE;
    int64_t dropUntilPts = AV_NOPTS_VALUE;
    int64_t preparedPts = AV_NOPTS_VALUE;
    int32_t preparedApi = -1;
    bool fatalDecodeFailure = false;
};

inline const char* EmbyVendorDecoderName(AVCodecID codecId, EmbyDecoderApi api)
{
    switch (codecId)
    {
    case AV_CODEC_ID_H264: return api == EmbyDecoderApi::IntelQsv ? "h264_qsv" : api == EmbyDecoderApi::AmdAmf ? "h264_amf" : "h264_cuvid";
    case AV_CODEC_ID_HEVC: return api == EmbyDecoderApi::IntelQsv ? "hevc_qsv" : api == EmbyDecoderApi::AmdAmf ? "hevc_amf" : "hevc_cuvid";
    case AV_CODEC_ID_AV1: return api == EmbyDecoderApi::IntelQsv ? "av1_qsv" : api == EmbyDecoderApi::AmdAmf ? "av1_amf" : "av1_cuvid";
    case AV_CODEC_ID_VP9: return api == EmbyDecoderApi::IntelQsv ? "vp9_qsv" : api == EmbyDecoderApi::AmdAmf ? "vp9_amf" : "vp9_cuvid";
    case AV_CODEC_ID_VP8: return api == EmbyDecoderApi::IntelQsv ? "vp8_qsv" : api == EmbyDecoderApi::NvidiaNvdec ? "vp8_cuvid" : nullptr;
    case AV_CODEC_ID_MPEG2VIDEO: return api == EmbyDecoderApi::IntelQsv ? "mpeg2_qsv" : api == EmbyDecoderApi::NvidiaNvdec ? "mpeg2_cuvid" : nullptr;
    case AV_CODEC_ID_VC1: return api == EmbyDecoderApi::IntelQsv ? "vc1_qsv" : api == EmbyDecoderApi::NvidiaNvdec ? "vc1_cuvid" : nullptr;
    case AV_CODEC_ID_MJPEG: return api == EmbyDecoderApi::IntelQsv ? "mjpeg_qsv" : api == EmbyDecoderApi::NvidiaNvdec ? "mjpeg_cuvid" : nullptr;
    case AV_CODEC_ID_MPEG1VIDEO: return api == EmbyDecoderApi::NvidiaNvdec ? "mpeg1_cuvid" : nullptr;
    case AV_CODEC_ID_MPEG4: return api == EmbyDecoderApi::NvidiaNvdec ? "mpeg4_cuvid" : nullptr;
    case AV_CODEC_ID_VVC: return api == EmbyDecoderApi::IntelQsv ? "vvc_qsv" : nullptr;
    default: return nullptr;
    }
}

inline std::shared_ptr<MediaSampleProvider> CreateEmbyVendorVideoProvider(
    std::shared_ptr<FFmpegReader> reader, AVFormatContext* format, AVStream* stream,
    MediaSourceConfig const& config, int index, bool useHdr,
    std::shared_ptr<winrt::FFmpegInteropX::implementation::EmbyDecoderState> state)
{
    const auto requested = static_cast<EmbyDecoderApi>(state->Observation.RequestedApi);
    auto actual = requested;
    auto fallback = EmbyDecoderFallbackReason::None;
    VendorDecoderDevice device;
    AVCodecContext* context = nullptr;
    const auto name = EmbyVendorDecoderName(stream->codecpar->codec_id, requested);
    const auto codec = name ? avcodec_find_decoder_by_name(name) : nullptr;
    if (!name) fallback = EmbyDecoderFallbackReason::CodecUnsupported;
    else if (!codec) fallback = EmbyDecoderFallbackReason::ApiUnavailable;
    else if (CreateVendorDecoderDevice(requested, device) < 0)
        fallback = EmbyDecoderFallbackReason::DeviceUnavailable;
    else
    {
        context = avcodec_alloc_context3(codec);
        auto error = context ? avcodec_parameters_to_context(context, stream->codecpar) : AVERROR(ENOMEM);
        if (error >= 0)
        {
            context->pkt_timebase = stream->time_base;
            context->get_format = EmbyVendorPixelFormat;
            context->hw_device_ctx = av_buffer_ref(device.Context);
            error = context->hw_device_ctx ? avcodec_open2(context, codec, nullptr) : AVERROR(ENOMEM);
        }
        if (error < 0)
        {
            avcodec_free_context(&context);
            fallback = EmbyDecoderFallbackReason::InitializationFailed;
        }
    }
    if (!context)
    {
        actual = EmbyDecoderApi::Software;
        if (OpenEmbySoftwareVideoContext(stream, config, &context) < 0) return nullptr;
    }
    auto provider = std::make_shared<VendorVideoSampleProvider>(reader, format, context,
        config, index, useHdr, actual, fallback, state, device);
    if (FAILED(provider->Initialize())) return nullptr;
    return provider;
}
