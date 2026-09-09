#!/usr/bin/env python3
"""Create two versions of a generated movie with distinguishable AAC tracks."""

from __future__ import annotations

import json
import math
import os
from pathlib import Path
import struct
import subprocess
import uuid


MOVIE_NAME = "Track Validation (2026)"


def main() -> int:
    os.umask(0o077)
    repository = Path(__file__).resolve().parents[2]
    artifacts = repository / "artifacts/emby-validation"
    runtime = Path((artifacts / "wsl-runtime-root.txt").read_text(encoding="utf-8-sig").strip())
    prefix = "emby-client-validation-"
    if runtime.parent != Path("/tmp") or not runtime.name.startswith(prefix):
        raise ValueError("The active runtime is outside the owned validation directory scope.")
    uuid.UUID(runtime.name[len(prefix):])
    if runtime.is_symlink() or runtime.stat().st_uid != os.geteuid():
        raise ValueError("The active runtime is not owned by this user.")
    state = json.loads((runtime / "state.json").read_text(encoding="utf-8"))
    if state.get("status") != "ready" or state.get("mode") != "server":
        raise ValueError("The official validation server must be running.")
    app_root = runtime / "package/opt/emby-server"
    source = runtime / "media/Fixture (2026).mp4"
    destination = runtime / "media" / MOVIE_NAME
    if destination.exists():
        raise ValueError("The multitrack validation movie already exists; it will not be replaced.")
    staging = runtime / f"multitrack-staging-{uuid.uuid4()}"
    staging.mkdir(mode=0o700)

    def run_tool(name: str, arguments: list[str], *, binary: bool = False) -> bytes | str:
        command = [
            "/usr/bin/unshare", "--user", "--map-root-user", "--net", "--",
            "/usr/bin/env", f"HOME={runtime / 'home'}", f"TMPDIR={runtime / 'tmp'}",
            f"LD_LIBRARY_PATH={app_root / 'lib'}:{app_root / 'extra/lib'}",
            str(app_root / "bin" / name), *arguments,
        ]
        completed = subprocess.run(
            command, cwd=app_root, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120,
            close_fds=True,
            env={"PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "LANG": "C.UTF-8"},
        )
        if completed.returncode != 0:
            raise RuntimeError(f"The official {name} command failed with exit code {completed.returncode}: "
                               + completed.stderr.decode("utf-8", errors="replace")[-1500:])
        return completed.stdout if binary else completed.stdout.decode("utf-8")

    def probe(path: Path) -> dict:
        return json.loads(run_tool("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)]))

    initial = probe(source)
    duration = float(initial["format"]["duration"])
    audio_metadata = [
        "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=English - 440 Hz",
        "-metadata:s:a:1", "language=fra", "-metadata:s:a:1", "title=French - 880 Hz",
        "-disposition:a:0", "default", "-disposition:a:1", "0",
    ]
    version_720 = staging / f"{MOVIE_NAME} - 720p.mp4"
    run_tool("ffmpeg", [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
        "-protocol_whitelist", "file,pipe", "-i", str(source),
        "-f", "lavfi", "-i", f"sine=frequency=880:sample_rate=48000:duration={duration:.9f}",
        "-map", "0:v:0", "-map", "0:a:0", "-map", "1:a:0",
        "-map_metadata", "-1", "-c:v", "copy", "-c:a", "aac",
        "-b:a", "128k", "-ac", "2", "-t", f"{duration:.9f}",
        *audio_metadata, "-movflags", "+faststart", str(version_720),
    ])
    version_480 = staging / f"{MOVIE_NAME} - 480p.mp4"
    run_tool("ffmpeg", [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
        "-protocol_whitelist", "file,pipe", "-i", str(version_720),
        "-map", "0:v:0", "-map", "0:a:0", "-map", "0:a:1",
        "-vf", "scale=-2:480", "-c:v", "libx264", "-preset", "veryfast",
        "-crf", "23", "-pix_fmt", "yuv420p", "-threads", "2", "-c:a", "copy",
        *audio_metadata, "-movflags", "+faststart", str(version_480),
    ])

    evidence = {"MovieDirectoryName": MOVIE_NAME, "Synthetic": True, "Versions": []}
    for path in (version_720, version_480):
        observed = probe(path)
        videos = [stream for stream in observed["streams"] if stream["codec_type"] == "video"]
        audios = [stream for stream in observed["streams"] if stream["codec_type"] == "audio"]
        if len(videos) != 1 or len(audios) != 2 or any(audio["codec_name"] != "aac" for audio in audios):
            raise ValueError("Each version must contain one video and exactly two AAC audio streams.")
        tracks = []
        for ordinal, audio in enumerate(audios):
            pcm = run_tool("ffmpeg", [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-protocol_whitelist", "file,pipe", "-i", str(path),
                "-map", f"0:a:{ordinal}", "-t", "1", "-ac", "1", "-ar", "8000",
                "-f", "f32le", "pipe:1",
            ], binary=True)
            samples = struct.unpack(f"<{len(pcm) // 4}f", pcm)
            levels = {}
            for frequency in (440, 880):
                real = sum(value * math.cos(2 * math.pi * frequency * index / 8000) for index, value in enumerate(samples))
                imaginary = sum(value * math.sin(2 * math.pi * frequency * index / 8000) for index, value in enumerate(samples))
                levels[frequency] = math.hypot(real, imaginary)
            expected = 440 if ordinal == 0 else 880
            other = 880 if ordinal == 0 else 440
            if levels[expected] <= 10 * max(levels[other], 1e-9):
                raise ValueError("The decoded audio tracks are not sufficiently distinguishable.")
            tracks.append({
                "StreamIndex": audio["index"], "Codec": audio["codec_name"],
                "Language": audio.get("tags", {}).get("language"),
                "Channels": audio["channels"], "SampleRate": int(audio["sample_rate"]),
                "MatchedTargetFrequencyHz": expected,
                "FrequencyLevelRatio": round(levels[expected] / max(levels[other], 1e-9), 2),
            })
        evidence["Versions"].append({
            "FileName": path.name, "SizeBytes": path.stat().st_size,
            "DurationSeconds": float(observed["format"]["duration"]),
            "Width": videos[0]["width"], "Height": videos[0]["height"],
            "VideoCodec": videos[0]["codec_name"], "AudioTracks": tracks,
        })
    staging.rename(destination)
    (artifacts / "multitrack-media-evidence.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(evidence, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"status": "failed", "error_type": type(error).__name__, "error": str(error)}))
        raise SystemExit(1)
