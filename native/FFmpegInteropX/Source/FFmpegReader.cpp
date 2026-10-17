//*****************************************************************************
//
//	Copyright 2015 Microsoft Corporation
//
//	Licensed under the Apache License, Version 2.0 (the "License");
//	you may not use this file except in compliance with the License.
//	You may obtain a copy of the License at
//
//	http ://www.apache.org/licenses/LICENSE-2.0
//
//	Unless required by applicable law or agreed to in writing, software
//	distributed under the License is distributed on an "AS IS" BASIS,
//	WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//	See the License for the specific language governing permissions and
//	limitations under the License.
//
//*****************************************************************************

#include "pch.h"
#include "FFmpegReader.h"
#include "StreamBuffer.h"
#include "UncompressedSampleProvider.h"
#include "SubtitleProviderBitmap.h"
#include <chrono>
#include <limits>

using namespace Concurrency;

namespace
{
    struct PgsIndexedReference
    {
        int64_t Timestamp;
        int64_t BytePosition;
    };

    struct PgsPacketDeleter
    {
        void operator()(AVPacket* packet) const noexcept { av_packet_free(&packet); }
    };
    using OwnedPgsPacket = std::unique_ptr<AVPacket, PgsPacketDeleter>;

    struct PgsPrimeBudget
    {
        size_t Packets = 0;
        size_t Bytes = 0;
        static constexpr size_t MaximumPackets = 16384;
        static constexpr size_t MaximumBytes = 64 * 1024 * 1024;
    };

    struct PgsDiscardScope
    {
        std::vector<std::pair<AVStream*, AVDiscard>> Saved;
        PgsDiscardScope(AVFormatContext* format, int selected)
        {
            Saved.reserve(format->nb_streams);
            for (unsigned index = 0; index < format->nb_streams; ++index)
            {
                auto stream = format->streams[index];
                Saved.emplace_back(stream, stream->discard);
                stream->discard = static_cast<int>(index) == selected ? AVDISCARD_DEFAULT : AVDISCARD_ALL;
            }
        }
        ~PgsDiscardScope()
        {
            for (auto const& saved : Saved) saved.first->discard = saved.second;
        }
    };

    bool PgsPrimeInterrupted(AVFormatContext* format)
    {
        return format->interrupt_callback.callback
            && format->interrupt_callback.callback(format->interrupt_callback.opaque);
    }

    HRESULT ReadIndexedPgsPacket(AVFormatContext* format, int streamIndex,
        PgsIndexedReference const& reference, PgsPrimeBudget& budget, OwnedPgsPacket& result)
    {
        result.reset();
        if (PgsPrimeInterrupted(format)) return E_ABORT;
        if (av_seek_frame(format, streamIndex, reference.Timestamp, AVSEEK_FLAG_BACKWARD) < 0)
            return E_FAIL;
        while (budget.Packets < PgsPrimeBudget::MaximumPackets && budget.Bytes < PgsPrimeBudget::MaximumBytes)
        {
            if (PgsPrimeInterrupted(format)) return E_ABORT;
            OwnedPgsPacket packet(av_packet_alloc());
            if (!packet) return E_OUTOFMEMORY;
            if (av_read_frame(format, packet.get()) < 0) return E_FAIL;
            ++budget.Packets;
            if (packet->size < 0 || static_cast<size_t>(packet->size) > PgsPrimeBudget::MaximumBytes - budget.Bytes)
                return E_FAIL;
            budget.Bytes += static_cast<size_t>(packet->size);
            if (packet->stream_index != streamIndex) continue;
            if (packet->pts == AV_NOPTS_VALUE) return E_FAIL;
            if (packet->pts < reference.Timestamp) continue;
            if (packet->pts > reference.Timestamp) return E_FAIL;
            result = std::move(packet);
            return S_OK;
        }
        return E_FAIL;
    }

