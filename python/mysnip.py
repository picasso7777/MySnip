"""
MySnip - Python Edition
A lightweight screenshot & sticker tool for Windows.

Hotkeys:
  F1 - Capture screenshot (region selection)
  F3 - Paste clipboard image as sticker
  Esc - Cancel capture / close sticker
"""

import io
import os
import sys
import json
import threading
import ctypes
import ctypes.wintypes
from datetime import datetime
from pathlib import Path

import keyboard
import mss
import mss.tools
from PIL import Image, ImageTk, ImageGrab, ImageDraw
import tkinter as tk
from tkinter import filedialog, messagebox

# High-DPI awareness
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass


class Settings:
    """App settings stored as JSON."""

    def __init__(self):
        self.dir = Path(os.environ.get("LOCALAPPDATA", ".")) / "MySnip"
        self.dir.mkdir(parents=True, exist_ok=True)
        self.path = self.dir / "settings.json"
        self.capture_hotkey = "f1"
        self.paste_hotkey = "f3"
        self.output_directory = str(Path.home() / "Pictures" / "MySnip")
        self.load()

    def load(self):
        if self.path.exists():
            try:
                data = json.loads(self.path.read_text(encoding="utf-8"))
                self.capture_hotkey = data.get("capture_hotkey", self.capture_hotkey)
                self.paste_hotkey = data.get("paste_hotkey", self.paste_hotkey)
                self.output_directory = data.get("output_directory", self.output_directory)
            except (json.JSONDecodeError, OSError):
                pass

    def save(self):
        data = {
            "capture_hotkey": self.capture_hotkey,
            "paste_hotkey": self.paste_hotkey,
            "output_directory": self.output_directory,
        }
        self.path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")


class StickerWindow:
    """A floating always-on-top image window."""

    def __init__(self, master, image: Image.Image, on_close):
        self.on_close = on_close
        self.top = tk.Toplevel(master)
        self.top.overrideredirect(True)
        self.top.attributes("-topmost", True)
        self.top.attributes("-alpha", 1.0)

        # Scale large images down
        w, h = image.size
        max_dim = 640
        if w > max_dim or h > max_dim:
            ratio = min(max_dim / w, max_dim / h)
            w, h = int(w * ratio), int(h * ratio)
            image = image.resize((w, h), Image.LANCZOS)

        self.image = image
        self.tk_image = ImageTk.PhotoImage(image)
        self.label = tk.Label(self.top, image=self.tk_image, bd=2, relief="solid")
        self.label.pack()

        # Position
        self.top.geometry(f"+100+100")

        # Drag support
        self._drag_x = 0
        self._drag_y = 0
        self.label.bind("<ButtonPress-1>", self._start_drag)
        self.label.bind("<B1-Motion>", self._do_drag)
        self.label.bind("<ButtonPress-3>", self._show_menu)
        self.top.bind("<Escape>", lambda e: self.close())
        self.top.bind("<MouseWheel>", self._on_scroll)

        # Context menu
        self.menu = tk.Menu(self.top, tearoff=0)
        self.menu.add_command(label="複製到剪貼簿", command=self._copy)
        self.menu.add_command(label="另存圖片…", command=self._save)
        self.menu.add_separator()
        self.menu.add_command(label="關閉", command=self.close)

    def _start_drag(self, event):
        self._drag_x = event.x
        self._drag_y = event.y

    def _do_drag(self, event):
        x = self.top.winfo_x() + event.x - self._drag_x
        y = self.top.winfo_y() + event.y - self._drag_y
        self.top.geometry(f"+{x}+{y}")

    def _on_scroll(self, event):
        # Ctrl+scroll = opacity, scroll = zoom
        current_alpha = self.top.attributes("-alpha")
        if event.state & 0x4:  # Ctrl held
            delta = 0.05 if event.delta > 0 else -0.05
            new_alpha = max(0.1, min(1.0, current_alpha + delta))
            self.top.attributes("-alpha", new_alpha)
        else:
            factor = 1.1 if event.delta > 0 else 1 / 1.1
            w, h = self.image.size
            nw, nh = int(w * factor), int(h * factor)
            if nw < 32 or nh < 32 or nw > 4000 or nh > 4000:
                return
            self.image = self.image.resize((nw, nh), Image.LANCZOS)
            self.tk_image = ImageTk.PhotoImage(self.image)
            self.label.configure(image=self.tk_image)

    def _show_menu(self, event):
        self.menu.post(event.x_root, event.y_root)

    def _copy(self):
        _copy_image_to_clipboard(self.image)

    def _save(self):
        path = filedialog.asksaveasfilename(
            defaultextension=".png",
            filetypes=[("PNG", "*.png"), ("JPEG", "*.jpg"), ("All", "*.*")],
        )
        if path:
            self.image.save(path)

    def close(self):
        self.top.destroy()
        self.on_close(self)


