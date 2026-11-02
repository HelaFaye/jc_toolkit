"""The screen color pickers: Xlib against an Xvfb screen of a known color, and the desktop
portal against a stand-in portal service on a private D-Bus session bus."""
import ctypes
import os
import shutil
import subprocess
import sys
import threading
import time

import pytest

from jctool import screen_color

linux = pytest.mark.skipif(not sys.platform.startswith("linux"), reason="Linux only")


def test_channels():
    assert screen_color._channel(0x123456, 0xFF0000) == 0x12
    assert screen_color._channel(0x123456, 0x00FF00) == 0x34
    assert screen_color._channel(0xF800, 0xF800) == 255        # 5 bits (RGB565): full scale
    assert screen_color._channel(0, 0) == 0


@linux
@pytest.mark.skipif(not shutil.which("Xvfb"), reason="needs Xvfb")
def test_x11_pixel_under_pointer(monkeypatch):
    display = ":%d" % (90 + os.getpid() % 9)
    xvfb = subprocess.Popen(["Xvfb", display, "-screen", "0", "320x240x24", "-nolisten", "tcp"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        monkeypatch.setenv("DISPLAY", display)
        monkeypatch.setattr(screen_color, "_x11_cache", [])
        for _ in range(50):
            if screen_color._x11() is not None:
                break
            screen_color._x11_cache.clear()
            time.sleep(0.1)
        x = screen_color._x11()
        assert x is not None and screen_color.has_pointer_picker()
        lib = x.lib
        lib.XSetWindowBackground.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong]
        lib.XClearWindow.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        lib.XWarpPointer.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong] + [ctypes.c_int] * 2 + \
            [ctypes.c_uint] * 2 + [ctypes.c_int] * 2
        lib.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
        lib.XSetWindowBackground(x.display, x.root, 0x123456)
        lib.XClearWindow(x.display, x.root)
        lib.XWarpPointer(x.display, 0, x.root, 0, 0, 0, 0, 17, 23)
        lib.XSync(x.display, 0)
        assert screen_color.under_pointer() == ((17, 23), (0x12, 0x34, 0x56))
    finally:
        xvfb.terminate()
        xvfb.wait()


class FakePortal(threading.Thread):
    """org.freedesktop.portal.Desktop with Screenshot.PickColor (version 2): answers with
    `color`, or cancels (response 1) when color is None."""

    def __init__(self, color):
        super().__init__(daemon=True)
        from jeepney.io.blocking import open_dbus_connection
        self.color = color
        self.conn = open_dbus_connection(bus="SESSION")
        from jeepney import message_bus
        from jeepney.io.blocking import Proxy
        Proxy(message_bus, self.conn).RequestName(screen_color.PORTAL_BUS)
        self.calls = []

    def run(self):
        from jeepney import DBusAddress, new_method_return, new_signal
        while True:
            try:
                msg = self.conn.receive(timeout=30)
            except Exception:
                return
            h = msg.header
            member = h.fields.get(3)                  # HeaderFields.member
            if member == "Get":
                self.conn.send(new_method_return(msg, "v", (("u", 2),)))
            elif member == "PickColor":
                parent, options = msg.body
                self.calls.append(options)
                sender = h.fields.get(7)[1:].replace(".", "_")
                path = "%s/request/%s/%s" % (screen_color.PORTAL_PATH, sender, options["handle_token"][1])
                self.conn.send(new_method_return(msg, "o", (path,)))
                request = DBusAddress(path, interface="org.freedesktop.portal.Request")
                if self.color is None:
                    body = (1, {})
                else:
                    body = (0, {"color": ("(ddd)", tuple(v / 255.0 for v in self.color))})
                self.conn.send(new_signal(request, "Response", "ua{sv}", body))


@pytest.fixture
def session_bus(monkeypatch):
    if not shutil.which("dbus-daemon"):
        pytest.skip("needs dbus-daemon")
    pytest.importorskip("jeepney")
    proc = subprocess.Popen(["dbus-daemon", "--session", "--nofork", "--print-address"],
                            stdout=subprocess.PIPE, text=True)
    address = proc.stdout.readline().strip()
    monkeypatch.setenv("DBUS_SESSION_BUS_ADDRESS", address)
    yield
    proc.terminate()
    proc.wait()


@linux
def test_portal_pick_color(session_bus):
    assert not screen_color._portal_available()          # No portal on the bus yet
    portal = FakePortal((0x0A, 0xB9, 0xE6))
    portal.start()
    assert screen_color.has_system_picker()
    assert screen_color.system_pick() == (0x0A, 0xB9, 0xE6)
    assert portal.calls and portal.calls[0]["handle_token"][1].startswith("jctool")


@linux
def test_portal_cancelled(session_bus):
    FakePortal(None).start()
    assert screen_color.system_pick() is None
