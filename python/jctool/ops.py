"""The window's features on top of the protocol (jctool.core): device info, battery,
temperature, colors, SPI backup checks and restores, S/N, HD Rumble files, IR camera
settings and images, NTAG dumps, debug command arguments.

Ported from the original's FormJoy (through the Linux CLI, jctool_linux_interactive.cpp).
"""
import struct

from . import tables
from .core import IrConfig, int16, msleep
from .hidio import NOTHING, JOYCON_L, JOYCON_R, PROCON, TYPE_NAMES

BATTERY_STATES = ["Empty (disconnected?)", "Empty, charging", "Low, please charge your device!", "Low, charging",
                  "Medium", "Medium, charging", "Good", "Good, charging", "Full", "Almost full, charging"]


class DeviceInfo:
    def __init__(self, raw):
        self.fw = "%X.%02X" % (raw[0], raw[1])
        self.mac = ":".join("%02X" % b for b in raw[4:10])
        self.mac_bytes = bytes(raw[4:10])


def device_info(jc):
    return DeviceInfo(jc.get_device_info())


def battery(jc):
    """(voltage, percent, state text, level 0-4)"""
    b = jc.get_battery()
    level = (b[0] & 0xF0) >> 4
    volt = b[1] | (b[2] << 8)
    if volt < 0x560:
        pct = 1
    elif volt < 0x5A0:
        pct = int(((volt - 0x60) & 0xFF) / 7.0 + 1)
    elif volt < 0x5E0:
        pct = int(((volt - 0xA0) & 0xFF) / 2.625 + 11)
    elif volt < 0x618:
        pct = int((volt - 0x5E0) / 1.8965 + 36)
    elif volt < 0x658:
        pct = int(((volt - 0x18) & 0xFF) / 1.8529 + 66)
    else:
        pct = 100
    state = BATTERY_STATES[level] if level <= 9 else "?"
    return volt * 2.5 / 1000, pct, state, level // 2


def temperature_c(jc):
    t = jc.get_temperature()
    return 25.0 + int16(t[1] << 8 | t[0]) * 0.0625


# ----------------------------------------------------------------------------------------
# Colors
def read_colors(jc):
    """(body, buttons, left grip, right grip) as (r, g, b), or None."""
    c = jc.get_spi_data(0x6050, 12)
    if c is None:
        return None
    return tuple(tuple(c[i:i + 3]) for i in range(0, 12, 3))


def write_colors(jc, body, buttons, left_grip=None, right_grip=None):
    """Writes the colors (grips: Pro Controller). 0 or 1."""
    data = bytes(body) + bytes(buttons)
    if jc.type == PROCON:
        cur = jc.get_spi_data(0x6050, 12) or bytes(12)
        data += bytes(left_grip) if left_grip else cur[6:9]
        data += bytes(right_grip) if right_grip else cur[9:12]
    jc.set_led_busy()
    res = jc.write_spi_data(0x6050, data)
    jc.send_rumble()
    return res


def hex_color(rgb):
    return "#%02X%02X%02X" % tuple(rgb)


def parse_color(text):
    t = text.strip().lstrip("#")
    if len(t) != 6:
        raise ValueError("Use RRGGBB")
    return tuple(int(t[i:i + 2], 16) for i in (0, 2, 4))


# ----------------------------------------------------------------------------------------
# SPI backup and restore
def backup_filename(jc):
    mac = jc.get_device_info()[4:10]
    kind = {JOYCON_L: "left_", JOYCON_R: "right_"}.get(jc.type, "pro_")
    return "spi_%s%s.bin" % (kind, "".join("%02X" % b for b in mac))


