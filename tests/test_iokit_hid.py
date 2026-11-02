"""The ctypes IOKit HID backend (jctool/iokit_hid.py, for Macs without the hidapi package:
PowerPC Macs running the command line from source). On macOS: the frameworks load, the
devices enumerate, a missing device is refused."""
import sys

import pytest

pytestmark = pytest.mark.skipif(sys.platform != "darwin", reason="macOS only")


def test_enumerate_and_open_refused():
    from jctool import iokit_hid
    found = iokit_hid.enumerate(0, 0)
    assert isinstance(found, list)
    for d in found:
        assert {"path", "vendor_id", "product_id", "serial_number", "product_string", "bus_type"} <= set(d)
        assert isinstance(d["path"], bytes)
    assert iokit_hid.enumerate(0x057E, 0xFFFF) == [] or all(d["product_id"] == 0xFFFF for d in iokit_hid.enumerate(0x057E, 0xFFFF))
    dev = iokit_hid.device()
    with pytest.raises(OSError):
        dev.open_path(b"IOService:/no/such/device")
    dev.close()


def test_hidio_uses_it_when_asked(monkeypatch):
    from jctool import hidio, iokit_hid
    monkeypatch.setenv("JCTOOL_HID_BACKEND", "iokit")
    assert hidio.hid_module() is iokit_hid
    assert isinstance(hidio.list_hid_devices(), str)