    HRESULT ReadNextPgsPacket(AVFormatContext* format, int streamIndex,
        PgsPrimeBudget& budget, OwnedPgsPacket& result)
    {
        result.reset();
        while (budget.Packets < PgsPrimeBudget::MaximumPackets && budget.Bytes < PgsPrimeBudget::MaximumBytes)
        {
            if (PgsPrimeInterrupted(format)) return E_ABORT;
            OwnedPgsPacket packet(av_packet_alloc());
            if (!packet) return E_OUTOFMEMORY;
            const auto read = av_read_frame(format, packet.get());
            if (read == AVERROR_EOF) return S_FALSE;
            if (read < 0) return E_FAIL;
            ++budget.Packets;
            if (packet->size < 0 || static_cast<size_t>(packet->size) > PgsPrimeBudget::MaximumBytes - budget.Bytes)
                return E_FAIL;
            budget.Bytes += static_cast<size_t>(packet->size);
            if (packet->stream_index != streamIndex) continue;
            if (packet->pts == AV_NOPTS_VALUE) return E_FAIL;
            result = std::move(packet);
            return S_OK;
        }
        return E_FAIL;
    }

    // S_FALSE leaves a normal video seek in place when no application-presented PGS is selected.
    HRESULT PrimeSelectedPgsForSeek(AVFormatContext* format,
        std::vector<std::shared_ptr<MediaSampleProvider>> const& providers,
        std::shared_ptr<MediaSampleProvider> const& video, TimeSpan requested,
        std::shared_ptr<SubtitleProviderBitmap>& primed)
    {
        primed.reset();
        if (!format || !format->iformat || !format->iformat->name || !video || requested.count() <= 0
            || strcmp(format->iformat->name, "matroska,webm") != 0)
            return S_FALSE;
        std::shared_ptr<SubtitleProviderBitmap> selected;
        for (auto const& provider : providers)
        {
            auto bitmap = std::dynamic_pointer_cast<SubtitleProviderBitmap>(provider);
            if (!bitmap || bitmap->IsExternal()) continue;
            const auto index = bitmap->StreamIndex();
            if (index < 0 || static_cast<unsigned>(index) >= format->nb_streams
                || format->streams[index]->codecpar->codec_id != AV_CODEC_ID_HDMV_PGS_SUBTITLE)
                continue;
            auto item = bitmap->PlaybackItemWeak.get();
            if (!item) continue;
            auto tracks = item.TimedMetadataTracks();
            bool active = false;
            for (uint32_t track = 0; track < tracks.Size(); ++track)
                if (tracks.GetAt(track) == bitmap->SubtitleTrack
                    && tracks.GetPresentationMode(track) == TimedMetadataTrackPresentationMode::ApplicationPresented)
                    active = true;
            if (!active) continue;
            if (selected) return E_UNEXPECTED;
            selected = std::move(bitmap);
        }
        if (!selected) return S_FALSE;
        auto stream = format->streams[selected->StreamIndex()];
        auto wanted = selected->ConvertPosition(requested);
        const auto requestedTimestamp = wanted;
        std::vector<PgsIndexedReference> reverseReferences;
        constexpr size_t maximumReferences = 4096;
        PgsPrimeBudget budget;
        PgsDiscardScope discard(format, selected->StreamIndex());
        bool foundReset = false;
        while (reverseReferences.size() < maximumReferences)
        {
            if (PgsPrimeInterrupted(format)) return E_ABORT;
            const auto entry = avformat_index_get_entry_from_timestamp(stream, wanted, AVSEEK_FLAG_BACKWARD);
            if (!entry)
            {
                break;
            }
            if (entry->timestamp == AV_NOPTS_VALUE || entry->pos < 0) return E_FAIL;
            const PgsIndexedReference reference{ entry->timestamp, entry->pos };
            if (!reverseReferences.empty() && reference.Timestamp >= reverseReferences.back().Timestamp)
                return E_FAIL;
            reverseReferences.push_back(reference);
            OwnedPgsPacket packet;
            auto read = ReadIndexedPgsPacket(format, selected->StreamIndex(), reference, budget, packet);
            if (FAILED(read)) return read;
            const auto kind = SubtitleProviderBitmap::InspectIndexedPgsPacket(packet.get());
            if (kind == SubtitleProviderBitmap::PgsSeekPacketKind::Invalid) return E_FAIL;
            if (kind == SubtitleProviderBitmap::PgsSeekPacketKind::Reset)
            {
                foundReset = true;
                break;
            }
            if (reference.Timestamp == (std::numeric_limits<int64_t>::min)()) return E_FAIL;
            wanted = reference.Timestamp - 1;
        }
        selected->BeginIndexedPgsPrime();
        OwnedPgsPacket packet;
        if (foundReset)
        {
            auto read = ReadIndexedPgsPacket(format, selected->StreamIndex(), reverseReferences.back(), budget, packet);
            if (FAILED(read)) return read;
        }
        else
        {
            // A partial/absent public index does not prove blankness. Start at the real
            // source beginning and inspect selected subtitle data instead of guessing.
            if (av_seek_frame(format, video->StreamIndex(), video->ConvertPosition(TimeSpan{0}), AVSEEK_FLAG_BACKWARD) < 0)
                return E_FAIL;
            const auto read = ReadNextPgsPacket(format, selected->StreamIndex(), budget, packet);
            if (FAILED(read)) return read;
            if (read == S_FALSE) return S_FALSE;
        }
        bool decodedAny = false;
        while (packet)
        {
            if (packet->pts > requestedTimestamp) break;
            if (!decodedAny && SubtitleProviderBitmap::InspectIndexedPgsPacket(packet.get())
                != SubtitleProviderBitmap::PgsSeekPacketKind::Reset)
                return E_FAIL;
            const auto decoded = selected->DecodeIndexedPgsPrimePacket(packet.release());
            if (FAILED(decoded)) return decoded;
            decodedAny = true;
            const auto read = ReadNextPgsPacket(format, selected->StreamIndex(), budget, packet);
            if (FAILED(read)) return read;
            if (read == S_FALSE)
            {
                packet.reset();
                break;
            }
        }
        if (!decodedAny) return S_FALSE;
        if (!selected->HasIndexedPgsPrimeState(requested)) return E_FAIL;
        primed = std::move(selected);
        return S_OK;
    }
}

