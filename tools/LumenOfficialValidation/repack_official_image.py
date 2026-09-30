"""Convert verified official OCI blobs into a classic Docker archive without changing layers."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile

from import_official_image import PIN


TAG = "emby/embyserver:4.9.5.0"


def checked_blob(root: Path, descriptor: dict) -> Path:
    digest = descriptor["digest"]
    path = root / "blobs/sha256" / digest.removeprefix("sha256:")
    checksum = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            checksum.update(chunk)
    if "sha256:" + checksum.hexdigest() != digest:
        raise ValueError("VerifiedOfficialBlobRequired")
    return path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()
    root = args.directory.absolute()
    try:
        if root.is_symlink() or root.resolve().parent != Path("/tmp") or not root.name.startswith("lumen-official-image-"):
            raise ValueError("OwnedImportDirectoryRequired")
        index_path = checked_blob(root, {"digest": PIN})
        index = json.loads(index_path.read_bytes())
        platforms = [item for item in index["manifests"] if item.get("platform", {}).get("os") == "linux"
            and item.get("platform", {}).get("architecture") == "amd64"]
        if len(platforms) != 1:
            raise ValueError("SingleOfficialAmd64ManifestRequired")
        descriptor = platforms[0]
        manifest = json.loads(checked_blob(root, descriptor).read_bytes())
        config_path = checked_blob(root, manifest["config"])
        config = json.loads(config_path.read_bytes())
        expected_diff_ids = config["rootfs"]["diff_ids"]
        if len(expected_diff_ids) != len(manifest["layers"]):
            raise ValueError("OfficialLayerCountMismatch")
        layer_paths = []
        total_size = 0
        for index, layer in enumerate(manifest["layers"]):
            compressed = checked_blob(root, layer)
            expanded = root / (layer["digest"][7:] + ".layer.tar")
            checksum = hashlib.sha256()
            with gzip.open(compressed, "rb") as source, expanded.open("wb") as output:
                for chunk in iter(lambda: source.read(1024 * 1024), b""):
                    total_size += len(chunk)
                    if total_size > 2 * 1024 * 1024 * 1024:
                        raise ValueError("OfficialExpandedImageLimit")
                    checksum.update(chunk)
                    output.write(chunk)
            if "sha256:" + checksum.hexdigest() != expected_diff_ids[index]:
                raise ValueError("OfficialUncompressedLayerMismatch")
            layer_paths.append(expanded)
        archive = root / "official-emby-amd64.docker.tar"
        config_name = manifest["config"]["digest"][7:] + ".json"
        archive_manifest = [{"Config": config_name, "RepoTags": [TAG],
            "Layers": [path.name for path in layer_paths]}]
        with tarfile.open(archive, "w") as output:
            output.add(config_path, arcname=config_name)
            for path in layer_paths:
                output.add(path, arcname=path.name)
            payload = json.dumps(archive_manifest).encode("ascii")
            info = tarfile.TarInfo("manifest.json")
            info.size = len(payload)
            output.addfile(info, io.BytesIO(payload))
        result = subprocess.run(["docker", "--host", "unix:///var/run/docker.sock", "load", "--input", str(archive)],
            capture_output=True, timeout=240, check=False)
        (root / "docker-load-output.bin").write_bytes(result.stdout + result.stderr)
        receipt = json.loads((root / "import-receipt.json").read_bytes())
        receipt.update({"Outcome": "Imported" if result.returncode == 0 else "DockerLoadFailed", "DockerLoadExitCode": result.returncode,
            "VerifiedTag": TAG, "UncompressedLayerDigestsVerified": True, "ExpandedImageBytes": total_size,
            "ArchiveFormat": "Docker", "ConfigDigest": manifest["config"]["digest"]})
        (root / "import-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="ascii")
        print(json.dumps(receipt))
        return 0 if result.returncode == 0 else 1
    except Exception as error:
        print(json.dumps({"Outcome": "Failed", "FailureCode": type(error).__name__, "Directory": str(root)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