class Backup:
    """A loaded SPI backup and the original's checks."""

    VALIDATION = bytes([0x01, 0x08, 0x00, 0xF0, 0x00, 0x00, 0x62, 0x08, 0xC0, 0x5D, 0x89, 0xFD, 0x04, 0x00, 0xFF, 0xFF,
                        0xFF, 0xFF, 0x40, 0x06])
    FW_MAGIC = bytes([0x0A, 0xFB, 0x00, 0x00, 0x02, 0x0D])
    OTA_MAGIC = bytes([0xAA, 0x55, 0xF0, 0x0F, 0x68, 0xE5, 0x97, 0xD2])

    def __init__(self, data):
        self.data = bytes(data)

    def check(self, device_type, device_mac):
        """(error or None, same_mac). Errors: partial, wrong type, corrupt."""
        d = self.data
        if len(d) != 0x80000:
            return "Partial backup! The file size must be 512KB (524288 Bytes)", False
        if d[0x6012] != device_type or d[0x6013] != 0xA0:
            backup_type = TYPE_NAMES.get(d[0x6012], "unknown") if 1 <= d[0x6012] <= 3 else "unknown"
            name = TYPE_NAMES[device_type]
            return 'Wrong backup! The file is a "%s" backup but your device is a "%s"!\nPlease try with a "%s" SPI backup.' % (
                backup_type, name, name), False
        valid = d[0:20] == self.VALIDATION
        ota = d[0x1FF4 + 6:0x1FF4 + 14] == self.OTA_MAGIC
        for i in range(6):
            if i == 1:
                continue
            if d[0x10000 + i] != self.FW_MAGIC[i] or (ota and d[0x28000 + i] != self.FW_MAGIC[i]):
                valid = False
        if not valid:
            return "Corrupt backup! Please try another backup.", False
        mac = bytes(d[0x1A - i] for i in range(6))     # Stored reversed
        return None, mac == bytes(device_mac)

    def mac_text(self):
        return ":".join("%02x" % self.data[0x1A - i] for i in range(6))


def restore_colors(jc, backup):
    jc.set_led_busy()
    res = jc.write_spi_data(0x6050, backup.data[0x6050:0x6050 + 12])
    jc.send_rumble()
    return res


def restore_sn(jc, backup):
    jc.set_led_busy()
    res = jc.write_spi_data(0x6000, backup.data[0x6000:0x6010])
    jc.send_rumble()
    return res


def restore_user_calibration(jc, backup, left, right, sensor, factory_reset=False):
    """From the backup, or erased (factory_reset). 0 or 1."""
    jc.set_led_busy()
    d = backup.data if backup is not None else None
    l_stick = b"\xFF" * 11 if factory_reset else d[0x8010:0x801B]
    r_stick = b"\xFF" * 11 if factory_reset else d[0x801B:0x8026]
    sens = b"\xFF" * 26 if factory_reset else d[0x8026:0x8040]
    res = 0
    if jc.type != JOYCON_R and left:
        res = jc.write_spi_data(0x8010, l_stick)
    msleep(100)
    if jc.type != JOYCON_L and right and res == 0:
        res = jc.write_spi_data(0x801B, r_stick)
    msleep(100)
    if sensor and res == 0:
        res = jc.write_spi_data(0x8026, sens)
    jc.send_rumble()
    return res


def full_restore(jc, backup, progress=None):
    """Factory configuration and user calibration from the backup, then the controller
    reboots into pairing mode. 0 or 1."""
    jc.set_led_busy()
    d = backup.data
    res = 0
    for base, start_kb in ((0x6000, 0), (0x8000, 4)):
        for i in range(0, 0x1000, 0x10):
            if res != 0:
                break
            res = jc.write_spi_data(base + i, d[base + i:base + i + 0x10])
            if progress:
                progress(start_kb + i / 1024.0)
            msleep(60)
    if res == 0:
        res = jc.write_spi_data(0xF000, b"\xFF" * 16)      # Erase the S/N backup
    if progress:
        progress(8.0)
    if res == 0:
        for cmd in ((0x08, 0x01), (0x07, None), (0x06, 0x02)):   # Shipment, clear pairing, reboot to pairing
            arg = bytearray(44)
            arg[0] = 0x01
            arg[5] = cmd[0]
            if cmd[1] is not None:
                arg[6] = cmd[1]
            jc.send_custom_command(arg)
        jc.send_rumble()
    return res


# ----------------------------------------------------------------------------------------
# Serial number
def valid_sn(text):
    return 1 <= len(text) <= 15 and all(32 <= ord(c) <= 126 for c in text)


def change_sn(jc, new_sn):
    """Writes a new S/N (backs the original up at 0xF000 the first time). 0 or 1."""
    spi_sn = jc.get_spi_data(0x6000, 0x10) or b"\x11" * 16
    sn_ok = spi_sn[0:3] == b"\x00\x00\x58"
    backup = jc.get_spi_data(0xF000, 1) or b"\x00"
    res = 0
    if sn_ok and backup[0] == 0xFF:
        res = jc.write_spi_data(0xF000, spi_sn)
    msleep(100)
    raw = new_sn.encode("ascii")
    sn = bytes(16 - len(raw)) + raw
    if res == 0:
        res = jc.write_spi_data(0x6000, sn)
    jc.send_rumble()
    return res


