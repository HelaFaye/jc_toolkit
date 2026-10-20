"""Builds the app. Run it with the venv's Python (scripts/setup_venv.sh first):

    .venv/bin/python scripts/build.py desktop     # This system: Windows, macOS or Linux
    .venv/bin/python scripts/build.py android     # An APK (on Linux or macOS), with buildozer
    .venv/bin/python scripts/build.py android release
    .venv/bin/python scripts/build.py helper      # Only the Bluetooth helper (needs the NDK)

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


def arch_name():
    """x86_64, x86, arm64: the Python's architecture (a 32-bit Python on 64-bit Windows is x86)."""
    machine = platform.machine().lower()
    if machine in ("amd64", "x86_64", "i386", "i686", "x86"):
        return "x86_64" if sys.maxsize > 2 ** 32 else "x86"
    return {"aarch64": "arm64"}.get(machine, machine)


def desktop():
    if arch_name() == "x86" and sys.platform == "win32":
        # Kivy's last 32-bit Windows wheels (see setup_venv.bat)
        os.environ.setdefault("PIP_CONSTRAINT", os.path.join(ROOT, "packaging", "constraints-win32.txt"))
    pip("pyinstaller>=6.0")
    if sys.platform == "win32":
        pip("kivy_deps.sdl2", "kivy_deps.glew", "kivy_deps.angle")
    dist = os.path.join(ROOT, "dist")
    subprocess.check_call([sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean",
                           "--distpath", dist, "--workpath", os.path.join(ROOT, "build", "pyinstaller"),
                           os.path.join(ROOT, "packaging", "jctool.spec")])
    archive = os.path.join(dist, "JoyConToolkit-%s-%s-%s" % (__version__, system_name(), arch_name()))
    if sys.platform == "darwin":
        # ditto keeps the app bundle's symlinks and attributes
        subprocess.check_call(["ditto", "-c", "-k", "--keepParent", os.path.join(dist, "Joy-Con Toolkit.app"),
                               archive + ".zip"])
        out = archive + ".zip"
    elif sys.platform == "win32":
        out = shutil.make_archive(archive, "zip", dist, "JoyConToolkit")
    else:
        # Next to the app: the udev rule and the script that installs it and a menu entry
        linux = os.path.join(ROOT, "packaging", "linux")
        for name in ("50-nintendo-switch-controllers.rules", "install.sh"):
            shutil.copy2(os.path.join(linux, name), os.path.join(dist, "JoyConToolkit", name))
        out = shutil.make_archive(archive, "gztar", dist, "JoyConToolkit")
    print("\nBuilt:", out)


# The APK's ABIs (buildozer.spec's android.archs) and their NDK compilers
ANDROID_ABIS = {"arm64-v8a": "aarch64-linux-android", "armeabi-v7a": "armv7a-linux-androideabi"}
ANDROID_MIN_API = 24


def find_ndk():
    """The Android NDK: $ANDROID_NDK_HOME / $ANDROID_NDK_ROOT / $ANDROID_NDK_LATEST_HOME, or
    the one buildozer downloaded."""
    for var in ("ANDROID_NDK_HOME", "ANDROID_NDK_ROOT", "ANDROID_NDK_LATEST_HOME"):
        path = os.environ.get(var)
        if path and os.path.isdir(os.path.join(path, "toolchains", "llvm")):
            return path
    import glob
    found = sorted(glob.glob(os.path.expanduser("~/.buildozer/android/platform/android-ndk-*")))
    return found[-1] if found else None


def build_bluetooth_helper(ndk):
    """native/jctool_hidraw.c, for every ABI of the APK: jctool/native/<abi>/jctool-hidraw."""
    import glob
    prebuilt = glob.glob(os.path.join(ndk, "toolchains", "llvm", "prebuilt", "*", "bin"))
    if not prebuilt:
        sys.exit("No LLVM toolchain in the NDK at %s" % ndk)
    source = os.path.join(ROOT, "native", "jctool_hidraw.c")
    for abi, triple in ANDROID_ABIS.items():
        cc = os.path.join(prebuilt[0], "%s%d-clang" % (triple, ANDROID_MIN_API))
        out_dir = os.path.join(ROOT, "jctool", "native", abi)
        os.makedirs(out_dir, exist_ok=True)
        out = os.path.join(out_dir, "jctool-hidraw")
        subprocess.check_call([cc, "-O2", "-Wall", "-s", "-o", out, source])
        os.chmod(out, 0o755)
        print("Built the Bluetooth helper:", os.path.relpath(out, ROOT))


def android_venv():
    """.venv-android: buildozer installs python-for-android's tools with pip --user, which a
    venv allows only when it sees the system site-packages. Returns its bin folder."""
    venv = os.path.join(ROOT, ".venv-android")
    bin_dir = os.path.join(venv, "bin")
    if not os.path.exists(os.path.join(bin_dir, "python")):
        subprocess.check_call([sys.executable, "-m", "venv", "--system-site-packages", venv])
    subprocess.check_call([os.path.join(bin_dir, "python"), "-m", "pip", "install", "--upgrade",
                           "buildozer", "cython<3.1", "setuptools"])
    return bin_dir


def android(mode="debug"):
    if sys.platform == "win32":
        sys.exit("Android builds need Linux or macOS (on Windows: WSL2, or the GitHub Actions workflow).")
    bin_dir = android_venv()
    buildozer = [os.path.join(bin_dir, "buildozer"), "android", mode]
    # buildozer looks for cython (and runs pip) from PATH: the venv's first
    os.environ["PATH"] = bin_dir + os.pathsep + os.environ.get("PATH", "")
    ndk = find_ndk()
    if ndk is None:
        # The first build downloads the SDK and NDK; then the helper, then the APK again
        subprocess.check_call(buildozer, cwd=ROOT)
        ndk = find_ndk()
        if ndk is None:
            sys.exit("No Android NDK found: set ANDROID_NDK_HOME")
    build_bluetooth_helper(ndk)
    subprocess.check_call(buildozer, cwd=ROOT)
    print("\nBuilt: the APK in", os.path.join(ROOT, "bin"))


def main():
    check_venv()
    what = sys.argv[1] if len(sys.argv) > 1 else "desktop"
    if what == "desktop":
        desktop()
    elif what == "android":
        android(sys.argv[2] if len(sys.argv) > 2 else "debug")
    elif what == "helper":
        ndk = find_ndk()
        if ndk is None:
            sys.exit("No Android NDK found: set ANDROID_NDK_HOME")
        build_bluetooth_helper(ndk)
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
