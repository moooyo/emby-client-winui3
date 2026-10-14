#include "pch.h"
#include "WasapiBitstreamOutput.h"

#include <audioclient.h>
#include <avrt.h>
#include <mmdeviceapi.h>
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <propidl.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <iterator>
#include <limits>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

extern "C"
{
#include <libavcodec/avcodec.h>
}

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "avrt.lib")

namespace
{
    constexpr int64_t TicksPerSecond = 10000000;
    constexpr int64_t DriftRebaseThreshold = 500000;
    constexpr int64_t DiscontinuityLimit = 2500000;
    constexpr size_t MaximumQueuedBytes = 64 * 1024 * 1024;
    constexpr size_t MaximumQueuedPackets = 131072;
    constexpr int64_t MaximumQueuedDuration = 90 * TicksPerSecond;
    constexpr size_t SoftQueuedBytes = 32 * 1024 * 1024;
    constexpr int64_t SoftQueuedDuration = 20 * TicksPerSecond;
    constexpr size_t MaximumMuxBytes = 2 * 1024 * 1024;
    constexpr size_t MaximumBufferedBursts = 32;
    std::atomic<uint64_t> NextOutputOwner{ 1 };

    constexpr GUID Ac3Subtype{ 0x00000092, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
    constexpr GUID DtsSubtype{ 0x00000008, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
    constexpr GUID Eac3Subtype{ 0x0000000a, 0x0cea, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
    constexpr GUID DtsHdSubtype{ 0x0000000b, 0x0cea, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
    constexpr GUID TrueHdSubtype{ 0x0000000c, 0x0cea, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
    constexpr PROPERTYKEY FriendlyNameKey{ { 0xa45c254e, 0xdf1c, 0x4efd, { 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0 } }, 14 };

    template<size_t Length>
    void CopyText(wchar_t (&destination)[Length], std::wstring_view value) noexcept
    {
        auto count = (std::min)(Length - 1, value.size());
        if (count) memcpy(destination, value.data(), count * sizeof(wchar_t));
        destination[count] = L'\0';
    }

    struct PacketDeleter
    {
        void operator()(AVPacket* packet) const noexcept { av_packet_free(&packet); }
    };

    using PacketPtr = std::unique_ptr<AVPacket, PacketDeleter>;

    struct PendingPacket
    {
        PacketPtr Packet;
        int64_t PresentationTicks = 0;
        int64_t DurationTicks = 0;
        uint64_t Epoch = 0;
    };

    struct EncodedBurst
    {
        std::vector<uint8_t> Bytes;
        int64_t PresentationTicks = 0;
        int64_t DurationTicks = 0;
        size_t SubmittedBytes = 0;
    };

    struct EncodedFormat
    {
        WAVEFORMATEXTENSIBLE_IEC61937 Wave{};
        EmbyAudioCodec Codec = EmbyAudioCodec::Unknown;
        int DtsHdRate = 0;
        const wchar_t* Name = L"";
    };

    EmbyAudioFallbackReason DescribeEndpointFailure(HRESULT hr) noexcept
    {
        if (hr == AUDCLNT_E_DEVICE_INVALIDATED || hr == AUDCLNT_E_RESOURCES_INVALIDATED)
            return EmbyAudioFallbackReason::DeviceInvalidated;
        if (hr == AUDCLNT_E_UNSUPPORTED_FORMAT) return EmbyAudioFallbackReason::FormatUnsupported;
        if (hr == AUDCLNT_E_DEVICE_IN_USE || hr == AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED)
            return EmbyAudioFallbackReason::ExclusiveUnavailable;
        return EmbyAudioFallbackReason::EndpointUnavailable;
    }

    bool IsDtsCoreProfile(int profile) noexcept
    {
        return profile == AV_PROFILE_DTS || profile == AV_PROFILE_DTS_ES || profile == AV_PROFILE_DTS_96_24;
    }

    bool IsDtsHdProfile(int profile) noexcept
    {
        return profile == AV_PROFILE_DTS_HD_HRA || profile == AV_PROFILE_DTS_HD_MA
            || profile == AV_PROFILE_DTS_HD_MA_X || profile == AV_PROFILE_DTS_HD_MA_X_IMAX;
    }

    bool PrepareEncodedFormat(const AVCodecParameters* parameters, EmbyAudioPassthroughMode mode,
        EncodedFormat& result, EmbyAudioFallbackReason& reason) noexcept
    {
        reason = EmbyAudioFallbackReason::CodecUnsupported;
        if (mode == EmbyAudioPassthroughMode::Off)
        {
            reason = EmbyAudioFallbackReason::PolicyOff;
            return false;
        }
        if (!parameters || parameters->sample_rate <= 0 || parameters->ch_layout.nb_channels <= 0
            || parameters->ch_layout.nb_channels > 8 || parameters->ch_layout.order != AV_CHANNEL_ORDER_NATIVE
            || parameters->ch_layout.u.mask == 0 || parameters->ch_layout.u.mask > UINT32_MAX)
        {
            reason = EmbyAudioFallbackReason::MetadataUnavailable;
            return false;
        }

        auto rate = parameters->sample_rate;
        DWORD carrierRate = 0;
        WORD carrierChannels = 2;
        GUID subtype{};
        switch (parameters->codec_id)
        {
        case AV_CODEC_ID_AC3:
            if (rate != 32000 && rate != 44100 && rate != 48000) return false;
            result.Codec = EmbyAudioCodec::Ac3;
            result.Name = L"spdif-ac3";
            carrierRate = rate;
            subtype = Ac3Subtype;
            break;
        case AV_CODEC_ID_EAC3:
            if (rate != 44100 && rate != 48000) return false;
            result.Codec = EmbyAudioCodec::Eac3;
            result.Name = L"spdif-eac3";
            carrierRate = rate * 4;
            subtype = Eac3Subtype;
            break;
        case AV_CODEC_ID_DTS:
            if (IsDtsHdProfile(parameters->profile))
            {
                if (mode != EmbyAudioPassthroughMode::All) return false;
                result.Codec = EmbyAudioCodec::DtsHd;
                result.Name = L"spdif-dtshd";
                carrierChannels = parameters->profile == AV_PROFILE_DTS_HD_HRA ? 2 : 8;
                carrierRate = 192000;
                result.DtsHdRate = carrierChannels * 96000;
                subtype = DtsHdSubtype;
            }
            else if (IsDtsCoreProfile(parameters->profile))
            {
                if (rate != 44100 && rate != 48000 && rate != 96000) return false;
                result.Codec = EmbyAudioCodec::Dts;
                result.Name = L"spdif-dts";
                carrierRate = rate == 44100 ? 44100 : 48000;
                subtype = DtsSubtype;
            }
            else
            {
                reason = parameters->profile == AV_PROFILE_UNKNOWN
                    ? EmbyAudioFallbackReason::MetadataUnavailable : EmbyAudioFallbackReason::CodecUnsupported;
                return false;
            }
            break;
        case AV_CODEC_ID_TRUEHD:
            if (mode != EmbyAudioPassthroughMode::All) return false;
            if (rate == 44100 || rate == 88200 || rate == 176400) carrierRate = 176400;
            else if (rate == 48000 || rate == 96000 || rate == 192000) carrierRate = 192000;
            else return false;
            result.Codec = EmbyAudioCodec::TrueHd;
            result.Name = L"spdif-truehd";
            carrierChannels = 8;
            subtype = TrueHdSubtype;
            break;
        default:
            return false;
        }

        auto& wave = result.Wave;
        wave.FormatExt.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
        wave.FormatExt.Format.nChannels = carrierChannels;
        wave.FormatExt.Format.nSamplesPerSec = carrierRate;
        wave.FormatExt.Format.wBitsPerSample = 16;
        wave.FormatExt.Format.nBlockAlign = carrierChannels * sizeof(uint16_t);
        wave.FormatExt.Format.nAvgBytesPerSec = carrierRate * wave.FormatExt.Format.nBlockAlign;
        wave.FormatExt.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE_IEC61937) - sizeof(WAVEFORMATEX);
        wave.FormatExt.Samples.wValidBitsPerSample = 16;
        wave.FormatExt.dwChannelMask = static_cast<DWORD>(parameters->ch_layout.u.mask);
        wave.FormatExt.SubFormat = subtype;
        wave.dwEncodedSamplesPerSec = rate;
        wave.dwEncodedChannelCount = parameters->ch_layout.nb_channels;
        wave.dwAverageBytesPerSec = parameters->bit_rate > 0 && parameters->bit_rate / 8 <= UINT32_MAX
            ? static_cast<DWORD>(parameters->bit_rate / 8) : 0;
        reason = EmbyAudioFallbackReason::None;
        return true;
    }

    bool HasTrueHdMajorSync(const AVPacket* packet) noexcept
    {
        return packet && packet->size >= 8 && packet->data[4] == 0xf8 && packet->data[5] == 0x72
            && packet->data[6] == 0x6f && packet->data[7] == 0xba;
    }

    // FFmpeg can silently send only a DTS core after an HD bandwidth overflow.
    // Reject that output instead of describing it as the original HD stream.
    bool ContainsCompleteDtsHdPacket(const std::vector<uint8_t>& bytes, int packetSize) noexcept
    {
        if (packetSize <= 0 || packetSize > UINT16_MAX || bytes.size() < 20) return false;
        auto dataType = static_cast<uint16_t>(bytes[4] | (bytes[5] << 8));
        if ((dataType & 0x1f) != 0x11) return false;
        auto first = static_cast<uint16_t>(bytes[18] | (bytes[19] << 8));
        auto second = static_cast<uint16_t>(bytes[19] | (bytes[18] << 8));
        return first == packetSize || second == packetSize;
    }
}

struct WasapiBitstreamOutput::State : std::enable_shared_from_this<State>
{
    struct DeviceNotifications final : IMMNotificationClient
    {
        explicit DeviceNotifications(std::weak_ptr<State> owner, std::wstring endpoint)
            : Owner(std::move(owner)), Endpoint(std::move(endpoint)) { }
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) noexcept override
        {
            if (!value) return E_POINTER;
            *value = nullptr;
            if (iid != __uuidof(IUnknown) && iid != __uuidof(IMMNotificationClient)) return E_NOINTERFACE;
            *value = static_cast<IMMNotificationClient*>(this);
            AddRef();
            return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() noexcept override { return ++References; }
        ULONG STDMETHODCALLTYPE Release() noexcept override
        {
            auto remaining = --References;
            if (!remaining) delete this;
            return remaining;
        }
        HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR id, DWORD status) noexcept override
        {
            if (Matches(id) && status != DEVICE_STATE_ACTIVE) Notify(EmbyAudioFallbackReason::DeviceInvalidated);
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) noexcept override { return S_OK; }
        HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR id) noexcept override
        {
            if (Matches(id)) Notify(EmbyAudioFallbackReason::DeviceInvalidated);
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR id) noexcept override
        {
            if (flow == eRender && role == eMultimedia && !Matches(id)) Notify(EmbyAudioFallbackReason::EndpointChanged);
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR id, const PROPERTYKEY key) noexcept override
        {
            // A friendly-name edit cannot change encoded-format eligibility.
            if (Matches(id) && (key.fmtid != FriendlyNameKey.fmtid || key.pid != FriendlyNameKey.pid))
                Notify(EmbyAudioFallbackReason::EndpointChanged);
            return S_OK;
        }
        bool Matches(LPCWSTR id) const noexcept { return id && Endpoint == id; }
        void Notify(EmbyAudioFallbackReason reason) noexcept
        {
            if (auto owner = Owner.lock()) owner->Fail(reason);
        }
        std::atomic<ULONG> References{ 1 };
        std::weak_ptr<State> Owner;
        std::wstring Endpoint;
    };

    State(const AVCodecParameters* sourceParameters, int32_t streamIndex, EmbyAudioPassthroughMode mode,
        std::function<void()> consumerWake) : ConsumerWake(std::move(consumerWake))
    {
        Observation.Size = sizeof(Observation);
        Observation.Version = 1;
        Observation.RequestedMode = static_cast<int32_t>(mode);
        Observation.State = static_cast<int32_t>(EmbyAudioOutputState::Pending);
        Observation.StreamIndex = streamIndex;
        Observation.Generation = NextOutputOwner.fetch_add(1, std::memory_order_relaxed);
        if (!Observation.Generation) Observation.Generation = NextOutputOwner.fetch_add(1, std::memory_order_relaxed);
        Parameters = avcodec_parameters_alloc();
        if (!Parameters) winrt::throw_hresult(E_OUTOFMEMORY);
        if (!sourceParameters || avcodec_parameters_copy(Parameters, sourceParameters) < 0)
        {
            avcodec_parameters_free(&Parameters);
            winrt::throw_hresult(E_INVALIDARG);
        }
        Observation.ContentSampleRate = Parameters->sample_rate > 0 ? Parameters->sample_rate : 0;
        Observation.ContentChannels = Parameters->ch_layout.nb_channels > 0 ? Parameters->ch_layout.nb_channels : 0;
        const auto* codec = avcodec_get_name(Parameters->codec_id);
        if (codec)
        {
            for (size_t i = 0; codec[i] && i + 1 < std::size(Observation.SourceCodecName); ++i)
                Observation.SourceCodecName[i] = static_cast<unsigned char>(codec[i]);
        }
        EmbyAudioFallbackReason reason{};
        if (!PrepareEncodedFormat(Parameters, mode, Format, reason))
        {
            SetEarlyFallback(reason);
            return;
        }
        Observation.Codec = static_cast<int32_t>(Format.Codec);
        Observation.TransportSampleRate = Format.Wave.FormatExt.Format.nSamplesPerSec;
        Observation.TransportChannels = Format.Wave.FormatExt.Format.nChannels;
        WakeEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        BufferEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!WakeEvent || !BufferEvent)
        {
            SetEarlyFallback(EmbyAudioFallbackReason::EndpointUnavailable);
        }
    }

    ~State()
    {
        DestroyMux();
        avcodec_parameters_free(&Parameters);
        if (WakeEvent) CloseHandle(WakeEvent);
        if (BufferEvent) CloseHandle(BufferEvent);
    }

    void Wake() const noexcept { if (WakeEvent) SetEvent(WakeEvent); }

    void SetEarlyFallback(EmbyAudioFallbackReason reason) noexcept
    {
        std::lock_guard<std::mutex> lock(ObservationMutex);
        Observation.State = static_cast<int32_t>(EmbyAudioOutputState::Pcm);
        Observation.Reason = static_cast<int32_t>(reason);
        Observation.ActualFormatConfirmed = 0;
        OpenResult = S_FALSE;
    }

    void Fail(EmbyAudioFallbackReason reason) noexcept
    {
        if (StopRequested.load(std::memory_order_acquire)) return;
        if (!Failed.exchange(true, std::memory_order_acq_rel))
        {
            std::lock_guard<std::mutex> lock(ObservationMutex);
            Observation.State = static_cast<int32_t>(EmbyAudioOutputState::FallbackRequired);
            Observation.Reason = static_cast<int32_t>(reason);
            Observation.ActualFormatConfirmed = 0;
        }
        Wake();
    }

    HRESULT OpenEndpoint() noexcept
    {
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
            __uuidof(IMMDeviceEnumerator), Enumerator.put_void());
        if (FAILED(hr)) return hr;
        hr = Enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, Device.put());
        if (FAILED(hr)) return hr;
        LPWSTR id = nullptr;
        hr = Device->GetId(&id);
        if (FAILED(hr)) return hr;
        try { EndpointId = id; }
        catch (...) { CoTaskMemFree(id); return E_OUTOFMEMORY; }
        CoTaskMemFree(id);

