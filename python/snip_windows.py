"""Windows screen, clipboard, and global shortcut operations."""

from __future__ import annotations

import ctypes as c
from ctypes import wintypes as w
import io
import os
import queue
import re
import struct
import threading
import time
from contextlib import contextmanager
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urlsplit

from PIL import Image, ImageGrab, ImageWin

from snip_images import IMAGE_EXTENSIONS, StickerAsset, color_card, parse_color, text_image

user32 = c.WinDLL("user32", use_last_error=True)
gdi32 = c.WinDLL("gdi32", use_last_error=True)
_dwm = c.WinDLL("dwmapi")
_dwm.DwmGetWindowAttribute.argtypes = (w.HWND, w.DWORD, c.c_void_p, w.DWORD)
gdi32.CreateCompatibleDC.argtypes = (w.HDC,)
gdi32.CreateCompatibleDC.restype = w.HDC
gdi32.CreateDIBSection.argtypes = (w.HDC, c.c_void_p, w.UINT, c.POINTER(c.c_void_p), w.HANDLE, w.DWORD)
gdi32.CreateDIBSection.restype = w.HBITMAP
gdi32.SelectObject.argtypes = (w.HDC, w.HANDLE)
gdi32.SelectObject.restype = w.HANDLE
gdi32.DeleteObject.argtypes = (w.HANDLE,)
gdi32.DeleteDC.argtypes = (w.HDC,)
user32.GetAncestor.argtypes = (w.HWND, w.UINT)
user32.GetAncestor.restype = w.HWND
user32.SetWindowPos.argtypes = (w.HWND, w.HWND, c.c_int, c.c_int, c.c_int, c.c_int, w.UINT)
user32.SetWindowPos.restype = w.BOOL
user32.GetWindowRect.argtypes = (w.HWND, c.POINTER(w.RECT))
user32.SetCursorPos.argtypes = (c.c_int, c.c_int)
user32.SetCursorPos.restype = w.BOOL
user32.GetWindowRect.restype = w.BOOL
user32.GetWindowLongPtrW.argtypes = (w.HWND, c.c_int)
user32.GetWindowLongPtrW.restype = c.c_ssize_t
user32.SetWindowLongPtrW.argtypes = (w.HWND, c.c_int, c.c_ssize_t)
user32.SetWindowLongPtrW.restype = c.c_ssize_t
user32.GetForegroundWindow.restype = w.HWND


def enable_dpi():
    try:
        set_context = user32.SetProcessDpiAwarenessContext
        set_context.argtypes = (c.c_void_p,)
        set_context.restype = w.BOOL
        if set_context(c.c_void_p(-4)):
            return
        thread_context = user32.SetThreadDpiAwarenessContext
        thread_context.argtypes = (c.c_void_p,)
        thread_context.restype = c.c_void_p
        if thread_context(c.c_void_p(-4)):
            return
    except AttributeError:
        pass
    try:
        c.windll.shcore.SetProcessDpiAwareness(2)
    except (AttributeError, OSError):
        user32.SetProcessDPIAware()


def hwnd(window):
    return user32.GetAncestor(window.winfo_id(), 2)


def position_window(window, x, y, width=None, height=None, topmost=True):
    window.update_idletasks()
    flags = 0x0010 | 0x0400
    if width is None or height is None:
        flags |= 0x0001 | 0x0004
    if not user32.SetWindowPos(hwnd(window), w.HWND(-1 if topmost else -2), x, y, width or 0, height or 0, flags):
        raise c.WinError(c.get_last_error())


def window_rect(handle):
    if not handle:
        return None
    rect = w.RECT()
    return (rect.left, rect.top, rect.right, rect.bottom) if user32.GetWindowRect(handle, c.byref(rect)) else None


def capture_window_rect(handle):
    if not handle:
        return None
    cloaked = w.DWORD()
    if _dwm.DwmGetWindowAttribute(handle, 14, c.byref(cloaked), c.sizeof(cloaked)) == 0 and cloaked.value:
        return None
    rect = w.RECT()
    if _dwm.DwmGetWindowAttribute(handle, 9, c.byref(rect), c.sizeof(rect)) == 0:
        return rect.left, rect.top, rect.right, rect.bottom
    return window_rect(handle)


