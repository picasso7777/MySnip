"""The Python edition's command line and output options."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import re


OUTPUTS = {
    "clipboard": "clipboard", "pin": "sticker", "quick-save": "quick-save", "file-dialog": "file-dialog",
    "ocr_clipboard": "ocr-clipboard", "barcode_clipboard": "barcode-clipboard", "copy-file": "copy-file",
    "printer": "print", "success": "success",
}


@dataclass(frozen=True)
class CaptureCommand:
    target: str | None = None
    region: tuple | None = None
    delay: float = 0
    hold: bool = False
    shadow: bool = False
    silent: bool = False
    skip_auto_save: bool = False
    outputs: tuple = ()

    @classmethod
    def parse(cls, args, directory):
        values = {}
        outputs = []
        index = 0

        def take():
            nonlocal index
            if index >= len(args):
                raise ValueError("命令列選項缺少參數。")
            value = args[index]
            index += 1
            return value

        def target(name):
            if "target" in values:
                raise ValueError("每次只能指定一種截圖範圍。")
            values["target"] = name

        while index < len(args):
            argument = take()
            if argument == "--area":
                target("area")
                values["region"] = area(*(integer(take()) for _ in range(4)))
            elif argument == "--size":
                target("size")
                values["region"] = area(0, 0, integer(take()), integer(take()))
            elif argument in ("--full", "--last", "--active-window", "--active-screen", "--custom"):
                target(argument[2:])
            elif argument == "--hold":
                values["hold"] = True
            elif argument in ("--shadow", "--no-shadow"):
                values["shadow"] = argument == "--shadow"
            elif argument == "--delay":
                values["delay"] = parse_delay(take())
            elif argument in ("-o", "--output"):
                for output in map(str.strip, take().split(";")):
                    if output == "silent":
                        values["silent"] = True
                    elif output == "no-auto-save":
                        values["skip_auto_save"] = True
                    elif output in OUTPUTS:
                        outputs.append((OUTPUTS[output], None))
                    elif Path(output).suffix.lower() in (".png", ".jpg", ".jpeg"):
                        outputs.append(("path", (directory / output).resolve()))
                    else:
                        raise ValueError(f"不支援的截圖輸出「{output}」。")
            else:
                raise ValueError(f"不支援的截圖選項「{argument}」。")
        return cls(**values, outputs=tuple(dict.fromkeys(outputs)))


def integer(value):
    if not re.fullmatch(r"[+-]?[0-9]+", value.strip()):
        raise ValueError("截圖座標須為整數。")
    number = int(value)
    if not -(2 ** 31) <= number < 2 ** 31:
        raise ValueError("截圖座標超出有效範圍。")
    return number


def area(x, y, width, height):
    if width <= 0 or height <= 0 or x + width >= 2 ** 31 or y + height >= 2 ** 31:
        raise ValueError("截圖位置或尺寸超出有效範圍。")
    return x, y, x + width, y + height


def parse_delay(value):
    if not re.fullmatch(r"(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)", value):
        raise ValueError("延遲時間須在 0–60 秒之間。")
    delay = float(value)
    if not 0 <= delay <= 60:
        raise ValueError("延遲時間須在 0–60 秒之間。")
    return delay


def custom_area(value, default_delay=0):
    parts = re.split(r"[\s,]+", value.strip())
    if len(parts) not in (4, 5):
        raise ValueError("請輸入 X Y 寬 高，以及選填的延遲秒數。")
    return area(*(integer(part) for part in parts[:4])), parse_delay(parts[4]) if len(parts) == 5 else default_delay


def parse_paste(args):
    args = list(args)
    position = None
    if args[:1] == ["--pos"]:
        if len(args) < 3:
            raise ValueError("命令列選項缺少參數。")
        position = tuple(map(integer, args[1:3]))
        args = args[3:]
    source = args.pop(0) if args else "--clipboard"
    if source in ("--plain", "--html") and args:
        payload = [args.pop(0)]
    elif source == "--files" and args:
        stop = args.index("--pos") if "--pos" in args else len(args)
        payload, args = args[:stop], args[stop:]
        if not payload:
            raise ValueError("請指定有效的貼圖來源。")
    elif source == "--clipboard":
        payload = []
    else:
        raise ValueError("請指定有效的貼圖來源。")
    if position is None and len(args) == 3 and args[0] == "--pos":
        position = tuple(map(integer, args[1:]))
        args = []
    if args:
        raise ValueError("請指定有效的貼圖來源。")
    return source, payload, position
