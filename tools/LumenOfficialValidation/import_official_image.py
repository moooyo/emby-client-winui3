"""Import the pinned official OCI image with request-scoped DNS, without host changes."""

from __future__ import annotations

import argparse
import hashlib
import http.client
import ipaddress
import json
import os
from pathlib import Path
import socket
import ssl
import subprocess
import tarfile
import time
import urllib.parse
import urllib.request


PIN = "sha256:734a6f03c7c783a9e566b08d09a2b6376f41229ff29f032a7e00302e0be98f8a"
SOURCE = "https://registry-1.docker.io/v2/emby/embyserver/"
REFERENCE = "docker.io/emby/embyserver@" + PIN
MEDIA_TYPES = ",".join(("application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json"))
DEADLINE = time.monotonic() + 15 * 60
DNS_CACHE = {}
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


class ImportFailure(Exception):
    pass


def resolve(host: str) -> list[str]:
    if host not in DNS_CACHE:
        request = urllib.request.Request("https://dns.google/resolve?" + urllib.parse.urlencode({"name": host, "type": "A"}))
        with OPENER.open(request, timeout=20) as response:
            result = json.load(response)
        addresses = [item["data"] for item in result.get("Answer", []) if item.get("type") == 1]
        if not addresses or any(not ipaddress.IPv4Address(value).is_global for value in addresses):
            raise ImportFailure("PublicDnsAnswerRequired")
        DNS_CACHE[host] = addresses
    return DNS_CACHE[host]


class ScopedConnection(http.client.HTTPSConnection):
    def connect(self):
        last = None
        for address in resolve(self.host):
            try:
                raw = socket.create_connection((address, self.port), timeout=self.timeout)
                self.sock = self._context.wrap_socket(raw, server_hostname=self.host)
                return
            except (OSError, ssl.SSLError) as error:
                last = error
        raise ImportFailure("ValidatedTlsConnectionFailed") from last


def request(url: str, *, authorization=None, accept=MEDIA_TYPES):
    for _ in range(6):
        if time.monotonic() >= DEADLINE:
            raise ImportFailure("ImportDeadline")
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme != "https" or parsed.port not in (None, 443) or parsed.username or parsed.password:
            raise ImportFailure("HttpsPublicRegistryRequestRequired")
        headers = {"Accept": accept, "User-Agent": "LumenOwnedOfficialImporter/1.0"}
        if authorization is not None and parsed.hostname == "registry-1.docker.io":
            headers["Authorization"] = "Bearer " + authorization
        connection = ScopedConnection(parsed.hostname, timeout=30, context=ssl.create_default_context())
        connection.request("GET", urllib.parse.urlunsplit(("", "", parsed.path, parsed.query, "")), headers=headers)
        response = connection.getresponse()
        if response.status in (301, 302, 303, 307, 308):
            location = response.getheader("Location")
            response.close()
            connection.close()
            if not location:
                raise ImportFailure("RegistryRedirectWithoutLocation")
            url = urllib.parse.urljoin(url, location)
            continue
        if response.status != 200:
            response.close()
            connection.close()
            raise ImportFailure("RegistryHttp" + str(response.status))
        return connection, response
    raise ImportFailure("RegistryRedirectLimit")


def download(root: Path, route: str, digest: str, token: str) -> tuple[Path, int]:
    if not digest.startswith("sha256:") or len(digest) != 71:
        raise ImportFailure("Sha256DescriptorRequired")
    path = root / "blobs/sha256" / digest[7:]
    connection, response = request(SOURCE + route, authorization=token)
    checksum = hashlib.sha256()
    size = 0
    try:
        with path.open("xb") as output:
            os.chmod(path, 0o600)
            while True:
                chunk = response.read(1024 * 1024)
                if not chunk:
                    break
                size += len(chunk)
                if size > 512 * 1024 * 1024 or time.monotonic() >= DEADLINE:
                    raise ImportFailure("RegistryBlobLimit")
                checksum.update(chunk)
                output.write(chunk)
    finally:
        response.close()
        connection.close()
    if checksum.hexdigest() != digest[7:]:
        raise ImportFailure("OfficialRegistryDigestMismatch")
    return path, size


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()
    root = args.directory.absolute()
    try:
        if root.is_symlink() or root.resolve().parent != Path("/tmp") or not root.name.startswith("lumen-official-image-"):
            raise ImportFailure("OwnedImportDirectoryRequired")
        root.mkdir(mode=0o700)
        (root / "blobs/sha256").mkdir(parents=True, mode=0o700)
        connection, response = request("https://auth.docker.io/token?service=registry.docker.io&scope=repository:emby/embyserver:pull", accept="application/json")
        try:
            token = json.loads(response.read(128 * 1024))["token"]
        finally:
            response.close()
            connection.close()
        source_index, size = download(root, "manifests/" + PIN, PIN, token)
        index = json.loads(source_index.read_bytes())
        descriptors = [item for item in index["manifests"] if item.get("platform", {}).get("os") == "linux"
            and item.get("platform", {}).get("architecture") == "amd64"]
        if len(descriptors) != 1:
            raise ImportFailure("SingleAmd64ManifestRequired")
        descriptor = descriptors[0]
        manifest_path, manifest_size = download(root, "manifests/" + descriptor["digest"], descriptor["digest"], token)
        manifest = json.loads(manifest_path.read_bytes())
        config = manifest["config"]
        config_path, _ = download(root, "blobs/" + config["digest"], config["digest"], token)
        image_config = json.loads(config_path.read_bytes())
        if image_config.get("os") != "linux" or image_config.get("architecture") != "amd64":
            raise ImportFailure("OfficialImagePlatformMismatch")
        for layer in manifest["layers"]:
            download(root, "blobs/" + layer["digest"], layer["digest"], token)
        (root / "oci-layout").write_text(json.dumps({"imageLayoutVersion": "1.0.0"}), encoding="ascii")
        (root / "index.json").write_text(json.dumps({"schemaVersion": 2, "manifests": [{
            "mediaType": index["mediaType"], "digest": PIN, "size": size,
            "annotations": {"org.opencontainers.image.ref.name": REFERENCE}}]}), encoding="ascii")
        archive = root / "official-emby-amd64.oci.tar"
        with tarfile.open(archive, "w") as output:
            for name in ("oci-layout", "index.json", "blobs"):
                output.add(root / name, arcname=name)
        result = subprocess.run(["docker", "--host", "unix:///var/run/docker.sock", "load", "--platform", "linux/amd64", "--input", str(archive)],
            capture_output=True, timeout=180, check=False)
        (root / "docker-load-output.bin").write_bytes(result.stdout + result.stderr)
        receipt = {"SourceRegistry": "registry-1.docker.io", "OfficialPublisher": "emby/embyserver", "PinnedIndexDigest": PIN,
            "Amd64ManifestDigest": descriptor["digest"], "ConfigDigest": config["digest"], "LayerCount": len(manifest["layers"]),
            "TlsCertificateVerification": True, "SystemDnsOrDockerConfigurationChanged": False, "Directory": str(root),
            "Outcome": "Imported" if result.returncode == 0 else "DockerLoadFailed", "DockerLoadExitCode": result.returncode}
        (root / "import-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="ascii")
        print(json.dumps(receipt))
        return 0 if result.returncode == 0 else 1
    except Exception as error:
        code = str(error) if isinstance(error, ImportFailure) else type(error).__name__
        print(json.dumps({"Outcome": "Failed", "FailureCode": code, "Directory": str(root)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
