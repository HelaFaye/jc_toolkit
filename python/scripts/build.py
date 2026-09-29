"""Builds the app. Run it with the venv's Python (scripts/setup_venv.sh first):

    .venv/bin/python scripts/build.py desktop     # This system: Windows, macOS or Linux
    .venv/bin/python scripts/build.py android     # An APK (on Linux or macOS), with buildozer
    .venv/bin/python scripts/build.py android release

Desktop builds go to dist/ (a folder, and a .zip / .tar.gz of it); PyInstaller builds only
for the system it runs on. Android builds go to bin/.
"""
import os
import platform
import shutil
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, ROOT)
from jctool import __version__  # noqa: E402


def pip(*packages):
    subprocess.check_call([sys.executable, "-m", "pip", "install", "--upgrade"] + list(packages))


def check_venv():
    if sys.prefix == getattr(sys, "base_prefix", sys.prefix) and not os.environ.get("JCTOOL_NO_VENV"):
        sys.exit("Run this with the venv's Python (scripts/setup_venv.sh or setup_venv.bat creates it),\n"
                 "or set JCTOOL_NO_VENV=1.")


def system_name():
    return {"win32": "windows", "darwin": "macos"}.get(sys.platform, "linux")


def desktop():
    pip("pyinstaller>=6.0")
    if sys.platform == "win32":
        pip("kivy_deps.sdl2", "kivy_deps.glew", "kivy_deps.angle")
    dist = os.path.join(ROOT, "dist")
    subprocess.check_call([sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean",
                           "--distpath", dist, "--workpath", os.path.join(ROOT, "build", "pyinstaller"),
                           os.path.join(ROOT, "packaging", "jctool.spec")])
    arch = platform.machine().lower().replace("amd64", "x86_64")
    archive = os.path.join(dist, "JoyConToolkit-%s-%s-%s" % (__version__, system_name(), arch))
    if sys.platform == "darwin":
        # ditto keeps the app bundle's symlinks and attributes
        subprocess.check_call(["ditto", "-c", "-k", "--keepParent", os.path.join(dist, "Joy-Con Toolkit.app"),
                               archive + ".zip"])
        out = archive + ".zip"
    elif sys.platform == "win32":
        out = shutil.make_archive(archive, "zip", dist, "JoyConToolkit")
    else:
        out = shutil.make_archive(archive, "gztar", dist, "JoyConToolkit")
    print("\nBuilt:", out)


def android(mode="debug"):
    if sys.platform == "win32":
        sys.exit("Android builds need Linux or macOS (on Windows: WSL2, or the GitHub Actions workflow).")
    pip("buildozer", "cython<3.1", "setuptools")
    env = dict(os.environ)
    subprocess.check_call([os.path.join(os.path.dirname(sys.executable), "buildozer"), "android", mode],
                          cwd=ROOT, env=env)
    print("\nBuilt: the APK in", os.path.join(ROOT, "bin"))


def main():
    check_venv()
    what = sys.argv[1] if len(sys.argv) > 1 else "desktop"
    if what == "desktop":
        desktop()
    elif what == "android":
        android(sys.argv[2] if len(sys.argv) > 2 else "debug")
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
