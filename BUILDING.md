# Building Joy-Con Toolkit

Each target, how it's built, and what's unusual about it. GitHub Actions builds them all
(`.github/workflows/build.yml`, the `Build` workflow) on every push; the builds are attached
to the run (Actions > the run > Artifacts).

| Target | Built on | Command | Output |
|---|---|---|---|
| Windows x64 | Windows | `scripts\setup_venv.bat`, then `.venv\Scripts\python scripts\build.py desktop` | `dist\JoyConToolkit-<v>-windows-x86_64.zip` |
| Windows x86 (32-bit) | Windows, a 32-bit Python | the same | `...-windows-x86.zip` |
| Windows ARM64 | Windows on ARM, an ARM64 Python | `python scripts\build_windows_arm64.py` | `...-windows-arm64.zip` |
| macOS Apple Silicon / Intel | a Mac of that kind | `scripts/setup_venv.sh`, then `.venv/bin/python scripts/build.py desktop` | `...-macos-arm64.zip` / `...-macos-x86_64.zip` |
| Linux x86_64 / ARM64 | Linux of that kind | the same | `...-linux-x86_64.tar.gz` / `...-linux-arm64.tar.gz` |
| Linux x86 (32-bit) | Linux x86_64 with Docker | `docker run --rm --platform linux/386 -v "$PWD":/src -w /src i386/debian:bookworm sh scripts/build_linux_x86.sh` | `...-linux-x86.tar.gz` |
| Android | Linux or macOS (Windows: WSL2) | `python3 scripts/build.py android` | `bin/jctool-<v>-arm64-v8a_armeabi-v7a-debug.apk` |
| Nintendo Switch | anywhere with Docker, or devkitPro | `docker run --rm -v "$PWD":/src -w /src devkitpro/devkita64 make -C switch` | `switch/jctool.nro` |

PyInstaller doesn't cross-compile: every desktop build runs on the system (and the
architecture) it's for. The version comes from `jctool/__init__.py` (keep `pyproject.toml`'s
in step); the Switch Makefile reads it too.

## Releases

A tag `v<version>` publishes everything as a GitHub release. So does a manual run of the
workflow (Actions > Build > Run workflow) given a tag name, which creates the tag on the
commit it ran on. The release job waits for every build: if one fails, nothing is published.

## All desktop builds

- `packaging/jctool.spec` makes two programs, the app (`JoyConToolkit`, no console) and the
  command line (`jctool-cli`, a console program without Kivy), in one folder.
- While the spec runs, Kivy is told to leave Python's logging alone and to use its mock
  OpenGL backend (only for the build). Without the first, Kivy sets logging to TRACE and echoes
  every PyInstaller message (186k lines, then a hang on the Windows runner); without the
  second, something in the build opens Kivy's window, and on a machine without OpenGL 2.0
  Kivy shows a "The application will leave now" message box that waits forever.
- Kivy's providers are named (`get_deps_minimal`), so that no window opens while building.
- The packaged app saves its files in `Documents/Joy-Con Toolkit`.

## Windows

**x64.** `setup_venv.bat` uses the `python` on PATH (or `%PYTHON%`), then the `py`
launcher: the launcher alone may pick a Python too new for Kivy's wheels (3.14 had none at
the time). `build.py` adds the `kivy_deps.sdl2`, `glew` and `angle` wheels, whose DLLs the
spec bundles. The builds aren't signed: SmartScreen warns until you choose Run anyway.

**x86 (32-bit).** Use a 32-bit Python (python.org's "Windows installer (32-bit)", or
`actions/setup-python` with `architecture: x86`); `build.py` names the build by the
Python's architecture, not Windows'. Kivy 2.3.1 and its newer `kivy_deps` have no 32-bit
wheels, so `setup_venv.bat` and `build.py` use `packaging/constraints-win32.txt` (Kivy 2.3.0
and the matching `kivy_deps`). python-rtmidi has no 32-bit wheel either, so the 32-bit venv
skips the `midi` extra: MIDI files work, MIDI devices don't.

