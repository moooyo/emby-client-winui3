"""Bounded, byte-transparent access to one validated private container endpoint."""

from __future__ import annotations

import ipaddress
import select
import socket
import threading
import time


class RelayError(Exception):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


class LoopbackRelay:
    MAX_CLIENTS = 8
    COPY_BYTES = 64 * 1024
    IDLE_SECONDS = 60
    DRAIN_SECONDS = 5

    def __init__(self, target_ipv4: str, target_port: int = 8096, *, listen_port: int = 19096):
        if not isinstance(target_ipv4, str):
            raise RelayError("InvalidRelayTarget")
        try:
            address = ipaddress.IPv4Address(target_ipv4)
        except ipaddress.AddressValueError:
            raise RelayError("InvalidRelayTarget") from None
        if not address.is_private or address.is_unspecified or address.is_multicast:
            raise RelayError("InvalidRelayTarget")
        if (type(target_port) is not int or not 1 <= target_port <= 65535
                or type(listen_port) is not int or not 0 <= listen_port <= 65535):
            raise RelayError("InvalidRelayPort")
        self._target = (str(address), target_port)
        self._listen_port = listen_port
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._slots = threading.BoundedSemaphore(self.MAX_CLIENTS)
        self._listener = None
        self._accept_thread = None
        self._workers: set[threading.Thread] = set()
        self._sockets: set[socket.socket] = set()
        self.port = None

    @property
    def is_running(self) -> bool:
        return self._accept_thread is not None and self._accept_thread.is_alive() and not self._stop.is_set()

    def start(self):
        if self._stop.is_set() or self._accept_thread is not None:
            raise RelayError("RelayAlreadyStartedOrClosed")
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(("127.0.0.1", self._listen_port))
            listener.listen(self.MAX_CLIENTS)
            listener.settimeout(0.2)
            self.port = listener.getsockname()[1]
            self._listener = listener
            self._accept_thread = threading.Thread(target=self._accept, name="protocol-relay-accept", daemon=True)
            self._accept_thread.start()
        except Exception:
            listener.close()
            self._stop.set()
            raise RelayError("RelayStartFailed") from None
        return self

    def _accept(self) -> None:
        try:
            while not self._stop.is_set():
                if not self._slots.acquire(timeout=0.2):
                    continue
                client = None
                worker = None
                try:
                    client, remote = self._listener.accept()
                    if self._stop.is_set() or not ipaddress.IPv4Address(remote[0]).is_loopback:
                        client.close()
                        self._slots.release()
                        continue
                    worker = threading.Thread(target=self._serve, args=(client,), name="protocol-relay-client", daemon=True)
                    with self._lock:
                        self._sockets.add(client)
                        # Keep a joinable reference until a thread has actually exited, including
                        # its final permit return. Reap completed clients at the next admission.
                        self._workers = {previous for previous in self._workers if previous.is_alive()}
                        self._workers.add(worker)
                    worker.start()
                except socket.timeout:
                    self._slots.release()
                except Exception:
                    if client is not None:
                        client.close()
                    with self._lock:
                        self._sockets.discard(client)
                        self._workers.discard(worker)
                    self._slots.release()
                    if not self._stop.is_set():
                        self._stop.set()
                    break
        finally:
            self._listener.close()

    def _serve(self, client: socket.socket) -> None:
        upstream = None
        try:
            upstream = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            # Register the socket before connect so shutdown can interrupt that operation too.
            with self._lock:
                self._sockets.add(upstream)
            if self._stop.is_set():
                return
            upstream.settimeout(2)
            upstream.connect(self._target)
            client.settimeout(10)
            upstream.settimeout(10)
            peers = {client: upstream, upstream: client}
            readers = [client, upstream]
            last_activity = time.monotonic()
            while readers and not self._stop.is_set():
                if time.monotonic() - last_activity >= self.IDLE_SECONDS:
                    return
                ready, _, _ = select.select(readers, [], [], 0.2)
                for source in ready:
                    data = source.recv(self.COPY_BYTES)
                    destination = peers[source]
                    if not data:
                        readers.remove(source)
                        # A client may finish sending its request and still need the full response.
                        try:
                            destination.shutdown(socket.SHUT_WR)
                        except OSError:
                            pass
                        continue
                    destination.sendall(data)
                    last_activity = time.monotonic()
        except (OSError, ValueError):
            # The HTTP caller observes connection failure. Never log transferred bytes or socket errors.
            pass
        finally:
            client.close()
            if upstream is not None:
                upstream.close()
            with self._lock:
                self._sockets.discard(client)
                self._sockets.discard(upstream)
            self._slots.release()

    @staticmethod
    def _interrupt(connection: socket.socket) -> None:
        try:
            connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        connection.close()

    def close(self) -> None:
        self._stop.set()
        if self._listener is not None:
            self._interrupt(self._listener)
        deadline = time.monotonic() + self.DRAIN_SECONDS
        # Join admission first, so the worker snapshot cannot miss a newly accepted client.
        if self._accept_thread is not None and self._accept_thread.ident is not None:
            self._accept_thread.join(max(0, deadline - time.monotonic()))
        with self._lock:
            connections = list(self._sockets)
            workers = list(self._workers)
        for connection in connections:
            self._interrupt(connection)
        for worker in workers:
            if worker.ident is not None:
                worker.join(max(0, deadline - time.monotonic()))
        if (self._accept_thread is not None and self._accept_thread.is_alive()) or any(worker.is_alive() for worker in workers):
            raise RelayError("RelayDrainTimeout")
        with self._lock:
            self._workers.clear()