class CaptureOverlay:
    """Full-screen transparent overlay for region selection."""

    def __init__(self, master, screenshot: Image.Image, on_done):
        self.on_done = on_done
        self.top = tk.Toplevel(master)
        self.top.attributes("-fullscreen", True)
        self.top.attributes("-topmost", True)
        self.top.configure(cursor="crosshair")

        self.screenshot = screenshot
        self.tk_image = ImageTk.PhotoImage(screenshot)

        self.canvas = tk.Canvas(
            self.top,
            width=screenshot.width,
            height=screenshot.height,
            highlightthickness=0,
        )
        self.canvas.pack(fill="both", expand=True)
        self.canvas.create_image(0, 0, anchor="nw", image=self.tk_image)

        # Selection state
        self._start_x = 0
        self._start_y = 0
        self._rect_id = None
        self._selecting = False
        self._dim_ids = []

        # Draw semi-transparent overlay
        self._draw_dim_overlay()

        # Bindings
        self.canvas.bind("<ButtonPress-1>", self._on_press)
        self.canvas.bind("<B1-Motion>", self._on_drag)
        self.canvas.bind("<ButtonRelease-1>", self._on_release)
        self.top.bind("<Escape>", self._cancel)
        self.top.bind("<Return>", self._confirm)

        # Info label
        self._info = self.canvas.create_text(
            screenshot.width // 2, 30,
            text="拖曳選取區域 | Enter 確認 | Esc 取消",
            fill="white", font=("Microsoft JhengHei UI", 12),
        )

    def _draw_dim_overlay(self):
        # Dark semi-transparent overlay via stipple
        self._dim_rect = self.canvas.create_rectangle(
            0, 0, self.screenshot.width, self.screenshot.height,
            fill="black", stipple="gray50", outline="",
        )

    def _on_press(self, event):
        self._start_x = event.x
        self._start_y = event.y
        self._selecting = True
        if self._rect_id:
            self.canvas.delete(self._rect_id)
            self._rect_id = None

    def _on_drag(self, event):
        if not self._selecting:
            return
        if self._rect_id:
            self.canvas.delete(self._rect_id)
        x0, y0 = self._start_x, self._start_y
        x1, y1 = event.x, event.y
        # Draw selection rectangle
        self._rect_id = self.canvas.create_rectangle(
            x0, y0, x1, y1, outline="#27a7e7", width=2
        )
        # Update info with size
        w = abs(x1 - x0)
        h = abs(y1 - y0)
        self.canvas.itemconfig(self._info, text=f"{w} × {h}")

    def _on_release(self, event):
        if not self._selecting:
            return
        self._selecting = False
        x0 = min(self._start_x, event.x)
        y0 = min(self._start_y, event.y)
        x1 = max(self._start_x, event.x)
        y1 = max(self._start_y, event.y)
        if x1 - x0 < 4 or y1 - y0 < 4:
            return
        self._selection = (x0, y0, x1, y1)
        self.canvas.itemconfig(
            self._info,
            text=f"{x1-x0} × {y1-y0} | Enter 確認 | Esc 取消 | 右鍵儲存",
        )
        self.canvas.bind("<ButtonPress-3>", self._save_dialog)

    def _confirm(self, event=None):
        if hasattr(self, "_selection"):
            region = self.screenshot.crop(self._selection)
            self.top.destroy()
            self.on_done(region, "sticker")
        else:
            self._cancel()

    def _cancel(self, event=None):
        self.top.destroy()
        self.on_done(None, None)

    def _save_dialog(self, event=None):
        if not hasattr(self, "_selection"):
            return
        menu = tk.Menu(self.top, tearoff=0)
        menu.add_command(label="貼到螢幕（貼圖）", command=lambda: self._finish("sticker"))
        menu.add_command(label="複製到剪貼簿", command=lambda: self._finish("clipboard"))
        menu.add_command(label="存檔", command=lambda: self._finish("save"))
        menu.add_separator()
        menu.add_command(label="取消", command=lambda: None)
        menu.post(event.x_root, event.y_root)

    def _finish(self, action):
        region = self.screenshot.crop(self._selection)
        self.top.destroy()
        self.on_done(region, action)


def _copy_image_to_clipboard(image: Image.Image):
    """Copy PIL Image to Windows clipboard as DIB."""
    import win32clipboard
    output = io.BytesIO()
    image.convert("RGB").save(output, "BMP")
    data = output.getvalue()[14:]  # Strip BMP header
    win32clipboard.OpenClipboard()
    try:
        win32clipboard.EmptyClipboard()
        win32clipboard.SetClipboardData(win32clipboard.CF_DIB, data)
    finally:
        win32clipboard.CloseClipboard()


