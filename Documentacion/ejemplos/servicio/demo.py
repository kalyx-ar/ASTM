"""Consumidor mínimo, sólo sintético. No SDK, persistencia ni lógica de laboratorio.

Iniciar el servicio por separado con config.synthetic.json. Windows: --endpoint
astm-synthetic. Unix: --endpoint /ruta/privada/service.sock (ajustar configuración).
La traza de demostración se conserva en memoria y la ingestión devuelve Deferred.
No conectar este ejemplo a un canal físico ni usarlo como custodia productiva.
"""

import argparse
import base64
import concurrent.futures
import decimal
import json
import os
import socket
import struct
import threading
import uuid


class _WindowsPipe:
    """E/S solapada Win32 de la biblioteca estándar: lectura y callbacks simultáneos.

    Un FileIO síncrono comparte un bloqueo entre read/write y no sirve para este
    lector permanente. Cada operación conserva su OVERLAPPED/buffer hasta terminar.
    """
    def __init__(self, endpoint):
        import ctypes
        from ctypes import wintypes as w
        self.c = ctypes

        class Overlapped(ctypes.Structure):
            _fields_ = [("internal", ctypes.c_size_t), ("internal_high", ctypes.c_size_t),
                        ("offset", w.DWORD), ("offset_high", w.DWORD), ("event", w.HANDLE)]

        self.overlapped = Overlapped
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        prototypes = {
            "CreateFileW": (w.HANDLE, [w.LPCWSTR, w.DWORD, w.DWORD, w.LPVOID, w.DWORD, w.DWORD, w.HANDLE]),
            "CreateEventW": (w.HANDLE, [w.LPVOID, w.BOOL, w.BOOL, w.LPCWSTR]),
            "ReadFile": (w.BOOL, [w.HANDLE, w.LPVOID, w.DWORD, ctypes.POINTER(w.DWORD), ctypes.POINTER(Overlapped)]),
            "WriteFile": (w.BOOL, [w.HANDLE, w.LPVOID, w.DWORD, ctypes.POINTER(w.DWORD), ctypes.POINTER(Overlapped)]),
            "GetOverlappedResult": (w.BOOL, [w.HANDLE, ctypes.POINTER(Overlapped), ctypes.POINTER(w.DWORD), w.BOOL]),
            "CancelIoEx": (w.BOOL, [w.HANDLE, ctypes.POINTER(Overlapped)]),
            "CloseHandle": (w.BOOL, [w.HANDLE]),
        }
        for name, (result, arguments) in prototypes.items():
            function = getattr(self.api, name)
            function.restype, function.argtypes = result, arguments
        self.handle = self.api.CreateFileW("\\\\.\\pipe\\" + endpoint, 0xC0000000, 0, None, 3, 0x40000000, None)
        if self.handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())

    def _io(self, data, count, writing):
        from ctypes import wintypes as w
        c = self.c
        buffer = c.create_string_buffer(bytes(data)) if writing else c.create_string_buffer(count)
        event = self.api.CreateEventW(None, True, False, None)
        if not event:
            raise c.WinError(c.get_last_error())
        operation = self.overlapped()
        operation.event = event
        done = w.DWORD()
        try:
            call = self.api.WriteFile if writing else self.api.ReadFile
            if not call(self.handle, buffer, count, c.byref(done), c.byref(operation)):
                if c.get_last_error() != 997:  # ERROR_IO_PENDING
                    raise c.WinError(c.get_last_error())
                if not self.api.GetOverlappedResult(self.handle, c.byref(operation), c.byref(done), True):
                    raise c.WinError(c.get_last_error())
            return done.value if writing else buffer.raw[:done.value]
        finally:
            self.api.CloseHandle(event)

    def read(self, count):
        return self._io(None, count, False)

    def write(self, data):
        return self._io(data, len(data), True)

    def cancel(self):
        self.api.CancelIoEx(self.handle, None)

    def close(self):
        self.api.CloseHandle(self.handle)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--endpoint", required=True)
    parser.add_argument("--synthetic", action="store_true", required=True)
    args = parser.parse_args()
    transport = None
    if os.name == "nt":
        stream = _WindowsPipe(args.endpoint)
    else:
        transport = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        transport.connect(args.endpoint)
        stream = transport.makefile("rwb", buffering=0)
    write_lock = threading.Lock()
    pending_lock = threading.Lock()
    pending = {}
    traces = []  # Sólo sintéticos: no es custodia durable.
    ingests = []
    callbacks = concurrent.futures.ThreadPoolExecutor(max_workers=4)

    def exact(count):
        result = bytearray()
        while len(result) < count:
            chunk = stream.read(count - len(result))
            if not chunk:
                raise EOFError("IPC cerrado")
            result.extend(chunk)
        return result

    def send(message):
        data = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        if len(data) > 4 * 1024 * 1024:
            raise ValueError("Límite IPC")
        with write_lock:
            # FileIO.write puede ser parcial en un pipe: completar toda la trama.
            wire = memoryview(struct.pack(">I", len(data)) + data)
            while wire:
                written = stream.write(wire)
                if not written:
                    raise EOFError("IPC cerrado")
                wire = wire[written:]

    def callback(message):
        body = message["data"]
        method = message["method"]
        if method == "trace.ready":
            data = {"traceId": body["traceId"], "ready": True}
        elif method == "trace.append":
            traces.append(body)
            data = {"traceId": body["traceId"], "sequence": body["entry"]["sequence"], "stored": True}
        elif method == "workflow.ingest":
            ingests.append(body)
            data = {"state": "Deferred", "code": "synthetic.no-durable-store"}
        elif method == "workflow.query":
            data = {"state": "NoInformation", "orders": []}
        else:
            send({"v": 1, "id": message["id"], "kind": "error", "data": {}, "error": "unsupported"})
            return
        send({"v": 1, "id": message["id"], "kind": "result", "data": data})

    def receive():
        try:
            while True:
                length = struct.unpack(">I", exact(4))[0]
                if not 0 < length <= 4 * 1024 * 1024:
                    raise ValueError("Límite IPC")
                message = json.loads(exact(length), parse_float=decimal.Decimal)
                if message["v"] != 1:
                    raise ValueError("Versión IPC")
                if message["kind"] == "callback":
                    callbacks.submit(callback, message)
                else:
                    with pending_lock:
                        completion = pending.get(message["id"])
                    if completion is not None:
                        completion.set_result(message)
        except (EOFError, OSError, ValueError) as error:
            with pending_lock:
                for completion in pending.values():
                    if not completion.done():
                        completion.set_exception(error)

    reader = threading.Thread(target=receive, daemon=True)
    reader.start()

    def request(method, data, session=None):
        identity = str(uuid.uuid4())
        completion = concurrent.futures.Future()
        with pending_lock:
            pending[identity] = completion
        try:
            message = {"v": 1, "id": identity, "kind": "request", "method": method, "data": data}
            if session:
                message["sessionId"] = session
            send(message)
            result = completion.result(timeout=15)
            if result["kind"] == "error":
                raise RuntimeError(result.get("error", "IPC"))
            return result["data"]
        finally:
            with pending_lock:
                pending.pop(identity, None)

    try:
        hello = request("service.hello", {})
        profiles = request("profiles.list", {})
        for profile in profiles:
            query = {"profileId": profile["id"]}
            if profile["requiresTiming"]:
                query.update(queryBudgetMs=1000, responseTimeoutMs=1000)
            described = request("profiles.describe", query)
            assert described["operations"] and described["wireOutputs"]
        details = request("profiles.describe", {"profileId": "abbott.architect.hl7-2.5.1", "queryBudgetMs": 1000, "responseTimeoutMs": 1000})
        opened = request("sessions.open", {"instrumentId": "synthetic", "traceId": str(uuid.uuid4())})
        session = opened["sessionId"]
        message = (
            "MSH|^~\\&|ARCHITECT|SYN|HOST|LAB|20261103000000+0000||INU^U05^INU_U05|INV-SYN|P|2.5.1||||||UNICODE UTF-8\r"
            "SFT|SYN|1|SYN|SYN\rEQU|MOD-SYN&1|20261103000000\rINV|WASTE|IO|LW||||100|10|90||mL\r"
        )
        outcome = request("messages.process", {"receptionId": str(uuid.uuid4()), "content": base64.b64encode(message.encode()).decode()}, session)
        assert outcome["outcomes"][0]["state"] == "Deferred"
        assert b"MSA|AE|" in base64.b64decode(outcome["responses"][0])
        assert len(ingests[0]["inventoryStatuses"]) == 1
        request("sessions.close", {}, session)
        print(json.dumps({"protocol": hello["protocol"], "profiles": len(profiles), "operations": len(details["operations"]),
                          "profiles_described": len(profiles), "synthetic_ingests": len(ingests), "trace_entries_in_memory": len(traces), "outcome": "Deferred"}))
    finally:
        callbacks.shutdown(wait=True)
        if transport:
            transport.shutdown(socket.SHUT_RDWR)
        else:
            stream.cancel()
        reader.join(timeout=5)
        if reader.is_alive():
            raise RuntimeError("El lector IPC no terminó")
        stream.close()
        if transport:
            transport.close()


if __name__ == "__main__":
    main()
