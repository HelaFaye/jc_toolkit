"""USB HID on Android (USB host / OTG), with the part of the hidapi interface that
jctool.hidio uses: enumerate(vid, pid) and device() with open_path, write, read, close.

Android apps can't open Bluetooth HID devices, but they can open USB ones: a Pro Controller
on a USB cable (or a Joy-Con's Charging Grip) through a USB OTG adapter. Android's Java API
finds the device, asks the user for permission and opens it; the reports then go through
the kernel's usbdevfs ioctls on the file descriptor Android gives us (what libusb does on
Android), so no Java call happens per report.
"""
import ctypes
import os
import threading

ACTION_USB_PERMISSION = "org.jctool.USB_PERMISSION"
USB_CLASS_HID = 3
USB_DIR_IN = 0x80
USB_ENDPOINT_XFER_INT = 3
ETIMEDOUT = 110
USB_BUS = 1                     # hidapi's bus_type for USB

_java = None


class _Java:
    """The Android classes, loaded once (pyjnius is only available on Android)."""

    def __init__(self):
        from jnius import autoclass, cast
        self.cast = cast
        self.activity = autoclass("org.kivy.android.PythonActivity").mActivity
        context = autoclass("android.content.Context")
        self.manager = cast("android.hardware.usb.UsbManager", self.activity.getSystemService(context.USB_SERVICE))
        self.Intent = autoclass("android.content.Intent")
        self.PendingIntent = autoclass("android.app.PendingIntent")
        self.sdk = autoclass("android.os.Build$VERSION").SDK_INT


def java():
    global _java
    if _java is None:
        _java = _Java()
        _detach_threads_on_exit()
    return _java


def _detach_threads_on_exit():
    """Android aborts the app when a thread that called Java ends without detaching from the
    VM. pyjnius attaches threads on their first Java call, so detach every thread when its
    run() ends."""
    try:
        import jnius
    except ImportError:
        return
    if getattr(threading.Thread, "_jctool_detaches", False):
        return
    original = threading.Thread.run

    def run(self, *args, **kwargs):
        try:
            original(self, *args, **kwargs)
        finally:
            try:
                jnius.detach()
            except Exception:
                pass
    threading.Thread.run = run
    threading.Thread._jctool_detaches = True


def _devices():
    j = java()
    devices = j.manager.getDeviceList()
    return [j.cast("android.hardware.usb.UsbDevice", d) for d in devices.values().toArray()]


def _hid_interface(dev):
    for i in range(dev.getInterfaceCount()):
        iface = dev.getInterface(i)
        if iface.getInterfaceClass() == USB_CLASS_HID:
            return iface
    return None


def _text(fn):
    try:
        return fn() or ""
    except Exception:            # Serial numbers need the permission first
        return ""


def enumerate(vendor_id=0, product_id=0):
    """The USB HID devices, as hidapi-style dicts (path: the Android device name)."""
    out = []
    for dev in _devices():
        vid, pid = dev.getVendorId(), dev.getProductId()
        if (vendor_id and vid != vendor_id) or (product_id and pid != product_id):
            continue
        iface = _hid_interface(dev)
        if iface is None:
            continue
        out.append({
            "path": dev.getDeviceName(), "vendor_id": vid, "product_id": pid,
            "product_string": _text(dev.getProductName), "manufacturer_string": _text(dev.getManufacturerName),
            "serial_number": _text(dev.getSerialNumber), "release_number": 0,
            "interface_number": iface.getId(), "usage_page": 0, "usage": 0, "bus_type": USB_BUS,
        })
    return out


def request_permission(dev):
    """Shows Android's "Allow access to the USB device" dialog."""
    j = java()
    intent = j.Intent(ACTION_USB_PERMISSION)
    intent.setPackage(j.activity.getPackageName())
    flags = 0x02000000 if j.sdk >= 31 else 0          # FLAG_MUTABLE: Android fills in the extras
    pending = j.PendingIntent.getBroadcast(j.activity, 0, intent, flags)
    j.manager.requestPermission(dev, pending)


