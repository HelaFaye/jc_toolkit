"""Android's HID backend: Bluetooth controllers through the root hidraw bridge
(jctool.hidraw_bridge), and USB ones through the USB host API (jctool.android_usb). Same
interface as hidapi: enumerate(vid, pid) and device()."""
from . import android_usb, hidraw_bridge

DESCRIPTION = "android (Bluetooth: root hidraw bridge, USB: USB host)"


def enumerate(vendor_id=0, product_id=0):
    found = []
    try:
        found += hidraw_bridge.enumerate(vendor_id, product_id)
    except Exception as e:                    # No root: USB still works
        hidraw_bridge.last_error = str(e)
    # A USB controller that the kernel also exposes through hidraw is listed once, by USB
    usb = android_usb.enumerate(vendor_id, product_id)
    if usb:
        found = [d for d in found if d.get("bus_type") != 1]
    return found + usb


class device:
    def __init__(self):
        self.impl = None

    def open_path(self, path):
        text = path.decode() if isinstance(path, bytes) else path
        self.impl = hidraw_bridge.device() if text.startswith(hidraw_bridge.PREFIX) else android_usb.device()
        self.impl.open_path(path)

    def write(self, data):
        return self.impl.write(data)

    def read(self, max_length, timeout_ms=0):
        return self.impl.read(max_length, timeout_ms)

    def close(self):
        if self.impl is not None:
            self.impl.close()