def restore_sn_from_controller(jc):
    """Restores the S/N backed up inside the controller. 0, 1 (failed), or 2 (no backup)."""
    spi_sn = jc.get_spi_data(0xF000, 0x10) or b"\x11" * 16
    msleep(100)
    if spi_sn[0] != 0x00:
        return 2
    res = jc.write_spi_data(0x6000, spi_sn)
    jc.send_rumble()
    return res


# ----------------------------------------------------------------------------------------
# HD Rumble files
class VibFile:
    """A loaded .jcvib (raw, type 1) or .bnvib (binary, types 2-4) file."""

    MAGIC = bytes([0x52, 0x52, 0x41, 0x57, 0x4, 0xC, 0x3, 0x10])
    TYPE_NAMES = {1: "Raw HD Rumble", 2: "Binary HD Rumble", 3: "Loop Binary HD Rumble", 4: "Loop and Wait Binary"}

    def __init__(self, data):
        f = bytes(data)
        if len(f) < 0x18:
            raise ValueError("Unknown format")
        self.loaded = f
        self.converted = bytearray(f)
        self.type = 0
        self.sample_rate = 0
        self.samples = self.loop_start = self.loop_end = self.loop_wait = 0
        self.loop_times = 0
        le32 = lambda o: struct.unpack_from("<I", f, o)[0]
        if f[0] == self.MAGIC[0]:
            if f[1:4] == self.MAGIC[1:4]:
                self.type = 1
                self.sample_rate = (f[4] << 8) + f[5]
                self.samples = struct.unpack_from(">I", f, 6)[0]
        elif f[4] == self.MAGIC[6]:
            rate = f[6] + (f[7] << 8)
            self.sample_rate = 1000 // rate if rate else 0
            size = 0
            if f[0] == self.MAGIC[4]:
                self.type = 2
                size = le32(0x8)
            elif f[0] == self.MAGIC[5]:
                self.type = 3
                size = le32(0x10)
                self.loop_start, self.loop_end = le32(0x8), le32(0xC)
            elif f[0] == self.MAGIC[7]:
                self.type = 4
                size = le32(0x14)
                self.loop_start, self.loop_end, self.loop_wait = le32(0x8), le32(0xC), le32(0x10)
            self.samples = size // 4
        if self.type == 0 or self.sample_rate == 0:
            raise ValueError("Unknown format")
        self.data_offset = {1: 0xA, 2: 0xC, 3: 0x14, 4: 0x18}[self.type]
        if (self.data_offset + self.samples * 4 > len(f) or self.loop_start > self.loop_end
                or self.loop_end > self.samples):
            raise ValueError("The file is shorter than its header says, or its loop points are invalid")

    @property
    def type_name(self):
        return self.TYPE_NAMES[self.type]

    @property
    def seconds(self):
        return self.sample_rate * self.samples / 1000.0

    def sample(self, i):
        """The 4 rumble bytes of sample i (converted for binary files)."""
        if self.type == 1:
            return self.loaded[0xA + i * 4:0xE + i * 4]
        off = self.data_offset + i * 4
        return bytes(self.converted[off:off + 4])

    def convert(self, lf_amp=10, lf_freq=10, hf_amp=10, hf_freq=10):
        """Binary files: raw rumble with the equalizer (0-20, 10 = unchanged) and the safe
        amplitude limit (the sum of both bands at most 1.0)."""
        if self.type == 1:
            return
        f = self.loaded
        base = self.data_offset
        for i in range(self.samples):
            o = base + i * 4
            la = f[o] if lf_amp == 10 else int(min(max(f[o] * lf_amp / 10.0, 0.0), 255.0))
            ha = f[o + 2] if hf_amp == 10 else int(min(max(f[o + 2] * hf_amp / 10.0, 0.0), 255.0))
            limit = la / 255.0 + ha / 255.0
            if limit > 1.0:
                la = int(la * (1.0 / limit))
                ha = int(ha * (1.0 / limit))
            lf = ((f[o + 1] if lf_freq == 10 else int(min(max(f[o + 1] * lf_freq / 10.0, 0.0), 191.0))) - 0x40) & 0xFF
            hf = (((f[o + 3] if hf_freq == 10 else int(min(max(f[o + 3] * hf_freq / 10.0, 0.0), 223.0))) - 0x60) * 4) & 0xFFFF
            jl = amp_index(la / 255.0)
            jh = amp_index(ha / 255.0)
            self.converted[o + 2] = (((tables.AMP_LA[jl] >> 8) & 0xFF) + lf) & 0xFF
            self.converted[o + 3] = tables.AMP_LA[jl] & 0xFF
            self.converted[o] = hf & 0xFF
            self.converted[o + 1] = (((hf >> 8) & 0xFF) + tables.AMP_HA[jh]) & 0xFF


