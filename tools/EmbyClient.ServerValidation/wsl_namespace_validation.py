#!/usr/bin/env python3
"""Run an isolated WSL validation service behind a loopback-only Unix bridge."""

from __future__ import annotations

import argparse
import ipaddress
import json
import os
from pathlib import Path
import select
import signal
import socket
import subprocess
import sys
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any


OUTER_HOST = "127.0.0.1"
OUTER_PORT = 19096
PROBE_PORT = 19097
PROBE_MARKER = "emby-client-wsl-namespace-probe"
STOP = threading.Event()


def request_stop(signum: int, frame: Any) -> None:
    """Allow both runner layers to clean up their own child processes."""
    STOP.set()


def write_json(path: Path, value: dict[str, Any]) -> None:
    temporary = path.with_name(path.name + f".{os.getpid()}.tmp")
    with temporary.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")
    os.chmod(temporary, 0o600)
    temporary.replace(path)


def prepare_runtime(value: str, inner: bool) -> Path:
    supplied = Path(value)
    prefix = "emby-client-validation-"
    if not supplied.is_absolute() or supplied.parent != Path("/tmp"):
        raise ValueError("The runtime directory must be an absolute direct child of /tmp.")
    if not supplied.name.startswith(prefix):
        raise ValueError("The runtime directory must use the task-specific validation prefix.")
    uuid.UUID(supplied.name[len(prefix):])
    if supplied.is_symlink():
        raise ValueError("The runtime directory must not be a symbolic link.")
    if not inner:
        supplied.mkdir(mode=0o700, exist_ok=True)
    if not supplied.is_dir() or supplied.resolve() != supplied:
        raise ValueError("The runtime directory must be a real directory.")
    if supplied.stat().st_uid != os.geteuid():
        raise ValueError("The runtime directory must be owned by the current user.")
    if not inner:
        supplied.chmod(0o700)
    if supplied.stat().st_mode & 0o077:
        raise ValueError("The runtime directory must be private to its owner.")
    return supplied


def namespace_id() -> str:
    return os.readlink("/proc/self/ns/net")


def ip_json(*arguments: str) -> Any:
    completed = subprocess.run(
        ["/usr/sbin/ip", "-j", *arguments],
        check=True,
        capture_output=True,
        text=True,
    )
    return json.loads(completed.stdout)


def isolated_environment() -> dict[str, str]:
    """Avoid inheriting host integration sockets or proxy configuration."""
    excluded = {
        "wsl_interop", "wslenv", "http_proxy", "https_proxy", "all_proxy",
        "no_proxy", "ftp_proxy", "display", "wayland_display", "pulse_server",
        "dbus_session_bus_address", "xdg_runtime_dir", "ssh_auth_sock",
    }
    environment = {key: value for key, value in os.environ.items() if key.lower() not in excluded}
    environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
    return environment


def isolated_network_evidence() -> dict[str, Any]:
    """Fail closed if the namespace contains a non-loopback interface or route."""
    interfaces = [name for _, name in socket.if_nameindex()]
    routes = Path("/proc/net/route").read_text(encoding="ascii").splitlines()
    ipv4_routes = ip_json("-4", "route", "show", "table", "main")
    ipv6_routes = ip_json("-6", "route", "show", "table", "main")
    all_ipv4_routes = ip_json("-4", "route", "show", "table", "all")
    all_ipv6_routes = ip_json("-6", "route", "show", "table", "all")
    if interfaces != ["lo"] or any(line.strip() for line in routes[1:]):
        raise RuntimeError("The isolated namespace must contain only lo and no IPv4 routes.")
    if ipv4_routes or ipv6_routes:
        raise RuntimeError("The isolated namespace must not contain main-table routes.")
    for route in all_ipv4_routes + all_ipv6_routes:
        destination = ipaddress.ip_network(route.get("dst", "default"), strict=False)
        if route.get("dev") != "lo" or not destination.is_loopback:
            raise RuntimeError("The isolated namespace contains a non-loopback route.")
    addresses = ip_json("address", "show")
    if len(addresses) != 1 or addresses[0].get("ifname") != "lo":
        raise RuntimeError("Unexpected interface addresses in the isolated namespace.")
    if "UP" not in addresses[0].get("flags", []):
        raise RuntimeError("The isolated loopback interface is not up.")
    return {
        "network_namespace": namespace_id(),
        "interfaces": interfaces,
        "addresses": addresses,
        "proc_net_route": routes,
        "ipv4_main_routes": ipv4_routes,
        "ipv6_main_routes": ipv6_routes,
        "ipv4_all_routes": all_ipv4_routes,
        "ipv6_all_routes": all_ipv6_routes,
    }


