#!/usr/bin/env python3
"""Copy only generated complex subtitle media into the owned runtime."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import uuid


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    arguments = parser.parse_args()
    repository = Path(__file__).resolve().parents[2]
    owned_artifacts = Path(__file__).resolve().parent / "artifacts"
    manifest_path = Path(arguments.manifest).resolve()
    if not manifest_path.is_relative_to(owned_artifacts) or manifest_path.name != "manifest.json":
        raise ValueError("Only this tool's generated manifest is accepted.")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if manifest.get("Synthetic") is not True or manifest.get("BindingStatus") != "UnboundGeneratedFilesOnly":
        raise ValueError("A generated, unbound synthetic manifest is required.")
    runtime = Path((repository / "artifacts/emby-validation/wsl-runtime-root.txt").read_text(encoding="utf-8-sig").strip())
    prefix = "emby-client-validation-"
    if runtime.parent != Path("/tmp") or not runtime.name.startswith(prefix):
        raise ValueError("The runtime is outside the owned task scope.")
    uuid.UUID(runtime.name[len(prefix):])
    if runtime.is_symlink() or runtime.stat().st_uid != os.geteuid():
        raise ValueError("The runtime is not owned by the current user.")
    state = json.loads((runtime / "state.json").read_text())
    if state.get("status") != "ready" or state.get("mode") != "server":
        raise ValueError("The owned server must be ready before media staging.")
    destination = runtime / "complex-subtitle-media" / manifest_path.parent.name
    if destination.exists():
        raise ValueError("This generation was already staged; existing media will not be overwritten.")
    source_root = manifest_path.parent / "media"
    for case in manifest["Cases"]:
        source = (manifest_path.parent / case["MediaFileName"].replace("\\", "/")).resolve()
        if not source.is_relative_to(source_root) or sha256(source) != case["MediaSha256"]:
            raise ValueError("A generated media identity changed before staging.")
    destination.parent.mkdir(exist_ok=True, mode=0o700)
    shutil.copytree(source_root, destination)
    for case in manifest["Cases"]:
        relative = Path(case["MediaFileName"].replace("\\", "/")).relative_to("media")
        if sha256(destination / relative) != case["MediaSha256"]:
            raise ValueError("The staged media bytes differ from the generated fixture.")
    receipt = {"RuntimeDirectory": str(runtime), "MediaDirectory": str(destination), "ManifestSha256": sha256(manifest_path), "CaseCount": len(manifest["Cases"])}
    (manifest_path.parent / "staging-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt))


if __name__ == "__main__":
    main()
