"""Floating, grouped image windows."""

from __future__ import annotations

from pathlib import Path
import tkinter as tk
from tkinter import colorchooser, filedialog, messagebox, simpledialog, ttk

from PIL import Image, ImageDraw, ImageFilter, ImageOps, ImageTk

from snip_capture import ordered_box, valid_box
from snip_drop import DropTarget
from snip_images import RESAMPLE, TRANSPOSE, StickerAsset, color_card, save_image
from snip_native import WindowsShare
from snip_windows import (copy_image, copy_text, hwnd, position_window, reset_layered_surface,
                          set_click_through, show_layered_image, user32)

GLOW_PADDING = 8


def sticker_glow(size, selected):
    width, height = size
    pad = GLOW_PADDING
    mask = Image.new("L", (width + pad * 2, height + pad * 2))
    edge = (pad - 1, pad - 1, pad + width, pad + height)
    ImageDraw.Draw(mask).rectangle(edge, fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(2.6))
    mask = mask.point(lambda alpha: round(alpha * (0.80 if selected else 0.62)))
    draw = ImageDraw.Draw(mask)
    draw.rectangle(edge, outline=225 if selected else 172)
    draw.rectangle((pad, pad, pad + width - 1, pad + height - 1), fill=0)
    glow = Image.new("RGBA", mask.size, "#3d8dff")
    glow.putalpha(mask)
    return glow


