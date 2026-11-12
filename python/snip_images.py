"""Image data, annotation rendering, and screenshot output."""

from __future__ import annotations

import math
import os
import re
import winreg
from dataclasses import dataclass
from datetime import datetime
from functools import lru_cache
from pathlib import Path

from PIL import Image, ImageColor, ImageDraw, ImageFilter, ImageFont, ImageOps, ImageSequence

RESAMPLE = getattr(Image, "Resampling", Image)
TRANSPOSE = getattr(Image, "Transpose", Image)
IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".tga"}


@dataclass
class StickerAsset:
    frames: tuple
    delays: tuple = (100,)
    color: str | None = None
    original_gif: bytes | None = None

    @property
    def animated(self):
        return len(self.frames) > 1

    @classmethod
    def from_image(cls, image):
        count = getattr(image, "n_frames", 1) if image.format == "GIF" else 1
        if image.width * image.height * count > 64_000_000:
            raise ValueError("圖片的總像素過多。")
        if count == 1:
            return cls((image.convert("RGBA"),))
        original = None
        if stream := getattr(image, "fp", None):
            position = stream.tell()
            stream.seek(0)
            original = stream.read(24 * 1024 * 1024 + 1)
            stream.seek(position)
            if len(original) > 24 * 1024 * 1024:
                original = None
        frames = []
        delays = []
        for frame in ImageSequence.Iterator(image):
            frames.append(frame.convert("RGBA"))
            delays.append(max(20, int(frame.info.get("duration", 100))))
        return cls(tuple(frames), tuple(delays), original_gif=original)

    @classmethod
    def load(cls, path):
        with Image.open(path) as image:
            return cls.from_image(image)

    def save(self, path):
        if self.animated and Path(path).suffix.lower() == ".gif":
            if self.original_gif:
                Path(path).write_bytes(self.original_gif)
            else:
                self.frames[0].save(path, save_all=True, append_images=self.frames[1:], duration=self.delays, loop=0, disposal=2)
        else:
            save_image(self.frames[0], path)


_COMMON_FONTS = {
    "Microsoft JhengHei UI": ("msjh.ttc", "msjhbd.ttc", "msjh.ttc", "msjhbd.ttc"),
    "Segoe UI": ("segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"),
    "Arial": ("arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"),
    "Consolas": ("consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf"),
}


@lru_cache(maxsize=1)
def annotation_fonts():
    fonts = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"
    catalog = {family: {index: fonts / path for index, path in enumerate(files)} for family, files in _COMMON_FONTS.items()}
    registry = r"Software\Microsoft\Windows NT\CurrentVersion\Fonts"
    for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        try:
            with winreg.OpenKey(hive, registry) as key:
                for index in range(winreg.QueryInfoKey(key)[1]):
                    name, filename, _ = winreg.EnumValue(key, index)
                    if not isinstance(filename, str) or Path(filename).suffix.lower() not in (".ttf", ".otf", ".ttc"):
                        continue
                    path = fonts / os.path.expandvars(filename)
                    label = re.sub(r"\s+\((?:TrueType|OpenType)\)$", "", name)
                    for alias in label.split(" & "):
                        style = re.search(r"\s+(Regular|Bold Italic|Bold Oblique|Bold|Italic|Oblique)$", alias)
                        family = alias[:style.start()] if style else alias
                        traits = style[1] if style else ""
                        variant = int("Bold" in traits) + 2 * int("Italic" in traits or "Oblique" in traits)
                        catalog.setdefault(family, {})[variant] = path
        except OSError:
            continue
    return catalog


@lru_cache(maxsize=64)
def image_font(size, family="Microsoft JhengHei UI", bold=False, italic=False):
    fonts = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"
    variants = annotation_fonts().get(family, {})
    preferred = variants.get(int(bold) + 2 * int(italic), variants.get(int(bold), variants.get(0)))
    for path in (preferred, fonts / "msjh.ttc", fonts / "segoeui.ttf", fonts / "arial.ttf"):
        if path is None:
            continue
        try:
            return ImageFont.truetype(str(path), size)
        except OSError:
            continue
    return ImageFont.load_default()


def parse_color(text):
    text = text.strip()
    if re.fullmatch(r"#[0-9a-fA-F]{3}(?:[0-9a-fA-F]{3})?", text):
        return ImageColor.getrgb(text)
    match = re.fullmatch(r"(?:rgb\s*\()?\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*\)?", text, re.I)
    if match:
        values = tuple(int(value) for value in match.groups())
        if all(value <= 255 for value in values):
            return values
    return None


