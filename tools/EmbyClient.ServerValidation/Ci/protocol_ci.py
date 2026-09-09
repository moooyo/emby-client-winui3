"""Own one disposable Docker server and export only a reconstructed protocol summary."""

from __future__ import annotations

import argparse
import errno
import hashlib
import http.client
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET


SERVER_VERSION = "4.9.5.0"
IMAGE = "emby/embyserver@sha256:734a6f03c7c783a9e566b08d09a2b6376f41229ff29f032a7e00302e0be98f8a"
DOCKER_HOST = "unix:///var/run/docker.sock"
OWNER_LABEL = "org.embyclient.protocol-ci.owner"
SERVER_URL = "http://127.0.0.1:19096"
MEDIA_PATH = "/mnt/protocol-media"
SUMMARY_RELATIVE = Path("artifacts/emby-protocol-ci-summary/summary.json")
MAX_JSON_BYTES = 2 * 1024 * 1024
MAX_COMMAND_BYTES = 8 * 1024 * 1024
MAX_STARTUP_LOG_BYTES = 64 * 1024
STARTUP_ERROR_CATEGORIES = frozenset(("HttpResponse", "Redirect", "ConnectionRefused", "ConnectionReset",
    "ConnectionAborted", "HostUnreachable", "NetworkUnreachable", "TimedOut", "AccessDenied",
    "PeerClosed", "MalformedHttp", "OtherOsError", "OtherRequestFailure", "InvalidJson"))
EXPECTED_STEPS = frozenset("""
Server.PublicInfo Authentication.ValidCredentials Authentication.WrongPassword User.Current
Server.AuthenticatedInfo Session.Capabilities Library.Views Items.List Items.Latest Items.Resume
Items.NextUp Items.Details Items.Search Items.EmptySearch Items.Paging UserData.FavoriteToggle
UserData.FavoriteRestore UserData.PlayedToggle UserData.PlayedRestore PlaybackInfo.Original
Media.OriginalRange PlaybackInfo.ForcedHls Media.HlsManifest Media.HlsSegment
Subtitles.ExternalNegotiation Media.WebVttSubtitle PlaybackReport.Start Session.StartReadback
PlaybackReport.Progress Session.ProgressReadback PlaybackReport.Stop Session.StopReadback
Cleanup.OriginalEncoding Cleanup.HlsEncoding Cleanup.SubtitleEncoding Cleanup.FavoriteRestore
Cleanup.PlayedRestore Cleanup.UserDataBooleans Session.Logout Authentication.LoggedOutTokenRejected
""".split())


class Failure(Exception):
    """Only fixed orchestration codes may reach the console or summary."""

    def __init__(self, code: str, *, diagnostic: dict | None = None):
        super().__init__(code)
        self.code = code
        self.diagnostic = diagnostic


def summarize_startup_request_error(error: BaseException) -> dict:
    """Keep only fixed request categories and numeric HTTP/OS codes, never exception text."""
    if isinstance(error, urllib.error.HTTPError):
        result = {"Category": "HttpResponse"}
        if type(error.code) is int and 100 <= error.code <= 599:
            result["HttpStatus"] = error.code
        return result
    if isinstance(error, urllib.error.URLError):
        error = error.reason if isinstance(error.reason, BaseException) else error
    number = getattr(error, "errno", None)
    result = {}
    if type(number) is int and 0 <= number <= 65535:
        result["Errno"] = number
    category = {
        errno.ECONNREFUSED: "ConnectionRefused", errno.ECONNRESET: "ConnectionReset",
        errno.ECONNABORTED: "ConnectionAborted", errno.EHOSTUNREACH: "HostUnreachable",
        errno.ENETUNREACH: "NetworkUnreachable", errno.ETIMEDOUT: "TimedOut",
        errno.EACCES: "AccessDenied", errno.EPERM: "AccessDenied",
    }.get(number)
    if category is None:
        if isinstance(error, TimeoutError):
            category = "TimedOut"
        elif isinstance(error, http.client.RemoteDisconnected):
            category = "PeerClosed"
        elif isinstance(error, http.client.HTTPException):
            category = "MalformedHttp"
        else:
            category = "OtherOsError" if isinstance(error, OSError) else "OtherRequestFailure"
    result["Category"] = category
    return result


def summarize_startup_container(container: dict) -> dict:
    """Reconstruct container state and actual port-programming facts without Docker's error text."""
    state = container.get("State", {})
    if not isinstance(state, dict):
        raise Failure("InvalidContainerState")
    result = {name: state.get(name) if type(state.get(name)) is bool else None
        for name in ("Running", "Paused", "Restarting", "OOMKilled", "Dead")}
    status = state.get("Status")
    result["Status"] = status if status in ("created", "running", "paused", "restarting", "removing", "exited", "dead") else "unknown"
    result["ExitCode"] = state.get("ExitCode") if type(state.get("ExitCode")) is int and 0 <= state["ExitCode"] <= 255 else None
    result["StateErrorPresent"] = isinstance(state.get("Error"), str) and bool(state["Error"])
    restarts = container.get("RestartCount")
    result["RestartCount"] = restarts if type(restarts) is int and 0 <= restarts <= 10000 else None
    ports = container.get("NetworkSettings", {}).get("Ports")
    result["ActualPortsMetadataPresent"] = isinstance(ports, dict)
    result["ActualPublishedPortPresent"] = isinstance(ports, dict) and any(isinstance(value, list) and bool(value) for value in ports.values())
    result["ActualLoopbackPublication"] = isinstance(ports, dict) and ports.get("8096/tcp") == [{"HostIp": "127.0.0.1", "HostPort": "19096"}]
    return result


