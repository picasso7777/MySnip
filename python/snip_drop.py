"""Native OLE drop targets for floating images."""

import ctypes as c
from ctypes import wintypes as w
import struct
from uuid import UUID

import win32clipboard

_ole = c.OleDLL("ole32")
_shell = c.WinDLL("shell32")
_memory = c.WinDLL("kernel32")
_gdi = c.WinDLL("gdi32")
_ole.OleInitialize.argtypes = (c.c_void_p,)
_ole.OleUninitialize.argtypes = ()
_ole.OleUninitialize.restype = None
_ole.RegisterDragDrop.argtypes = (w.HWND, c.c_void_p)
_ole.RevokeDragDrop.argtypes = (w.HWND,)
_memory.GlobalLock.argtypes = (w.HGLOBAL,)
_memory.GlobalLock.restype = c.c_void_p
_memory.GlobalUnlock.argtypes = (w.HGLOBAL,)
_memory.GlobalSize.argtypes = (w.HGLOBAL,)
_memory.GlobalSize.restype = c.c_size_t
_shell.DragQueryFileW.argtypes = (w.HANDLE, w.UINT, w.LPWSTR, w.UINT)
_shell.DragQueryFileW.restype = w.UINT
_gdi.CreateCompatibleDC.argtypes = (w.HDC,)
_gdi.CreateCompatibleDC.restype = w.HDC
_gdi.DeleteDC.argtypes = (w.HDC,)
_gdi.GetObjectW.argtypes = (w.HANDLE, c.c_int, c.c_void_p)
_gdi.GetDIBits.argtypes = (w.HDC, w.HBITMAP, w.UINT, w.UINT, c.c_void_p, c.c_void_p, w.UINT)

_MAX_BYTES = 24 * 1024 * 1024
_COPY = 1
_HGLOBAL = 1
_GDI = 16
_INTERFACES = tuple(UUID(value).bytes_le for value in (
    "00000000-0000-0000-c000-000000000046",  # IUnknown
    "00000122-0000-0000-c000-000000000046",  # IDropTarget
))


class _Format(c.Structure):
    _fields_ = [("format", w.WORD), ("target", c.c_void_p), ("aspect", w.DWORD),
                ("index", w.LONG), ("medium", w.DWORD)]


class _Medium(c.Structure):
    _fields_ = [("kind", w.DWORD), ("handle", c.c_void_p), ("release", c.c_void_p)]


_ole.ReleaseStgMedium.argtypes = (c.POINTER(_Medium),)
_ole.ReleaseStgMedium.restype = None
_query = c.WINFUNCTYPE(w.LONG, c.c_void_p, c.c_void_p, c.POINTER(c.c_void_p))
_ref = c.WINFUNCTYPE(w.ULONG, c.c_void_p)
_enter = c.WINFUNCTYPE(w.LONG, c.c_void_p, c.c_void_p, w.DWORD, w.POINT, c.POINTER(w.DWORD))
_over = c.WINFUNCTYPE(w.LONG, c.c_void_p, w.DWORD, w.POINT, c.POINTER(w.DWORD))
_leave = c.WINFUNCTYPE(w.LONG, c.c_void_p)
_query_data = c.WINFUNCTYPE(w.LONG, c.c_void_p, c.POINTER(_Format))
_get_data = c.WINFUNCTYPE(w.LONG, c.c_void_p, c.POINTER(_Format), c.POINTER(_Medium))


class _Target(c.Structure):
    _fields_ = [("vtable", c.POINTER(c.c_void_p))]


class _Bitmap(c.Structure):
    _fields_ = [("type", w.LONG), ("width", w.LONG), ("height", w.LONG), ("stride", w.LONG),
                ("planes", w.WORD), ("bits_per_pixel", w.WORD), ("pixels", c.c_void_p)]


def _method(interface, slot, signature):
    table = c.cast(interface, c.POINTER(c.POINTER(c.c_void_p))).contents
    return signature(table[slot])


def _bitmap_as_dib(bitmap):
    shape = _Bitmap()
    if not _gdi.GetObjectW(bitmap, c.sizeof(shape), c.byref(shape)) or min(shape.width, shape.height) < 1:
        return None
    size = ((shape.width * 3 + 3) & ~3) * shape.height
    if size > _MAX_BYTES:
        return None
    header = c.create_string_buffer(struct.pack("<IiiHHIIiiII", 40, shape.width, shape.height, 1, 24, 0, size, 0, 0, 0, 0))
    dc = _gdi.CreateCompatibleDC(None)
    if not dc:
        return None
    try:
        pixels = c.create_string_buffer(size)
        if _gdi.GetDIBits(dc, bitmap, 0, shape.height, pixels, header, 0) == shape.height:
            return header.raw[:40] + pixels.raw
        return None
    finally:
        _gdi.DeleteDC(dc)