def color_card(color):
    hex_color = "#{:02X}{:02X}{:02X}".format(*color)
    image = Image.new("RGBA", (280, 108), "white")
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 279, 71), fill=color)
    draw.text((12, 79), f"{hex_color}  RGB {color}", font=image_font(16), fill="black")
    return StickerAsset((image,), color=hex_color)


def text_image(text):
    font = image_font(17)
    scratch = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    lines = []
    for original in text[:8000].expandtabs(4).splitlines() or [""]:
        line = ""
        for char in original:
            if line and scratch.textlength(line + char, font=font) > 620:
                lines.append(line)
                line = ""
            line += char
        lines.append(line)
        if len(lines) >= 150:
            break
    wrapped = "\n".join(lines[:150])
    _, _, width, height = scratch.multiline_textbbox((0, 0), wrapped, font=font, spacing=4)
    image = Image.new("RGBA", (max(60, width + 24), max(38, height + 24)), "white")
    ImageDraw.Draw(image).multiline_text((12, 10), wrapped, font=font, fill="#161616", spacing=4)
    return image


def save_image(image, path):
    path = Path(path)
    if path.suffix.lower() in (".jpg", ".jpeg"):
        rgb = Image.new("RGB", image.size, "white")
        rgba = image.convert("RGBA")
        rgb.paste(rgba, mask=rgba.getchannel("A"))
        rgb.save(path, quality=94)
    else:
        image.save(path, format="PNG")


def quick_save(image, directory):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    stem = datetime.now().strftime("MySnip-%Y%m%d-%H%M%S-%f")
    path = directory / (stem + ".png")
    with path.open("xb") as stream:
        image.save(stream, format="PNG")
    return path


