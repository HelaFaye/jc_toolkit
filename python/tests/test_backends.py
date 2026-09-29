"""HID backends: hidapi module choice, the USB handshake, and Android's USB host backend
(with the Android Java classes and the usbdevfs ioctl emulated)."""
import ctypes
import sys
import types

import pytest

from jctool import android_usb, hidio


# --- hidapi module choice ------------------------------------------------------------------
@pytest.mark.skipif(not sys.platform.startswith("linux"), reason="Linux only")
def test_linux_uses_hidraw_for_bluetooth(monkeypatch):
    monkeypatch.delenv("JCTOOL_HID_BACKEND", raising=False)
    fake_hidraw = types.ModuleType("hidraw")
    fake_hid = types.ModuleType("hid")
    monkeypatch.setitem(sys.modules, "hidraw", fake_hidraw)
    monkeypatch.setitem(sys.modules, "hid", fake_hid)
    assert hidio.hid_module() is fake_hidraw
    monkeypatch.setenv("JCTOOL_HID_BACKEND", "libusb")
    assert hidio.hid_module() is fake_hid


def test_android_uses_usb_host(monkeypatch):
    monkeypatch.setenv("ANDROID_ARGUMENT", "")
    assert hidio.hid_module() is android_usb


def fake_backend(entries):
    m = types.ModuleType("fakehid")
    m.enumerate = lambda vid=0, pid=0: entries
    return m


def test_usb_controllers_are_marked(monkeypatch):
    entries = [
        {"path": b"/dev/hidraw1", "vendor_id": 0x057E, "product_id": 0x2009, "bus_type": 1, "interface_number": 0},
        {"path": b"/dev/hidraw2", "vendor_id": 0x057E, "product_id": 0x2007, "bus_type": 2, "interface_number": -1},
        {"path": b"old-usb", "vendor_id": 0x057E, "product_id": 0x2006, "interface_number": 0},
    ]
    monkeypatch.setattr(hidio, "hid_module", lambda: fake_backend(entries))
    found = hidio.enumerate_controllers()
    assert [(f.type, f.usb) for f in found] == [(hidio.PROCON, True), (hidio.JOYCON_R, False), (hidio.JOYCON_L, True)]
    assert found[0].name == "Pro Controller (USB)" and found[1].name == "Joy-Con (R)"


class UsbProCon:
    """Answers the USB handshake like a Pro Controller does."""

    def __init__(self):
        self.written = []
        self.pending = []

    def write(self, data):
        self.written.append(bytes(data))
        if data[0] == 0x80 and data[1] in (0x01, 0x02, 0x03):
            self.pending.append([0x81, data[1]] + [0] * 62)
        return len(data)

    def read(self, length, timeout):
        return self.pending.pop(0) if self.pending else []


def test_usb_handshake():
    raw = UsbProCon()
    dev = hidio.Device(raw, hidio.PROCON)
    assert dev.usb_handshake()
    assert [w[:2] for w in raw.written] == [b"\x80\x02", b"\x80\x03", b"\x80\x02", b"\x80\x04"]
    assert all(len(w) == 49 for w in raw.written)


# --- Android USB host ----------------------------------------------------------------------
class Obj:
    def __init__(self, **kw):
        self.__dict__.update(kw)


def java_list(items):
    return Obj(values=lambda: Obj(toArray=lambda: list(items)))


class FakeEndpoint:
    def __init__(self, address, direction):
        self.address, self.direction = address, direction

    def getType(self):
        return android_usb.USB_ENDPOINT_XFER_INT

    def getDirection(self):
        return self.direction

    def getAddress(self):
        return self.address

    def getMaxPacketSize(self):
        return 64


class FakeInterface:
    def __init__(self, cls, endpoints=()):
        self.cls, self.endpoints = cls, list(endpoints)

    def getInterfaceClass(self):
        return self.cls

    def getId(self):
        return 0

    def getEndpointCount(self):
        return len(self.endpoints)

    def getEndpoint(self, i):
        return self.endpoints[i]


class FakeUsbDevice:
    def __init__(self, name, vid, pid, interfaces):
        self.name, self.vid, self.pid, self.interfaces = name, vid, pid, interfaces

    def getDeviceName(self):
        return self.name

    def getVendorId(self):
        return self.vid

    def getProductId(self):
        return self.pid

    def getProductName(self):
        return "Pro Controller"

    def getManufacturerName(self):
        return "Nintendo Co., Ltd."

    def getSerialNumber(self):
        raise Exception("SecurityException: no permission")

    def getInterfaceCount(self):
        return len(self.interfaces)

    def getInterface(self, i):
        return self.interfaces[i]


