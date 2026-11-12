"""MySnip's independent Python edition for Windows."""

from __future__ import annotations

from dataclasses import replace
import io
import os
from pathlib import Path
import queue
import re
import subprocess
import sys
import threading
import time
import winreg
from datetime import datetime
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk
from urllib.parse import urlsplit
from urllib.request import urlopen

from PIL import BmpImagePlugin, Image

from snip_automation import automation_bounds
from snip_capture import CaptureOverlay, intersection, ordered_box, valid_box
from snip_commands import CaptureCommand, custom_area, parse_paste
from snip_images import IMAGE_EXTENSIONS, RESAMPLE, StickerAsset, color_card, decorate, parse_color, quick_save, render_annotations, save_image, text_image
from snip_instance import Instance
from snip_recognition import decode, recognize
from snip_native import VirtualDesktops, WinDrag
from snip_stickers import StickerWindow
from snip_storage import CaptureHistory, Settings, StickerStorage, is_hex_color
from snip_tray import Tray
from snip_windows import (Hotkeys, capture_desktop, capture_window_rect, capture_windows, clipboard_contents, clipboard_image_url, copy_image,
                          copy_text, enable_dpi, excluded_window, hwnd, monitor_at, monitor_bounds, parse_shortcut,
                          position_window, print_image,
                          set_click_through, split_command, user32)

DEFAULT_GROUP = "預設"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE = "MySnip.Python"


def set_startup(enabled):
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, RUN_KEY, access=winreg.KEY_SET_VALUE) as key:
        if enabled:
            executable = Path(sys.executable)
            windowless = executable.with_name("pythonw.exe")
            launcher = os.environ.get("MYSNIP_SINGLE_FILE_EXE")
            if launcher and Path(launcher).is_file():
                command = subprocess.list2cmdline([launcher])
            else:
                command = subprocess.list2cmdline([str(windowless if windowless.is_file() else executable), str(Path(__file__).resolve())])
            winreg.SetValueEx(key, RUN_VALUE, 0, winreg.REG_SZ, command)
        else:
            try:
                winreg.DeleteValue(key, RUN_VALUE)
            except FileNotFoundError:
                pass


