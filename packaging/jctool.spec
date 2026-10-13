# PyInstaller spec for the desktop builds (Windows, macOS, Linux). Build it on the system it
# is for (PyInstaller doesn't cross-compile): python scripts/build.py desktop
# Output: dist/JoyConToolkit/ (Windows, Linux) or dist/Joy-Con Toolkit.app (macOS), with the
# app (JoyConToolkit) and the command line (jctool-cli).
import os
import sys

from kivy.tools.packaging.pyinstaller_hooks import get_deps_minimal, hookspath, runtime_hooks

ROOT = os.path.abspath(os.path.join(SPECPATH, ".."))
NAME = "JoyConToolkit"
sys.path.insert(0, ROOT)
from jctool import __version__  # noqa: E402

# Providers named (not True) so that no window is opened while building
deps = get_deps_minimal(window="sdl2", text="sdl2", clipboard=["sdl2", "dummy"], image=True,
                        video=None, audio=None, spelling=None, camera=None)
hidden = deps["hiddenimports"] + ["hid", "mido.backends.rtmidi", "rtmidi"]
if sys.platform.startswith("linux"):
    hidden.append("hidraw")

a = Analysis(
    [os.path.join(ROOT, "main.py")],
    pathex=[ROOT],
    datas=[(os.path.join(ROOT, "jctool", "app", "icon.png"), os.path.join("jctool", "app")),
           (os.path.join(ROOT, "jctool", "app", "res"), os.path.join("jctool", "app", "res"))],
    hiddenimports=hidden,
    excludes=deps["excludes"] + ["pytest", "jnius", "android"],
    hookspath=hookspath(),
    runtime_hooks=runtime_hooks(),
)
pyz = PYZ(a.pure)

extra = []
if sys.platform == "win32":
    from kivy_deps import angle, glew, sdl2
    extra = [Tree(p) for p in (sdl2.dep_bins + glew.dep_bins + angle.dep_bins)]

exe = EXE(
    pyz, a.scripts, [],
    exclude_binaries=True,
    name=NAME,
    console=False,
    icon=os.path.join(SPECPATH, "icon.icns" if sys.platform == "darwin" else "icon.ico"),
)
# The command line, next to the app (a console program; it doesn't use Kivy)
c = Analysis(
    [os.path.join(SPECPATH, "jctool_cli.py")],
    pathex=[ROOT],
    hiddenimports=["hid", "mido.backends.rtmidi", "rtmidi"] + (["hidraw"] if sys.platform.startswith("linux") else []),
    excludes=["kivy", "pytest", "jnius", "android"],
)
exe_cli = EXE(
    PYZ(c.pure), c.scripts, [],
    exclude_binaries=True,
    name="jctool-cli",
    console=True,
    icon=os.path.join(SPECPATH, "icon.icns" if sys.platform == "darwin" else "icon.ico"),
)
coll = COLLECT(exe, exe_cli, a.binaries, a.datas, c.binaries, c.datas, *extra, name=NAME)

if sys.platform == "darwin":
    app = BUNDLE(
        coll,
        name="Joy-Con Toolkit.app",
        icon=os.path.join(SPECPATH, "icon.icns"),
        bundle_identifier="org.jctool.jctool",
        version=__version__,
        info_plist={
            "CFBundleShortVersionString": __version__,
            "NSHighResolutionCapable": True,
            "NSBluetoothAlwaysUsageDescription": "Joy-Con Toolkit talks to your Joy-Con and Pro Controller.",
        },
    )
