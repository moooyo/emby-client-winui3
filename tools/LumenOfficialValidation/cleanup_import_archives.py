"""Remove only expanded archives belonging to the verified private official image import."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re

from import_official_image import PIN
from repack_official_image import checked_blob


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()
    root = args.directory.absolute()
    try:
        if (root.is_symlink() or root.resolve().parent != Path("/tmp") or root.stat().st_uid != os.getuid()
                or not re.fullmatch(r"lumen-official-image-[0-9a-f]{32}", root.name)):
            raise ValueError("OwnedImportDirectoryRequired")
        proof = json.loads((root / "import-receipt.json").read_bytes())
        if proof.get("Outcome") != "Imported" or proof.get("PinnedIndexDigest") != PIN or proof.get("UncompressedLayerDigestsVerified") is not True:
            raise ValueError("VerifiedOfficialImportReceiptRequired")
        index = json.loads(checked_blob(root, {"digest": PIN}).read_bytes())
        descriptors = [item for item in index["manifests"] if item.get("platform", {}).get("architecture") == "amd64"
            and item.get("platform", {}).get("os") == "linux"]
        if len(descriptors) != 1:
            raise ValueError("SingleOfficialAmd64ManifestRequired")
        manifest = json.loads(checked_blob(root, descriptors[0]).read_bytes())
        paths = [root / (item["digest"][7:] + ".layer.tar") for item in manifest["layers"]]
        paths += [root / "official-emby-amd64.oci.tar", root / "official-emby-amd64.docker.tar"]
        if any(path.is_symlink() or path.resolve().parent != root for path in paths):
            raise ValueError("OwnedArchivePathMismatch")
        removed = 0
        bytes_removed = 0
        for path in paths:
            if path.exists():
                bytes_removed += path.stat().st_size
                path.unlink()
                removed += 1
        print(json.dumps({"Outcome": "Cleaned", "RemovedArchiveCount": removed, "RemovedBytes": bytes_removed,
            "RetainedVerifiedSourceBlobsAndReceipt": True, "Directory": str(root)}))
        return 0
    except Exception as error:
        print(json.dumps({"Outcome": "Failed", "FailureCode": type(error).__name__, "Directory": str(root)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
