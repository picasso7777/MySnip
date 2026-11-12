"""Native notification-area icon with configurable mouse actions."""

import ctypes as c
from ctypes import wintypes as w
import os
from pathlib import Path
import queue
import threading

import pywintypes
import win32api
import win32con
import win32gui

from snip_native import Guid


class NotifyIconData(c.Structure):
    _fields_ = [("size", w.DWORD), ("window", w.HWND), ("id", w.UINT), ("flags", w.UINT),
                ("message", w.UINT), ("icon", w.HICON), ("tip", w.WCHAR * 128),
                ("state", w.DWORD), ("state_mask", w.DWORD), ("info", w.WCHAR * 256),
                ("timeout_or_version", w.UINT), ("info_title", w.WCHAR * 64), ("info_flags", w.DWORD),
                ("guid", Guid), ("balloon_icon", w.HICON)]


class Tray:
    _callback = 0x8008
    _notification = 0x8009
    _show_menu = 0x800A

    def __init__(self, items, action, dispatch):
        self._items, self._action, self._dispatch = items, action, dispatch
        self._ready = threading.Event()
        self._messages = queue.Queue()
        self._window = None
        self._data = None
        self._closed = False
        self._added = False
        self._shell = c.WinDLL("shell32")
        self._shell.Shell_NotifyIconW.argtypes = (w.DWORD, c.POINTER(NotifyIconData))
        self._shell.Shell_NotifyIconW.restype = w.BOOL
        self._taskbar = win32gui.RegisterWindowMessage("TaskbarCreated")
        self._thread = threading.Thread(target=self._run, name="MySnip tray", daemon=True)
        self._thread.start()
        if not self._ready.wait(2) or not self._added:
            self.stop()
            raise OSError("Windows 系統匣目前無法使用。")

    def _run(self):
        wc = win32gui.WNDCLASS()
        wc.hInstance = win32api.GetModuleHandle(None)
        wc.lpszClassName = f"MySnip.Python.Tray.{os.getpid()}"
        wc.lpfnWndProc = self._message
        icon = None
        own_icon = False
        try:
            win32gui.RegisterClass(wc)
            self._window = win32gui.CreateWindow(wc.lpszClassName, "", win32con.WS_OVERLAPPED,
                                                 0, 0, 0, 0, 0, 0, wc.hInstance, None)
            icon_path = Path(__file__).resolve().parent.parent / "assets" / "MySnip.ico"
            if icon_path.is_file():
                icon = win32gui.LoadImage(0, str(icon_path), win32con.IMAGE_ICON, 0, 0,
                                          win32con.LR_LOADFROMFILE | win32con.LR_DEFAULTSIZE)
                own_icon = True
            else:
                icon = win32gui.LoadIcon(0, win32con.IDI_APPLICATION)
            self._data = NotifyIconData(size=c.sizeof(NotifyIconData), window=self._window,
                                        id=1, flags=7, message=self._callback, icon=icon, tip="MySnip Python")
            self._added = bool(self._shell.Shell_NotifyIconW(0, c.byref(self._data)))
            self._ready.set()
            if self._added and not self._closed:
                win32gui.PumpMessages()
        except (OSError, pywintypes.error):
            pass
        finally:
            if self._data is not None and self._added:
                self._shell.Shell_NotifyIconW(2, c.byref(self._data))
            if self._window and win32gui.IsWindow(self._window):
                win32gui.DestroyWindow(self._window)
            if icon and own_icon:
                win32gui.DestroyIcon(icon)
            self._ready.set()

    def _message(self, window, message, wparam, lparam):
        if message == self._callback:
            button = {0x0202: 0, 0x0208: 1, 0x0205: 2}.get(lparam)
            if button is not None:
                action = self._action(button)
                if action == "menu":
                    self._popup(window)
                elif action is not None:
                    self._dispatch(action)
            return 0
        if message == self._notification:
            while not self._messages.empty():
                text, title = self._messages.get_nowait()
                data = NotifyIconData(size=c.sizeof(NotifyIconData), window=window, id=1, flags=0x10,
                                      info=text[:250], info_title=title[:60], info_flags=1)
                self._shell.Shell_NotifyIconW(1, c.byref(data))
            return 0
        if message == self._show_menu:
            self._popup(window)
            return 0
        if message == self._taskbar and self._data:
            self._shell.Shell_NotifyIconW(0, c.byref(self._data))
            return 0
        if message == win32con.WM_CLOSE:
            win32gui.PostQuitMessage(0)
            return 0
        return win32gui.DefWindowProc(window, message, wparam, lparam)

    def _popup(self, window):
        menu = win32gui.CreatePopupMenu()
        try:
            for index, item in enumerate(self._items, 1):
                if item is None:
                    win32gui.AppendMenu(menu, win32con.MF_SEPARATOR, 0, "")
                else:
                    win32gui.AppendMenu(menu, win32con.MF_STRING, index, item[0])
            x, y = win32gui.GetCursorPos()
            try:
                win32gui.SetForegroundWindow(window)
            except pywintypes.error:
                pass
            selected = win32gui.TrackPopupMenu(menu, 0x0102, x, y, 0, window, None)
            if selected and self._items[selected - 1]:
                self._dispatch(self._items[selected - 1][1])
            win32gui.PostMessage(window, win32con.WM_NULL, 0, 0)
        finally:
            win32gui.DestroyMenu(menu)

    def notify(self, text, title):
        if self._window:
            self._messages.put((text, title))
            try:
                win32gui.PostMessage(self._window, self._notification, 0, 0)
            except pywintypes.error as error:
                raise OSError("Windows 系統匣目前無法使用。") from error

    def show_menu(self):
        if self._window:
            win32gui.PostMessage(self._window, self._show_menu, 0, 0)

    def stop(self):
        self._closed = True
        if self._window and win32gui.IsWindow(self._window):
            win32gui.PostMessage(self._window, win32con.WM_CLOSE, 0, 0)
        self._thread.join(timeout=0.5)