        winrt::com_ptr<IPropertyStore> properties;
        if (SUCCEEDED(Device->OpenPropertyStore(STGM_READ, properties.put())))
        {
            PROPVARIANT name{};
            if (SUCCEEDED(properties->GetValue(FriendlyNameKey, &name)) && name.vt == VT_LPWSTR && name.pwszVal)
            {
                std::lock_guard<std::mutex> lock(ObservationMutex);
                CopyText(Observation.EndpointName, name.pwszVal);
            }
            PropVariantClear(&name);
        }
        for (int attempt = 0; attempt < 2; ++attempt)
        {
            Client = nullptr;
            hr = Device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, Client.put_void());
            if (FAILED(hr)) return hr;
            // No closest-match acceptance and no substitution of another codec GUID.
            hr = Client->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, &Format.Wave.FormatExt.Format, nullptr);
            if (hr != S_OK) return hr == S_FALSE ? AUDCLNT_E_UNSUPPORTED_FORMAT : hr;
            if (!BufferDuration)
            {
                REFERENCE_TIME defaultPeriod = 0, minimumPeriod = 0;
                hr = Client->GetDevicePeriod(&defaultPeriod, &minimumPeriod);
                if (FAILED(hr)) return hr;
                BufferDuration = (std::max)(defaultPeriod, minimumPeriod);
                if (BufferDuration <= 0 || BufferDuration > DiscontinuityLimit) return AUDCLNT_E_UNSUPPORTED_FORMAT;
            }
            hr = Client->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                BufferDuration, BufferDuration, &Format.Wave.FormatExt.Format, nullptr);
            if (hr != AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED) break;
            if (attempt == 1) return hr;
            UINT32 alignedFrames = 0;
            hr = Client->GetBufferSize(&alignedFrames);
            if (FAILED(hr) || !alignedFrames) return FAILED(hr) ? hr : E_FAIL;
            BufferDuration = static_cast<REFERENCE_TIME>(
                (static_cast<long double>(alignedFrames) * TicksPerSecond)
                / Format.Wave.FormatExt.Format.nSamplesPerSec + 0.5L);
        }
        if (FAILED(hr)) return hr;
        hr = Client->SetEventHandle(BufferEvent);
        if (FAILED(hr)) return hr;
        hr = Client->GetBufferSize(&BufferFrames);
        if (FAILED(hr) || !BufferFrames) return FAILED(hr) ? hr : E_FAIL;
        hr = Client->GetStreamLatency(&EndpointLatency);
        if (FAILED(hr)) return hr;
        hr = Client->GetService(__uuidof(IAudioRenderClient), Render.put_void());
        if (FAILED(hr)) return hr;
        hr = Client->GetService(__uuidof(IAudioClock), HardwareClock.put_void());
        if (FAILED(hr)) return hr;
        hr = HardwareClock->GetFrequency(&HardwareFrequency);
        if (FAILED(hr) || !HardwareFrequency) return FAILED(hr) ? hr : E_FAIL;
        try { Notifications.attach(new DeviceNotifications(weak_from_this(), EndpointId)); }
        catch (...) { return E_OUTOFMEMORY; }
        hr = Enumerator->RegisterEndpointNotificationCallback(Notifications.get());
        if (FAILED(hr)) return hr;
        NotificationRegistered = true;
        EndpointOwned.store(true, std::memory_order_release);
        return S_OK;
    }

    static int WriteMuxBytes(void* owner, const uint8_t* bytes, int size) noexcept
    {
        auto self = static_cast<State*>(owner);
        if (size < 0 || self->MuxBytes.size() + static_cast<size_t>(size) > MaximumMuxBytes)
            return AVERROR(ENOMEM);
        try { self->MuxBytes.insert(self->MuxBytes.end(), bytes, bytes + size); }
        catch (...) { return AVERROR(ENOMEM); }
        return size;
    }

    bool CreateMux() noexcept
    {
        DestroyMux();
        if (avformat_alloc_output_context2(&Mux, nullptr, "spdif", nullptr) < 0 || !Mux) return false;
        auto buffer = static_cast<unsigned char*>(av_malloc(32768));
        if (!buffer) return false;
        MuxIo = avio_alloc_context(buffer, 32768, 1, this, nullptr, WriteMuxBytes, nullptr);
        if (!MuxIo)
        {
            av_free(buffer);
            return false;
        }
        Mux->pb = MuxIo;
        Mux->flags |= AVFMT_FLAG_CUSTOM_IO;
        auto stream = avformat_new_stream(Mux, nullptr);
        if (!stream || avcodec_parameters_copy(stream->codecpar, Parameters) < 0) return false;
        Parser = av_parser_init(Parameters->codec_id);
        ParserContext = avcodec_alloc_context3(nullptr);
        if (!Parser || !ParserContext || avcodec_parameters_to_context(ParserContext, Parameters) < 0) return false;
        Parser->flags |= PARSER_FLAG_COMPLETE_FRAMES;
        stream->codecpar->sample_rate = Format.Wave.FormatExt.Format.nSamplesPerSec;
        stream->time_base = AVRational{ 1, static_cast<int>(Format.Wave.FormatExt.Format.nSamplesPerSec) };
        AVDictionary* options = nullptr;
        if (Format.DtsHdRate)
        {
            av_dict_set_int(&options, "dtshd_rate", Format.DtsHdRate, 0);
            av_dict_set_int(&options, "dtshd_fallback_time", 0, 0);
        }
        auto result = avformat_write_header(Mux, &options);
        auto unconsumed = av_dict_count(options);
        av_dict_free(&options);
        if (result < 0 || unconsumed) return false;
        MuxBytes.clear();
        MuxTimestamp.reset();
        NextMuxTimestamp.reset();
        LastSourceEnd.reset();
        LastEncodedEnd.reset();
        DrainAttempted = false;
        Drained = false;
        ObservedHeaderSampleRate = 0;
        WaitingForTrueHdSync = Format.Codec == EmbyAudioCodec::TrueHd;
        return true;
    }

    void DestroyMux() noexcept
    {
        // Do not flush an incomplete MAT frame into a retired or rebased output.
        if (Mux) Mux->pb = nullptr;
        if (MuxIo) av_freep(&MuxIo->buffer);
        avio_context_free(&MuxIo);
        avformat_free_context(Mux);
        Mux = nullptr;
        if (Parser) av_parser_close(Parser);
        Parser = nullptr;
        avcodec_free_context(&ParserContext);
        MuxBytes.clear();
    }

    bool ReadMaster(winrt::Windows::Media::Playback::MediaPlaybackSession& session,
        int64_t& position, winrt::Windows::Media::Playback::MediaPlaybackState& playbackState, double& rate) noexcept
    {
        winrt::weak_ref<winrt::Windows::Media::Playback::MediaPlaybackSession> weak{ nullptr };
        {
            std::lock_guard<std::mutex> lock(ClockMutex);
            weak = Master;
        }
        try
        {
            session = weak.get();
            if (!session) return false;
            // These agile getters run outside every source, reader, queue and
            // observation lock. Wall time is never substituted for media time.
            position = session.Position().count();
            playbackState = session.PlaybackState();
            rate = session.PlaybackRate();
            return true;
        }
        catch (...)
        {
            return false;
        }
    }

    void MarkPending() noexcept
    {
        std::lock_guard<std::mutex> lock(ObservationMutex);
        if (!Failed.load(std::memory_order_acquire) && !StopRequested.load(std::memory_order_acquire))
        {
            Observation.State = static_cast<int32_t>(EmbyAudioOutputState::Pending);
            Observation.ActualFormatConfirmed = 0;
        }
    }

    bool StopHardware(bool rewindBursts) noexcept
    {
        if (!Client) return true;
        auto hr = Client->Stop();
        if (SUCCEEDED(hr)) hr = Client->Reset();
        HardwareStarted = false;
        HardwareOrigin.reset();
        LastHardwarePosition.reset();
        SubmittedSinceReset = 0;
        ResetEvent(BufferEvent);
        if (rewindBursts)
        {
            for (auto& burst : Bursts) burst.SubmittedBytes = 0;
        }
        MarkPending();
        if (FAILED(hr))
        {
            Fail(DescribeEndpointFailure(hr));
            return false;
        }
        return true;
    }

    bool PopPacket(PendingPacket& packet) noexcept
    {
        {
            std::lock_guard<std::mutex> lock(QueueMutex);
            if (Packets.empty()) return false;
            packet = std::move(Packets.front());
            QueuedBytes -= static_cast<size_t>(packet.Packet->size);
            Packets.pop_front();
        }
        // The reader can be asleep because this external queue was full.
        // Wake it outside the queue lock and never read its demuxer here.
        try { if (ConsumerWake) ConsumerWake(); }
        catch (...) { Fail(EmbyAudioFallbackReason::TimingDiscontinuity); }
        return true;
    }

    bool MuxPacket(PendingPacket& packet) noexcept
    {
        if (packet.Epoch != QueueEpoch.load(std::memory_order_acquire)) return true;
        if (WaitingForTrueHdSync)
        {
            if (!HasTrueHdMajorSync(packet.Packet.get())) return true;
            WaitingForTrueHdSync = false;
        }
        if (!ValidatePacketFormat(packet.Packet.get())) return false;
        auto sourceDuration = packet.DurationTicks;
        if (sourceDuration <= 0 && Parser->duration > 0 && ParserContext->sample_rate > 0)
            sourceDuration = static_cast<int64_t>((static_cast<long double>(Parser->duration) * TicksPerSecond)
                / ParserContext->sample_rate);
        if (sourceDuration > 0) LastSourceEnd = packet.PresentationTicks + sourceDuration;
        if (!MuxTimestamp)
        {
            MuxTimestamp = packet.PresentationTicks;
            if (NextMuxTimestamp && std::abs(*NextMuxTimestamp - packet.PresentationTicks) <= DriftRebaseThreshold)
                MuxTimestamp = *NextMuxTimestamp;
            else if (NextMuxTimestamp)
            {
                Fail(EmbyAudioFallbackReason::TimingDiscontinuity);
                return false;
            }
        }
        MuxBytes.clear();
        packet.Packet->stream_index = 0;
        auto result = av_write_frame(Mux, packet.Packet.get());
        avio_flush(MuxIo);
        if (result < 0 || MuxIo->error < 0)
        {
            Fail(EmbyAudioFallbackReason::MuxFailed);
            return false;
        }
        if (packet.Epoch != QueueEpoch.load(std::memory_order_acquire)) return true;
        if (MuxBytes.empty()) return true;
        auto alignment = Format.Wave.FormatExt.Format.nBlockAlign;
        if (!alignment || MuxBytes.size() % alignment
            || (Format.Codec == EmbyAudioCodec::DtsHd && !ContainsCompleteDtsHdPacket(MuxBytes, packet.Packet->size)))
        {
            Fail(EmbyAudioFallbackReason::MuxFailed);
            return false;
        }
        auto frames = MuxBytes.size() / alignment;
        auto duration = static_cast<int64_t>((static_cast<long double>(frames) * TicksPerSecond)
            / Format.Wave.FormatExt.Format.nSamplesPerSec);
        if (!duration || duration > DiscontinuityLimit || !MuxTimestamp)
        {
            Fail(EmbyAudioFallbackReason::TimingDiscontinuity);
            return false;
        }
        try
        {
            EncodedBurst burst;
            burst.Bytes = std::move(MuxBytes);
            burst.PresentationTicks = *MuxTimestamp;
            burst.DurationTicks = duration;
            NextMuxTimestamp = burst.PresentationTicks + duration;
            LastEncodedEnd = NextMuxTimestamp;
            Bursts.push_back(std::move(burst));
            MuxTimestamp.reset();
            return true;
        }
        catch (...)
        {
            Fail(EmbyAudioFallbackReason::QueueOverflow);
            return false;
        }
    }

    bool ValidatePacketFormat(const AVPacket* packet) noexcept
    {
        size_t changeSize = 0;
        if (av_packet_get_side_data(packet, AV_PKT_DATA_PARAM_CHANGE, &changeSize) && changeSize)
        {
            Fail(EmbyAudioFallbackReason::FormatChanged);
            return false;
        }
        const auto minimum = Format.Codec == EmbyAudioCodec::Dts || Format.Codec == EmbyAudioCodec::DtsHd ? 10 : 8;
        if (packet->size < minimum)
        {
            Fail(EmbyAudioFallbackReason::MuxFailed);
            return false;
        }
        uint8_t* parsed = nullptr;
        int parsedSize = 0;
        // Some parsers only fill these fields when they are unspecified. Parse
        // every real packet header instead of carrying the original descriptor
        // forward as if it proved that a stream's format never changed.
        ParserContext->sample_rate = 0;
        if (Parameters->codec_id == AV_CODEC_ID_DTS) ParserContext->profile = AV_PROFILE_UNKNOWN;
        auto result = av_parser_parse2(Parser, ParserContext, &parsed, &parsedSize,
            packet->data, packet->size, packet->pts, packet->dts, packet->pos);
        if (result < 0)
        {
            Fail(EmbyAudioFallbackReason::MuxFailed);
            return false;
        }
        if (ParserContext->sample_rate > 0)
        {
            if (Parameters->codec_id != AV_CODEC_ID_DTS && ParserContext->sample_rate != Parameters->sample_rate)
            {
                Fail(EmbyAudioFallbackReason::FormatChanged);
                return false;
            }
            // A DTS-HD packet can expose the core's rate while the decoded
            // source has a higher rate. Compare real headers with one another.
            if (ObservedHeaderSampleRate && ObservedHeaderSampleRate != ParserContext->sample_rate)
            {
                Fail(EmbyAudioFallbackReason::FormatChanged);
                return false;
            }
            ObservedHeaderSampleRate = ParserContext->sample_rate;
        }
        if (Parameters->codec_id == AV_CODEC_ID_DTS && ParserContext->profile != AV_PROFILE_UNKNOWN
            && IsDtsHdProfile(Parameters->profile) != IsDtsHdProfile(ParserContext->profile))
        {
            Fail(EmbyAudioFallbackReason::FormatChanged);
            return false;
        }
        if (Format.Codec == EmbyAudioCodec::DtsHd && ParserContext->profile != AV_PROFILE_UNKNOWN
            && (ParserContext->profile == AV_PROFILE_DTS_HD_HRA) != (Parameters->profile == AV_PROFILE_DTS_HD_HRA))
        {
            Fail(EmbyAudioFallbackReason::FormatChanged);
            return false;
        }
        return true;
    }

    bool InputExhausted() noexcept
    {
        std::lock_guard<std::mutex> lock(QueueMutex);
        return EndOfInputEpoch.load(std::memory_order_acquire) == AppliedQueueEpoch && Packets.empty();
    }

    bool FinishMux() noexcept
    {
        if (DrainAttempted || !InputExhausted()) return !Failed.load(std::memory_order_acquire);
        DrainAttempted = true;
        MuxBytes.clear();
        auto result = av_write_trailer(Mux);
        avio_flush(MuxIo);
        if (result < 0 || MuxIo->error < 0)
        {
            Fail(EmbyAudioFallbackReason::MuxFailed);
            return false;
        }
        if (!MuxBytes.empty())
        {
            auto start = MuxTimestamp ? MuxTimestamp : LastEncodedEnd;
            auto alignment = Format.Wave.FormatExt.Format.nBlockAlign;
            if (!start || !alignment || MuxBytes.size() % alignment)
            {
                Fail(EmbyAudioFallbackReason::MuxFailed);
                return false;
            }
            EncodedBurst burst;
            burst.Bytes = std::move(MuxBytes);
            burst.PresentationTicks = *start;
            burst.DurationTicks = static_cast<int64_t>((static_cast<long double>(burst.Bytes.size() / alignment)
                * TicksPerSecond) / Format.Wave.FormatExt.Format.nSamplesPerSec);
            LastEncodedEnd = burst.PresentationTicks + burst.DurationTicks;
            try { Bursts.push_back(std::move(burst)); }
            catch (...) { Fail(EmbyAudioFallbackReason::QueueOverflow); return false; }
            MuxTimestamp.reset();
        }
        // Some FFmpeg families do not finalize partial MAT/E-AC3 groups. The
        // public mux API cannot expose that retained tail: request real PCM
        // recovery instead of inspecting private structs or dropping samples.
        const auto oneSample = (TicksPerSecond + Parameters->sample_rate - 1) / Parameters->sample_rate;
        if (MuxTimestamp || (LastSourceEnd && (!LastEncodedEnd || *LastSourceEnd > *LastEncodedEnd + oneSample)))
        {
            Fail(EmbyAudioFallbackReason::MuxFailed);
            return false;
        }
        return true;
    }

    bool PrepareBursts(int64_t masterPosition, int64_t delay) noexcept
    {
        // This is source preroll, not a guessed media timeline. Only entire
        // timestamped bursts that have ended before the master are discarded.
        while (!HardwareStarted && !Bursts.empty()
            && Bursts.front().PresentationTicks + delay + Bursts.front().DurationTicks <= masterPosition)
            Bursts.pop_front();
        auto bufferBytes = static_cast<size_t>(BufferFrames) * Format.Wave.FormatExt.Format.nBlockAlign;
        size_t available = AvailableBytes();
        size_t attempts = 0;
        while (available < bufferBytes * 2 && Bursts.size() < MaximumBufferedBursts && attempts++ < 256)
        {
            PendingPacket packet;
            if (!PopPacket(packet)) break;
            // No device output or mux work starts from a retired packet epoch.
            if (packet.Epoch != AppliedQueueEpoch) continue;
            if (!MuxPacket(packet)) return false;
            while (!HardwareStarted && !Bursts.empty()
                && Bursts.front().PresentationTicks + delay + Bursts.front().DurationTicks <= masterPosition)
                Bursts.pop_front();
            available = AvailableBytes();
        }
        return FinishMux();
    }

    size_t AvailableBytes() const noexcept
    {
        size_t total = 0;
        for (const auto& burst : Bursts) total += burst.Bytes.size() - burst.SubmittedBytes;
        return total;
    }

    bool SubmitBuffer(bool allowFinalPadding = false) noexcept
    {
        const size_t byteCount = static_cast<size_t>(BufferFrames) * Format.Wave.FormatExt.Format.nBlockAlign;
        auto available = AvailableBytes();
        if (!available || (available < byteCount && !allowFinalPadding)) return false;
        BYTE* destination = nullptr;
        auto hr = Render->GetBuffer(BufferFrames, &destination);
        if (FAILED(hr))
        {
            Fail(DescribeEndpointFailure(hr));
            return false;
        }
        size_t copied = 0;
        for (auto& burst : Bursts)
        {
            auto size = (std::min)(byteCount - copied, burst.Bytes.size() - burst.SubmittedBytes);
            if (size)
            {
                memcpy(destination + copied, burst.Bytes.data() + burst.SubmittedBytes, size);
                burst.SubmittedBytes += size;
                copied += size;
            }
            if (copied == byteCount) break;
        }
        if (copied < byteCount)
        {
            // IEC 61937 transport null padding follows the last real payload.
            // It is not generated media, PCM, or a fabricated source duration.
            memset(destination + copied, 0, byteCount - copied);
        }
        hr = Render->ReleaseBuffer(BufferFrames, 0);
        if (FAILED(hr))
        {
            Fail(DescribeEndpointFailure(hr));
            return false;
        }
        SubmittedSinceReset += BufferFrames;
        {
            std::lock_guard<std::mutex> lock(ObservationMutex);
            Observation.SubmittedFrameCount += BufferFrames;
            if (HardwareStarted && !Failed.load(std::memory_order_acquire) && !StopRequested.load(std::memory_order_acquire))
            {
                Observation.ActualFormatConfirmed = 1;
                Observation.State = static_cast<int32_t>(EmbyAudioOutputState::Bitstream);
                CopyText(Observation.OutputFormatName, Format.Name);
            }
        }
        return true;
    }

    void MarkDrained() noexcept
    {
        if (!StopHardware(false)) return;
        Drained = true;
        std::lock_guard<std::mutex> lock(ObservationMutex);
        // A normal audio EOF can precede the video's EOF. Preserve the last
        // measured output format rather than forcing an unnecessary restart.
        if (Observation.SubmittedFrameCount && !Failed.load(std::memory_order_acquire))
        {
            Observation.State = static_cast<int32_t>(EmbyAudioOutputState::Bitstream);
            Observation.ActualFormatConfirmed = 1;
        }
    }

    bool ObserveHardware(int64_t masterPosition, int64_t delay) noexcept
    {
        if (!HardwareStarted || !HardwareOrigin) return true;
        UINT64 position = 0, qpc = 0;
        auto hr = HardwareClock->GetPosition(&position, &qpc);
        if (FAILED(hr))
        {
            Fail(DescribeEndpointFailure(hr));
            return false;
        }
        // An imprecise clock reading is not used to invent a correction.
        if (hr != S_OK) return true;
        auto playedTicks = static_cast<int64_t>((static_cast<long double>(position) * TicksPerSecond) / HardwareFrequency);
        auto actualPosition = *HardwareOrigin + playedTicks;
        auto drift = actualPosition - masterPosition;
        {
            std::lock_guard<std::mutex> lock(ObservationMutex);
            Observation.DriftTicks = drift;
            Observation.ClockPositionTicks = actualPosition;
        }
        LastHardwarePosition = actualPosition;
        while (!Bursts.empty() && Bursts.front().SubmittedBytes == Bursts.front().Bytes.size()
            && Bursts.front().PresentationTicks + delay + Bursts.front().DurationTicks <= actualPosition)
            Bursts.pop_front();
        if (std::abs(drift) > DiscontinuityLimit)
        {
            Fail(EmbyAudioFallbackReason::TimingDiscontinuity);
            return false;
        }
        if (std::abs(drift) > (std::max)(DriftRebaseThreshold, EndpointLatency + BufferDuration))
        {
            // Encoded samples cannot be resampled. Stop and rebase at a whole
            // real burst boundary while preserving unread future packets.
            if (!StopHardware(true)) return false;
            while (!Bursts.empty() && Bursts.front().PresentationTicks + delay + Bursts.front().DurationTicks <= masterPosition)
                Bursts.pop_front();
        }
        return true;
    }

    void Run() noexcept
    {
        bool apartment = false;
        HANDLE mmcss = nullptr;
        try
        {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            apartment = true;
            SetThreadDescription(GetCurrentThread(), L"Emby IEC 61937 audio");
            auto hr = OpenEndpoint();
            if (SUCCEEDED(hr) && !CreateMux())
            {
                SetEarlyFallback(EmbyAudioFallbackReason::MuxUnavailable);
                hr = S_FALSE;
            }
            else if (FAILED(hr))
            {
                SetEarlyFallback(DescribeEndpointFailure(hr));
                hr = S_FALSE;
            }
            if (StopRequested.load(std::memory_order_acquire)) hr = E_ABORT;
            {
                std::lock_guard<std::mutex> lock(LifecycleMutex);
                OpenResult = hr;
                OpenCompleted = true;
            }
            LifecycleChanged.notify_all();
            if (hr == S_OK)
            {
                DWORD taskIndex = 0;
                mmcss = AvSetMmThreadCharacteristicsW(L"Audio", &taskIndex);
                WorkerLoop();
            }
        }
        catch (...)
        {
            Fail(EmbyAudioFallbackReason::EndpointUnavailable);
            {
                std::lock_guard<std::mutex> lock(LifecycleMutex);
                OpenResult = E_FAIL;
                OpenCompleted = true;
            }
            LifecycleChanged.notify_all();
        }
        if (Client) Client->Stop();
        if (NotificationRegistered && Enumerator && Notifications)
            Enumerator->UnregisterEndpointNotificationCallback(Notifications.get());
        NotificationRegistered = false;
        Notifications = nullptr;
        HardwareClock = nullptr;
        Render = nullptr;
        Client = nullptr;
        Device = nullptr;
        Enumerator = nullptr;
        EndpointOwned.store(false, std::memory_order_release);
        DestroyMux();
        if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
        {
            std::lock_guard<std::mutex> lock(QueueMutex);
            Packets.clear();
            QueuedBytes = 0;
        }
        Bursts.clear();
        if (apartment) winrt::uninit_apartment();
        {
            std::lock_guard<std::mutex> lock(LifecycleMutex);
            WorkerExited = true;
        }
        LifecycleChanged.notify_all();
    }

    void WorkerLoop()
    {
        using winrt::Windows::Media::Playback::MediaPlaybackState;
        auto lastBoundClock = std::chrono::steady_clock::now();
        std::optional<std::chrono::steady_clock::time_point> noBurstSince;
        std::optional<std::chrono::steady_clock::time_point> seekWaitSince;
        bool wasPlaying = false;
        HANDLE events[]{ WakeEvent, BufferEvent };
        while (!StopRequested.load(std::memory_order_acquire) && !Failed.load(std::memory_order_acquire))
        {
            auto wait = WaitForMultipleObjects(2, events, FALSE, 10);
            if (wait == WAIT_FAILED)
            {
                Fail(EmbyAudioFallbackReason::EndpointUnavailable);
                break;
            }
            auto epoch = QueueEpoch.load(std::memory_order_acquire);
            auto revision = ResetRevision.load(std::memory_order_acquire);
            if (epoch != AppliedQueueEpoch)
            {
                if (!StopHardware(false)) break;
                Bursts.clear();
                if (!CreateMux())
                {
                    Fail(EmbyAudioFallbackReason::MuxFailed);
                    break;
                }
                AppliedQueueEpoch = epoch;
                noBurstSince.reset();
                seekWaitSince.reset();
            }
            if (revision != AppliedResetRevision)
            {
                if (!StopHardware(true)) break;
                AppliedResetRevision = revision;
                if (Drained) MarkDrained();
            }
            if (Suspended.load(std::memory_order_acquire))
            {
                if (HardwareStarted) StopHardware(true);
                continue;
            }
            if (Drained) continue;
            winrt::Windows::Media::Playback::MediaPlaybackSession session{ nullptr };
            int64_t masterPosition = 0;
            auto playbackState = MediaPlaybackState::None;
            double rate = 1;
            if (!ReadMaster(session, masterPosition, playbackState, rate))
            {
                if (HardwareStarted) StopHardware(true);
                if (std::chrono::steady_clock::now() - lastBoundClock > std::chrono::seconds(60))
                    Fail(EmbyAudioFallbackReason::ClockUnavailable);
                continue;
            }
            lastBoundClock = std::chrono::steady_clock::now();
            if (!std::isfinite(rate) || rate != 1.0)
            {
                Fail(EmbyAudioFallbackReason::RateUnsupported);
                break;
            }
            bool playing = playbackState == MediaPlaybackState::Playing;
            if (!playing)
            {
                if (HardwareStarted || wasPlaying) StopHardware(true);
                wasPlaying = false;
                noBurstSince.reset();
                seekWaitSince.reset();
                continue;
            }
            wasPlaying = true;
            auto expectedSeek = SeekTarget.load(std::memory_order_acquire);
            if (expectedSeek != INT64_MIN)
            {
                if (std::abs(masterPosition - expectedSeek) > DiscontinuityLimit)
                {
                    if (!seekWaitSince) seekWaitSince = std::chrono::steady_clock::now();
                    else if (std::chrono::steady_clock::now() - *seekWaitSince > std::chrono::seconds(10))
                        Fail(EmbyAudioFallbackReason::ClockUnavailable);
                    continue;
                }
                SeekTarget.compare_exchange_strong(expectedSeek, INT64_MIN, std::memory_order_acq_rel);
                seekWaitSince.reset();
            }
            auto delay = DelayTicks.load(std::memory_order_acquire);
            if (!ObserveHardware(masterPosition, delay)) break;
            if (!PrepareBursts(masterPosition, delay)) break;
            if (QueueEpoch.load(std::memory_order_acquire) != AppliedQueueEpoch
                || ResetRevision.load(std::memory_order_acquire) != AppliedResetRevision) continue;
            const bool finalInput = DrainAttempted && InputExhausted();
            if (finalInput && HardwareStarted && LastEncodedEnd && LastHardwarePosition
                && *LastHardwarePosition >= *LastEncodedEnd + delay && AvailableBytes() == 0)
            {
                MarkDrained();
                continue;
            }
            if (!HardwareStarted)
            {
                auto available = AvailableBytes();
                if (Bursts.empty() || !available)
                {
                    if (finalInput)
                    {
                        if (LastEncodedEnd && *LastEncodedEnd + delay <= masterPosition) MarkDrained();
                        else Fail(EmbyAudioFallbackReason::MuxFailed);
                    }
                    else if (!noBurstSince) noBurstSince = std::chrono::steady_clock::now();
                    else if (std::chrono::steady_clock::now() - *noBurstSince > std::chrono::seconds(2))
                        Fail(WaitingForTrueHdSync ? EmbyAudioFallbackReason::MetadataUnavailable
                            : EmbyAudioFallbackReason::TimingDiscontinuity);
                    continue;
                }
                if (available < static_cast<size_t>(BufferFrames) * Format.Wave.FormatExt.Format.nBlockAlign && !finalInput)
                {
                    if (!noBurstSince) noBurstSince = std::chrono::steady_clock::now();
                    else if (std::chrono::steady_clock::now() - *noBurstSince > std::chrono::seconds(2))
                        Fail(EmbyAudioFallbackReason::TimingDiscontinuity);
                    continue;
                }
                noBurstSince.reset();
                auto first = std::find_if(Bursts.begin(), Bursts.end(), [](const auto& burst)
                    { return burst.SubmittedBytes < burst.Bytes.size(); });
                if (first == Bursts.end()) continue;
                auto presentation = first->PresentationTicks + delay;
                if (presentation > masterPosition + EndpointLatency + BufferDuration) continue;
                // The origin is a timestamp from the actual packet sequence.
                HardwareOrigin = presentation;
                if (!SubmitBuffer(finalInput)) continue;
                auto hr = Client->Start();
                if (FAILED(hr))
                {
                    Fail(DescribeEndpointFailure(hr));
                    break;
                }
                HardwareStarted = true;
                {
                    std::lock_guard<std::mutex> lock(ObservationMutex);
                    if (!Failed.load(std::memory_order_acquire) && !StopRequested.load(std::memory_order_acquire))
                    {
                        Observation.ActualFormatConfirmed = 1;
                        Observation.State = static_cast<int32_t>(EmbyAudioOutputState::Bitstream);
                        CopyText(Observation.OutputFormatName, Format.Name);
                    }
                }
                continue;
            }
            // A busy read-ahead producer can keep WakeEvent signaled. Service
            // the render event as well instead of starving the actual device.
            if (wait == WAIT_OBJECT_0 + 1 || WaitForSingleObject(BufferEvent, 0) == WAIT_OBJECT_0)
            {
                if (!SubmitBuffer(finalInput))
                {
                    if (finalInput && !Failed.load(std::memory_order_acquire))
                    {
                        // No more buffers are queued. The hardware clock still
                        // drains the last submitted real payload before Stop.
                        continue;
                    }
                    // Do not continue a successful video-only source with a
                    // silent/null audio sink when real encoded data is missing.
                    if (!Failed.load(std::memory_order_acquire)) Fail(EmbyAudioFallbackReason::TimingDiscontinuity);
                    break;
                }
            }
        }
    }

    mutable std::mutex ObservationMutex;
    AudioOutputObservation Observation{};
    std::mutex QueueMutex;
    std::deque<PendingPacket> Packets;
    size_t QueuedBytes = 0;
    std::mutex ClockMutex;
    winrt::weak_ref<winrt::Windows::Media::Playback::MediaPlaybackSession> Master{ nullptr };
    std::mutex LifecycleMutex;
    std::condition_variable LifecycleChanged;
    bool WorkerStarted = false;
    bool WorkerExited = false;
    bool OpenCompleted = false;
    HRESULT OpenResult = E_FAIL;
    std::atomic<bool> StopRequested{ false };
    std::atomic<bool> Failed{ false };
    std::atomic<bool> EndpointOwned{ false };
    std::atomic<bool> Suspended{ false };
    std::atomic<uint64_t> QueueEpoch{ 1 };
    std::atomic<uint64_t> EndOfInputEpoch{ 0 };
    std::atomic<uint64_t> ResetRevision{ 1 };
    std::atomic<int64_t> SeekTarget{ INT64_MIN };
    std::atomic<int64_t> DelayTicks{ 0 };
    uint64_t AppliedQueueEpoch = 1;
    uint64_t AppliedResetRevision = 1;
    AVCodecParameters* Parameters = nullptr;
    EncodedFormat Format;
    HANDLE WakeEvent = nullptr;
    HANDLE BufferEvent = nullptr;
    winrt::com_ptr<IMMDeviceEnumerator> Enumerator;
    winrt::com_ptr<IMMDevice> Device;
    winrt::com_ptr<IAudioClient> Client;
    winrt::com_ptr<IAudioRenderClient> Render;
    winrt::com_ptr<IAudioClock> HardwareClock;
    winrt::com_ptr<DeviceNotifications> Notifications;
    std::wstring EndpointId;
    bool NotificationRegistered = false;
    REFERENCE_TIME BufferDuration = 0;
    REFERENCE_TIME EndpointLatency = 0;
    UINT32 BufferFrames = 0;
    UINT64 HardwareFrequency = 0;
    bool HardwareStarted = false;
    std::optional<int64_t> HardwareOrigin;
    std::optional<int64_t> LastHardwarePosition;
    uint64_t SubmittedSinceReset = 0;
    AVFormatContext* Mux = nullptr;
    AVIOContext* MuxIo = nullptr;
    AVCodecParserContext* Parser = nullptr;
    AVCodecContext* ParserContext = nullptr;
    std::vector<uint8_t> MuxBytes;
    std::deque<EncodedBurst> Bursts;
    std::optional<int64_t> MuxTimestamp;
    std::optional<int64_t> NextMuxTimestamp;
    bool WaitingForTrueHdSync = false;
    int ObservedHeaderSampleRate = 0;
    bool DrainAttempted = false;
    bool Drained = false;
    std::optional<int64_t> LastSourceEnd;
    std::optional<int64_t> LastEncodedEnd;
    std::function<void()> ConsumerWake;
};