FFmpegReader::FFmpegReader(AVFormatContext* avFormatCtx, std::vector<shared_ptr<MediaSampleProvider>>* initProviders, MediaSourceConfig config)
    : avFormatCtx(avFormatCtx)
    , sampleProviders(initProviders)
    , config(config)
{
}

FFmpegReader::~FFmpegReader()
{
}

void FFmpegReader::Start()
{
    std::lock_guard lock(mutex);
    if (!isActive && !isEof && config.General().ReadAheadBufferEnabled() && (config.General().ReadAheadBufferSize() > 0 || config.General().ReadAheadBufferDuration().count() > 0) && !config.as<implementation::MediaSourceConfig>()->IsFrameGrabber)
    {
        sleepTimerTarget = new call<int>([this](int value) { OnTimer(value); });
        if (!sleepTimerTarget)
        {
            return;
        }

        sleepTimer = new timer<int>(100u, 0, sleepTimerTarget, true);
        if (!sleepTimer)
        {
            delete sleepTimerTarget;
            return;
        }

        isSleeping = false;
        readTask = create_task([this]() { this->ReadDataLoop(); });
        isActive = true;
    }
}

void FFmpegReader::Stop()
{
    bool wait = false;
    {
        std::lock_guard lock(mutex);
        isSleeping = false;
        if (isActive)
        {
            isActive = false;
            wait = true;

            sleepTimer->stop();
        }
    }

    if (wait)
    {
        try
        {
            readTask.wait();
        }
        catch (...)
        {
            DebugMessage(L"Failed to wait for task. Probably destructor called from UI thread.\n");

            while (!readTask.is_done())
            {
                Sleep(1);
            }
        }
        delete sleepTimer;
        delete sleepTimerTarget;
    }

    if (forceReadStream != -1)
    {
        waitStreamEvent.set();
    }
    forceReadStream = -1;
    lastStream = nullptr;
    fullStream = nullptr;
}

void FFmpegReader::Flush()
{
    std::lock_guard lock(mutex);
    FlushCodecsAndBuffers();
}

void FFmpegReader::FlushCodecs()
{
    DebugMessage(L"FlushCodecs\n");
    for (auto& stream : *sampleProviders)
    {
        if (stream)
            stream->Flush(false);
    }
}

