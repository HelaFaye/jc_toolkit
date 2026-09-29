"""Controller access: finds Joy-Cons / Pro Controllers and wraps a hidapi device the way
CTCaer's Windows code expected it to behave, on every platform.

- Writes are padded to the 49 byte output report (Windows hidapi did that).
- A read of length 0 waits for one report and drops it (the Windows behaviour the protocol
  code relies on).
- Calls are serialized with a lock: hidapi is not thread safe for one device.
- Link health counters and the traffic log (traffic_log.txt, like the original's -d).

Uses the `hidapi` package (cython-hidapi), which has wheels for Windows, macOS and Linux.
"""
import os
import threading
import time

VENDOR_NINTENDO = 0x057E
PRODUCT_JOYCON_L = 0x2006
PRODUCT_JOYCON_R = 0x2007
PRODUCT_PRO = 0x2009

NOTHING, JOYCON_L, JOYCON_R, PROCON = 0, 1, 2, 3
TYPE_NAMES = {NOTHING: "None", JOYCON_L: "Joy-Con (L)", JOYCON_R: "Joy-Con (R)", PROCON: "Pro Controller"}
OUTPUT_REPORT_LENGTH = 49


def hid_module():
    """The hidapi module, or None when it isn't installed."""
    try:
        import hid
        return hid
    except ImportError:
        return None


class Found:
    """A controller seen by enumerate_controllers()."""

    def __init__(self, path, type_, name, serial="", third_party=False):
        self.path = path
        self.type = type_
        self.name = name
        self.serial = serial
        self.third_party = third_party

    def __repr__(self):
        return "Found(%r, %s)" % (self.name, self.path)


def enumerate_controllers():
    """Nintendo controllers, and third-party ones that report as a "Wireless Gamepad" (the
    original's pseudo third-party support: used as a Pro Controller)."""
    hid = hid_module()
    if hid is None:
        return []
    found = []
    for d in hid.enumerate(0, 0):
        vid, pid = d.get("vendor_id"), d.get("product_id")
        product = d.get("product_string") or ""
        maker = d.get("manufacturer_string") or ""
        if vid == VENDOR_NINTENDO and pid == PRODUCT_JOYCON_L:
            t, third = JOYCON_L, False
        elif vid == VENDOR_NINTENDO and pid == PRODUCT_JOYCON_R:
            t, third = JOYCON_R, False
        elif vid == VENDOR_NINTENDO and pid == PRODUCT_PRO:
            t, third = PROCON, False
        elif product == "Wireless Gamepad" and maker in ("Nintendo", "") and d.get("usage") == 0x0005:
            t, third = PROCON, True
        else:
            continue
        path = d.get("path")
        if isinstance(path, bytes):
            path = path.decode("utf-8", "replace")
        name = 'Third-party "Wireless Gamepad" (as Pro Controller)' if third else TYPE_NAMES[t]
        found.append(Found(path, t, name, d.get("serial_number") or "", third))
    # One entry per path (some platforms list a device once per usage)
    seen, unique = set(), []
    for f in found:
        if f.path not in seen:
            seen.add(f.path)
            unique.append(f)
    return unique


def list_hid_devices():
    """Every HID device, as text (the original's HID listing)."""
    hid = hid_module()
    if hid is None:
        return "The hidapi Python package is not installed."
    lines = []
    for d in hid.enumerate(0, 0):
        path = d.get("path")
        if isinstance(path, bytes):
            path = path.decode("utf-8", "replace")
        lines.append('HID Device: 0x%04x "%s"' % (d.get("product_id", 0), d.get("product_string") or "Unknown Product"))
        lines.append('\tvendor = 0x%04x "%s"' % (d.get("vendor_id", 0), d.get("manufacturer_string") or "Unknown Manufacturer"))
        lines.append("\trelease = %d" % d.get("release_number", 0))
        lines.append("\tserial = %s" % (d.get("serial_number") or "Unknown Serial Number"))
        lines.append("\tusage = 0x%04x page: 0x%x" % (d.get("usage", 0), d.get("usage_page", 0)))
        lines.append("%s\n" % path)
    return "\n".join(lines)


class LinkStats:
    """Link health over one interval: reports received, reads that timed out, failed calls,
    and the longest wait for a report while the app kept reading (idle time doesn't count)."""

    def __init__(self):
        self.reports = self.timeouts = self.errors = self.writes = self.write_errors = 0
        self.longest_gap_ms = 0
        self.interval_ms = 0