class FakeConnection:
    def __init__(self):
        self.claimed = self.closed = False

    def claimInterface(self, iface, force):
        self.claimed = force
        return True

    def releaseInterface(self, iface):
        self.claimed = False

    def getFileDescriptor(self):
        return 42

    def close(self):
        self.closed = True


class FakeManager:
    def __init__(self, devices, allowed):
        self.devices, self.allowed = devices, allowed
        self.requested = []
        self.conn = FakeConnection()

    def getDeviceList(self):
        return java_list(self.devices)

    def hasPermission(self, dev):
        return self.allowed

    def requestPermission(self, dev, pending):
        self.requested.append((dev, pending))

    def openDevice(self, dev):
        return self.conn


class FakeIntent:
    def __init__(self, action):
        self.action = action
        self.package = None

    def setPackage(self, p):
        self.package = p


@pytest.fixture
def android(monkeypatch):
    procon = FakeUsbDevice("/dev/bus/usb/001/002", 0x057E, 0x2009, [
        FakeInterface(android_usb.USB_CLASS_HID, [FakeEndpoint(0x81, 0x80), FakeEndpoint(0x01, 0x00)])])
    storage = FakeUsbDevice("/dev/bus/usb/001/003", 0x0781, 0x5581, [FakeInterface(8)])
    manager = FakeManager([storage, procon], allowed=True)
    j = Obj(cast=lambda cls, o: o, manager=manager, activity=Obj(getPackageName=lambda: "org.jctool.jctool"),
            Intent=FakeIntent, sdk=34,
            PendingIntent=Obj(getBroadcast=lambda act, code, intent, flags: ("pending", intent, flags)))
    monkeypatch.setattr(android_usb, "_java", j)
    # The controller behind usbdevfs: replies to what was written, times out otherwise
    usb = Obj(out=[], replies=[])

    def ioctl(fd, request, t):
        assert fd == 42 and request == android_usb.USBDEVFS_BULK
        if t.ep == 0x01:
            data = ctypes.string_at(t.data, t.len)
            usb.out.append(data)
            if data[0] == 0x80 and data[1] in (0x01, 0x02, 0x03):     # 0x04 gets no reply
                usb.replies.append(bytes([0x81, data[1]]) + bytes(62))
            return t.len
        if not usb.replies:
            return -android_usb.ETIMEDOUT
        r = usb.replies.pop(0)
        ctypes.memmove(t.data, r, len(r))
        return len(r)
    monkeypatch.setattr(android_usb, "_ioctl", ioctl)
    monkeypatch.setenv("ANDROID_ARGUMENT", "")
    return manager, usb


def test_android_enumerate_lists_hid_devices_only(android):
    devices = android_usb.enumerate(0, 0)
    assert len(devices) == 1
    d = devices[0]
    assert (d["vendor_id"], d["product_id"], d["bus_type"], d["serial_number"]) == (0x057E, 0x2009, 1, "")
    found = hidio.enumerate_controllers()
    assert len(found) == 1 and found[0].usb and found[0].type == hidio.PROCON


def test_android_open_handshakes_and_transfers(android):
    manager, usb = android
    found = hidio.enumerate_controllers()[0]
    dev = hidio.Device.open(found)
    assert manager.conn.claimed
    assert [o[:2] for o in usb.out] == [b"\x80\x02", b"\x80\x03", b"\x80\x02", b"\x80\x04"]
    assert dev.read(49, 10) == (0, bytearray(49))                    # Timeout
    usb.replies.append(bytes([0x21, 0x05]) + bytes(62))
    n, buf = dev.read(49, 10)
    assert n == 49 and buf[:2] == b"\x21\x05"
    assert dev.write(b"\x01\x00") == 49 and usb.out[-1] == b"\x01\x00" + bytes(47)
    dev.close()
    assert manager.conn.closed and not manager.conn.claimed


def test_android_asks_for_permission(android):
    manager, usb = android
    manager.allowed = False
    found = hidio.enumerate_controllers()[0]
    with pytest.raises(android_usb.PermissionNeeded):
        hidio.Device.open(found)
    (dev, pending), = manager.requested
    assert pending[1].action == android_usb.ACTION_USB_PERMISSION and pending[1].package == "org.jctool.jctool"
    assert pending[2] == 0x02000000                                  # FLAG_MUTABLE on Android 12+


def test_usbdevfs_ioctl_number():
    # _IOWR('U', 2, struct usbdevfs_bulktransfer): 24 bytes with 64-bit pointers, 16 with 32-bit
    size = 24 if ctypes.sizeof(ctypes.c_void_p) == 8 else 16
    assert android_usb.USBDEVFS_BULK == 0xC0005502 | (size << 16)