class StickerWindow:
    def __init__(self, app, asset, group, layout=None, position=None):
        self.app, self.asset, self.group = app, asset, group
        self.top = tk.Toplevel(app.root)
        self.top.withdraw()
        self.top.overrideredirect(True)
        layout = layout or {}
        self.backup_name = layout.get("file")
        self.desktop = layout.get("desktop", app.active_desktop)
        self.hidden = bool(layout.get("hidden", False))
        self.click_through = bool(layout.get("click_through", False))
        self.topmost = bool(layout.get("topmost", True))
        self.zoom = bounded(layout.get("zoom"), 1, 0.1, 8)
        self.opacity = bounded(layout.get("opacity"), 1, 0.1, 1)
        self.rotation = int(bounded(layout.get("rotation"), 0, 0, 359))
        self.flip_x, self.flip_y = bool(layout.get("flip_x")), bool(layout.get("flip_y"))
        self.grayscale, self.invert = bool(layout.get("grayscale")), bool(layout.get("invert"))
        self.thumbnail = bool(layout.get("thumbnail"))
        self.thumbnail_width = int(bounded(layout.get("thumbnail_width"), 160, 40, 1000))
        self.background = layout.get("background", "transparent")
        self.x, self.y = app.clamp_sticker_position(position or (layout.get("x"), layout.get("y")))
        self.selected = False
        self.visible = False
        self.frame = 0
        self.playing = True
        self.speed = 1.0
        self.timer = None
        self.photo = None
        self.closed = False
        self._share = None
        self._drop_target = None
        self._layered = False
        self._glow_window = None
        self._glow_key = None
        self._origin = None
        self._right_origin = None
        self.label = tk.Label(self.top, bd=0, highlightthickness=0, padx=0, pady=0)
        self.label.pack()
        self.top.attributes("-topmost", self.topmost)
        self.top.attributes("-alpha", self.opacity)
        self._bind()
        self.render()
        try:
            self._drop_target = DropTarget(hwnd(self.top), lambda data: app.actions.put(lambda: app.drop_on_sticker(self, data)))
        except OSError:
            pass

    def _bind(self):
        for key, action in (
            ("<ButtonPress-1>", self._start_drag), ("<B1-Motion>", self._drag), ("<ButtonRelease-1>", self._end_drag),
            ("<Double-Button-1>", self._double_click), ("<ButtonPress-3>", self._right_press),
            ("<ButtonRelease-3>", self._right_release), ("<MouseWheel>", self._wheel),
            ("<Motion>", self._sample_color),
        ):
            self.label.bind(key, action)
        for key, action in {
            "<Escape>": lambda: self._escape(False), "<Shift-Escape>": lambda: self._escape(True), "<Delete>": self.close,
            "<space>": self._space, "<1>": lambda: self._step_animation(-1), "<2>": lambda: self._step_animation(1),
            "<3>": lambda: self.change("flip_x"), "<4>": lambda: self.change("flip_y"),
            "<5>": lambda: self.change("grayscale"), "<6>": lambda: self.change("invert"),
            "<KeyPress-p>": lambda: self.change("click_through"), "<Control-c>": self.copy,
            "<Alt-c>": self._copy_color, "<Control-s>": self.save,
        }.items():
            self.top.bind(key, lambda _, callback=action: callback())
        self._swatch = tk.Label(self.top, background="#202020", foreground="white", padx=3)
        self.label.bind("<Leave>", lambda _: self._swatch.place_forget())
        self.top.bind("<FocusIn>", lambda _: self._position_glow())
        self.top.bind("<KeyRelease-Alt_L>", lambda _: self._swatch.place_forget())
        self.top.bind("<KeyRelease-Alt_R>", lambda _: self._swatch.place_forget())
        for key, direction in (("<plus>", 1), ("<minus>", -1), ("<KP_Add>", 1), ("<KP_Subtract>", -1)):
            self.top.bind(key, lambda event, step=direction: self._adjust(step, event.state & 4))

    def _escape(self, shifted):
        action = self.app.settings.sticker_shift_escape if shifted else self.app.settings.sticker_escape
        if action == "hide":
            self.hide()
        elif action == "close":
            self.close()
        elif action == "disable_click_through":
            self.app.disable_click_through()

    def snapshot(self):
        return {"file": self.backup_name, "group": self.group, "hidden": self.hidden, "topmost": self.topmost,
                "click_through": self.click_through, "zoom": self.zoom, "opacity": self.opacity,
                "x": self.x, "y": self.y, "rotation": self.rotation, "flip_x": self.flip_x, "flip_y": self.flip_y,
                "grayscale": self.grayscale, "invert": self.invert, "thumbnail": self.thumbnail,
                "thumbnail_width": self.thumbnail_width, "background": self.background, "color": self.asset.color,
                "desktop": self.desktop}

    def image(self):
        result = self.asset.frames[self.frame]
        if self.flip_x:
            result = result.transpose(TRANSPOSE.FLIP_LEFT_RIGHT)
        if self.flip_y:
            result = result.transpose(TRANSPOSE.FLIP_TOP_BOTTOM)
        if self.rotation:
            result = result.rotate(-self.rotation, expand=True, resample=RESAMPLE.BICUBIC)
        if self.grayscale or self.invert:
            alpha = result.getchannel("A")
            rgb = ImageOps.grayscale(result).convert("RGB") if self.grayscale else result.convert("RGB")
            if self.invert:
                rgb = ImageOps.invert(rgb)
            result = rgb.convert("RGBA")
            result.putalpha(alpha)
        return result

    def render(self):
        source = self.image()
        scale = min(1, self.thumbnail_width / source.width) if self.thumbnail else self.zoom
        # Limit additional magnification of large images without shrinking the native 100% size.
        scale = min(scale, max(1, 4096 / max(source.size)))
        size = max(1, round(source.width * scale)), max(1, round(source.height * scale))
        display = source if size == source.size else source.resize(size, RESAMPLE.LANCZOS)
        self._display_size = size
        if self.background == "clickable" and self.visible:
            self._render_clickable(display)
            return
        if self._layered:
            reset_layered_surface(self.top)
            self._layered = False
            self.top.attributes("-alpha", self.opacity)
        if self.background in ("light", "dark"):
            colors = ("#f5f5f5", "#cccccc") if self.background == "light" else ("#303030", "#505050")
            base = Image.new("RGB", size, colors[0])
            draw = ImageDraw.Draw(base)
            for y in range(0, size[1], 12):
                for x in range((y // 12 % 2) * 12, size[0], 24):
                    draw.rectangle((x, y, x + 11, y + 11), fill=colors[1])
            base.paste(display, mask=display.getchannel("A"))
            display = base
        elif self.background in ("solid", "clickable"):
            base = Image.new("RGB", size, "#ffffff")
            base.paste(display, mask=display.getchannel("A"))
            display = base
        else:
            # Tk's Windows transparent colour handles the fully transparent pixels.
            mask = display.getchannel("A")
            base = Image.new("RGB", size, "#010203")
            base.paste(display, mask=mask)
            display = base
        try:
            self.top.attributes("-transparentcolor", "#010203" if self.background == "transparent" else "")
        except tk.TclError:
            pass
        self.photo = ImageTk.PhotoImage(display, master=self.top)
        self.label.configure(image=self.photo)
        self._update_glow()

    def _render_clickable(self, display):
        width, height = display.size
        # A minimal nonzero alpha keeps fully transparent pixels in the hit-test region.
        surface = Image.new("RGBA", (width, height), (0, 0, 0, max(1, round(1 / self.opacity))))
        surface.alpha_composite(display)
        self.photo = ImageTk.PhotoImage(display, master=self.top)
        self.label.configure(image=self.photo)
        self.top.update_idletasks()
        if not self._layered:
            reset_layered_surface(self.top)
            self._layered = True
        try:
            show_layered_image(self.top, surface, self.opacity)
        except OSError:
            self.background = "solid"
            self.render()
            return
        self._update_glow()

    def _update_glow(self):
        if not self.visible or self.click_through:
            if self._glow_window is not None:
                self._glow_window.withdraw()
            return

        width, height = self._display_size
        if self._glow_window is None:
            window = self._glow_window = tk.Toplevel(self.top)
            window.withdraw()
            window.overrideredirect(True)
            window.resizable(False, False)
            window.attributes("-topmost", self.topmost)
            window.geometry("1x1+0+0")
            window.update_idletasks()
            handle = hwnd(window)
            # The glow is a separate transparent surface behind the image and never takes input.
            user32.SetWindowLongPtrW(handle, -20, user32.GetWindowLongPtrW(handle, -20) | 0x080000A0)
            set_click_through(window, True)
        window = self._glow_window
        key = (width, height, self.selected, self.opacity)
        if key != self._glow_key:
            try:
                show_layered_image(window, sticker_glow((width, height), self.selected), self.opacity)
            except OSError:
                window.withdraw()
                return
            self._glow_key = key
        self._position_glow()

    def _position_glow(self):
        if self._glow_window is None or not self.visible or self.click_through:
            return
        # Keep the layered surface immediately below its sticker without activating either window.
        width, height = self._display_size
        user32.SetWindowPos(hwnd(self._glow_window), hwnd(self.top), self.x - GLOW_PADDING, self.y - GLOW_PADDING,
                            width + 2 * GLOW_PADDING, height + 2 * GLOW_PADDING, 0x0010 | 0x0040)

    def set_visible(self, visible):
        if self.closed or visible == self.visible:
            return
        self.visible = visible
        if visible:
            self.top.deiconify()
            self.place(self.x, self.y)
            set_click_through(self.top, self.click_through)
            if self.background == "clickable":
                self.render()
            else:
                self._update_glow()
            self._schedule_frame()
        else:
            self._stop_frame()
            self.top.withdraw()
            self._update_glow()

    def place(self, x, y):
        self.x, self.y = int(x), int(y)
        if self.visible:
            position_window(self.top, self.x, self.y)
            self._position_glow()

    def move_to_desktop(self, desktops, desktop):
        desktops.move(hwnd(self.top), desktop)
        if self._glow_window is not None:
            desktops.move(hwnd(self._glow_window), desktop)
            self._position_glow()

    def replace(self, asset):
        self.asset = asset
        self.backup_name = None
        self.frame = 0
        self.playing = True
        self.grayscale = self.invert = False
        self.render()
        self._schedule_frame()
        self.app.workspace_changed()

    def _start_drag(self, event):
        self.top.focus_set()
        if event.state & 4:
            self.selected = not self.selected
            self.render()
            return "break"
        self._origin = event.x_root, event.y_root
        self._drag_positions = {sticker: (sticker.x, sticker.y) for sticker in self.app.targets(self)}

    def _drag(self, event):
        if self._origin is None:
            return
        dx, dy = event.x_root - self._origin[0], event.y_root - self._origin[1]
        for sticker, (x, y) in self._drag_positions.items():
            sticker.place(x + dx, y + dy)

    def _end_drag(self, event):
        if self._origin is not None:
            self._origin = None
            if any((sticker.x, sticker.y) != position for sticker, position in self._drag_positions.items()):
                self.app.workspace_changed()

    def _double_click(self, event):
        self._origin = None
        if event.state & 1:
            self.change("thumbnail")
        else:
            self.hide()
        return "break"

    def _right_press(self, event):
        self._right_origin = event.x, event.y

    def _right_release(self, event):
        start, self._right_origin = self._right_origin, None
        if start and max(abs(event.x - start[0]), abs(event.y - start[1])) >= 10 and not self.asset.animated:
            box = ordered_box(start, (event.x, event.y))
            source = self.image()
            left, top, right, bottom = (round(value * source.size[index % 2] / self._display_size[index % 2])
                                        for index, value in enumerate(box))
            crop = max(0, left), max(0, top), min(source.width, right), min(source.height, bottom)
            if valid_box(crop):
                self.app.create_sticker(StickerAsset((source.crop(crop),)), position=(self.x, self.y))
            return "break"
        self._menu(event)
        return "break"

    def _wheel(self, event):
        if event.delta:
            self._adjust(1 if event.delta > 0 else -1, event.state & 4)

    def _adjust(self, direction, opacity):
        if opacity:
            for sticker in self.app.targets(self):
                sticker.opacity = max(0.1, min(1, sticker.opacity + direction * 0.05))
                if sticker.background == "clickable":
                    sticker.render()
                else:
                    sticker.top.attributes("-alpha", sticker.opacity)
                    sticker._update_glow()
        elif self.asset.animated:
            self.speed = max(0.1, min(8, self.speed * (1.25 ** direction)))
            self._schedule_frame()
        else:
            for sticker in self.app.targets(self):
                sticker.zoom = max(0.1, min(8, sticker.zoom * (1.1 ** direction)))
                sticker.thumbnail = False
                sticker.render()
        self.app.workspace_changed()
        return "break"

    def change(self, attribute):
        value = not getattr(self, attribute)
        for sticker in self.app.targets(self):
            setattr(sticker, attribute, value)
            if attribute == "click_through":
                set_click_through(sticker.top, value)
                sticker._update_glow()
            elif attribute == "topmost":
                sticker.top.attributes("-topmost", value)
                if sticker._glow_window is not None:
                    sticker._glow_window.attributes("-topmost", value)
                    sticker._position_glow()
            else:
                sticker.render()
        self.app.workspace_changed()

    def _step_animation(self, direction):
        if self.asset.animated:
            self.playing = False
            self._stop_frame()
            self._step(direction)

    def _rotate(self, direction):
        for sticker in self.app.targets(self):
            sticker.rotation = (sticker.rotation + direction * 90) % 360
            sticker.render()
        self.app.workspace_changed()

    def _space(self):
        if self.asset.animated:
            self.playing = not self.playing
            self._schedule_frame()
        else:
            self.app.edit_sticker(self)

    def _step(self, direction=1):
        self.frame = (self.frame + direction) % len(self.asset.frames)
        self.render()
        self._schedule_frame()

    def _stop_frame(self):
        if self.timer is not None:
            self.top.after_cancel(self.timer)
            self.timer = None

    def _schedule_frame(self):
        self._stop_frame()
        if self.asset.animated and self.playing and self.visible:
            self.timer = self.top.after(max(16, round(self.asset.delays[self.frame] / self.speed)), self._tick)

    def _tick(self):
        self.timer = None
        self._step()

    def _sample_color(self, event):
        if not event.state & 0x20008:
            self._swatch.place_forget()
            return
        source = self.image()
        x = max(0, min(source.width - 1, int(event.x * source.width / self._display_size[0])))
        y = max(0, min(source.height - 1, int(event.y * source.height / self._display_size[1])))
        self._swatch.configure(text="#{:02X}{:02X}{:02X}".format(*source.getpixel((x, y))[:3]))
        self._swatch.place(relx=1, rely=1, anchor="se")

    def _copy_color(self):
        if self._swatch.winfo_ismapped():
            self._output(lambda: copy_text(self._swatch.cget("text")))

    def hide(self):
        for sticker in self.app.targets(self):
            sticker.hidden = True
        self.app.update_sticker_visibility()
        self.app.workspace_changed()

    def close(self):
        for sticker in self.app.targets(self):
            sticker.destroy()

    def destroy(self):
        if self.closed:
            return
        self.closed = True
        self._stop_frame()
        if self._drop_target:
            self._drop_target.close()
            self._drop_target = None
        if self._share:
            self._share.close()
        if self._glow_window is not None:
            self._glow_window.destroy()
        self.top.destroy()
        self.app.remove_sticker(self)

    def copy(self):
        self._output(lambda: copy_image(self.output_image()))

    def output_image(self):
        image = self.image()
        if self.zoom != 1:
            image = image.resize(tuple(max(1, round(side * self.zoom)) for side in image.size), RESAMPLE.LANCZOS)
        elif self.opacity != 1:
            image = image.copy()
        if self.opacity != 1:
            image.putalpha(image.getchannel("A").point(lambda value: round(value * self.opacity)))
        return image

    def share(self):
        if self._share is None:
            self._share = WindowsShare()
        self._output(lambda: self._share.show(hwnd(self.top), self.output_image()))

    def save(self):
        def output():
            choices = [("PNG", "*.png"), ("JPEG", "*.jpg")]
            if self.asset.animated:
                choices.append(("GIF 動畫", "*.gif"))
            path = filedialog.asksaveasfilename(parent=self.top, defaultextension=".png", filetypes=choices,
                                               initialdir=self.app.settings.output_directory)
            if path:
                if Path(path).suffix.lower() == ".gif" and self.asset.animated:
                    self.asset.save(path)
                else:
                    save_image(self.output_image(), path)
        self._output(output)

    def _output(self, action):
        try:
            action()
        except (OSError, ValueError, RuntimeError) as error:
            messagebox.showerror("MySnip", str(error), parent=self.top)

    def _menu(self, event):
        menu = tk.Menu(self.top, tearoff=False)
        orientation_actions = (
            ("上一格 1", lambda: self._step_animation(-1)),
            ("下一格 2", lambda: self._step_animation(1)),
        ) if self.asset.animated else (
            ("逆時針旋轉 90°", lambda: self._rotate(-1)),
            ("順時針旋轉 90°", lambda: self._rotate(1)),
        )
        for label, command in (
            ("複製 Ctrl+C", self.copy), ("另存 Ctrl+S", self.save), ("使用 Windows 分享…", self.share),
            ("編輯標註／播放 GIF Space", self._space),
            ("裁切圖片…", self._crop), ("載入圖片網址…", lambda: self.app.load_sticker_url(self)),
            *orientation_actions,
            ("水平翻轉 3", lambda: self.change("flip_x")), ("垂直翻轉 4", lambda: self.change("flip_y")),
            ("灰階 5", lambda: self.change("grayscale")), ("反相 6", lambda: self.change("invert")),
            ("設定縮放比例…", self._set_zoom), ("切換縮圖 Shift+雙擊", lambda: self.change("thumbnail")),
            ("重設大小與角度", self._reset_transform),
            ("設定縮圖寬度…", self._set_thumbnail), ("切換永遠置頂", lambda: self.change("topmost")),
            ("切換滑鼠穿透 P", lambda: self.change("click_through")),
            ("只顯示選取貼圖", lambda: self.app.toggle_solo(self)), ("隱藏", self.hide), ("關閉", self.close),
        ):
            menu.add_command(label=label, command=command)
        if self.asset.color:
            menu.insert_command(3, label="編輯色卡…", command=self._edit_color)
        backgrounds = tk.Menu(menu, tearoff=False)
        for label, value in (("透明", "transparent"), ("透明且可點擊", "clickable"), ("白底可點擊", "solid"),
                             ("淺色棋盤", "light"), ("深色棋盤", "dark")):
            backgrounds.add_command(label=label, command=lambda mode=value: self._set_background(mode))
        menu.add_cascade(label="透明區域背景", menu=backgrounds)
        groups = tk.Menu(menu, tearoff=False)
        for group in self.app.groups:
            groups.add_command(label=group, command=lambda name=group: self.app.move_to_group(self, name))
        menu.add_cascade(label="移至群組", menu=groups)
        try:
            menu.tk_popup(event.x_root, event.y_root)
        finally:
            menu.grab_release()

    def _edit_color(self):
        rgb, _ = colorchooser.askcolor(self.asset.color, parent=self.top)
        if rgb:
            self.replace(color_card(tuple(int(channel) for channel in rgb)))

    def _set_background(self, mode):
        for sticker in self.app.targets(self):
            sticker.background = mode
            sticker.render()
        self.app.workspace_changed()

    def _reset_transform(self):
        for sticker in self.app.targets(self):
            sticker.zoom = sticker.opacity = 1
            sticker.rotation = 0
            sticker.flip_x = sticker.flip_y = sticker.thumbnail = False
            if sticker.background != "clickable":
                sticker.top.attributes("-alpha", sticker.opacity)
            sticker.render()
        self.app.workspace_changed()

    def _set_zoom(self):
        value = simpledialog.askinteger("貼圖縮放", "輸入百分比（10–800）", initialvalue=round(self.zoom * 100),
                                        minvalue=10, maxvalue=800, parent=self.top)
        if value is not None:
            for sticker in self.app.targets(self):
                sticker.zoom = value / 100
                sticker.thumbnail = False
                sticker.render()
            self.app.workspace_changed()

    def _set_thumbnail(self):
        value = simpledialog.askinteger("縮圖寬度", "輸入寬度（40–1000）", initialvalue=self.thumbnail_width,
                                        minvalue=40, maxvalue=1000, parent=self.top)
        if value is not None:
            for sticker in self.app.targets(self):
                sticker.thumbnail_width = value
                sticker.thumbnail = True
                sticker.render()
            self.app.workspace_changed()

    def _crop(self):
        if self.asset.animated:
            return
        source = self.asset.frames[0]
        window = tk.Toplevel(self.top)
        window.title("裁切貼圖")
        window.attributes("-topmost", self.topmost)
        preview = source.copy()
        preview.thumbnail((800, 550), RESAMPLE.LANCZOS)
        canvas = tk.Canvas(window, width=preview.width, height=preview.height, highlightthickness=0)
        canvas.pack()
        photo = ImageTk.PhotoImage(preview, master=window)
        canvas.create_image(0, 0, image=photo, anchor="nw")
        canvas.photo = photo
        rect = canvas.create_rectangle(0, 0, 0, 0, outline="#27a7e7", width=2)
        start = None
        selection = None

        def down(event):
            nonlocal start
            start = event.x, event.y

        def drag(event):
            nonlocal selection
            if start is None:
                return
            selection = ordered_box(start, (min(preview.width, max(0, event.x)), min(preview.height, max(0, event.y))))
            canvas.coords(rect, *selection)

        def apply():
            if valid_box(selection):
                crop = tuple(round(value * source.size[index % 2] / preview.size[index % 2])
                             for index, value in enumerate(selection))
                self.replace(StickerAsset((source.crop(crop),)))
                window.destroy()

        canvas.bind("<ButtonPress-1>", down)
        canvas.bind("<B1-Motion>", drag)
        ttk.Button(window, text="裁切", command=apply).pack(side="right", padx=6, pady=6)
        ttk.Button(window, text="取消", command=window.destroy).pack(side="right")


def bounded(value, default, minimum, maximum):
    try:
        return max(minimum, min(maximum, float(value)))
    except (TypeError, ValueError, OverflowError):
        return default