def decorate(image, rounded=False, border=False, shadow=False):
    if not (rounded or border or shadow):
        return image
    image = image.convert("RGBA")
    width, height = image.size
    radius = min(12, width // 2, height // 2) if rounded else 0
    if rounded:
        mask = Image.new("L", image.size)
        rounded_rectangle(ImageDraw.Draw(mask), (0, 0, width - 1, height - 1), radius, fill=255)
        from PIL import ImageChops
        image.putalpha(ImageChops.multiply(image.getchannel("A"), mask))
    if border:
        rounded_rectangle(ImageDraw.Draw(image), (0, 0, width - 1, height - 1), radius, outline="#444444")
    if not shadow:
        return image
    pad = 15
    result = Image.new("RGBA", (width + pad * 2, height + pad * 2))
    rounded_rectangle(ImageDraw.Draw(result), (pad, pad + 3, pad + width - 1, pad + height + 2),
                      radius, fill=(0, 0, 0, 135))
    result = result.filter(ImageFilter.GaussianBlur(6))
    result.alpha_composite(image, (pad, pad))
    return result


def rounded_rectangle(draw, box, radius, fill=None, outline=None, width=1):
    if hasattr(draw, "rounded_rectangle"):
        draw.rounded_rectangle(box, radius, fill=fill, outline=outline, width=width)
        return
    if radius <= 0:
        draw.rectangle(box, fill=fill, outline=outline, width=width)
        return
    left, top, right, bottom = box
    diameter = radius * 2
    corners = ((left, top, 180), (right - diameter, top, 270),
               (right - diameter, bottom - diameter, 0), (left, bottom - diameter, 90))
    if fill is not None:
        draw.rectangle((left + radius, top, right - radius, bottom), fill=fill)
        draw.rectangle((left, top + radius, right, bottom - radius), fill=fill)
        for x, y, angle in corners:
            draw.pieslice((x, y, x + diameter, y + diameter), angle, angle + 90, fill=fill)
    if outline is not None:
        for x, y, angle in corners:
            draw.arc((x, y, x + diameter, y + diameter), angle, angle + 90, fill=outline, width=width)
        for a, b in (((left + radius, top), (right - radius, top)), ((right, top + radius), (right, bottom - radius)),
                     ((left + radius, bottom), (right - radius, bottom)), ((left, top + radius), (left, bottom - radius))):
            draw.line((a, b), fill=outline, width=width)


@dataclass(frozen=True)
class Annotation:
    tool: str
    points: tuple
    color: str = "#ff3b30"
    width: int = 3
    opacity: int = 255
    text: str = ""
    font_size: int = 24
    font_family: str = "Microsoft JhengHei UI"
    bold: bool = False
    italic: bool = False
    outline: str = "#000000"
    outline_width: int = 0
    filled: bool = False
    dashed: bool = False
    elliptical: bool = False
    angle: float = 0
    filter_strength: int | None = None

    def bounds(self):
        xs, ys = zip(*self.points)
        if self.tool == "text":
            box = ImageDraw.Draw(Image.new("RGB", (1, 1))).multiline_textbbox(
                (xs[0], ys[0]), self.text, font=image_font(self.font_size, self.font_family, self.bold, self.italic),
                stroke_width=self.outline_width)
            return min(xs[0], box[0]), min(ys[0], box[1]), box[2] + 2, box[3] + 2
        margin = max(self.width, 14 if self.tool == "number" else 0)
        if self.tool in ("arrow", "double_arrow", "filled_arrow", "outline_arrow"):
            margin = max(margin, self.width * 4, 10)
        elif self.tool == "marker":
            margin = max(margin, self.width * 2, 5)
        return min(xs) - margin, min(ys) - margin, max(xs) + margin + 1, max(ys) + margin + 1


class Annotations:
    def __init__(self):
        self.items = ()
        self._undo = []
        self._redo = []

    @property
    def can_undo(self):
        return bool(self._undo)

    @property
    def can_redo(self):
        return bool(self._redo)

    def replace(self, items):
        items = tuple(items)
        if items != self.items:
            self._undo.append(self.items)
            self.items = items
            self._redo.clear()

    def add(self, annotation):
        self.replace((*self.items, annotation))

    def undo(self):
        if self._undo:
            self._redo.append(self.items)
            self.items = self._undo.pop()

    def redo(self):
        if self._redo:
            self._undo.append(self.items)
            self.items = self._redo.pop()

    def hit(self, point):
        x, y = point
        for index in reversed(range(len(self.items))):
            left, top, right, bottom = self.items[index].bounds()
            if left - 3 <= x < right + 3 and top - 3 <= y < bottom + 3:
                return index
        return None


def render_annotations(base, region, annotations):
    result = base.crop(region).convert("RGBA")
    left, top = region[:2]
    for annotation in annotations:
        points = [(x - left, y - top) for x, y in annotation.points]
        if annotation.tool in ("mosaic", "blur", "magnify"):
            _filter_region(result, annotation, points)
        else:
            box = region if annotation.angle else annotation.bounds()
            x0, y0, x1, y1 = max(left, box[0]), max(top, box[1]), min(region[2], box[2]), min(region[3], box[3])
            if x1 <= x0 or y1 <= y0:
                continue
            layer = Image.new("RGBA", (x1 - x0, y1 - y0))
            _draw_annotation(ImageDraw.Draw(layer), annotation, [(x - x0, y - y0) for x, y in annotation.points])
            if annotation.angle:
                box = layer.getbbox()
                if box:
                    center = ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2)
                    layer = layer.rotate(-annotation.angle, resample=RESAMPLE.BICUBIC, center=center)
            result.alpha_composite(layer, (x0 - left, y0 - top))
    return result