WasapiBitstreamOutput::WasapiBitstreamOutput(const AVCodecParameters* parameters, int32_t streamIndex,
    EmbyAudioPassthroughMode mode, std::function<void()> consumerWake)
    : state(std::make_shared<State>(parameters, streamIndex, mode, std::move(consumerWake))) { }

WasapiBitstreamOutput::~WasapiBitstreamOutput()
{
    // The detached worker owns State until endpoint cleanup completes. Normal
    // retirement calls Close outside source/reader locks and waits explicitly.
    RequestStop();
}

HRESULT WasapiBitstreamOutput::TryOpen() noexcept
{
    auto current = state;
    std::unique_lock<std::mutex> lock(current->LifecycleMutex);
    if (current->WorkerStarted) return E_UNEXPECTED;
    {
        std::lock_guard<std::mutex> observationLock(current->ObservationMutex);
        if (current->Observation.State == static_cast<int32_t>(EmbyAudioOutputState::Pcm)) return S_FALSE;
    }
    current->WorkerStarted = true;
    try
    {
        std::thread([current] { current->Run(); }).detach();
    }
    catch (...)
    {
        current->WorkerExited = true;
        current->OpenCompleted = true;
        current->SetEarlyFallback(EmbyAudioFallbackReason::EndpointUnavailable);
        return S_FALSE;
    }
    if (!current->LifecycleChanged.wait_for(lock, std::chrono::seconds(15), [&] { return current->OpenCompleted; }))
    {
        current->StopRequested.store(true, std::memory_order_release);
        current->Wake();
        // A still-opening driver is not a safe early PCM success.
        return E_ABORT;
    }
    auto result = current->OpenResult;
    if (result != S_OK && !current->LifecycleChanged.wait_for(lock, std::chrono::seconds(2),
        [&] { return current->WorkerExited; }))
    {
        current->StopRequested.store(true, std::memory_order_release);
        current->Wake();
        // PCM is not safe until the failed opening has released its endpoint.
        return E_ABORT;
    }
    return result;
}