class _PrintDialog(c.Structure):
    _fields_ = [("size", w.DWORD), ("owner", w.HWND), ("mode", w.HGLOBAL), ("names", w.HGLOBAL), ("dc", w.HDC),
                ("flags", w.DWORD), ("first", w.WORD), ("last", w.WORD), ("minimum", w.WORD), ("maximum", w.WORD),
                ("copies", w.WORD), ("instance", w.HINSTANCE), ("data", w.LPARAM),
                ("print_hook", c.c_void_p), ("setup_hook", c.c_void_p), ("print_template", w.LPCWSTR),
                ("setup_template", w.LPCWSTR), ("print_handle", w.HGLOBAL), ("setup_handle", w.HGLOBAL)]


class _PrintDocument(c.Structure):
    _fields_ = [("size", c.c_int), ("name", w.LPCWSTR), ("output", w.LPCWSTR), ("data_type", w.LPCWSTR), ("flags", w.DWORD)]


def print_image(image, owner):
    common = c.WinDLL("comdlg32")
    common.PrintDlgW.argtypes = (c.POINTER(_PrintDialog),)
    common.PrintDlgW.restype = w.BOOL
    common.CommDlgExtendedError.restype = w.DWORD
    free = c.WinDLL("kernel32").GlobalFree
    free.argtypes = (w.HGLOBAL,)
    free.restype = w.HGLOBAL
    gdi32.GetDeviceCaps.argtypes = (w.HDC, c.c_int)
    gdi32.StartDocW.argtypes = (w.HDC, c.POINTER(_PrintDocument))
    for operation in (gdi32.StartPage, gdi32.EndPage, gdi32.EndDoc, gdi32.AbortDoc):
        operation.argtypes = (w.HDC,)
    dialog = _PrintDialog(size=c.sizeof(_PrintDialog), owner=owner, copies=1,
                          flags=0x100 | 0x40000 | 0x80000 | 0x4 | 0x8)
    try:
        if not common.PrintDlgW(c.byref(dialog)):
            if error := common.CommDlgExtendedError():
                raise OSError(f"無法開啟列印對話框（{error:#x}）。")
            return False
        dc = dialog.dc
        size = gdi32.GetDeviceCaps(dc, 8), gdi32.GetDeviceCaps(dc, 10)
        if min(size) <= 0 or gdi32.StartDocW(dc, c.byref(_PrintDocument(c.sizeof(_PrintDocument), "MySnip"))) <= 0:
            raise OSError("無法開始列印。")
        try:
            if gdi32.StartPage(dc) <= 0:
                raise OSError("無法開始列印頁面。")
            source = image.convert("RGBA")
            rgb = Image.new("RGB", source.size, "white")
            rgb.paste(source, mask=source.getchannel("A"))
            scale = min(size[0] / image.width, size[1] / image.height)
            ImageWin.Dib(rgb).draw(ImageWin.HDC(dc), (0, 0, max(1, round(image.width * scale)), max(1, round(image.height * scale))))
            if gdi32.EndPage(dc) <= 0 or gdi32.EndDoc(dc) <= 0:
                raise OSError("無法完成列印。")
        except Exception:
            gdi32.AbortDoc(dc)
            raise
        return True
    finally:
        if dialog.dc:
            gdi32.DeleteDC(dialog.dc)
        for handle in (dialog.mode, dialog.names):
            if handle:
                free(handle)


def set_click_through(window, enabled):
    handle = hwnd(window)
    style = user32.GetWindowLongPtrW(handle, -20)
    user32.SetWindowLongPtrW(handle, -20, style | 0x80020 if enabled else style & ~0x20)
    user32.SetWindowPos(handle, None, 0, 0, 0, 0, 0x0037)


class _BlendFunction(c.Structure):
    _fields_ = [("operation", w.BYTE), ("flags", w.BYTE), ("opacity", w.BYTE), ("alpha_format", w.BYTE)]


user32.UpdateLayeredWindow.argtypes = (w.HWND, w.HDC, c.POINTER(w.POINT), c.POINTER(w.SIZE),
                                      w.HDC, c.POINTER(w.POINT), w.DWORD, c.POINTER(_BlendFunction), w.DWORD)