void FFmpegReader::FlushCodecsAndBuffers()
{
    DebugMessage(L"FlushCodecsAndBuffers\n");
    for (auto& stream : *sampleProviders)
    {
        if (stream)
            stream->Flush(true);
    }
    readResult = 0;
    isEof = false;
}

HRESULT FFmpegReader::Seek(TimeSpan position, TimeSpan& actualPosition, TimeSpan currentPosition, bool fastSeek, std::shared_ptr<MediaSampleProvider> videoStream, std::shared_ptr<MediaSampleProvider> audioStream)
{
    Stop();

    std::lock_guard lock(mutex);

    auto hr = S_OK;
    if (readResult != 0)
    {
        fastSeek = false;
        readResult = 0;
    }

    auto isForwardSeek = position > currentPosition;
    const auto indexedPgsMarker = config.FFmpegOptions().TryLookup(L"emby_indexed_pgs_accurate_seek");
    const bool indexedPgsSeek = indexedPgsMarker && StringUtils::ToString(indexedPgsMarker) == L"1";

    if (isForwardSeek && !indexedPgsSeek && TrySeekBuffered(position, actualPosition, fastSeek, isForwardSeek, videoStream, audioStream))
    {
        // all good
        DebugMessage(L"BufferedSeek!\n");
    }
    else if (fastSeek && !indexedPgsSeek)
    {
        hr = SeekFast(position, actualPosition, currentPosition, videoStream, audioStream);
    }
    else
    {
        DebugMessage(L"NormalSeek\n");
        // Select the first valid stream either from video or audio
        auto stream = videoStream ? videoStream : audioStream;
        int64_t seekTarget = stream->ConvertPosition(position);
        if (av_seek_frame(avFormatCtx, stream->StreamIndex(), seekTarget, AVSEEK_FLAG_BACKWARD) < 0)
        {
            hr = E_FAIL;
            DebugMessage(L" - ### Error while seeking\n");
        }
        else
        {
            std::shared_ptr<SubtitleProviderBitmap> primedPgs;
            if (indexedPgsSeek)
            {
                const auto indexed = PrimeSelectedPgsForSeek(avFormatCtx, *sampleProviders,
                    videoStream, position, primedPgs);
                if (FAILED(indexed)) return indexed;
                // Priming uses the same demuxer cursor. Restore the actual video target after
                // discard flags have been restored, even for a verified before-first blank.
                if (av_seek_frame(avFormatCtx, stream->StreamIndex(), seekTarget, AVSEEK_FLAG_BACKWARD) < 0)
                    return E_FAIL;
            }
            // Flush all active streams with buffers
            if (primedPgs)
            {
                for (auto const& provider : *sampleProviders)
                    if (provider && provider != primedPgs) provider->Flush(true);
                readResult = 0;
                isEof = false;
            }
            else FlushCodecsAndBuffers();

            const auto marker = config.FFmpegOptions().TryLookup(L"emby_finite_hls_accurate_seek");
            const bool accurateFiniteHls = marker && StringUtils::ToString(marker) == L"1"
                && avFormatCtx->iformat && avFormatCtx->iformat->name
                && strcmp(avFormatCtx->iformat->name, "hls") == 0 && videoStream && position.count() > 0;
            if (accurateFiniteHls)
            {
                constexpr int64_t second = 10000000;
                constexpr int64_t maximumPreroll = 30 * second;
                constexpr int maximumRetries = 4;
                constexpr int maximumPacketsPerAttempt = 4096;
                int64_t backoff = 0;
                for (int attempt = 0; ; ++attempt)
                {
                    if (avFormatCtx->interrupt_callback.callback
                        && avFormatCtx->interrupt_callback.callback(avFormatCtx->interrupt_callback.opaque))
                    {
                        hr = E_ABORT;
                        break;
                    }
                    TimeSpan firstPacket{};
                    TimeSpan packetDuration{};
                    const auto probeStarted = std::chrono::steady_clock::now();
                    int packetsRead = 0;
                    while (videoStream->packetBuffer->IsEmpty() && packetsRead < maximumPacketsPerAttempt
                        && std::chrono::steady_clock::now() - probeStarted < std::chrono::seconds(5))
                    {
                        if (avFormatCtx->interrupt_callback.callback
                            && avFormatCtx->interrupt_callback.callback(avFormatCtx->interrupt_callback.opaque))
                            break;
                        if (ReadPacket() < 0) break;
                        ++packetsRead;
                    }
                    if (videoStream->packetBuffer->IsEmpty())
                    {
                        hr = E_FAIL;
                        break;
                    }
                    const auto peek = videoStream->GetNextPacketTimestamp(firstPacket, packetDuration);
                    // S_FALSE includes EOF and missing timestamps; neither proves an accurate origin.
                    if (peek != S_OK)
                    {
                        hr = FAILED(peek) ? peek : E_FAIL;
                        break;
                    }
                    bool actualKeyWithPts = false;
                    {
                        const auto packet = videoStream->packetBuffer->PeekPacket();
                        actualKeyWithPts = packet && packet->pts != AV_NOPTS_VALUE
                            && (packet->flags & AV_PKT_FLAG_KEY) != 0;
                    }
                    if (!actualKeyWithPts)
                    {
                        hr = E_FAIL;
                        break;
                    }
                    const auto frameAllowance = packetDuration.count() > 0 && packetDuration.count() <= second / 10
                        ? packetDuration.count() : 0;
                    const auto earlyLimit = position.count() > maximumPreroll ? position.count() - maximumPreroll : 0;
                    if (firstPacket.count() < earlyLimit)
                    {
                        hr = E_FAIL;
                        break;
                    }
                    if (firstPacket.count() <= position.count()
                        || firstPacket.count() - position.count() <= frameAllowance)
                        break;
                    if (attempt >= maximumRetries || backoff >= maximumPreroll || backoff >= position.count())
                    {
                        hr = E_FAIL;
                        break;
                    }

                    // HLS nominal EXTINF boundaries may be slightly after the preceding I-frame.
                    // Preserve that real frame for decoding rather than moving presentation forward.
                    const auto overshoot = firstPacket.count() - position.count();
                    const auto observedStep = std::min<int64_t>(maximumPreroll,
                        overshoot > maximumPreroll - second / 8 ? maximumPreroll : overshoot + second / 8);
                    backoff = std::min<int64_t>(maximumPreroll,
                        std::max<int64_t>(std::max<int64_t>(second, observedStep), backoff * 2));
                    const auto beforeTarget = TimeSpan{ position.count() > backoff ? position.count() - backoff : 0 };
                    const auto earlierRawTarget = stream->ConvertPosition(beforeTarget);
                    if (earlierRawTarget >= seekTarget)
                    {
                        hr = E_FAIL;
                        break;
                    }
                    seekTarget = earlierRawTarget;
                    if (av_seek_frame(avFormatCtx, stream->StreamIndex(), seekTarget, AVSEEK_FLAG_BACKWARD) < 0)
                    {
                        hr = E_FAIL;
                        break;
                    }
                    FlushCodecsAndBuffers();
                }
                // actualPosition remains the requested clock. MediaStreamSource discards decoded
                // preroll samples before that clock; neither packet timestamps nor PTS are rewritten.
            }
        }
    }

    return hr;
}