bool WasapiBitstreamOutput::OwnsOutput() const noexcept
{
    return state->EndpointOwned.load(std::memory_order_acquire)
        && !state->StopRequested.load(std::memory_order_acquire) && !state->Failed.load(std::memory_order_acquire);
}

void WasapiBitstreamOutput::QueuePacket(AVPacket* packet, int64_t presentationTicks, int64_t durationTicks) noexcept
{
    PacketPtr owned(packet);
    if (!packet || state->StopRequested.load(std::memory_order_acquire) || state->Failed.load(std::memory_order_acquire)) return;
    if (packet->size <= 0 || presentationTicks == INT64_MIN)
    {
        state->Fail(EmbyAudioFallbackReason::MetadataUnavailable);
        return;
    }
    try
    {
        std::lock_guard<std::mutex> lock(state->QueueMutex);
        if (state->StopRequested.load(std::memory_order_acquire) || state->Failed.load(std::memory_order_acquire)) return;
        if (state->QueuedBytes + static_cast<size_t>(packet->size) > MaximumQueuedBytes
            || state->Packets.size() >= MaximumQueuedPackets
            || (!state->Packets.empty() && presentationTicks > state->Packets.front().PresentationTicks
                && presentationTicks - state->Packets.front().PresentationTicks > MaximumQueuedDuration))
        {
            state->Fail(EmbyAudioFallbackReason::QueueOverflow);
            return;
        }
        PendingPacket pending;
        pending.Packet = std::move(owned);
        pending.PresentationTicks = presentationTicks;
        pending.DurationTicks = durationTicks;
        pending.Epoch = state->QueueEpoch.load(std::memory_order_acquire);
        state->Packets.push_back(std::move(pending));
        state->QueuedBytes += static_cast<size_t>(packet->size);
    }
    catch (...)
    {
        state->Fail(EmbyAudioFallbackReason::QueueOverflow);
    }
    state->Wake();
}