def amp_index(amp):
    """Index into the amplitude tables for an amplitude 0..1 (like the original's search)."""
    if amp <= 0:
        return 0
    for j in range(1, 101):
        if amp < tables.AMP_FLOAT[j]:
            return j - 1
    return 100


# ----------------------------------------------------------------------------------------
# IR camera
class IrSettings:
    """The IR Camera panel's settings."""

    RESOLUTIONS = ["240x320", "120x160", "60x80", "30x40"]
    MODES = ["Capture", "Pointing", "Clustering"]
    COLORS = ["Greyscale", "Night vision", "Ironbow", "Infrared"]

    def __init__(self):
        self.resolution = 0         # 0: 240x320, 1: 120x160, 2: 60x80, 3: 30x40
        self.mode = 0               # 0: Capture, 1: Pointing, 2: Clustering
        self.colorize = 2
        self.leds_far = True        # Far/Narrow (75 degrees) leds 1/2
        self.leds_near = True       # Near/Wide (130 degrees) leds 3/4
        self.intensity_far = 15     # 0-15
        self.intensity_near = 16    # 0-16
        self.flashlight = False
        self.strobe = False
        self.ex_filter = True
        self.selfie = False
        self.exposure = 300         # us, 0-600
        self.auto_exposure = False  # Streaming (Capture always uses it, unless quick capture)
        self.gain = 2               # 1-20 (streaming without auto exposure)
        self.denoise = True
        self.edge_smoothing = 35
        self.color_interpolation = 68
        self.custom_reg = 0
        self.custom_val = 0

    def config(self, jc, new_config, streaming):
        """prepareSendIRConfig: an IrConfig; for a new configuration it also sets the frame
        size (jc.ir_max_frag_no) and auto exposure (jc.enable_ir_auto_exposure)."""
        cfg = IrConfig()
        if new_config:
            r = self.resolution
            if self.auto_exposure and r == 3 and streaming:
                r = 2            # 30x40 is disabled with auto exposure
            cfg.ir_res_reg = (0x00, 0x50, 0x64, 0x69)[r]
            jc.ir_max_frag_no = (0xFF, 0x3F, 0x0F, 0x03)[r]
            cfg.ir_mode = 0x07 if self.mode == 0 else 0x04 if self.mode == 1 else 0x06
        else:
            cfg.ir_res_reg = 0x69 if self.resolution == 3 else 0x00
        if self.leds_far and self.leds_near:
            cfg.ir_leds = 0x00
        elif self.leds_far:
            cfg.ir_leds = 0x20
        elif self.leds_near:
            cfg.ir_leds = 0x10
        else:
            cfg.ir_leds = 0x30
        cfg.ir_leds_intensity = (self.intensity_far << 8) | self.intensity_near
        if self.flashlight:
            cfg.ir_leds |= 0x01
        if self.strobe and not self.flashlight:
            cfg.ir_leds |= 0x80
        cfg.ir_ex_light_filter = 0x03 if (self.ex_filter or self.strobe) and not self.flashlight else 0x00
        cfg.ir_flip = 0x02 if self.selfie else 0x00
        cfg.ir_exposure = self.exposure * 31200 // 1000
        if not self.auto_exposure and streaming:
            jc.enable_ir_auto_exposure = False
            cfg.ir_digital_gain = self.gain
        else:
            jc.enable_ir_auto_exposure = True
            cfg.ir_digital_gain = 1
        cfg.ir_denoise = ((0x01 if self.denoise else 0x00) << 16) | ((self.edge_smoothing & 0xFF) << 8) | (self.color_interpolation & 0xFF)
        cfg.ir_custom_register = (self.custom_reg & 0xFFFF) | ((self.custom_val & 0xFF) << 16)
        return cfg


