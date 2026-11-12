"""Native Windows sharing, virtual desktops, and Win+drag input."""

from __future__ import annotations

import ctypes as c
from ctypes import wintypes as w
import io
import threading
from uuid import UUID

from snip_windows import user32

PTR = c.c_void_p
HRESULT = c.c_long
ULONG = w.ULONG
CALLTYPE = c.WINFUNCTYPE


class Guid(c.Structure):
    _fields_ = [("data", c.c_ubyte * 16)]

    @classmethod
    def parse(cls, text):
        return cls((c.c_ubyte * 16).from_buffer_copy(UUID(text).bytes_le))

    def text(self):
        return str(UUID(bytes_le=bytes(self.data)))


def abi(instance, slot, result, *args):
    if not instance:
        raise OSError("Windows 物件尚未初始化。")
    table = c.cast(instance, c.POINTER(c.POINTER(PTR))).contents
    return CALLTYPE(result, PTR, *args)(table[slot])


def check(code):
    if code < 0:
        raise OSError(f"Windows API 錯誤 0x{code & 0xffffffff:08X}")


def release(instance):
    if instance:
        abi(instance, 2, ULONG)(instance)


class VirtualDesktops:
    def __init__(self):
        self.manager = PTR()
        self.ole32 = c.WinDLL("ole32")
        self.ole32.CoInitializeEx.argtypes = (PTR, w.DWORD)
        self.ole32.CoInitializeEx.restype = HRESULT
        self._initialized = self.ole32.CoInitializeEx(None, 2) >= 0
        create = self.ole32.CoCreateInstance
        create.argtypes = (c.POINTER(Guid), PTR, w.DWORD, c.POINTER(Guid), c.POINTER(PTR))
        create.restype = HRESULT
        clsid = Guid.parse("aa509086-5ca9-4c25-8f95-589d3c07b48a")
        iid = Guid.parse("a5cd92ff-29be-454c-8d04-d82879fb3f1b")
        create(c.byref(clsid), None, 1, c.byref(iid), c.byref(self.manager))

    def current(self, handle):
        if not handle or not self.manager:
            return None
        active = w.BOOL()
        result = abi(self.manager, 3, HRESULT, w.HWND, c.POINTER(w.BOOL))(self.manager, handle, c.byref(active))
        if result < 0 or not active:
            return None
        desktop = Guid()
        result = abi(self.manager, 4, HRESULT, w.HWND, c.POINTER(Guid))(self.manager, handle, c.byref(desktop))
        return desktop.text() if result >= 0 and any(desktop.data) else None

    def move(self, handle, desktop):
        if not handle or not desktop or not self.manager or self.current(handle) == desktop:
            return
        target = Guid.parse(desktop)
        move = abi(self.manager, 5, HRESULT, w.HWND, c.POINTER(Guid))
        if move(self.manager, handle, c.byref(target)) < 0:
            owner = user32.GetAncestor(handle, 3)
            if owner and owner != handle:
                move(self.manager, owner, c.byref(target))

    def close(self):
        release(self.manager)
        self.manager = PTR()
        if self._initialized:
            self.ole32.CoUninitialize()
            self._initialized = False


class MouseEvent(c.Structure):
    _fields_ = [("position", w.POINT), ("data", w.DWORD), ("flags", w.DWORD), ("time", w.DWORD), ("extra", c.c_size_t)]


