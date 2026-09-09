#!/usr/bin/env python3
"""Inspect bounded progressive responses from the explicitly owned Emby fixture."""

from __future__ import annotations

import hashlib
import http.client
import json
from pathlib import Path
import re
import time
import urllib.parse
import uuid


ROOT_URL = "http://127.0.0.1:19096"
SERVER_ID = "cf4feb10df224135877fc61204a28212"
SERVER_VERSION = "4.9.5.0"
ITEM_ID = "19"
SOURCE_ID = "mediasource_19"
SUBTITLE_INDEX = 2
PREFIX_LIMIT = 64 * 1024


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def profile() -> dict:
    # Mirror inspect_playlists.py and change only the transcoding container/protocol.
    conditions = [
        ("EqualsAny", "VideoProfile", "high|main|baseline|constrained baseline"),
        ("LessThanEqual", "VideoLevel", "41"),
        ("LessThanEqual", "VideoBitDepth", "8"),
        ("Equals", "IsInterlaced", "false"),
    ]
    return {
        "Name": "Windows Native AVC AAC Baseline", "MaxStreamingBitrate": 20000000, "MaxStaticBitrate": 20000000,
        "DirectPlayProfiles": [{"Type": "Video", "Container": "mp4", "VideoCodec": "h264", "AudioCodec": "aac"}],
        "TranscodingProfiles": [{"Type": "Video", "Container": "mp4", "Protocol": "http", "Context": "Streaming", "VideoCodec": "h264", "AudioCodec": "aac", "MaxAudioChannels": "2", "BreakOnNonKeyFrames": False, "CopyTimestamps": False}],
        "CodecProfiles": [
            {"Type": "Video", "Codec": "h264", "Conditions": [{"Condition": c, "Property": p, "Value": v, "IsRequired": True} for c, p, v in conditions]},
            {"Type": "VideoAudio", "Codec": "aac", "Conditions": [{"Condition": "LessThanEqual", "Property": "AudioChannels", "Value": "2", "IsRequired": True}]},
        ],
        "SubtitleProfiles": [{"Format": f, "Method": "Encode"} for f in ("srt", "vtt", "ass", "ssa", "sub", "pgssub", "dvdsub")],
    }


def safe_value(value: object) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if len(text) <= 256 and re.fullmatch(r"[A-Za-z0-9_.,|+*/;= -]*", text) else "[REDACTED]"


def safe_query(value: str) -> dict:
    allowed = {
        "starttimeticks", "copytimestamps", "subtitlestreamindex", "subtitlemethod", "mediasourceid",
        "videocodec", "audiocodec", "transcodereasons", "videobitrate", "audiobitrate", "maxaudiochannels",
        "maxwidth", "maxheight", "profile", "level", "static", "container", "segmentcontainer",
    }
    return {key: [safe_value(item) for item in values]
            for key, values in urllib.parse.parse_qs(urllib.parse.urlsplit(value).query).items()
            if key.lower() in allowed}


def prefix_summary(data: bytes) -> dict:
    boxes = []
    offset = 0
    while offset + 8 <= len(data) and len(boxes) < 128:
        size = int.from_bytes(data[offset:offset + 4], "big")
        kind_bytes = data[offset + 4:offset + 8]
        kind = kind_bytes.decode("ascii", errors="replace")
        if not re.fullmatch(r"[A-Za-z0-9 ]{4}", kind):
            break
        header = 8
        if size == 1:
            if offset + 16 > len(data):
                break
            size = int.from_bytes(data[offset + 8:offset + 16], "big")
            header = 16
        if size != 0 and size < header:
            break
        boxes.append({"Type": kind, "Offset": offset, "DeclaredSize": size,
                      "CompleteInPrefix": size != 0 and offset + size <= len(data)})
        if size == 0 or offset + size > len(data):
            break
        offset += size
    return {
        "BytesRead": len(data), "Sha256": digest(data), "TopLevelBoxes": boxes,
        "MarkerByteOffsets": {name: data.find(name.encode("ascii")) for name in ("ftyp", "moov", "moof")},
        "MarkerPresenceDoesNotProvePlayableOrComplete": True,
    }


