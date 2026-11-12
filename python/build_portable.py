"""Build a single-file Windows x64 executable with an embedded Python runtime."""

from __future__ import annotations

import argparse
import importlib.util
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
from zipfile import ZIP_DEFLATED, ZipFile


SOURCE = Path(__file__).resolve().parent
PROJECT = SOURCE.parent
PYTHON = Path(sys.base_prefix)
IGNORE = shutil.ignore_patterns("__pycache__", "*.pyc", "*.pdb", "*.lib")


def module_path(name):
    spec = importlib.util.find_spec(name)
    if not spec or not spec.origin:
        raise RuntimeError(f"The build Python is missing {name}. Install requirements.txt with that interpreter.")
    return Path(spec.origin)


def copy_runtime(runtime):
    runtime.mkdir()
    version = f"python{sys.version_info.major}{sys.version_info.minor}"
    for filename in ("python.exe", "pythonw.exe", "python3.dll", f"{version}.dll"):
        shutil.copy2(PYTHON / filename, runtime / filename)
    for pattern in ("vcruntime140*.dll", "msvcp140*.dll", "LICENSE*"):
        for path in PYTHON.glob(pattern):
            if path.is_file():
                shutil.copy2(path, runtime / path.name)
    for name in ("DLLs", "tcl"):
        shutil.copytree(PYTHON / name, runtime / name, ignore=IGNORE)
    shutil.copytree(PYTHON / "Lib", runtime / "Lib", ignore=shutil.ignore_patterns(
        "site-packages", "__pycache__", "*.pyc", "test", "tests", "ensurepip", "turtledemo"
    ))
    site = runtime / "Lib" / "site-packages"
    site.mkdir()
    pillow = module_path("PIL").parent
    shutil.copytree(pillow, site / pillow.name, ignore=IGNORE)
    win32 = module_path("win32api").parent
    for name in ("_win32sysloader", "win32api", "win32clipboard", "win32gui", "win32process"):
        shutil.copy2(module_path(name), site)
    for name in ("win32con", "pywintypes"):
        shutil.copy2(module_path(name), site)
    pywin = win32.parent / "pywin32_system32"
    shutil.copy2(pywin / f"pywintypes{version[6:]}.dll", runtime)
    for licenses in (pillow.parent.glob("Pillow-*.dist-info/LICENSE*"), win32.parent.glob("pywin32-*.dist-info/LICENSE*")):
        for license_file in licenses:
            target = site / license_file.parent.name
            target.mkdir(exist_ok=True)
            shutil.copy2(license_file, target)

    for name in ("mss", "zxingcpp"):
        spec = importlib.util.find_spec(name)
        if spec and spec.origin:
            path = Path(spec.origin)
            if spec.submodule_search_locations:
                shutil.copytree(path.parent, site / path.parent.name, ignore=IGNORE)
            else:
                shutil.copy2(path, site)

    # Conda distributes some standard-library and Pillow dependencies outside DLLs.
    conda_dlls = PYTHON / "Library" / "bin"
    for pattern in ("libcrypto-*.dll", "libssl-*.dll", "libjpeg*.dll", "jpeg*.dll", "libpng*.dll",
                    "zlib.dll", "zstd.dll", "libtiff*.dll", "tiff.dll", "freetype*.dll", "lcms*.dll",
                    "libwebp*.dll", "liblzma.dll", "libbz2.dll", "libexpat.dll", "sqlite3.dll"):
        for path in conda_dlls.glob(pattern):
            shutil.copy2(path, runtime / path.name)
    for path in pillow.parent.glob("pillow.libs"):
        shutil.copytree(path, site / path.name, ignore=IGNORE)
    (runtime / f"{version}._pth").write_text(".\nDLLs\nLib\nLib\\site-packages\n..\\app\nimport site\n", encoding="ascii")


def build_launcher(output, payload):
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
    if not compiler.is_file():
        raise RuntimeError("The Windows .NET Framework C# compiler was not found.")
    subprocess.run([str(compiler), "/nologo", "/target:winexe", "/platform:x64", "/optimize+", "/r:System.Windows.Forms.dll",
                    f"/r:{compiler.parent / 'System.IO.Compression.dll'}", f"/resource:{payload},MySnip.Python.Payload",
                    f"/win32icon:{PROJECT / 'assets' / 'MySnip.ico'}", f"/out:{output}",
                    str(SOURCE / "MySnip.PythonLauncher.cs")], check=True)


def build(output):
    if os.name != "nt" or struct.calcsize("P") != 8:
        raise RuntimeError("Use a Windows x64 Python installation.")
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="mysnip-build-", dir=output) as temporary:
        work = Path(temporary)
        bundle = work / "bundle"
        bundle.mkdir()
        copy_runtime(bundle / "runtime")
        (bundle / "app").mkdir()
        for path in [SOURCE / "mysnip.py", *SOURCE.glob("snip_*.py")]:
            shutil.copy2(path, bundle / "app")
        shutil.copytree(PROJECT / "assets", bundle / "assets")
        runtime = bundle / "runtime"
        windows = Path(os.environ["SystemRoot"])
        environment = dict(os.environ, PATH=os.pathsep.join(map(str, (runtime, runtime / "DLLs", windows / "System32", windows))))
        for name, script in (("TCL_LIBRARY", "init.tcl"), ("TK_LIBRARY", "tk.tcl")):
            path = next((runtime / "tcl").glob(f"*/{script}")).parent.as_posix()
            environment[name] = "//?/UNC/" + path[2:] if path.startswith("//") else "//?/" + path
        subprocess.run([str(runtime / "python.exe"), "-B", "-c",
                        "import tkinter, ssl, PIL.Image, win32api, win32clipboard, win32gui, mysnip; "
                        "window = tkinter.Tk(); window.withdraw(); window.destroy()"],
                       env=environment, cwd=work, check=True)
        payload = work / "payload.zip"
        with ZipFile(payload, "w", ZIP_DEFLATED, compresslevel=6) as archive:
            for path in sorted(bundle.rglob("*")):
                if path.is_file():
                    archive.write(path, path.relative_to(bundle).as_posix())
        executable = work / "MySnip-Python.exe"
        build_launcher(executable, payload)
        executable.replace(output / executable.name)
    print(output / "MySnip-Python.exe")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=PROJECT / "publish" / "python-single-file-win-x64")
    args = parser.parse_args()
    build(args.output)