class PermissionNeeded(IOError):
    pass


# --- usbdevfs (linux/usbdevice_fs.h)
class _BulkTransfer(ctypes.Structure):
    _fields_ = [("ep", ctypes.c_uint), ("len", ctypes.c_uint), ("timeout", ctypes.c_uint),
                ("data", ctypes.c_void_p)]


def _iowr(kind, nr, size):
    return (3 << 30) | (size << 16) | (ord(kind) << 8) | nr


USBDEVFS_BULK = _iowr("U", 2, ctypes.sizeof(_BulkTransfer))
_libc = None


def _ioctl(fd, request, arg):
    """ioctl(2): returns its result, or -errno."""
    global _libc
    if _libc is None:
        _libc = ctypes.CDLL(None, use_errno=True)
        _libc.ioctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_void_p]
    res = _libc.ioctl(fd, request, ctypes.addressof(arg))
    return res if res >= 0 else -ctypes.get_errno()


class device:
    """An open USB HID device (hidapi's hid.device())."""

    def __init__(self):
        self.conn = self.iface = None
        self.fd = -1
        self.ep_in = self.ep_out = None
        self.in_size = 64
        self.iface_id = 0

    def open_path(self, path):
        if isinstance(path, bytes):
            path = path.decode()
        j = java()
        dev = next((d for d in _devices() if d.getDeviceName() == path), None)
        if dev is None:
            raise IOError("The USB device is gone: %s" % path)
        if not j.manager.hasPermission(dev):
            request_permission(dev)
            raise PermissionNeeded("Allow the app to access the controller in Android's dialog, "
                                   "then press Connect again.")
        self.iface = _hid_interface(dev)
        self.conn = j.manager.openDevice(dev)
        if self.conn is None:
            raise IOError("Android could not open the USB device")
        if not self.conn.claimInterface(self.iface, True):
            self.conn.close()
            raise IOError("Could not claim the controller's USB interface")
        self.iface_id = self.iface.getId()
        for i in range(self.iface.getEndpointCount()):
            ep = self.iface.getEndpoint(i)
            if ep.getType() != USB_ENDPOINT_XFER_INT:
                continue
            if ep.getDirection() == USB_DIR_IN:
                self.ep_in, self.in_size = ep.getAddress(), ep.getMaxPacketSize()
            else:
                self.ep_out = ep.getAddress()
        if self.ep_in is None:
            self.close()
            raise IOError("The controller has no interrupt IN endpoint")
        self.fd = self.conn.getFileDescriptor()

    def _transfer(self, ep, buf, timeout_ms):
        t = _BulkTransfer(ep, len(buf), max(1, int(timeout_ms)), ctypes.addressof(buf))
        return _ioctl(self.fd, USBDEVFS_BULK, t)

    def write(self, data):
        data = bytes(data)
        if self.fd < 0:
            return -1
        if self.ep_out is None:
            # No OUT endpoint: SET_REPORT (output report, its ID in the first byte)
            n = self.conn.controlTransfer(0x21, 0x09, 0x0200 | data[0], self.iface_id, data, len(data), 1000)
            return n
        buf = ctypes.create_string_buffer(data, len(data))
        n = self._transfer(self.ep_out, buf, 1000)
        return n if n >= 0 else -1

    def read(self, max_length, timeout_ms=0):
        """One input report as a list of ints: [] on timeout. Raises IOError on errors."""
        if self.fd < 0:
            raise IOError("not open")
        buf = ctypes.create_string_buffer(max(self.in_size, 64))
        n = self._transfer(self.ep_in, buf, timeout_ms if timeout_ms > 0 else 1)
        if n == -ETIMEDOUT:
            return []
        if n < 0:
            raise IOError(os.strerror(-n))
        return list(buf.raw[:min(n, max_length)])

    def close(self):
        if self.conn is not None:
            try:
                self.conn.releaseInterface(self.iface)
            except Exception:
                pass
            self.conn.close()
        self.conn = None
        self.fd = -1