def _filter_region(image, annotation, points):
    (x0, y0), (x1, y1) = points[0], points[-1]
    left, right = max(0, min(x0, x1)), min(image.width, max(x0, x1))
    top, bottom = max(0, min(y0, y1)), min(image.height, max(y0, y1))
    if right <= left or bottom <= top:
        return
    box = left, top, right, bottom
    original = image.crop(box)
    width, height = original.size
    if annotation.tool == "mosaic":
        size = annotation.filter_strength or max(4, annotation.width * 3)
        filtered = original.resize((max(1, width // size), max(1, height // size)), RESAMPLE.BOX)
        filtered = filtered.resize(original.size, RESAMPLE.NEAREST)
    elif annotation.tool == "blur":
        radius = max(1, annotation.filter_strength // 2) if annotation.filter_strength else max(2, annotation.width)
        filtered = original.filter(ImageFilter.GaussianBlur(radius))
    else:
        filtered = original.crop((width // 4, height // 4, max(width // 4 + 1, width * 3 // 4),
                                  max(height // 4 + 1, height * 3 // 4))).resize(original.size, RESAMPLE.LANCZOS)
    if annotation.elliptical or annotation.tool == "magnify":
        mask = Image.new("L", original.size)
        ImageDraw.Draw(mask).ellipse((0, 0, width - 1, height - 1), fill=255)
        image.paste(filtered, box, mask)
    else:
        image.paste(filtered, box)


def _draw_annotation(draw, annotation, points):
    tool = annotation.tool
    color = (*ImageColor.getrgb(annotation.color), annotation.opacity)
    width = annotation.width
    x0, y0 = points[0]
    x1, y1 = points[-1]
    box = (min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1))
    if tool in ("pen", "line", "polyline", "arrow", "double_arrow", "marker"):
        if tool == "marker":
            color = (*color[:3], min(color[3], 115))
            width = max(8, width * 3)
        _stroke(draw, points, color, width, annotation.dashed)
        if tool in ("arrow", "double_arrow"):
            _arrow(draw, points[0], points[-1], color, width)
            if tool == "double_arrow":
                _arrow(draw, points[-1], points[0], color, width)
    elif tool in ("outline_arrow", "filled_arrow"):
        _arrow_shape(draw, points[0], points[-1], color, width, tool == "filled_arrow")
    elif tool in ("rectangle", "rounded", "ellipse", "marker_ellipse", "marker_rectangle"):
        fill = color if annotation.filled else None
        if tool == "marker_ellipse":
            draw.ellipse(box, fill=(*color[:3], min(color[3], 115)))
        elif tool == "marker_rectangle":
            draw.rectangle(box, fill=(*color[:3], min(color[3], 115)))
        elif annotation.dashed and not annotation.filled:
            if tool == "ellipse":
                for angle in range(0, 360, 20):
                    draw.arc(box, angle, angle + 12, fill=color, width=width)
            else:
                l, t, r, b = box
                _stroke(draw, [(l, t), (r, t), (r, b), (l, b), (l, t)], color, width, True)
        elif tool == "rounded":
            rounded_rectangle(draw, box, min(14, (box[2] - box[0]) // 2, (box[3] - box[1]) // 2),
                              outline=color, fill=fill, width=width)
        elif tool == "ellipse":
            draw.ellipse(box, outline=color, fill=fill, width=width)
        else:
            draw.rectangle(box, outline=color, fill=fill, width=width)
    elif tool == "text":
        font = image_font(annotation.font_size, annotation.font_family, annotation.bold, annotation.italic)
        if annotation.filled:
            draw.rectangle(draw.multiline_textbbox((x0, y0), annotation.text, font=font), fill="white")
        draw.multiline_text((x0, y0), annotation.text, font=font, fill=color,
                            stroke_width=annotation.outline_width, stroke_fill=annotation.outline)
    elif tool == "number":
        draw.ellipse((x0 - 14, y0 - 14, x0 + 14, y0 + 14), fill=color)
        draw.text((x0, y0), annotation.text, font=image_font(16), fill="white", anchor="mm")


def _stroke(draw, points, color, width, dashed):
    if len(points) == 1 or points[0] == points[-1] and len(points) == 2:
        x, y = points[0]
        draw.ellipse((x - width / 2, y - width / 2, x + width / 2, y + width / 2), fill=color)
    elif not dashed:
        draw.line(points, fill=color, width=width, joint="curve")
    else:
        for (x0, y0), (x1, y1) in zip(points, points[1:]):
            length = math.hypot(x1 - x0, y1 - y0)
            if length == 0:
                continue
            for start in range(0, math.ceil(length), width * 5):
                end = min(length, start + width * 3)
                draw.line((x0 + (x1 - x0) * start / length, y0 + (y1 - y0) * start / length,
                           x0 + (x1 - x0) * end / length, y0 + (y1 - y0) * end / length), fill=color, width=width)


def _arrow(draw, start, end, color, width):
    distance = math.dist(start, end)
    if distance < 2:
        return
    ux, uy = (end[0] - start[0]) / distance, (end[1] - start[1]) / distance
    head = min(distance * 0.55, max(12, width * 6))
    base = end[0] - ux * head, end[1] - uy * head
    wing = head * 0.45
    first = base[0] - uy * wing, base[1] + ux * wing
    second = base[0] + uy * wing, base[1] - ux * wing
    draw.line([first, end, second], fill=color, width=width)


def _arrow_shape(draw, start, end, color, width, filled):
    distance = math.dist(start, end)
    if distance < 2:
        return
    dx, dy = (end[0] - start[0]) / distance, (end[1] - start[1]) / distance
    head = min(distance * 0.55, max(12, width * 6))
    base = end[0] - dx * head, end[1] - dy * head
    shaft = max(2, head * 0.18)
    wing = head * 0.52

    def offset(point, amount):
        return point[0] - dy * amount, point[1] + dx * amount

    outline = [offset(start, shaft), offset(base, shaft), offset(base, wing), end,
               offset(base, -wing), offset(base, -shaft), offset(start, -shaft)]
    if filled:
        draw.polygon(outline, fill=color)
    else:
        draw.line([*outline, outline[0]], fill=color, width=width, joint="curve")
