"""A hidapi-like HID backend for macOS through ctypes (IOKit's IOHIDManager and
IOHIDDevice), for Macs where the hidapi package can't be installed: PowerPC Macs on Mac OS
X 10.5 Leopard (IOHIDManager came with 10.5), running the command line from source.

The interface is the part of hidapi's that jctool.hidio uses: enumerate(vid, pid) returns
dicts (path, vendor_id, product_id, serial_number, product_string, manufacturer_string,
usage_page, usage, interface_number, bus_type), and device() has open_path(path),
write(data), read(size, timeout_ms) and close(). Like hidapi on macOS, an input report keeps
its report ID as its first byte, and write() sends data[0] as the report ID.
"""
import ctypes
import ctypes.util
import queue
import threading

DESCRIPTION = "IOKit (ctypes)"

_lib = {}

kCFStringEncodingUTF8 = 0x08000100
kCFNumberSInt32Type = 3
kIOHIDReportTypeOutput = 1
kIOHIDOptionsTypeNone = 0
kIOServicePlane = b"IOService"
MAX_REPORT = 128

REPORT_CALLBACK = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_int,
                                   ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint8), ctypes.c_long)


def _framework(name):
    path = ctypes.util.find_library(name) or "/System/Library/Frameworks/%s.framework/%s" % (name, name)
    return ctypes.CDLL(path)


def _libs():
    """(IOKit, CoreFoundation), with the signatures this module uses."""
    if _lib:
        return _lib["iokit"], _lib["cf"]
    iokit, cf = _framework("IOKit"), _framework("CoreFoundation")
    vp, i32, cl = ctypes.c_void_p, ctypes.c_int, ctypes.c_long

    def sig(lib, name, restype, *argtypes):
        f = getattr(lib, name)
        f.restype, f.argtypes = restype, list(argtypes)
    sig(cf, "CFStringCreateWithCString", vp, vp, ctypes.c_char_p, ctypes.c_uint32)
    sig(cf, "CFStringGetCString", ctypes.c_bool, vp, ctypes.c_char_p, cl, ctypes.c_uint32)
    sig(cf, "CFGetTypeID", ctypes.c_ulong, vp)
    sig(cf, "CFStringGetTypeID", ctypes.c_ulong)
    sig(cf, "CFNumberGetTypeID", ctypes.c_ulong)
    sig(cf, "CFNumberGetValue", ctypes.c_bool, vp, i32, vp)
    sig(cf, "CFSetGetCount", cl, vp)
    sig(cf, "CFSetGetValues", None, vp, ctypes.POINTER(vp))
    sig(cf, "CFRelease", None, vp)
    sig(cf, "CFRetain", vp, vp)
    sig(cf, "CFRunLoopGetCurrent", vp)
    sig(cf, "CFRunLoopRun", None)
    sig(cf, "CFRunLoopStop", None, vp)
    sig(cf, "CFRunLoopWakeUp", None, vp)
    sig(iokit, "IOHIDManagerCreate", vp, vp, ctypes.c_uint32)
    sig(iokit, "IOHIDManagerSetDeviceMatching", None, vp, vp)
    sig(iokit, "IOHIDManagerOpen", i32, vp, ctypes.c_uint32)
    sig(iokit, "IOHIDManagerCopyDevices", vp, vp)
    sig(iokit, "IOHIDDeviceGetProperty", vp, vp, vp)
    sig(iokit, "IOHIDDeviceGetService", ctypes.c_uint32, vp)
    sig(iokit, "IOHIDDeviceOpen", i32, vp, ctypes.c_uint32)
    sig(iokit, "IOHIDDeviceClose", i32, vp, ctypes.c_uint32)
    sig(iokit, "IOHIDDeviceSetReport", i32, vp, i32, cl, ctypes.c_char_p, cl)
    sig(iokit, "IOHIDDeviceRegisterInputReportCallback", None, vp, ctypes.c_void_p, cl, REPORT_CALLBACK, vp)
    sig(iokit, "IOHIDDeviceScheduleWithRunLoop", None, vp, vp, vp)
    sig(iokit, "IOHIDDeviceUnscheduleFromRunLoop", None, vp, vp, vp)
    sig(iokit, "IORegistryEntryGetPath", i32, ctypes.c_uint32, ctypes.c_char_p, ctypes.c_char_p)
    _lib.update(iokit=iokit, cf=cf, mode=ctypes.c_void_p.in_dll(cf, "kCFRunLoopDefaultMode"))
    return iokit, cf


def _cfstr(text):
    iokit, cf = _libs()
    return cf.CFStringCreateWithCString(None, text.encode(), kCFStringEncodingUTF8)


def _prop(dev, key):
    """A device property: int, str, or None."""
    iokit, cf = _libs()
    name = _cfstr(key)
    try:
        ref = iokit.IOHIDDeviceGetProperty(dev, name)
    finally:
        cf.CFRelease(name)
    if not ref:
        return None
    kind = cf.CFGetTypeID(ref)
    if kind == cf.CFNumberGetTypeID():
        v = ctypes.c_int32()
        return v.value if cf.CFNumberGetValue(ref, kCFNumberSInt32Type, ctypes.byref(v)) else None
    if kind == cf.CFStringGetTypeID():
        buf = ctypes.create_string_buffer(256)
        return buf.value.decode("utf-8", "replace") if cf.CFStringGetCString(ref, buf, 256, kCFStringEncodingUTF8) else None
    return None


