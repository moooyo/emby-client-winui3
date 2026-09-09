#!/usr/bin/env python3
"""Replay two recorded FFmpeg commands into isolated, fresh output directories."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import time
import uuid


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    repository = Path(__file__).resolve().parents[2]
    runtime = Path((repository / "artifacts/emby-validation/wsl-runtime-root.txt").read_text(encoding="utf-8-sig").strip())
    if runtime.parent != Path("/tmp") or not re.fullmatch(r"emby-client-validation-[0-9a-f-]{36}", runtime.name):
        raise ValueError("The runtime marker is outside the owned scope.")
    if runtime.is_symlink() or runtime.stat().st_uid != os.geteuid():
        raise ValueError("The runtime is not owned by the current user.")
    app = runtime / "package/opt/emby-server"
    output = Path(__file__).resolve().parent / "artifacts" / ("tail-command-replay-" + time.strftime("%Y%m%d-%H%M%S", time.gmtime()) + "-" + uuid.uuid4().hex[:8])
    output.mkdir()
    cases = [
        ("successful-mp4-srt", "ffmpeg-transcode-9424f1b0-7461-4276-a5f3-1c487da9b642_1.txt"),
        ("failed-ass-mkv-tail", "ffmpeg-transcode-94cd62f5-5679-4f53-9a77-ca3f1a2fbcfd_1.txt"),
    ]
    results = []
    for label, log_name in cases:
        directory = output / label
        directory.mkdir()
        for name in ("home", "tmp", "cache"):
            (directory / name).mkdir()
        log = runtime / "programdata/logs" / log_name
        lines = log.read_text(errors="replace").splitlines()
        command_lines = [line for line in lines if "/bin/ffmpeg " in line and " -i " in line]
        if len(command_lines) != 1:
            raise ValueError("Expected one actual FFmpeg command in the recorded log.")
        line = command_lines[0]
        original = shlex.split(line[line.index(str(runtime)):])
        if original[0] != str(app / "bin/ffmpeg") or any("http://" in arg or "https://" in arg for arg in original):
            raise ValueError("The replay only accepts the owned official executable and local inputs.")
        inputs = [Path(original[i + 1]) for i, value in enumerate(original) if value == "-i"]
        if len(inputs) != 1 or not inputs[0].is_relative_to(runtime):
            raise ValueError("The recorded input is outside owned generated media.")
        arguments = original.copy()
        replacements = []
        for option in ("-print_graphs_file", "-segment_list"):
            index = arguments.index(option) + 1
            previous = Path(arguments[index])
            if not previous.is_relative_to(runtime / "programdata"):
                raise ValueError("The expected recorded diagnostic/output path is outside the owned runtime.")
            arguments[index] = str(directory / previous.name)
            replacements.append({"Option": option, "OriginalFileName": previous.name, "NewFileName": Path(arguments[index]).name})
        previous = Path(arguments[-1])
        if not previous.is_relative_to(runtime / "programdata/transcoding-temp") or "%d.ts" not in previous.name:
            raise ValueError("The recorded output is not the expected segment pattern.")
        arguments[-1] = str(directory / previous.name)
        replacements.append({"Option": "segment-output", "OriginalFileName": previous.name, "NewFileName": previous.name})
        (directory / "argv.json").write_text(json.dumps(arguments, indent=2) + "\n")
        environment = {
            "PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "LANG": "C.UTF-8",
            "HOME": str(directory / "home"), "TMPDIR": str(directory / "tmp"), "XDG_CACHE_HOME": str(directory / "cache"),
            "FONTCONFIG_PATH": str(app / "etc/fonts"), "LD_LIBRARY_PATH": f"{app / 'lib'}:{app / 'extra/lib'}",
        }
        environment_arguments = [f"{name}={value}" for name, value in environment.items()]
        namespace_prefix = ["/usr/bin/unshare", "--user", "--map-root-user", "--net", "--", "/usr/bin/env", *environment_arguments]
        host_environment = {"PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "LANG": "C.UTF-8"}
        command = [*namespace_prefix, *arguments]
        completed = subprocess.run(command, cwd=app, env=host_environment, stdin=subprocess.DEVNULL, capture_output=True, timeout=60)
        (directory / "ffmpeg.stdout.txt").write_bytes(completed.stdout)
        (directory / "ffmpeg.stderr.txt").write_bytes(completed.stderr)
        stderr_lines = completed.stderr.decode(errors="replace").splitlines()
        shape = [{"Name": f.name, "Bytes": f.stat().st_size, "Sha256": sha256(f)} for f in directory.iterdir() if f.is_file() and (".ts" in f.name or ".m3u8" in f.name)]
        tail_files = [f for f in directory.iterdir() if f.is_file() and re.search(r"_20\.ts(?:\.tmp)?$", f.name)]
        tail_evidence = []
        for file in tail_files:
            probe_args = [str(app / "bin/ffprobe"), "-v", "error", "-show_streams", "-show_packets", "-show_format", "-of", "json", str(file)]
            probe = subprocess.run([*namespace_prefix, *probe_args], cwd=app, env=host_environment, stdin=subprocess.DEVNULL, capture_output=True, timeout=30)
            (directory / (file.name + ".ffprobe.json")).write_bytes(probe.stdout)
            (directory / (file.name + ".ffprobe.stderr.txt")).write_bytes(probe.stderr)
            parsed = json.loads(probe.stdout or b"{}")
            streams = [{k: s.get(k) for k in ("index", "codec_type", "codec_name", "start_time", "duration", "r_frame_rate", "avg_frame_rate")} for s in parsed.get("streams", [])]
            packets = [{k: p.get(k) for k in ("stream_index", "codec_type", "pts_time", "dts_time", "duration_time", "size", "flags")} for p in parsed.get("packets", [])]
            tail_evidence.append({"Name": file.name, "Bytes": file.stat().st_size, "ProbeExitCode": probe.returncode, "Streams": streams, "PacketCount": len(packets), "Packets": packets, "AudioPacketCount": sum(p.get("codec_type") == "audio" for p in packets)})
        safe_options = []
        for i, argument in enumerate(original):
            if argument in ("-copyts", "-start_at_zero", "-sn"):
                safe_options.append([argument, True])
            elif argument in ("-ss", "-r:v:0", "-g:v:0", "-keyint_min:v:0", "-c:v:0", "-c:a:0", "-segment_start_number", "-segment_time", "-avoid_negative_ts", "-individual_header_trailer", "-write_header_trailer", "-segment_write_temp"):
                safe_options.append([argument, original[i + 1]])
        result = {
            "Case": label, "SourceLog": log_name, "SourceLogSha256": sha256(log), "InputSha256": sha256(inputs[0]), "InputFileName": inputs[0].name,
            "ReplacedOutputPathsOnly": replacements, "CommandOptions": safe_options, "SubtitleFilterPresent": "-filter_complex" in original,
            "ExitCode": completed.returncode, "OutputShape": shape, "TailFiles": tail_evidence,
            "SegmentCompleteLines": [line for line in stderr_lines if line.startswith("SegmentComplete=") or " SegmentComplete=" in line],
            "FinalProgress": [line for line in stderr_lines if "frame=" in line][-1:],
            "FontSelections": [line for line in stderr_lines if "fontselect:" in line],
        }
        results.append(result)
    report = {"OutputDirectory": str(output), "OfficialFfmpegSha256": sha256(app / "bin/ffmpeg"), "ServerInputsUnchanged": True, "ServerApiRequests": 0, "Cases": results}
    (output / "replay-evidence.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
