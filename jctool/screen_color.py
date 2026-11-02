"""Picking a color from anywhere on the screen (the original's eyedropper). Kivy only sees its
own window, so each system has its own way:

- Linux: the desktop portal's color picker (org.freedesktop.portal.Screenshot.PickColor),
  KDE's or GNOME's own eyedropper, on Wayland and X11 alike. Without a portal, on X11, the
  pixel under the pointer (Xlib).
- Windows: the pixel under the pointer (GDI).
- macOS: the system color panel ("choose color"), whose magnifier picks from the screen.

Two kinds of picker: system_pick() shows the system's own picker and blocks until the user
picks or cancels (run it off the UI thread); under_pointer() reads the pixel under the
pointer now, for a front end that follows the pointer and lets the user confirm.
"""
import ctypes
import ctypes.util
import os
import random
import subprocess
import sys

# ----------------------------------------------------------------------------------------
# Which pickers this system has


def has_system_picker():
    if sys.platform == "darwin":
        return True
    if sys.platform.startswith("linux"):
        return _portal_available()
    return False


def has_pointer_picker():
    if sys.platform == "win32":
        return True
    if sys.platform.startswith("linux") and not _is_android():
        return _x11() is not None
    return False


def _is_android():
    return "ANDROID_ARGUMENT" in os.environ or "ANDROID_PRIVATE" in os.environ


def system_pick(start=(128, 128, 128)):
    """The system's color picker: (r, g, b), or None when cancelled or unavailable."""
    if sys.platform == "darwin":
        return _macos_choose_color(start)
    if sys.platform.startswith("linux"):
        return _portal_pick_color()
    return None


def under_pointer():
    """((x, y), (r, g, b)) of the pixel under the pointer, or None."""
    if sys.platform == "win32":
        return _windows_under_pointer()
    if sys.platform.startswith("linux"):
        x = _x11()
        return x.under_pointer() if x else None
    return None


# ----------------------------------------------------------------------------------------
# Linux: the desktop portal (D-Bus, through jeepney)

PORTAL_BUS = "org.freedesktop.portal.Desktop"
PORTAL_PATH = "/org/freedesktop/portal/desktop"


def _session_bus():
    from jeepney.io.blocking import open_dbus_connection
    return open_dbus_connection(bus="SESSION")


def _portal_available():
    if not os.environ.get("DBUS_SESSION_BUS_ADDRESS") and not os.environ.get("XDG_RUNTIME_DIR"):
        return False
    try:
        from jeepney import DBusAddress, new_method_call
        with _session_bus() as conn:
            props = DBusAddress(PORTAL_PATH, bus_name=PORTAL_BUS, interface="org.freedesktop.DBus.Properties")
            reply = conn.send_and_get_reply(
                new_method_call(props, "Get", "ss", ("org.freedesktop.portal.Screenshot", "version")), timeout=3)
            return reply.body[0][1] >= 2              # PickColor came with version 2
    except Exception:
        return False


def _portal_pick_color(timeout=600):
    """PickColor: the portal's Response signal carries the color (doubles 0-1)."""
    try:
        from jeepney import DBusAddress, MatchRule, message_bus, new_method_call
        from jeepney.io.blocking import Proxy
    except ImportError:
        return None
    try:
        with _session_bus() as conn:
            token = "jctool%d" % random.randrange(1 << 30)
            sender = conn.unique_name[1:].replace(".", "_")
            path = "%s/request/%s/%s" % (PORTAL_PATH, sender, token)
            rule = MatchRule(type="signal", interface="org.freedesktop.portal.Request", member="Response", path=path)
            Proxy(message_bus, conn).AddMatch(rule)
            with conn.filter(rule) as queue:
                portal = DBusAddress(PORTAL_PATH, bus_name=PORTAL_BUS, interface="org.freedesktop.portal.Screenshot")
                conn.send_and_get_reply(new_method_call(portal, "PickColor", "sa{sv}",
                                                        ("", {"handle_token": ("s", token)})), timeout=10)
                signal = conn.recv_until_filtered(queue, timeout=timeout)
            response, results = signal.body
            if response != 0 or "color" not in results:
                return None
            return tuple(max(0, min(255, int(round(v * 255)))) for v in results["color"][1])
    except Exception:
        return None


# ----------------------------------------------------------------------------------------
# Linux (X11): Xlib through ctypes

class _XImageFuncs(ctypes.Structure):
    _fields_ = [("create_image", ctypes.c_void_p),
                ("destroy_image", ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p)),
                ("get_pixel", ctypes.CFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p, ctypes.c_int, ctypes.c_int)),
                ("put_pixel", ctypes.c_void_p), ("sub_image", ctypes.c_void_p), ("add_pixel", ctypes.c_void_p)]


