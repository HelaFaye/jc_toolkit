"""Joy-Con Toolkit protocol code.

A port of cli/jc_core.cpp (itself a port of CTCaer's jctool/jctool.cpp through the Linux
build's JcTool.cs): the same packet layouts, reply checks and retry counts, and the fixes
made there (the IR camera waits for complete frames, auto exposure counts white pixels on
the full sensor, a camera that ignores its resolution is detected, NFC lengths are
bounds-checked).

Copyright (c) 2018 CTCaer. Licensed under the MIT license (see LICENSE).
"""
import math
import os
import struct
import time

from . import tables
from .hidio import NOTHING, JOYCON_L, JOYCON_R, PROCON


def msleep(ms):
    if ms > 0:
        time.sleep(ms / 1000.0)


def int16(v):
    v &= 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def u16le(buf, i):
    return buf[i] | (buf[i + 1] << 8)


def mcu_crc8(data):
    crc = 0
    for b in data:
        crc = tables.MCU_CRC8[(crc ^ b) & 0xFF]
    return crc


def decode_stick_params(enc):
    """3 bytes -> two 12-bit values."""
    return [((enc[1] << 8) & 0xF00) | enc[0], (enc[2] << 4) | (enc[1] >> 4)]


def encode_stick_params(a, b):
    return bytes([a & 0xFF, ((a & 0xF00) >> 8) | ((b & 0xF) << 4), (b & 0xFF0) >> 4])


def analog_stick_calc(x, y, x_calc, y_calc):
    """Calibrated stick position (-1..1) with the Joy-Con deadzones.
    Credit to Hypersect (Ryan Juckett): http://blog.hypersect.com/interpreting-analog-sticks/"""
    dead_center, dead_outer = 0.15, 0.10
    x = min(max(x, x_calc[0]), x_calc[2])
    y = min(max(y, y_calc[0]), y_calc[2])
    def axis(v, c):
        if v >= c[1]:
            return (v - c[1]) / float(c[2] - c[1]) if c[2] != c[1] else 0.0
        return -((v - c[1]) / float(c[0] - c[1])) if c[0] != c[1] else 0.0
    xf, yf = axis(x, x_calc), axis(y, y_calc)
    mag = math.sqrt(xf * xf + yf * yf)
    if mag > dead_center:
        legal = 1.0 - dead_outer - dead_center
        scale = min(1.0, (mag - dead_center) / legal) / mag
        return xf * scale, yf * scale
    return 0.0, 0.0


class IrConfig:
    """ir_image_config"""

    def __init__(self):
        self.ir_res_reg = 0
        self.ir_exposure = 0
        self.ir_leds = 0
        self.ir_leds_intensity = 0
        self.ir_digital_gain = 0
        self.ir_ex_light_filter = 0
        self.ir_custom_register = 0
        self.ir_buffer_update_time = 0
        self.ir_hand_analysis_mode = 0
        self.ir_hand_analysis_threshold = 0
        self.ir_denoise = 0
        self.ir_flip = 0
        self.ir_mode = 0


class Ui:
    """What the window shows while an operation runs. Override what you need; every method
    is called on the thread that runs the operation."""

    def poll(self):                       # Long operations call this often (stop requests etc.)
        pass

    def spi_progress(self, offset):
        pass

    def custom_command(self, sent, reply_cmd, reply):
        pass

    def button_test_info(self, text):
        pass

    def button_test(self, report, sensors, raw):
        pass

    def ir_status(self, text):
        pass

    def ir_help(self, text):
        pass

    def ir_frame(self, image, width, height):
        pass

    def ir_exposure(self, exposure):
        pass

    def nfc_uid(self, text):
        pass

    def nfc_tag(self, text):
        pass

    def ntag_contents(self, data, pages):
        pass