user32.UpdateLayeredWindow.restype = w.BOOL


def reset_layered_surface(window):
    handle = hwnd(window)
    style = user32.GetWindowLongPtrW(handle, -20)
    user32.SetWindowLongPtrW(handle, -20, style & ~0x80000)
    user32.SetWindowLongPtrW(handle, -20, style | 0x80000)


def show_layered_image(window, image, opacity):
    width, height = image.size
    pixels = image.convert("RGBa").tobytes("raw", "BGRa")
    header = c.create_string_buffer(struct.pack("<IiiHHIIiiII", 40, width, -height, 1, 32, 0, 0, 0, 0, 0, 0))
    dc = gdi32.CreateCompatibleDC(None)
    if not dc:
        raise c.WinError(c.get_last_error())
    pointer = c.c_void_p()
    bitmap = previous = None
    try:
        bitmap = gdi32.CreateDIBSection(dc, header, 0, c.byref(pointer), None, 0)
        if not bitmap:
            raise c.WinError(c.get_last_error())
        previous = gdi32.SelectObject(dc, bitmap)
        c.memmove(pointer, pixels, len(pixels))
        size, origin = w.SIZE(width, height), w.POINT(0, 0)
        blend = _BlendFunction(0, 0, round(opacity * 255), 1)
        if not user32.UpdateLayeredWindow(hwnd(window), None, None, c.byref(size), dc, c.byref(origin), 0, c.byref(blend), 2):
            raise c.WinError(c.get_last_error())
    finally:
        if previous:
            gdi32.SelectObject(dc, previous)
        if bitmap:
            gdi32.DeleteObject(bitmap)
        gdi32.DeleteDC(dc)


def monitor_bounds():
    import pywintypes
    import win32api
    monitors = []
    try:
        displays = win32api.EnumDisplayMonitors()
    except pywintypes.error as error:
        raise OSError("無法列舉螢幕。") from error
    dpi = c.WinDLL("shcore").GetDpiForMonitor
    dpi.argtypes = (w.HMONITOR, c.c_int, c.POINTER(w.UINT), c.POINTER(w.UINT))
    for handle, _, (left, top, right, bottom) in displays:
        x, y = w.UINT(96), w.UINT(96)
        dpi(int(handle), 0, c.byref(x), c.byref(y))
        monitors.append(dict(left=left, top=top, width=right - left, height=bottom - top,
                             dpi_x=x.value or 96, dpi_y=y.value or 96))
    if not monitors:
        raise OSError("找不到可用的螢幕。")
    left = min(m["left"] for m in monitors)
    top = min(m["top"] for m in monitors)
    right = max(m["left"] + m["width"] for m in monitors)
    bottom = max(m["top"] + m["height"] for m in monitors)
    return dict(left=left, top=top, width=right - left, height=bottom - top), monitors


def monitor_at(point, monitors):
    x, y = point

    def distance(monitor):
        left, top, width, height = (monitor[key] for key in ("left", "top", "width", "height"))
        dx = max(left - x, x - (left + width - 1), 0)
        dy = max(top - y, y - (top + height - 1), 0)
        return dx * dx + dy * dy

    return min(monitors, key=distance)


def capture_desktop(include_cursor=False):
    bounds, monitors = monitor_bounds()
    try:
        import mss
    except ImportError:
        mss = None
    image = Image.new("RGB", (bounds["width"], bounds["height"]))
    if mss is not None:
        from mss.exception import ScreenShotError
        try:
            with mss.mss() as grabber:
                for monitor in monitors:
                    frame = grabber.grab(monitor)
                    image.paste(Image.frombytes("RGB", frame.size, frame.rgb),
                                (monitor["left"] - bounds["left"], monitor["top"] - bounds["top"]))
        except ScreenShotError as ex:
            raise OSError(str(ex)) from ex
    else:
        image = ImageGrab.grab(include_layered_windows=True, all_screens=True).convert("RGB")
        if image.size != (bounds["width"], bounds["height"]):
            raise OSError("擷取期間螢幕配置已變更。")
    if include_cursor:
        image = _paint_cursor(image, bounds)
    return image, bounds, monitors