def tcp_listener_evidence(port: int) -> list[dict[str, Any]]:
    listeners: list[dict[str, Any]] = []
    for filename, family in (("/proc/net/tcp", "IPv4"), ("/proc/net/tcp6", "IPv6")):
        for line in Path(filename).read_text(encoding="ascii").splitlines()[1:]:
            fields = line.split()
            address_hex, port_hex = fields[1].split(":")
            if int(port_hex, 16) != port or fields[3] != "0A":
                continue
            if family == "IPv4":
                address = socket.inet_ntop(socket.AF_INET, bytes.fromhex(address_hex)[::-1])
            else:
                raw = bytes.fromhex(address_hex)
                address = socket.inet_ntop(
                    socket.AF_INET6,
                    b"".join(raw[index:index + 4][::-1] for index in range(0, 16, 4)),
                )
            listeners.append({"family": family, "address": address, "port": port, "inode": fields[9]})
    return listeners


class Relay:
    """Forward a listener to one fixed local destination without exposing routing."""

    def __init__(self, listener: socket.socket, target_family: int, target: Any) -> None:
        self.listener = listener
        self.target_family = target_family
        self.target = target
        self.finished = threading.Event()
        self.connections: set[socket.socket] = set()
        self.lock = threading.Lock()
        self.slots = threading.BoundedSemaphore(64)
        self.thread = threading.Thread(target=self.accept_connections, daemon=True)

    def start(self) -> None:
        self.listener.settimeout(0.25)
        self.thread.start()

    def accept_connections(self) -> None:
        while not STOP.is_set() and not self.finished.is_set():
            try:
                client, _ = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            if not self.slots.acquire(blocking=False):
                client.close()
                continue
            threading.Thread(target=self.forward, args=(client,), daemon=True).start()

    def forward(self, client: socket.socket) -> None:
        peer = socket.socket(self.target_family, socket.SOCK_STREAM)
        with self.lock:
            self.connections.update((client, peer))
        try:
            peer.settimeout(5)
            peer.connect(self.target)
            client.settimeout(5)
            pending = [client, peer]
            while pending and not STOP.is_set() and not self.finished.is_set():
                readable, _, _ = select.select(pending, [], [], 0.25)
                for source in readable:
                    destination = peer if source is client else client
                    data = source.recv(65536)
                    if data:
                        destination.sendall(data)
                    else:
                        pending.remove(source)
                        destination.shutdown(socket.SHUT_WR)
        except (OSError, ValueError):
            pass
        finally:
            with self.lock:
                self.connections.discard(client)
                self.connections.discard(peer)
            client.close()
            peer.close()
            self.slots.release()

    def close(self) -> None:
        self.finished.set()
        self.listener.close()
        with self.lock:
            for connection in self.connections:
                try:
                    connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                connection.close()
        self.thread.join(timeout=2)


class ProbeHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        body = json.dumps({
            "marker": PROBE_MARKER,
            "network_namespace": namespace_id(),
            "pid": os.getpid(),
        }).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *arguments: Any) -> None:
        return


def terminate_owned_group(process: subprocess.Popen[Any], timeout: float = 8) -> None:
    """Only signal the session created for this exact Popen child."""
    if process.poll() is not None:
        return
    try:
        if os.getpgid(process.pid) != process.pid:
            raise RuntimeError("Refusing to signal a process group that this runner did not create.")
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        process.wait(timeout=3)
        return
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=3)