IR_ERRORS = {1: "1ID31", 2: "2MCUON", 3: "3MCUONBUSY", 4: "4MCUMODESET", 5: "5MCUSETBUSY", 6: "6IRMODESET",
             7: "7IRSETBUSY", 8: "8IRCFG", 9: "9IRFCFG", 10: "10IRNOCFG"}
NFC_ERRORS = {1: "1ID31", 2: "2MCUON", 3: "3MCUONBUSY", 4: "4MCUMODESET", 5: "5MCUSETBUSY", 6: "6NFCPOLL",
              7: "7NFCRECV", 8: "8NFCREAD"}
NFC_HELP = {6: "The NFC reader didn't get ready. Scan again.",
            7: "No tag detected. Hold it on the right stick, then scan again.",
            8: "The tag couldn't be read. Keep it still on the right stick and scan again."}


def ir_run(jc, settings, stream):
    """Captures (or streams until jc.enable_ir_video is cleared). If the camera didn't apply
    the settings, sets it up again once. Returns 0, an error code, or 10 (settings ignored)."""
    jc.enable_ir_video = stream
    cfg = settings.config(jc, True, stream)
    jc.ir_exposure_value = settings.exposure
    res = jc.ir_sensor(cfg)
    if res == 0 and jc.ir_last_capture_stale and (not stream or jc.enable_ir_video):
        jc.note("IR: camera didn't apply the settings, setting it up again")
        res = jc.ir_sensor(cfg)
        if res == 0 and jc.ir_last_capture_stale:
            res = 10
    jc.enable_ir_video = False
    return res


def ir_pixel(v, colorize):
    if colorize == 2:
        c = tables.IRON_PALETTE[v]
        return (c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF
    if colorize == 0:
        return v, v, v
    if colorize == 1:
        return 0, v, 0
    return v, 0, 0


def ir_render(image, width, height, colorize):
    """Colorizes and rotates 90 degrees clockwise, like the window shows and saves it.
    Returns (rgb bytes, width, height)."""
    out_w, out_h = height, width
    lut = [bytes(ir_pixel(v, colorize)) for v in range(256)]
    rows = []
    for x in range(width):                 # Output row x: source column x, from the bottom up
        rows.append(b"".join(lut[image[y * width + x]] for y in range(height - 1, -1, -1)))
    return b"".join(rows), out_w, out_h


# ----------------------------------------------------------------------------------------
# NFC
def ntag_text(data, pages):
    model = {45: "213", 135: "215", 231: "216"}.get(pages, "???")
    kind = " (Amiibo)" if data[16] == 0xA5 else " (NDEF)" if data[16] == 0x01 else ""
    lines = ["NTAG %s%s" % (model, kind), ""]
    for i in range(pages):
        d = data[i * 4:i * 4 + 4]
        lines.append("%02X: %s|%s|" % (i, "".join("%02X " % b for b in d),
                                       "".join(chr(b) if 0x20 <= b <= 0x7E else "." for b in d)))
    return "\n".join(lines)


# ----------------------------------------------------------------------------------------
# Debug custom command
def debug_command_args(cmd=0x01, hf_freq=0x00, hf_amp=0x01, lf_freq=0x40, lf_amp=0x40, subcmd=0x00, arguments=""):
    """The 44 byte custom command buffer, with the window's argument parser: pairs of hex
    digits, a leading low nibble if odd."""
    test = bytearray(44)
    test[0:6] = bytes([cmd & 0xFF, hf_freq & 0xFF, hf_amp & 0xFF, lf_freq & 0xFF, lf_amp & 0xFF, subcmd & 0xFF])
    clean = "".join(c for c in arguments.upper() if c in "0123456789ABCDEF")
    i = 0
    pos = 6
    if len(clean) % 2:
        test[6] = int(clean[0], 16)
        i, pos = 1, 7
    while i < len(clean) and pos < 44:
        test[pos] = int(clean[i:i + 2], 16)
        i += 2
        pos += 1
    return test


def disconnect(jc):
    arg = bytearray(44)
    arg[0], arg[5], arg[6] = 0x01, 0x06, 0x00
    return jc.send_custom_command(arg)