def summarize_startup_logs(data: bytes) -> dict:
    """Known log signatures are observations, not an attribution of the startup failure."""
    text = data[:MAX_STARTUP_LOG_BYTES].decode("utf-8", errors="replace")
    patterns = {
        "PermissionDenied": r"\bpermission denied\b",
        "OperationNotPermitted": r"\boperation not permitted\b",
        "ReadOnlyFileSystem": r"\bread-only file system\b",
        "AddressAlreadyInUse": r"\baddress already in use\b|\bEADDRINUSE\b",
        "NoSpaceLeft": r"\bno space left on device\b",
        "OutOfMemory": r"\bout of memory\b|\bcannot allocate memory\b",
        "MissingFileOrLibrary": r"\bno such file or directory\b|\berror while loading shared libraries\b",
        "S6Fatal": r"\bs6[-a-z0-9]*\b[^\r\n]{0,160}\bfatal\b",
        "UserOrGroupSwitchFailure": r"\b(?:setuid|setgid|setgroups|s6-setuidgid|s6-applyuidgid|su-exec)\b[^\r\n]{0,160}(?:failed|failure|permission|not permitted)",
        "LoggedHttpListenerPrefix": r"\bAdding HttpListener prefix\b",
    }
    exceptions = ("UnauthorizedAccessException", "IOException", "SocketException", "XmlException",
        "ConfigurationErrorsException", "FileNotFoundException", "DllNotFoundException",
        "TypeInitializationException", "BadImageFormatException", "OutOfMemoryException", "NotSupportedException")
    return {
        "Signals": {name: re.search(pattern, text, re.IGNORECASE) is not None for name, pattern in patterns.items()},
        "ExceptionTypes": {name: re.search(r"\b" + name + r"\b", text) is not None for name in exceptions},
        "BytesExamined": min(len(data), MAX_STARTUP_LOG_BYTES),
    }


def summarize_report(report, exit_code: int) -> tuple[dict, bool]:
    """Reconstruct fixed check results; never export raw evidence, names outside the plan, or errors."""
    if not isinstance(report, dict) or type(exit_code) is not int:
        raise Failure("InvalidProbeReport")
    steps = report.get("Steps")
    if not isinstance(steps, list) or len(steps) > 128 or any(not isinstance(step, dict) for step in steps):
        raise Failure("InvalidProbeReport")
    names = [step.get("Name") for step in steps]
    if any(not isinstance(name, str) for name in names):
        raise Failure("InvalidProbeReport")
    by_name = {step["Name"]: step for step in steps if step["Name"] in EXPECTED_STEPS}
    safe_steps = []
    for name in sorted(EXPECTED_STEPS):
        step = by_name.get(name, {})
        status = step.get("Status", "Blocked")
        if status not in ("Passed", "Failed", "Blocked"):
            status = "Failed"
        item = {"Name": name, "Status": status}
        http_status = step.get("HttpStatus")
        if type(http_status) is int and 100 <= http_status <= 599:
            item["HttpStatus"] = http_status
        safe_steps.append(item)
    summary = {
        "Checks": safe_steps,
        "PassedChecks": sum(item["Status"] == "Passed" for item in safe_steps),
        "ExpectedChecks": len(EXPECTED_STEPS),
        "ProbeExitCode": exit_code if 0 <= exit_code <= 255 else -1,
        "UnknownOrDuplicateStepCount": len(names) - len(set(names) & EXPECTED_STEPS),
    }
    passed = (exit_code == 0 and report.get("Outcome") == "Passed"
        and report.get("ExpectedServerVersion") == SERVER_VERSION and report.get("ObservedServerVersion") == SERVER_VERSION
        and report.get("NativeUi") == "NotRun" and report.get("NativeDecoderAndFirstFrame") == "NotRun"
        and len(names) == len(EXPECTED_STEPS) and set(names) == EXPECTED_STEPS
        and all(item["Status"] == "Passed" for item in safe_steps))
    return summary, passed


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        raise Failure("UnexpectedServerRedirect", diagnostic={"Category": "Redirect", "HttpStatus": code})


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(65536), b""):
            digest.update(block)
    return digest.hexdigest()


def read_json(path: Path, maximum: int = MAX_JSON_BYTES):
    if path.is_symlink() or path.stat().st_size > maximum:
        raise Failure("InvalidLocalJson")
    with path.open("rb") as source:
        return json.load(source)