HRESULT FFmpegReader::SeekFast(TimeSpan position, TimeSpan& actualPosition, TimeSpan currentPosition, std::shared_ptr<MediaSampleProvider> videoStream, std::shared_ptr<MediaSampleProvider> audioStream)
{
    DebugMessage(L"SeekFast\n");

    HRESULT hr = S_OK;
    int64_t seekTarget = videoStream->ConvertPosition(position);
    bool isUriSource = avFormatCtx->url;

    // fast seek
    bool seekForward;
    TimeSpan referenceTime;

    // decide seek direction
    if (isLastSeekForward && position > lastSeekStart && position <= lastSeekActual)
    {
        seekForward = true;
        referenceTime = lastSeekStart + ((position - lastSeekStart) * 0.2);
        DebugMessage(L" - ### Forward seeking continue\n");
    }
    else if (!isLastSeekForward && position < lastSeekStart && position >= lastSeekActual)
    {
        seekForward = false;
        referenceTime = lastSeekStart + ((position - lastSeekStart) * 0.2);
        DebugMessage(L" - ### Backward seeking continue\n");
    }
    else if (position >= currentPosition)
    {
        seekForward = true;
        referenceTime = currentPosition + ((position - currentPosition) * 0.2);
        DebugMessage(L" - ### Forward seeking\n");
    }
    else
    {
        seekForward = false;
        referenceTime = currentPosition + ((position - currentPosition) * 0.2);
        DebugMessage(L" - ### Backward seeking\n");
    }

    int64_t min = INT64_MIN;
    int64_t max = INT64_MAX;
    if (seekForward)
    {
        min = videoStream->ConvertPosition(referenceTime);
    }
    else
    {
        max = videoStream->ConvertPosition(referenceTime);
    }

    if (avformat_seek_file(avFormatCtx, videoStream->StreamIndex(), min, seekTarget, max, 0) < 0)
    {
        hr = E_FAIL;
        DebugMessage(L" - ### Error while seeking\n");
    }
    else
    {
        // Flush all active streams with buffers
        FlushCodecsAndBuffers();

        // get and apply keyframe position for fast seeking
        TimeSpan timestampVideo;
        TimeSpan timestampVideoDuration;
        hr = videoStream->GetNextPacketTimestamp(timestampVideo, timestampVideoDuration);
        bool hasVideoPts = hr == S_OK;

        if (hr == S_FALSE)
        {
            // S_FALSE means that the video packets do not contain timestamps
            if (std::dynamic_pointer_cast<UncompressedSampleProvider>(videoStream))
            {
                // If we do not use passthrough, we can decode (and drop) then next sample to get the correct time.
                auto sample = videoStream->GetNextSample();
                if (sample)
                {
                    timestampVideo = sample.Timestamp();
                    timestampVideoDuration = sample.Duration();
                    timestampVideo += timestampVideoDuration;
                    hr = S_OK;
                }
            }
            else
            {
                // Otherwise, try with audio stream instead
                hr = audioStream->GetNextPacketTimestamp(timestampVideo, timestampVideoDuration);
            }
        }

        int seekCount = 0;
        while (hr == S_OK && seekForward && timestampVideo < referenceTime && !isUriSource && hasVideoPts && seekCount++ < 10)
        {
            // our min position was not respected. try again with higher min and target.
            min += videoStream->ConvertDuration(TimeSpan{ 50000000 });
            seekTarget += videoStream->ConvertDuration(TimeSpan{ 50000000 });

            if (avformat_seek_file(avFormatCtx, videoStream->StreamIndex(), min, seekTarget, max, 0) < 0)
            {
                hr = E_FAIL;
                DebugMessage(L" - ### Error while seeking\n");
            }
            else
            {
                // Flush all active streams with buffers
                FlushCodecsAndBuffers();

                // get updated timestamp
                hr = videoStream->GetNextPacketTimestamp(timestampVideo, timestampVideoDuration);
            }
        }

        if (hr == S_OK)
        {
            actualPosition = timestampVideo;

            // remember last seek direction
            isLastSeekForward = seekForward;
            lastSeekStart = position;
            lastSeekActual = actualPosition;

            if (audioStream)
            {
                // if we have audio, we need to seek back a bit more to get 100% clean audio
                TimeSpan timestampAudio;
                TimeSpan timestampAudioDuration;
                hr = audioStream->GetNextPacketTimestamp(timestampAudio, timestampAudioDuration);
                if (hr == S_OK)
                {
                    // audio stream should start one sample before video
                    auto audioTarget = timestampVideo - timestampAudioDuration;
                    auto audioPreroll = timestampAudio - timestampVideo;
                    if (audioPreroll.count() > 0 && config.General().FastSeekCleanAudio() && !isUriSource)
                    {
                        seekTarget = videoStream->ConvertPosition(audioTarget - audioPreroll);
                        if (av_seek_frame(avFormatCtx, videoStream->StreamIndex(), seekTarget, AVSEEK_FLAG_BACKWARD | AVSEEK_FLAG_ANY) < 0)
                        {
                            hr = E_FAIL;
                            DebugMessage(L" - ### Error while seeking\n");
                        }
                        else
                        {
                            // Flush all active streams with buffers
                            FlushCodecsAndBuffers();

                            // Now drop all packets until desired keyframe position
                            videoStream->SkipPacketsUntilTimestamp(timestampVideo);
                            audioStream->SkipPacketsUntilTimestamp(audioTarget);

                            auto sample = audioStream->GetNextSample();
                            if (sample)
                            {
                                actualPosition = sample.Timestamp() + sample.Duration();
                            }
                        }
                    }
                    else if (audioPreroll.count() <= 0)
                    {
                        // Negative audio preroll. Just drop all packets until target position.
                        audioStream->SkipPacketsUntilTimestamp(audioTarget);

                        hr = audioStream->GetNextPacketTimestamp(timestampAudio, timestampAudioDuration);
                        if (hr == S_OK && (config.General().FastSeekCleanAudio() || (timestampAudio + timestampAudioDuration) <= timestampVideo))
                        {
                            // decode one audio sample to get clean output
                            auto sample = audioStream->GetNextSample();
                            if (sample)
                            {
                                actualPosition = sample.Timestamp() + sample.Duration();
                            }
                        }
                    }
                }
            }
        }
    }

    return hr;
}