class WinDrag:
    _callback_type = CALLTYPE(c.c_ssize_t, c.c_int, w.WPARAM, w.LPARAM)

    def __init__(self, completed, available):
        self.completed, self.available = completed, available
        self.gesture = None
        self._start = None
        self._cancel = False
        self._stopped = False
        self._thread_id = 0
        self._ready = threading.Event()
        self._installed = False
        self._callback = self._callback_type(self._on_mouse)
        self._thread = threading.Thread(target=self._run, name="MySnip Win-drag", daemon=True)
        self._thread.start()
        if not self._ready.wait(2) or not self._installed:
            self.close()
            raise OSError("無法啟用 Win＋拖曳。")

    @staticmethod
    def down(key):
        return bool(user32.GetAsyncKeyState(key) & 0x8000)

    def _run(self):
        kernel = c.WinDLL("kernel32")
        kernel.GetModuleHandleW.argtypes = (w.LPCWSTR,)
        kernel.GetModuleHandleW.restype = w.HMODULE
        user32.SetWindowsHookExW.argtypes = (c.c_int, self._callback_type, w.HMODULE, w.DWORD)
        user32.SetWindowsHookExW.restype = PTR
        user32.UnhookWindowsHookEx.argtypes = (PTR,)
        user32.CallNextHookEx.argtypes = (PTR, c.c_int, w.WPARAM, w.LPARAM)
        user32.CallNextHookEx.restype = c.c_ssize_t
        user32.keybd_event.argtypes = (w.BYTE, w.BYTE, w.DWORD, c.c_size_t)
        user32.PostThreadMessageW.argtypes = (w.DWORD, w.UINT, w.WPARAM, w.LPARAM)
        message = w.MSG()
        user32.PeekMessageW(c.byref(message), None, 0, 0, 0)
        self._thread_id = kernel.GetCurrentThreadId()
        hook = user32.SetWindowsHookExW(14, self._callback, kernel.GetModuleHandleW(None), 0)
        self._installed = bool(hook)
        self._ready.set()
        if not hook:
            return
        try:
            while not self._stopped and user32.GetMessageW(c.byref(message), None, 0, 0) > 0:
                user32.TranslateMessage(c.byref(message))
                user32.DispatchMessageW(c.byref(message))
        finally:
            user32.UnhookWindowsHookEx(hook)

    def _on_mouse(self, code, message, data):
        if code < 0 or self._stopped or message not in (0x200, 0x201, 0x202, 0x204):
            return user32.CallNextHookEx(None, code, message, data)
        if self._start is None and message != 0x201:
            return user32.CallNextHookEx(None, code, message, data)
        event = c.cast(data, c.POINTER(MouseEvent)).contents
        if event.flags & 1:
            return user32.CallNextHookEx(None, code, message, data)
        point = event.position.x, event.position.y
        if self._start is None:
            if (self.down(0x5B) or self.down(0x5C)) and not any(self.down(key) for key in (0x10, 0x11, 0x12)) and self.available():
                self._start = point
                self._cancel = False
                self.gesture = point, point
                user32.keybd_event(0x87, 0, 0, 0)
                user32.keybd_event(0x87, 0, 2, 0)
                return 1
        else:
            if message == 0x204 or self.down(0x1B):
                self._cancel = True
            self.gesture = None if self._cancel else (self._start, point)
            if message == 0x202:
                origin, self._start = self._start, None
                self.gesture = None
                if not self._cancel and min(abs(origin[0] - point[0]), abs(origin[1] - point[1])) >= 4:
                    self.completed((min(origin[0], point[0]), min(origin[1], point[1]),
                                    max(origin[0], point[0]), max(origin[1], point[1])))
                return 1
            if message != 0x200:
                return 1
        return user32.CallNextHookEx(None, code, message, data)

    def close(self):
        self._stopped = True
        self.gesture = None
        if self._thread_id:
            user32.PostThreadMessageW(self._thread_id, 0x0012, 0, 0)
        self._thread.join(timeout=0.5)


