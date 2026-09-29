#!/bin/sh
# Creates python/.venv with the app, MIDI device support and the test tools.
#   scripts/setup_venv.sh            (PYTHON=python3.12 scripts/setup_venv.sh for another Python)
# Then: .venv/bin/jctool, or source .venv/bin/activate.
set -e
cd "$(dirname "$0")/.."
PY=${PYTHON:-python3}
if [ ! -x .venv/bin/python ]; then
    "$PY" -m venv .venv
fi
.venv/bin/python -m pip install --upgrade pip
.venv/bin/python -m pip install -e ".[midi,test]"
echo
echo "Ready. Run the app with: .venv/bin/jctool   (or: source .venv/bin/activate; jctool)"
