"""Bluetooth controllers on Android through /dev/hidraw, with hidapi's interface
(enumerate(vid, pid) and device() with open_path, write, read, close).

Android's Bluetooth service connects a paired controller and the kernel makes a hidraw
node for it, which only root can open. The app runs its native helper (jctool-hidraw,
built with the NDK from native/jctool_hidraw.c) through su; the helper opens the node and
relays the reports over its stdin / stdout. It also moves the controller off the kernel's
hid_nintendo driver while it's open, and gives it back after.

Works on any Linux too (JCTOOL_HID_BACKEND=bridge; without su when already allowed).
Settings: JCTOOL_HIDRAW_HELPER (the helper's path), JCTOOL_HIDRAW_SU=0/1 (run it with su or
not; default: with su on Android unless the app is root).
"""
import collections
import os
import platform
import stat
import struct
import subprocess
import threading

PREFIX = "bt:"
BUS_USB, BUS_BLUETOOTH = 0x03, 0x05
QUEUE = 64                        # Like the kernel's hidraw buffer: older reports drop
ABIS = {"aarch64": "arm64-v8a", "arm64": "arm64-v8a", "armv7l": "armeabi-v7a", "armv8l": "armeabi-v7a",
        "x86_64": "x86_64", "i686": "x86"}
last_error = ""                   # Why enumerate() found nothing (shown in the HID listing)


def helper_path():
    env = os.environ.get("JCTOOL_HIDRAW_HELPER")
    if env:
        return env
    abi = ABIS.get(platform.machine(), platform.machine())
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), "native", abi, "jctool-hidraw")


def use_su():
    env = os.environ.get("JCTOOL_HIDRAW_SU")
    if env is not None:
        return env == "1"
    return ("ANDROID_ARGUMENT" in os.environ or "ANDROID_PRIVATE" in os.environ) and os.geteuid() != 0


def command(*args):
    path = helper_path()
    if not os.path.exists(path):
        raise OSError("The Bluetooth helper is missing: %s" % path)
    mode = os.stat(path).st_mode
    if not mode & stat.S_IXUSR:            # Unpacked from the APK without its exec bit
        os.chmod(path, mode | stat.S_IXUSR | stat.S_IRUSR | stat.S_IXGRP | stat.S_IXOTH | stat.S_IRGRP | stat.S_IROTH)
    if use_su():
        return ["su", "-c", " ".join([_quote(path)] + [_quote(a) for a in args])]
    return [path] + list(args)


def _quote(s):
    return "'" + s.replace("'", "'\\''") + "'"


def _environment():
    env = dict(os.environ)
    env.pop("LD_PRELOAD", None)
    return env


def enumerate(vendor_id=0, product_id=0):
    """The hidraw devices, as hidapi-style dicts (path: "bt:<kernel HID device id>")."""
    global last_error
    try:
        res = subprocess.run(command("list"), capture_output=True, timeout=20, env=_environment())
    except (OSError, subprocess.SubprocessError) as e:
        last_error = str(e)
        return []
    if res.returncode != 0:
        last_error = (res.stderr.decode("utf-8", "replace").strip() or
                      "the helper failed (%d): root access denied?" % res.returncode)
        return []
    last_error = ""
    out = []
    for line in res.stdout.decode("utf-8", "replace").splitlines():
        parts = line.split("\t")
        if len(parts) < 8:
            continue
        hid_id, node, bus, vid, pid, driver, name, uniq = parts[:8]
        vid, pid, bus = int(vid, 16), int(pid, 16), int(bus, 16)
        if (vendor_id and vid != vendor_id) or (product_id and pid != product_id):
            continue
        out.append({
            "path": PREFIX + hid_id, "vendor_id": vid, "product_id": pid, "product_string": name,
            "manufacturer_string": "", "serial_number": uniq, "release_number": 0,
            "interface_number": -1 if bus == BUS_BLUETOOTH else 0, "usage_page": 0, "usage": 0,
            "bus_type": 2 if bus == BUS_BLUETOOTH else 1 if bus == BUS_USB else 0,
            "hidraw": node, "driver": driver,
        })
    return out


class device:
    """An open controller, through the helper."""

    def __init__(self):
        self.proc = None
        self.reports = collections.deque(maxlen=QUEUE)
        self.cond = threading.Condition()
        self.error = None
        self.node = ""
        self.write_lock = threading.Lock()

    def open_path(self, path):
        if isinstance(path, bytes):
            path = path.decode()
        hid_id = path[len(PREFIX):] if path.startswith(PREFIX) else path
        self.proc = subprocess.Popen(command("open", hid_id), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, bufsize=0, env=_environment())
        kind, payload = self._frame(self.proc.stdout)
        if kind != b"O":
            self.close()
            raise IOError(payload.decode("utf-8", "replace") if kind == b"E" else
                          "The Bluetooth helper didn't start (root access denied?)")
        self.node = payload.decode()
        threading.Thread(target=self._reader, args=(self.proc.stdout,), name="hidraw bridge", daemon=True).start()

    def _read_exact(self, stream, n):
        data = b""
        while len(data) < n:
            try:
                chunk = stream.read(n - len(data))
            except (OSError, ValueError):
                return None
            if not chunk:
                return None
            data += chunk
        return data

    def _frame(self, stream):
        head = self._read_exact(stream, 3)
        if head is None:
            return None, b""
        length = head[1] | (head[2] << 8)
        payload = self._read_exact(stream, length) if length else b""
        return head[:1], payload or b""

    def _reader(self, stream):
        while True:
            kind, payload = self._frame(stream)
            with self.cond:
                if kind == b"I":
                    self.reports.append(payload)
                else:
                    self.error = payload.decode("utf-8", "replace") if kind == b"E" else "The Bluetooth helper stopped"
                self.cond.notify_all()
            if kind != b"I":
                return

    def write(self, data):
        data = bytes(data)
        if self.proc is None or self.error:
            return -1
        try:
            with self.write_lock:
                self.proc.stdin.write(struct.pack("<cH", b"W", len(data)) + data)
        except (OSError, ValueError):
            return -1
        return len(data)

    def read(self, max_length, timeout_ms=0):
        """One input report as a list of ints: [] on timeout. Raises IOError when the
        controller or the helper is gone."""
        with self.cond:
            if not self.reports and not self.error and timeout_ms > 0:
                self.cond.wait_for(lambda: self.reports or self.error, timeout_ms / 1000.0)
            if self.reports:
                return list(self.reports.popleft()[:max_length])
            if self.error:
                raise IOError(self.error)
            return []

    def close(self):
        proc, self.proc = self.proc, None
        if proc is None:
            return
        try:
            proc.stdin.write(b"Q\x00\x00")
            proc.stdin.close()
        except (OSError, ValueError):
            pass
        try:
            proc.wait(3)
        except subprocess.TimeoutExpired:
            proc.kill()
