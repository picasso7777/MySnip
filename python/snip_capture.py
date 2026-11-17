"""Cross-monitor selection and the annotation toolbar."""

from __future__ import annotations

from dataclasses import replace
from math import atan2, degrees
from pathlib import Path
from types import SimpleNamespace
import tkinter as tk
from tkinter import colorchooser, filedialog, font, messagebox, simpledialog, ttk

from PIL import Image, ImageEnhance, ImageTk

from snip_images import (Annotation, Annotations, RESAMPLE, SHAPE_TOOLS, TEXT_PADDING, annotation_fonts, decorate,
                         image_font, render_annotations, rotate_offset, save_image, wrap_annotation_text)
from snip_native import WindowsShare
from snip_storage import Settings, is_hex_color, read_json, write_json
from snip_windows import copy_text, hwnd, monitor_at, position_window, user32

TOOL_VARIANTS = {
    "shape": (("rectangle", "▭", "矩形"), ("ellipse", "◯", "圓形／橢圓"), ("rounded", "▢", "圓角矩形")),
    "line": (("line", "―", "直線"), ("arrow", "→", "開放箭頭"), ("outline_arrow", "⇨", "空心箭頭"),
             ("filled_arrow", "➜", "實心箭頭"), ("double_arrow", "↔", "雙向箭頭"), ("polyline", "⌁", "折線")),
    "highlighter": (("marker", "━", "直線螢光筆"), ("marker_rectangle", "▬", "矩形螢光筆"),
                    ("marker_ellipse", "◉", "橢圓螢光筆")),
    "filter": (("mosaic", "▦", "馬賽克"), ("blur", "▧", "高斯模糊"), ("magnify", "⊕", "放大標註")),
}
TOOL_GROUPS = {tool: group for group, variants in TOOL_VARIANTS.items() for tool, _, _ in variants}
TOOLBAR_BG = "#f9f9f9"
TOOLBAR_BORDER = "#bdbdbd"
OUTSIDE_SELECTION_TOOLS = (*SHAPE_TOOLS, "text")
SHAPE_HANDLES = ((-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1))
RESIZE_CURSORS = ("size_we", "size_nw_se", "size_ns", "size_ne_sw")
TEXT_HANDLES = ((-1, -1), (1, -1), (1, 1), (-1, 1))
TEXT_DELETE_CORNER = (1, -1)
TEXT_HANDLE_RADIUS = 6


def ordered_box(first, second):
    return min(first[0], second[0]), min(first[1], second[1]), max(first[0], second[0]), max(first[1], second[1])


def contains(box, point):
    return box and box[0] <= point[0] < box[2] and box[1] <= point[1] < box[3]


def intersection(first, second):
    return max(first[0], second[0]), max(first[1], second[1]), min(first[2], second[2]), min(first[3], second[3])


def valid_box(box):
    return box and box[2] > box[0] and box[3] > box[1]


