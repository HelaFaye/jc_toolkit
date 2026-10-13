"""The Bluetooth hidraw bridge end to end: the native helper (native/jctool_hidraw.c, built
here with the system's C compiler) over a fake /sys and /dev, where the "hidraw node" is a
SOCK_SEQPACKET socket with the emulated Joy-Con behind it."""
import fcntl
import os
import select
import shutil
import socket
import subprocess
import sys
import threading
import time

import pytest

from conftest import RecordingUi, wait_until
from jctool import calibration, hidio, hidraw_bridge, ops
from jctool.core import JoyCon
from jctool.fake import FakeJoyCon

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "native", "jctool_hidraw.c")
HID_ID = "0005:057E:2007.0003"

pytestmark = pytest.mark.skipif(not sys.platform.startswith("linux") or not (shutil.which("cc") or shutil.which("gcc")),
                                reason="needs Linux and a C compiler")


@pytest.fixture(scope="module")
def helper(tmp_path_factory):
    out = str(tmp_path_factory.mktemp("helper") / "jctool-hidraw")
    cc = shutil.which("cc") or shutil.which("gcc")
    subprocess.check_call([cc, "-O2", "-o", out, SOURCE])
    return out


class FakeHidraw:
    """/sys and /dev for one Bluetooth Joy-Con (R) bound to hid_nintendo; its node is a
    seqpacket socket served by the emulated controller. hid-generic's bind file is a FIFO
    that records whether hid.ignore_special_drivers was on (the kernel refuses the bind
    with "No such device" otherwise, while hid_nintendo is loaded)."""

    def __init__(self, root, fake):
        self.root, self.fake = root, fake
        dev_dir = os.path.join(root, "sys/bus/hid/devices", HID_ID)
        os.makedirs(os.path.join(dev_dir, "hidraw/hidraw0"))
        with open(os.path.join(dev_dir, "uevent"), "w") as f:
            f.write("DRIVER=nintendo\nHID_ID=0005:0000057E:00002007\nHID_NAME=Joy-Con (R)\n"
                    "HID_PHYS=aa:bb:cc:dd:ee:ff\nHID_UNIQ=98:b6:e9:12:34:56\nMODALIAS=hid:b0005g0000v0000057Ep00002007\n")
        for drv in ("nintendo", "hid-generic"):
            os.makedirs(os.path.join(root, "sys/bus/hid/drivers", drv))
            for name in ("bind", "unbind"):
                open(os.path.join(root, "sys/bus/hid/drivers", drv, name), "w").close()
        os.symlink(os.path.join(root, "sys/bus/hid/drivers/nintendo"), os.path.join(dev_dir, "driver"))
        self.param = os.path.join(root, "sys/module/hid/parameters/ignore_special_drivers")
        os.makedirs(os.path.dirname(self.param))
        with open(self.param, "w") as f:
            f.write("0\n")
        bind = os.path.join(root, "sys/bus/hid/drivers/hid-generic/bind")
        os.remove(bind)
        os.mkfifo(bind)
        self.generic_binds = []          # (id, ignore_special_drivers at the time)
        threading.Thread(target=self._generic_bind, args=(bind,), daemon=True).start()
        os.makedirs(os.path.join(root, "sys/class/hidraw/hidraw0"))
        os.symlink(dev_dir, os.path.join(root, "sys/class/hidraw/hidraw0/device"))
        os.makedirs(os.path.join(root, "dev"))
        self.server = socket.socket(socket.AF_UNIX, socket.SOCK_SEQPACKET)
        self.server.bind(os.path.join(root, "dev/hidraw0"))
        self.server.listen(1)
        self.stop = False
        threading.Thread(target=self._serve, daemon=True).start()

    def _generic_bind(self, fifo):
        """Keeps the FIFO's buffer full, so the helper's write of the id blocks there: the
        parameter is checked while the helper is inside the bind, then the buffer drained."""
        fd = os.open(fifo, os.O_RDWR | os.O_NONBLOCK)
        fcntl.fcntl(fd, 1031, 4096)                          # F_SETPIPE_SZ: one page
        filled = 0
        while True:
            try:
                filled += os.write(fd, b"x" * 4096)
            except BlockingIOError:
                break
        param = lambda: open(self.param).read().strip()
        end = time.monotonic() + 5
        while param() != "1" and time.monotonic() < end:   # The helper turns it on, then binds
            time.sleep(0.001)
        time.sleep(0.1)                                      # Now blocked writing the id
        state = param()
        data = b""
        while True:
            select.select([fd], [], [], 0.5)
            try:
                chunk = os.read(fd, 65536)
            except BlockingIOError:
                break
            data += chunk
            if len(data) > filled:
                time.sleep(0.05)
        self.generic_binds.append((data[filled:].decode(), state))
        os.close(fd)

    def driver_file(self, drv, name):
        with open(os.path.join(self.root, "sys/bus/hid/drivers", drv, name)) as f:
            return f.read()

    def _serve(self):
        """One thread, so reports keep their order (like the kernel's hidraw queue)."""
        conn, _ = self.server.accept()
        try:
            while not self.stop:
                if select.select([conn], [], [], 0.002)[0]:
                    data = conn.recv(4096)
                    if not data:
                        return
                    self.fake.write(data)
                r = self.fake.read(0x170, 0)          # Replies, then streamed reports (0x30 every 15ms)
                while r:
                    conn.send(bytes(r))
                    r = self.fake.read(0x170, 0) if self.fake.replies else None
        except OSError:
            return


