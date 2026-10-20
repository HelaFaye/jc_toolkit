"""The native Windows on ARM (ARM64) build. Kivy, its SDL2 / GLEW libraries, hidapi and
python-rtmidi have no Windows ARM64 wheels, so this compiles them:

    python scripts\\build_windows_arm64.py        (an ARM64 Python 3.10+, on Windows on ARM)

1. vcpkg builds SDL2, SDL2_image, SDL2_ttf, SDL2_mixer and GLEW for arm64-windows
   (VCPKG_ROOT or VCPKG_INSTALLATION_ROOT if set, else a clone in .vcpkg). Visual Studio
   2022 (or its Build Tools) with the ARM64 C++ tools is needed, for vcpkg and for the
   Python extensions.
2. .venv-arm64 gets Kivy built from source against them (without pip's build isolation:
   Kivy's build requirements on Windows include the kivy_deps wheels, which are x86/x64
   only), hidapi and python-rtmidi from source, and the app.
3. A kivy_deps.vcpkg package in the venv points Kivy at vcpkg's DLLs, like the kivy_deps
   wheels do on x64: Kivy adds it to the DLL search path when it starts, and the
   PyInstaller spec bundles its DLLs.
4. The tests, then scripts/build.py desktop: dist\\JoyConToolkit-<version>-windows-arm64.zip.
"""
import os
import shutil
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
VENV = os.path.join(ROOT, ".venv-arm64")
TRIPLET = "arm64-windows"
PORTS = ["sdl2", "sdl2-image[libjpeg-turbo]", "sdl2-ttf", "sdl2-mixer", "glew"]
KIVY = "kivy==2.3.1"
# Kivy's own build requirements (its pyproject.toml), minus the kivy_deps wheels
KIVY_BUILD = ["setuptools~=69.2.0", "wheel~=0.44.0", "packaging~=24.0", "cython>=0.29.1,<=3.0.11"]
# Kivy's and the app's requirements, without the kivy_deps wheels (installed with --no-deps)
RUNTIME = ["Kivy-Garden>=0.1.4", "docutils", "pygments", "requests", "filetype", "pywin32",
           "hidapi>=0.14", "mido>=1.3", "python-rtmidi>=1.5", "pytest", "pytest-timeout"]


def run(*cmd, **kw):
    print("+", " ".join(cmd), flush=True)
    subprocess.check_call(cmd, **kw)


def vcpkg_root():
    for var in ("VCPKG_ROOT", "VCPKG_INSTALLATION_ROOT"):
        root = os.environ.get(var)
        if root and os.path.isfile(os.path.join(root, "vcpkg.exe")):
            return root
    root = os.path.join(ROOT, ".vcpkg")
    if not os.path.isdir(root):
        run("git", "clone", "--depth", "1", "https://github.com/microsoft/vcpkg.git", root)
    if not os.path.isfile(os.path.join(root, "vcpkg.exe")):
        run(os.path.join(root, "bootstrap-vcpkg.bat"), "-disableMetrics")
    return root


def main():
    if sys.platform != "win32" or "ARM64" not in sys.version.upper():
        sys.exit("Run this with an ARM64 Python on Windows on ARM (python.org's ARM64 installer).")
    cache = os.environ.get("VCPKG_DEFAULT_BINARY_CACHE")
    if cache:
        os.makedirs(cache, exist_ok=True)      # vcpkg wants it to exist
    root = vcpkg_root()
    run(os.path.join(root, "vcpkg.exe"), "install", "--triplet", TRIPLET, "--recurse", *PORTS)
    inst = os.path.join(root, "installed", TRIPLET)
    include, lib, bin_ = (os.path.join(inst, d) for d in ("include", "lib", "bin"))

    if not os.path.isfile(os.path.join(VENV, "Scripts", "python.exe")):
        run(sys.executable, "-m", "venv", VENV)
    py = os.path.join(VENV, "Scripts", "python.exe")
    pip = [py, "-m", "pip", "install"]
    run(*pip, "--upgrade", "pip")
    run(*pip, *KIVY_BUILD)

    # Kivy: SDL2 from KIVY_SDL2_PATH (its headers and libs), GLEW (GL/glew.h, glew32.lib)
    # from the compiler's INCLUDE and LIB
    env = dict(os.environ)
    env["KIVY_SDL2_PATH"] = os.pathsep.join([os.path.join(include, "SDL2"), lib])
    env["INCLUDE"] = include + os.pathsep + env.get("INCLUDE", "")
    env["LIB"] = lib + os.pathsep + env.get("LIB", "")
    # The compiler and linker also read these, whatever environment setuptools sets up
    env["CL"] = ('/I"%s" ' % include) + env.get("CL", "")
    env["LINK"] = ('/LIBPATH:"%s" ' % lib) + env.get("LINK", "")
    run(*pip, "--no-build-isolation", "--no-deps", "--no-binary", "kivy", KIVY, env=env)
    run(*pip, *RUNTIME)
    run(*pip, "--no-deps", "-e", ROOT)

    # kivy_deps.vcpkg: vcpkg's DLLs for Kivy (at startup) and for the PyInstaller spec
    site = subprocess.check_output([py, "-c", "import sysconfig; print(sysconfig.get_paths()['purelib'])"],
                                   text=True).strip()
    pkg = os.path.join(site, "kivy_deps", "vcpkg")
    os.makedirs(pkg, exist_ok=True)
    open(os.path.join(site, "kivy_deps", "__init__.py"), "a").close()
    with open(os.path.join(pkg, "__init__.py"), "w") as f:
        f.write('"""vcpkg\'s SDL2 and GLEW DLLs (scripts/build_windows_arm64.py)."""\n'
                "import os\n\n"
                "dep_bins = [%r]\n"
                "for _p in dep_bins:\n"
                "    if os.path.isdir(_p):\n"
                "        os.environ['PATH'] = _p + os.pathsep + os.environ.get('PATH', '')\n"
                "        os.add_dll_directory(_p)\n" % bin_)

    run(py, "-c", "import kivy, kivy.core.window._window_sdl2, kivy.graphics; print('Kivy', kivy.__version__, 'imports')")
    test_env = dict(os.environ, JCTOOL_SKIP_GUI_TEST="1")
    run(py, "-m", "pytest", "-q", "--timeout=300", cwd=ROOT, env=test_env)
    run(py, os.path.join(ROOT, "scripts", "build.py"), "desktop", cwd=ROOT)
    print("\nvcpkg DLLs:", bin_, "\nDone:", [f for f in os.listdir(os.path.join(ROOT, "dist")) if f.endswith(".zip")])


if __name__ == "__main__":
    if shutil.which("git") is None and not os.environ.get("VCPKG_ROOT"):
        print("Note: git is needed to fetch vcpkg (or set VCPKG_ROOT)")
    main()