def inspect_case(repository: Path, start_ticks: int) -> dict:
    credentials = json.loads((repository / "artifacts/emby-validation/official-user-credentials.json").read_text(encoding="utf-8-sig"))
    if credentials.get("ServerUrl") != ROOT_URL:
        raise ValueError("Only the owned loopback credential is accepted.")
    device = "progressive-prefix-" + uuid.uuid4().hex
    headers = {
        "X-Emby-Authorization": f'MediaBrowser Client="Windows Native Client", Device="Owned progressive prefix control", DeviceId="{device}", Version="1.0.0"',
        "Accept-Encoding": "identity",
        "Connection": "close",
    }
    session = None
    authenticated = False
    events = []
    evidence = {
        "ItemId": ITEM_ID, "MediaSourceId": SOURCE_ID, "SubtitleStreamIndex": SUBTITLE_INDEX,
        "RequestedStartTimeTicks": start_ticks, "DeviceIdHash": digest(device.encode()),
        "NativePlayback": False, "PlaybackReportsSent": False, "MaximumMediaBytes": PREFIX_LIMIT,
        "ProfileChangesFromBaseline": {"TranscodingProfiles[0].Container": "mp4", "TranscodingProfiles[0].Protocol": "http"},
    }

    def resolve(address: str, method: str, media: bool = False) -> urllib.parse.SplitResult:
        parsed = urllib.parse.urlsplit(urllib.parse.urljoin(ROOT_URL + "/emby/", address))
        if parsed.scheme != "http" or parsed.netloc != "127.0.0.1:19096" or parsed.fragment:
            raise ValueError("OutOfScopeOrigin")
        route = parsed.path.lower()
        allowed = (
            method == "GET" and not media and route == "/emby/system/info/public"
            or method == "POST" and not media and route in (
                "/emby/users/authenticatebyname", "/emby/sessions/logout", f"/emby/items/{ITEM_ID}/playbackinfo")
            or method == "DELETE" and not media and route == "/emby/videos/activeencodings"
            or method == "GET" and media and re.fullmatch(r"/(?:emby/)?videos/19/stream(?:\.mp4)?", route)
        )
        if not allowed:
            raise ValueError("OutOfScopeRoute")
        return parsed

    def request(method: str, address: str, body: dict | None = None) -> tuple[int, bytes]:
        parsed = resolve(address, method)
        sent = dict(headers)
        payload = None if body is None else json.dumps(body).encode("utf-8")
        if payload is not None:
            sent["Content-Type"] = "application/json"
        connection = http.client.HTTPConnection("127.0.0.1", 19096, timeout=30)
        response = None
        try:
            connection.request(method, parsed.path + ("?" + parsed.query if parsed.query else ""), payload, sent)
            response = connection.getresponse()
            data = response.read(4_000_001)
            events.append({"Method": method, "Path": parsed.path, "Status": response.status})
            if len(data) > 4_000_000:
                raise ValueError("ApiResponseTooLarge")
            if response.status >= 300:
                raise RuntimeError("ApiHttpFailure")
            return response.status, data
        finally:
            if response is not None:
                response.close()
            connection.close()

    def inspect_stream(address: str) -> None:
        parsed = resolve(address, "GET", media=True)
        sent = dict(headers)
        # Measure actual Range behavior rather than infer it from Accept-Ranges alone.
        sent["Range"] = f"bytes=0-{PREFIX_LIMIT - 1}"
        connection = http.client.HTTPConnection("127.0.0.1", 19096, timeout=30)
        response = None
        started = time.monotonic()
        try:
            connection.request("GET", parsed.path + ("?" + parsed.query if parsed.query else ""), headers=sent)
            response = connection.getresponse()
            evidence["Http"] = {
                "Status": response.status, "ContentType": safe_value(response.getheader("Content-Type")),
                "ContentLength": safe_value(response.getheader("Content-Length")),
                "TransferEncoding": safe_value(response.getheader("Transfer-Encoding")),
                "AcceptRanges": safe_value(response.getheader("Accept-Ranges")),
                "ContentRange": safe_value(response.getheader("Content-Range")),
                "ContentEncoding": safe_value(response.getheader("Content-Encoding")),
                "RequestedRange": sent["Range"], "HeadersAfterSeconds": round(time.monotonic() - started, 3),
            }
            events.append({"Method": "GET", "Path": parsed.path, "Status": response.status, "MediaPrefixOnly": True})
            if response.status not in (200, 206):
                raise RuntimeError("ProgressiveHttpFailure")
            prefix = response.read(PREFIX_LIMIT)
            evidence["Prefix"] = prefix_summary(prefix)
            evidence["Http"]["PrefixAfterSeconds"] = round(time.monotonic() - started, 3)
            evidence["Http"]["RangeObservation"] = "PartialResponse" if response.status == 206 else "RangeNotHonoredAsPartialResponse"
            evidence["Http"]["FullResponseNotRead"] = len(prefix) == PREFIX_LIMIT
            evidence["Status"] = "BoundedResponseObserved"
        finally:
            if response is not None:
                response.close()
            connection.close()
            evidence["MediaResponseDisposed"] = True
            evidence["MediaConnectionClosed"] = True

    try:
        evidence["Stage"] = "VerifyOwnedServer"
        _, raw_info = request("GET", "System/Info/Public")
        info = json.loads(raw_info)
        if info.get("Id") != SERVER_ID or info.get("Version") != SERVER_VERSION:
            raise ValueError("OwnedServerIdentityChanged")
        evidence["ServerVersion"] = info["Version"]
        evidence["Stage"] = "AuthenticateDedicatedDevice"
        _, raw_auth = request("POST", "Users/AuthenticateByName", {"Username": credentials["Username"], "Pw": credentials["Password"]})
        auth = json.loads(raw_auth)
        headers["X-Emby-Token"] = auth["AccessToken"]
        authenticated = True
        body = {
            "UserId": auth["User"]["Id"], "MediaSourceId": SOURCE_ID, "MaxStreamingBitrate": 20000000,
            "StartTimeTicks": start_ticks, "SubtitleStreamIndex": SUBTITLE_INDEX, "MaxAudioChannels": 2,
            "DeviceProfile": profile(), "EnableDirectPlay": False, "EnableDirectStream": True,
            "EnableTranscoding": True, "AllowVideoStreamCopy": True, "AllowAudioStreamCopy": True,
            "AllowInterlacedVideoStreamCopy": False, "IsPlayback": True, "AutoOpenLiveStream": False,
        }
        evidence["RequestWithoutIdentity"] = {key: value for key, value in body.items() if key != "UserId"}
        evidence["Stage"] = "NegotiateProgressive"
        _, raw_playback = request("POST", f"Items/{ITEM_ID}/PlaybackInfo", body)
        playback = json.loads(raw_playback)
        session = playback.get("PlaySessionId")
        if not session:
            raise ValueError("MissingOwnedPlaySession")
        evidence["PlaySessionIdHash"] = digest(session.encode())
        source = next(value for value in playback.get("MediaSources", []) if value.get("Id") == SOURCE_ID)
        subtitle = next(value for value in source.get("MediaStreams", [])
                        if value.get("Type", "").lower() == "subtitle" and value.get("Index") == SUBTITLE_INDEX)
        evidence["Negotiated"] = {
            "SourceProtocol": safe_value(source.get("Protocol")), "SourceContainer": safe_value(source.get("Container")),
            "SourceRunTimeTicks": source.get("RunTimeTicks"), "SourceSize": source.get("Size"),
            "TranscodingSubProtocol": safe_value(source.get("TranscodingSubProtocol")),
            "TranscodingContainer": safe_value(source.get("TranscodingContainer")),
            "SupportsTranscoding": source.get("SupportsTranscoding"), "SubtitleCodec": safe_value(subtitle.get("Codec")),
            "SubtitleDeliveryMethod": safe_value(subtitle.get("DeliveryMethod")),
            "ReturnedSafeQuery": safe_query(source.get("TranscodingUrl", "")),
        }
        if (source.get("RequiresOpening") is True or source.get("RequiresClosing") is True
                or source.get("IsInfiniteStream") is True or source.get("SupportsTranscoding") is not True
                or source.get("TranscodingSubProtocol", "").lower() != "http"
                or source.get("TranscodingContainer", "").lower() != "mp4"
                or subtitle.get("Codec", "").lower() != "ass" or subtitle.get("DeliveryMethod", "").lower() != "encode"):
            raise ValueError("ExpectedProgressiveAssBurnInNotNegotiated")
        stream_url = source.get("TranscodingUrl")
        if not stream_url:
            raise ValueError("MissingProgressiveUrl")
        parsed = resolve(stream_url, "GET", media=True)
        query = urllib.parse.parse_qs(parsed.query)
        returned_start = [value for key, values in query.items() if key.lower() == "starttimeticks" for value in values]
        if returned_start and (len(returned_start) != 1 or returned_start[0] != str(start_ticks)):
            raise ValueError("UnexpectedReturnedStartTime")
        evidence["AppliedFactoryMissingStartTime"] = not returned_start and start_ticks > 0
        if evidence["AppliedFactoryMissingStartTime"]:
            stream_url += ("&" if parsed.query else "?") + "StartTimeTicks=" + str(start_ticks)
        evidence["RequestedStreamSafeQuery"] = safe_query(stream_url)
        evidence["Stage"] = "ReadBoundedProgressivePrefix"
        inspect_stream(stream_url)
        evidence["Stage"] = "Complete"
    except Exception as error:
        evidence["Status"] = "Failed"
        evidence["ErrorType"] = type(error).__name__
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
        evidence["CleanupSucceeded"] = authenticated and bool(session) and len(cleanup) == 2 and all(value.get("Status") == 204 for value in cleanup)
        evidence["ApiEvents"] = events
        headers.clear()
        credentials.clear()
    return evidence