bool FFmpegReader::TrySeekBuffered(TimeSpan position, TimeSpan& actualPosition, bool fastSeek, bool isForwardSeek, std::shared_ptr<MediaSampleProvider> videoStream, std::shared_ptr<MediaSampleProvider> audioStream)
{
    bool result = true;
    int vIndex = -1;
    int aIndex = -1;

    TimeSpan targetPosition = position;

    if (videoStream)
    {
        auto pts = videoStream->ConvertPosition(targetPosition);
        LONGLONG resultPts = pts;
        vIndex = videoStream->packetBuffer->TryFindPacketIndex(pts, resultPts, true, fastSeek, isForwardSeek);
        result &= vIndex >= 0;

        if (result && fastSeek)
        {
            targetPosition = videoStream->ConvertPosition(resultPts);
        }
    }

    if (result && audioStream)
    {
        auto pts = audioStream->ConvertPosition(targetPosition);
        LONGLONG resultPts = pts;
        aIndex = audioStream->packetBuffer->TryFindPacketIndex(pts, resultPts, false, fastSeek, isForwardSeek);
        result &= aIndex >= 0;
    }

    if (result && vIndex == 0)
    {
        // We are at correct position already. No flush required.
        DebugMessage(L"BufferedSeek: No flush\n");
    }
    else if (result)
    {
        // Flush all active streams but keep buffers
        FlushCodecs();

        if (videoStream)
        {
            videoStream->packetBuffer->DropPackets(vIndex);
        }

        if (audioStream)
        {
            if (config.General().FastSeekCleanAudio() && aIndex > 0)
            {
                aIndex--;
                audioStream->packetBuffer->DropPackets(aIndex);

                // decode one audio sample to get clean output
                auto sample = audioStream->GetNextSample();
                if (sample)
                {
                    // TODO check if this is a good idea
                    //actualPosition = sample.Timestamp() + sample.Duration();
                }
            }
            else
            {
                audioStream->packetBuffer->DropPackets(aIndex);
            }
        }

        actualPosition = targetPosition;
    }

    return result;
}

