#!/usr/bin/env python3
"""Compare actual playlists without requesting segments or starting playback."""

from __future__ import annotations

from decimal import Decimal
import hashlib
import http.client
import json
from pathlib import Path
import re
import time
import urllib.parse
import uuid


ROOT_URL = "http://127.0.0.1:19096"


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def profile() -> dict:
    # This literal mirrors the reviewed ConservativeDeviceProfile.Create() baseline.
    # Its source hash is recorded with the observation to expose later drift.
    conditions = [
        ("EqualsAny", "VideoProfile", "high|main|baseline|constrained baseline"),
        ("LessThanEqual", "VideoLevel", "41"),
        ("LessThanEqual", "VideoBitDepth", "8"),
        ("Equals", "IsInterlaced", "false"),
    ]
    return {
        "Name": "Windows Native AVC AAC Baseline", "MaxStreamingBitrate": 20000000, "MaxStaticBitrate": 20000000,
        "DirectPlayProfiles": [{"Type": "Video", "Container": "mp4", "VideoCodec": "h264", "AudioCodec": "aac"}],
        "TranscodingProfiles": [{"Type": "Video", "Container": "ts", "Protocol": "hls", "Context": "Streaming", "VideoCodec": "h264", "AudioCodec": "aac", "MaxAudioChannels": "2", "BreakOnNonKeyFrames": False, "CopyTimestamps": False}],
        "CodecProfiles": [
            {"Type": "Video", "Codec": "h264", "Conditions": [{"Condition": c, "Property": p, "Value": v, "IsRequired": True} for c, p, v in conditions]},
            {"Type": "VideoAudio", "Codec": "aac", "Conditions": [{"Condition": "LessThanEqual", "Property": "AudioChannels", "Value": "2", "IsRequired": True}]},
        ],
        "SubtitleProfiles": [{"Format": f, "Method": "Encode"} for f in ("srt", "vtt", "ass", "ssa", "sub", "pgssub", "dvdsub")],
    }


def safe_query(value: str) -> dict:
    allowed = {"starttimeticks", "copytimestamps", "subtitlestreamindex", "subtitlemethod", "segmentlength", "minsegments", "mediasourceid", "videocodec", "audiocodec", "transcodereasons"}
    return {key: values for key, values in urllib.parse.parse_qs(urllib.parse.urlsplit(value).query).items() if key.lower() in allowed}