class CaptureOverlay:
    def __init__(self, master, screenshot, bounds, monitors, on_done, *, settings=None, candidates=(), active_window=None,
                 history=None, refresh=None, on_preferences=None, on_recognize=None, region=None, editing=False,
                 auto_action=None, shadow=False):
        self.root = master
        self.on_done = on_done
        self.settings = settings or Settings()
        self.bounds, self.monitors = bounds, monitors
        self.history, self.refresh, self.on_preferences = history, refresh, on_preferences
        self.on_recognize = on_recognize
        self.candidates, self.active_window = candidates, active_window
        self.editing, self.auto_action = editing, auto_action
        self.screenshot = screenshot
        self.top = tk.Toplevel(master)
        self.top.withdraw()
        self.top.overrideredirect(True)
        self.top.attributes("-topmost", True)
        self.canvas = tk.Canvas(self.top, width=screenshot.width, height=screenshot.height,
                                highlightthickness=0, bg="#202020", cursor="crosshair")
        self.canvas.pack(fill="both", expand=True)
        self.document = Annotations()
        self._selection = self._hover = self._initial_selection = self._replay_image = None
        self._history_index = -1
        self._tool = "select"
        self._last_tools = {"shape": "rectangle", "line": "arrow", "highlighter": "marker", "filter": "mosaic"}
        self._width = tk.IntVar(master=self.top, value=self.settings.tool_width)
        self._opacity = tk.IntVar(master=self.top, value=self.settings.tool_opacity)
        self._filled = tk.BooleanVar(master=self.top)
        self._text_background = tk.BooleanVar(master=self.top)
        self._dashed = tk.BooleanVar(master=self.top)
        self._ellipse = tk.BooleanVar(master=self.top)
        self._round_eraser = tk.BooleanVar(master=self.top)
        self._filter_strength = tk.IntVar(master=self.top, value=10)
        self._color = self.settings.tool_color
        self._board = None
        self._ratio = 0.0
        self._unit_dip = False
        self._outline = "#000000"
        self._outline_width = 0
        self._font = tk.StringVar(master=self.top, value="Microsoft JhengHei UI")
        self._font_size = tk.IntVar(master=self.top, value=22)
        self._bold = tk.BooleanVar(master=self.top)
        self._italic = tk.BooleanVar(master=self.top)
        self._free_select = tk.BooleanVar(master=self.top)
        self._decoration = {"rounded": tk.BooleanVar(master=self.top), "border": tk.BooleanVar(master=self.top),
                            "shadow": tk.BooleanVar(master=self.top, value=shadow)}
        self._selected = set()
        self._drawing_style = None
        self._text_editor = self._text_draft = self._text_index = self._text_editor_id = None
        self._resume_text_resize = False
        self._text_move_origin = None
        self._text_move_caret = None
        self._clipboard = ()
        self._polyline = []
        self._drag = None
        self._shape_resize = None
        self._palette_dirty = False
        self._closed = False
        self._refreshing = False
        self._share = None
        self._magnifier_photo = None
        self._toolbar_tip = None
        self._toolbar_tip_timer = None
        self._toolbar_layout = None
        self._screen = self.canvas.create_image(0, 0, anchor="nw")
        self._outside = self.canvas.create_image(0, 0, anchor="nw", state="hidden")
        self._crop = self.canvas.create_image(0, 0, anchor="nw")
        self._border = self.canvas.create_rectangle(0, 0, 0, 0, outline="#27a7e7", width=2, state="hidden")
        self._handles = [self.canvas.create_rectangle(0, 0, 0, 0, fill="white", outline="#27a7e7", state="hidden")
                         for _ in range(8)]
        self._info = self.canvas.create_text(0, 0, anchor="nw", fill="white", font=("Microsoft JhengHei UI", 10))
        self._magnifier = self.canvas.create_image(0, 0, anchor="nw", state="hidden")
        self._magnifier_background = self.canvas.create_rectangle(0, 0, 0, 0, fill=self.settings.magnifier_background,
                                                                  width=0, state="hidden")
        self._magnifier_label = self.canvas.create_text(0, 0, anchor="nw", fill="white", state="hidden")
        self._toolbar = self._build_toolbar()
        self._toolbar_id = self.canvas.create_window(0, 0, window=self._toolbar, anchor="nw", state="hidden")
        self._set_screen()
        self._bind()
        self._draw()
        self._show()
        if region:
            self._set_selection(self._local_box(region))

    def _show(self):
        try:
            self.top.geometry(f"{self.bounds['width']}x{self.bounds['height']}+0+0")
            self.top.deiconify()
            position_window(self.top, self.bounds["left"], self.bounds["top"], self.bounds["width"], self.bounds["height"])
            self.canvas.focus_force()
        except (OSError, tk.TclError):
            self.top.destroy()
            raise

    def _bind(self):
        for key, callback in {
            "<Motion>": self._on_motion, "<ButtonPress-1>": self._on_press, "<B1-Motion>": self._on_drag,
            "<Double-Button-1>": self._on_double_click,
            "<ButtonRelease-1>": self._on_release, "<Button-3>": self._context_menu,
            "<Button-2>": lambda _: self._finish("sticker"), "<MouseWheel>": self._cycle_candidate,
        }.items():
            self.canvas.bind(key, callback)
        bindings = {
            "<Escape>": self._cancel, "<Return>": lambda _: self._finish("apply" if self.editing else "clipboard"),
            "<Control-c>": lambda _: self._finish("clipboard"), "<Control-t>": lambda _: self._finish("apply" if self.editing else "sticker"),
            "<Control-s>": self._save, "<Control-Shift-KeyPress-S>": lambda _: self._finish("quick-save"),
            "<Control-z>": lambda _: self._undo(False), "<Control-y>": lambda _: self._undo(True),
            "<Control-Shift-KeyPress-C>": self._copy_annotations, "<Control-Shift-KeyPress-V>": self._paste_annotations,
            "<Delete>": self._delete_annotations, "<KeyPress-f>": lambda _: self._select_monitor(False),
            "<Shift-KeyPress-F>": lambda _: self._select_monitor(True), "<KeyPress-w>": self._select_window,
            "<Control-Key-1>": lambda _: self._choose_tool(self._last_tools["shape"]),
            "<KeyPress-v>": lambda _: self._choose_tool("select"), "<KeyPress-e>": lambda _: self._choose_tool("erase"),
            "<KeyPress-n>": lambda _: self._choose_tool("number") if self._selection else "break",
            "<Shift-KeyPress-C>": self._copy_color, "<Tab>": self._tab_candidate,
            "<KeyPress-c>": self._copy_color, "<KeyPress-r>": self._refresh, "<comma>": lambda _: self._replay(1),
            "<period>": lambda _: self._replay(-1),
        }
        for key, callback in bindings.items():
            self.top.bind(key, callback if key == "<Escape>" else
                          lambda event, action=callback: "break" if self._drag == "resize-annotation" else action(event))
        for key in ("Alt_L", "Alt_R"):
            self.top.bind(f"<KeyRelease-{key}>", lambda _: self._hide_magnifier() if self._selection else None)
        for key, dx, dy in (("Left", -1, 0), ("Right", 1, 0), ("Up", 0, -1), ("Down", 0, 1)):
            self.top.bind(f"<{key}>", lambda event, x=dx, y=dy: self._nudge(event, x, y))
            self.top.bind(f"<Shift-{key}>", lambda event, x=dx, y=dy: self._nudge(event, x * 10, y * 10))
        for key, dx, dy in (("a", -1, 0), ("d", 1, 0), ("s", 0, 1)):
            self.top.bind(f"<KeyPress-{key}>", lambda event, x=dx, y=dy: self._nudge(event, x, y) if not self._selection else None)

    def _build_toolbar(self):
        style = ttk.Style(self.top)
        self._toolbar_icon_font = font.Font(self.top, family="Segoe UI Symbol", size=-17)
        self._toolbar_action_font = font.Font(self.top, family="Segoe MDL2 Assets", size=-15)
        style.configure("CapturePanel.TFrame", background=TOOLBAR_BG, bordercolor=TOOLBAR_BORDER, relief="solid", borderwidth=1)
        style.configure("Capture.TFrame", background=TOOLBAR_BG)
        for kind in ("TLabel", "TCheckbutton", "Horizontal.TScale", "TCombobox"):
            style.configure(f"Capture.{kind}", background=TOOLBAR_BG, foreground="#252525")
        style.map("Capture.TCombobox", fieldbackground=[("readonly", "#ffffff")])
        style.configure("CaptureIcon.TButton", background=TOOLBAR_BG, foreground="#252525",
                        bordercolor=TOOLBAR_BG, lightcolor=TOOLBAR_BG, darkcolor=TOOLBAR_BG,
                        padding=0, font=self._toolbar_icon_font)
        style.map("CaptureIcon.TButton", background=[("selected", "#168ce5"), ("pressed", "#cbe8fa"), ("active", "#e6f3fc")],
                  foreground=[("disabled", "#999999"), ("selected", "#ffffff")])
        style.configure("CaptureAction.CaptureIcon.TButton", font=self._toolbar_action_font)
        panel = ttk.Frame(self.canvas, style="CapturePanel.TFrame")

        self._group_buttons = {}
        for key, glyph, title, action_font in (
            ("shape", "▭", "矩形／圓形／橢圓 (Ctrl+1)", False), ("line", "↗", "直線／箭頭", False),
            ("pen", "\ue70f", "畫筆", True), ("highlighter", "▰", "螢光筆", False),
            ("filter", "▦", "馬賽克／模糊", False), ("text", "T", "文字", False), ("erase", "◇", "橡皮擦 (E)", False),
        ):
            self._group_buttons[key] = self._icon_button(panel, glyph, title,
                lambda name=key: self._choose_tool(self._last_tools.get(name, name)), action_font)
        options = self._icon_button(panel, "⋯", "截圖選項", self._context_menu)
        self._undo_button = self._icon_button(panel, "\ue7a7", "復原 (Ctrl+Z)", lambda: self._undo(False), True)
        self._redo_button = self._icon_button(panel, "\ue7a6", "重做 (Ctrl+Y)", lambda: self._undo(True), True)
        self._main_toolbar = [
            *self._group_buttons.values(), options, self._undo_button, self._redo_button,
            self._icon_button(panel, "\ue711", "取消 (Esc)", self._cancel, True),
            self._icon_button(panel, "\ue718", "套用 (Ctrl+T)" if self.editing else "貼到螢幕 (Ctrl+T)",
                              lambda: self._finish("apply" if self.editing else "sticker"), True),
            self._icon_button(panel, "\ue74e", "儲存至檔案 (Ctrl+S)", self._save, True),
            self._icon_button(panel, "\ue8c8", "複製並完成 (Ctrl+C／Enter)", lambda: self._finish("clipboard"), True),
        ]
        self._variant_buttons = {
            group: {tool: self._icon_button(panel, glyph, title, lambda value=tool: self._choose_variant(value))
                    for tool, glyph, title in variants}
            for group, variants in TOOL_VARIANTS.items()
        }
        self._shape_fill_buttons = [
            self._icon_button(panel, glyph, title, lambda value=filled: self._set_shape_fill(value))
            for filled, glyph, title in ((False, "▭", "空心"), (True, "▬", "實心"))
        ]
        self._filter_controls = [
            ttk.Checkbutton(panel, text="橢圓", variable=self._ellipse, style="Capture.TCheckbutton",
                            command=lambda: self._update_annotation_style(elliptical=self._ellipse.get())),
            self._toolbar_slider(panel, "強度", self._filter_strength, 4, 28, self._set_filter_strength),
        ]
        self._text_controls = self._build_text_controls(panel)
        self._stroke_controls = []
        for index, width in enumerate(self.settings.pen_sizes):
            button = self._icon_button(panel, str(width), "點擊套用粗細，滾輪調整此預設",
                                       lambda i=index: self._set_width(self.settings.pen_sizes[i]))
            button.bind("<MouseWheel>", lambda event, i=index, item=button: self._change_pen_preset(i, item, event))
            self._stroke_controls.append(button)
        self._stroke_controls.extend((
            self._toolbar_slider(panel, "粗細", self._width, 1, 32, self._set_width),
            ttk.Checkbutton(panel, text="虛線", variable=self._dashed, style="Capture.TCheckbutton",
                            command=lambda: self._update_annotation_style(dashed=self._dashed.get())),
        ))
        self._current_color_chip = tk.Frame(panel, width=21, height=21, bg=self._color,
                                            highlightthickness=1, highlightbackground=TOOLBAR_BORDER)
        self._palette_frame = ttk.Frame(panel, style="Capture.TFrame")
        self._refresh_palette()
        self._color_controls = [
            self._current_color_chip, self._palette_frame, self._icon_button(panel, "…", "更多色彩", self._choose_color),
            self._toolbar_slider(panel, "透明", self._opacity, 10, 100, self._set_opacity, "%"),
        ]
        self._context_widgets = [
            *(button for buttons in self._variant_buttons.values() for button in buttons.values()),
            *self._shape_fill_buttons, *self._filter_controls, *self._text_controls,
            *self._stroke_controls, *self._color_controls,
        ]
        return panel

    def _build_text_controls(self, parent):
        family = ttk.Combobox(parent, textvariable=self._font, state="readonly", width=19, style="Capture.TCombobox")
        family.configure(postcommand=lambda: family.configure(values=sorted(annotation_fonts(), key=str.casefold)))
        family.bind("<<ComboboxSelected>>", self._apply_text_style)
        size = ttk.Combobox(parent, textvariable=self._font_size, values=(14, 18, 22, 28, 36, 48, 64),
                            state="readonly", width=4, style="Capture.TCombobox")
        size.bind("<<ComboboxSelected>>", self._apply_text_style)
        return [
            ttk.Checkbutton(parent, text="粗體", variable=self._bold, style="Capture.TCheckbutton", command=self._apply_text_style),
            ttk.Checkbutton(parent, text="斜體", variable=self._italic, style="Capture.TCheckbutton", command=self._apply_text_style),
            ttk.Checkbutton(parent, text="底色", variable=self._text_background, style="Capture.TCheckbutton", command=self._apply_text_style),
            self._icon_button(parent, "描邊", "文字描邊顏色與粗細", self._input_outline), family, size,
        ]

    def _icon_button(self, parent, glyph, title, command, action_font=False):
        style = "CaptureAction.CaptureIcon.TButton" if action_font else "CaptureIcon.TButton"
        button = ttk.Button(parent, text=glyph, style=style, width=0, command=command)
        button.toolbar_icon = len(glyph) == 1
        button.bind("<Enter>", lambda _: self._queue_tooltip(button, title))
        button.bind("<Leave>", self._hide_tooltip)
        button.bind("<ButtonPress>", self._hide_tooltip)
        return button

    def _toolbar_slider(self, parent, title, variable, minimum, maximum, command, suffix=""):
        group = ttk.Frame(parent, style="Capture.TFrame")
        ttk.Label(group, text=title, style="Capture.TLabel").pack(side="left", padx=(0, 3))
        ttk.Scale(group, from_=minimum, to=maximum, variable=variable, length=74,
                  style="Capture.Horizontal.TScale", command=lambda value: command(round(float(value)))).pack(side="left")
        label = ttk.Label(group, text=f"{variable.get()}{suffix}", width=len(str(maximum)) + len(suffix),
                          anchor="e", style="Capture.TLabel")
        label.pack(side="left", padx=2)
        variable.trace_add("write", lambda *_: label.configure(text=f"{variable.get()}{suffix}"))
        return group

    def _refresh_palette(self):
        for child in self._palette_frame.winfo_children():
            child.destroy()
        for index, color in enumerate(self.settings.palette[:16]):
            chip = tk.Frame(self._palette_frame, width=16, height=16, background=color,
                            highlightthickness=1, highlightbackground="#777777", cursor="hand2")
            chip.grid(row=index // 8, column=index % 8, padx=1, pady=1)
            chip.bind("<Button-1>", lambda _, value=color: self._set_color(value))
            chip.bind("<ButtonRelease-1>", lambda event, source=index: self._reorder_palette(event, source))
            chip.bind("<Button-3>", self._palette_menu)
            chip.palette_index = index
        self._toolbar_layout = None

    def _reorder_palette(self, event, source):
        widget = self.top.winfo_containing(event.x_root, event.y_root)
        destination = getattr(widget, "palette_index", source)
        if destination == source:
            return
        colors = list(self.settings.palette)
        colors.insert(destination, colors.pop(source))
        self.settings.palette = tuple(colors)
        self._palette_dirty = True
        self._refresh_palette()
        self._draw()

    def _choose_color(self):
        _, color = colorchooser.askcolor(self._color, parent=self.top)
        if color:
            self._set_color(color)

    def _set_color(self, color):
        self._color = color
        self._current_color_chip.configure(bg=color)
        tool = self._context_tool()
        if self.settings.remember_colors and tool != "select":
            self.settings.tool_colors = {**(self.settings.tool_colors or {}), tool: color}
            self._palette_dirty = True
        self._update_annotation_style(color=color)

    def _set_width(self, value):
        self._width.set(value)
        self._update_annotation_style(width=value)

    def _set_opacity(self, value):
        self._opacity.set(value)
        self._update_annotation_style(opacity=round(value * 255 / 100))

    def _set_filter_strength(self, value):
        self._filter_strength.set(value)
        self._update_annotation_style(filter_strength=value)

    def _change_pen_preset(self, index, button, event):
        widths = list(self.settings.pen_sizes)
        widths[index] = max(1, min(32, widths[index] + (1 if event.delta > 0 else -1)))
        self.settings.pen_sizes = tuple(widths)
        self._palette_dirty = True
        button.configure(text=widths[index])
        self._toolbar_layout = None
        self._set_width(widths[index])
        self._draw()
        return "break"

    def _update_annotation_style(self, **style):
        if self._text_draft is not None:
            self._text_draft = replace(self._text_draft, **style)
            self._update_text_editor_geometry()
            self._draw()
            return
        if not self._selected:
            return
        self.document.replace(replace(item, **style) if index in self._selected else item
                              for index, item in enumerate(self.document.items))
        self._draw()

    def _choose_tool(self, tool):
        self._finish_text_editor()
        if self._tool != tool:
            self._commit_polyline()
        self._select_annotations(())
        self._tool = tool
        group = TOOL_GROUPS.get(tool)
        if group:
            self._last_tools[group] = tool
        if tool == "rounded":
            self._filled.set(False)
        if self.settings.remember_colors:
            self._color = (self.settings.tool_colors or {}).get(self._tool, self.settings.tool_color)
            self._current_color_chip.configure(bg=self._color)
        self._hide_tooltip()
        self.canvas.focus_set()
        self.canvas.configure(cursor="arrow" if tool == "select" else "crosshair")
        self._draw()
        return "break"

    def _selected_annotation(self):
        return self.document.items[next(iter(self._selected))] if len(self._selected) == 1 else None

    def _context_tool(self):
        item = self._text_draft or self._selected_annotation()
        return item.tool if item is not None else self._tool

    def _select_annotations(self, indexes):
        self._selected = set(indexes)
        item = self._selected_annotation()
        if item is not None:
            if self._drawing_style is None:
                variables = (self._width, self._opacity, self._filled, self._text_background, self._dashed,
                             self._ellipse, self._filter_strength, self._font, self._font_size, self._bold, self._italic)
                self._drawing_style = (self._color, self._outline, self._outline_width,
                                       tuple((variable, variable.get()) for variable in variables))
            self._color, self._outline, self._outline_width = item.color, item.outline, item.outline_width
            self._width.set(item.width)
            self._opacity.set(round(item.opacity * 100 / 255))
            self._filled.set(item.filled)
            self._text_background.set(item.filled)
            self._dashed.set(item.dashed)
            self._ellipse.set(item.elliptical)
            self._filter_strength.set(item.filter_strength if item.filter_strength is not None else 10)
            self._font.set(item.font_family)
            self._font_size.set(item.font_size)
            self._bold.set(item.bold)
            self._italic.set(item.italic)
        elif self._drawing_style is not None:
            self._color, self._outline, self._outline_width, values = self._drawing_style
            for variable, value in values:
                variable.set(value)
            self._drawing_style = None
        self._current_color_chip.configure(bg=self._color)

    def _choose_variant(self, tool):
        item = self._selected_annotation()
        if item is None or TOOL_GROUPS.get(item.tool) != TOOL_GROUPS[tool]:
            return self._choose_tool(tool)
        points = item.points if tool == "polyline" else (item.points[0], item.points[-1])
        filled = item.filled and tool != "rounded"
        self._filled.set(filled)
        self._update_annotation_style(tool=tool, points=points, filled=filled)
        return "break"

    def _set_shape_fill(self, filled):
        item = self._selected_annotation()
        if item is not None and TOOL_GROUPS.get(item.tool) == "shape":
            self._filled.set(filled)
            self._update_annotation_style(filled=filled, tool="ellipse" if item.tool == "ellipse" else "rectangle")
        else:
            self._choose_tool("ellipse" if self._last_tools["shape"] == "ellipse" else "rectangle")
            self._filled.set(filled)
            self._draw()

    def _apply_text_style(self, event=None):
        self._update_annotation_style(font_family=self._font.get(), font_size=self._number(self._font_size, 22, 8, 144),
                                      bold=self._bold.get(), italic=self._italic.get(), filled=self._text_background.get())

    def _local_point(self, event):
        return self._clamp_point((event.x_root - self.bounds["left"], event.y_root - self.bounds["top"]))

    def _clamp_point(self, point):
        return min(self.screenshot.width, max(0, point[0])), min(self.screenshot.height, max(0, point[1]))

    def _local_box(self, box):
        return (*self._clamp_point((box[0] - self.bounds["left"], box[1] - self.bounds["top"])),
                *self._clamp_point((box[2] - self.bounds["left"], box[3] - self.bounds["top"])))

    def _global_box(self, box):
        return box[0] + self.bounds["left"], box[1] + self.bounds["top"], box[2] + self.bounds["left"], box[3] + self.bounds["top"]

    def _monitor_at(self, x, y):
        return monitor_at((x, y), self.monitors)

    def _selection_scale(self):
        monitor = self._monitor_at(*self.top.winfo_pointerxy())
        return monitor.get("dpi_x", 96) / 96, monitor.get("dpi_y", 96) / 96

    def _selection_display(self):
        x, y = self.top.winfo_pointerxy()
        monitor = self._monitor_at(x, y)
        return self._local_box((monitor["left"], monitor["top"], monitor["left"] + monitor["width"],
                                monitor["top"] + monitor["height"]))

    def _set_screen(self):
        dim = ImageEnhance.Brightness(self.screenshot.convert("RGB")).enhance(0.55)
        self._screen_photo = ImageTk.PhotoImage(dim, master=self.top)
        self.canvas.itemconfigure(self._screen, image=self._screen_photo)

    def _set_selection(self, box):
        if not valid_box(box):
            return
        self._selection = box
        self._hover = None
        self._hide_magnifier()
        self._draw()
        if self.auto_action:
            self.top.after_idle(lambda: self._finish(self.auto_action))

    def _draw(self, temporary=None, resized_shape=None):
        box = temporary or self._selection or self._hover
        display = self._selection_display()
        selected = temporary is None and self._selection is not None
        if selected:
            self._draw_outside_annotations(box, resized_shape)
        else:
            self.canvas.itemconfigure(self._outside, state="hidden")
        if valid_box(box):
            image = self._render(box, resized_shape) if selected else self.screenshot.crop(box)
            self._crop_photo = ImageTk.PhotoImage(image, master=self.top)
            self.canvas.coords(self._crop, box[0], box[1])
            self.canvas.itemconfigure(self._crop, image=self._crop_photo, state="normal")
            self.canvas.coords(self._border, *box)
            self.canvas.itemconfigure(self._border, state="normal")
            width, height = box[2] - box[0], box[3] - box[1]
            if self._unit_dip:
                sx, sy = self._selection_scale()
                width_dip = f"{width / sx:.2f}".rstrip("0").rstrip(".")
                height_dip = f"{height / sy:.2f}".rstrip("0").rstrip(".")
                size = f"{width_dip} × {height_dip} DIP"
            else:
                size = f"{width} × {height} px"
            self.canvas.itemconfigure(self._info, text=size)
            self.canvas.coords(self._info, max(display[0] + 6, min(box[0], display[2] - 120)),
                               max(display[1] + 6, min(box[1] - 22, display[3] - 20)))
            if selected:
                toolbar_w, toolbar_h = self._layout_toolbar(display)
                x = max(display[0], min(box[0], display[2] - toolbar_w))
                y = box[3] + 8 if box[3] + toolbar_h + 8 <= display[3] else box[1] - toolbar_h - 8
                self.canvas.coords(self._toolbar_id, x, max(display[1], min(y, display[3] - toolbar_h)))
                self.canvas.itemconfigure(self._toolbar_id, state="normal")
            else:
                self.canvas.itemconfigure(self._toolbar_id, state="hidden")
            self._show_handles(box, selected and not self.editing)
        else:
            for item in (self._crop, self._border, self._toolbar_id, *self._handles):
                self.canvas.itemconfigure(item, state="hidden")
            self.canvas.coords(self._info, display[0] + 16, display[1] + 16)
            self.canvas.itemconfigure(self._info, text="拖曳框選　F 選目前螢幕　Shift+F 選全部　右鍵開啟選項")
        self._draw_selected_annotations(resized_shape=resized_shape)

    def _layout_toolbar(self, display):
        scale, _ = self._selection_scale()
        available = display[2] - display[0]
        active = self._context_tool()
        group = TOOL_GROUPS.get(active, active)
        key = (scale, available, active)
        self._undo_button.state(("!disabled",) if self.document.can_undo else ("disabled",))
        self._redo_button.state(("!disabled",) if self.document.can_redo else ("disabled",))
        for name, button in self._group_buttons.items():
            button.state(("selected",) if name == group else ("!selected",))
        for buttons in self._variant_buttons.values():
            for tool, button in buttons.items():
                button.state(("selected",) if tool == active else ("!selected",))
        for filled, button in enumerate(self._shape_fill_buttons):
            chosen = active in ("rectangle", "ellipse") and self._filled.get() == bool(filled)
            button.state(("selected",) if chosen else ("!selected",))
        if self._toolbar_layout == key:
            return self._toolbar_size

        self._toolbar_icon_font.configure(size=-round(17 * scale))
        self._toolbar_action_font.configure(size=-round(15 * scale))
        for widget in self._context_widgets:
            widget.place_forget()
        context = list(self._variant_buttons.get(group, {}).values())
        if group == "shape":
            context = [*self._shape_fill_buttons, *context]
        elif group == "filter":
            context.extend(self._filter_controls)
        elif group == "text":
            context.extend(self._text_controls)
        if group in ("shape", "line", "pen", "highlighter"):
            context.extend(self._stroke_controls)
        if group in ("shape", "line", "pen", "highlighter", "text", "number"):
            context.extend(self._color_controls)

        self._toolbar.update_idletasks()
        inset = max(3, round(4 * scale))
        gap = max(1, round(2 * scale))
        y, total_width = inset, 0
        for widgets, icon_width, icon_height in ((self._main_toolbar, 34, 32), (context, 29, 27)):
            if not widgets:
                continue
            x, row_height = inset, 0
            for widget in widgets:
                width, height = widget.winfo_reqwidth(), widget.winfo_reqheight()
                if getattr(widget, "toolbar_icon", False):
                    width, height = round(icon_width * scale), round(icon_height * scale)
                width = min(width, max(1, available - 2 * inset))
                if x > inset and x + width + inset > available:
                    x, y, row_height = inset, y + row_height + gap, 0
                widget.place(x=x, y=y, width=width, height=height)
                x += width + gap
                row_height = max(row_height, height)
                total_width = max(total_width, x - gap + inset)
            y += row_height + gap
        self._toolbar_size = total_width, y - gap + inset
        self._toolbar.configure(width=self._toolbar_size[0], height=self._toolbar_size[1])
        self._toolbar_layout = key
        return self._toolbar_size

    def _queue_tooltip(self, widget, title):
        self._hide_tooltip()

        def show():
            self._toolbar_tip_timer = None
            if not widget.winfo_ismapped():
                return
            tip = self._toolbar_tip = tk.Toplevel(self.top)
            tip.withdraw()
            tip.overrideredirect(True)
            tip.attributes("-topmost", True)
            tk.Label(tip, text=title, background="#252525", foreground="white", padx=7, pady=4).pack()
            tip.update_idletasks()
            x, y = widget.winfo_rootx(), widget.winfo_rooty()
            monitor = self._monitor_at(x, y)
            width, height = tip.winfo_reqwidth(), tip.winfo_reqheight()
            x = max(monitor["left"], min(x, monitor["left"] + monitor["width"] - width))
            y -= height + 6
            if y < monitor["top"]:
                y = widget.winfo_rooty() + widget.winfo_height() + 6
            tip.deiconify()
            position_window(tip, x, y, width, height)

        self._toolbar_tip_timer = self.top.after(400, show)

    def _hide_tooltip(self, event=None):
        if self._toolbar_tip_timer is not None:
            self.top.after_cancel(self._toolbar_tip_timer)
            self._toolbar_tip_timer = None
        if self._toolbar_tip is not None:
            self._toolbar_tip.destroy()
            self._toolbar_tip = None

    def _show_handles(self, box, visible):
        l, t, r, b = box
        for handle, (x, y) in zip(self._handles, ((l, t), ((l + r) / 2, t), (r, t), (l, (t + b) / 2),
                                                (r, (t + b) / 2), (l, b), ((l + r) / 2, b), (r, b))):
            self.canvas.coords(handle, x - 3, y - 3, x + 3, y + 3)
            self.canvas.itemconfigure(handle, state="normal" if visible else "hidden")

    def _draw_selected_annotations(self, dx=0, dy=0, resized_shape=None):
        self.canvas.delete("selected-annotation")
        if self._text_draft is not None:
            self._draw_text_handles(self._text_draft)
        for index in self._selected:
            if index == self._text_index or not 0 <= index < len(self.document.items):
                continue
            item = resized_shape[1] if resized_shape is not None and resized_shape[0] == index else self.document.items[index]
            if len(self._selected) == 1 and item.tool == "text" and self._selection:
                self._draw_text_handles(item, dx, dy)
            elif len(self._selected) == 1 and item.tool in SHAPE_TOOLS and self._selection:
                for handle in SHAPE_HANDLES:
                    x, y = item.shape_handle(*handle)
                    x, y = x + dx, y + dy
                    self.canvas.create_oval(x - 3, y - 3, x + 3, y + 3, fill="#27a7e7", outline="white",
                                            tags="selected-annotation")
            else:
                left, top, right, bottom = item.bounds()
                self.canvas.create_rectangle(left + dx, top + dy, right + dx, bottom + dy,
                                             outline="#27a7e7", dash=(3, 2),
                                             tags="selected-annotation")
        self.canvas.tag_raise(self._toolbar_id)

    def _draw_text_handles(self, item, dx=0, dy=0):
        corners = [(x + dx, y + dy) for x, y in (item.shape_handle(*handle) for handle in TEXT_HANDLES)]
        self.canvas.create_polygon(corners, fill="", outline="black", tags="selected-annotation")
        radius = TEXT_HANDLE_RADIUS
        for handle, (x, y) in zip(TEXT_HANDLES, corners):
            self.canvas.create_oval(x - radius, y - radius, x + radius + 1, y + radius + 1,
                                    fill="#1a84ff", outline="white", tags="selected-annotation")
            if handle == TEXT_DELETE_CORNER:
                self.canvas.create_line(x - 2, y - 2, x + 2, y + 2, fill="white", tags="selected-annotation")
                self.canvas.create_line(x + 2, y - 2, x - 2, y + 2, fill="white", tags="selected-annotation")
            else:
                vx, vy = handle
                self.canvas.create_line(x - vx * 3, y - vy * 3, x + vx * 3, y + vy * 3, fill="white",
                                        arrow="both", arrowshape=(2, 3, 1), tags="selected-annotation")

    def _inside_capture(self, point):
        left, top, right, bottom = self._selection
        return left <= point[0] <= right and top <= point[1] <= bottom

    def _hit_annotation_handle(self, point):
        item = self._text_draft or self._selected_annotation()
        if not self._selection or item is None or self._tool == "erase" or self._polyline:
            return None
        handles = TEXT_HANDLES if item.tool == "text" else SHAPE_HANDLES if item.tool in SHAPE_TOOLS else ()
        radius = TEXT_HANDLE_RADIUS + 1 if item.tool == "text" else 6
        hits = []
        for handle in handles:
            x, y = item.shape_handle(*handle)
            distance = (x - point[0]) ** 2 + (y - point[1]) ** 2
            if distance <= radius ** 2:
                hits.append((distance, handle))
        return min(hits)[1] if hits else None

    def _is_text_delete_handle(self, handle):
        item = self._text_draft or self._selected_annotation()
        return item is not None and item.tool == "text" and handle == TEXT_DELETE_CORNER

    def _in_text_frame(self, point):
        item = self._text_draft or self._selected_annotation()
        if item is None or item.tool != "text" or self._tool == "erase":
            return False
        cx, cy = item.shape_handle(0, 0)
        x, y = rotate_offset(point[0] - cx, point[1] - cy, -item.angle)
        return contains(item.text_box(), (cx + x, cy + y))

    def _update_pointer_cursor(self, point):
        handle = self._hit_annotation_handle(point)
        cursor = "arrow" if self._tool == "select" else "crosshair"
        if handle is not None:
            cursor = ("hand2" if self._is_text_delete_handle(handle) else
                      self._resize_cursor(handle, (self._text_draft or self._selected_annotation()).angle))
        elif self._in_text_frame(point):
            cursor = "fleur"
        if self.canvas.cget("cursor") != cursor:
            self.canvas.configure(cursor=cursor)
        if self._text_editor is not None:
            self._text_editor.configure(cursor=cursor if cursor != "crosshair" else "xterm")

    @staticmethod
    def _resize_cursor(handle, angle):
        x, y = rotate_offset(*handle, angle)
        return RESIZE_CURSORS[round(degrees(atan2(y, x)) / 45) % 4]

    def _resized_shape(self, point):
        index, item, handle = self._shape_resize
        dx, dy = point[0] - self._start[0], point[1] - self._start[1]
        return index, item.resize_text(handle, dx, dy) if item.tool == "text" else item.resize_shape(handle, dx, dy)

    def _preview_annotations(self, resized_shape):
        items = self.document.items
        if resized_shape is not None:
            index, item = resized_shape
            items = (*items[:index], item, *items[index + 1:])
        if self._text_index is not None:
            items = (*items[:self._text_index], *items[self._text_index + 1:])
        return items

    def _draw_outside_annotations(self, box, resized_shape):
        left, top, right, bottom = box
        items = []
        bounds = []
        for item in self._preview_annotations(resized_shape):
            if item.tool not in OUTSIDE_SELECTION_TOOLS:
                continue
            mark = intersection(item.visual_bounds(), (0, 0, *self.screenshot.size))
            if valid_box(mark) and (mark[0] < left or mark[1] < top or mark[2] > right or mark[3] > bottom):
                items.append(item)
                bounds.append(mark)
        if not bounds:
            self.canvas.itemconfigure(self._outside, state="hidden")
            return
        region = (min(b[0] for b in bounds), min(b[1] for b in bounds),
                  max(b[2] for b in bounds), max(b[3] for b in bounds))
        image = render_annotations(self.screenshot, region, items)
        image = ImageEnhance.Brightness(image).enhance(0.55)
        overlap = intersection(region, box)
        if valid_box(overlap):
            x, y = region[:2]
            image.paste((0, 0, 0, 0), (overlap[0] - x, overlap[1] - y, overlap[2] - x, overlap[3] - y))
        self._outside_photo = ImageTk.PhotoImage(image, master=self.top)
        self.canvas.coords(self._outside, *region[:2])
        self.canvas.itemconfigure(self._outside, image=self._outside_photo, state="normal")

    def _render(self, box=None, resized_shape=None):
        box = box or self._selection
        items = self._preview_annotations(resized_shape)
        if self._board or self._replay_image is not None:
            source = self._replay_image or Image.new("RGBA", (box[2] - box[0], box[3] - box[1]), self._board)
            if source.size != (box[2] - box[0], box[3] - box[1]):
                source = source.resize((box[2] - box[0], box[3] - box[1]), RESAMPLE.LANCZOS)
            annotations = [replace(item, points=tuple((x - box[0], y - box[1]) for x, y in item.points))
                           for item in items]
            return render_annotations(source, (0, 0, *source.size), annotations)
        return render_annotations(self.screenshot, box, items)

    def _candidate(self, event):
        point = (event.x_root, event.y_root)
        for window, children in self.candidates:
            if not contains(window, point):
                continue
            containing = [child for child in children if contains(child, point)]
            return window, sorted(containing, key=lambda box: (box[2] - box[0]) * (box[3] - box[1]))
        return None, []

    def update_candidates(self, candidates):
        self.candidates = candidates
        if not self._selection and not self._drag:
            x, y = self.top.winfo_pointerxy()
            self._on_motion(SimpleNamespace(x_root=x, y_root=y, state=0))

    def _on_motion(self, event):
        if self._drag:
            return
        self._update_pointer_cursor(self._local_point(event))
        if not self._selection and not self.editing:
            window, children = self._candidate(event)
            box = children[0] if children else window
            display = self._monitor_at(event.x_root, event.y_root)
            candidate = self._local_box(box) if box and (box[2] - box[0]) * (box[3] - box[1]) <= (
                display["width"] * display["height"] / 2) else None
            if candidate != self._hover:
                self._hover = candidate
                self._draw()
        self._show_magnifier(event)

    def _show_magnifier(self, event):
        if (self._selection or not self.settings.show_magnifier) and not event.state & 0x20008:
            self._hide_magnifier()
            return
        x, y = self._local_point(event)
        x, y = min(x, self.screenshot.width - 1), min(y, self.screenshot.height - 1)
        size = self.settings.magnifier_size
        radius = self.settings.magnifier_radius
        detail = self.screenshot.crop((x - radius, y - radius, x + radius + 1, y + radius + 1)).resize(
            (size, size), RESAMPLE.NEAREST)
        if self.settings.magnifier_crosshair:
            from PIL import ImageDraw
            draw = ImageDraw.Draw(detail)
            middle = size // 2
            draw.line((middle, 0, middle, size), fill="white", width=1)
            draw.line((0, middle, size, middle), fill="white", width=1)
        self._magnifier_photo = ImageTk.PhotoImage(detail, master=self.top)
        left, top, right, bottom = self._selection_display()
        px = max(left, min(x + 20 if x + 20 + size <= right else x - size - 20, right - size))
        py = max(top, min(y + 20 if y + size + 45 <= bottom else y - size - 45, bottom - size - 24))
        rgb = self.screenshot.getpixel((x, y))[:3]
        label = "#{:02X}{:02X}{:02X}".format(*rgb) if event.state & 1 else f"RGB {rgb}"
        self.canvas.coords(self._magnifier, px, py)
        self.canvas.itemconfigure(self._magnifier, image=self._magnifier_photo, state="normal")
        self.canvas.coords(self._magnifier_background, px, py + size, px + size, py + size + 22)
        self.canvas.itemconfigure(self._magnifier_background, state="normal")
        self.canvas.coords(self._magnifier_label, px, py + size + 2)
        self.canvas.itemconfigure(self._magnifier_label, text=label, state="normal")

    def _hide_magnifier(self):
        for item in (self._magnifier, self._magnifier_background, self._magnifier_label):
            self.canvas.itemconfigure(item, state="hidden")

    def _on_press(self, event):
        point = self._local_point(event)
        active_handle = self._hit_annotation_handle(point)
        if self._is_text_delete_handle(active_handle):
            editing_new = self._text_editor is not None and self._text_index is None
            self._finish_text_editor(commit=False)
            if not editing_new:
                self._delete_annotations()
            self.canvas.focus_set()
            self._draw()
            self._update_pointer_cursor(point)
            return "break"
        if active_handle is None and self._text_editor is not None and self._in_text_frame(point):
            self._start_text_move(point)
            return "break"
        self._resume_text_resize = (self._text_editor is not None and
                                    bool(self._text_editor.get("1.0", "end-1c").strip()) and active_handle is not None)
        self._finish_text_editor()
        self.canvas.focus_set()
        self._start = point
        self._initial_selection = self._selection
        self._initial_hover = self._hover
        self._shift = bool(event.state & 1)
        self._clone = bool(event.state & 4)
        self._points = [point]
        handle = self._hit_annotation_handle(point)
        if handle is not None and not self._is_text_delete_handle(handle):
            index = next(iter(self._selected))
            item = self.document.items[index]
            self._shape_resize = (index, item, handle)
            self._drag = "resize-annotation"
            self.canvas.configure(cursor=self._resize_cursor(handle, item.angle))
            return
        inside = self._selection and self._inside_capture(point)
        if self._selection and self._tool != "erase" and not self._polyline:
            hit = self._hit_annotation(point, precise=self._tool != "select")
            if hit is not None:
                if self._shift:
                    self._select_annotations(self._selected ^ {hit})
                    self._draw()
                    return
                if hit not in self._selected:
                    self._select_annotations({hit})
                self._drag = "move-annotation"
                self._draw()
                return
        if self._selection and self._tool == "select":
            if inside and (self._shift or self._free_select.get()):
                self._drag = "select-annotations"
                return
            if self._selected:
                self._select_annotations(())
                self._draw()
            if not self.editing:
                self._edges = self._hit_edges(point)
                if any(self._edges):
                    self._drag = "resize"
                    return
                if inside:
                    self._drag = "move"
                    return
        if self._selection and self._tool != "select" and (inside or self._tool in OUTSIDE_SELECTION_TOOLS):
            if self._selected:
                self._select_annotations(())
                self._draw()
            if self._tool == "text":
                self._drag = "draw-text"
            elif self._tool == "number":
                number = max((int(item.text) for item in self.document.items if item.tool == "number"), default=0) + 1
                self._add_annotation(self._annotation("number", (point,), str(number)))
                self._draw()
            elif self._tool == "polyline":
                self._polyline.append(point)
                self._draw_polyline_preview()
            elif self._tool == "erase" and not self._round_eraser.get():
                hit = self.document.hit(point)
                if hit is not None:
                    self._select_annotations({hit})
                    self._delete_annotations()
            else:
                self._drag = "draw"
            return
        if not self.editing:
            self._select_annotations(())
            self._drag = "new"
            self._selection = None
            self._hover = None
            self._replay_image = None

    def _on_double_click(self, event):
        point = self._local_point(event)
        if self._hit_annotation_handle(point) is not None:
            return self._on_press(event)
        if self._selection and self._tool != "erase" and not self._polyline:
            hit = self._hit_annotation(point, precise=self._tool != "select")
            if hit is not None and self.document.items[hit].tool == "text":
                self._drag = None
                self._select_annotations({hit})
                self._draw()
                return self._edit_annotation_text()
        return self._on_press(event)

    def _hit_annotation(self, point, precise=False):
        tools = None if self._inside_capture(point) else OUTSIDE_SELECTION_TOOLS
        return self.document.hit(point, precise=precise, tools=tools)

    def _hit_edges(self, point):
        l, t, r, b = self._selection
        x, y = point
        if not (l - 6 <= x <= r + 6 and t - 6 <= y <= b + 6):
            return False, False, False, False
        return abs(x - l) <= 6, abs(y - t) <= 6, abs(x - r) <= 6, abs(y - b) <= 6

    def _drag_box(self, point):
        l, t, r, b = self._initial_selection or (0, 0, 0, 0)
        if self._drag == "new":
            if self._ratio:
                dx, dy = point[0] - self._start[0], point[1] - self._start[1]
                width = min(abs(dx), abs(dy) * self._ratio)
                point = (self._start[0] + round(width) * (1 if dx >= 0 else -1),
                         self._start[1] + round(width / self._ratio) * (1 if dy >= 0 else -1))
            return ordered_box(self._start, point)
        if self._drag == "resize":
            left, top, right, bottom = self._edges
            return ordered_box((point[0] if left else l, point[1] if top else t),
                               (point[0] if right else r, point[1] if bottom else b))
        dx = min(self.screenshot.width - r, max(-l, point[0] - self._start[0]))
        dy = min(self.screenshot.height - b, max(-t, point[1] - self._start[1]))
        return l + dx, t + dy, r + dx, b + dy

    def _on_drag(self, event):
        if not self._drag:
            return
        point = self._local_point(event)
        self.canvas.delete("draw-preview")
        if self._drag in ("new", "move", "resize"):
            self._draw(self._drag_box(point))
        elif self._drag == "move-inline-text":
            self._move_inline_text(point)
        elif self._drag == "move-annotation":
            dx, dy = point[0] - self._start[0], point[1] - self._start[1]
            item = self._selected_annotation()
            if item is not None and item.tool in OUTSIDE_SELECTION_TOOLS and not self._clone:
                moved = replace(item, points=tuple((x + dx, y + dy) for x, y in item.points))
                self._draw(resized_shape=(next(iter(self._selected)), moved))
            else:
                self._draw_selected_annotations(dx, dy)
        elif self._drag == "resize-annotation":
            self._draw(resized_shape=self._resized_shape(point))
        elif self._drag in ("select-annotations", "draw-text"):
            self.canvas.create_rectangle(*ordered_box(self._start, point), outline="#27a7e7", dash=(4, 2),
                                         tags="draw-preview")
        elif self._tool == "pen":
            self._points.append(point)
            self.canvas.create_line(*[value for pair in self._points for value in pair], fill=self._color,
                                    width=self._number(self._width, 3, 1, 32), tags="draw-preview")
        elif self._tool in ("line", "arrow", "double_arrow", "filled_arrow", "outline_arrow", "marker"):
            self.canvas.create_line(*self._start, *point, fill=self._color,
                                    width=self._number(self._width, 3, 1, 32), tags="draw-preview")
        else:
            ellipse = self._tool in ("ellipse", "marker_ellipse", "magnify") or (
                TOOL_GROUPS.get(self._tool) == "filter" and self._ellipse.get()) or self._tool == "erase"
            draw = self.canvas.create_oval if ellipse else self.canvas.create_rectangle
            draw(*ordered_box(self._start, point), outline=self._color, width=2, tags="draw-preview")

    def _on_release(self, event):
        if not self._drag:
            return
        point = self._local_point(event)
        self.canvas.delete("draw-preview")
        mode = self._drag
        if mode == "move-inline-text":
            return self._end_text_move(point)
        if mode in ("new", "move", "resize"):
            box = self._drag_box(point)
            if mode == "new" and min(box[2] - box[0], box[3] - box[1]) < 3:
                box = self._initial_hover
            if mode == "new":
                self.document = Annotations()
                self._select_annotations(())
            self._drag = None
            if valid_box(box):
                self._set_selection(box)
            else:
                self._draw()
            return
        if mode == "select-annotations" or mode == "draw" and self._tool == "erase":
            box = ordered_box(self._start, point)
            self._select_annotations(index for index, item in enumerate(self.document.items)
                                     if self._hits_annotation(box, item.bounds(), self._tool == "erase"))
            if self._tool == "erase":
                self._delete_annotations()
        elif mode == "move-annotation":
            self._move_annotations(point[0] - self._start[0], point[1] - self._start[1], clone=self._clone)
        elif mode == "draw-text":
            left, top, right, bottom = ordered_box(self._start, point)
            drawn_height = bottom - top
            if right - left < 8:
                right = min(self.screenshot.width, left + 220)
                left = max(0, min(left, right - 8))
            if bottom - top < 8:
                bottom = min(self.screenshot.height, top + 44)
                top = max(0, min(top, bottom - 8))
            self._drag = None
            item = self._annotation("text", ((left, top), (max(left + 8, right), max(top + 8, bottom))))
            if drawn_height >= 8:
                item = replace(item, font_size=self._fit_text_font_size(item, drawn_height))
            self._begin_text_editor(item)
            return
        elif mode == "resize-annotation":
            if self.canvas.grab_current() == self.canvas:
                self.canvas.grab_release()
            index, item = self._resized_shape(point)
            items = self.document.items
            self.document.replace((*items[:index], item, *items[index + 1:]))
            self._shape_resize = None
            if item.tool == "text":
                self._select_annotations({index})
                if self._resume_text_resize:
                    self._resume_text_resize = False
                    self._drag = None
                    self._begin_text_editor(item, index)
                    return
        elif mode == "draw":
            if self._tool not in SHAPE_TOOLS or valid_box(ordered_box(self._start, point)):
                self._add_annotation(self._annotation(self._tool, (*self._points, point)))
        self._drag = None
        self._draw()
        self._update_pointer_cursor(point)

    def _move_annotations(self, dx, dy, clone=False):
        if not (dx or dy):
            return
        items = list(self.document.items)
        moved = {index: replace(items[index], points=tuple((x + dx, y + dy) for x, y in items[index].points))
                 for index in sorted(self._selected)}
        if clone:
            self.document.replace((*items, *moved.values()))
            self._select_annotations(range(len(items), len(self.document.items)))
        else:
            self.document.replace(moved.get(index, item) for index, item in enumerate(items))

    @staticmethod
    def _number(variable, default, minimum, maximum):
        try:
            return max(minimum, min(maximum, variable.get()))
        except tk.TclError:
            return default

    @staticmethod
    def _hits_annotation(box, item, round_eraser):
        if not valid_box(intersection(box, item)):
            return False
        if not round_eraser:
            return True
        rx, ry = (box[2] - box[0]) / 2, (box[3] - box[1]) / 2
        x, y = box[0] + rx, box[1] + ry
        dx = max(item[0] - x, x - item[2], 0) / rx
        dy = max(item[1] - y, y - item[3], 0) / ry
        return dx * dx + dy * dy <= 1

    def _annotation(self, tool, points, text=""):
        return Annotation(tool, points, self._color, self._number(self._width, 3, 1, 32),
                          round(self._number(self._opacity, 100, 10, 100) * 2.55), text,
                          font_size=self._number(self._font_size, 22, 8, 144), font_family=self._font.get(),
                          bold=self._bold.get(), italic=self._italic.get(), outline=self._outline,
                          outline_width=self._outline_width,
                          filled=self._text_background.get() if tool == "text" else self._filled.get(),
                          dashed=self._dashed.get(), elliptical=self._ellipse.get(),
                          filter_strength=self._filter_strength.get())

    def _draw_polyline_preview(self):
        self.canvas.delete("polyline-preview")
        if len(self._polyline) > 1:
            self.canvas.create_line(*[value for pair in self._polyline for value in pair], fill=self._color,
                                    width=self._number(self._width, 3, 1, 32), tags="polyline-preview")

    def _commit_polyline(self):
        if len(self._polyline) > 1:
            self._add_annotation(self._annotation("polyline", tuple(self._polyline)))
        self._polyline.clear()
        self.canvas.delete("polyline-preview")

    def _add_annotation(self, annotation):
        self.document.add(annotation)
        if annotation.tool in SHAPE_TOOLS or self.settings.select_new_annotation:
            self._select_annotations({len(self.document.items) - 1})

    def _tab_candidate(self, event):
        if not self._selection:
            x, y = self.top.winfo_pointerxy()
            self._cycle_candidate(SimpleNamespace(x_root=x, y_root=y, delta=1), wrap=True)
            return "break"

    def _cycle_candidate(self, event, wrap=False):
        if self._selection:
            self._set_width(max(1, min(32, self._number(self._width, 3, 1, 32) + (1 if event.delta > 0 else -1))))
            return
        window, children = self._candidate(event)
        if window:
            options = list(dict.fromkeys(self._local_box(box) for box in [*children, window]))
            index = options.index(self._hover) if self._hover in options else 0
            next_index = index + (1 if event.delta > 0 else -1)
            self._hover = options[next_index % len(options) if wrap else max(0, min(len(options) - 1, next_index))]
            self._draw()

    def _select_monitor(self, whole):
        if not self.editing:
            self._replay_image = None
            self._set_selection((0, 0, *self.screenshot.size) if whole else self._selection_display())
        return "break"

    def _select_window(self, event):
        if not self.editing:
            point = self.top.winfo_pointerxy()
            window = next((area for area, _ in self.candidates if contains(area, point)), self.active_window)
            if window:
                self._set_selection(self._local_box(window))
        return "break"

    def _nudge(self, event, dx, dy):
        if self._drag == "resize-annotation":
            return "break"
        if self._selection and self._selected:
            self._move_annotations(dx, dy)
            self._draw()
        elif not self.editing and self._selection:
            l, t, r, b = self._selection
            dx, dy = min(self.screenshot.width - r, max(-l, dx)), min(self.screenshot.height - b, max(-t, dy))
            self._set_selection((l + dx, t + dy, r + dx, b + dy))
        elif not self.editing:
            x, y = self.top.winfo_pointerxy()
            user32.SetCursorPos(x + dx, y + dy)
        return "break"

    def _undo(self, redo):
        self._finish_text_editor()
        (self.document.redo if redo else self.document.undo)()
        self._select_annotations(())
        self._draw()
        return "break"

    def _delete_annotations(self, event=None):
        self.document.replace(item for index, item in enumerate(self.document.items) if index not in self._selected)
        self._select_annotations(())
        self._draw()
        return "break"

    def _copy_annotations(self, event=None):
        self._clipboard = tuple(item for index, item in enumerate(self.document.items) if index in self._selected)
        return "break"

    def _paste_annotations(self, event=None):
        if self._selection and self._clipboard:
            copies = tuple(replace(item, points=tuple((x + 12, y + 12) for x, y in item.points)) for item in self._clipboard)
            start = len(self.document.items)
            self.document.replace((*self.document.items, *copies))
            self._select_annotations(range(start, len(self.document.items)))
            self._draw()
        return "break"

    def _rotate_annotations(self):
        angle = simpledialog.askfloat("旋轉標註", "輸入要增加的角度", parent=self.top, minvalue=-360, maxvalue=360)
        if angle is not None:
            self.document.replace(replace(item, angle=item.angle + angle) if index in self._selected else item
                                  for index, item in enumerate(self.document.items))
            self._draw()

    def _copy_color(self, event):
        try:
            x_root, y_root = self.top.winfo_pointerxy()
            x, y = self._local_point(SimpleNamespace(x_root=x_root, y_root=y_root))
            pixel = self.screenshot.getpixel((min(x, self.screenshot.width - 1), min(y, self.screenshot.height - 1)))[:3]
            copy_text("#{:02X}{:02X}{:02X}".format(*pixel) if event.state & 1 else f"RGB {pixel}")
        except OSError as error:
            messagebox.showerror("MySnip", str(error), parent=self.top)
        else:
            self._close(None, None, None)
        return "break"

    def _replay(self, delta):
        if self.editing or not self.history:
            return "break"
        records = self.history.entries()
        if records:
            self._history_index = max(0, min(len(records) - 1, self._history_index + delta))
            entry = records[self._history_index]
            try:
                image = self.history.open(entry)
                l, t, _, _ = self._local_box(entry["region"])
                width, height = min(image.width, self.screenshot.width), min(image.height, self.screenshot.height)
                l, t = min(l, self.screenshot.width - width), min(t, self.screenshot.height - height)
                self.document = Annotations()
                self._select_annotations(())
                self._board = None
                self._replay_image = image
                self._set_selection((l, t, l + width, t + height))
            except (OSError, ValueError) as error:
                messagebox.showerror("MySnip", str(error), parent=self.top)
        return "break"

    def _refresh(self, event=None):
        if self.editing or not self.refresh or self._refreshing:
            return "break"
        self._refreshing = True
        self.top.withdraw()

        def update():
            try:
                image, bounds, _ = self.refresh()
                if bounds != self.bounds:
                    raise OSError("螢幕配置已變更，請重新開始截圖。")
                self.screenshot = image
                self._replay_image = None
                self._set_screen()
            except OSError as error:
                messagebox.showerror("MySnip", str(error), parent=self.root)
            finally:
                self._refreshing = False
                if not self._closed:
                    self._show()
                    self._draw()

        self.root.after(140, update)
        return "break"

    def _context_menu(self, event=None):
        if self._text_editor is not None:
            self._end_text_input()
        if self._polyline:
            self._commit_polyline()
            self._draw()
            return
        if event is not None and self._selection:
            hit = self._hit_annotation(self._local_point(event))
            if hit is not None and hit not in self._selected:
                self._select_annotations({hit})
                self._draw()
        menu = tk.Menu(self.top, tearoff=False)
        if self._selected:
            menu.add_command(label="複製標註 Ctrl+Shift+C", command=self._copy_annotations)
            menu.add_command(label="貼上標註 Ctrl+Shift+V", command=self._paste_annotations)
            if any(self.document.items[index].tool == "text" for index in self._selected):
                menu.add_command(label="修改文字…", command=self._edit_annotation_text)
            menu.add_command(label="旋轉標註…", command=self._rotate_annotations)
            menu.add_command(label="套用目前色彩、粗細與透明度",
                             command=lambda: self._update_annotation_style(
                                 color=self._color, width=self._width.get(), opacity=round(self._opacity.get() * 255 / 100)))
            menu.add_command(label="刪除標註", command=self._delete_annotations)
            menu.add_separator()
        menu.add_command(label="選取目前螢幕 F", command=lambda: self._select_monitor(False))
        menu.add_command(label="選取全部螢幕 Shift+F", command=lambda: self._select_monitor(True))
        menu.add_command(label="選取開始截圖前的視窗", command=lambda: self._set_selection(self._local_box(self.active_window))
                         if self.active_window and not self.editing else None)
        menu.add_command(label="輸入截圖尺寸…", command=self._input_size)
        menu.add_command(label="固定比例…", command=self._input_ratio)
        menu.add_command(label="切換 px／DIP", command=self._toggle_unit)
        menu.add_command(label="重新擷取桌面 R", command=self._refresh)
        menu.add_command(label="切換 UI 元素範圍 Tab", command=lambda: self._tab_candidate(None))
        menu.add_separator()
        for label, board in (("桌面", None), ("白板", "white"), ("透明白板", (0, 0, 0, 0))):
            menu.add_command(label=label, command=lambda value=board: self._set_board(value))
        menu.add_command(label="文字描邊…", command=self._input_outline)
        menu.add_command(label="選取／移動 (V)", command=lambda: self._choose_tool("select"))
        menu.add_command(label="編號標註 (N)", command=lambda: self._choose_tool("number"),
                         state="normal" if self._selection else "disabled")
        menu.add_checkbutton(label="自由選取標註", variable=self._free_select,
                             command=lambda: self._choose_tool("select") if self._free_select.get() else None)
        menu.add_checkbutton(label="圓形橡皮擦", variable=self._round_eraser,
                             command=lambda: self._choose_tool("erase") if self._selection else None,
                             state="normal" if self._selection else "disabled")
        menu.add_command(label="自訂色盤…", command=self._edit_palette)
        menu.add_command(label="匯入色盤…", command=self._import_palette)
        menu.add_command(label="匯出色盤…", command=self._export_palette)
        menu.add_separator()
        for label, key in (("輸出圓角", "rounded"), ("輸出外框", "border"), ("輸出陰影", "shadow")):
            menu.add_checkbutton(label=label, variable=self._decoration[key])
        for label, action in (("快速儲存 PNG", "quick-save"), ("複製圖片與 PNG 檔案", "copy-file"),
                              ("在其他應用程式開啟", "open"), ("列印", "print")):
            menu.add_command(label=label, command=lambda value=action: self._finish(value),
                             state="normal" if self._selection else "disabled")
        menu.add_command(label="使用 Windows 分享…", command=self._share_selection,
                         state="normal" if self._selection else "disabled")
        for label, codes in (("擷取文字（OCR）…", False), ("讀取 QR Code／條碼…", True)):
            menu.add_command(label=label, command=lambda value=codes: self.on_recognize(self, self._render(), value),
                             state="normal" if self._selection and self.on_recognize else "disabled")
        menu.add_separator()
        menu.add_command(label="上一筆截圖歷史 ,", command=lambda: self._replay(1))
        menu.add_command(label="下一筆截圖歷史 .", command=lambda: self._replay(-1))
        menu.add_command(label="複製游標色碼 C", command=lambda: self._copy_color(SimpleNamespace(state=0)))
        x, y = self.top.winfo_pointerxy()
        try:
            menu.tk_popup(x, y)
        finally:
            menu.grab_release()

    def _edit_annotation_text(self):
        index = next((index for index in self._selected if self.document.items[index].tool == "text"), None)
        if index is not None:
            self._begin_text_editor(self.document.items[index], index)
        return "break"

    @staticmethod
    def _text_font_spec(item, size):
        styles = " ".join(style for style, enabled in (("bold", item.bold), ("italic", item.italic)) if enabled)
        return item.font_family, -size, styles or "normal"

    def _fit_text_font_size(self, item, height):
        available = height - (TEXT_PADDING + item.outline_width) * 2
        sample = font.Font(root=self.top, font=self._text_font_spec(item, 8))
        low, high = 8, 144
        while low < high:
            size = (low + high + 1) // 2
            sample.configure(size=-size)
            pil_font = image_font(size, item.font_family, item.bold, item.italic)
            exported_height = pil_font.getbbox("Ag")[3]
            if max(sample.metrics("linespace"), exported_height) <= available:
                low = size
            else:
                high = size - 1
        return low

    def _begin_text_editor(self, item, index=None):
        self._finish_text_editor()
        self._text_draft_font_size = self._font_size.get()
        self._font_size.set(item.font_size)
        self._text_draft = item
        self._text_index = index
        # Use a native text widget for caret, selection, and IME input.
        editor = self._text_editor = tk.Text(self.canvas, wrap="char", undo=True, relief="flat", borderwidth=0,
                                             highlightthickness=0, padx=0, pady=0, exportselection=False,
                                             insertwidth=2, insertontime=530, insertofftime=530)
        editor.insert("1.0", item.text)
        editor.edit_reset()
        editor.edit_modified(False)
        editor.bind("<<Modified>>", self._on_text_modified)
        editor.bind("<Escape>", self._cancel)
        editor.bind("<Control-Return>", lambda _: self._end_text_input())
        editor.bind("<ButtonPress-1>", self._text_editor_press)
        editor.bind("<Double-Button-1>", self._text_editor_press)
        editor.bind("<Motion>", lambda event: self._update_pointer_cursor(self._local_point(event)))
        # Keep screenshot shortcuts from firing while typing or using Ctrl+C/Ctrl+Z in the widget.
        editor.bindtags(tuple(tag for tag in editor.bindtags() if tag != str(self.top)))
        self._text_editor_id = self.canvas.create_window(0, 0, window=editor, anchor="nw", tags="text-editor")
        self._update_text_editor_geometry()
        self._draw()
        editor.focus_force()
        editor.mark_set("insert", "end-1c")
        editor.see("insert")

    def _text_editor_press(self, event):
        if self._hit_annotation_handle(self._local_point(event)) is not None:
            self._on_press(event)
            if self._drag == "resize-annotation":
                self.canvas.grab_set()
            return "break"
        point = self._local_point(event)
        if self._in_text_frame(point):
            self._start_text_move(point, self._text_editor.index(f"@{event.x},{event.y}"))
            return "break"

    def _start_text_move(self, point, caret=None):
        self._start = point
        self._text_move_origin = self._text_draft.points
        self._text_move_caret = caret
        self._drag = "move-inline-text"
        self.canvas.grab_set()
        self.canvas.configure(cursor="fleur")

    def _move_inline_text(self, point):
        dx, dy = point[0] - self._start[0], point[1] - self._start[1]
        self._text_draft = replace(self._text_draft, points=tuple((x + dx, y + dy) for x, y in self._text_move_origin))
        self._update_text_editor_geometry()
        self._draw_selected_annotations()

    def _end_text_move(self, point, cancel=False):
        click = not cancel and max(abs(point[0] - self._start[0]), abs(point[1] - self._start[1])) < 2
        self._move_inline_text(self._start if cancel or click else point)
        self._drag = self._text_move_origin = None
        self.canvas.grab_release()
        if click and self._text_move_caret is not None:
            self._text_editor.mark_set("insert", self._text_move_caret)
            self._text_editor.tag_remove("sel", "1.0", "end")
        self._text_move_caret = None
        self._text_editor.focus_set()
        self._update_pointer_cursor(point)
        return "break"

    def _on_text_modified(self, event):
        if self._text_editor is None or not self._text_editor.edit_modified():
            return
        self._text_editor.edit_modified(False)
        self._update_text_editor_geometry()
        self._draw_selected_annotations()

    def _update_text_editor_geometry(self):
        editor = self._text_editor
        item = self._text_draft
        if editor is None or item is None:
            return
        left, top, right, bottom = item.text_box()
        width = right - left
        value = editor.get("1.0", "end-1c")
        font = image_font(item.font_size, item.font_family, item.bold, item.italic)
        padding = TEXT_PADDING + item.outline_width
        wrapped = wrap_annotation_text(value + "\u200b", font, max(1, width - 2 * padding))
        height = max(bottom - top, font.getbbox("Ag")[3] + 2 * padding + wrapped.count("\n") * (item.font_size + 4))
        height = max(bottom - top, min(height, max(8, self.screenshot.height - top)))
        self._text_draft = replace(item, text=value, points=((left, top), (right, top + height)))
        background = "white" if item.filled else self._text_background_color(left, top)
        red, green, blue = editor.winfo_rgb(background)
        caret_color = "black" if 0.299 * red + 0.587 * green + 0.114 * blue >= 32768 else "white"
        editor.configure(font=self._text_font_spec(item, item.font_size), foreground=item.color,
                         insertbackground=caret_color, background=background)
        inset_x = min(TEXT_PADDING, (width - 1) // 2)
        inset_y = min(TEXT_PADDING, (height - 1) // 2)
        self.canvas.coords(self._text_editor_id, left + inset_x, top + inset_y)
        self.canvas.itemconfigure(self._text_editor_id, width=width - 2 * inset_x, height=height - 2 * inset_y)
        self.canvas.tag_raise(self._text_editor_id)

    def _text_background_color(self, x, y):
        inside = self._inside_capture((x, y))
        image = self.screenshot
        if inside and (self._board or self._replay_image is not None):
            if self._replay_image is None:
                return self._board if isinstance(self._board, str) else "white"
            image = self._replay_image
            x -= self._selection[0]
            y -= self._selection[1]
        pixel = image.getpixel((min(image.width - 1, max(0, x)), min(image.height - 1, max(0, y))))
        rgb = pixel[:3] if inside else tuple(int(channel * 0.55) for channel in pixel[:3])
        return "#%02x%02x%02x" % rgb

    def _finish_text_editor(self, commit=True):
        if self._text_editor is None:
            return
        self._update_text_editor_geometry()
        item, index = self._text_draft, self._text_index
        self.canvas.delete(self._text_editor_id)
        self._text_editor.destroy()
        self._text_editor = self._text_draft = self._text_index = self._text_editor_id = None
        self._font_size.set(self._text_draft_font_size)
        if not commit:
            return
        if item.text.strip():
            if index is None:
                self.document.add(item)
                index = len(self.document.items) - 1
            else:
                items = self.document.items
                self.document.replace((*items[:index], item, *items[index + 1:]))
            self._select_annotations({index})
        elif index is not None:
            items = self.document.items
            self.document.replace((*items[:index], *items[index + 1:]))
            self._select_annotations(())

    def _end_text_input(self, commit=True):
        self._finish_text_editor(commit)
        self.canvas.focus_set()
        self._draw()
        return "break"

    def _input_size(self):
        if self.editing:
            return
        sx, sy = self._selection_scale() if self._unit_dip else (1, 1)
        unit = "DIP" if self._unit_dip else "px"
        value = simpledialog.askstring("截圖尺寸", f"輸入寬 × 高（{unit}），例如 800x600", parent=self.top)
        if not value:
            return
        try:
            width, height = (float(item) for item in value.lower().replace("×", "x").split("x"))
            width, height = round(width * sx), round(height * sy)
            if not 0 < width <= self.screenshot.width or not 0 < height <= self.screenshot.height:
                raise ValueError
        except (ValueError, OverflowError):
            messagebox.showwarning("MySnip", "請輸入螢幕範圍內的有效尺寸。", parent=self.top)
            return
        x_root, y_root = self.top.winfo_pointerxy()
        x, y = self._selection[:2] if self._selection else self._local_point(SimpleNamespace(x_root=x_root, y_root=y_root))
        x, y = min(x, self.screenshot.width - width), min(y, self.screenshot.height - height)
        self._set_selection((x, y, x + width, y + height))

    def _input_ratio(self):
        value = simpledialog.askstring("固定比例", "輸入寬:高，例如 16:9。輸入 0 取消。", parent=self.top)
        if not value:
            return
        if value == "0":
            self._ratio = 0
            return
        try:
            width, height = (float(item) for item in value.split(":"))
            ratio = width / height
            if not 0.02 <= ratio <= 50:
                raise ValueError
            self._ratio = ratio
        except (ValueError, ZeroDivisionError):
            messagebox.showwarning("MySnip", "比例格式無效。", parent=self.top)

    def _toggle_unit(self):
        self._unit_dip = not self._unit_dip
        self._draw()

    def _set_board(self, board):
        if not self.editing:
            self._board = board
            self._replay_image = None
            self._draw()

    def _input_outline(self):
        _, color = colorchooser.askcolor(self._outline, parent=self.top)
        if color:
            width = simpledialog.askinteger("文字描邊", "粗細（0 關閉）", parent=self.top, minvalue=0, maxvalue=12)
            if width is not None:
                self._outline, self._outline_width = color, width
                self._update_annotation_style(outline=color, outline_width=width)

    def _palette_menu(self, event):
        menu = tk.Menu(self.top, tearoff=False)
        for label, action in (("自訂色盤…", self._edit_palette), ("匯入色盤…", self._import_palette),
                              ("匯出色盤…", self._export_palette),
                              ("還原預設色盤", lambda: self._apply_palette(list(Settings().palette)))):
            menu.add_command(label=label, command=action)
        try:
            menu.tk_popup(event.x_root, event.y_root)
        finally:
            menu.grab_release()
        return "break"

    def _edit_palette(self):
        value = simpledialog.askstring("自訂色盤", "輸入 #RRGGBB 色碼，以逗號分隔",
                                       initialvalue=", ".join(self.settings.palette), parent=self.top)
        if value:
            self._apply_palette([color.strip() for color in value.split(",")])

    def _apply_palette(self, colors):
        if not isinstance(colors, list) or not 1 <= len(colors) <= 40 or not all(map(is_hex_color, colors)):
            messagebox.showwarning("MySnip", "色盤須包含 1–40 個有效色碼。", parent=self.top)
            return
        self.settings.palette = tuple(colors)
        self._palette_dirty = True
        self._refresh_palette()
        self._draw()

    def _import_palette(self):
        path = filedialog.askopenfilename(parent=self.top, filetypes=[("MySnip 色盤", "*.json")])
        if path:
            colors = read_json(Path(path), [])
            if isinstance(colors, list):
                colors = ["#" + color[3:] if isinstance(color, str) and len(color) == 9 else color for color in colors]
            self._apply_palette(colors)

    def _export_palette(self):
        path = filedialog.asksaveasfilename(parent=self.top, defaultextension=".json", filetypes=[("MySnip 色盤", "*.json")])
        if path:
            try:
                write_json(Path(path), ["#FF" + color[1:] for color in self.settings.palette])
            except OSError as error:
                messagebox.showerror("MySnip", str(error), parent=self.top)

    def _save(self, event=None):
        self._finish_text_editor()
        if not self._selection:
            return "break"
        self._commit_polyline()
        hidden = self.settings.hide_capture_for_save
        if hidden:
            self.top.withdraw()
        try:
            path = filedialog.asksaveasfilename(parent=self.root if hidden else self.top, defaultextension=".png",
                                               initialdir=self.settings.output_directory,
                                               filetypes=[("PNG", "*.png"), ("JPEG", "*.jpg")])
            if path:
                image = self._output_image()
                save_image(image, path)
                self._close(image, "saved", self._global_box(self._selection))
        except (OSError, ValueError) as error:
            messagebox.showerror("MySnip", str(error), parent=self.root)
        finally:
            if hidden and not self._closed:
                self._show()
        return "break"

    def _output_image(self):
        self._finish_text_editor()
        return decorate(self._render(), **{name: value.get() for name, value in self._decoration.items()})

    def _share_selection(self):
        if self._selection:
            self._commit_polyline()
            try:
                if self._share is None:
                    self._share = WindowsShare()
                self._share.show(hwnd(self.top), self._output_image())
            except OSError as error:
                messagebox.showerror("MySnip Windows 分享", str(error), parent=self.top)

    def _finish(self, action):
        if not self._selection or self._closed:
            return "break"
        self._commit_polyline()
        self._close(self._output_image(), action, self._global_box(self._selection))
        return "break"

    def _cancel(self, event=None):
        if self._drag == "move-inline-text":
            return self._end_text_move(self._start, cancel=True)
        if self._text_editor is not None:
            return self._end_text_input(commit=False)
        if self._drag == "draw-text":
            self._drag = None
            self.canvas.delete("draw-preview")
            return "break"
        if self._drag == "resize-annotation":
            if self.canvas.grab_current() == self.canvas:
                self.canvas.grab_release()
            index, item, _ = self._shape_resize
            self._shape_resize = self._drag = None
            if item.tool == "text" and self._resume_text_resize:
                self._resume_text_resize = False
                self._begin_text_editor(item, index)
            self._draw()
            self._update_pointer_cursor(self._local_point(event) if event is not None else self._start)
            return "break"
        if self.settings.record_canceled and not self.editing:
            image = self._render() if self._selection else self.screenshot
            self._close(image, "canceled", self._global_box(self._selection or (0, 0, *self.screenshot.size)))
        else:
            self._close(None, None, None)
        return "break"

    def _close(self, image, action, region):
        if self._closed:
            return
        self._closed = True
        self._hide_tooltip()
        if self._share:
            self._share.close()
        self.top.destroy()
        if self._palette_dirty and self.on_preferences:
            self.on_preferences()
        self.on_done(image, action, region)
