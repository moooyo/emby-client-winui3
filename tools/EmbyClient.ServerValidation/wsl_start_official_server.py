#!/usr/bin/env python3
"""Extract the official package and keep one WSL invocation alive through validation."""

from __future__ import annotations

import hashlib
import fcntl
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import time
import uuid


PACKAGE_NAME = "emby-server-deb_4.9.5.0_amd64.deb"
PACKAGE_SHA256 = "1d718ffa0169c393de3eafda65b1b057a3db4ead93ffeb5883abd01735de9843"


def archive_diagnostics(runtime: Path, archive: Path) -> None:
    """Keep local evidence outside WSL's temporary filesystem during the run."""
    sources = []
    for name in ("state.json", "inner-state.json", "inner.log", "server.log"):
        sources.append(runtime / name)
    log_directory = runtime / "programdata/logs"
    sources.extend(log_directory.glob("embyserver*.txt"))
    for source in sources:
        try:
            if source.is_file():
                shutil.copyfile(source, archive / source.name)
        except OSError:
            # A temporary diagnostic-file lock must never stop the active server.
            continue


def run_validation(repository: Path, artifacts: Path) -> int:
    # This preflight catches an already-running legacy invocation before extraction.
    # The namespace runner also reserves the port atomically before starting Emby.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as preflight:
        preflight.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        preflight.bind(("127.0.0.1", 19096))
    package = artifacts / "downloads" / PACKAGE_NAME
    with package.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != PACKAGE_SHA256:
            raise ValueError("The official package SHA-256 does not match the pinned release.")

    fixture_root = repository / "tools" / "EmbyClient.MediaFixtures" / "artifacts" / "sixty-seconds"
    metadata = json.loads((fixture_root / "fixture-h264-aac.json").read_text(encoding="utf-8-sig"))
    fixture = fixture_root / "fixture-h264-aac.mp4"
    if metadata.get("Synthetic") is not True or fixture.stat().st_size != metadata["FileLength"]:
        raise ValueError("A matching generated synthetic fixture is required.")

    run_id = str(uuid.uuid4())
    runtime = Path("/tmp") / f"emby-client-validation-{run_id}"
    runtime.mkdir(mode=0o700)
    archive = artifacts / "wsl-runs" / run_id
    archive.mkdir(parents=True)
    for directory in ("programdata/config", "media", "tmp", "home"):
        runtime.joinpath(directory).mkdir(parents=True, mode=0o700)
    subprocess.run(["/usr/bin/dpkg-deb", "--extract", str(package), str(runtime / "package")], check=True)
    app_root = runtime / "package" / "opt" / "emby-server"
    shutil.copyfile(fixture, runtime / "media" / "Fixture (2026).mp4")
    runtime.joinpath("media/Fixture (2026).eng.srt").write_text(
        "1\n00:00:02,000 --> 00:00:08,000\nOfficial server validation subtitle.\n\n"
        "2\n00:00:15,000 --> 00:00:22,000\nGenerated media only.\n\n"
        "3\n00:00:35,000 --> 00:00:42,000\nSeek and subtitle delivery check.\n",
        encoding="utf-8",
    )
    runtime.joinpath("programdata/config/system.xml").write_text(
        "<ServerConfiguration>"
        "<IsStartupWizardCompleted>false</IsStartupWizardCompleted>"
        f"<ServerName>Official Emby validation {run_id}</ServerName>"
        "<EnableAutoUpdate>false</EnableAutoUpdate>"
        "<EnableAutomaticRestart>false</EnableAutomaticRestart>"
        "<EnableUPnP>false</EnableUPnP>"
        "<EnableRemoteAccess>false</EnableRemoteAccess>"
        "<HttpServerPortNumber>8096</HttpServerPortNumber>"
        "<PublicPort>8096</PublicPort>"
        "</ServerConfiguration>",
        encoding="utf-8",
    )
    command = [
        "/usr/bin/env",
        f"HOME={runtime / 'home'}", f"TMPDIR={runtime / 'tmp'}",
        f"EMBY_DATA={runtime / 'programdata'}",
        f"XDG_CACHE_HOME={runtime / 'programdata/cache'}",
        f"FONTCONFIG_PATH={app_root / 'etc/fonts'}",
        f"LD_LIBRARY_PATH={app_root / 'lib'}:{app_root / 'extra/lib'}",
        f"SSL_CERT_FILE={app_root / 'etc/ssl/certs/ca-certificates.crt'}",
        str(app_root / "system/EmbyServer"),
        "-programdata", str(runtime / "programdata"),
        "-ffdetect", str(app_root / "bin/ffdetect"),
        "-ffmpeg", str(app_root / "bin/ffmpeg"),
        "-ffprobe", str(app_root / "bin/ffprobe"),
        "-noautorunwebapp",
    ]
    runner_command = [
        sys.executable, str(Path(__file__).with_name("wsl_namespace_validation.py")),
        "--mode", "server", "--runtime-dir", str(runtime),
        "--server-root", str(app_root), "--media-path", str(runtime / "media"),
        "--server-command-json", json.dumps(command), "--server-port", "8096",
    ]
    print(json.dumps({"status": "prepared", "runtime_dir": str(runtime), "archive_dir": str(archive)}), flush=True)
    runner = subprocess.Popen(runner_command, close_fds=True)

    def stop_runner(signum: int, frame: object) -> None:
        if runner.poll() is None:
            runner.send_signal(signal.SIGTERM)

    signal.signal(signal.SIGTERM, stop_runner)
    signal.signal(signal.SIGINT, stop_runner)
    try:
        deadline = time.monotonic() + 190
        while runner.poll() is None and time.monotonic() < deadline:
            state_path = runtime / "state.json"
            if state_path.is_file():
                state = json.loads(state_path.read_text(encoding="utf-8"))
                if state.get("status") == "ready":
                    marker = artifacts / f"wsl-runtime-root.{run_id}.tmp"
                    marker.write_text(str(runtime) + "\n", encoding="utf-8")
                    marker.replace(artifacts / "wsl-runtime-root.txt")
                    break
            time.sleep(0.1)
        else:
            if runner.poll() is None:
                runner.terminate()
                raise RuntimeError("The namespace runner did not become ready.")
        while runner.poll() is None:
            archive_diagnostics(runtime, archive)
            time.sleep(2)
        return runner.returncode
    finally:
        if runner.poll() is None:
            runner.terminate()
            runner.wait(timeout=15)
        # Retain local diagnostics before WSL's temporary filesystem can disappear.
        # These ignored artifacts must be sanitized before sharing or committing.
        archive_diagnostics(runtime, archive)


def main() -> int:
    os.umask(0o077)
    repository = Path(__file__).resolve().parents[2]
    artifacts = repository / "artifacts" / "emby-validation"
    artifacts.mkdir(parents=True, exist_ok=True)
    with (artifacts / "wsl-server-start.lock").open("a") as operation_lock:
        try:
            fcntl.flock(operation_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise RuntimeError("An official server validation invocation is already active.") from error
        return run_validation(repository, artifacts)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"status": "failed", "error_type": type(error).__name__, "error": str(error)}), flush=True)
        raise SystemExit(1)
