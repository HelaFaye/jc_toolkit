"""BlueCubeMod (https://github.com/HelaFaye/BlueCubeMod, v3 and later): a GameCube controller
as a Pro Controller over Bluetooth (an ESP32). Its SPI flash is emulated, so the toolkit's
colors, serial number, stick calibration, backups and restores work on it. It has no
motion sensors, NFC or IR camera, and its own settings (the triggers, the sticks).

Detected with its vendor subcommand 0xBC (a real controller only acknowledges it, with no
data); its settings change with 0xBD.
"""
from .hidio import PROCON, TYPE_NAMES

SUB_INFO = 0xBC
SUB_SETTINGS = 0xBD

TRIGGER_MODES = [
    "Clicked all the way: ZL / ZR",
    "Analog past the threshold: ZL / ZR",
    "Analog past the threshold: L / R; clicked: ZL / ZR",
]
STICK_MODES = [
    "Raw (the calibration in the controller's memory applies)",
    "Re-centered at power-on (like a GameCube)",
]


class Info:
    def __init__(self, data):
        self.protocol = data[3]
        self.version = "%d.%d.%d" % (data[4], data[5], data[6])
        self.trigger_mode, self.threshold, self.stick_mode = data[7], data[8], data[9]
        self.gc_connected = bool(data[10])
        self.gc_raw = bytes(data[11:19])

    @property
    def name(self):
        return "BlueCubeMod " + self.version

    def describe(self):
        gc = "connected" if self.gc_connected else "not answering"
        return "%s, GameCube controller %s" % (self.name, gc)

    def settings_text(self):
        t = TRIGGER_MODES[self.trigger_mode] if self.trigger_mode < len(TRIGGER_MODES) else "?"
        s = STICK_MODES[self.stick_mode] if self.stick_mode < len(STICK_MODES) else "?"
        return "Triggers: %s (threshold %d)\nSticks: %s" % (t, self.threshold, s)


def detect(jc):
    """Info when the controller is a BlueCubeMod, else None."""
    # A few tries only: every controller is asked once, when it connects
    r = jc._transact(lambda: jc.packet(0x01, SUB_INFO), lambda b: b[0] == 0x21 and b[14] == SUB_INFO,
                     reads=4, attempts=2)
    if r is None or bytes(r[1][15:18]) != b"BCM":
        return None
    return Info(bytes(r[1][15:15 + 19]))


def set_settings(jc, trigger_mode, threshold, stick_mode):
    """0: saved, 1: invalid, 2: applied but not saved, None: no reply."""
    r = jc.send_subcommand(SUB_SETTINGS, bytes([trigger_mode, threshold, stick_mode]))
    return None if r is None else r[15]


def sn_supported(jc):
    """Pro Controllers have no serial number in their SPI flash; a BlueCubeMod has one."""
    return jc.type != PROCON or getattr(jc, "bcm", None) is not None


def device_name(jc):
    name = TYPE_NAMES[jc.type]
    return name + " (%s)" % jc.bcm.name if getattr(jc, "bcm", None) is not None else name


def has_feature(jc, feature):
    """False for what a BlueCubeMod hasn't got: "nfc", "ir", "motion"."""
    return getattr(jc, "bcm", None) is None or feature not in ("nfc", "ir", "motion")