@pytest.fixture
def bridge(helper, tmp_path, monkeypatch):
    fake = FakeJoyCon(hidio.JOYCON_R)
    hw = FakeHidraw(str(tmp_path / "root"), fake)
    monkeypatch.setenv("JCTOOL_HID_BACKEND", "bridge")
    monkeypatch.setenv("JCTOOL_HIDRAW_HELPER", helper)
    monkeypatch.setenv("JCTOOL_HIDRAW_ROOT", hw.root)
    monkeypatch.setenv("JCTOOL_HIDRAW_SU", "0")
    yield fake, hw
    hw.stop = True


def test_bridge_lists_the_bluetooth_joycon(bridge):
    devices = hidraw_bridge.enumerate(0x057E, 0)
    assert len(devices) == 1
    d = devices[0]
    assert (d["path"], d["product_id"], d["bus_type"], d["driver"], d["product_string"]) == (
        "bt:" + HID_ID, 0x2007, 2, "nintendo", "Joy-Con (R)")
    found = hidio.enumerate_controllers()
    assert [(f.type, f.usb, f.name) for f in found] == [(hidio.JOYCON_R, False, "Joy-Con (R)")]


def test_bridge_runs_the_protocol_and_gives_the_driver_back(bridge, tmp_path):
    fake, hw = bridge
    dev = hidio.Device.open(hidio.enumerate_controllers()[0])
    assert dev.raw.node == "hidraw0"
    assert hw.driver_file("nintendo", "unbind") == HID_ID          # Moved off hid_nintendo
    assert wait_until(lambda: hw.generic_binds, 3)
    assert hw.generic_binds == [(HID_ID, "1")]                     # With ignore_special_drivers on
    with open(hw.param) as f:
        assert f.read().strip() == "0"                              # And put back
    jc = JoyCon(dev, RecordingUi())
    info = ops.device_info(jc)
    assert info.fw == "3.89" and info.mac == "98:B6:E9:12:34:56"
    assert ops.write_colors(jc, (0x12, 0x34, 0x56), (0xAB, 0xCD, 0xEF)) == 0
    assert bytes(fake.spi[0x6050:0x6056]).hex() == "123456abcdef"
    assert calibration.read_status(jc)[0] == "Factory"
    path = tmp_path / "spi.bin"
    assert jc.dump_spi(str(path)) == 0 and path.read_bytes() == bytes(fake.spi)
    dev.close()
    assert hw.driver_file("hid-generic", "unbind") == HID_ID        # Given back
    assert hw.driver_file("nintendo", "bind") == HID_ID


def test_bridge_ir_capture(bridge):
    fake, hw = bridge
    dev = hidio.Device.open(hidio.enumerate_controllers()[0])
    ui = RecordingUi()
    jc = JoyCon(dev, ui)
    settings = ops.IrSettings()
    assert ops.ir_run(jc, settings, stream=False) == 0
    image, w, h = ui.frames[-1]
    assert (w, h) == (320, 240)
    dev.close()


def test_bridge_reports_errors(bridge, monkeypatch):
    with pytest.raises(IOError, match="No hidraw node"):
        hidraw_bridge.device().open_path("bt:0005:057E:2006.0009")
    with pytest.raises(IOError, match="Invalid device id"):
        hidraw_bridge.device().open_path("bt:../x")
    monkeypatch.setenv("JCTOOL_HIDRAW_HELPER", "/nonexistent/jctool-hidraw")
    assert hidraw_bridge.enumerate() == [] and "missing" in hidraw_bridge.last_error