**ARM64.** Nothing it needs has Windows ARM64 wheels except PyInstaller, so
`scripts/build_windows_arm64.py` compiles the rest:

1. You need an ARM64 Python (python.org's ARM64 installer; 3.12 in CI), Visual Studio 2022
   or its Build Tools with the "C++ ARM64 build tools", and git.
2. vcpkg builds SDL2, SDL2_image (with libjpeg-turbo), SDL2_ttf, SDL2_mixer and GLEW for
   `arm64-windows`. It uses `VCPKG_ROOT` or `VCPKG_INSTALLATION_ROOT` if set, else clones
   vcpkg into `.vcpkg`. The first build takes a while; CI keeps vcpkg's binary cache
   (`VCPKG_DEFAULT_BINARY_CACHE`, `.vcpkg-cache`) between runs.
3. Kivy 2.3.1 is built from source with `--no-build-isolation --no-deps`: on Windows its
   build requirements and its requirements include the `kivy_deps` wheels, which are
   x86/x64 only. Its other requirements, and the app's, are installed separately (and the
   app with `--no-deps`, so that pip doesn't look for `kivy_deps` again). Kivy finds SDL2
   through `KIVY_SDL2_PATH`, and GLEW (`GL/glew.h`, `glew32.lib`) through the compiler's
   paths: the script sets `INCLUDE` / `LIB`, and `CL` / `LINK`, which MSVC reads whatever
   environment setuptools sets up. Kivy is built from its source extracted to `C:\kvsrc`
   (the drive's root), not from pip's temp folder: setuptools puts each object file under
   the source's full path, and from there some paths go past Windows' 260-character limit
   (`C1083: Cannot open compiler generated file`, `LNK1104`).
4. hidapi and python-rtmidi build from their sdists with MSVC. python-rtmidi builds with
   meson, which takes the first compiler on PATH unless told otherwise: on the GitHub
   runner that's MinGW's gcc for x86_64, which can't link against an ARM64 Python
   (`python312.dll: file not recognized`). The script passes meson `--vsenv`
   (`--config-settings=setup-args=--vsenv`) so that it uses Visual Studio.
5. The script writes a `kivy_deps.vcpkg` package into the venv, like the `kivy_deps`
   wheels: its `dep_bins` is vcpkg's `bin` folder. Kivy imports every `kivy_deps` package
   at startup (which adds that folder to the DLL search path: since Python 3.8, Windows
   doesn't use PATH for an extension's DLLs), and the spec bundles every package's
   `dep_bins`.

At run time this build uses desktop OpenGL (GLEW) like the x64 one, so it needs an OpenGL
2.0 driver. Windows on ARM PCs get OpenGL through the "OpenCL, OpenGL, and Vulkan
Compatibility Pack" (Microsoft Store), preinstalled on most; without it Kivy says it can't
find OpenGL 2.0. The x64 build also runs on Windows on ARM, emulated.

## macOS

Apple Silicon builds on `macos-latest`, Intel on `macos-15-intel`. Kivy's wheel is
universal2; hidapi and python-rtmidi have one wheel per architecture, hence one build per
architecture. `build.py` zips the app with `ditto`, which keeps the bundle's symlinks and
attributes (a plain zip breaks it). The binaries aren't stripped: that would invalidate
their (ad hoc) signatures. The builds aren't notarized: Gatekeeper asks you to confirm
(right-click > Open, or `xattr -dr com.apple.quarantine "Joy-Con Toolkit.app"`), and the
first time, macOS may ask to allow Input Monitoring.

## Linux

**x86_64 and ARM64.** Built on Ubuntu 22.04 (`ubuntu-22.04`, `ubuntu-22.04-arm`): the
oldest glibc the runners have (2.35), so the builds run on that and newer.

- The spec strips the binaries on Linux (`strip=True`): Kivy's modules ship with their
  debug symbols, and stripped the download is about half the size (29 instead of 56 MB).
- Kivy's wheel brings its own SDL2, built without Wayland. On a desktop that asks SDL for
  Wayland (`SDL_VIDEODRIVER=wayland`), the app switches it to `x11` when XWayland is there
  (`jctool/app/main.py`), and the spec bundles Kivy's X11 window provider as a fallback.
- The spec bundles Kivy's xclip / xsel clipboard providers: its middle-click paste uses
  them, when those programs are installed.
- `build.py` puts the udev rule and `install.sh` (the rule, and an applications menu entry)
  in the archive.
- Kivy logs that `libmtdev.so.1` is missing on systems without it; that's only for
  multitouch screens.
- The self-test (`JoyConToolkit --selftest`, under Xvfb in CI) drives the packaged app's
  screens against an emulated controller.

**x86 (32-bit).** GitHub has no 32-bit runners, and Kivy and python-rtmidi have no 32-bit
Linux wheels, so `scripts/build_linux_x86.sh` runs in a 32-bit Debian 12 container
(glibc 2.36) on an x86_64 machine: it installs Debian's SDL2, Mesa and ALSA headers, builds
Kivy and python-rtmidi from source in `.venv-x86` (Debian 12's Python 3.11), runs the tests,
builds and self-tests the app. Kivy uses Debian's SDL2 here, which has Wayland support. (Debian 11, with an older glibc, would run on more systems,
but its support ended in August 2026 and its updates left the mirrors: `apt-get install`
fails with 404s.)
Docker Hub limits anonymous image pulls; if `docker run` says "Too Many Requests", wait or
`docker login`.

## Android

`scripts/build.py android` runs buildozer (`buildozer.spec`) from its own `.venv-android`.

- The venv has `--system-site-packages`: buildozer installs its tools with `pip --user`,
  which a plain venv refuses.
- It needs Java 17 and, on Debian/Ubuntu, `git zip unzip autoconf automake libtool
  pkg-config zlib1g-dev libncurses-dev cmake libffi-dev libssl-dev`. The first build
  downloads the Android SDK and NDK (a few GB, in `~/.buildozer`).
- Cython is pinned below 3.1 for python-for-android's recipes.
- python-for-android is its `develop` branch at a fixed commit (`p4a.commit`): the
  release then current installed `charset_normalizer`'s Android wheel without its platform
  tags. That version has no `--feature` option, so the USB host feature is declared in
  `packaging/android/extra_manifest.xml` (`android.extra_manifest_xml`).
- Before buildozer packs the app, `build.py` compiles the Bluetooth helper
  (`native/jctool_hidraw.c`) with the NDK's clang for each ABI in `android.archs`
  (`scripts/build.py helper` does only that).
- The APK is a debug build (debug-signed). `scripts/build.py android release` makes an
  unsigned release APK/AAB to sign with your own key.

## Nintendo Switch

The homebrew (`switch/`) is C with libnx, built with devkitPro's devkitA64:
`make -C switch` with devkitPro installed, or the `devkitpro/devkita64` Docker image (CI
uses it as the job's container).

- `APP_VERSION` comes from `jctool/__init__.py`; the icon is `switch/icon.jpg` (256x256 JPEG,
  made from the app's icon).
- `switch/source/tables.h` (the HD Rumble amplitude tables) is generated from
  `jctool/tables.py` by `tools/gen_switch_tables.py`.
- SPI access needs the Switch's system version 6.0.0 or newer (hiddbg's
  `ReadSerialFlash` / `WriteSerialFlash`). The app makes those calls itself (the same IPC
  as libnx) so that it can wait with a timeout.

## Tests

`.venv/bin/python -m pytest` (in CI on Linux, and before each Windows and macOS build).
`JCTOOL_SKIP_GUI_TEST=1` skips the app's self-test where there's no display or OpenGL.
