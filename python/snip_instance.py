"""Route commands to the running Python edition."""

from __future__ import annotations

import ctypes as c
from ctypes import wintypes as w
import hashlib
import json
from multiprocessing import AuthenticationError
from multiprocessing.connection import Client, Listener
import os
import secrets
import threading
import time


class Instance:
    def __init__(self, directory):
        key = hashlib.sha256(str(directory.resolve()).casefold().encode()).hexdigest()[:20]
        self.address = "\\\\.\\pipe\\MySnip.Python." + key
        self.token = directory / "python-command.key"
        self._kernel = c.WinDLL("kernel32", use_last_error=True)
        self._kernel.CreateMutexW.argtypes = (c.c_void_p, w.BOOL, w.LPCWSTR)
        self._kernel.CreateMutexW.restype = w.HANDLE
        self._kernel.CloseHandle.argtypes = (w.HANDLE,)
        self._handle = self._kernel.CreateMutexW(None, False, "Local\\MySnip.Python." + key)
        if not self._handle:
            raise c.WinError(c.get_last_error())
        self.primary = c.get_last_error() != 183
        self._listener = None
        self._closed = False

    def listen(self, dispatch):
        self.token.parent.mkdir(parents=True, exist_ok=True)
        secret = secrets.token_bytes(32)
        self.token.write_bytes(secret)
        self._listener = Listener(self.address, family="AF_PIPE", authkey=secret)

        def run():
            while not self._closed:
                try:
                    with self._listener.accept() as connection:
                        arguments, cwd = json.loads(connection.recv_bytes(64 * 1024))
                        if (isinstance(arguments, list) and all(isinstance(arg, str) for arg in arguments)
                                and isinstance(cwd, str)):
                            dispatch(arguments, cwd)
                except (AuthenticationError, OSError, EOFError, TypeError, ValueError):
                    if not self._closed:
                        time.sleep(0.02)

        threading.Thread(target=run, name="MySnip command pipe", daemon=True).start()

    def forward(self, arguments):
        deadline = time.monotonic() + 2
        while time.monotonic() < deadline:
            try:
                secret = self.token.read_bytes()
                with Client(self.address, family="AF_PIPE", authkey=secret) as connection:
                    connection.send_bytes(json.dumps([arguments, os.getcwd()]).encode("utf-8"))
                return
            except (OSError, EOFError, AuthenticationError):
                time.sleep(0.05)
        raise OSError("執行中的 MySnip Python 無法接收命令。")

    def close(self):
        self._closed = True
        if self._listener is not None:
            self._listener.close()
        if self._handle:
            self._kernel.CloseHandle(self._handle)
            self._handle = None