def inspect_case(repository: Path, output: Path, item_id: str, source_id: str, label: str) -> dict:
    credentials = json.loads((repository / "artifacts/emby-validation/official-user-credentials.json").read_text(encoding="utf-8-sig"))
    if credentials["ServerUrl"] != ROOT_URL:
        raise ValueError("Only the owned loopback credential is accepted.")
    device = "playlist-only-" + uuid.uuid4().hex
    headers = {"X-Emby-Authorization": f'MediaBrowser Client="Playlist Only Diagnostic", Device="Owned playlist inspection", DeviceId="{device}", Version="1.0.0"'}
    events = []
    session = None
    authenticated = False
    case_output = output / label
    case_output.mkdir()
    evidence = {"Label": label, "ItemId": item_id, "MediaSourceId": source_id, "DeviceIdHash": digest(device.encode()), "SegmentRequests": 0, "PlaybackStarted": False}

    def request(method: str, address: str, body: dict | None = None) -> tuple[int, bytes]:
        url = urllib.parse.urljoin(ROOT_URL + "/emby/", address)
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme != "http" or parsed.netloc != "127.0.0.1:19096":
            raise ValueError("An out-of-scope origin was returned.")
        route = parsed.path.lower()
        allowed_get = route.endswith("/system/info/public") or re.fullmatch(r"/(?:emby/)?videos/" + item_id + r"/(?:master|main)\.m3u8", route)
        allowed_post = route.endswith("/users/authenticatebyname") or route.endswith("/sessions/logout") or route.endswith(f"/items/{item_id}/playbackinfo")
        allowed_delete = route.endswith("/videos/activeencodings")
        if not ((method == "GET" and allowed_get) or (method == "POST" and allowed_post) or (method == "DELETE" and allowed_delete)):
            raise ValueError("The diagnostic refuses segments, playback reports, and unrelated API routes.")
        payload = None if body is None else json.dumps(body).encode()
        sent_headers = dict(headers)
        if payload is not None:
            sent_headers["Content-Type"] = "application/json"
        connection = http.client.HTTPConnection("127.0.0.1", 19096, timeout=30)
        try:
            connection.request(method, parsed.path + ("?" + parsed.query if parsed.query else ""), body=payload, headers=sent_headers)
            response = connection.getresponse()
            data = response.read(4000001)
            events.append({"Method": method, "Path": parsed.path, "Status": response.status})
            if len(data) > 4000000:
                raise ValueError("The diagnostic response exceeded its bounded size.")
            if response.status >= 300:
                raise RuntimeError(f"HTTP {response.status}")
            return response.status, data
        finally:
            connection.close()

    try:
        _, raw_info = request("GET", "System/Info/Public")
        info = json.loads(raw_info)
        if info["Id"] != "cf4feb10df224135877fc61204a28212" or info["Version"] != "4.9.5.0":
            raise ValueError("The official owned server identity changed.")
        _, raw_auth = request("POST", "Users/AuthenticateByName", {"Username": credentials["Username"], "Pw": credentials["Password"]})
        auth = json.loads(raw_auth)
        headers["X-Emby-Token"] = auth["AccessToken"]
        authenticated = True
        body = {
            "UserId": auth["User"]["Id"], "MediaSourceId": source_id, "MaxStreamingBitrate": 20000000,
            "StartTimeTicks": 410000000, "SubtitleStreamIndex": 2, "MaxAudioChannels": 2, "DeviceProfile": profile(),
            "EnableDirectPlay": False, "EnableDirectStream": True, "EnableTranscoding": True,
            "AllowVideoStreamCopy": True, "AllowAudioStreamCopy": True, "AllowInterlacedVideoStreamCopy": False,
            "IsPlayback": True, "AutoOpenLiveStream": False,
        }
        (case_output / "request.json").write_text(json.dumps(body, indent=2) + "\n")
        _, raw_playback = request("POST", f"Items/{item_id}/PlaybackInfo", body)
        playback = json.loads(raw_playback)
        session = playback.get("PlaySessionId")
        source = next(s for s in playback["MediaSources"] if s["Id"] == source_id)
        if not session or source.get("RequiresOpening") is True or source.get("RequiresClosing") is True or not source.get("TranscodingUrl"):
            raise ValueError("An ordinary finite transcoding source with its own session is required.")
        subtitle = next(s for s in source["MediaStreams"] if s["Type"].lower() == "subtitle" and s["Index"] == 2)
        if subtitle.get("DeliveryMethod", "").lower() != "encode":
            raise ValueError("The expected default-profile encoded subtitle was not negotiated.")
        master_url = urllib.parse.urljoin(ROOT_URL + "/emby/", source["TranscodingUrl"])
        _, master = request("GET", master_url)
        (case_output / "master.raw.m3u8").write_bytes(master)
        master_lines = master.decode("utf-8-sig").splitlines()
        references = [line.strip() for line in master_lines if line.strip() and not line.startswith("#")]
        if len(references) != 1:
            raise ValueError("Exactly one media playlist reference is required.")
        media_url = urllib.parse.urljoin(master_url, references[0])
        _, media = request("GET", media_url)
        (case_output / "main.raw.m3u8").write_bytes(media)
        lines = media.decode("utf-8-sig").splitlines()
        durations = [Decimal(line.split(":", 1)[1].split(",", 1)[0]) for line in lines if line.startswith("#EXTINF:")]
        segment_references = [line for line in lines if line.strip() and not line.startswith("#")]
        numbers = [int(re.search(r"/(\d+)\.ts$", urllib.parse.urlsplit(line).path).group(1)) for line in segment_references]
        if len(durations) != len(numbers):
            raise ValueError("The media playlist has unmatched durations and segments.")
        evidence.update({
            "ServerVersion": info["Version"], "SourceRunTimeTicks": source.get("RunTimeTicks"), "SourceContainer": source.get("Container"),
            "SubtitleCodec": subtitle.get("Codec"), "SubtitleDeliveryMethod": subtitle.get("DeliveryMethod"),
            "PlaySessionIdHash": digest(session.encode()), "RequestedStartTimeTicks": body["StartTimeTicks"],
            "MasterQuery": safe_query(master_url), "MediaQuery": safe_query(media_url),
            "MediaSequence": next((line.split(":", 1)[1] for line in lines if line.startswith("#EXT-X-MEDIA-SEQUENCE:")), None),
            "PlaylistType": next((line.split(":", 1)[1] for line in lines if line.startswith("#EXT-X-PLAYLIST-TYPE:")), None),
            "TargetDuration": next((line.split(":", 1)[1] for line in lines if line.startswith("#EXT-X-TARGETDURATION:")), None),
            "ExtInfCount": len(durations), "ExtInfTotalSeconds": str(sum(durations)), "ExtInfLastTwoSeconds": [str(x) for x in durations[-2:]],
            "EndList": "#EXT-X-ENDLIST" in lines, "ExtXStart": [line for line in lines if line.startswith("#EXT-X-START:")],
            "SegmentNumberFirst": numbers[0], "SegmentNumberLast": numbers[-1], "SegmentNumberCount": len(numbers),
            "MasterSha256": digest(master), "MediaSha256": digest(media),
            "MasterTags": [re.sub(r'URI="[^"]+"', 'URI="[REDACTED]"', line) for line in master_lines if line.startswith("#")],
            "SafeMediaTags": [line for line in lines if line.startswith(("#EXTINF:", "#EXT-X-MEDIA-SEQUENCE:", "#EXT-X-ENDLIST", "#EXT-X-PLAYLIST-TYPE:", "#EXT-X-TARGETDURATION:", "#EXT-X-START:"))],
        })
    except Exception as error:
        evidence["ErrorType"] = type(error).__name__
        evidence["Status"] = "Failed"
    finally:
        cleanup = []
        if authenticated:
            if session:
                try:
                    status, _ = request("DELETE", "Videos/ActiveEncodings?" + urllib.parse.urlencode({"DeviceId": device, "PlaySessionId": session}))
                    cleanup.append({"Operation": "OwnStopEncoding", "Status": status})
                except Exception as error:
                    cleanup.append({"Operation": "OwnStopEncoding", "ErrorType": type(error).__name__})
            try:
                status, _ = request("POST", "Sessions/Logout")
                cleanup.append({"Operation": "OwnLogout", "Status": status})
            except Exception as error:
                cleanup.append({"Operation": "OwnLogout", "ErrorType": type(error).__name__})
        evidence["Cleanup"] = cleanup
        evidence["ApiEvents"] = events
        evidence["PlaybackReportsSent"] = False
        if "Status" not in evidence:
            evidence["Status"] = "Observed" if all("Status" in x for x in cleanup) else "CleanupFailed"
        headers.clear()
        credentials.clear()
    return evidence


def main() -> None:
    repository = Path(__file__).resolve().parents[2]
    output = Path(__file__).resolve().parent / "artifacts" / ("playlist-inspection-" + time.strftime("%Y%m%d-%H%M%S", time.gmtime()) + "-" + uuid.uuid4().hex[:8])
    output.mkdir()
    observations = [inspect_case(repository, output, item, source, label) for item, source, label in [("19", "mediasource_19", "ass-mkv"), ("5", "mediasource_5", "srt-mp4")]]
    report = {
        "RecordedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "SegmentRequests": 0, "NativePlayback": False,
        "ProfileSourceSha256": digest((repository / "src/EmbyClient.Playback/ConservativeDeviceProfile.cs").read_bytes()),
        "CoordinatorSourceSha256": digest((repository / "src/EmbyClient.Playback/PlaybackCoordinator.cs").read_bytes()),
        "Cases": observations,
    }
    (output / "playlist-evidence.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"OutputDirectory": str(output), "Evidence": report}, indent=2))


if __name__ == "__main__":
    main()
