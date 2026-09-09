"""Real loopback TCP tests for the relay; no Docker, Emby, HTTP client, or player is used."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import socket
import threading
import time
import unittest
from unittest import mock


_SPEC = importlib.util.spec_from_file_location("loopback_relay_under_test", Path(__file__).with_name("loopback_relay.py"))
assert _SPEC is not None and _SPEC.loader is not None
relay_module = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(relay_module)
DEADLINE = 4.0


def receive_exact(connection: socket.socket, length: int) -> bytes:
    result = bytearray()
    while len(result) < length:
        block = connection.recv(min(65536, length - len(result)))
        if not block:
            raise AssertionError("The TCP stream ended before all expected bytes arrived.")
        result.extend(block)
    return bytes(result)


def receive_to_eof(connection: socket.socket) -> bytes:
    result = bytearray()
    while True:
        block = connection.recv(65536)
        if not block:
            return bytes(result)
        result.extend(block)
        if len(result) > 2 * 1024 * 1024:
            raise AssertionError("The TCP fixture exceeded its bounded response budget.")


class FakeTcpServer:
    """A random-port, thread-owned TCP fixture with explicit connection and shutdown barriers."""

    def __init__(self, handler):
        self.handler = handler
        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(16)
        self.listener.settimeout(0.1)
        self.port = self.listener.getsockname()[1]
        self.stop = threading.Event()
        self.changed = threading.Condition()
        self.clients = set()
        self.workers = []
        self.accepted = 0
        self.errors = []
        self.accept_thread = threading.Thread(target=self._accept, daemon=True, name="test-relay-upstream-accept")
        self.accept_thread.start()

    def _accept(self):
        while not self.stop.is_set():
            try:
                client, remote = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                if self.stop.is_set():
                    return
                raise
            client.settimeout(DEADLINE)
            worker = threading.Thread(target=self._serve, args=(client,), daemon=True, name="test-relay-upstream-client")
            with self.changed:
                self.clients.add(client)
                self.workers.append(worker)
                self.accepted += 1
                self.changed.notify_all()
            worker.start()

    def _serve(self, client):
        try:
            self.handler(client)
        except (ConnectionResetError, ConnectionAbortedError):
            pass
        except Exception as error:
            if not self.stop.is_set():
                with self.changed:
                    self.errors.append(error)
        finally:
            client.close()
            with self.changed:
                self.clients.discard(client)
                self.changed.notify_all()

    def wait_for_accepted(self, count: int, timeout: float = DEADLINE) -> bool:
        with self.changed:
            return self.changed.wait_for(lambda: self.accepted >= count, timeout)

    def wait_for_no_clients(self) -> bool:
        with self.changed:
            return self.changed.wait_for(lambda: not self.clients, DEADLINE)

    def close(self):
        self.stop.set()
        self.listener.close()
        self.accept_thread.join(DEADLINE)
        with self.changed:
            clients = list(self.clients)
            workers = list(self.workers)
        for client in clients:
            try:
                client.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            client.close()
        deadline = time.monotonic() + DEADLINE
        for worker in workers:
            worker.join(max(0, deadline - time.monotonic()))
        if self.accept_thread.is_alive() or any(worker.is_alive() for worker in workers):
            raise AssertionError("The fake TCP server did not drain its own threads.")
        if self.errors:
            raise AssertionError("The fake TCP server failed.") from self.errors[0]

    def __enter__(self):
        return self

    def __exit__(self, exception_type, exception, traceback):
        self.close()


class LoopbackRelayTests(unittest.TestCase):
    def connect(self, port: int) -> socket.socket:
        connection = socket.create_connection(("127.0.0.1", port), timeout=DEADLINE)
        connection.settimeout(DEADLINE)
        return connection

    def test_large_http_headers_and_opaque_bodies_are_transferred_unchanged_in_both_directions(self):
        request_body = bytes(range(256)) * 1025
        response_body = bytes(reversed(range(256))) * 1027
        request = (b"POST /emby/opaque?api_key=synthetic HTTP/1.1\r\nHost: original-host\r\n"
                   b"X-Original-Spacing:  preserved  \r\nContent-Length: " + str(len(request_body)).encode() + b"\r\n\r\n" + request_body)
        response = (b"HTTP/1.1 200 OK\r\nX-Opaque: keep-this-value\r\nContent-Length: "
                    + str(len(response_body)).encode() + b"\r\n\r\n" + response_body)
        received = []

        def serve(connection):
            received.append(receive_exact(connection, len(request)))
            connection.sendall(response)

        with FakeTcpServer(serve) as upstream:
            relay = relay_module.LoopbackRelay("127.0.0.1", upstream.port, listen_port=0).start()
            try:
                self.assertTrue(relay.is_running)
                self.assertNotEqual(upstream.port, relay.port)
                with self.connect(relay.port) as client:
                    client.sendall(request)
                    self.assertEqual(response, receive_exact(client, len(response)))
                self.assertEqual([request], received)
            finally:
                relay.close()
            self.assertTrue(upstream.wait_for_no_clients())

    def test_request_half_close_does_not_truncate_the_later_response(self):
        request = b"opaque-request\x00\xff" * 9000
        response = b"opaque-response\xff\x00" * 9000
        request_ended = threading.Event()

        def serve(connection):
            self.assertEqual(request, receive_to_eof(connection))
            request_ended.set()
            connection.sendall(response)

        with FakeTcpServer(serve) as upstream:
            relay = relay_module.LoopbackRelay("127.0.0.1", upstream.port, listen_port=0).start()
            try:
                with self.connect(relay.port) as client:
                    client.sendall(request)
                    client.shutdown(socket.SHUT_WR)
                    self.assertTrue(request_ended.wait(DEADLINE))
                    self.assertEqual(response, receive_to_eof(client))
            finally:
                relay.close()
            self.assertTrue(upstream.wait_for_no_clients())

    def test_close_interrupts_stalled_connections_and_is_bounded_and_reentrant(self):
        stalled = threading.Event()

        def serve(connection):
            self.assertEqual(b"hold", receive_exact(connection, 4))
            stalled.set()
            receive_to_eof(connection)

        with FakeTcpServer(serve) as upstream:
            relay = relay_module.LoopbackRelay("127.0.0.1", upstream.port, listen_port=0).start()
            try:
                with self.connect(relay.port) as client:
                    client.sendall(b"hold")
                    self.assertTrue(stalled.wait(DEADLINE))
                    started = time.monotonic()
                    relay.close()
                    self.assertLess(time.monotonic() - started, 5.5)
                    self.assertFalse(relay.is_running)
                    self.assertTrue(upstream.wait_for_no_clients())
                    try:
                        self.assertEqual(b"", client.recv(1))
                    except (ConnectionResetError, ConnectionAbortedError):
                        pass
                    relay.close()
                    relay.close()
            finally:
                relay.close()

    def test_close_waits_for_a_client_still_returning_its_final_capacity_permit(self):
        permit_reached = threading.Event()
        return_permit = threading.Event()
        admission_joined = threading.Event()
        worker_joined = threading.Event()
        close_finished = threading.Event()
        blocked_workers = []
        errors = []
        closer = None

        def serve(connection):
            connection.sendall(receive_exact(connection, 1))

        with FakeTcpServer(serve) as upstream:
            relay = relay_module.LoopbackRelay("127.0.0.1", upstream.port, listen_port=0)
            original_release = relay._slots.release

            def gated_release(*args, **kwargs):
                if threading.current_thread().name == "protocol-relay-client":
                    blocked_workers.append(threading.current_thread())
                    permit_reached.set()
                    if not return_permit.wait(DEADLINE):
                        errors.append(AssertionError("The final-permit barrier was not released."))
                return original_release(*args, **kwargs)

            def close_relay():
                try:
                    relay.close()
                except Exception as error:
                    errors.append(error)
                finally:
                    close_finished.set()

            with mock.patch.object(relay._slots, "release", side_effect=gated_release):
                relay.start()
                try:
                    with self.connect(relay.port) as client:
                        client.sendall(b"x")
                        client.shutdown(socket.SHUT_WR)
                        self.assertEqual(b"x", receive_to_eof(client))
                    self.assertTrue(permit_reached.wait(DEADLINE))
                    worker = blocked_workers[0]
                    self.assertTrue(worker.is_alive())
                    original_accept_join = relay._accept_thread.join
                    original_worker_join = worker.join

                    def observe_accept_join(*args, **kwargs):
                        original_accept_join(*args, **kwargs)
                        admission_joined.set()

                    def observe_worker_join(*args, **kwargs):
                        worker_joined.set()
                        original_worker_join(*args, **kwargs)

                    with mock.patch.object(relay._accept_thread, "join", side_effect=observe_accept_join), \
                            mock.patch.object(worker, "join", side_effect=observe_worker_join):
                        closer = threading.Thread(target=close_relay, name="test-relay-close", daemon=True)
                        closer.start()
                        self.assertTrue(admission_joined.wait(DEADLINE))
                        self.assertFalse(relay._accept_thread.is_alive())
                        self.assertTrue(worker_joined.wait(DEADLINE))
                        self.assertFalse(close_finished.is_set())
                        self.assertTrue(worker.is_alive())

                        return_permit.set()
                        closer.join(DEADLINE)
                        self.assertFalse(closer.is_alive())
                        self.assertTrue(close_finished.is_set())
                        self.assertFalse(worker.is_alive())
                        self.assertEqual([], errors)
                finally:
                    return_permit.set()
                    if closer is not None:
                        closer.join(DEADLINE)
                    for worker in blocked_workers:
                        worker.join(DEADLINE)
                    relay.close()
            self.assertTrue(upstream.wait_for_no_clients())

    def test_eight_upstream_connections_hold_capacity_until_one_client_retires(self):
        def serve(connection):
            marker = receive_exact(connection, 1)
            connection.sendall(marker)
            receive_to_eof(connection)

        with FakeTcpServer(serve) as upstream:
            relay = relay_module.LoopbackRelay("127.0.0.1", upstream.port, listen_port=0).start()
            clients = []
            try:
                for index in range(8):
                    client = self.connect(relay.port)
                    clients.append(client)
                    client.sendall(bytes([index]))
                    self.assertEqual(bytes([index]), receive_exact(client, 1))
                self.assertTrue(upstream.wait_for_accepted(8))
                waiting = self.connect(relay.port)
                clients.append(waiting)
                waiting.sendall(b"\x08")
                # Eight upstream acknowledgements establish saturation before the bounded negative observation.
                self.assertFalse(upstream.wait_for_accepted(9, timeout=0.25))

                clients[0].shutdown(socket.SHUT_RDWR)
                clients[0].close()
                self.assertTrue(upstream.wait_for_accepted(9))
                self.assertEqual(b"\x08", receive_exact(waiting, 1))
            finally:
                relay.close()
                for client in clients:
                    client.close()
            self.assertTrue(upstream.wait_for_no_clients())

    def test_an_occupied_port_fails_without_stopping_or_replacing_its_original_listener(self):
        def serve(connection):
            self.assertEqual(b"ping", receive_exact(connection, 4))
            connection.sendall(b"pong")

        with FakeTcpServer(serve) as original:
            relay = relay_module.LoopbackRelay("127.0.0.1", original.port, listen_port=original.port)
            try:
                with self.assertRaises(relay_module.RelayError) as failure:
                    relay.start()
                self.assertEqual("RelayStartFailed", failure.exception.code)
                self.assertFalse(relay.is_running)
                with self.connect(original.port) as client:
                    client.sendall(b"ping")
                    self.assertEqual(b"pong", receive_exact(client, 4))
            finally:
                relay.close()
            self.assertTrue(original.wait_for_no_clients())

    def test_invalid_targets_and_ports_are_rejected_without_creating_sockets(self):
        with mock.patch.object(relay_module.socket, "socket", side_effect=AssertionError("Invalid input must not create a socket.")) as create_socket:
            for address in ("8.8.8.8", "0.0.0.0", "224.0.0.1", "::1", "not-an-address", "http://127.0.0.1", None, 123):
                with self.subTest(address=address):
                    with self.assertRaises(relay_module.RelayError) as failure:
                        relay_module.LoopbackRelay(address)
                    self.assertEqual("InvalidRelayTarget", failure.exception.code)
            for target_port, listen_port in ((0, 0), (65536, 0), (True, 0), (8096, -1), (8096, 65536), (8096, True)):
                with self.subTest(target_port=target_port, listen_port=listen_port):
                    with self.assertRaises(relay_module.RelayError) as failure:
                        relay_module.LoopbackRelay("127.0.0.1", target_port, listen_port=listen_port)
                    self.assertEqual("InvalidRelayPort", failure.exception.code)
            create_socket.assert_not_called()


if __name__ == "__main__":
    unittest.main()