class CursorInfo(c.Structure):
    _fields_ = [("size", w.DWORD), ("flags", w.DWORD), ("cursor", w.HANDLE), ("position", w.POINT)]


class IconInfo(c.Structure):
    _fields_ = [("icon", w.BOOL), ("x", w.DWORD), ("y", w.DWORD), ("mask", w.HBITMAP), ("color", w.HBITMAP)]


def _paint_cursor(image, bounds):
    info = CursorInfo(c.sizeof(CursorInfo))
    icon = IconInfo()
    user32.GetCursorInfo.argtypes = (c.POINTER(CursorInfo),)
    user32.GetIconInfo.argtypes = (w.HANDLE, c.POINTER(IconInfo))
    user32.DrawIconEx.argtypes = (w.HDC, c.c_int, c.c_int, w.HANDLE, c.c_int, c.c_int, w.UINT, w.HBRUSH, w.UINT)
    if not user32.GetCursorInfo(c.byref(info)) or not info.flags & 1 or not user32.GetIconInfo(info.cursor, c.byref(icon)):
        return image
    x = info.position.x - bounds["left"] - icon.x
    y = info.position.y - bounds["top"] - icon.y
    box = max(0, x), max(0, y), min(image.width, x + 256), min(image.height, y + 256)
    dc = bitmap = previous = None
    try:
        if box[2] <= box[0] or box[3] <= box[1]:
            return image
        tile = image.crop(box)
        width, height = tile.size
        data = tile.tobytes("raw", "BGRX")
        dc = gdi32.CreateCompatibleDC(None)
        pointer = c.c_void_p()
        header = c.create_string_buffer(struct.pack("<IiiHHIIiiII", 40, width, -height, 1, 32, 0, 0, 0, 0, 0, 0))
        bitmap = gdi32.CreateDIBSection(dc, header, 0, c.byref(pointer), None, 0) if dc else None
        if not bitmap:
            return image
        previous = gdi32.SelectObject(dc, bitmap)
        c.memmove(pointer, data, len(data))
        if user32.DrawIconEx(dc, x - box[0], y - box[1], info.cursor, 0, 0, 0, None, 3):
            image.paste(Image.frombytes("RGB", tile.size, c.string_at(pointer, len(data)), "raw", "BGRX"), box[:2])
        return image
    finally:
        if previous:
            gdi32.SelectObject(dc, previous)
        for handle in (bitmap, icon.mask, icon.color):
            if handle:
                gdi32.DeleteObject(handle)
        if dc:
            gdi32.DeleteDC(dc)


def capture_windows():
    import pywintypes
    import win32gui
    import win32process
    candidates = []
    pid = os.getpid()

    def add(window, _):
        try:
            if (not win32gui.IsWindowVisible(window) or win32gui.IsIconic(window) or
                    win32gui.GetClassName(window) in ("Progman", "WorkerW") or
                    win32process.GetWindowThreadProcessId(window)[1] == pid):
                return
            box = capture_window_rect(window)
            if not box or box[2] - box[0] < 6 or box[3] - box[1] < 6:
                return
            children = []

            def child(handle, _):
                if len(children) < 200 and win32gui.IsWindowVisible(handle):
                    area = window_rect(handle)
                    if area and area[2] - area[0] >= 6 and area[3] - area[1] >= 6:
                        children.append(area)

            win32gui.EnumChildWindows(window, child, None)
            candidates.append((window, box, children))
        except (OSError, pywintypes.error):
            pass

    try:
        win32gui.EnumWindows(add, None)
    except pywintypes.error:
        pass
    return candidates


@contextmanager
def clipboard():
    import pywintypes
    import win32clipboard as cb
    for attempt in range(5):
        try:
            cb.OpenClipboard()
            break
        except pywintypes.error:
            if attempt == 4:
                raise OSError("剪貼簿正在使用中。")
            time.sleep(0.025)
    try:
        yield cb
    except pywintypes.error as error:
        raise OSError("無法讀寫 Windows 剪貼簿。") from error
    finally:
        cb.CloseClipboard()


