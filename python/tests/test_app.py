"""The Kivy app's own self-test (python -m jctool.app --selftest), when a display (or
xvfb-run) is available."""
import os
import shutil
import subprocess
import sys

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))


def command():
    if os.environ.get("JCTOOL_SKIP_GUI_TEST"):
        return None
    cmd = [sys.executable, "-m", "jctool.app", "--selftest"]
    if os.environ.get("DISPLAY") or sys.platform in ("win32", "darwin"):
        return cmd
    if shutil.which("xvfb-run"):
        return ["xvfb-run", "-a", "-s", "-screen 0 1100x800x24"] + cmd
    return None


@pytest.mark.skipif(command() is None, reason="needs a display or xvfb-run")
def test_app_selftest():
    pytest.importorskip("kivy")
    res = subprocess.run(command(), cwd=os.path.join(HERE, ".."), capture_output=True, text=True, timeout=300)
    lines = [l for l in res.stdout.splitlines() if l.startswith(("PASS", "FAIL"))]
    assert res.returncode == 0 and lines and not any(l.startswith("FAIL") for l in lines), "\n".join(lines) + res.stderr[-2000:]
