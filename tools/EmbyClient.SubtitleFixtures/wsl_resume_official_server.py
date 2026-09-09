#!/usr/bin/env python3
"""Resume only a cleanly stopped, owned official validation runtime."""

from __future__ import annotations

import fcntl
import hashlib
import importlib.util
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


def main() -> int:
    os.umask(0o077)
    repository = Path(__file__).resolve().parents[2]
    artifacts = repository / "artifacts/emby-validation"
    runtime = Path((artifacts / "wsl-runtime-root.txt").read_text(encoding="utf-8-sig").strip())
    prefix = "emby-client-validation-"
    if runtime.parent != Path("/tmp") or not runtime.name.startswith(prefix):
        raise ValueError("The runtime marker is outside the owned validation scope.")
    uuid.UUID(runtime.name[len(prefix):])
    if runtime.is_symlink() or runtime.resolve() != runtime or runtime.stat().st_uid != os.geteuid():
        raise ValueError("The runtime is not a real directory owned by the current user.")
    if runtime.stat().st_mode & 0o077:
        raise ValueError("The owned runtime is not private.")
    with (artifacts / "wsl-server-start.lock").open("a") as launch_lock:
        fcntl.flock(launch_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        state = json.loads((runtime / "state.json").read_text())
        inner = json.loads((runtime / "inner-state.json").read_text())
        if state.get("mode") != "server" or state.get("status") != "stopped" or inner.get("status") != "stopped":
            raise ValueError("Only a cleanly stopped server runtime can be resumed.")
        if state.get("runtime_dir") != str(runtime) or (runtime / "bridge.sock").exists():
            raise ValueError("The stopped runtime identity or socket state is unexpected.")
        for pid in (state["outer"]["pid"], state["inner_pid"], inner["server_pid"]):
            if Path("/proc", str(pid)).exists():
                raise ValueError("A previously recorded process identifier is still present; inspect ownership first.")
        with socket.socket() as preflight:
            preflight.bind(("127.0.0.1", 19096))
        package = artifacts / "downloads/emby-server-deb_4.9.5.0_amd64.deb"
        with package.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != "1d718ffa0169c393de3eafda65b1b057a3db4ead93ffeb5883abd01735de9843":
                raise ValueError("The original official package hash changed.")
        app_root = runtime / "package/opt/emby-server"
        for relative in ("system/EmbyServer", "bin/ffmpeg", "bin/ffprobe", "bin/ffdetect"):
            if not (app_root / relative).is_file():
                raise ValueError("An existing official server executable is missing.")
        resume_id = time.strftime("%Y%m%d-%H%M%S", time.gmtime()) + "-" + uuid.uuid4().hex[:8]
        history = runtime / "restart-history" / resume_id
        history.mkdir(parents=True, mode=0o700)
        archive = artifacts / "wsl-resume-runs" / resume_id
        archive.mkdir(parents=True)
        for name in ("state.json", "inner-state.json", "inner.log", "server.log"):
            source = runtime / name
            if source.is_file():
                shutil.copyfile(source, archive / ("previous-" + name))
                source.rename(history / name)
        command = [
            "/usr/bin/env", f"HOME={runtime / 'home'}", f"TMPDIR={runtime / 'tmp'}",
            f"EMBY_DATA={runtime / 'programdata'}", f"XDG_CACHE_HOME={runtime / 'programdata/cache'}",
            f"FONTCONFIG_PATH={app_root / 'etc/fonts'}",
            f"LD_LIBRARY_PATH={app_root / 'lib'}:{app_root / 'extra/lib'}",
            f"SSL_CERT_FILE={app_root / 'etc/ssl/certs/ca-certificates.crt'}",
            str(app_root / "system/EmbyServer"), "-programdata", str(runtime / "programdata"),
            "-ffdetect", str(app_root / "bin/ffdetect"), "-ffmpeg", str(app_root / "bin/ffmpeg"),
            "-ffprobe", str(app_root / "bin/ffprobe"), "-noautorunwebapp",
        ]
        server_tools = repository / "tools/EmbyClient.ServerValidation"
        runner = subprocess.Popen([
            sys.executable, str(server_tools / "wsl_namespace_validation.py"),
            "--mode", "server", "--runtime-dir", str(runtime), "--server-root", str(app_root),
            "--media-path", str(runtime / "media"), "--server-command-json", json.dumps(command),
            "--server-port", "8096",
        ], close_fds=True)
        spec = importlib.util.spec_from_file_location("owned_bootstrap", server_tools / "wsl_start_official_server.py")
        bootstrap = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(bootstrap)

        def stop_runner(signum: int, frame: object) -> None:
            if runner.poll() is None:
                runner.send_signal(signal.SIGTERM)

        signal.signal(signal.SIGTERM, stop_runner)
        signal.signal(signal.SIGINT, stop_runner)
        print(json.dumps({"status": "resuming", "runtime_dir": str(runtime), "archive_dir": str(archive)}), flush=True)
        try:
            while runner.poll() is None:
                bootstrap.archive_diagnostics(runtime, archive)
                time.sleep(2)
            return runner.returncode
        finally:
            if runner.poll() is None:
                runner.terminate()
                runner.wait(timeout=15)
            bootstrap.archive_diagnostics(runtime, archive)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"status": "failed", "error_type": type(error).__name__, "error": str(error)}), flush=True)
        raise SystemExit(1)