def _read_data(data, format_id, kind):
    if not format_id:
        return None
    info = _Format(format_id, None, 1, -1, kind)
    medium = _Medium()
    if _method(data, 3, _get_data)(data, c.byref(info), c.byref(medium)) < 0:
        return None
    try:
        if medium.kind != kind or not medium.handle:
            return None
        if kind == _GDI:
            return _bitmap_as_dib(medium.handle)
        if format_id == win32clipboard.CF_HDROP:
            count = _shell.DragQueryFileW(medium.handle, 0xFFFFFFFF, None, 0)
            paths = []
            for index in range(min(count, 256)):
                size = _shell.DragQueryFileW(medium.handle, index, None, 0)
                if size:
                    buffer = c.create_unicode_buffer(size + 1)
                    _shell.DragQueryFileW(medium.handle, index, buffer, size + 1)
                    paths.append(buffer.value)
            return paths
        size = _memory.GlobalSize(medium.handle)
        if not 0 < size <= _MAX_BYTES:
            return None
        pointer = _memory.GlobalLock(medium.handle)
        if pointer:
            try:
                value = c.string_at(pointer, size)
                if format_id == win32clipboard.CF_UNICODETEXT or format_id == _URL:
                    return value[:size & ~1].decode("utf-16-le", errors="replace").split("\0", 1)[0]
                return value
            finally:
                _memory.GlobalUnlock(medium.handle)
        return None
    finally:
        _ole.ReleaseStgMedium(c.byref(medium))


_URL = win32clipboard.RegisterClipboardFormat("UniformResourceLocatorW")
_FORMATS = (
    ("files", win32clipboard.CF_HDROP, _HGLOBAL),
    ("png", win32clipboard.RegisterClipboardFormat("PNG"), _HGLOBAL),
    ("dib", win32clipboard.CF_DIB, _HGLOBAL),
    ("dib", win32clipboard.CF_BITMAP, _GDI),
    ("html", win32clipboard.RegisterClipboardFormat("HTML Format"), _HGLOBAL),
    ("url", _URL, _HGLOBAL),
    ("text", win32clipboard.CF_UNICODETEXT, _HGLOBAL),
)


class DropTarget:
    def __init__(self, window, deliver):
        self._window = window
        self._deliver = deliver
        self._refs = 1
        self._accept = False
        self._callbacks = (_query(self._query), _ref(self._add_ref), _ref(self._release),
                           _enter(self._enter), _over(self._over), _leave(self._leave), _enter(self._drop))
        table_type = c.c_void_p * len(self._callbacks)
        self._table = table_type(*(c.cast(callback, c.c_void_p) for callback in self._callbacks))
        self._target = _Target(self._table)
        _ole.OleInitialize(None)
        try:
            _ole.RegisterDragDrop(window, c.byref(self._target))
        except OSError:
            _ole.OleUninitialize()
            raise

    def close(self):
        if self._window:
            try:
                _ole.RevokeDragDrop(self._window)
            except OSError:
                pass
            finally:
                self._window = None
                _ole.OleUninitialize()

    def _query(self, this, interface, result):
        result[0] = None
        if c.string_at(interface, 16) not in _INTERFACES:
            return -2147467262  # E_NOINTERFACE
        result[0] = this
        self._add_ref(this)
        return 0

    def _add_ref(self, _):
        self._refs += 1
        return self._refs

    def _release(self, _):
        self._refs -= 1
        return self._refs

    def _enter(self, this, data, keys, point, effect):
        self._accept = bool(data) and any(
            _method(data, 5, _query_data)(data, c.byref(_Format(format_id, None, 1, -1, kind))) == 0
            for _, format_id, kind in _FORMATS if format_id
        )
        return self._over(this, keys, point, effect)

    def _over(self, _this, _keys, _point, effect):
        effect[0] = effect[0] & _COPY if self._accept else 0
        return 0

    def _leave(self, _):
        self._accept = False
        return 0

    def _drop(self, _this, data, _keys, _point, effect):
        allowed = effect[0] & _COPY
        effect[0] = 0
        if not self._accept or not allowed or not data:
            return 0
        self._accept = False
        try:
            payload = {}
            for name, format_id, kind in _FORMATS:
                value = _read_data(data, format_id, kind)
                if value:
                    payload[name] = value
                    if name in ("files", "png", "dib"):
                        break
            if payload:
                self._deliver(payload)
                effect[0] = _COPY
        except Exception:
            return -2147467259  # E_FAIL
        return 0
