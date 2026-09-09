#!/usr/bin/env python3
"""Signal only the live namespace runner identified by this checkout's state."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import select
import signal
import uuid


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--validate-only", action="store_true")
    arguments = parser.parse_args()
    repository = Path(__file__).resolve().parents[2]
    marker = repository / "artifacts/emby-validation/wsl-runtime-root.txt"
    runtime = Path(marker.read_text(encoding="utf-8-sig").strip())
    prefix = "emby-client-validation-"
    if runtime.parent != Path("/tmp") or not runtime.name.startswith(prefix):
        raise ValueError("The current runtime marker is outside the task's temporary directory scope.")
    uuid.UUID(runtime.name[len(prefix):])
    if runtime.is_symlink() or runtime.stat().st_uid != os.geteuid():
        raise ValueError("The runtime directory is not owned by the current user.")
    state = json.loads((runtime / "state.json").read_text(encoding="utf-8"))
    if state.get("mode") != "server" or state.get("status") != "ready":
        raise ValueError("The recorded official server is not in the ready state.")
    process_id = state["outer"]["pid"]
    if not isinstance(process_id, int) or process_id < 2:
        raise ValueError("The recorded process identifier is invalid.")

    # A pidfd keeps the validated process identity stable across a later signal.
    process_handle = os.pidfd_open(process_id)
    try:
        process_path = Path("/proc") / str(process_id)
        command = process_path.joinpath("cmdline").read_bytes().decode().rstrip("\0").split("\0")
        runner_path = str(Path(__file__).with_name("wsl_namespace_validation.py"))
        if runner_path not in command or command.count("--runtime-dir") != 1:
            raise ValueError("The recorded process is not this checkout's namespace runner.")
        if command[command.index("--runtime-dir") + 1] != str(runtime):
            raise ValueError("The recorded process belongs to a different runtime directory.")
        if process_path.stat().st_uid != os.geteuid():
            raise ValueError("The recorded process is not owned by the current user.")
        if os.readlink(process_path / "ns/net") != state["outer"]["network_namespace"]:
            raise ValueError("The recorded process namespace identity changed.")
        if arguments.validate_only:
            print(json.dumps({"status": "validated", "runtime_dir": str(runtime), "pid": process_id}))
            return 0
        signal.pidfd_send_signal(process_handle, signal.SIGTERM)
        if not select.select([process_handle], [], [], 20)[0]:
            raise RuntimeError("The owned runner did not exit before the shutdown deadline.")
        print(json.dumps({"status": "stopped", "runtime_dir": str(runtime), "pid": process_id}))
        return 0
    finally:
        os.close(process_handle)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"status": "failed", "error_type": type(error).__name__, "error": str(error)}))
        raise SystemExit(1)