class MySnipApp:
    def __init__(self, directory=None, *, integrate=True):
        enable_dpi()
        self.directory = Path(directory or Path(os.environ.get("LOCALAPPDATA", Path.home())) / "MySnip")
        self.settings = Settings.load(self.directory)
        self.history = CaptureHistory(self.directory / "python-captures")
        self.storage = StickerStorage(self.directory / "python-stickers")
        self.root = tk.Tk()
        self.root.title("MySnip Python")
        self.root.withdraw()
        self._apply_theme()
        self.root.protocol("WM_DELETE_WINDOW", self.quit)
        self.actions = queue.Queue()
        self.stickers = []
        self.groups = [DEFAULT_GROUP]
        self.active_group = DEFAULT_GROUP
        self.previous_group = None
        self.all_hidden = False
        self.solo = False
        self._active_capture = False
        self._overlay = None
        self._saving = None
        self._quitting = False
        self._hotkeys = None
        self._tray = None
        self._fallback_status = None
        self._settings_window = None
        self._win_drag = None
        self._drag_preview = None
        self._drag_box = None
        self._corner = None
        self._corner_since = 0
        self._corner_fired = False
        self._monitors = []
        self._monitors_checked = 0
        self._foreground = user32.GetForegroundWindow()
        self._hotkeys_excluded = False
        self._hotkeys_enabled = True
        self._corners_enabled = True
        self._desktops = VirtualDesktops()
        self.active_desktop = self._desktops.current(self._foreground)
        self._restore_workspace()
        if integrate:
            self._start_integration()
        self.root.after(20, self._poll)

    def _apply_theme(self):
        dark = self.settings.theme == "dark"
        if self.settings.theme == "system":
            try:
                with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as key:
                    dark = winreg.QueryValueEx(key, "AppsUseLightTheme")[0] == 0
            except OSError:
                pass
        style = ttk.Style(self.root)
        style.theme_use("clam")
        style.configure(".", background="#292929" if dark else "#f0f0f0",
                        foreground="#f0f0f0" if dark else "#202020",
                        fieldbackground="#363636" if dark else "#ffffff")
        style.map(".", background=[("active", "#424242" if dark else "#e4e4e4")],
                  foreground=[("disabled", "#aaaaaa" if dark else "#777777")])
        self.root.option_add("*TCombobox*Listbox.background", "#363636" if dark else "#ffffff")
        self.root.option_add("*TCombobox*Listbox.foreground", "#f0f0f0" if dark else "#202020")
        for widget in ("Listbox", "Text", "Menu", "Toplevel"):
            self.root.option_add(f"*{widget}.background", "#292929" if dark else "#f0f0f0")
            if widget != "Toplevel":
                self.root.option_add(f"*{widget}.foreground", "#f0f0f0" if dark else "#202020")

    def run(self, arguments=()):
        if arguments:
            self.root.after_idle(lambda: self.run_command(arguments))
        self.root.mainloop()

    def _start_integration(self):
        self._hotkeys = Hotkeys(lambda action: self.actions.put(lambda: self._dispatch_hotkey(action)))
        try:
            failures = self._register_hotkeys(self.settings)
            if failures:
                self.root.after_idle(lambda: self._notify("部分快捷鍵被其他程式占用，請從設定更換。"))
        except (OSError, ValueError) as error:
            self.root.after_idle(lambda message=str(error): self._notify(message))
        items = [
            ("開始截圖", self.begin_capture), ("延遲截圖", lambda: self.begin_capture(delay=self.settings.capture_delay)),
            ("自訂截圖範圍…", self.custom_capture),
            ("截取全部螢幕", lambda: self.begin_capture(command=CaptureCommand(target="full"), action="clipboard")),
            ("重截上次範圍", self.repeat_capture), ("截圖歷史…", self.show_history), None,
            ("從剪貼簿貼圖", self.paste), ("顯示／隱藏貼圖", self.toggle_stickers),
            ("顯示已隱藏貼圖", self.show_stickers), ("取消滑鼠穿透", self.disable_click_through),
            ("貼圖群組…", self.show_groups), None, ("開啟截圖資料夾", self.open_output),
            ("設定…", self.show_settings), ("離開", self.quit),
        ]
        try:
            self._tray = Tray(items, self._tray_action, self.actions.put)
        except OSError:
            self._fallback_controls()
        self.configure_win_drag()

    def _tray_action(self, button):
        return {"menu": "menu", "capture": self.begin_capture, "paste": self.paste,
                "visibility": self.toggle_stickers, "settings": self.show_settings}.get(self.settings.tray_actions[button])

    def _fallback_controls(self):
        if self._fallback_status is not None:
            return
        panel = ttk.Frame(self.root, padding=10)
        panel.pack()
        for label, callback in (("截圖", self.begin_capture), ("貼圖", self.paste), ("歷史", self.show_history),
                                ("群組", self.show_groups), ("設定", self.show_settings)):
            ttk.Button(panel, text=label, command=callback).pack(side="left", padx=2)
        self._fallback_status = tk.StringVar(master=self.root)
        ttk.Label(self.root, textvariable=self._fallback_status, wraplength=350).pack(fill="x", padx=10, pady=(0, 8))
        self.root.deiconify()

    def _register_hotkeys(self, settings):
        if not self._hotkeys:
            return []
        self._hotkeys_excluded = excluded_window(self._foreground, settings.ignore_applications)
        if self._hotkeys_excluded or not self._hotkeys_enabled:
            return self._hotkeys.configure({})
        bindings = {"capture": settings.capture_hotkey, "paste": settings.paste_hotkey, "visibility": settings.visibility_hotkey}
        for index, (shortcut, _, exclusions) in enumerate(settings.command_hotkeys):
            if not excluded_window(self._foreground, exclusions):
                bindings[f"command:{index}"] = shortcut
        return self._hotkeys.configure(bindings)

    def _dispatch_hotkey(self, action):
        if excluded_window(user32.GetForegroundWindow(), self.settings.ignore_applications):
            return
        if action.startswith("command:"):
            index = int(action.split(":")[1])
            if index < len(self.settings.command_hotkeys):
                _, command, exclusions = self.settings.command_hotkeys[index]
                if not excluded_window(user32.GetForegroundWindow(), exclusions):
                    self.run_command(split_command(command), self.settings.output_directory)
        else:
            {"capture": self.begin_capture, "paste": self.paste, "visibility": self.toggle_stickers}[action]()

    def _poll(self):
        if self._quitting:
            return
        for _ in range(30):
            try:
                action = self.actions.get_nowait()
            except queue.Empty:
                break
            try:
                action()
            except (OSError, RuntimeError, ValueError, tk.TclError) as error:
                self._notify(str(error))
        if not self._quitting:
            try:
                self._poll_windows()
            except (OSError, RuntimeError, ValueError, tk.TclError) as error:
                self._notify(str(error))
            finally:
                if not self._quitting:
                    self.root.after(20, self._poll)

    def configure_win_drag(self):
        if self._win_drag and not self.settings.super_snip:
            self._win_drag.close()
            self._win_drag = None
            self._hide_drag_preview()
        elif self.settings.super_snip and not self._win_drag:
            try:
                self._win_drag = WinDrag(lambda region: self.actions.put(lambda: self._finish_win_drag(region)),
                                         lambda: self._hotkeys_enabled and not self._active_capture and not self._quitting
                                         and not self._hotkeys_excluded)
            except OSError as error:
                self._notify(str(error))

    def _finish_win_drag(self, region):
        self._hide_drag_preview()
        action = None if self.settings.super_output == "edit" else self.settings.super_output
        self.begin_capture(region=region, action=action)

    def _hide_drag_preview(self):
        if self._drag_preview and self._drag_box is not None:
            self._drag_preview.withdraw()
        self._drag_box = None

    def _poll_windows(self):
        foreground = user32.GetForegroundWindow()
        if foreground != self._foreground:
            self._foreground = foreground
            if self.settings.ignore_applications or any(binding[2] for binding in self.settings.command_hotkeys):
                self._register_hotkeys(self.settings)
            desktop = self._desktops.current(foreground)
            if desktop and desktop != self.active_desktop:
                self.active_desktop = desktop
                self.solo = False
                self.update_sticker_visibility()
        gesture = self._win_drag.gesture if self._win_drag else None
        if gesture and not self._active_capture:
            box = ordered_box(*gesture)
            if valid_box(box) and box != self._drag_box:
                if self._drag_preview is None:
                    self._drag_preview = tk.Toplevel(self.root)
                    self._drag_preview.withdraw()
                    self._drag_preview.overrideredirect(True)
                    self._drag_preview.attributes("-topmost", True)
                    self._drag_preview.attributes("-transparentcolor", "#010203")
                    tk.Frame(self._drag_preview, background="#010203", highlightbackground="#27a7e7",
                             highlightthickness=2).pack(fill="both", expand=True)
                if self._drag_box is None:
                    self._drag_preview.deiconify()
                    set_click_through(self._drag_preview, True)
                position_window(self._drag_preview, box[0], box[1], box[2] - box[0], box[3] - box[1])
                self._drag_box = box
        else:
            self._hide_drag_preview()
        self._poll_corners()

    def _poll_corners(self):
        if not self._corners_enabled or self._active_capture or all(action == "none" for action in self.settings.hot_corners):
            return
        now = time.monotonic()
        if now - self._monitors_checked > 1:
            self._monitors_checked = now
            try:
                _, self._monitors = monitor_bounds()
            except OSError:
                self._monitors = []
        if not self._monitors:
            return
        x, y = self.root.winfo_pointerxy()
        monitor = monitor_at((x, y), self._monitors)
        left, top = monitor["left"], monitor["top"]
        right, bottom = left + monitor["width"] - 1, top + monitor["height"] - 1
        index = next((i for i, (cx, cy) in enumerate(((left, top), (right, top), (left, bottom), (right, bottom)))
                      if abs(cx - x) <= 1 and abs(cy - y) <= 1), None)
        if index is None or self.settings.hot_corners[index] == "none":
            self._corner = None
            return
        current = left, top, index
        if current != self._corner:
            self._corner, self._corner_since, self._corner_fired = current, now, False
        elif not self._corner_fired and now - self._corner_since >= 0.35:
            self._corner_fired = True
            {"capture": self.begin_capture, "paste": self.paste, "visibility": self.toggle_stickers}[self.settings.hot_corners[index]]()

    def _notify(self, text):
        if self._quitting:
            return
        if self._tray:
            try:
                self._tray.notify(text, "MySnip Python")
                return
            except (OSError, RuntimeError):
                pass
        self._fallback_controls()
        self._fallback_status.set(text)

    def begin_capture(self, *, delay=0, region=None, action=None, command=None):
        if self._active_capture:
            return
        self._active_capture = True
        active = capture_window_rect(user32.GetForegroundWindow())
        self.root.after(round((command.delay if command else delay) * 1000),
                        lambda: self._hide_before_capture(active, region, action, command))

    def _hide_before_capture(self, active, region, action, command):
        if not self._active_capture:
            return
        active = capture_window_rect(user32.GetForegroundWindow()) or active
        self.update_sticker_visibility()
        self.root.after(115, lambda: self._capture(active, region, action, command))

    def _capture(self, active, region, action, command):
        try:
            image, bounds, monitors = capture_desktop(self.settings.include_cursor)
            desktop = bounds["left"], bounds["top"], bounds["left"] + bounds["width"], bounds["top"] + bounds["height"]
            target = command.target if command else None
            if command and command.outputs:
                action = "command"
            if target == "full":
                region = desktop
            elif target == "active-screen":
                cursor = self.root.winfo_pointerxy()
                screen = monitor_at(cursor, monitors)
                region = (screen["left"], screen["top"], screen["left"] + screen["width"], screen["top"] + screen["height"])
            elif target == "active-window":
                if active is None:
                    raise ValueError("找不到作用中的視窗。")
                region = active
            elif target == "area":
                region = command.region
            elif target == "size":
                width = min(command.region[2], bounds["width"])
                height = min(command.region[3], bounds["height"])
                x, y = self.root.winfo_pointerxy()
                x = min(max(x - width // 2, desktop[0]), desktop[2] - width)
                y = min(max(y - height // 2, desktop[1]), desktop[3] - height)
                region = x, y, x + width, y + height
            elif target == "last":
                record = self._last_capture()
                if record is None:
                    raise ValueError("沒有可重截的歷史範圍。")
                region = record["region"]
            if region:
                region = intersection(region, desktop)
                if not valid_box(region):
                    raise ValueError("選取範圍不在目前螢幕內。")
            auto_action = action if not command or not command.hold else None
            if region and auto_action:
                crop = (region[0] - desktop[0], region[1] - desktop[1], region[2] - desktop[0], region[3] - desktop[1])
                self._capture_done(decorate(image.crop(crop), shadow=bool(command and command.shadow)),
                                   action, region, command)
                return
            windows = capture_windows()
            self._overlay = CaptureOverlay(self.root, image, bounds, monitors,
                                           lambda result, selected_action, box: self._capture_done(result, selected_action, box, command),
                                           settings=self.settings, candidates=[(box, children) for _, box, children in windows],
                                           active_window=active,
                                           history=self.history, refresh=lambda: capture_desktop(self.settings.include_cursor),
                                           on_preferences=self.save_settings, on_recognize=self.recognize_selection, region=region,
                                           auto_action=auto_action, shadow=bool(command and command.shadow))
            if not region and windows:
                overlay = self._overlay

                def add_details(details):
                    if self._overlay is overlay:
                        overlay.update_candidates([
                            (box, list(dict.fromkeys(children + details.get(handle, []))))
                            for handle, box, children in windows
                        ])

                self._background(lambda: automation_bounds([handle for handle, _, _ in windows]), add_details)
        except (OSError, RuntimeError, ValueError, tk.TclError) as error:
            self._active_capture = False
            self.update_sticker_visibility()
            self._notify(f"無法開始截圖：{error}")

    def _capture_done(self, image, action, region, command=None):
        self._active_capture = False
        self._overlay = None
        self.update_sticker_visibility()
        if image is None or region is None:
            return
        try:
            saved = action in ("saved", "quick-save", "copy-file", "open")
            outputs = command.outputs if command and action != "canceled" else ()
            if action == "sticker" and ("sticker", None) not in outputs:
                self.create_sticker(StickerAsset.from_image(image), position=region[:2])
            elif action == "clipboard" and not outputs:
                file = quick_save(image, self.settings.output_directory) if self.settings.copy_as_file else None
                copy_image(image, file)
                saved = file is not None
            elif action == "copy-file":
                copy_image(image, quick_save(image, self.settings.output_directory))
            elif action == "print":
                print_image(image, hwnd(self.root))
            elif action in ("quick-save", "open"):
                file = quick_save(image, self.settings.output_directory)
                if action == "open":
                    os.startfile(str(file))
            for mode, path in outputs:
                saved |= self._command_output(image, region, mode, path, command.silent)
            self.history.add(image, region, self.settings.history_limit, canceled=action == "canceled")
            if self.settings.auto_save and not saved and action != "canceled" and not (command and command.skip_auto_save):
                quick_save(image, self.settings.output_directory)
        except (OSError, RuntimeError, ValueError) as error:
            self._notify(f"無法輸出截圖：{error}")

    def _command_output(self, image, region, mode, path, silent):
        if mode == "sticker":
            self.create_sticker(StickerAsset.from_image(image), position=region[:2])
        elif mode == "clipboard":
            copy_image(image)
        elif mode == "print":
            print_image(image, hwnd(self.root))
        elif mode in ("quick-save", "copy-file"):
            file = quick_save(image, self.settings.output_directory)
            if mode == "copy-file":
                copy_image(image, file)
            elif not silent:
                self._notify(f"已儲存 {file}")
            return True
        elif mode in ("ocr-clipboard", "barcode-clipboard"):
            text = (recognize if mode == "ocr-clipboard" else decode)(image)
            if text:
                copy_text(text)
            elif not silent:
                self._notify("選取區域中沒有辨識到內容。")
        elif mode in ("file-dialog", "path"):
            if mode == "file-dialog":
                path = filedialog.asksaveasfilename(parent=self.root, initialdir=self.settings.output_directory,
                                                    initialfile=f"MySnip-{datetime.now():%Y%m%d-%H%M%S}",
                                                    defaultextension=".png",
                                                    filetypes=(("PNG 圖片", "*.png"), ("JPEG 圖片", "*.jpg")))
            if path:
                save_image(image, path)
                return True
        return False

    def custom_capture(self, command=None):
        command = command or CaptureCommand()
        x, y = self.root.winfo_pointerxy()
        value = simpledialog.askstring("自訂截圖", "輸入 X Y 寬 高，可加上延遲秒數",
                                       initialvalue=f"{x} {y} 800 600", parent=self.root)
        if not value:
            return
        try:
            region, delay = custom_area(value, command.delay)
        except ValueError as error:
            self._notify(str(error))
            return
        self.begin_capture(command=replace(command, target="area", region=region, delay=delay))

    def _last_capture(self):
        return next((record for record in self.history.entries() if not record.get("canceled")), None)

    def repeat_capture(self, action="clipboard"):
        record = self._last_capture()
        if record is None:
            self._notify("沒有可重截的歷史範圍。")
        else:
            self.begin_capture(region=record["region"], action=action)

    def show_history(self):
        window = self._dialog("截圖歷史")
        records = self.history.entries()
        listing = tk.Listbox(window, height=10, width=55, exportselection=False)
        listing.pack(fill="both", expand=True, padx=12, pady=10)
        for entry in records:
            left, top, right, bottom = entry["region"]
            date = datetime.fromtimestamp(entry.get("time", 0)).strftime("%m/%d %H:%M:%S")
            listing.insert("end", f"{date}　{right-left} × {bottom-top}" + ("（已取消）" if entry.get("canceled") else ""))

        def use(callback):
            selected = listing.curselection()
            if selected:
                callback(records[selected[0]])

        panel = ttk.Frame(window, padding=8)
        panel.pack()
        for label, command in (("貼到桌面", lambda: use(lambda entry: self.create_sticker(StickerAsset.from_image(self.history.open(entry))))),
                               ("複製", lambda: use(lambda entry: copy_image(self.history.open(entry)))),
                               ("重截範圍", lambda: use(lambda entry: (window.destroy(), self.begin_capture(region=entry["region"])))),
                               ("清除歷史", lambda: (self.history.trim(0), window.destroy()))):
            ttk.Button(panel, text=label, command=command).pack(side="left", padx=2)

    def paste(self, position=None):
        if address := clipboard_image_url():
            self.load_sticker_url(address=address, position=position)
            return
        assets = clipboard_contents()
        if not assets:
            self._notify("剪貼簿沒有可貼上的圖片或文字。")
            return
        x, y = position or self.root.winfo_pointerxy()
        for index, asset in enumerate(assets):
            self.create_sticker(asset, position=(x + 12 + 20 * index, y + 12 + 20 * index))

    def clamp_sticker_position(self, position):
        x, y = position
        if type(x) is not int or type(y) is not int:
            x, y = self.root.winfo_pointerxy()
        try:
            _, monitors = monitor_bounds()
        except OSError:
            return x, y
        monitor = monitor_at((x, y), monitors)
        return (max(monitor["left"], min(x, monitor["left"] + monitor["width"] - 40)),
                max(monitor["top"], min(y, monitor["top"] + monitor["height"] - 40)))

    def create_sticker(self, asset, position=None):
        sticker = StickerWindow(self, asset, self.active_group, position=position)
        self.stickers.append(sticker)
        self.all_hidden = self.solo = False
        self.update_sticker_visibility()
        self.workspace_changed()
        return sticker

    def remove_sticker(self, sticker):
        if sticker in self.stickers:
            self.stickers.remove(sticker)
        if self.solo and not any(item.selected for item in self.stickers):
            self.solo = False
            self.update_sticker_visibility()
        self.workspace_changed()

    def update_sticker_visibility(self):
        for sticker in self.stickers:
            visible = (not self._active_capture and not self.all_hidden and not sticker.hidden
                       and sticker.group == self.active_group and (not self.solo or sticker.selected)
                       and (self.settings.follow_desktop or not self.active_desktop
                            or not sticker.desktop or sticker.desktop == self.active_desktop))
            sticker.set_visible(visible)
            if visible and self.active_desktop:
                self._desktops.move(hwnd(sticker.top), self.active_desktop)

    def toggle_stickers(self):
        self.all_hidden = not self.all_hidden
        self.update_sticker_visibility()
        self.workspace_changed()

    def show_stickers(self):
        self.all_hidden = self.solo = False
        for sticker in self.stickers:
            if sticker.group == self.active_group:
                sticker.hidden = False
        self.update_sticker_visibility()
        self.workspace_changed()

    def disable_click_through(self):
        for sticker in self.stickers:
            sticker.click_through = False
            if sticker.visible:
                set_click_through(sticker.top, False)
        self.show_stickers()

    def targets(self, sticker):
        return [item for item in self.stickers if item.selected and item.group == sticker.group] if sticker.selected else [sticker]

    def toggle_solo(self, sticker):
        if not self.solo and not sticker.selected:
            for item in self.stickers:
                item.selected = item is sticker
                if item.visible:
                    item.render()
        self.solo = not self.solo
        self.update_sticker_visibility()

    def switch_group(self, group):
        if group not in self.groups:
            return
        if group != self.active_group:
            self.previous_group = self.active_group
        self.active_group = group
        self.all_hidden = self.solo = False
        for sticker in self.stickers:
            sticker.selected = False
        self.update_sticker_visibility()
        self.workspace_changed()

    def switch_to_next_group(self):
        self.switch_group(self.groups[(self.groups.index(self.active_group) + 1) % len(self.groups)])

    def switch_to_previous_group(self):
        if self.previous_group in self.groups:
            self.switch_group(self.previous_group)
        else:
            self.switch_to_next_group()

    def empty_group(self):
        for sticker in tuple(self.stickers):
            if sticker.group == self.active_group:
                sticker.destroy()

    def toggle_click_through(self):
        x, y = self.root.winfo_pointerxy()
        sticker = next((item for item in reversed(self.stickers)
                        if item.visible and item.x <= x < item.x + item.top.winfo_width()
                        and item.y <= y < item.y + item.top.winfo_height()), None)
        if sticker:
            sticker.change("click_through")
        else:
            self.disable_click_through()

    def create_group(self, name=None, parent=None):
        if name is None:
            name = simpledialog.askstring("新增群組", "輸入群組名稱", parent=parent or self.root)
        if name is None or not self._new_group_name(name):
            return
        self.groups.append(name)
        self.switch_group(name)

    def _new_group_name(self, name):
        return bool(name.strip()) and len(name) <= 40 and all(group.casefold() != name.casefold() for group in self.groups)

    def move_to_group(self, sticker, group):
        for item in self.targets(sticker):
            item.group, item.selected = group, False
        self.solo = False
        self.update_sticker_visibility()
        self.workspace_changed()

    def show_groups(self):
        window = self._dialog("貼圖群組")
        listing = tk.Listbox(window, height=8, width=42, exportselection=False)
        listing.pack(fill="both", expand=True, padx=12, pady=10)

        def refresh():
            listing.delete(0, "end")
            for name in self.groups:
                listing.insert("end", name)
            listing.selection_set(self.groups.index(self.active_group))

        def selected():
            return self.groups[listing.curselection()[0]] if listing.curselection() else self.active_group

        def edit(rename):
            if not rename:
                self.create_group(parent=window)
                refresh()
                return
            old = selected()
            if old == DEFAULT_GROUP:
                return
            name = simpledialog.askstring("群組名稱", "輸入群組名稱", initialvalue=old, parent=window)
            if not name or not self._new_group_name(name):
                return
            self.groups[self.groups.index(old)] = name
            for sticker in self.stickers:
                if sticker.group == old:
                    sticker.group = name
            self.switch_group(name)
            refresh()

        def remove():
            group = selected()
            if group == DEFAULT_GROUP:
                return
            self.groups.remove(group)
            for sticker in self.stickers:
                if sticker.group == group:
                    sticker.group = DEFAULT_GROUP
            self.switch_group(DEFAULT_GROUP)
            refresh()

        panel = ttk.Frame(window, padding=8)
        panel.pack()
        for label, command in (("切換", lambda: self.switch_group(selected())), ("新增", lambda: edit(False)),
                               ("重新命名", lambda: edit(True)), ("移除", remove),
                               ("排序", lambda: (self.groups.sort(key=lambda name: (name != DEFAULT_GROUP, name.casefold())),
                                                  refresh(), self.workspace_changed())),
                               ("移至目前桌面", lambda: self.bring_group(selected()))):
            ttk.Button(panel, text=label, command=command).pack(side="left", padx=2)
        refresh()

    def bring_group(self, group):
        for sticker in self.stickers:
            if sticker.group == group:
                sticker.desktop = self.active_desktop
        self.update_sticker_visibility()
        self.workspace_changed()

    def edit_sticker(self, sticker):
        if self._active_capture or sticker.asset.animated:
            return
        source = sticker.asset.frames[0]
        x, y = self.clamp_sticker_position((sticker.x, sticker.y))
        _, monitors = monitor_bounds()
        monitor = monitor_at((x, y), monitors)
        preview = source.copy()
        preview.thumbnail((monitor["width"], max(1, monitor["height"] - 100)), RESAMPLE.LANCZOS)
        x = max(monitor["left"], min(x, monitor["left"] + monitor["width"] - preview.width))
        y = max(monitor["top"], min(y, monitor["top"] + monitor["height"] - preview.height - 80))
        bounds = monitor.copy()
        image = Image.new("RGBA", (bounds["width"], bounds["height"]))
        offset_x, offset_y = x - bounds["left"], y - bounds["top"]
        image.paste(preview, (offset_x, offset_y))
        self._active_capture = True
        self.update_sticker_visibility()

        def completed(result, action, region):
            document = self._overlay.document
            self._active_capture = False
            self._overlay = None
            self.update_sticker_visibility()
            if result is not None:
                if action == "apply" and not sticker.closed:
                    sx, sy = source.width / preview.width, source.height / preview.height
                    annotations = [
                        replace(item, points=tuple((round((ax - offset_x) * sx), round((ay - offset_y) * sy))
                                                   for ax, ay in item.points), width=max(1, round(item.width * sx)),
                                font_size=max(1, round(item.font_size * sy)), outline_width=round(item.outline_width * sx))
                        for item in document.items
                    ]
                    result = render_annotations(source, (0, 0, *source.size), annotations)
                    sticker.replace(StickerAsset((result,)))
                elif action != "saved":
                    self._capture_done(result, action, region)

        try:
            self._overlay = CaptureOverlay(self.root, image, bounds, [monitor], completed, settings=self.settings,
                                           on_preferences=self.save_settings, on_recognize=self.recognize_selection,
                                           region=(x, y, x + preview.width, y + preview.height),
                                           editing=True)
        except (OSError, tk.TclError):
            self._active_capture = False
            self.update_sticker_visibility()
            raise

    def drop_on_sticker(self, sticker, data):
        if sticker.closed:
            return
        try:
            if data.get("files"):
                assets = [StickerAsset.load(path) for path in data["files"] if Path(path).suffix.lower() in IMAGE_EXTENSIONS]
                if assets:
                    sticker.replace(assets[0])
                    for asset in assets[1:]:
                        self.create_sticker(asset)
            elif data.get("png") or data.get("dib"):
                if data.get("png"):
                    image = Image.open(io.BytesIO(data["png"]))
                else:
                    image = BmpImagePlugin.DibImageFile(io.BytesIO(data["dib"]))
                with image:
                    sticker.replace(StickerAsset.from_image(image))
            else:
                from html import unescape
                from snip_windows import plain_html
                text = data.get("text", "").strip()
                address = data.get("url") or text
                html = data.get("html", b"")
                match = re.search(rb"""<img\b[^>]*\bsrc\s*=\s*["'](https?://[^"']+)["']""", html, re.I)
                url = urlsplit(address)
                if match:
                    self.load_sticker_url(sticker, unescape(match[1].decode("utf-8", errors="replace")))
                elif url.scheme in ("http", "https") and Path(url.path).suffix.lower() in IMAGE_EXTENSIONS:
                    self.load_sticker_url(sticker, address)
                elif color := parse_color(text):
                    sticker.replace(color_card(color))
                elif content := plain_html(html) or text or address:
                    sticker.replace(StickerAsset.from_image(text_image(content)))
        except (OSError, RuntimeError, ValueError) as error:
            self._notify(f"無法開啟拖放的內容：{error}")

    def load_sticker_url(self, sticker=None, address=None, position=None):
        address = address or simpledialog.askstring("網頁圖片", "輸入 HTTP 或 HTTPS 圖片網址",
                                                     parent=sticker.top if sticker else self.root)
        if not address:
            return
        address = address.strip()
        url = urlsplit(address)
        if url.scheme not in ("http", "https") or not url.hostname or url.username or url.password:
            self._notify("請輸入有效的 HTTP 或 HTTPS 網址。")
            return

        def load():
            with urlopen(address, timeout=15) as response:
                data = response.read(24 * 1024 * 1024 + 1)
                if len(data) > 24 * 1024 * 1024:
                    raise ValueError("圖片超過 24 MiB。")
                with Image.open(io.BytesIO(data)) as image:
                    return StickerAsset.from_image(image)

        def apply(asset):
            if sticker is None:
                self.create_sticker(asset, position=position)
            elif not sticker.closed:
                sticker.replace(asset)

        self._background(load, apply)

    def _background(self, action, on_success):
        def run():
            try:
                result = action()
                self.actions.put(lambda: on_success(result))
            except Exception as error:
                self.actions.put(lambda message=str(error): self._notify(message))
        threading.Thread(target=run, daemon=True).start()

    def recognize_selection(self, overlay, image, codes):
        def show(text):
            if self._overlay is overlay:
                self._show_recognition(text, overlay.top)
        self._background(lambda: (decode if codes else recognize)(image), show)

    def _show_recognition(self, result, parent):
        if not result:
            self._notify("選取區域中沒有辨識到結果。")
            return
        window = self._dialog("辨識結果")
        window.transient(parent)
        text = tk.Text(window, width=62, height=10)
        text.pack(fill="both", expand=True, padx=10, pady=10)
        text.insert("1.0", result)
        ttk.Button(window, text="複製", command=lambda: (copy_text(text.get("1.0", "end-1c")), window.destroy())).pack(pady=6)
        window.update_idletasks()
        window.grab_set()

    def _restore_workspace(self):
        if not self.settings.restore_stickers:
            return
        saved = self.storage.open()
        groups = saved.get("groups")
        if isinstance(groups, list):
            self.groups = list(dict.fromkeys([DEFAULT_GROUP, *(item for item in groups if isinstance(item, str) and item)]))
        if saved.get("active") in self.groups:
            self.active_group = saved["active"]
        self.all_hidden = bool(saved.get("hidden", False))
        layouts = saved.get("stickers")
        if not isinstance(layouts, list):
            return
        for layout in layouts:
            if not isinstance(layout, dict) or (path := self.storage.path(layout.get("file"))) is None:
                continue
            try:
                asset = StickerAsset.load(path)
                asset.color = layout.get("color")
                group = layout.get("group")
                self.stickers.append(StickerWindow(self, asset, group if group in self.groups else DEFAULT_GROUP, layout))
            except (OSError, RuntimeError, ValueError, tk.TclError):
                continue
        self.update_sticker_visibility()

    def workspace_changed(self):
        if self._quitting or not self.settings.restore_stickers:
            return
        if self._saving:
            self.root.after_cancel(self._saving)
        self._saving = self.root.after(400, self.save_workspace)

    def save_workspace(self):
        if self._saving:
            self.root.after_cancel(self._saving)
            self._saving = None
        if self.settings.restore_stickers:
            try:
                self.storage.save(self.groups, self.active_group, self.all_hidden, self.stickers)
            except (OSError, ValueError) as error:
                self._notify(f"無法備份貼圖：{error}")

    def save_settings(self):
        try:
            self.settings.save(self.directory)
        except OSError as error:
            self._notify(f"無法儲存設定：{error}")

    def _dialog(self, title):
        window = tk.Toplevel(self.root)
        window.title(title)
        window.attributes("-topmost", True)
        return window

    def show_settings(self):
        if self._settings_window and self._settings_window.winfo_exists():
            self._settings_window.lift()
            return
        window = self._settings_window = self._dialog("MySnip Python 設定")
        panel = ttk.Frame(window, padding=12)
        panel.pack(fill="both", expand=True)
        fields = (
            ("截圖快捷鍵", "capture_hotkey"), ("貼圖快捷鍵", "paste_hotkey"), ("顯示／隱藏快捷鍵", "visibility_hotkey"),
            ("預設輸出資料夾", "output_directory"), ("延遲秒數", "capture_delay"), ("歷史筆數", "history_limit"),
            ("放大鏡尺寸", "magnifier_size"), ("畫筆粗細預設（逗號分隔）", "pen_sizes"),
            ("標註預設色彩（#RRGGBB）", "tool_color"), ("標註預設粗細", "tool_width"),
            ("標註預設不透明度（%）", "tool_opacity"),
        )
        values = {}
        for index, (label, key) in enumerate(fields):
            ttk.Label(panel, text=label).grid(row=index, column=0, sticky="w", padx=3, pady=3)
            value = getattr(self.settings, key)
            values[key] = tk.StringVar(master=window, value=", ".join(map(str, value)) if isinstance(value, tuple) else value)
            ttk.Entry(panel, textvariable=values[key], width=40).grid(row=index, column=1, padx=3, pady=3)
        ttk.Button(panel, text="瀏覽…", command=lambda: values["output_directory"].set(
            filedialog.askdirectory(parent=window) or values["output_directory"].get())).grid(row=3, column=2)
        checks = (
            ("自動儲存成功截圖", "auto_save"), ("截圖包含滑鼠游標", "include_cursor"),
            ("啟動時還原貼圖", "restore_stickers"), ("記錄取消的截圖", "record_canceled"),
            ("選取前顯示放大鏡", "show_magnifier"), ("記住各工具顏色", "remember_colors"),
            ("複製圖片時同時複製 PNG 檔案", "copy_as_file"),
            ("登入 Windows 時啟動 MySnip Python", "start_with_windows"),
        )
        for index, (label, key) in enumerate(checks, len(fields)):
            values[key] = tk.BooleanVar(master=window, value=getattr(self.settings, key))
            ttk.Checkbutton(panel, text=label, variable=values[key]).grid(row=index, column=0, columnspan=2, sticky="w", pady=2)

        def apply():
            current = {key: value.get() for key, value in values.items()}
            try:
                current["capture_delay"] = float(current["capture_delay"])
                current["history_limit"] = int(current["history_limit"])
                current["magnifier_size"] = int(current["magnifier_size"])
                current["pen_sizes"] = tuple(int(value) for value in current["pen_sizes"].split(","))
                current["tool_width"] = int(current["tool_width"])
                current["tool_opacity"] = int(current["tool_opacity"])
                if (not is_hex_color(current["tool_color"]) or not 1 <= current["tool_width"] <= 32
                        or not 10 <= current["tool_opacity"] <= 100):
                    raise ValueError("請檢查標註預設色彩、粗細（1–32）和不透明度（10–100）。")
                if (not 0 <= current["capture_delay"] <= 60 or not 0 <= current["history_limit"] <= 100
                        or not 72 <= current["magnifier_size"] <= 320 or not 1 <= len(current["pen_sizes"]) <= 5
                        or not all(1 <= value <= 32 for value in current["pen_sizes"]) or not current["output_directory"].strip()):
                    raise ValueError("請檢查延遲、歷史筆數、放大鏡尺寸、畫筆粗細及輸出路徑。")
                candidate = replace(self.settings, **current)
                for name in ("capture_hotkey", "paste_hotkey"):
                    parse_shortcut(current[name])
                failures = self._register_hotkeys(candidate)
                if failures:
                    raise ValueError("部分快捷鍵被占用。")
                startup_changed = candidate.start_with_windows != self.settings.start_with_windows
                if startup_changed:
                    set_startup(candidate.start_with_windows)
                try:
                    candidate.save(self.directory)
                except OSError:
                    if startup_changed:
                        set_startup(self.settings.start_with_windows)
                    raise
            except (OSError, ValueError) as error:
                self._register_hotkeys(self.settings)
                messagebox.showerror("MySnip", str(error), parent=window)
                return
            self.settings = candidate
            self.history.trim(candidate.history_limit)
            if candidate.restore_stickers:
                self.save_workspace()
            else:
                self.storage.clear()
                for sticker in self.stickers:
                    sticker.backup_name = None
            window.destroy()

        ttk.Button(panel, text="Windows 進階設定…", command=self.show_advanced_settings).grid(
            row=len(fields) + len(checks), column=0, sticky="w", pady=8)
        ttk.Button(panel, text="儲存", command=apply).grid(row=len(fields) + len(checks), column=1, sticky="e", pady=8)

    def show_advanced_settings(self):
        window = self._dialog("MySnip Windows 進階設定")
        panel = ttk.Frame(window, padding=12)
        panel.pack()
        enabled = tk.BooleanVar(master=window, value=self.settings.super_snip)
        follow = tk.BooleanVar(master=window, value=self.settings.follow_desktop)
        ttk.Checkbutton(panel, text="啟用 Win＋滑鼠左鍵拖曳快速截圖", variable=enabled).grid(row=0, columnspan=2, sticky="w")
        ttk.Checkbutton(panel, text="貼圖跟隨目前的虛擬桌面", variable=follow).grid(row=1, columnspan=2, sticky="w")
        outputs = {"clipboard": "複製", "sticker": "貼圖", "edit": "進入編輯"}
        modes = {"none": "停用", "capture": "開始截圖", "paste": "貼圖", "visibility": "顯示／隱藏貼圖"}

        def choose(row, label, choices, value):
            ttk.Label(panel, text=label).grid(row=row, column=0, sticky="w", pady=3)
            combo = ttk.Combobox(panel, values=list(choices.values()), state="readonly")
            combo.set(choices[value])
            combo.grid(row=row, column=1, pady=3)
            return combo

        output = choose(2, "快速截圖完成後", outputs, self.settings.super_output)
        corners = [choose(index + 3, label, modes, self.settings.hot_corners[index])
                   for index, label in enumerate(("左上角", "右上角", "左下角", "右下角"))]
        tray_modes = {**modes, "menu": "開啟選單", "settings": "開啟設定"}
        tray = [choose(index + 7, label, tray_modes, self.settings.tray_actions[index])
                for index, label in enumerate(("系統匣左鍵", "系統匣中鍵", "系統匣右鍵"))]
        ttk.Label(panel, text="排除全域快捷鍵的程式").grid(row=10, column=0, sticky="w")
        ignore = ttk.Entry(panel, width=34)
        ignore.insert(0, self.settings.ignore_applications)
        ignore.grid(row=10, column=1, pady=3)
        ttk.Label(panel, text="自訂快捷鍵（每列 快捷鍵 = 命令，可加 | 排除程式）").grid(row=11, columnspan=2, sticky="w")
        commands = tk.Text(panel, width=60, height=4)
        commands.insert("1.0", "\n".join(f"{key} = {cmd}" + (f" | {excluded}" if excluded else "")
                                         for key, cmd, excluded in self.settings.command_hotkeys))
        commands.grid(row=12, columnspan=2, pady=3)
        themes = {"system": "跟隨 Windows", "light": "淺色", "dark": "深色"}
        escapes = {"hide": "隱藏貼圖", "close": "關閉貼圖", "disable_click_through": "取消所有穿透", "none": "無動作"}
        theme = choose(13, "主題", themes, self.settings.theme)
        escape = choose(14, "貼圖 Esc", escapes, self.settings.sticker_escape)
        shift_escape = choose(15, "貼圖 Shift+Esc", escapes, self.settings.sticker_shift_escape)
        magnifier = ttk.Frame(panel)
        magnifier.grid(row=16, columnspan=2, sticky="w")
        radius = tk.StringVar(master=window, value=self.settings.magnifier_radius)
        background = tk.StringVar(master=window, value=self.settings.magnifier_background)
        crosshair = tk.BooleanVar(master=window, value=self.settings.magnifier_crosshair)
        ttk.Label(magnifier, text="放大鏡取樣半徑").pack(side="left", padx=3)
        ttk.Spinbox(magnifier, from_=2, to=25, textvariable=radius, width=4).pack(side="left")
        ttk.Label(magnifier, text="背景色").pack(side="left", padx=(8, 2))
        ttk.Entry(magnifier, textvariable=background, width=9).pack(side="left")
        ttk.Checkbutton(magnifier, text="十字線", variable=crosshair).pack(side="left", padx=4)
        hide_for_save = tk.BooleanVar(master=window, value=self.settings.hide_capture_for_save)
        select_annotation = tk.BooleanVar(master=window, value=self.settings.select_new_annotation)
        ttk.Checkbutton(panel, text="另存時隱藏截圖介面", variable=hide_for_save).grid(row=17, sticky="w")
        ttk.Checkbutton(panel, text="自動選取新標註", variable=select_annotation).grid(row=17, column=1, sticky="w")

        def apply():
            bindings = []
            known = {parse_shortcut(key) for key in (self.settings.capture_hotkey, self.settings.paste_hotkey,
                                                     self.settings.visibility_hotkey) if key}
            try:
                for line in commands.get("1.0", "end-1c").splitlines():
                    if not line.strip():
                        continue
                    shortcut, equal, rest = line.partition("=")
                    command, _, excluded = rest.partition("|")
                    if not equal or not split_command(command):
                        raise ValueError(f"請輸入快捷鍵與命令：{line}")
                    gesture = parse_shortcut(shortcut.strip())
                    if gesture in known:
                        raise ValueError(f"快捷鍵重複：{shortcut}")
                    known.add(gesture)
                    bindings.append((shortcut.strip(), command.strip(), excluded.strip()))
                if len(bindings) > 64:
                    raise ValueError("最多可設定 64 組命令快捷鍵。")
                sample_radius = int(radius.get())
                if not 2 <= sample_radius <= 25 or not is_hex_color(background.get()):
                    raise ValueError("放大鏡半徑須在 2–25 之間，背景色格式為 #RRGGBB。")
                options = replace(self.settings, super_snip=enabled.get(), follow_desktop=follow.get(),
                                  super_output=next(key for key, label in outputs.items() if label == output.get()),
                                  hot_corners=tuple(next(key for key, label in modes.items() if label == combo.get()) for combo in corners),
                                  tray_actions=tuple(next(key for key, label in tray_modes.items() if label == combo.get()) for combo in tray),
                                  ignore_applications=ignore.get().strip(), command_hotkeys=tuple(bindings),
                                  theme=next(key for key, label in themes.items() if label == theme.get()),
                                  sticker_escape=next(key for key, label in escapes.items() if label == escape.get()),
                                  sticker_shift_escape=next(key for key, label in escapes.items() if label == shift_escape.get()),
                                  magnifier_radius=sample_radius, magnifier_background=background.get(),
                                  magnifier_crosshair=crosshair.get(), hide_capture_for_save=hide_for_save.get(),
                                  select_new_annotation=select_annotation.get())
                if self._register_hotkeys(options):
                    raise ValueError("部分命令快捷鍵被占用。")
            except (OSError, ValueError) as error:
                self._register_hotkeys(self.settings)
                messagebox.showerror("MySnip", str(error), parent=window)
                return
            self.settings = options
            self.save_settings()
            self._apply_theme()
            self.configure_win_drag()
            self.update_sticker_visibility()
            window.destroy()

        ttk.Button(panel, text="套用", command=apply).grid(row=18, column=1, sticky="e", pady=6)

    def open_output(self):
        path = Path(self.settings.output_directory)
        path.mkdir(parents=True, exist_ok=True)
        os.startfile(str(path))

    def run_command(self, args, working_directory=None):
        try:
            self._run_command(args, working_directory)
        except (OSError, RuntimeError, ValueError, tk.TclError) as error:
            self._notify(str(error))

    def set_hotkeys_enabled(self, enabled):
        self._hotkeys_enabled = enabled
        if self._register_hotkeys(self.settings):
            self._notify("部分快捷鍵被其他程式占用。")

    def set_corners_enabled(self, enabled):
        self._corners_enabled = enabled
        self._corner = None

    def _run_command(self, args, working_directory):
        if not args:
            self.show_settings()
            return
        directory = Path(working_directory or Path.cwd())
        command = args[0]
        if command == "snip":
            capture = CaptureCommand.parse(args[1:], directory)
            if capture.target == "custom":
                self.custom_capture(capture)
            else:
                self.begin_capture(command=capture)
        elif command == "paste":
            source, payload, position = parse_paste(args[1:])
            if source == "--files":
                for path in payload:
                    self.create_sticker(StickerAsset.load(directory / path), position=position)
            elif source in ("--plain", "--html"):
                from snip_windows import plain_html
                value = payload[0] if source == "--plain" else plain_html(payload[0])
                self.create_sticker(StickerAsset((text_image(value),)), position=position)
            else:
                self.paste(position)
        elif command == "switch-group" and len(args) in (1, 2):
            if len(args) == 2:
                self.switch_group(args[1])
            else:
                self.switch_to_next_group()
        elif command == "create-group" and len(args) in (1, 2):
            self.create_group(args[1] if len(args) == 2 else None)
        else:
            commands = {"toggle-images": self.toggle_stickers, "show-images": self.show_stickers,
                        "hide-images": lambda: self.toggle_stickers() if not self.all_hidden else None,
                        "no-click-through": self.disable_click_through, "open-preferences": self.show_settings,
                        "toggle-click-through": self.toggle_click_through, "empty-group": self.empty_group,
                        "clear-snip-history": lambda: self.history.trim(0), "show-group-manager": self.show_groups,
                        "show-capture-history": self.show_history,
                        "switch-group-prev": self.switch_to_previous_group,
                        "show-tray-menu": lambda: self._tray.show_menu() if self._tray else self.root.lift(),
                        "enable-hotkeys": lambda: self.set_hotkeys_enabled(True),
                        "disable-hotkeys": lambda: self.set_hotkeys_enabled(False),
                        "enable-hot-corners": lambda: self.set_corners_enabled(True),
                        "disable-hot-corners": lambda: self.set_corners_enabled(False),
                        "exit": self.quit}
            if command not in commands or len(args) != 1:
                raise ValueError(f"不支援的 MySnip Python 命令：{command}")
            commands[command]()

    def quit(self):
        if self._quitting:
            return
        self._quitting = True
        self.save_workspace()
        if self._win_drag:
            self._win_drag.close()
        if self._overlay and self._overlay._share:
            self._overlay._share.close()
        for sticker in tuple(self.stickers):
            sticker.destroy()
        self._desktops.close()
        if self._hotkeys:
            self._hotkeys.close()
        if self._tray:
            self._tray.stop()
        self.root.destroy()


def main(arguments):
    directory = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "MySnip"
    instance = Instance(directory)
    try:
        if not instance.primary:
            instance.forward(arguments)
            return
        app = MySnipApp(directory)
        instance.listen(lambda args, cwd: app.actions.put(lambda: app.run_command(args, cwd)))
        app.run(arguments)
    finally:
        instance.close()


if __name__ == "__main__":
    main(sys.argv[1:])