def copy_text(text):
    with clipboard() as cb:
        cb.EmptyClipboard()
        cb.SetClipboardData(cb.CF_UNICODETEXT, text)


def copy_image(image, path=None):
    png, dib = io.BytesIO(), io.BytesIO()
    image.save(png, "PNG")
    image.convert("RGB").save(dib, "BMP")
    with clipboard() as cb:
        cb.EmptyClipboard()
        cb.SetClipboardData(cb.RegisterClipboardFormat("PNG"), png.getvalue())
        cb.SetClipboardData(cb.CF_DIB, dib.getvalue()[14:])
        if path is not None:
            files = (str(Path(path).absolute()) + "\0\0").encode("utf-16-le")
            cb.SetClipboardData(cb.CF_HDROP, struct.pack("<IiiII", 20, 0, 0, 0, 1) + files)


class HtmlText(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts = []
        self.ignored = 0

    def handle_starttag(self, tag, attrs):
        if tag in ("script", "style", "head"):
            self.ignored += 1
        if tag == "br":
            self.parts.append("\n")
        if tag == "li":
            self.parts.append("• ")

    def handle_endtag(self, tag):
        if tag in ("script", "style", "head"):
            self.ignored = max(0, self.ignored - 1)
        if tag in ("p", "div", "li", "tr", "h1", "h2", "h3"):
            self.parts.append("\n")

    def handle_data(self, data):
        if not self.ignored:
            self.parts.append(data)


def plain_html(html):
    if isinstance(html, bytes):
        html = html.decode("utf-8", errors="replace")
    if "<!--StartFragment-->" in html:
        html = html.split("<!--StartFragment-->", 1)[1].split("<!--EndFragment-->", 1)[0]
    elif "<html" in html.lower():
        html = html[html.lower().index("<html"):]
    parser = HtmlText()
    parser.feed(html[:250_000])
    return "".join(parser.parts).strip("\0 \n")


def clipboard_contents():
    with clipboard() as cb:
        png_format = cb.RegisterClipboardFormat("PNG")
        html_format = cb.RegisterClipboardFormat("HTML Format")
        files = cb.GetClipboardData(cb.CF_HDROP) if cb.IsClipboardFormatAvailable(cb.CF_HDROP) else ()
        png = cb.GetClipboardData(png_format) if cb.IsClipboardFormatAvailable(png_format) else None
        html = cb.GetClipboardData(html_format) if cb.IsClipboardFormatAvailable(html_format) else None
        text = cb.GetClipboardData(cb.CF_UNICODETEXT) if cb.IsClipboardFormatAvailable(cb.CF_UNICODETEXT) else ""
    assets = []
    for path in files:
        if Path(path).suffix.lower() not in IMAGE_EXTENSIONS:
            continue
        try:
            assets.append(StickerAsset.load(path))
        except (OSError, ValueError):
            continue
    if assets:
        return assets
    if png:
        with Image.open(io.BytesIO(png)) as image:
            return [StickerAsset.from_image(image)]
    source = ImageGrab.grabclipboard()
    if isinstance(source, Image.Image):
        return [StickerAsset.from_image(source)]
    color = parse_color(text)
    if color:
        return [color_card(color)]
    text = plain_html(html) or text if html else text
    return [StickerAsset((text_image(text),))] if text.strip() else []


def clipboard_image_url():
    with clipboard() as cb:
        png_format = cb.RegisterClipboardFormat("PNG")
        html_format = cb.RegisterClipboardFormat("HTML Format")
        if any(cb.IsClipboardFormatAvailable(kind) for kind in (png_format, cb.CF_DIB, cb.CF_BITMAP, cb.CF_HDROP)):
            return None
        plain = cb.GetClipboardData(cb.CF_UNICODETEXT).strip() if cb.IsClipboardFormatAvailable(cb.CF_UNICODETEXT) else ""
        if plain:
            url = urlsplit(plain)
            return plain if url.scheme in ("http", "https") and url.hostname and Path(url.path).suffix.lower() in IMAGE_EXTENSIONS else None
        if cb.IsClipboardFormatAvailable(html_format):
            html = cb.GetClipboardData(html_format)
            if isinstance(html, bytes):
                html = html.decode("utf-8", errors="replace")
            match = re.search(r"""<img\b[^>]*\bsrc\s*=\s*["'](https?://[^"']+)["']""", html, re.I)
            if match:
                from html import unescape
                return unescape(match.group(1))
    return None


def split_command(command):
    shell = c.WinDLL("shell32")
    split = shell.CommandLineToArgvW
    split.argtypes = (w.LPCWSTR, c.POINTER(c.c_int))
    split.restype = c.POINTER(w.LPWSTR)
    count = c.c_int()
    array = split("mysnip " + command, c.byref(count))
    if not array:
        return []
    try:
        return [array[index] for index in range(1, count.value)]
    finally:
        free = c.WinDLL("kernel32").LocalFree
        free.argtypes = (w.HLOCAL,)
        free(array)


def excluded_window(window, rules):
    if not window or not rules.strip():
        return False
    import pywintypes
    import win32api
    import win32process
    try:
        pid = win32process.GetWindowThreadProcessId(window)[1]
        process = win32api.OpenProcess(0x1000, False, pid)
        try:
            path = win32process.GetModuleFileNameEx(process, 0).casefold()
        finally:
            process.Close()
    except pywintypes.error:
        return False
    name = Path(path).name
    return any(entry in (name, Path(name).stem) or ("\\" in entry and entry in path)
               for entry in (part.strip().replace("/", "\\").casefold() for part in re.split("[,;，]", rules)) if entry)


def parse_shortcut(value):
    parts = [part.strip().lower() for part in value.split("+")]
    modifiers = {"alt": 1, "ctrl": 2, "control": 2, "shift": 4, "win": 8, "windows": 8}
    flags = 0
    for part in parts[:-1]:
        bit = modifiers.get(part, 0)
        if not bit or flags & bit:
            raise ValueError(f"快捷鍵格式無效：{value}")
        flags |= bit
    key = parts[-1]
    if key.startswith("f") and key[1:].isdigit() and 1 <= int(key[1:]) <= 24:
        code = 0x6F + int(key[1:])
    elif len(key) == 1 and key.isascii() and key.isalnum() and flags & 11:
        code = ord(key.upper())
    else:
        raise ValueError(f"請使用功能鍵，或 Ctrl、Alt、Win 加上英數鍵：{value}")
    return flags | 0x4000, code


class Hotkeys:
    def __init__(self, deliver):
        self._deliver = deliver
        self._requests = queue.Queue()
        self._thread = threading.Thread(target=self._run, name="MySnip hotkeys", daemon=True)
        self._thread.start()

    def configure(self, bindings):
        parsed = {name: parse_shortcut(shortcut) for name, shortcut in bindings.items() if shortcut}
        if len(set(parsed.values())) != len(parsed):
            raise ValueError("快捷鍵不能重複。")
        result = queue.Queue(1)
        self._requests.put((parsed, result))
        try:
            return result.get(timeout=2)
        except queue.Empty as ex:
            raise OSError("無法初始化 Windows 快捷鍵。") from ex

    def close(self):
        self._requests.put((None, None))
        self._thread.join(timeout=0.5)

    def _run(self):
        message = w.MSG()
        active = {}
        try:
            while True:
                try:
                    parsed, result = self._requests.get(timeout=0.025)
                except queue.Empty:
                    pass
                else:
                    if parsed is None:
                        return
                    previous = active
                    for index in previous:
                        user32.UnregisterHotKey(None, index)
                    active, failures = self._register(parsed)
                    if failures and previous:
                        for index in active:
                            user32.UnregisterHotKey(None, index)
                        active, _ = self._register({name: gesture for name, gesture in previous.values()})
                    result.put(failures)
                while user32.PeekMessageW(c.byref(message), None, 0x0312, 0x0312, 1):
                    binding = active.get(message.wParam)
                    if binding:
                        self._deliver(binding[0])
        finally:
            for index in active:
                user32.UnregisterHotKey(None, index)

    @staticmethod
    def _register(bindings):
        active = {}
        failed = []
        for index, (name, gesture) in enumerate(bindings.items(), 1):
            if user32.RegisterHotKey(None, index, *gesture):
                active[index] = name, gesture
            else:
                failed.append(name)
        return active, failed
