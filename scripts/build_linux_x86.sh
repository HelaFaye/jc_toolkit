#!/bin/sh
# The 32-bit x86 Linux build. Kivy and python-rtmidi have no 32-bit Linux wheels, so they're
# compiled here, against Debian 12's SDL2 and ALSA. It runs inside a 32-bit Debian 12
# container (glibc 2.36: the build runs on that and newer), from the repo root:
#
#   docker run --rm --platform linux/386 -v "$PWD":/src -w /src i386/debian:bookworm \
#       sh scripts/build_linux_x86.sh
#
# Output: dist/JoyConToolkit-<version>-linux-x86.tar.gz, self-tested under Xvfb.
set -eu
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
# Compilers and headers for Kivy (SDL2, OpenGL) and python-rtmidi (ALSA); Xvfb and Mesa
# for the self-test
apt-get install -y -qq --no-install-recommends \
    python3 python3-venv python3-dev build-essential pkg-config ca-certificates \
    libsdl2-dev libsdl2-image-dev libsdl2-mixer-dev libsdl2-ttf-dev \
    libgl1-mesa-dev libgles2-mesa-dev libasound2-dev libmtdev1 \
    xvfb xauth libgl1-mesa-dri >/dev/null

# Its own venv: the repo's .venv may be the host's
VENV=.venv-x86
python3 -m venv "$VENV"
"$VENV/bin/python" -m pip install -q --upgrade pip setuptools wheel
# Kivy's sdist pins its own Cython for the build; the rest come as wheels where they exist
"$VENV/bin/python" -m pip install -q -e ".[midi,test]"

JCTOOL_SKIP_GUI_TEST=1 "$VENV/bin/python" -m pytest -q --timeout=300
"$VENV/bin/python" scripts/build.py desktop
xvfb-run -a -s "-screen 0 1100x800x24" dist/JoyConToolkit/JoyConToolkit --selftest