class Device:
    """One open controller. `raw` is a hidapi device, or an emulated one (jctool.fake) with
    the same write(data) / read(length, timeout_ms) interface."""

    def __init__(self, raw, type_, path=""):
        self.raw = raw
        self.type = type_
        self.path = path
        self.lock = threading.RLock()
        self.closed = False
        self.traffic_log = None          # An open file: log every report (traffic_log.txt)
        self.traffic_timestamps = os.environ.get("JCTOOL_TIMESTAMPS") == "1"
        self._clock0 = time.monotonic()
        self._stats = LinkStats()
        self._stats_lock = threading.Lock()
        self._interval_start = self._now_ms()
        self._last_report_ms = 0
        self._last_read_end_ms = -1000

    @classmethod
    def open(cls, found):
        hid = hid_module()
        if hid is None:
            raise RuntimeError("The hidapi Python package is not installed (pip install hidapi)")
        raw = hid.device()
        path = found.path.encode() if isinstance(found.path, str) else found.path
        raw.open_path(path)
        return cls(raw, found.type, found.path)

    def _now_ms(self):
        return (time.monotonic() - self._clock0) * 1000.0

    # --- Traffic log
    def enable_traffic_log(self, path="traffic_log.txt"):
        self.traffic_log = open(path, "a")

    def _log(self, prefix, data, zero_length=False):
        if self.traffic_log is None:
            return
        line = ""
        if self.traffic_timestamps:
            line += "[%10.1f] " % self._now_ms()
        line += prefix + "".join("%02x " % b for b in data)
        if zero_length:
            line += "Requested hid read length was 0 bytes."
        self.traffic_log.write(line + "\n\n")
        self.traffic_log.flush()

    def note(self, what):
        """A NOTE line in the traffic log."""
        if self.traffic_log is not None:
            self.traffic_log.write("[%10.1f] NOTE %s\n\n" % (self._now_ms(), what))
            self.traffic_log.flush()

    # --- I/O
    def write(self, data, length=None):
        """Writes a report, padded to 49 bytes. Returns the byte count, or -1."""
        data = bytes(data if length is None else data[:length])
        if len(data) < OUTPUT_REPORT_LENGTH:
            data = data + bytes(OUTPUT_REPORT_LENGTH - len(data))
        if self.closed:
            return -1
        self._log("W: ", data)
        with self.lock:
            try:
                res = self.raw.write(data)
            except (IOError, OSError, ValueError):
                res = -1
        with self._stats_lock:
            self._stats.writes += 1
            if res < 0:
                self._stats.write_errors += 1
        return res

    def read(self, length, timeout_ms):
        """Reads one report: (count, buffer). The buffer is `length` bytes (zero padded), or
        0x170 for a 0-length read, which drops the report like Windows did. count: bytes
        read, 0 on timeout, -1 on error."""
        want = length if length > 0 else 0x170
        start = self._now_ms()
        if self.closed:
            return -1, bytearray(want)
        with self.lock:
            try:
                got = self.raw.read(want, timeout_ms)
            except (IOError, OSError, ValueError):
                got = None
        n = -1 if got is None else len(got)
        buf = bytearray(want)
        if n > 0:
            buf[:n] = bytes(got[:want])
        self._count_read(n, start)
        if length == 0:
            if n > 0:
                self._log("R: ", b"", zero_length=True)
            return (0 if n >= 0 else -1), buf
        if n > 0:
            self._log("R: ", buf[:n])
        elif n == 0 and self.traffic_timestamps:
            self._log("R: timeout after %dms " % timeout_ms, b"")
        return n, buf

    def close(self):
        if self.closed:
            return
        self.closed = True
        with self.lock:
            try:
                self.raw.close()
            except Exception:
                pass
        if self.traffic_log is not None:
            self.traffic_log.close()
            self.traffic_log = None

    # --- Link health
    def _count_read(self, n, start):
        now = self._now_ms()
        with self._stats_lock:
            if start - self._last_read_end_ms > 50:
                self._last_report_ms = start       # The app wasn't reading before this call
            if n > 0:
                self._stats.reports += 1
                self._stats.longest_gap_ms = max(self._stats.longest_gap_ms, int(now - self._last_report_ms))
                self._last_report_ms = now
            elif n == 0:
                self._stats.timeouts += 1
            else:
                self._stats.errors += 1
            self._last_read_end_ms = now

    def take_link_stats(self):
        """The counters since the last call (and starts a new interval)."""
        with self._stats_lock:
            now = self._now_ms()
            s = self._stats
            s.interval_ms = int(now - self._interval_start)
            if now - self._last_read_end_ms <= 50:   # A report still awaited counts too
                s.longest_gap_ms = max(s.longest_gap_ms, int(now - self._last_report_ms))
            self._stats = LinkStats()
            self._interval_start = now
            return s