class WindowsShare:
    manager_class = "Windows.ApplicationModel.DataTransfer.DataTransferManager"
    stream_class = "Windows.Storage.Streams.RandomAccessStreamReference"

    def __init__(self):
        self.manager = PTR()
        self._token = c.c_int64()
        self._subscribed = False
        self._apartment = False
        self.com = c.WinDLL("combase")
        self.com.RoInitialize.argtypes = (c.c_int,)
        self.com.RoInitialize.restype = HRESULT
        self.com.WindowsCreateString.argtypes = (w.LPCWSTR, w.UINT, c.POINTER(PTR))
        self.com.WindowsCreateString.restype = HRESULT
        self.com.WindowsDeleteString.argtypes = (PTR,)
        self.com.RoGetActivationFactory.argtypes = (PTR, c.POINTER(Guid), c.POINTER(PTR))
        self.com.RoGetActivationFactory.restype = HRESULT

    def _string(self, value):
        result = PTR()
        check(self.com.WindowsCreateString(value, len(value.encode("utf-16-le")) // 2, c.byref(result)))
        return result

    def _factory(self, runtime, interface):
        name = self._string(runtime)
        result = PTR()
        try:
            check(self.com.RoGetActivationFactory(name, c.byref(Guid.parse(interface)), c.byref(result)))
            return result
        finally:
            self.com.WindowsDeleteString(name)

    def show(self, handle, image):
        self.close()
        data = io.BytesIO()
        image.save(data, "PNG")
        self._apartment = self.com.RoInitialize(0) >= 0
        factory = PTR()
        try:
            factory = self._factory(self.manager_class, "3a3dcd6c-3eab-43dc-bcde-45671ce800c8")
            interface = Guid.parse("a5caee9b-8708-49d1-8d36-67d25a8da00c")
            check(abi(factory, 3, HRESULT, w.HWND, c.POINTER(Guid), c.POINTER(PTR))(
                factory, handle, c.byref(interface), c.byref(self.manager)))
            callback = _ShareCallback(self, data.getvalue())
            try:
                check(abi(self.manager, 6, HRESULT, PTR, c.POINTER(c.c_int64))(
                    self.manager, callback.pointer, c.byref(self._token)))
                self._subscribed = True
            finally:
                callback.drop(callback.pointer.value)
            check(abi(factory, 4, HRESULT, w.HWND)(factory, handle))
        except OSError:
            release(factory)
            factory = PTR()
            self.close()
            raise
        finally:
            release(factory)

    def _request(self, args, png):
        request, package, properties, bitmap = PTR(), PTR(), PTR(), PTR()
        title = PTR()
        try:
            check(abi(args, 6, HRESULT, c.POINTER(PTR))(args, c.byref(request)))
            check(abi(request, 6, HRESULT, c.POINTER(PTR))(request, c.byref(package)))
            check(abi(package, 7, HRESULT, c.POINTER(PTR))(package, c.byref(properties)))
            title = self._string("MySnip 截圖")
            check(abi(properties, 7, HRESULT, PTR)(properties, title))
            bitmap = self._stream_reference(png)
            check(abi(package, 21, HRESULT, PTR)(package, bitmap))
            return 0
        except Exception:
            return -2147467259
        finally:
            self.com.WindowsDeleteString(title)
            for instance in (bitmap, properties, package, request):
                release(instance)

    def _stream_reference(self, png):
        memory = c.WinDLL("shlwapi").SHCreateMemStream
        memory.argtypes = (c.c_char_p, w.UINT)
        memory.restype = PTR
        stream = memory(png, len(png))
        if not stream:
            raise OSError("無法建立分享圖片串流。")
        access, factory = PTR(), PTR()
        try:
            create = c.WinDLL("shcore").CreateRandomAccessStreamOverStream
            create.argtypes = (PTR, w.DWORD, c.POINTER(Guid), c.POINTER(PTR))
            create.restype = HRESULT
            stream_id = Guid.parse("905a0fe1-bc53-11df-8c49-001e4fc686da")
            check(create(stream, 0, c.byref(stream_id), c.byref(access)))
            factory = self._factory(self.stream_class, "857309dc-3fbf-4e7d-986f-ef3b1a07a964")
            result = PTR()
            check(abi(factory, 8, HRESULT, PTR, c.POINTER(PTR))(factory, access, c.byref(result)))
            return result
        finally:
            release(factory)
            release(access)
            release(stream)

    def close(self):
        if self._subscribed:
            abi(self.manager, 7, HRESULT, c.c_int64)(self.manager, self._token)
        self._subscribed = False
        release(self.manager)
        self.manager = PTR()
        if self._apartment:
            self.com.RoUninitialize()
            self._apartment = False


class _ShareCallback:
    _live = {}
    _handler_id = UUID("ec6f9cc8-46d0-5e0e-b4d2-7d7773ae37a0").bytes_le
    _unknown_id = UUID("00000000-0000-0000-c000-000000000046").bytes_le

    def __init__(self, owner, png):
        self._count = 1
        query = CALLTYPE(HRESULT, PTR, c.POINTER(Guid), c.POINTER(PTR))(self.query)
        ref = CALLTYPE(ULONG, PTR)
        invoke = CALLTYPE(HRESULT, PTR, PTR, PTR)(lambda _, sender, args: owner._request(args, png))
        self._callbacks = query, ref(self.add), ref(self.drop), invoke
        self._table = (PTR * 4)(*(c.cast(callback, PTR) for callback in self._callbacks))
        self._interface = (PTR * 1)(c.cast(self._table, PTR))
        self.pointer = c.cast(self._interface, PTR)
        self._live[self.pointer.value] = self

    def query(self, current, iid, result):
        if bytes(iid.contents.data) in (self._handler_id, self._unknown_id):
            self.add(current)
            result[0] = current
            return 0
        result[0] = None
        return -2147467262

    def add(self, current):
        self._count += 1
        return self._count

    def drop(self, current):
        self._count -= 1
        if self._count == 0:
            self._live.pop(self.pointer.value, None)
        return self._count
