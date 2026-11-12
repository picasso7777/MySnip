"""Persistent settings and image history for the Python edition."""

from __future__ import annotations

import json
import os
import time
from dataclasses import asdict, dataclass, fields
from pathlib import Path
from uuid import UUID, uuid4

from PIL import Image


def read_json(path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return default


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(f".{uuid4().hex}.tmp")
    try:
        temp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        temp.replace(path)
    finally:
        temp.unlink(missing_ok=True)


def discard(path):
    try:
        path.unlink(missing_ok=True)
    except OSError:
        pass


def image_id(value):
    try:
        return isinstance(value, str) and UUID(value).hex == value
    except ValueError:
        return False


@dataclass
class Settings:
    capture_hotkey: str = "f1"
    paste_hotkey: str = "f3"
    visibility_hotkey: str = "shift+f3"
    output_directory: str = str(Path.home() / "Pictures" / "MySnip")
    capture_delay: float = 3.0
    history_limit: int = 20
    auto_save: bool = False
    restore_stickers: bool = True
    start_with_windows: bool = False
    include_cursor: bool = False
    record_canceled: bool = False
    show_magnifier: bool = True
    magnifier_size: int = 120
    magnifier_radius: int = 7
    magnifier_background: str = "#151515"
    magnifier_crosshair: bool = True
    palette: tuple = ("#ff3b30", "#ff9500", "#ffcc00", "#34c759", "#00c7be",
                      "#007aff", "#5856d6", "#af52de", "#ffffff", "#111111")
    pen_sizes: tuple = (2, 4, 8)
    remember_colors: bool = False
    tool_colors: dict | None = None
    tool_color: str = "#ff3b30"
    tool_width: int = 3
    tool_opacity: int = 100
    copy_as_file: bool = False
    hide_capture_for_save: bool = True
    select_new_annotation: bool = False
    sticker_escape: str = "hide"
    sticker_shift_escape: str = "close"
    theme: str = "system"
    super_snip: bool = False
    super_output: str = "clipboard"
    follow_desktop: bool = False
    hot_corners: tuple = ("none", "none", "none", "none")
    tray_actions: tuple = ("capture", "paste", "menu")
    ignore_applications: str = ""
    command_hotkeys: tuple = ()

    @classmethod
    def load(cls, root):
        local = root / "python-settings.json"
        raw = read_json(local, None)
        if raw is None:
            # The first Python edition stored lowercase fields alongside WPF settings.
            raw = read_json(root / "settings.json", {})
        if not isinstance(raw, dict):
            return cls()
        defaults = cls()
        values = {}
        for field in fields(defaults):
            if field.name not in raw:
                continue
            value = raw[field.name]
            default = getattr(defaults, field.name)
            if isinstance(default, bool) and isinstance(value, bool):
                values[field.name] = value
            elif isinstance(default, str) and isinstance(value, str) and (
                    value.strip() or field.name in ("capture_hotkey", "paste_hotkey", "visibility_hotkey")):
                values[field.name] = value.strip()
        try:
            values["capture_delay"] = max(0.0, min(60.0, float(raw.get("capture_delay", 3.0))))
            values["history_limit"] = max(0, min(100, int(raw.get("history_limit", 20))))
            values["magnifier_size"] = max(72, min(320, int(raw.get("magnifier_size", 120))))
            values["magnifier_radius"] = max(2, min(25, int(raw.get("magnifier_radius", 7))))
            values["tool_width"] = max(1, min(32, int(raw.get("tool_width", 3))))
            values["tool_opacity"] = max(10, min(100, int(raw.get("tool_opacity", 100))))
        except (TypeError, ValueError, OverflowError):
            pass
        for key, limit, validator in (("palette", 40, is_hex_color), ("pen_sizes", 5, valid_pen_size)):
            items = raw.get(key)
            if isinstance(items, (list, tuple)) and 1 <= len(items) <= limit and all(map(validator, items)):
                values[key] = tuple(items)
        colors = raw.get("tool_colors")
        if isinstance(colors, dict):
            values["tool_colors"] = {key: value for key, value in colors.items() if is_hex_color(value)}
        corners = raw.get("hot_corners")
        if isinstance(corners, list) and len(corners) == 4 and all(
                isinstance(item, str) and item in ("none", "capture", "paste", "visibility") for item in corners):
            values["hot_corners"] = tuple(corners)
        if values.get("super_output") not in ("clipboard", "sticker", "edit"):
            values.pop("super_output", None)
        for key in ("sticker_escape", "sticker_shift_escape"):
            if values.get(key) not in ("hide", "close", "disable_click_through", "none"):
                values.pop(key, None)
        if values.get("theme") not in ("system", "light", "dark"):
            values.pop("theme", None)
        for key in ("magnifier_background", "tool_color"):
            if not is_hex_color(values.get(key)):
                values.pop(key, None)
        tray = raw.get("tray_actions")
        if isinstance(tray, list) and len(tray) == 3 and all(
                item in ("capture", "paste", "menu", "visibility", "settings", "none") for item in tray):
            values["tray_actions"] = tuple(tray)
        commands = raw.get("command_hotkeys")
        if isinstance(commands, list):
            values["command_hotkeys"] = tuple(tuple(item) for item in commands[:64] if isinstance(item, list)
                                               and len(item) == 3 and all(isinstance(value, str) for value in item))
        return cls(**values)

    def save(self, root):
        write_json(root / "python-settings.json", asdict(self))


def is_hex_color(value):
    if not isinstance(value, str) or len(value) != 7 or not value.startswith("#"):
        return False
    return all(char in "0123456789abcdefABCDEF" for char in value[1:])


def valid_pen_size(value):
    return type(value) is int and 1 <= value <= 32


class CaptureHistory:
    def __init__(self, directory):
        self.directory = directory
        self.index = directory / "history.json"

    def entries(self):
        records = read_json(self.index, [])
        if not isinstance(records, list):
            return []
        return [entry for entry in records if isinstance(entry, dict)
                and image_id(entry.get("id")) and self.path(entry).is_file()
                and isinstance(entry.get("region"), list) and len(entry["region"]) == 4
                and all(type(value) is int for value in entry["region"])]

    def path(self, record):
        return self.directory / (record["id"] + ".png")

    def open(self, record):
        with Image.open(self.path(record)) as image:
            return image.convert("RGBA")

    def add(self, image, region, limit, canceled=False):
        if limit <= 0:
            return
        self.directory.mkdir(parents=True, exist_ok=True)
        entry = {"id": uuid4().hex, "region": list(region), "time": time.time(), "canceled": canceled}
        path = self.path(entry)
        try:
            image.save(path, format="PNG")
            self._update([entry] + self.entries(), limit)
        except (OSError, ValueError):
            discard(path)
            raise

    def trim(self, limit):
        records = self.entries()
        if len(records) > limit:
            self._update(records, limit)

    def _update(self, records, limit):
        write_json(self.index, records[:limit])
        for record in records[limit:]:
            discard(self.path(record))


class StickerStorage:
    def __init__(self, directory):
        self.directory = directory
        self.index = directory / "workspace.json"

    def open(self):
        workspace = read_json(self.index, {})
        return workspace if isinstance(workspace, dict) else {}

    def path(self, name):
        if not isinstance(name, str):
            return None
        stem, extension = os.path.splitext(name)
        if not image_id(stem) or extension not in (".png", ".gif"):
            return None
        return self.directory / name

    def save(self, groups, active, hidden, stickers):
        self.directory.mkdir(parents=True, exist_ok=True)
        layouts = []
        created = []
        try:
            for sticker in stickers:
                if sticker.backup_name is None:
                    name = uuid4().hex + (".gif" if sticker.asset.animated else ".png")
                    path = self.directory / name
                    created.append((sticker, path))
                    sticker.asset.save(path)
                    sticker.backup_name = name
                layouts.append(sticker.snapshot())
            write_json(self.index, {"groups": groups, "active": active, "hidden": hidden, "stickers": layouts})
        except (OSError, ValueError):
            for sticker, path in created:
                discard(path)
                sticker.backup_name = None
            raise
        keep = {layout["file"] for layout in layouts}
        for path in self.directory.iterdir():
            if path.name not in keep and self.path(path.name) is not None:
                discard(path)

    def clear(self):
        discard(self.index)
        if self.directory.is_dir():
            for path in self.directory.iterdir():
                if self.path(path.name) is not None:
                    discard(path)