class _XImage(ctypes.Structure):
    _fields_ = [("width", ctypes.c_int), ("height", ctypes.c_int), ("xoffset", ctypes.c_int),
                ("format", ctypes.c_int), ("data", ctypes.c_char_p), ("byte_order", ctypes.c_int),
                ("bitmap_unit", ctypes.c_int), ("bitmap_bit_order", ctypes.c_int), ("bitmap_pad", ctypes.c_int),
                ("depth", ctypes.c_int), ("bytes_per_line", ctypes.c_int), ("bits_per_pixel", ctypes.c_int),
                ("red_mask", ctypes.c_ulong), ("green_mask", ctypes.c_ulong), ("blue_mask", ctypes.c_ulong),
                ("obdata", ctypes.c_char_p), ("f", _XImageFuncs)]


def _channel(pixel, mask):
    """An 8-bit channel from a pixel and its mask (any width and position)."""
    if not mask:
        return 0
    shift = (mask & -mask).bit_length() - 1
    top = mask >> shift
    return ((pixel & mask) >> shift) * 255 // top


class _X11:
    ZPIXMAP = 2

    def __init__(self, lib, display):
        self.lib, self.display = lib, display
        self.root = lib.XDefaultRootWindow(display)

    def pointer(self):
        root, child = ctypes.c_ulong(), ctypes.c_ulong()
        rx, ry, wx, wy, mask = (ctypes.c_int() for _ in range(5))
        if not self.lib.XQueryPointer(self.display, self.root, ctypes.byref(root), ctypes.byref(child),
                                      ctypes.byref(rx), ctypes.byref(ry), ctypes.byref(wx), ctypes.byref(wy),
                                      ctypes.byref(mask)):
            return None
        return rx.value, ry.value

    def pixel(self, x, y):
        image = self.lib.XGetImage(self.display, self.root, x, y, 1, 1, ctypes.c_ulong(-1).value, self.ZPIXMAP)
        if not image:
            return None
        img = image.contents
        try:
            p = img.f.get_pixel(image, 0, 0)
            return _channel(p, img.red_mask), _channel(p, img.green_mask), _channel(p, img.blue_mask)
        finally:
            img.f.destroy_image(image)

    def under_pointer(self):
        pos = self.pointer()
        if pos is None:
            return None
        rgb = self.pixel(*pos)
        return (pos, rgb) if rgb else None


_x11_cache = []


def _x11():
    """The X display (cached), or None: not X11 (or XWayland), or no libX11."""
    if _x11_cache:
        return _x11_cache[0]
    x = None
    if os.environ.get("DISPLAY"):
        try:
            lib = ctypes.CDLL(ctypes.util.find_library("X11") or "libX11.so.6")
            lib.XOpenDisplay.restype = ctypes.c_void_p
            lib.XOpenDisplay.argtypes = [ctypes.c_char_p]
            lib.XDefaultRootWindow.restype = ctypes.c_ulong
            lib.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
            lib.XQueryPointer.argtypes = [ctypes.c_void_p, ctypes.c_ulong] + [ctypes.c_void_p] * 7
            lib.XGetImage.restype = ctypes.POINTER(_XImage)
            lib.XGetImage.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_int,
                                      ctypes.c_uint, ctypes.c_uint, ctypes.c_ulong, ctypes.c_int]
            display = lib.XOpenDisplay(None)
            if display:
                x = _X11(lib, display)
        except OSError:
            x = None
    _x11_cache.append(x)
    return x


# ----------------------------------------------------------------------------------------
# Windows: GDI through ctypes

def _windows_under_pointer():
    try:
        user32, gdi32 = ctypes.windll.user32, ctypes.windll.gdi32
    except AttributeError:
        return None

    class POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]
    # Physical pixels on every monitor, whatever scaling the app runs with
    old = None
    try:
        user32.SetThreadDpiAwarenessContext.restype = ctypes.c_void_p
        user32.SetThreadDpiAwarenessContext.argtypes = [ctypes.c_void_p]
        old = user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4))   # PER_MONITOR_AWARE_V2
    except AttributeError:
        pass
    try:
        pt = POINT()
        if not user32.GetCursorPos(ctypes.byref(pt)):
            return None
        user32.GetDC.restype = ctypes.c_void_p
        user32.ReleaseDC.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        gdi32.GetPixel.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
        gdi32.GetPixel.restype = ctypes.c_uint32
        dc = user32.GetDC(None)
        try:
            c = gdi32.GetPixel(dc, pt.x, pt.y)
        finally:
            user32.ReleaseDC(None, dc)
        if c == 0xFFFFFFFF:                       # CLR_INVALID
            return None
        return (pt.x, pt.y), (c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF)
    finally:
        if old:
            user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(old))


# ----------------------------------------------------------------------------------------
# macOS: the system color panel, through osascript

def _macos_choose_color(start):
    default = "{%d, %d, %d}" % tuple(v * 257 for v in start)
    script = "choose color default color %s" % default
    try:
        out = subprocess.run(["osascript", "-e", script], capture_output=True, text=True, timeout=600)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if out.returncode != 0:                       # Cancelled (-128)
        return None
    try:
        return tuple(int(v) // 257 for v in out.stdout.strip().split(","))[:3]
    except ValueError:
        return None