class JoyCon:
    """Protocol functions for one open controller (a jctool.hidio.Device)."""

    def __init__(self, device, ui=None):
        self.dev = device
        self.type = device.type
        self.ui = ui or Ui()
        self.timming_byte = 0
        # Operation flags (clear them from another thread to stop an operation)
        self.enable_button_test = False
        self.enable_ir_video = False
        self.enable_ir_auto_exposure = False
        self.enable_nfc_scanning = False
        self.cancel_spi_dump = False
        # IR state and options
        self.ir_max_frag_no = 0xFF
        self.ir_exposure_value = 300
        self.ir_quick_capture = False
        self.ir_skip_leftover = os.environ.get("JCTOOL_IR_SKIP_LEFTOVER") == "1"
        self.ir_patient_setup = os.environ.get("JCTOOL_IR_PATIENT_SETUP") == "1"
        self.ir_last_frame_missing = 0
        self.ir_last_capture_stale = False
        self.ir_pending_live = None      # An IrConfig to apply during a stream (live config)

    # ------------------------------------------------------------------------------------
    # Packets
    def _timer(self):
        t = self.timming_byte & 0xF
        self.timming_byte = (self.timming_byte + 1) & 0xFF
        return t

    def packet(self, cmd=0x01, subcmd=None, args=b"", size=49):
        buf = bytearray(size)
        buf[0] = cmd
        buf[1] = self._timer()
        if subcmd is not None:
            buf[10] = subcmd
        buf[11:11 + len(args)] = args
        return buf

    def write(self, buf, length=49):
        return self.dev.write(buf, length)

    def read(self, length, timeout_ms):
        return self.dev.read(length, timeout_ms)

    def note(self, what):
        self.dev.note(what)

    def _transact(self, build, match, reads=8, attempts=20, read_len=49):
        """Sends build() and reads up to `reads` + 1 replies for one that match()es; on none,
        sends again (`attempts` + 1 times in all). Returns (count, reply) or None."""
        for _ in range(attempts + 1):
            self.write(build())
            retries = 0
            while True:
                n, buf = self.read(read_len, 64)
                if n > 0 and match(buf):
                    return n, buf
                retries += 1
                if retries > reads or n == 0:
                    break
        return None

    def _subcmd_simple(self, subcmd, arg1=0, arg2=None, wait=64):
        """A subcommand whose reply is only waited for and dropped (0-length read)."""
        args = bytes([arg1]) if arg2 is None else bytes([arg1, arg2])
        self.write(self.packet(0x01, subcmd, args))
        self.read(0, wait)

    def send_subcommand(self, subcmd, args=b""):
        """Sends subcommand x01 and returns its x21 reply (49 bytes), or None."""
        r = self._transact(lambda: self.packet(0x01, subcmd, bytes(args)[:38]),
                           lambda b: b[0] == 0x21 and b[0xE] == subcmd)
        return None if r is None else r[1]

    # ------------------------------------------------------------------------------------
    # Info, SPI
    def set_led_busy(self):
        self._subcmd_simple(0x30, 0x81)
        if self.type != JOYCON_L:
            buf = self.packet(0x01, 0x38, bytes([0x28, 0x20, 0xF2, 0xF0, 0xF0]))
            self.write(buf)
            self.read(0, 64)
        return 0

    def get_spi_data(self, offset, length):
        """SPI flash bytes, or None."""
        def build():
            return self.packet(0x01, 0x10, struct.pack("<IB", offset, length))
        r = self._transact(build, lambda b: b[13] == 0x90 and b[14] == 0x10 and struct.unpack_from("<I", b, 15)[0] == offset)
        if r is None:
            return None
        n, buf = r
        if n >= 0x14 + length:
            return bytes(buf[0x14:0x14 + length])
        return bytes(length)

    def write_spi_data(self, offset, data):
        """Returns 0, or 1 on failure."""
        data = bytes(data)
        def build():
            buf = self.packet(0x01, 0x11, struct.pack("<IB", offset, len(data)))
            buf[0x10:0x10 + len(data)] = data
            return buf
        r = self._transact(build, lambda b: b[13] == 0x80 and b[14] == 0x11, attempts=19)
        return 0 if r is not None else 1

    def get_sn(self, offset=0x6001, length=0xF):
        data = self.get_spi_data(offset, length)
        if data is None:
            return "Error!"
        return "".join(chr(c) for c in data if c != 0)

    def get_device_info(self):
        """10 bytes: FW major, minor, type, ?, MAC (6)."""
        r = self._transact(lambda: self.packet(0x01, 0x02), lambda b: b[13] == 0x82 and b[14] == 0x02)
        buf = r[1] if r else bytearray(49)
        return bytes(buf[0xF:0xF + 10])

    def get_battery(self):
        """3 bytes: report byte 2 (battery level), voltage LSB, MSB."""
        r = self._transact(lambda: self.packet(0x01, 0x50), lambda b: b[13] == 0xD0 and b[14] == 0x50)
        buf = r[1] if r else bytearray(49)
        return bytes([buf[0x2], buf[0xF], buf[0x10]])

    def get_temperature(self):
        """Raw temperature (2 bytes)."""
        imu_changed = False
        r = self._transact(lambda: self.packet(0x01, 0x43, bytes([0x10, 0x01])), lambda b: b[13] == 0xC0 and b[14] == 0x43)
        buf = r[1] if r else bytearray(49)
        if (buf[0x11] >> 4) == 0:
            self._subcmd_simple(0x40, 0x01)
            imu_changed = True
            msleep(64)                     # Let the temperature sensor stabilize
        r = self._transact(lambda: self.packet(0x01, 0x43, bytes([0x20, 0x02])), lambda b: b[13] == 0xC0 and b[14] == 0x43)
        buf = r[1] if r else bytearray(49)
        out = bytes([buf[0x11], buf[0x12]])
        if imu_changed:
            self._subcmd_simple(0x40, 0x00)
        return out

    def dump_spi(self, path):
        """Saves the whole 512KB SPI flash to `path`. 0: done (or cancelled), 1: failed."""
        read_len = 0x1D
        offset = 0
        with open(path, "wb") as f:
            while offset < 0x80000 and not self.cancel_spi_dump:
                self.ui.spi_progress(offset)
                self.ui.poll()
                error_reading = 0
                while True:
                    buf = self.packet(0x01, 0x10, struct.pack("<IB", offset, read_len))
                    self.write(buf)
                    retries = 0
                    got = None
                    while True:
                        n, rb = self.read(49, 64)
                        if n > 0 and rb[13] == 0x90 and rb[14] == 0x10 and struct.unpack_from("<I", rb, 15)[0] == offset:
                            got = rb
                            break
                        retries += 1
                        if retries > 8 or n == 0:
                            break
                    if got is not None:
                        break
                    if retries > 8:
                        error_reading += 1
                    if error_reading > 10:
                        return 1
                f.write(bytes(got[0x14:0x14 + read_len]))
                offset += read_len
                if offset == 0x7FFE6:
                    read_len = 0x1A
            self.ui.spi_progress(offset)
        return 0

    # ------------------------------------------------------------------------------------
    # Rumble
    def send_rumble(self):
        """The confirmation rumble and LEDs."""
        self._subcmd_simple(0x48, 0x01)
        msleep(16)
        buf = self.packet(0x01)
        buf[2:6] = buf[6:10] = bytes([0xC2, 0xC8, 0x03, 0x72])
        self.write(buf)
        self.read(0, 64)
        msleep(81)
        buf[1] = self._timer()
        buf[2:6] = buf[6:10] = bytes([0x00, 0x01, 0x40, 0x40])
        self.write(buf)
        self.read(0, 64)
        msleep(5)
        buf[1] = self._timer()
        buf[2:6] = buf[6:10] = bytes([0xC3, 0xC8, 0x60, 0x64])
        self.write(buf)
        self.read(0, 64)
        msleep(5)
        buf = self.packet(0x01, 0x48, bytes([0x00]))
        buf[2:6] = buf[6:10] = bytes([0x00, 0x01, 0x40, 0x40])
        self.write(buf)
        self.read(0, 64)
        self._subcmd_simple(0x30, 0x01)
        if self.type != JOYCON_L:
            # HOME led: heartbeat style
            buf = self.packet(0x01, 0x38)
            buf[11] = 0xF1
            buf[12] = 0x00
            for i in range(13, 19):
                buf[i] = 0xF0
            for i in (19, 22, 25, 28, 31):
                buf[i] = 0x00
            for i in (20, 21, 23, 24, 26, 27, 29, 30, 32, 33):
                buf[i] = 0xFF
            self.write(buf)
            self.read(0, 64)
        return 0

    def rumble_report(self, rumble4):
        """One rumble-only report (0x10), the same 4 bytes on both sides."""
        buf = self.packet(0x10)
        buf[2:6] = buf[6:10] = bytes(rumble4)
        return self.write(buf, 10)

    def enable_vibration(self, on):
        self._subcmd_simple(0x48, 0x01 if on else 0x00, wait=120 if on else 64)

    def _vibration_off_and_leds(self):
        buf = self.packet(0x01, 0x48, bytes([0x00]))
        buf[2:6] = buf[6:10] = bytes([0x00, 0x01, 0x40, 0x40])
        self.write(buf)
        self.read(0, 64)
        self._subcmd_simple(0x30, 0x01)

    def play_tune(self, tune_no):
        """The easter egg HD Rumble tunes: 0 Super Mario Bros., 1 Super Mario Odyssey "OK"."""
        self._subcmd_simple(0x48, 0x01, wait=120)
        tune = tables.TUNE_SMB if tune_no == 0 else tables.TUNE_SMO_OK
        self.stop_playback = False
        for sample in tune:
            msleep(15)
            self.rumble_report(struct.pack(">I", sample))
            self.ui.poll()
            if self.stop_playback:
                break
        msleep(15)
        self._vibration_off_and_leds()
        return 0

    def play_hd_rumble(self, vib):
        """Plays a loaded HD Rumble file (jctool.hdrumble.VibFile)."""
        self._subcmd_simple(0x48, 0x01, wait=120)
        self.stop_playback = False
        rate = vib.sample_rate
        def play(i):
            msleep(rate)
            self.rumble_report(vib.sample(i))
            self.ui.poll()
        if vib.type in (1, 2):
            for i in range(vib.samples):
                if self.stop_playback:
                    break
                play(i)
        else:
            for i in range(vib.loop_start):
                if self.stop_playback:
                    break
                play(i)
            for _ in range(1 + vib.loop_times):
                for i in range(vib.loop_start, vib.loop_end):
                    if self.stop_playback:
                        break
                    play(i)
                if self.stop_playback:
                    break
                msleep(rate)
                self.rumble_report(b"\x00\x01\x40\x40")
                msleep(vib.loop_wait * rate)
            for i in range(vib.loop_end, vib.samples):
                if self.stop_playback:
                    break
                play(i)
        msleep(rate)
        self.rumble_report(b"\x00\x01\x40\x40")
        msleep(rate + 120)
        self._vibration_off_and_leds()
        return 0

    # ------------------------------------------------------------------------------------
    # Debug custom command
    def send_custom_command(self, arg):
        """arg: 44 bytes: cmd, rumble (4), subcmd, 38 argument bytes. Shows the sent report
        and the reply through ui.custom_command; returns (sent, reply_cmd, reply)."""
        arg = bytearray(arg) + bytearray(max(0, 44 - len(arg)))
        cmd = bytearray(49)
        cmd[0] = arg[0]
        cmd[1] = self._timer()
        cmd[2] = cmd[6] = arg[1]
        cmd[3] = cmd[7] = arg[2]
        cmd[4] = cmd[8] = arg[3]
        cmd[5] = cmd[9] = arg[4]
        cmd[10] = arg[5]
        if arg[5] == 0x21:
            arg[43] = mcu_crc8(arg[7:43])

        def hexdump(values):
            out, sep = "", 1
            for v in values:
                out += "%02X " % v
                if sep == 4:
                    out += " "
                if sep == 8:
                    sep = 0
                    out += "\n"
                sep += 1
            return out

        sent = "Cmd:  %02X   Subcmd: %02X\n" % (cmd[0], cmd[10])
        if cmd[0] in (0x01, 0x10, 0x11):
            for i in range(6, 44):
                cmd[5 + i] = arg[i]
            sent += hexdump(cmd[11:49])
        else:
            for i in range(6, 44):
                cmd[i - 5] = arg[i]
            sent += hexdump(cmd[1:39])

        reply_sys = ""
        if self.write(cmd) < 0:
            reply_sys += "hid_write failed!\r\n\r\n"
        n, rb = 0, bytearray(0x170)
        for _ in range(20):
            n, rb = self.read(0x170, 64)
            if n > 0 and (arg[0] != 0x01 or rb[0] == 0x21):
                break
        reply_cmd = ""
        if n > 12:
            if rb[0] in (0x21, 0x30, 0x33, 0x31, 0x3F):
                reply_cmd += "\nInput report: 0x%02X\n" % rb[0] + hexdump(rb[1:13])
                length = 362 if rb[0] in (0x33, 0x31) else 49
                reply_sys += "Subcmd Reply:\n" + hexdump(rb[13:length])
                if arg[5] == 0x21:
                    reply_sys += "(CRC OK)" if rb[48] == mcu_crc8(rb[0xF:0xF + 33]) else "(Wrong CRC)"
            else:
                reply_sys += "ID: %02X Subcmd reply:\n" % rb[0] + hexdump(rb[13:n])
        elif n > 0:
            reply_sys += "".join("%02X " % rb[i] for i in range(n))
        else:
            reply_sys += "No reply"
        self.ui.custom_command(sent, reply_cmd, reply_sys)
        return sent, reply_cmd, reply_sys

    def silence_input_report(self):
        self._transact(lambda: self.packet(0x01, 0x03, bytes([0x3F])), lambda b: b[13] == 0x80 and b[14] == 0x03, attempts=4)
        return 0

    def set_input_report_mode(self, mode, wait=64):
        self._subcmd_simple(0x03, mode, wait=wait)

    # ------------------------------------------------------------------------------------
    # Button test
    def read_calibration_info(self):
        """Stick/6-axis calibration and device parameters, as text and as values."""
        from . import calibration
        return calibration.read_all(self)

    def button_test(self):
        """Live input until enable_button_test is cleared. ui.button_test gets the text of the
        report and sensors (and the raw report)."""
        cal = self.read_calibration_info()
        self.ui.button_test_info(cal.info_text())
        self._subcmd_simple(0x03, 0x30, wait=120)
        self._subcmd_simple(0x40, 0x01, wait=120)
        sc = cal.sensor_origin()
        acc_coeff = [1.0 / float(16384 - int16(sc[i])) * 4.0 * 9.8 for i in range(3)]
        gyro_coeff = [936.0 / float(13371 - int16(sc[3 + i])) * 0.01745329251994 for i in range(3)]
        xl, yl, xr, yr = cal.stick_ranges()
        limit_output = 0
        report = sensors = ""
        while self.enable_button_test:
            n, b = self.read(0x170, 200)
            if n > 12:
                if b[0] in (0x21, 0x30, 0x31, 0x32, 0x33):
                    conn = (b[2] >> 1) & 0x3
                    report = "Conn: BT" if conn == 3 else "Conn: USB" if conn == 0 else "Conn: %X?" % conn
                    report += "\nBatt: %X/4   " % (b[2] >> 5)
                    report += "Charging: Yes\n" if (b[2] >> 4) & 1 else "Charging: No\n"
                    report += "Vibration decision: %X, %X\n" % ((b[12] >> 7) & 1, (b[12] >> 4) & 7)
                    report += "\nButtons: " + "".join("%02X " % b[i] for i in range(3, 6))
                    if self.type != JOYCON_R:
                        x, y = b[6] | ((b[7] & 0xF) << 8), (b[7] >> 4) | (b[8] << 4)
                        cx, cy = analog_stick_calc(x, y, xl, yl)
                        report += "\n\nL Stick (Raw/Cal):\nX:   %03X   Y:   %03X\nX: %5.2f   Y: %5.2f\n" % (x, y, cx, cy)
                    if self.type != JOYCON_L:
                        x, y = b[9] | ((b[10] & 0xF) << 8), (b[10] >> 4) | (b[11] << 4)
                        cx, cy = analog_stick_calc(x, y, xr, yr)
                        report += "\n\nR Stick (Raw/Cal):\nX:   %03X   Y:   %03X\nX: %5.2f   Y: %5.2f\n" % (x, y, cx, cy)
                    sensors = "Acc/meter (Raw/Cal):\n"
                    for i, axis in enumerate("XYZ"):
                        raw = u16le(b, 13 + i * 2)
                        sensors += "%s: %04X  %7.2f m/s²\n" % (axis, raw, int16(raw) * acc_coeff[i])
                    sensors += "\nGyroscope (Raw/Cal):\n"
                    for i, axis in enumerate("XYZ"):
                        raw = u16le(b, 19 + i * 2)
                        sensors += "%s: %04X  %7.2f rad/s\n" % (axis, raw, (int16(raw) - int16(sc[3 + i])) * gyro_coeff[i])
                elif b[0] == 0x3F:
                    report = "".join("%02X " % b[i] for i in range(17))
                if limit_output == 1:
                    self.ui.button_test(report, sensors, bytes(b[:49]))
                elif limit_output > 4:
                    limit_output = 0
                limit_output += 1
            self.ui.poll()
        self._subcmd_simple(0x03, 0x3F)
        self._subcmd_simple(0x40, 0x00)
        return 0

    # ------------------------------------------------------------------------------------
    # IR camera
    def ir_setup_reads(self):
        return 19 if self.ir_patient_setup else 8

    def ir_image_size(self, mode):
        if mode != 0x07:
            return 320, 240
        m = self.ir_max_frag_no
        return (160, 120) if m == 0x3F else (80, 60) if m == 0x0F else (40, 30) if m == 0x03 else (320, 240)

    def _regs(self, buf, regs):
        """Writes (address, value) register pairs of an 0x21 0x23 0x04 subcommand."""
        buf[13] = len(regs)
        for i, (addr, val) in enumerate(regs):
            struct.pack_into("<HB", buf, 14 + i * 3, addr, val & 0xFF)

    def ir_sensor_auto_exposure(self, white_pixels_percent):
        old = self.ir_exposure_value
        if white_pixels_percent == 0:
            old += 10
        elif white_pixels_percent > 5:
            old -= (white_pixels_percent // 4) * 20
        old = min(max(old, 0), 600)
        self.ir_exposure_value = old
        self.ui.ir_exposure(old)
        new = old * 31200 // 1000
        buf = self.packet(0x01, 0x21, bytes([0x23, 0x04]))
        self._regs(buf, [(0x3001, new & 0xFF), (0x3101, (new >> 8) & 0xFF), (0x0700, 0x01)])
        buf[48] = mcu_crc8(buf[12:48])
        return self.write(buf)

    @staticmethod
    def _vertical_roughness(image, pixels, width):
        total = 0
        for i in range(pixels - width):
            total += abs(image[i] - image[i + width])
        return total / float(pixels - width)

    def ir_frame_has_other_width(self, image, max_frag_no=None):
        """True when a frame's pixels line up as rows of another resolution's width, i.e.
        the camera kept its previous resolution. A real image is smoothest at its own width."""
        m = self.ir_max_frag_no if max_frag_no is None else max_frag_no
        pixels = (m + 1) * 300
        width = 160 if m == 0x3F else 80 if m == 0x0F else 40 if m == 0x03 else 320
        if width == 320:
            return False
        own = self._vertical_roughness(image, pixels, width)
        if own < 1.0:
            return False                   # (Almost) uniform: nothing to tell by
        for other in (320, 160, 80, 40):
            if other == width or pixels < 3 * other:
                continue
            if self._vertical_roughness(image, pixels, other) < own * 0.5:
                return True
        return False

    @staticmethod
    def _draw_cluster(image, data):
        brightness = data[1] & 0xFF
        x0, x1, y0, y1 = data[4], data[5], data[6], data[7]
        cx, cy = (data[2] + 32) // 64, (data[3] + 32) // 64
        if x1 < x0 or y1 < y0 or x1 >= 320 or y1 >= 240 or not (x0 <= cx <= x1) or not (y0 <= cy <= y1):
            return
        for y in (y0, y1, cy):
            for x in range(x0, x1 + 1):
                image[y * 320 + x] = brightness
        for x in (x0, x1, cx):
            for y in range(y0, y1 + 1):
                image[y * 320 + x] = brightness

    def get_raw_ir_image(self, mode, show_status):
        sw = time.monotonic()
        elapsed = elapsed2 = 0
        image = bytearray(19 * 4096)
        previous_frag_no = got_frag_no = missed_packet_no = 0
        missed_packet = False
        initialization = 2
        frag_seen = bytearray(256)
        incomplete_retries = 0
        exposure_adjusted = False
        frames_done = 0
        stream_checks = stream_stuck_frames = 0
        first_frame_stats = -1
        self.ir_last_capture_stale = False
        quick = self.ir_quick_capture and not self.enable_ir_video
        self.note("IR: quick capture %s, skip leftover %s, patient setup %s" % (
            "on" if quick else "off", "on" if self.ir_skip_leftover else "off", "on" if self.ir_patient_setup else "off"))
        skip_tried = skip_pending = False
        leftover_frag0 = b""
        leftover_stats = 0
        max_pixels = 218 * 300               # White pixels are counted on the full sensor
        mf = self.ir_max_frag_no
        width, height = self.ir_image_size(mode)

        def ms():
            return int((time.monotonic() - sw) * 1000)

        buf = bytearray(49)
        buf[0] = 0x11
        buf[10] = 0x03
        buf[48] = 0xFF

        def ack(frag=None, request_missed=None):
            buf[1] = self._timer()
            if request_missed is not None:
                buf[12], buf[13], buf[14] = 0x01, request_missed & 0xFF, 0
            elif frag is not None:
                buf[14] = frag & 0xFF
            buf[47] = mcu_crc8(buf[11:47])
            self.write(buf)
            buf[12] = buf[13] = 0

        def status_text(frag):
            if initialization < 2:
                s = "Status: Streaming.. " if show_status == 2 else "Status: Receiving.. "
            else:
                s = "Status: Initializing.. "
            return s + "%3.0f%% - " % (frag / float(mf + 1) * 100.0)

        def stats_of(r):
            return (r[53] << 32) | (r[54] << 16) | u16le(r, 55)

        ack(0)                                # First ack
        while self.enable_ir_video or initialization != 0:
            if self.ir_pending_live is not None:   # Live config from the window, between reports
                cfg, self.ir_pending_live = self.ir_pending_live, None
                self.ir_sensor_config_live(cfg)
            n, r = self.read(0x170, 200)
            if r[0] == 0x31 and r[49] == 0x03 and r[51] == mode:
                got_frag_no = r[52]
                frame_done = False
                if skip_pending and mode == 0x07:
                    skip_pending = False
                    same = got_frag_no == 0 and bytes(r[59:359]) == leftover_frag0
                    if got_frag_no == 0 and not same:
                        self.note("IR: skip leftover worked, a new frame started")
                        initialization = 1
                        frames_done = 1
                        first_frame_stats = leftover_stats
                        frag_seen = bytearray(256)
                        previous_frag_no = mf
                    else:
                        self.note("IR: skip leftover ignored (got fragment %d%s)" % (got_frag_no, ", the same data" if same else ""))
                if got_frag_no == (previous_frag_no + 1) % (mf + 1) or mode != 0x07:
                    previous_frag_no = got_frag_no
                    ack(previous_frag_no)
                    if mode in (0x06, 0x04):
                        image[:320 * 240] = bytes(320 * 240)
                        i = 61
                        while i + 16 <= 59 + 300:
                            if mode == 0x04 and i in (61 + 48, 61 + 97, 61 + 146, 61 + 195, 61 + 244):
                                i += 1   # Weird data arrangement in pointing mode
                            if r[i] != 0 or r[i + 1] != 0:
                                self._draw_cluster(image, struct.unpack_from("<8H", r, i))
                            i += 16
                    elif mode == 0x07:
                        image[300 * got_frag_no:300 * got_frag_no + 300] = r[59:359]
                        frag_seen[got_frag_no] = 1
                        # Auto exposure: once per capture while it waits for a complete frame
                        # (the Joy-Con answers the change in place of a fragment); every
                        # frame when streaming. Quick capture skips it.
                        if (self.enable_ir_auto_exposure and not quick and initialization < 2 and got_frag_no == 0
                                and (initialization == 0 or not exposure_adjusted)):
                            self.ir_sensor_auto_exposure(u16le(r, 55) * 100 // max_pixels)
                            exposure_adjusted = True
                        self.ui.ir_status(status_text(got_frag_no) + "%dms" % (ms() - elapsed))
                        elapsed = ms()
                    frame_done = got_frag_no == mf or mode != 0x07
                elif got_frag_no != 0 or previous_frag_no != 0:
                    if got_frag_no == previous_frag_no:
                        ack(got_frag_no)
                        missed_packet = False
                    elif missed_packet_no != got_frag_no and not missed_packet:
                        if mf != 0x03:
                            # Request the missed fragment: send what the next one will be
                            ack(request_missed=previous_frag_no + 1)
                            image[300 * got_frag_no:300 * got_frag_no + 300] = r[59:359]
                            frag_seen[got_frag_no] = 1
                            previous_frag_no = got_frag_no
                            missed_packet_no = got_frag_no - 1
                            missed_packet = True
                        else:
                            ack(got_frag_no)   # 30x40: don't request it
                            image[300 * got_frag_no:300 * got_frag_no + 300] = r[59:359]
                            frag_seen[got_frag_no] = 1
                            previous_frag_no = got_frag_no
                    elif missed_packet_no == got_frag_no:
                        ack(got_frag_no)
                        image[300 * got_frag_no:300 * got_frag_no + 300] = r[59:359]
                        frag_seen[got_frag_no] = 1
                        previous_frag_no = got_frag_no
                        missed_packet = False
                    else:
                        ack(got_frag_no)
                    self.ui.ir_status(status_text(got_frag_no) + "%dms" % (ms() - elapsed))
                    elapsed = ms()
                else:
                    # Streaming start
                    frag = got_frag_no
                    if self.ir_skip_leftover and not skip_tried and mode == 0x07 and initialization == 2 and got_frag_no == 0:
                        skip_tried = skip_pending = True
                        leftover_frag0 = bytes(r[59:359])
                        leftover_stats = stats_of(r)
                        frag = mf
                    ack(frag)
                    buf[14] = got_frag_no
                    image[300 * got_frag_no:300 * got_frag_no + 300] = r[59:359]
                    frag_seen[got_frag_no] = 1
                    self.ui.ir_status("%dms" % (ms() - elapsed))
                    elapsed = ms()
                    previous_frag_no = 0

                if frame_done:
                    elapsed2 = ms() - elapsed2
                    self.ui.ir_frame(bytes(image[:width * height]), width, height)
                    white = u16le(r, 55)
                    noise = u16le(r, 57) / (white + 1.0)
                    self.ui.ir_help("Amb Noise: %.2f,  Int: %d%%,  FPS: %d (%dms)\nEXFilter: %d,  White Px: %d%%,  EXF Int: %d" % (
                        noise, r[53] * 100 // 255, 1000 // elapsed2 if elapsed2 > 0 else 0, elapsed2,
                        u16le(r, 57), white * 100 // max_pixels, r[54]))
                    elapsed2 = ms()
                    missing = 0
                    if mode == 0x07:
                        missing = sum(1 for i in range(mf + 1) if not frag_seen[i])
                        if missing:
                            self.note("IR: frame done with %d fragment(s) not received" % missing)
                    frag_seen = bytearray(256)
                    self.ir_last_frame_missing = missing
                    if initialization != 0:
                        # Don't finish a capture on an incomplete frame (a few retries at most)
                        if initialization == 1 and missing > 0 and incomplete_retries < 3:
                            incomplete_retries += 1
                        else:
                            initialization -= 1
                        # The camera must have applied the capture's settings: the first frame of
                        # a run is a leftover from before; a stuck camera repeats it or sends rows
                        # of its previous resolution.
                        frame_stats = stats_of(r)
                        frames_done += 1
                        if frames_done == 1:
                            first_frame_stats = frame_stats
                        elif initialization == 0 and mode == 0x07 and not self.enable_ir_video:
                            if frame_stats == first_frame_stats:
                                self.ir_last_capture_stale = True
                                self.note("IR: the saved frame repeats the leftover first frame")
                            elif self.ir_frame_has_other_width(image, mf):
                                self.ir_last_capture_stale = True
                                self.note("IR: the saved frame has rows of another resolution")
                    # The same check for a stream's first frames: two stuck frames stop it
                    if self.enable_ir_video and mode == 0x07 and frames_done >= 2 and stream_checks < 6:
                        stream_checks += 1
                        if self.ir_frame_has_other_width(image, mf):
                            stream_stuck_frames += 1
                            if stream_stuck_frames >= 2:
                                self.ir_last_capture_stale = True
                                self.note("IR: the stream has rows of another resolution, restarting it")
                                break
                        else:
                            stream_stuck_frames = 0
            elif r[0] == 0x31:
                # Empty IR report: ACK again (else it falls back to 30ms per fragment)
                buf[1] = self._timer()
                if r[49] == 0xFF:
                    buf[14] = previous_frag_no & 0xFF
                elif r[49] == 0x00:
                    buf[12], buf[13], buf[14] = 0x01, (previous_frag_no + 1) & 0xFF, 0
                buf[47] = mcu_crc8(buf[11:47])
                self.write(buf)
                buf[12] = buf[13] = 0
            self.ui.poll()
        return 0

    def _ir_step(self, build, match, fail_code, attempts=7):
        """One setup step of ir_sensor/nfc: None when it worked, else the error code."""
        r = self._transact(build, match, reads=self.ir_setup_reads(), attempts=attempts, read_len=0x170)
        return None if r is not None else fail_code

    def _mcu_on_and_mode(self, mcu_mode, reads):
        """Input report 0x31, MCU on, standby, set MCU mode. None or the error code (1-5)."""
        def pk(cmd, subcmd, args=b""):
            return lambda: self.packet(cmd, subcmd, args, size=0x170)
        steps = [
            (pk(0x01, 0x03, bytes([0x31])), lambda b: b[13] == 0x80 and b[14] == 0x03, 1),
            (pk(0x01, 0x22, bytes([0x01])), lambda b: b[13] == 0x80 and b[14] == 0x22, 2),
            (pk(0x11, 0x01), lambda b: b[0] == 0x31 and b[49] == 0x01 and b[56] == 0x01, 3),
        ]
        for build, match, code in steps:
            if self._transact(build, match, reads=reads, attempts=7, read_len=0x170) is None:
                return code

        def set_mode():
            buf = self.packet(0x01, 0x21, bytes([0x21, 0x00, mcu_mode]), size=0x170)
            buf[48] = mcu_crc8(buf[12:48])
            return buf
        if mcu_mode == 0x05:
            match = lambda b: b[0] == 0x21 and b[15] == 0x01 and struct.unpack_from("<I", b, 22)[0] == 0x01
        else:
            match = lambda b: b[0] == 0x21 and b[15] == 0x01 and b[22] == 0x01
        if self._transact(set_mode, match, reads=reads, attempts=7, read_len=0x170) is None:
            return 4
        if self._transact(pk(0x11, 0x01), lambda b: b[0] == 0x31 and b[49] == 0x01 and b[56] == mcu_mode,
                          reads=reads, attempts=7, read_len=0x170) is None:
            return 5
        return None

    def _mcu_off(self, reads):
        buf = self.packet(0x01, 0x22, bytes([0x00]), size=0x170)
        self.write(buf)
        self.read(0x170, 64)
        self._transact(lambda: self.packet(0x01, 0x03, bytes([0x3F]), size=0x170),
                       lambda b: b[13] == 0x80 and b[14] == 0x03, reads=reads, attempts=7, read_len=0x170)

    def ir_sensor(self, cfg):
        """Sets the IR camera up and captures (enable_ir_video False) or streams until
        enable_ir_video is cleared. 0, or an error code 1-9."""
        reads = self.ir_setup_reads()
        res_get = self._mcu_on_and_mode(0x05, reads)
        if res_get is None:
            res_get = self._ir_configure(cfg, reads)
        if res_get is None:
            res_get = self.get_raw_ir_image(cfg.ir_mode, 2 if self.enable_ir_video else 1)
        self._mcu_off(reads)
        return res_get

    def _ir_configure(self, cfg, reads):
        # IR mode and number of fragments per frame (IR MCU FW v5.18)
        def ir_mode():
            buf = self.packet(0x01, 0x21, bytes([0x23, 0x01, cfg.ir_mode, self.ir_max_frag_no]), size=0x170)
            struct.pack_into("<HH", buf, 15, 0x0500, 0x1800)
            buf[48] = mcu_crc8(buf[12:48])
            return buf
        if self._transact(ir_mode, lambda b: b[0] == 0x21 and b[15] == 0x0B, reads=reads, attempts=7, read_len=0x170) is None:
            return 6

        expected = 0x02 if cfg.ir_mode == 0x04 else cfg.ir_mode

        def regs1():
            buf = self.packet(0x01, 0x21, bytes([0x23, 0x04]), size=0x170)
            self._regs(buf, [
                (0x2E00, cfg.ir_res_reg),                        # Resolution (binning/skipping)
                (0x3001, cfg.ir_exposure & 0xFF),                # Exposure LSB
                (0x3101, (cfg.ir_exposure >> 8) & 0xFF),         # Exposure MSB
                (0x3201, 0x00),                                  # Manual exposure
                (0x1000, cfg.ir_leds),                           # IR led groups
                (0x2E01, (cfg.ir_digital_gain & 0xF) << 4),      # Digital gain LSB
                (0x2F01, (cfg.ir_digital_gain & 0xF0) >> 4),     # Digital gain MSB
                (0x0E00, cfg.ir_ex_light_filter),                # External light filter
                (0x4301, 0xC8),                                  # ExLF / white pixel threshold
            ])
            buf[48] = mcu_crc8(buf[12:48])
            self.write(buf)
            # Request the IR mode status before waiting for the x21 ack
            buf = self.packet(0x11, 0x03, bytes([0x02]), size=0x170)
            buf[47] = mcu_crc8(buf[11:47])
            buf[48] = 0xFF
            return buf
        if self._transact(regs1, lambda b: b[0] == 0x21 and b[15] == 0x13 and b[16] == 0 and b[17] == expected,
                          reads=reads, attempts=7, read_len=0x170) is None:
            return 8

        def regs2():
            buf = self.packet(0x01, 0x21, bytes([0x23, 0x04]), size=0x170)
            self._regs(buf, [
                (0x1100, (cfg.ir_leds_intensity >> 8) & 0xFF),   # Leds 1/2 intensity
                (0x1200, cfg.ir_leds_intensity & 0xFF),          # Leds 3/4 intensity
                (0x2D00, cfg.ir_flip),                           # Flip
                (0x6701, (cfg.ir_denoise >> 16) & 0xFF),         # De-noise on/off
                (0x6801, (cfg.ir_denoise >> 8) & 0xFF),          # Edge smoothing
                (0x6901, cfg.ir_denoise & 0xFF),                 # Color interpolation
                (0x0400, 0x2D if cfg.ir_res_reg == 0x69 else 0x32),  # Buffer update time
                (0x0700, 0x01),                                  # Finalize config
            ])
            buf[48] = mcu_crc8(buf[12:48])
            return buf

        def regs2_ok(b):
            if b[0] != 0x21:
                return False
            if b[15] == 0x13 and ((b[16] == 0 and b[17] == expected) or (b[50] == 0 and b[51] == cfg.ir_mode)):
                return True
            return b[15] == 0x23        # The reply to the earlier status request came first
        if self._transact(regs2, regs2_ok, reads=reads, attempts=7, read_len=0x170) is None:
            return 9
        return None

    def ir_sensor_config_live(self, cfg):
        """Changes exposure, leds, gain, filter, flip, de-noise and a custom register while
        streaming."""
        buf = self.packet(0x01, 0x21, bytes([0x23, 0x04]))
        custom_addr = ((cfg.ir_custom_register & 0xFF) << 8) | ((cfg.ir_custom_register >> 8) & 0xFF)
        self._regs(buf, [
            (0x3001, cfg.ir_exposure & 0xFF),
            (0x3101, (cfg.ir_exposure >> 8) & 0xFF),
            (0x1000, cfg.ir_leds),
            (0x2E01, (cfg.ir_digital_gain & 0xF) << 4),
            (0x2F01, (cfg.ir_digital_gain & 0xF0) >> 4),
            (0x0E00, cfg.ir_ex_light_filter),
            (custom_addr, (cfg.ir_custom_register >> 16) & 0xFF),
            (0x1100, (cfg.ir_leds_intensity >> 8) & 0xFF),
            (0x1200, cfg.ir_leds_intensity & 0xFF),
        ])
        buf[48] = mcu_crc8(buf[12:48])
        self.write(buf)
        msleep(15)                           # Otherwise a packet gets dropped
        buf[1] = self._timer()
        buf[14:48] = bytes(34)
        self._regs(buf, [
            (0x2D00, cfg.ir_flip),
            (0x6701, (cfg.ir_denoise >> 16) & 0xFF),
            (0x6801, (cfg.ir_denoise >> 8) & 0xFF),
            (0x6901, cfg.ir_denoise & 0xFF),
            (0x0400, 0x2D if cfg.ir_res_reg == 0x69 else 0x32),
            (0x0700, 0x01),
        ])
        buf[48] = mcu_crc8(buf[12:48])
        return self.write(buf)

    # ------------------------------------------------------------------------------------
    # NFC (kudos to Eric Betts, https://github.com/bettse, for the NFC starters)
    def nfc_tag_info(self):
        """Scans until a tag is read or enable_nfc_scanning is cleared. 0, or an error code."""
        res_get = self._mcu_on_and_mode(0x04, 8)
        if res_get is None:
            res_get = self._nfc_read()
        buf = self.packet(0x01, 0x22, bytes([0x00]), size=0x170)
        self.write(buf)
        self.read(0x170, 64)
        self._transact(lambda: self.packet(0x01, 0x03, bytes([0x3F]), size=0x170),
                       lambda b: b[13] == 0x80 and b[14] == 0x03, attempts=7, read_len=0x170)
        return res_get or 0

    def _nfc_cmd(self, arg1, data=b"", length_byte=None):
        buf = self.packet(0x11, 0x02, bytes([arg1, 0x00]), size=0x170)
        buf[13] = 0x00
        buf[14] = 0x08                        # Last command packet
        buf[15] = len(data) if length_byte is None else length_byte
        buf[16:16 + len(data)] = data
        buf[47] = mcu_crc8(buf[11:47])
        self.write(buf, 48)

    def _nfc_read(self):
        tag_uid_size = tag_type = 0
        ntag = bytearray(924)
        ntag_pos = 0
        ntag_pages = 0
        ntag_init_done = False
        last_poll_reply = bytearray(0x170)
        detected = False
        while True:
            # Step 5: NFC status, wait until it's ready for a command
            ready = False
            for _ in range(10):
                self._nfc_cmd(0x04)
                retries = 0
                busy = False
                while True:
                    n, b = self.read(0x170, 64)
                    if b[0] == 0x31 and b[49] == 0x2A and u16le(b, 50) == 0x0500 and b[55] == 0x31:
                        if b[56] == 0x0B:
                            busy = True
                            break
                        if b[56] == 0x00:
                            ready = True
                            break
                    retries += 1
                    if retries > 4 or n == 0:
                        break
                if ready:
                    break
            if not ready:
                return 6
            # Step 6: start polling (Mifare support on)
            found = False
            error_reading = 0
            while not found:
                self._nfc_cmd(0x01, bytes([0x01, 0x00, 0x00, 0x2C, 0x01]))
                retries = 0
                while True:
                    if not self.enable_nfc_scanning:
                        if not detected:
                            return 0          # Stopped before a tag was there: nothing to read
                        found = True          # Stop requested: go on to reading, like the original
                        break
                    n, b = self.read(0x170, 64)
                    if b[0] == 0x31:
                        last_poll_reply = b
                        if b[49] == 0x2A and u16le(b, 50) == 0x0500 and b[56] == 0x09:   # Tag detected
                            tag_uid_size = b[64]
                            tag_type = b[62]
                            uid = ":".join("%02X" % b[65 + i] for i in range(min(tag_uid_size, 10)))
                            self.ui.nfc_uid("UID:  %s\nType: %s" % (uid, "NTAG" if b[62] == 0x2 else "MIFARE"))
                            self.ui.poll()
                            found = detected = True
                            break
                        elif b[49] == 0x2A:
                            break
                    retries += 1
                    if retries > 4 or n == 0:
                        self.ui.poll()
                        break
                if found:
                    break
                error_reading += 1
                if error_reading > 100:
                    self.ui.nfc_tag("Tag lost!" if ntag_init_done else "No Tag detected!")
                    return 7
            if detected and tag_type != 0x02:
                # Only NTAG contents can be read (the original tried anyway, then failed
                # with error 8): the UID is all there is
                self.note("NFC: tag type %02X is not NTAG: UID only" % tag_type)
                self.ui.nfc_tag("This is a MIFARE tag: its UID is above.\n"
                                "Reading the contents of MIFARE tags isn't supported.")
                return 0
            # Step 7: read the NTAG contents
            error_reading = 0
            restart = False
            while True:
                req = bytearray(0x13)
                req[0], req[1] = 0xD0, 0x07   # Unknown (UID length?)
                if ntag_pages == 0:
                    req[10] = 0x01
                elif ntag_pages == 45:        # NTAG213
                    req[10:19] = bytes([0x01, 0x00, 0x2C, 0, 0, 0, 0, 0, 0])
                elif ntag_pages == 135:       # NTAG215
                    req[10:19] = bytes([0x03, 0x00, 0x3B, 0x3C, 0x77, 0x78, 0x86, 0, 0])
                elif ntag_pages == 231:       # NTAG216
                    req[10:19] = bytes([0x04, 0x00, 0x3B, 0x3C, 0x77, 0x78, 0xB3, 0xB4, 0xE6])
                buf2 = self.packet(0x11, 0x02, bytes([0x06, 0x00]), size=0x170)
                buf2[12] = 0x00
                buf2[13] = 0x00
                buf2[14] = 0x08
                buf2[15] = 0x13
                buf2[16:16 + 0x13] = req
                buf2[47] = mcu_crc8(buf2[11:47])
                self.write(buf2, 48)
                retries = 0
                while True:
                    n, b2 = self.read(0x170, 64)
                    if b2[0] == 0x31:
                        if b2[49] in (0x3A, 0x2A) and b2[56] == 0x07:
                            self.ui.nfc_tag("Error %02X!" % b2[50])
                            return 8
                        elif b2[49] == 0x3A and b2[51] == 0x07:
                            if ntag_init_done:
                                payload = ((b2[54] << 8) | b2[55]) & 0x7FF
                                if b2[52] == 0x01:
                                    ln = min(payload - 60, 0x170 - 116, 924 - ntag_pos)
                                    if ln > 0:
                                        ntag[ntag_pos:ntag_pos + ln] = b2[116:116 + ln]
                                        ntag_pos += ln
                                else:
                                    ln = min(payload, 0x170 - 56, 924 - ntag_pos)
                                    if ln > 0:
                                        ntag[ntag_pos:ntag_pos + ln] = b2[56:56 + ln]
                            elif b2[52] == 0x01 and tag_type == 2:
                                ntag_pages = {0: 135, 3: 45, 4: 231}.get(b2[74])
                                if ntag_pages is None:
                                    return 0
                            break
                        elif b2[49] == 0x2A and b2[56] == 0x04:     # Finished
                            if ntag_init_done:
                                self.ui.ntag_contents(bytes(ntag), ntag_pages)
                                self.ui.poll()
                                return 0
                            ntag_init_done = True
                            self._nfc_cmd(0x02)                     # Stop polling
                            msleep(200)
                            restart = True
                            break
                        elif b2[49] == 0x2A:
                            break
                    retries += 1
                    if retries > 4 or n == 0:
                        break
                if restart:
                    break
                error_reading += 1
                if error_reading > 9:
                    if last_poll_reply[62] == 0x4:
                        self.ui.nfc_tag("Mifare reading is not supported for now..")
                    return 8

    # ------------------------------------------------------------------------------------
    def disconnect(self):
        """Turns the controller's Bluetooth connection off."""
        buf = self.packet(0x01, 0x06, bytes([0x00]))
        self.write(buf)
        self.read(0, 64)