def _get_clipboard_image() -> Image.Image | None:
    """Get image from clipboard, if any."""
    try:
        img = ImageGrab.grabclipboard()
        if isinstance(img, Image.Image):
            return img
    except Exception:
        pass
    return None


class MySnipApp:
    """Main application controller."""

    def __init__(self):
        self.settings = Settings()
        self.root = tk.Tk()
        self.root.withdraw()  # Hide main window

        self.stickers: list[StickerWindow] = []
        self._capture_active = False

        # Ensure output dir exists
        Path(self.settings.output_directory).mkdir(parents=True, exist_ok=True)

        # Setup system tray in background thread
        self._setup_tray()

        # Register global hotkeys
        self._register_hotkeys()

        # Start tkinter mainloop
        self.root.after(100, self._poll)
        self.root.mainloop()

    def _register_hotkeys(self):
        keyboard.add_hotkey(self.settings.capture_hotkey, self._on_capture, suppress=True)
        keyboard.add_hotkey(self.settings.paste_hotkey, self._on_paste, suppress=True)

    def _setup_tray(self):
        try:
            import pystray
            from PIL import Image as PilImage

            # Create a simple icon
            icon_img = PilImage.new("RGB", (64, 64), color=(39, 167, 231))
            draw = ImageDraw.Draw(icon_img)
            draw.rectangle([16, 16, 48, 48], outline="white", width=3)

            menu = pystray.Menu(
                pystray.MenuItem("截圖 (F1)", lambda: self._schedule(self._on_capture)),
                pystray.MenuItem("貼上剪貼簿 (F3)", lambda: self._schedule(self._on_paste)),
                pystray.Menu.SEPARATOR,
                pystray.MenuItem("開啟截圖資料夾", lambda: self._open_output_dir()),
                pystray.MenuItem("結束", lambda: self._schedule(self._quit)),
            )
            self._tray = pystray.Icon("MySnip", icon_img, "MySnip", menu)
            tray_thread = threading.Thread(target=self._tray.run, daemon=True)
            tray_thread.start()
        except ImportError:
            pass  # pystray not available, skip tray

    def _schedule(self, func):
        """Schedule a function on the tkinter main thread."""
        self.root.after(0, func)

    def _poll(self):
        """Keep tkinter responsive while hotkeys run on background threads."""
        self.root.after(100, self._poll)

    def _on_capture(self):
        """Start screen capture."""
        self.root.after(0, self._do_capture)

    def _do_capture(self):
        if self._capture_active:
            return
        self._capture_active = True

        # Hide stickers temporarily
        for s in self.stickers:
            s.top.withdraw()

        self.root.after(150, self._take_screenshot)

    def _take_screenshot(self):
        with mss.mss() as sct:
            monitor = sct.monitors[0]  # All monitors combined
            raw = sct.grab(monitor)
            img = Image.frombytes("RGB", raw.size, raw.rgb)

        # Show stickers again
        for s in self.stickers:
            s.top.deiconify()

        # Show overlay
        CaptureOverlay(self.root, img, self._on_capture_done)

    def _on_capture_done(self, image: Image.Image | None, action: str | None):
        self._capture_active = False
        if image is None:
            return
        if action == "sticker":
            self._create_sticker(image)
        elif action == "clipboard":
            _copy_image_to_clipboard(image)
        elif action == "save":
            self._save_image(image)

    def _on_paste(self):
        """Paste clipboard image as sticker."""
        self.root.after(0, self._do_paste)

    def _do_paste(self):
        img = _get_clipboard_image()
        if img:
            self._create_sticker(img)

    def _create_sticker(self, image: Image.Image):
        sticker = StickerWindow(self.root, image, self._remove_sticker)
        self.stickers.append(sticker)

    def _remove_sticker(self, sticker: StickerWindow):
        if sticker in self.stickers:
            self.stickers.remove(sticker)

    def _save_image(self, image: Image.Image):
        out_dir = Path(self.settings.output_directory)
        out_dir.mkdir(parents=True, exist_ok=True)
        filename = f"MySnip_{datetime.now().strftime('%Y%m%d_%H%M%S')}.png"
        path = out_dir / filename
        image.save(str(path))

    def _open_output_dir(self):
        out_dir = Path(self.settings.output_directory)
        out_dir.mkdir(parents=True, exist_ok=True)
        os.startfile(str(out_dir))

    def _quit(self):
        keyboard.unhook_all()
        try:
            self._tray.stop()
        except Exception:
            pass
        self.root.quit()


if __name__ == "__main__":
    MySnipApp()
