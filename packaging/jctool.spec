# PyInstaller spec for the desktop builds (Windows, macOS, Linux). Build it on the system it
# is for (PyInstaller doesn't cross-compile): python scripts/build.py desktop
# Output: dist/JoyConToolkit/ (Windows, Linux) or dist/Joy-Con Toolkit.app (macOS), with the
# app (JoyConToolkit) and the command line (jctool-cli).
import os
import sys

# Before Kivy is imported (here and in PyInstaller's hook subprocesses): leave Python's
# logging alone. Otherwise Kivy sets it to TRACE and echoes every PyInstaller message through
# its own handler (186k lines, then a hang, on the Windows runner).
# And no real OpenGL: something in the build opens Kivy's window, and on a runner without
# OpenGL 2.0 (Windows: only "GDI Generic" 1.1) Kivy shows a "The application will leave now"
# message box that waits forever. The mock backend is only for the build, not the app.
for name, value in (("KIVY_LOG_MODE", "PYTHON"), ("KIVY_NO_CONSOLELOG", "1"), ("KIVY_NO_FILELOG", "1"),
                    ("KIVY_NO_ARGS", "1"), ("KIVY_GL_BACKEND", "mock")):
    os.environ.setdefault(name, value)

from kivy.tools.packaging.pyinstaller_hooks import get_deps_minimal, hookspath, runtime_hooks

ROOT = os.path.abspath(os.path.join(SPECPATH, ".."))
NAME = "JoyConToolkit"
sys.path.insert(0, ROOT)
from jctool import __version__  # noqa: E402

# Providers named (not True) so that no window is opened while building. x11: Kivy's fallback
# when SDL can't open a window
LINUX = sys.platform.startswith("linux")
# xclip / xsel: on Linux, Kivy's middle-click paste (its "cutbuffer") uses them
deps = get_deps_minimal(window=["sdl2", "x11"] if LINUX else "sdl2", text="sdl2",
                        clipboard=["sdl2", "xclip", "xsel", "dummy"] if LINUX else ["sdl2", "dummy"], image=True,
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
    excludes=deps["excludes"] + ["pytest", "jnius", "android", "docutils"],
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
# Linux: without their debug symbols, Kivy's modules are a fraction of the size (macOS
# isn't stripped: that would break the binaries' signatures)
coll = COLLECT(exe, exe_cli, a.binaries, a.datas, c.binaries, c.datas, *extra, name=NAME, strip=LINUX)

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
