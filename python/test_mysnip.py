from pathlib import Path
import tempfile
import unittest

from PIL import Image

from snip_capture import intersection, valid_box
from snip_commands import CaptureCommand, custom_area, parse_paste
from snip_images import Annotation, Annotations, StickerAsset, decorate, render_annotations
from snip_storage import CaptureHistory, Settings, StickerStorage, write_json
from snip_windows import monitor_at, parse_shortcut, plain_html


class MySnipTests(unittest.TestCase):
    def test_virtual_coordinates_and_annotations(self):
        source = Image.new("RGB", (80, 60), "red")
        source.paste("blue", (40, 0, 80, 60))
        region = intersection((-15, 10, 25, 40), (-40, 0, 40, 60))
        self.assertTrue(valid_box(region))
        local = (region[0] + 40, region[1], region[2] + 40, region[3])
        output = render_annotations(source, local, [Annotation("rectangle", ((37, 12), (43, 18)), "#00ff00", filled=True)])
        self.assertEqual(output.size, (40, 30))
        self.assertEqual(output.getpixel((0, 0)), (255, 0, 0, 255))
        self.assertEqual(output.getpixel((39, 0)), (0, 0, 255, 255))
        self.assertEqual(output.getpixel((15, 4)), (0, 255, 0, 255))

    def test_uses_nearest_monitor_in_virtual_desktop_gap(self):
        primary = dict(left=0, top=0, width=1920, height=1080, dpi_x=96, dpi_y=96)
        upper_right = dict(left=1920, top=-1200, width=1600, height=1200, dpi_x=144, dpi_y=144)
        self.assertIs(monitor_at((1910, -1000), [primary, upper_right]), upper_right)
        self.assertIs(monitor_at((0, 0), [primary, upper_right]), primary)

    def test_transparent_board_and_undo(self):
        source = Image.new("RGBA", (48, 40))
        edits = Annotations()
        edits.add(Annotation("ellipse", ((10, 10), (32, 30)), filled=True))
        rendered = render_annotations(source, (0, 0, *source.size), edits.items)
        self.assertEqual(rendered.getpixel((0, 0))[3], 0)
        self.assertEqual(rendered.getpixel((20, 20))[3], 255)
        edits.undo()
        self.assertFalse(edits.items)
        edits.redo()
        self.assertEqual(len(edits.items), 1)
        self.assertEqual(decorate(rendered, rounded=True, shadow=True).size, (78, 70))

    def test_history_preserves_negative_coords_and_retention(self):
        with tempfile.TemporaryDirectory() as root:
            history = CaptureHistory(Path(root))
            image = Image.new("RGBA", (20, 10), "blue")
            history.add(image, (-20, -10, 0, 0), 1)
            history.add(image, (-5, 0, 15, 10), 1, canceled=True)
            records = history.entries()
            self.assertEqual(len(records), 1)
            self.assertEqual(records[0]["region"], [-5, 0, 15, 10])
            self.assertTrue(records[0]["canceled"])
            self.assertEqual(history.open(records[0]).size, image.size)
            self.assertEqual(len(list(Path(root).glob("*.png"))), 1)

    def test_settings_migrate_without_rewriting_wpf(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = {"SchemaVersion": 1, "capture_hotkey": "ctrl+alt+f8", "output_directory": "D:/captures"}
            write_json(root / "settings.json", original)
            settings = Settings.load(root)
            self.assertEqual(settings.capture_hotkey, "ctrl+alt+f8")
            settings.pen_sizes = (4, 10)
            settings.visibility_hotkey = ""
            settings.magnifier_radius = 11
            settings.sticker_shift_escape = "disable_click_through"
            settings.save(root)
            restored = Settings.load(root)
            self.assertEqual(restored.pen_sizes, (4, 10))
            self.assertEqual(restored.visibility_hotkey, "")
            self.assertEqual(restored.magnifier_radius, 11)
            self.assertEqual(restored.sticker_shift_escape, "disable_click_through")
            self.assertIn('"SchemaVersion": 1', (root / "settings.json").read_text(encoding="utf-8"))

    def test_gif_delay_and_workspace(self):
        with tempfile.TemporaryDirectory() as root:
            red, blue = [Image.new("RGBA", (15, 10), color) for color in ("red", "blue")]
            asset = StickerAsset((red, blue), (120, 240))
            storage = StickerStorage(Path(root))
            sticker = type("Sticker", (), {"backup_name": None, "asset": asset,
                           "snapshot": lambda self: {"file": self.backup_name, "group": "QA"}})()
            storage.save(["QA"], "QA", False, [sticker])
            entry = storage.open()["stickers"][0]
            restored = StickerAsset.load(storage.path(entry["file"]))
            self.assertEqual(restored.delays, (120, 240))
            self.assertEqual(restored.frames[1].getpixel((2, 3)), (0, 0, 255, 255))
            copy = Path(root) / "copy.gif"
            restored.save(copy)
            self.assertEqual(copy.read_bytes(), storage.path(entry["file"]).read_bytes())

    def test_outline_arrow_and_rectangular_highlighter(self):
        source = Image.new("RGBA", (70, 50))
        result = render_annotations(source, (0, 0, *source.size), [
            Annotation("outline_arrow", ((12, 14), (57, 14))),
            Annotation("marker_rectangle", ((15, 30), (54, 44)), "#ffff00"),
        ])
        self.assertEqual(result.getpixel((20, 14))[3], 0)
        self.assertTrue(any(result.getpixel((20, y))[3] for y in range(8, 13)))
        self.assertEqual(result.getpixel((20, 35)), (255, 255, 0, 115))

    def test_filter_strength_is_independent_of_pen_width(self):
        source = Image.linear_gradient("L").resize((64, 64)).convert("RGBA")
        for tool in ("mosaic", "blur"):
            with self.subTest(tool=tool):
                def render(strength):
                    mark = Annotation(tool, ((0, 0), source.size), width=3, filter_strength=strength)
                    return render_annotations(source, (0, 0, *source.size), [mark])

                self.assertNotEqual(render(4).tobytes(), render(24).tobytes())

    def test_clipboard_html_and_shortcuts(self):
        self.assertEqual(plain_html("<p>你好 &amp; hi</p><style>hidden</style><p>next</p>"), "你好 & hi\nnext")
        self.assertEqual(parse_shortcut("Ctrl+Alt+F8"), (0x4003, 0x77))
        with self.assertRaises(ValueError):
            parse_shortcut("A")

    def test_capture_command_combines_outputs_on_negative_desktop(self):
        directory = Path.cwd()
        command = CaptureCommand.parse(
            ["--area", "-1280", "-500", "1600", "900", "--hold", "--delay", ".5", "--shadow",
             "-o", "clipboard;pin;no-auto-save;silent", "--output", "result.png", "-o", "pin"], directory)
        self.assertEqual(command.region, (-1280, -500, 320, 400))
        self.assertEqual(command.outputs, (("clipboard", None), ("sticker", None), ("path", directory / "result.png")))
        self.assertEqual(command.delay, 0.5)
        self.assertTrue(command.hold and command.shadow and command.silent and command.skip_auto_save)
        self.assertEqual(custom_area("-5, -4, 30, 24, 2.5"), ((-5, -4, 25, 20), 2.5))

    def test_bad_capture_command_preserves_app_error_handling(self):
        for args in (["--last", "--full"], ["--area", "2147483640", "0", "20", "10"],
                     ["--area", "0", "0", "-20", "10"], ["--output", "text.txt"], ["--delay", "NaN"]):
            with self.subTest(args=args), self.assertRaises(ValueError):
                CaptureCommand.parse(args, Path.cwd())

    def test_paste_command_distinguishes_text_from_options(self):
        self.assertEqual(parse_paste(["--plain", "--pos", "--pos", "-80", "42"]),
                         ("--plain", ["--pos"], (-80, 42)))
        self.assertEqual(parse_paste(["--pos", "-80", "42", "--files", "a.png", "b.gif"]),
                         ("--files", ["a.png", "b.gif"], (-80, 42)))
        with self.assertRaises(ValueError):
            parse_paste(["--clipboard", "--plain", "ignored"])


if __name__ == "__main__":
    unittest.main()