bool WasapiBitstreamOutput::IsSoftFull() const noexcept
{
    std::lock_guard<std::mutex> lock(state->QueueMutex);
    return state->QueuedBytes >= SoftQueuedBytes
        || (state->Packets.size() > 1
            && state->Packets.back().PresentationTicks > state->Packets.front().PresentationTicks
            && state->Packets.back().PresentationTicks - state->Packets.front().PresentationTicks >= SoftQueuedDuration);
}

void WasapiBitstreamOutput::NotifyEndOfStream() noexcept
{
    state->EndOfInputEpoch.store(state->QueueEpoch.load(std::memory_order_acquire), std::memory_order_release);
    state->Wake();
}

void WasapiBitstreamOutput::RequestFallback(EmbyAudioFallbackReason reason) noexcept
{
    state->Fail(reason);
}

void WasapiBitstreamOutput::BindPlaybackSession(winrt::Windows::Media::Playback::MediaPlaybackSession const& session) noexcept
{
    try
    {
        std::lock_guard<std::mutex> lock(state->ClockMutex);
        state->Master = session ? winrt::make_weak(session)
            : winrt::weak_ref<winrt::Windows::Media::Playback::MediaPlaybackSession>{ nullptr };
    }
    catch (...)
    {
        state->Fail(EmbyAudioFallbackReason::ClockUnavailable);
    }
    state->Wake();
}