def main() -> None:
    repository = Path(__file__).resolve().parents[2]
    output = Path(__file__).resolve().parent / "artifacts" / ("progressive-prefix-control-" + time.strftime("%Y%m%d-%H%M%S", time.gmtime()) + "-" + uuid.uuid4().hex[:8])
    output.mkdir()
    report = {
        "RecordedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "ControlOnly": True,
        "NativePlayback": False, "PlayableOrCompleteMediaVerified": False, "MaximumMediaBytesPerCase": PREFIX_LIMIT,
        "ScriptSha256": digest(Path(__file__).read_bytes()),
        "ProfileSourceSha256": digest((repository / "src/EmbyClient.Playback/ConservativeDeviceProfile.cs").read_bytes()),
        "CoordinatorSourceSha256": digest((repository / "src/EmbyClient.Playback/PlaybackCoordinator.cs").read_bytes()),
        "Cases": [],
    }
    receipt = output / "progressive-evidence.json"
    for start in (0, 410000000):
        case = inspect_case(repository, start)
        report["Cases"].append(case)
        receipt.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        if not case["CleanupSucceeded"]:
            break
    report["Status"] = "BoundedResponsesObserved" if len(report["Cases"]) == 2 and all(case["Status"] == "BoundedResponseObserved" and case["CleanupSucceeded"] for case in report["Cases"]) else "Failed"
    receipt.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"Receipt": str(receipt), "Status": report["Status"], "Cases": report["Cases"]}, indent=2))


if __name__ == "__main__":
    main()