void FFmpegReader::ReadDataLoop()
{
    bool sleep = false;

    while (true)
    {
        {
            std::lock_guard lock(mutex);
            if (!isActive) break;
            if (CheckNeedsSleep(sleep))
            {
                isSleeping = true;
                sleepTimer->start();
                break;
            }
        }
        // Read next packet
        ReadPacket();

        // Lock and check result
        std::lock_guard lock(mutex);
        if (!isActive)
        {
            // Stopped externally. No need to clean up.
            break;
        }
        else if (isEof || !config.General().ReadAheadBufferEnabled())
        {
            // Self stop. Cleanup.
            isActive = false;
            isSleeping = false;
            sleepTimer->stop();
            delete sleepTimer;
            delete sleepTimerTarget;
            waitStreamEvent.set();
            break;
        }
        else if (readResult < 0 && avFormatCtx->url)
        {
            // if this is a uri stream, give it a little time to recover
            Sleep(10);
        }
        else
        {
            // Check if needs sleep
            sleep = CheckNeedsSleep(sleep);
            if (sleep)
            {
                isSleeping = true;
                sleepTimer->start();
                break;
            }
        }
    }
}

void FFmpegReader::OnTimer(int value)
{
    UNREFERENCED_PARAMETER(value);
    std::lock_guard lock(mutex);
    ResumeReadAhead();
}