void WasapiBitstreamOutput::PrepareSeek(int64_t targetTicks) noexcept
{
    state->SeekTarget.store(targetTicks, std::memory_order_release);
    Flush();
}

void WasapiBitstreamOutput::Flush() noexcept
{
    {
        std::lock_guard<std::mutex> lock(state->QueueMutex);
        state->QueueEpoch.fetch_add(1, std::memory_order_acq_rel);
        state->EndOfInputEpoch.store(0, std::memory_order_release);
        state->Packets.clear();
        state->QueuedBytes = 0;
    }
    state->ResetRevision.fetch_add(1, std::memory_order_acq_rel);
    state->Wake();
}

void WasapiBitstreamOutput::Suspend() noexcept
{
    state->Suspended.store(true, std::memory_order_release);
    state->ResetRevision.fetch_add(1, std::memory_order_acq_rel);
    state->Wake();
}

void WasapiBitstreamOutput::RequestStop() noexcept
{
    state->StopRequested.store(true, std::memory_order_release);
    state->Wake();
}

HRESULT WasapiBitstreamOutput::Close() noexcept
{
    RequestStop();
    std::unique_lock<std::mutex> lock(state->LifecycleMutex);
    if (state->WorkerStarted && !state->LifecycleChanged.wait_for(lock, std::chrono::seconds(2),
        [&] { return state->WorkerExited; }))
    {
        lock.unlock();
        std::lock_guard<std::mutex> observationLock(state->ObservationMutex);
        state->Observation.State = static_cast<int32_t>(EmbyAudioOutputState::FallbackRequired);
        state->Observation.ActualFormatConfirmed = 0;
        state->Observation.Reason = static_cast<int32_t>(EmbyAudioFallbackReason::SourceRetired);
        return E_PENDING;
    }
    lock.unlock();
    std::lock_guard<std::mutex> observationLock(state->ObservationMutex);
    state->Observation.State = static_cast<int32_t>(EmbyAudioOutputState::Stopped);
    state->Observation.ActualFormatConfirmed = 0;
    if (!state->Observation.Reason) state->Observation.Reason = static_cast<int32_t>(EmbyAudioFallbackReason::SourceRetired);
    return S_OK;
}

void WasapiBitstreamOutput::SetDelay(int64_t delayTicks) noexcept
{
    if (delayTicks < -10 * TicksPerSecond || delayTicks > 10 * TicksPerSecond)
    {
        state->Fail(EmbyAudioFallbackReason::TimingDiscontinuity);
        return;
    }
    if (state->DelayTicks.exchange(delayTicks, std::memory_order_acq_rel) != delayTicks)
    {
        // Raw packets and muxed bursts keep their original timestamps. Reset
        // only endpoint submission so the new delay also affects queued data.
        state->ResetRevision.fetch_add(1, std::memory_order_acq_rel);
        state->Wake();
    }
}

AudioOutputObservation WasapiBitstreamOutput::Snapshot() const noexcept
{
    std::lock_guard<std::mutex> lock(state->ObservationMutex);
    return state->Observation;
}
