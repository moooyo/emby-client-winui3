// Modified by EmbyClient contributors on 2026-10-01 for selectable decoder APIs and reproducible native builds.
#pragma once
#include "IAvFilter.h"
#include "AvFilterFactoryBase.h"
#include <mutex>
#include <functional>

extern "C"
{
#include <libavformat/avformat.h>
#include <libavutil/hwcontext.h>
#include <libavutil/pixdesc.h>
}


using namespace winrt::Windows::Foundation::Collections;

class UncompressedFrameProvider sealed
{
    std::shared_ptr<IAvFilter> filter;
    AVFormatContext* m_pAvFormatCtx = NULL;
    AVCodecContext* m_pAvCodecCtx = NULL;
    std::shared_ptr<AvFilterFactoryBase> m_effectFactory;
    winrt::hstring pendingFFmpegFilters{};
    winrt::hstring currentFFmpegFilters{};
    std::function<void(const AVFrame*)> frameObserver;


public:
    void SetFrameObserver(std::function<void(const AVFrame*)> observer)
    {
        frameObserver = std::move(observer);
    }

    static bool IsHardwareFrame(const AVFrame* frame)
    {
        const auto descriptor = frame ? av_pix_fmt_desc_get(static_cast<AVPixelFormat>(frame->format)) : nullptr;
        return descriptor && (descriptor->flags & AV_PIX_FMT_FLAG_HWACCEL);
    }

    UncompressedFrameProvider(AVFormatContext* p_pAvFormatCtx, AVCodecContext* p_pAvCodecCtx, std::shared_ptr<AvFilterFactoryBase> p_effectFactory)
    {
        m_pAvCodecCtx = p_pAvCodecCtx;
        m_pAvFormatCtx = p_pAvFormatCtx;
        m_effectFactory = p_effectFactory;
    }

    void UpdateCodecContext(AVCodecContext* avCodecCtx)
    {
        m_pAvCodecCtx = avCodecCtx;
    }

    void UpdateFilter(winrt::hstring ffmpegFilters)
    {
        if (!ffmpegFilters.empty())
        {
            currentFFmpegFilters = pendingFFmpegFilters = ffmpegFilters;
        }
        else
        {
            DisableFilter();
        }
    }

    void DisableFilter()
    {
        currentFFmpegFilters = winrt::hstring{};
        pendingFFmpegFilters.clear();
        filter = nullptr;
    }

    FilterCommandResult SendFilterCommand(winrt::hstring target, winrt::hstring command, winrt::hstring arguments)
    {
        if (filter)
        {
            return filter->SendCommand(target, command, arguments);
        }
        else
        {
            // no filter assigned
            return FilterCommandResult(false, L"No filter assigned");
        }
    }

    winrt::hstring GetCurrentFilters()
    {
        return currentFFmpegFilters;
    }

    HRESULT GetFrame(AVFrame** avFrame)
    {
        HRESULT hr = S_OK;

        if (!pendingFFmpegFilters.empty())
        {
            if (pendingFFmpegFilters.size() > 0)
            {
                filter = m_effectFactory->CreateEffect(pendingFFmpegFilters);
            }
            else
            {
                filter = nullptr;
            }

            pendingFFmpegFilters.clear();
        }

        if (filter)
        {
            hr = GetFrameFromFilter(avFrame);
        }
        else
        {
            hr = GetFrameFromCodec(avFrame);
        }

        return hr;
    }

    HRESULT GetFrameFromFilter(AVFrame** avFrame)
    {
        HRESULT hr = S_OK;

        // use polling loop and push frames only if needed
        while (SUCCEEDED(hr))
        {
            hr = filter->GetFrame(*avFrame);
            if (hr == AVERROR(EAGAIN))
            {
                // filter requires next source frame.
                // get from codec and feed to filter graph.

                hr = GetFrameFromCodec(avFrame);
                if (SUCCEEDED(hr) && IsHardwareFrame(*avFrame))
                {
                    // this is a hardware frame, replace it with a software copy
                    hr = ConvertHwToSwFrame(avFrame);
                }

                if (SUCCEEDED(hr))
                {
                    hr = filter->AddFrame(*avFrame);
                    if (FAILED(hr))
                    {
                        // add frame failed. clear filter to prevent crashes.
                        filter = nullptr;
                    }
                }
            }
            else
            {
                // filter has either success or failure
                break;
            }
        }

        return hr;
    }

    HRESULT GetFrameFromCodec(AVFrame** avFrame)
    {
        HRESULT hr = avcodec_receive_frame(m_pAvCodecCtx, *avFrame);
        if (hr >= 0 && frameObserver)
        {
            frameObserver(*avFrame);
        }
        return hr;
    }

    HRESULT ConvertHwToSwFrame(AVFrame** avFrame)
    {
        HRESULT hr = S_OK;
        AVFrame* swFrame = av_frame_alloc();
        if (!swFrame)
        {
            hr = E_OUTOFMEMORY;
        }
        if (SUCCEEDED(hr))
        {
            if (!(*avFrame)->hw_frames_ctx)
            {
                hr = E_INVALIDARG;
            }
            else
            {
                // AMF requires the exact frame-pool software format, including P010.
                const auto frames = reinterpret_cast<AVHWFramesContext*>((*avFrame)->hw_frames_ctx->data);
                AVPixelFormat* formats = nullptr;
                hr = av_hwframe_transfer_get_formats((*avFrame)->hw_frames_ctx,
                    AV_HWFRAME_TRANSFER_DIRECTION_FROM, &formats, 0);
                if (SUCCEEDED(hr))
                {
                    bool supported = false;
                    for (auto format = formats; format && *format != AV_PIX_FMT_NONE; ++format)
                    {
                        supported |= *format == frames->sw_format;
                    }
                    if (supported)
                    {
                        swFrame->format = frames->sw_format;
                        hr = av_hwframe_transfer_data(swFrame, *avFrame, 0);
                    }
                    else
                    {
                        hr = E_INVALIDARG;
                    }
                }
                av_free(formats);
            }
        }
        if (SUCCEEDED(hr))
        {
            hr = av_frame_copy_props(swFrame, *avFrame);
        }
        if (SUCCEEDED(hr))
        {
            av_frame_free(avFrame);
            *avFrame = swFrame;
        }
        else
        {
            av_frame_free(&swFrame);
        }

        return hr;
    }
};