def _path(dev):
    """The device's IORegistry path: what open_path() finds it by."""
    iokit, cf = _libs()
    buf = ctypes.create_string_buffer(512)
    if iokit.IORegistryEntryGetPath(iokit.IOHIDDeviceGetService(dev), kIOServicePlane, buf) != 0:
        return None
    return buf.value


_manager = []


def _devices():
    """The HID devices now: [IOHIDDeviceRef] (the manager keeps them alive)."""
    iokit, cf = _libs()
    if not _manager:
        mgr = iokit.IOHIDManagerCreate(None, kIOHIDOptionsTypeNone)
        iokit.IOHIDManagerSetDeviceMatching(mgr, None)            # All HID devices
        iokit.IOHIDManagerOpen(mgr, kIOHIDOptionsTypeNone)
        _manager.append(mgr)
    found = iokit.IOHIDManagerCopyDevices(_manager[0])
    if not found:
        return []
    try:
        n = cf.CFSetGetCount(found)
        refs = (ctypes.c_void_p * n)()
        cf.CFSetGetValues(found, refs)
        return [cf.CFRetain(r) for r in refs if r]
    finally:
        cf.CFRelease(found)


def enumerate(vendor_id=0, product_id=0):
    out = []
    iokit, cf = _libs()
    for dev in _devices():
        try:
            vid, pid = _prop(dev, "VendorID") or 0, _prop(dev, "ProductID") or 0
            if (vendor_id and vid != vendor_id) or (product_id and pid != product_id):
                continue
            path = _path(dev)
            if path is None:
                continue
            transport = (_prop(dev, "Transport") or "").lower()
            out.append({
                "path": path, "vendor_id": vid, "product_id": pid,
                "serial_number": _prop(dev, "SerialNumber") or "",
                "product_string": _prop(dev, "Product") or "",
                "manufacturer_string": _prop(dev, "Manufacturer") or "",
                "release_number": _prop(dev, "VersionNumber") or 0,
                "usage_page": _prop(dev, "PrimaryUsagePage") or 0, "usage": _prop(dev, "PrimaryUsage") or 0,
                "interface_number": 0 if transport == "usb" else -1,
                "bus_type": 1 if transport == "usb" else 2 if transport == "bluetooth" else 0,
            })
        finally:
            cf.CFRelease(dev)
    return out


class device:
    """One open HID device. Input reports arrive on a run loop thread and wait in a queue."""

    def __init__(self):
        self.dev = None
        self.reports = queue.Queue()
        self.loop = None
        self._buf = (ctypes.c_uint8 * MAX_REPORT)()
        self._callback = REPORT_CALLBACK(self._on_report)         # Kept: IOKit holds a pointer
        self._thread = None

    def _on_report(self, context, result, sender, report_type, report_id, report, length):
        self.reports.put(bytes(bytearray(report[:length])))

    def open_path(self, path):
        iokit, cf = _libs()
        path = path if isinstance(path, bytes) else path.encode()
        for dev in _devices():
            if self.dev is None and _path(dev) == path:
                self.dev = dev
            else:
                cf.CFRelease(dev)
        if self.dev is None:
            raise OSError("open failed: no such device")
        if iokit.IOHIDDeviceOpen(self.dev, kIOHIDOptionsTypeNone) != 0:
            cf.CFRelease(self.dev)
            self.dev = None
            raise OSError("open failed")
        iokit.IOHIDDeviceRegisterInputReportCallback(self.dev, ctypes.cast(self._buf, ctypes.c_void_p), MAX_REPORT,
                                                     self._callback, None)
        ready = threading.Event()

        def run():
            self.loop = cf.CFRunLoopGetCurrent()
            iokit.IOHIDDeviceScheduleWithRunLoop(self.dev, self.loop, _lib["mode"])
            ready.set()
            cf.CFRunLoopRun()
            iokit.IOHIDDeviceUnscheduleFromRunLoop(self.dev, self.loop, _lib["mode"])
        self._thread = threading.Thread(target=run, daemon=True)
        self._thread.start()
        ready.wait(5)

    def write(self, data):
        iokit, cf = _libs()
        data = bytes(bytearray(data))
        if not data:
            return -1
        # hidapi: a report ID of 0 isn't sent; another is the first byte of the report
        report_id, payload = data[0], data if data[0] else data[1:]
        res = iokit.IOHIDDeviceSetReport(self.dev, kIOHIDReportTypeOutput, report_id, payload, len(payload))
        return len(data) if res == 0 else -1

    def read(self, size, timeout_ms=-1):
        try:
            if timeout_ms is None or timeout_ms < 0:
                report = self.reports.get()
            elif timeout_ms == 0:
                report = self.reports.get_nowait()
            else:
                report = self.reports.get(timeout=timeout_ms / 1000.0)
        except queue.Empty:
            return []
        return list(bytearray(report[:size]))

    def close(self):
        if self.dev is None:
            return
        iokit, cf = _libs()
        if self.loop:
            cf.CFRunLoopStop(self.loop)
            cf.CFRunLoopWakeUp(self.loop)
            if self._thread:
                self._thread.join(2)
        iokit.IOHIDDeviceClose(self.dev, kIOHIDOptionsTypeNone)
        cf.CFRelease(self.dev)
        self.dev = None