void FFmpegReader::NotifyConsumerProgress()
{
    std::lock_guard lock(mutex);
    ResumeReadAhead();
}

void FFmpegReader::ResumeReadAhead()
{
    // The caller holds mutex. Publish the running state before scheduling work.
    if (isActive && isSleeping)
    {
        sleepTimer->pause();
        isSleeping = false;
        readTask = create_task([this]() { ReadDataLoop(); });
    }
}

bool FFmpegReader::CheckNeedsSleep(bool wasSleeping)
{
    UNREFERENCED_PARAMETER(wasSleeping);
    bool force = forceReadStream >= 0;
    if (force)
    {
        return false;
    }

    if (fullStream && fullStream->IsEnabled() && fullStream->IsBufferFull()) return true;
    fullStream = nullptr;
    for (auto const& stream : *sampleProviders)
    {
        if (stream && stream->IsEnabled() && stream->IsBufferFull())
        {
            fullStream = stream;
            return true;
        }
    }
    return false;
}

// Read the next packet from the stream and push it into the appropriate
// sample provider
int FFmpegReader::ReadPacket()
{
    AVPacket* avPacket = av_packet_alloc();

    if (!avPacket)
    {
        DebugMessage(L"Out of memory!\n");
        isEof = true;
        return E_OUTOFMEMORY;
    }

    auto ret = av_read_frame(avFormatCtx, avPacket);
    std::lock_guard lock(mutex);
    readResult = ret;

    if (readResult < 0)
    {
        if (readResult == AVERROR_EOF || (avFormatCtx->pb && avFormatCtx->pb->eof_reached))
        {
            DebugMessage(L"End of stream reached. Stop reading packets.\n");
            isEof = true;
            for (auto const& provider : *sampleProviders)
            {
                if (provider && provider->IsEnabled()) provider->NotifyEndOfStream();
            }
        }
        else if (errorCount++ <= config.General().SkipErrors())
        {
            DebugMessage(L"Read packet error. Retrying...\n");
        }
        else
        {
            DebugMessage(L"Packet read error. Stop reading packets.\n");
            isEof = true;
        }
        av_packet_free(&avPacket);
    }
    else
    {
        errorCount = 0;
        if (avPacket->stream_index == forceReadStream)
        {
            forceReadStream = -1;
            waitStreamEvent.set();
        }

        if (avPacket->stream_index >= (int)sampleProviders->size())
        {
            // new stream detected. if this is a subtitle stream, we could create it now.
            av_packet_free(&avPacket);
        }
        else if (avPacket->stream_index < 0)
        {
            av_packet_free(&avPacket);
        }
        else
        {
            auto& provider = sampleProviders->at(avPacket->stream_index);
            if (provider)
            {
                provider->QueuePacket(avPacket);
                lastStream = provider;
            }
            else
            {
                DebugMessage(L"Ignoring unused stream\n");
                av_packet_free(&avPacket);
            }
        }
    }

    return readResult;
}


int FFmpegReader::ReadPacketForStream(StreamBuffer* buffer)
{
    if (!(buffer->IsEmpty()))
    {
        return 0;
    }

    bool manual;
    {
        std::lock_guard lock(mutex);
        manual = !isActive;
        if (isEof)
        {
            return AVERROR_EOF;
        }
    }

    if (manual)
    {
        // no read-ahead used
        while (!isEof)
        {
            ReadPacket();

            if (!(buffer->IsEmpty()))
            {
                break;
            }
        }
    }
    else
    {
        // read-ahead active
        while (true)
        {
            task<void> waitStreamTask;
            {
                std::lock_guard lock(mutex);

                if (!(buffer->IsEmpty()))
                {
                    forceReadStream = -1;
                    break;
                }
                else if (isEof || !isActive)
                {
                    break;
                }
                else
                {
                    forceReadStream = buffer->StreamIndex;
                    waitStreamEvent = task_completion_event<void>();
                    waitStreamTask = create_task(waitStreamEvent);
                }
            }

            waitStreamTask.wait();
        }
    }

    return readResult;
}