def write_json(path: Path, value) -> None:
    data = (json.dumps(value, indent=2, ensure_ascii=True) + "\n").encode("utf-8")
    if len(data) > MAX_JSON_BYTES or path.is_symlink():
        raise Failure("InvalidLocalJson")
    temporary = path.with_name(path.name + ".new")
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(descriptor, "wb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


class Runner:
    def __init__(self):
        if (sys.platform != "linux" or os.environ.get("GITHUB_ACTIONS") != "true"
                or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted"
                or os.environ.get("RUNNER_OS") != "Linux" or os.getuid() == 0):
            raise Failure("DisposableLinuxHostedRunnerRequired")
        self.repository = Path(__file__).resolve().parents[3]
        if Path(os.environ["GITHUB_WORKSPACE"]).resolve() != self.repository:
            raise Failure("CheckoutPathMismatch")
        self.run_id = os.environ.get("GITHUB_RUN_ID", "")
        self.attempt = os.environ.get("GITHUB_RUN_ATTEMPT", "")
        if not self.run_id.isascii() or not self.run_id.isdigit() or not self.attempt.isascii() or not self.attempt.isdigit():
            raise Failure("InvalidWorkflowIdentity")
        self.temporary = Path(os.environ["RUNNER_TEMP"]).resolve(strict=True)
        self.work = self.temporary / f"emby-protocol-{self.run_id}-{self.attempt}"
        if self.work.is_symlink() or self.work.resolve().parent != self.temporary:
            raise Failure("InvalidPrivateDirectory")
        self.state_path = self.work / "owned-resources.json"
        self.summary_path = self.repository / SUMMARY_RELATIVE
        for ancestor in (self.summary_path.parent, self.summary_path.parent.parent):
            if ancestor.is_symlink():
                raise Failure("InvalidSummaryDirectory")
        self.summary_path.parent.mkdir(parents=True, exist_ok=True)
        self.environment = os.environ.copy()
        for name in ("DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH"):
            self.environment.pop(name, None)
        self.deadline = time.monotonic() + 18 * 60
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        self.state = None
        self.relay = None
        self.relay_target_ipv4 = None
        self.summary = {
            "SchemaVersion": 1,
            "Scope": "Official Emby API and authenticated HTTP media transfer only",
            "Outcome": "NotRun",
            "NativeUi": "NotRun",
            "NativeDecoderAndFirstFrame": "NotRun",
            "SyntheticPlaybackReports": True,
            "ServerVersion": SERVER_VERSION,
            "OfficialImage": IMAGE,
            "WorkflowRunId": self.run_id,
            "WorkflowRunAttempt": self.attempt,
            "CleanupCompleted": False,
            "LastStage": "NotStarted",
        }
        revision = os.environ.get("GITHUB_SHA", "")
        if not re.fullmatch(r"[0-9a-f]{40}", revision):
            raise Failure("InvalidSourceRevision")
        self.summary["SourceCommit"] = revision

    def save_summary(self) -> None:
        write_json(self.summary_path, self.summary)

    def stage(self, name: str) -> None:
        self.summary["LastStage"] = name
        self.save_summary()

    def command(self, arguments: list[str], timeout: int, code: str, *, cleanup: bool = False,
                accept_failure: bool = False, maximum_bytes: int = MAX_COMMAND_BYTES) -> subprocess.CompletedProcess:
        if not 0 < maximum_bytes <= MAX_COMMAND_BYTES:
            raise Failure("InvalidCommandOutputLimit")
        allowed = timeout if cleanup else min(timeout, self.deadline - time.monotonic())
        if allowed <= 0:
            raise Failure("OrchestrationDeadline")
        # Do not emit tool output: ffmpeg, Docker, and a failing CLI can contain local paths or raw errors.
        # The bounded temporary files are outside every artifact upload path.
        output_path = self.work / "command-output.bin"
        error_path = self.work / "command-error.bin"
        if not self.work.exists():
            raise Failure("PrivateDirectoryUnavailable")
        with output_path.open("w+b") as output, error_path.open("w+b") as error_output:
            os.chmod(output_path, 0o600)
            os.chmod(error_path, 0o600)
            try:
                process = subprocess.Popen(arguments, cwd=self.repository, env=self.environment,
                    stdin=subprocess.DEVNULL, stdout=output, stderr=error_output, start_new_session=True)
                expires = time.monotonic() + allowed
                try:
                    while process.poll() is None:
                        if time.monotonic() >= expires:
                            raise Failure(code)
                        if output_path.stat().st_size + error_path.stat().st_size > maximum_bytes:
                            raise Failure("CommandOutputLimit")
                        time.sleep(0.1)
                    return_code = process.returncode
                except BaseException:
                    if process.poll() is None:
                        os.killpg(process.pid, signal.SIGTERM)
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            os.killpg(process.pid, signal.SIGKILL)
                            process.wait(timeout=5)
                    raise
                output.seek(0)
                data = output.read(maximum_bytes + 1)
                if len(data) + error_path.stat().st_size > maximum_bytes:
                    raise Failure("CommandOutputLimit")
                error_output.seek(0)
                error_data = error_output.read(maximum_bytes - len(data))
            except Failure:
                raise
            except Exception:
                raise Failure(code) from None
        if return_code != 0 and not accept_failure:
            raise Failure(code)
        return subprocess.CompletedProcess(arguments, return_code, data, error_data)

    def docker(self, *arguments: str, timeout: int = 30, cleanup: bool = False,
               accept_failure: bool = False, maximum_bytes: int = MAX_COMMAND_BYTES) -> subprocess.CompletedProcess:
        return self.command(["docker", "--host", DOCKER_HOST, *arguments], timeout,
            "DockerOperationFailed", cleanup=cleanup, accept_failure=accept_failure, maximum_bytes=maximum_bytes)

    def docker_json(self, *arguments: str, cleanup: bool = False):
        data = self.docker(*arguments, cleanup=cleanup).stdout
        try:
            return json.loads(data)
        except Exception:
            raise Failure("InvalidDockerMetadata") from None

    def save_state(self) -> None:
        write_json(self.state_path, self.state)

    def owned_startup_container(self, *, cleanup: bool = False) -> dict:
        try:
            result = self.docker("container", "inspect", self.state["ContainerId"], timeout=10, cleanup=cleanup)
            container = json.loads(result.stdout)[0]
        except Exception:
            raise Failure("StartupContainerInspectionFailed") from None
        if (container.get("Id") != self.state["ContainerId"]
                or container.get("Name") != "/" + self.state["ContainerName"]
                or container.get("Image") != self.state["ImageId"]
                or container.get("Config", {}).get("Labels", {}).get(OWNER_LABEL) != self.state["Owner"]):
            raise Failure("StartupContainerIdentityMismatch")
        return container

    def record_startup_request_failure(self, failure: Failure) -> None:
        readiness = self.summary["ServerReadiness"]
        diagnostic = failure.diagnostic if isinstance(failure.diagnostic, dict) else {}
        category = diagnostic.get("Category")
        category = category if category in STARTUP_ERROR_CATEGORIES else "OtherRequestFailure"
        safe = {"Category": category}
        for name, minimum, maximum in (("HttpStatus", 100, 599), ("Errno", 0, 65535)):
            value = diagnostic.get(name)
            if type(value) is int and minimum <= value <= maximum:
                safe[name] = value
        readiness["LastRequestFailure"] = safe
        categories = readiness.setdefault("RequestFailureCounts", {})
        categories[category] = categories.get(category, 0) + 1
        if "HttpStatus" in safe:
            counts = readiness.setdefault("HttpStatusCounts", {})
            key = str(safe["HttpStatus"])
            counts[key] = counts.get(key, 0) + 1

    def capture_startup_logs(self) -> None:
        result = {"CaptureSucceeded": False, "Truncated": False}
        data = b""
        try:
            # Verify ownership again before reading any log. No raw Docker metadata/log text is exported.
            self.owned_startup_container(cleanup=True)
            response = self.docker("logs", "--tail", "200", self.state["ContainerId"], timeout=10,
                cleanup=True, accept_failure=True, maximum_bytes=MAX_STARTUP_LOG_BYTES)
            if response.returncode == 0:
                data = response.stdout + (response.stderr or b"")
                result["CaptureSucceeded"] = True
        except Failure as error:
            if error.code == "CommandOutputLimit":
                result["Truncated"] = True
                try:
                    for name in ("command-output.bin", "command-error.bin"):
                        with (self.work / name).open("rb") as source:
                            data += source.read(MAX_STARTUP_LOG_BYTES - len(data))
                except OSError:
                    data = b""
        except Exception:
            pass
        result.update(summarize_startup_logs(data))
        self.summary["StartupLogs"] = result

    def headers(self, role: str, token: str | None = None) -> dict[str, str]:
        headers = {"X-Emby-Authorization": 'MediaBrowser Client="Protocol CI", Device="Disposable CLI", '
            f'DeviceId="protocol-ci-{self.state["Owner"]}-{role}", Version="1.0.0"', "Connection": "close"}
        if token is not None:
            headers["X-Emby-Token"] = token
        return headers

    def api(self, method: str, route: str, *, body=None, token: str | None = None, role: str = "setup"):
        if not route or route.startswith("/") or "#" in route or "://" in route:
            raise Failure("InvalidSetupRoute")
        data = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
        headers = self.headers(role, token)
        if data is not None:
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(SERVER_URL + "/emby/" + route, data=data, method=method, headers=headers)
        timeout = min(10, self.deadline - time.monotonic())
        if timeout <= 0:
            raise Failure("OrchestrationDeadline")
        http_status = None
        try:
            with self.opener.open(request, timeout=timeout) as response:
                http_status = response.getcode()
                payload = response.read(MAX_JSON_BYTES + 1)
                if len(payload) > MAX_JSON_BYTES:
                    raise Failure("ServerJsonLimit")
                return json.loads(payload) if payload else None
        except urllib.error.HTTPError as error:
            diagnostic = summarize_startup_request_error(error)
            error.close()
            raise Failure("ServerHttpRejected", diagnostic=diagnostic) from None
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError, http.client.HTTPException) as error:
            raise Failure("ServerUnavailable", diagnostic=summarize_startup_request_error(error)) from None
        except (ValueError, UnicodeError):
            raise Failure("InvalidServerJson", diagnostic={"Category": "InvalidJson", "HttpStatus": http_status}) from None

    def check_server_identity(self, info) -> str:
        if (not isinstance(info, dict) or info.get("Version") != SERVER_VERSION
                or info.get("ServerName") != self.state["ServerName"]
                or not re.fullmatch(r"[0-9a-fA-F]{32}", info.get("Id", ""))):
            raise Failure("UnexpectedServerIdentity")
        identity = hashlib.sha256(info["Id"].encode("ascii")).hexdigest()
        if self.state.get("ServerIdentitySha256") not in (None, identity):
            raise Failure("ServerIdentityChanged")
        self.state["ServerIdentitySha256"] = identity
        self.save_state()
        self.summary["ServerIdentitySha256"] = identity
        self.summary["ServerNameMatchesOwnedConfiguration"] = True
        return identity

    def prepare(self) -> None:
        if self.work.exists():
            raise Failure("PrivateDirectoryAlreadyExists")
        self.work.mkdir(mode=0o700)
        owner = secrets.token_hex(16)
        self.state = {
            "Owner": owner, "WorkflowRunId": self.run_id, "WorkflowRunAttempt": self.attempt,
            "ContainerName": "emby-protocol-" + owner, "NetworkName": "emby-protocol-net-" + owner,
            "ServerName": "Protocol CI " + owner,
        }
        self.save_state()
        self.summary["Outcome"] = "Running"
        self.stage("Prerequisites")
        for tool in ("docker", "dotnet", "ffmpeg", "ffprobe"):
            if shutil.which(tool) is None:
                raise Failure("RequiredRunnerToolMissing")
        expected_sdk = read_json(self.repository / "global.json")["sdk"]["version"]
        actual_sdk = self.command(["dotnet", "--version"], 30, "SdkReadFailed").stdout.decode().strip()
        if actual_sdk != expected_sdk:
            raise Failure("SdkVersionMismatch")
        self.summary["DotNetSdk"] = expected_sdk
        engine = self.docker_json("version", "--format", "{{json .Server}}")
        version = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)(?:[-+][0-9A-Za-z.-]+)?", engine.get("Version", ""))
        if engine.get("Os") != "linux" or version is None or tuple(map(int, version.groups())) < (28, 0, 0):
            raise Failure("UnsupportedDockerEngine")
        self.summary["DockerEngineVersion"] = engine["Version"]
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as reservation:
            try:
                reservation.bind(("127.0.0.1", 19096))
            except OSError:
                raise Failure("LoopbackPortUnavailable") from None
        self.stage("OfficialImagePull")
        self.docker("pull", "--platform", "linux/amd64", IMAGE, timeout=240)
        image = self.docker_json("image", "inspect", IMAGE)[0]
        if (image.get("Architecture") != "amd64" or image.get("Os") != "linux"
                or IMAGE not in image.get("RepoDigests", [])
                or not re.fullmatch(r"sha256:[0-9a-f]{64}", image.get("Id", ""))):
            raise Failure("OfficialImageIdentityMismatch")
        self.state["ImageId"] = image["Id"]
        self.summary["ResolvedImageId"] = image["Id"]
        self.save_state()
        self.create_fixture()
        self.create_server()

    def create_fixture(self) -> None:
        self.stage("SyntheticFixture")
        media = self.work / "media"
        media.mkdir(mode=0o700)
        movie = media / "Protocol Fixture (2026).mp4"
        self.command(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "60",
            "-c:v", "libx264", "-preset", "ultrafast", "-profile:v", "baseline", "-level:v", "3.1",
            "-pix_fmt", "yuv420p", "-threads", "2", "-g", "72", "-keyint_min", "72", "-sc_threshold", "0",
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-disposition:a:0", "default",
            "-movflags", "+faststart", str(movie)], 120, "FixtureGenerationFailed")
        if movie.stat().st_size > 32 * 1024 * 1024:
            raise Failure("FixtureSizeLimit")
        metadata = json.loads(self.command(["ffprobe", "-v", "error", "-show_streams", "-show_format",
            "-of", "json", str(movie)], 30, "FixtureInspectionFailed").stdout)
        streams = metadata.get("streams", [])
        video = [item for item in streams if item.get("codec_type") == "video"]
        audio = [item for item in streams if item.get("codec_type") == "audio"]
        if (len(video) != 1 or len(audio) != 1 or video[0].get("codec_name") != "h264"
                or video[0].get("pix_fmt") != "yuv420p" or video[0].get("width") != 640
                or video[0].get("height") != 360 or audio[0].get("codec_name") != "aac"
                or audio[0].get("channels") != 2 or audio[0].get("sample_rate") != "48000"
                or not 59 <= float(metadata.get("format", {}).get("duration", "0")) <= 61):
            raise Failure("FixtureFormatMismatch")
        subtitle = media / "Protocol Fixture (2026).eng.srt"
        subtitle.write_text("1\n00:00:02,000 --> 00:00:08,000\nProtocol fixture one.\n\n"
            "2\n00:00:15,000 --> 00:00:22,000\nProtocol fixture two.\n\n"
            "3\n00:00:35,000 --> 00:00:42,000\nProtocol fixture three.\n", encoding="utf-8")
        version = self.command(["ffmpeg", "-version"], 30, "FixtureToolVersionFailed").stdout.decode().splitlines()[0]
        match = re.match(r"ffmpeg version ([0-9A-Za-z.+:~_-]+) ", version)
        if match is None:
            raise Failure("FixtureToolVersionFailed")
        self.summary["Fixture"] = {"Synthetic": True, "Video": "H264", "Audio": "AacStereo",
            "Subtitle": "GeneratedSrt", "Width": 640, "Height": 360, "DurationSeconds": 60,
            "MovieSha256": sha256(movie), "SubtitleSha256": sha256(subtitle), "FfmpegVersion": match[1]}

    def create_server(self) -> None:
        self.stage("OwnedServerCreation")
        config = self.work / "config"
        (config / "config").mkdir(mode=0o700, parents=True)
        root = ET.Element("ServerConfiguration")
        for name, value in {
            "ServerName": self.state["ServerName"], "IsStartupWizardCompleted": "false",
            "EnableAutoUpdate": "false", "EnableAutomaticRestart": "false", "EnableUPnP": "false",
            "EnableRemoteAccess": "false", "HttpServerPortNumber": "8096", "PublicPort": "19096",
        }.items():
            ET.SubElement(root, name).text = value
        ET.ElementTree(root).write(config / "config/system.xml", encoding="utf-8", xml_declaration=True)
        network_id = self.docker("network", "create", "--driver", "bridge", "--internal",
            "--label", OWNER_LABEL + "=" + self.state["Owner"], self.state["NetworkName"]).stdout.decode().strip()
        if not re.fullmatch(r"[0-9a-f]{64}", network_id):
            raise Failure("InvalidNetworkIdentity")
        self.state["NetworkId"] = network_id
        self.save_state()
        container_id = self.docker("create", "--name", self.state["ContainerName"], "--platform", "linux/amd64",
            "--label", OWNER_LABEL + "=" + self.state["Owner"], "--restart", "no",
            "--network", self.state["NetworkName"],
            "--env", "UID=" + str(os.getuid()), "--env", "GID=" + str(os.getgid()),
            "--env", "GIDLIST=" + str(os.getgid()), "--cpus", "2", "--memory", "2g", "--pids-limit", "512",
            "--security-opt", "no-new-privileges:true", "--log-opt", "max-size=5m", "--log-opt", "max-file=1",
            "--mount", f"type=bind,source={config},target=/config",
            "--mount", f"type=bind,source={self.work / 'media'},target={MEDIA_PATH},readonly", IMAGE).stdout.decode().strip()
        if not re.fullmatch(r"[0-9a-f]{64}", container_id):
            raise Failure("InvalidContainerIdentity")
        self.state["ContainerId"] = container_id
        self.save_state()
        self.inspect_isolation()
        self.stage("ServerReadiness")
        self.summary["ServerReadiness"] = {"Attempts": 0, "PublicInfoReceived": False,
            "ContainerInspectionSucceeded": False}
        expires = min(self.deadline, time.monotonic() + 180)
        try:
            self.docker("start", container_id)
            state = summarize_startup_container(self.owned_startup_container())
            self.summary["ServerReadiness"]["Container"] = state
            self.summary["ServerReadiness"]["ContainerInspectionSucceeded"] = True
            if state["Running"] is not True:
                raise Failure("ServerExitedBeforeReady")
            target = self.inspect_isolation()
            if target is None:
                raise Failure("ContainerEndpointUnavailable")
            # The Docker network stays internal and has no published ports. This owned host relay
            # forwards bytes unchanged to the container endpoint validated by both inspect views.
            from loopback_relay import LoopbackRelay, RelayError
            self.relay_target_ipv4 = target
            self.relay = LoopbackRelay(target, 8096, listen_port=19096)
            try:
                self.relay.start()
            except RelayError:
                raise Failure("LoopbackRelayStartFailed") from None
            self.summary["Isolation"]["HostRelayLoopbackOnly"] = self.relay.is_running and self.relay.port == 19096
            if not self.summary["Isolation"]["HostRelayLoopbackOnly"]:
                raise Failure("LoopbackRelayStartFailed")
            while time.monotonic() < expires:
                readiness = self.summary["ServerReadiness"]
                readiness["ContainerInspectionSucceeded"] = False
                state = summarize_startup_container(self.owned_startup_container())
                readiness["Container"] = state
                readiness["ContainerInspectionSucceeded"] = True
                if state["Running"] is not True:
                    raise Failure("ServerExitedBeforeReady")
                if not self.relay.is_running:
                    raise Failure("LoopbackRelayStopped")
                readiness["Attempts"] += 1
                try:
                    info = self.api("GET", "System/Info/Public")
                    readiness["PublicInfoReceived"] = True
                    self.check_server_identity(info)
                    self.save_summary()
                    return
                except Failure as error:
                    self.record_startup_request_failure(error)
                    if error.code not in ("ServerUnavailable", "ServerHttpRejected"):
                        raise
                    self.save_summary()
                    time.sleep(2)
            raise Failure("ServerStartupDeadline")
        except Failure:
            try:
                self.summary["ServerReadiness"]["Container"] = summarize_startup_container(self.owned_startup_container(cleanup=True))
                self.summary["ServerReadiness"]["ContainerInspectionSucceeded"] = True
            except Failure:
                self.summary["ServerReadiness"]["ContainerInspectionSucceeded"] = False
            self.capture_startup_logs()
            self.save_summary()
            raise

    def inspect_isolation(self) -> str | None:
        network = self.docker_json("network", "inspect", self.state["NetworkId"])[0]
        container = self.docker_json("container", "inspect", self.state["ContainerId"])[0]
        host = container.get("HostConfig", {})
        mounts = container.get("Mounts", [])
        expected_mounts = {"/config": (str(self.work / "config"), True), MEDIA_PATH: (str(self.work / "media"), False)}
        valid_mounts = len(mounts) == 2 and {mount.get("Destination") for mount in mounts} == set(expected_mounts) and all(mount.get("Type") == "bind"
            and (mount.get("Source"), mount.get("RW")) == expected_mounts.get(mount.get("Destination")) for mount in mounts)
        if (network.get("Name") != self.state["NetworkName"] or network.get("Id") != self.state["NetworkId"]
                or network.get("Driver") != "bridge" or network.get("Internal") is not True
                or network.get("EnableIPv6") is not False or network.get("Labels", {}).get(OWNER_LABEL) != self.state["Owner"]
                or container.get("Name") != "/" + self.state["ContainerName"] or container.get("Image") != self.state["ImageId"]
                or container.get("Config", {}).get("Labels", {}).get(OWNER_LABEL) != self.state["Owner"]
                or host.get("NetworkMode") != self.state["NetworkName"] or host.get("Privileged") is not False
                or host.get("PublishAllPorts") is not False or host.get("PidMode") not in (None, "")
                or host.get("Devices") or not valid_mounts
                or host.get("PortBindings") not in (None, {})
                or set(container.get("NetworkSettings", {}).get("Networks", {})) != {self.state["NetworkName"]}):
            raise Failure("IsolationInspectionFailed")
        actual = summarize_startup_container(container)
        if actual["ActualPublishedPortPresent"]:
            raise Failure("UnexpectedDockerPortPublication")
        self.summary["Isolation"] = {"InternalBridge": True, "DockerPortPublicationRequested": False,
            "DockerActualPublishedPortPresent": actual["ActualPublishedPortPresent"],
            "OnlyOwnedConfigAndReadOnlySyntheticMediaMounted": True, "Privileged": False, "HostNetwork": False,
            "HostRelayLoopbackOnly": self.relay is not None and self.relay.is_running and self.relay.port == 19096}
        if actual["Running"] is not True:
            return None
        try:
            endpoint = container["NetworkSettings"]["Networks"][self.state["NetworkName"]]
            entries = network["Containers"]
            recorded = entries[self.state["ContainerId"]]
            address = ipaddress.IPv4Address(endpoint["IPAddress"])
            interface = ipaddress.IPv4Interface(recorded["IPv4Address"])
            subnets = [ipaddress.IPv4Network(item["Subnet"]) for item in network["IPAM"]["Config"]]
            valid = (set(entries) == {self.state["ContainerId"]} and recorded["Name"] == self.state["ContainerName"]
                and endpoint["NetworkID"] == self.state["NetworkId"] and endpoint["EndpointID"] == recorded["EndpointID"]
                and len(subnets) == 1 and interface.network == subnets[0] and address == interface.ip
                and address in subnets[0] and address not in (subnets[0].network_address, subnets[0].broadcast_address)
                and address.is_private and not (address.is_loopback or address.is_link_local or address.is_unspecified or address.is_multicast))
        except (KeyError, TypeError, ValueError):
            raise Failure("ContainerEndpointMismatch") from None
        if not valid or self.relay_target_ipv4 not in (None, str(address)):
            raise Failure("ContainerEndpointMismatch")
        self.summary["Isolation"]["RelayTargetMatchesOwnedEndpoint"] = True
        return str(address)

    def initialize_and_wait_for_item(self) -> str:
        self.stage("FreshServerInitialization")
        admin_password, user_password = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
        self.api("POST", "Startup/Configuration", body={"UICulture": "en-US"})
        self.api("POST", "Startup/User", body={"Name": "protocol-admin", "Password": admin_password})
        self.api("POST", "Startup/RemoteAccess", body={"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False})
        self.api("POST", "Startup/Complete")
        authentication = self.api("POST", "Users/AuthenticateByName", body={"Username": "protocol-admin", "Pw": admin_password})
        admin_token = authentication["AccessToken"]
        try:
            configuration = self.api("GET", "System/Configuration", token=admin_token)
            configuration.update({"EnableUPnP": False, "EnableRemoteAccess": False, "EnableAutoUpdate": False})
            self.api("POST", "System/Configuration", body=configuration, token=admin_token)
            user = self.api("POST", "Users/New", body={"Name": "protocol-user"}, token=admin_token)
            user_id = user["Id"]
            if not re.fullmatch(r"[0-9a-fA-F]{32}", user_id):
                raise Failure("InvalidTemporaryUserIdentity")
            self.api("POST", f"Users/{user_id}/Password", body={"Id": user_id, "NewPw": user_password, "ResetPassword": False}, token=admin_token)
            options = {
                "PathInfos": [{"Path": MEDIA_PATH}], "ContentType": "movies", "EnableRealtimeMonitor": False,
                "EnableChapterImageExtraction": False, "ExtractChapterImagesDuringLibraryScan": False,
                "EnableMarkerDetectionDuringLibraryScan": False, "EnableInternetProviders": False,
                "DownloadImagesInAdvance": False, "SaveLocalMetadata": False, "MetadataSavers": [],
                "SubtitleDownloadLanguages": [], "TypeOptions": [{"Type": "Movie", "MetadataFetchers": [],
                    "MetadataFetcherOrder": [], "ImageFetchers": [], "ImageFetcherOrder": []}],
            }
            self.api("POST", "Library/VirtualFolders?name=Protocol%20Validation&collectionType=movies&refreshLibrary=true",
                body={"LibraryOptions": options}, token=admin_token)
        finally:
            self.api("POST", "Sessions/Logout", token=admin_token)
        authentication = self.api("POST", "Users/AuthenticateByName", role="scan",
            body={"Username": "protocol-user", "Pw": user_password})
        token = authentication["AccessToken"]
        try:
            self.stage("PlaybackAccountLibraryScan")
            user = self.api("GET", f"Users/{user_id}", token=token, role="scan")
            policy = user.get("Policy", {})
            if (user.get("Id") != user_id or policy.get("IsAdministrator") is not False
                    or policy.get("IsDisabled") is not False
                    or any(policy.get(flag) is not True for flag in ("EnableMediaPlayback", "EnablePlaybackRemuxing",
                        "EnableVideoPlaybackTranscoding", "EnableAudioPlaybackTranscoding"))):
                raise Failure("TemporaryPlaybackPolicyMismatch")
            expires = min(self.deadline, time.monotonic() + 180)
            item_id = None
            while time.monotonic() < expires:
                views = self.api("GET", f"Users/{user_id}/Views", token=token, role="scan")
                items = self.api("GET", f"Users/{user_id}/Items?Recursive=true&MediaTypes=Video&Limit=2&Fields=MediaSources,MediaStreams",
                    token=token, role="scan").get("Items", [])
                if len(items) > 1:
                    raise Failure("UnexpectedLibraryContents")
                if views.get("Items") and len(items) == 1:
                    candidate = items[0].get("Id", "")
                    if not re.fullmatch(r"[0-9A-Za-z_-]{1,128}", candidate):
                        raise Failure("InvalidFixtureItemIdentity")
                    detail = self.api("GET", f"Users/{user_id}/Items/{candidate}", token=token, role="scan")
                    sources = detail.get("MediaSources", [])
                    streams = sources[0].get("MediaStreams", []) if len(sources) == 1 else []
                    state = detail.get("UserData", {})
                    if (detail.get("RunTimeTicks", 0) > 0 and type(state.get("IsFavorite")) is bool
                            and type(state.get("Played")) is bool and any(item.get("Type") == "Video" for item in streams)
                            and any(item.get("Type") == "Audio" for item in streams)
                            and any(item.get("Type") == "Subtitle" and item.get("Codec", "").lower() in ("srt", "subrip", "webvtt", "vtt") for item in streams)):
                        item_id = candidate
                        break
                time.sleep(2)
            if item_id is None:
                raise Failure("LibraryScanDeadline")
            write_json(self.work / "credentials.json", {"ServerUrl": SERVER_URL, "Username": "protocol-user", "Password": user_password})
            self.summary["TemporaryPlaybackUserIsAdministrator"] = False
            self.summary["ScannedFixtureHasVideoAudioAndTextSubtitle"] = True
            return item_id
        finally:
            self.api("POST", "Sessions/Logout", token=token, role="scan")

    def run_probe(self, probe: Path, item_id: str) -> None:
        self.stage("ProtocolProbe")
        probe = probe.resolve(strict=True)
        if probe.parent.parent != self.temporary or probe.parent.name != "emby-protocol-probe" or probe.name != "ApiProbe.Ci.dll":
            raise Failure("InvalidProbePath")
        self.summary["ProbeAssemblySha256"] = sha256(probe)
        self.summary["ProbeMode"] = "FrameworkDependentPortableCli"
        paths = list((self.repository / "tools/EmbyClient.ServerValidation/ApiProbe").glob("*.cs"))
        paths += [Path(__file__).resolve(), self.repository / "tools/EmbyClient.ServerValidation/Ci/ApiProbe.Ci.csproj",
            self.repository / "tools/EmbyClient.ServerValidation/Ci/packages.lock.json",
            self.repository / "tools/EmbyClient.ServerValidation/Ci/loopback_relay.py", self.repository / "global.json"]
        self.summary["SourceSha256"] = {path.relative_to(self.repository).as_posix(): sha256(path) for path in sorted(paths)}
        self.check_server_identity(self.api("GET", "System/Info/Public"))
        self.inspect_isolation()
        report_path = self.work / "api-probe-report.json"
        result = self.command(["dotnet", str(probe), "--credentials-file", str(self.work / "credentials.json"),
            "--output", str(report_path), "--item-id", item_id, "--expected-version", SERVER_VERSION],
            660, "ProbeProcessDeadline", accept_failure=True)
        report = read_json(report_path, 256 * 1024)
        safe_summary, passed = summarize_report(report, result.returncode)
        self.summary.update(safe_summary)
        self.check_server_identity(self.api("GET", "System/Info/Public"))
        self.inspect_isolation()
        if not passed:
            raise Failure("ProtocolChecksFailed")
        self.summary["Outcome"] = "Passed"

    def find_resource(self, kind: str, name: str):
        # A failed daemon call is not equivalent to a missing resource.
        arguments = ("container", "ls", "--all", "--no-trunc", "--format", "{{json .}}") if kind == "container" else (
            "network", "ls", "--no-trunc", "--format", "{{json .}}")
        lines = self.docker(*arguments, cleanup=True).stdout.decode().splitlines()
        matches = [json.loads(line) for line in lines if line]
        field = "Names" if kind == "container" else "Name"
        matches = [item for item in matches if item.get(field) == name]
        if not matches:
            return None
        if len(matches) != 1:
            raise Failure("CleanupIdentityMismatch")
        return self.docker_json(kind, "inspect", name, cleanup=True)[0]

    def cleanup(self) -> bool:
        relay_complete = True
        relay = getattr(self, "relay", None)
        if relay is not None:
            try:
                relay.close()
                self.relay = None
            except Exception:
                relay_complete = False
        self.summary["RelayCleanupCompleted"] = relay_complete
        if not self.work.exists():
            self.summary["CleanupCompleted"] = relay_complete
            return relay_complete
        if self.work.is_symlink() or self.work.resolve().parent != self.temporary:
            raise Failure("CleanupDirectoryMismatch")
        state = read_json(self.state_path, 8192)
        owner = state.get("Owner", "")
        if (not re.fullmatch(r"[0-9a-f]{32}", owner) or state.get("WorkflowRunId") != self.run_id
                or state.get("WorkflowRunAttempt") != self.attempt or state.get("ContainerName") != "emby-protocol-" + owner
                or state.get("NetworkName") != "emby-protocol-net-" + owner):
            raise Failure("CleanupIdentityMismatch")
        complete = True
        for kind, name_key, id_key in (("container", "ContainerName", "ContainerId"), ("network", "NetworkName", "NetworkId")):
            try:
                resource = self.find_resource(kind, state[name_key])
                if resource is not None:
                    labels = resource.get("Config", {}).get("Labels", {}) if kind == "container" else resource.get("Labels", {})
                    if labels.get(OWNER_LABEL) != owner or state.get(id_key) not in (None, resource.get("Id")):
                        raise Failure("CleanupIdentityMismatch")
                    if kind == "container":
                        try:
                            self.docker("stop", "--time", "10", resource["Id"], timeout=20, cleanup=True, accept_failure=True)
                        except Failure:
                            pass
                        self.docker("rm", "--force", "--volumes", resource["Id"], timeout=20, cleanup=True)
                    else:
                        self.docker("network", "rm", resource["Id"], timeout=20, cleanup=True)
                if self.find_resource(kind, state[name_key]) is not None:
                    raise Failure("CleanupIncomplete")
            except Exception:
                complete = False
        if complete and relay_complete:
            shutil.rmtree(self.work)
        self.summary["CleanupCompleted"] = relay_complete and complete and not self.work.exists()
        return self.summary["CleanupCompleted"]


def interrupt(signum, frame):
    raise Failure("WorkflowInterrupted")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("run", "cleanup"))
    parser.add_argument("--probe", type=Path)
    arguments = parser.parse_args()
    signal.signal(signal.SIGINT, interrupt)
    signal.signal(signal.SIGTERM, interrupt)
    runner = None
    failure = None
    try:
        runner = Runner()
        if arguments.action == "cleanup":
            if runner.summary_path.exists():
                previous = read_json(runner.summary_path, 256 * 1024)
                # The only producer is this script; reject an unrelated run before retaining its summary.
                if previous.get("WorkflowRunId") != runner.run_id or previous.get("WorkflowRunAttempt") != runner.attempt:
                    raise Failure("SummaryIdentityMismatch")
                runner.summary = previous
            if not runner.cleanup():
                raise Failure("CleanupIncomplete")
        else:
            if arguments.probe is None:
                raise Failure("ProbePathRequired")
            runner.prepare()
            item_id = runner.initialize_and_wait_for_item()
            runner.run_probe(arguments.probe, item_id)
    except Failure as error:
        failure = error.code
    except Exception:
        failure = "UnexpectedOrchestrationFailure"
    finally:
        if runner is not None:
            # Cleanup is retried by the workflow's always() step if this process was interrupted.
            signal.signal(signal.SIGINT, signal.SIG_IGN)
            signal.signal(signal.SIGTERM, signal.SIG_IGN)
            try:
                if not runner.cleanup():
                    failure = failure or "CleanupIncomplete"
            except Exception:
                failure = failure or "CleanupIncomplete"
                runner.summary["CleanupCompleted"] = False
            if failure is not None:
                runner.summary["Outcome"] = "Failed"
                runner.summary["FailureCode"] = failure
            try:
                runner.save_summary()
            except Exception:
                failure = failure or "SummaryWriteFailed"
    print("PROTOCOL_CI " + (failure or "Completed") + "; native UI and decoder: NOT RUN")
    return 1 if failure else 0


if __name__ == "__main__":
    sys.exit(main())
