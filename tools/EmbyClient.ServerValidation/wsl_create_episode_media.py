#!/usr/bin/env python3
"""Copy the generated fixture into an independent two-episode TV library root."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import uuid


def main() -> int:
    os.umask(0o077)
    repository = Path(__file__).resolve().parents[2]
    artifacts = repository / "artifacts/emby-validation"
    runtime = Path((artifacts / "wsl-runtime-root.txt").read_text(encoding="utf-8-sig").strip())
    prefix = "emby-client-validation-"
    if runtime.parent != Path("/tmp") or not runtime.name.startswith(prefix):
        raise ValueError("The runtime marker is outside the owned temporary directory scope.")
    uuid.UUID(runtime.name[len(prefix):])
    if runtime.is_symlink() or runtime.stat().st_uid != os.geteuid():
        raise ValueError("The runtime directory is not owned by this user.")
    state = json.loads((runtime / "state.json").read_text(encoding="utf-8"))
    if state.get("mode") != "server" or state.get("status") != "ready":
        raise ValueError("The owned official server must be ready.")
    destination = runtime / "tv-media"
    if destination.exists():
        raise ValueError("The episode validation media already exists; it will not be replaced.")
    source = runtime / "media/Fixture (2026).mp4"
    with source.open("rb") as stream:
        expected_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    staging = runtime / f"episode-staging-{uuid.uuid4()}"
    season = staging / "Validation Series (2026)" / "Season 01"
    season.mkdir(parents=True, mode=0o700)
    episodes = []
    for number, title in ((1, "First Signal"), (2, "Second Signal")):
        filename = f"Validation Series - S01E{number:02d} - {title}.mp4"
        target = season / filename
        shutil.copyfile(source, target)
        with target.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != expected_hash:
                raise ValueError("A copied episode does not match the generated fixture.")
        episodes.append({"FileName": filename, "SeasonNumber": 1, "EpisodeNumber": number, "Sha256": expected_hash})
    staging.rename(destination)
    evidence = {"Synthetic": True, "SeriesDirectory": "Validation Series (2026)", "Episodes": episodes}
    (artifacts / "episode-media-evidence.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(evidence, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"status": "failed", "error_type": type(error).__name__, "error": str(error)}))
        raise SystemExit(1)