def wait_for_tcp(port: int, process: subprocess.Popen[Any] | None, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while not STOP.is_set() and time.monotonic() < deadline:
        if process is not None and process.poll() is not None:
            raise RuntimeError("The isolated server exited before opening its HTTP port.")
        try:
            with socket.create_connection((OUTER_HOST, port), timeout=0.25):
                return
        except OSError:
            STOP.wait(0.1)
    raise RuntimeError("The isolated HTTP listener did not become ready.")


def run_inner(arguments: argparse.Namespace, runtime: Path) -> int:
    # This changes only the new network namespace created by the outer runner.
    if namespace_id() == arguments.outer_namespace:
        raise RuntimeError("The inner runner did not enter a separate network namespace.")
    subprocess.run(["/usr/sbin/ip", "link", "set", "lo", "up"], check=True)
    network = isolated_network_evidence()
    port = PROBE_PORT if arguments.mode == "probe" else arguments.server_port
    state: dict[str, Any] = {
        "status": "starting", "mode": arguments.mode, "pid": os.getpid(),
        "process_group": os.getpgrp(), "network": network,
        "http_target": {"address": OUTER_HOST, "port": port},
        "unix_socket": str(runtime / "bridge.sock"),
    }
    write_json(runtime / "inner-state.json", state)
    probe: ThreadingHTTPServer | None = None
    server: subprocess.Popen[Any] | None = None
    server_log = None
    relay: Relay | None = None
    unix_listener: socket.socket | None = None
    socket_bound = False
    try:
        if arguments.mode == "probe":
            probe = ThreadingHTTPServer((OUTER_HOST, port), ProbeHandler)
            probe.daemon_threads = True
            threading.Thread(target=probe.serve_forever, daemon=True).start()
        else:
            command = json.loads(arguments.server_command_json)
            if not isinstance(command, list) or not command or not all(isinstance(item, str) and item for item in command):
                raise ValueError("The server command must be a nonempty JSON array of nonempty strings.")
            server_log = (runtime / "server.log").open("ab", buffering=0)
            server = subprocess.Popen(
                command,
                cwd=arguments.server_root,
                stdin=subprocess.DEVNULL,
                stdout=server_log,
                stderr=subprocess.STDOUT,
                start_new_session=True,
                close_fds=True,
                env=isolated_environment(),
            )
            state["server_pid"] = server.pid
            state["server_executable"] = command[0]
            state["server_network_namespace"] = os.readlink(f"/proc/{server.pid}/ns/net")
            if state["server_network_namespace"] != network["network_namespace"]:
                raise RuntimeError("The server process did not inherit the isolated namespace.")
            write_json(runtime / "inner-state.json", state)
        wait_for_tcp(port, server, arguments.startup_timeout)
        network = isolated_network_evidence()
        unix_listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        unix_listener.bind(str(runtime / "bridge.sock"))
        socket_bound = True
        os.chmod(runtime / "bridge.sock", 0o600)
        unix_listener.listen(64)
        relay = Relay(unix_listener, socket.AF_INET, (OUTER_HOST, port))
        relay.start()
        state.update({"status": "ready", "network": network, "http_listeners": tcp_listener_evidence(port)})
        write_json(runtime / "inner-state.json", state)
        while not STOP.wait(0.25):
            if server is not None and server.poll() is not None:
                raise RuntimeError("The isolated server process exited.")
        return 0
    except Exception as error:
        state.update({"status": "failed", "error_type": type(error).__name__, "error": str(error)})
        write_json(runtime / "inner-state.json", state)
        raise
    finally:
        if relay is not None:
            relay.close()
        elif unix_listener is not None:
            unix_listener.close()
        if probe is not None:
            probe.shutdown()
            probe.server_close()
        if server is not None:
            terminate_owned_group(server, timeout=5)
        if server_log is not None:
            server_log.close()
        if socket_bound:
            (runtime / "bridge.sock").unlink(missing_ok=True)
        state["status"] = "failed" if state["status"] == "failed" else "stopped"
        state["stopped_at_unix"] = time.time()
        write_json(runtime / "inner-state.json", state)


def read_inner_state(runtime: Path) -> dict[str, Any] | None:
    try:
        return json.loads((runtime / "inner-state.json").read_text(encoding="utf-8"))
    except FileNotFoundError:
        return None


def run_outer(arguments: argparse.Namespace, runtime: Path) -> int:
    if (runtime / "bridge.sock").exists() or (runtime / "inner-state.json").exists():
        raise ValueError("Use a fresh runtime directory for each validation run.")
    outer_namespace = namespace_id()
    state: dict[str, Any] = {
        "status": "starting", "mode": arguments.mode, "runtime_dir": str(runtime),
        "outer": {"pid": os.getpid(), "process_group": os.getpgrp(), "network_namespace": outer_namespace},
        "started_at_unix": time.time(),
    }
    write_json(runtime / "state.json", state)
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    inner: subprocess.Popen[Any] | None = None
    inner_log = None
    relay: Relay | None = None
    try:
        # Bind before spawning a child so a port collision cannot leave an orphan.
        listener.bind((OUTER_HOST, OUTER_PORT))
        command = [
            "/usr/bin/unshare", "--user", "--map-root-user", "--net", "--",
            sys.executable, str(Path(__file__).resolve()),
            "--inner", "--mode", arguments.mode, "--runtime-dir", str(runtime),
            "--outer-namespace", outer_namespace,
            "--server-port", str(arguments.server_port),
            "--startup-timeout", str(arguments.startup_timeout),
        ]
        for option in ("server_root", "media_path", "server_command_json"):
            value = getattr(arguments, option)
            if value is not None:
                command.extend(("--" + option.replace("_", "-"), value))
        inner_log = (runtime / "inner.log").open("ab", buffering=0)
        inner = subprocess.Popen(
            command, stdin=subprocess.DEVNULL, stdout=inner_log,
            stderr=subprocess.STDOUT, start_new_session=True, close_fds=True,
            env=isolated_environment(),
        )
        state["inner_pid"] = inner.pid
        write_json(runtime / "state.json", state)
        deadline = time.monotonic() + arguments.startup_timeout + 5
        while not STOP.is_set() and time.monotonic() < deadline:
            inner_state = read_inner_state(runtime)
            if inner_state is not None and inner_state.get("status") == "ready":
                break
            if inner.poll() is not None:
                raise RuntimeError("The namespace child exited before becoming ready; inspect inner.log.")
            STOP.wait(0.1)
        else:
            raise RuntimeError("The namespace child did not become ready.")
        if inner_state["network"]["network_namespace"] == outer_namespace:
            raise RuntimeError("The inner and outer network namespaces must differ.")
        listener.listen(64)
        relay = Relay(listener, socket.AF_UNIX, str(runtime / "bridge.sock"))
        relay.start()
        state.update({"status": "ready", "inner": inner_state})
        state["outer"]["bound_endpoint"] = {"address": listener.getsockname()[0], "port": listener.getsockname()[1]}
        state["outer"]["http_listeners"] = tcp_listener_evidence(OUTER_PORT)
        if any(item["address"] != OUTER_HOST for item in state["outer"]["http_listeners"]):
            raise RuntimeError("The external bridge port has a non-loopback listener.")
        write_json(runtime / "state.json", state)
        print(json.dumps({"status": "ready", "mode": arguments.mode, "runtime_dir": str(runtime), "endpoint": f"http://{OUTER_HOST}:{OUTER_PORT}"}), flush=True)
        while not STOP.wait(0.25):
            if inner.poll() is not None:
                raise RuntimeError("The namespace child exited.")
        return 0
    except Exception as error:
        state.update({"status": "failed", "error_type": type(error).__name__, "error": str(error)})
        write_json(runtime / "state.json", state)
        raise
    finally:
        if relay is not None:
            relay.close()
        else:
            listener.close()
        if inner is not None:
            terminate_owned_group(inner)
            state["inner_exit_code"] = inner.returncode
        if inner_log is not None:
            inner_log.close()
        state["inner"] = read_inner_state(runtime)
        state["status"] = "failed" if state["status"] == "failed" else "stopped"
        state["stopped_at_unix"] = time.time()
        write_json(runtime / "state.json", state)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", required=True, choices=("probe", "server"))
    parser.add_argument("--runtime-dir", required=True)
    parser.add_argument("--server-root")
    parser.add_argument("--media-path")
    parser.add_argument("--server-command-json")
    parser.add_argument("--server-port", type=int, default=8096)
    parser.add_argument("--startup-timeout", type=float, default=180)
    parser.add_argument("--inner", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--outer-namespace", help=argparse.SUPPRESS)
    arguments = parser.parse_args()
    if sys.platform != "linux":
        parser.error("Run this script with Python inside the existing WSL Debian distribution.")
    if arguments.mode == "server" and not arguments.server_command_json:
        parser.error("Server mode requires --server-command-json.")
    if arguments.inner and not arguments.outer_namespace:
        parser.error("The inner runner requires the parent namespace identity.")
    if not 1 <= arguments.server_port <= 65535 or arguments.startup_timeout <= 0:
        parser.error("The server port and startup timeout must be positive and valid.")
    for option in ("server_root", "media_path"):
        value = getattr(arguments, option)
        if value is not None and not Path(value).is_absolute():
            parser.error(f"--{option.replace('_', '-')} must be an absolute Linux path.")
    return arguments


def main() -> int:
    arguments = parse_arguments()
    os.umask(0o077)
    for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(signum, request_stop)
    try:
        runtime = prepare_runtime(arguments.runtime_dir, arguments.inner)
        return run_inner(arguments, runtime) if arguments.inner else run_outer(arguments, runtime)
    except Exception as error:
        print(json.dumps({"status": "failed", "error_type": type(error).__name__, "error": str(error)}), file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
