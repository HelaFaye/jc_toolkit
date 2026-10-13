"""An emulated Joy-Con / Pro Controller, for the tests and the demo mode (no hardware).

It answers the subcommands the toolkit uses the way a real controller does (see
dekuNukem/Nintendo_Switch_Reverse_Engineering), backed by a 512KB SPI flash image. The IR
camera is emulated in image transfer mode (a test pattern), with the quirks seen on a real
Joy-Con (R): the first frame of a run is a leftover, a register write during a transfer
replaces a fragment, and optionally a camera that keeps its previous resolution. Rumble is
only felt after vibration is enabled (subcommand 0x48), like on a real controller.

Same interface as a hidapi device: write(data) and read(length, timeout_ms).
"""
import collections
import struct
import threading
import time

from .hidio import Device, Found, JOYCON_L, JOYCON_R, PROCON

PRODUCT_IDS = {JOYCON_L: 0x2006, JOYCON_R: 0x2007, PROCON: 0x2009}


class FakeJoyCon:
    MAC = bytes([0x98, 0xB6, 0xE9, 0x12, 0x34, 0x56])

    def __init__(self, type_=JOYCON_R):
        self.type = type_
        self.product_id = PRODUCT_IDS[type_]
        self.spi = bytearray(b"\xFF" * 0x80000)
        self._lock = threading.Lock()
        self.replies = collections.deque()
        self.input_mode = 0x3F
        self.imu_on = False
        self.timer = 0
        self.tick = 0
        self.mcu_state = 0          # 0: off, 1: standby, 4: NFC, 5: IR
        self.nfc_tag = None         # (tag type byte, UID) on the NFC area: 0x02 NTAG, 0x04 MIFARE
        self.nfc_reads = 0
        self.ir_mode = 0
        self.ir_max_frag = 0
        self.ir_frames_sent = 0
        self.ir_frame_index = 0
        self.ir_white_pixels = 0
        self.ir_stale_captures = 0  # Runs that repeat the leftover frame
        self.ir_stuck_captures = 0  # Runs that keep sending 240x320 rows with real stats
        self.ir_stuck_run = self.ir_stale_run = False
        self.ir_mode_sets = 0
        self.ir_register_writes = 0
        self.ir_fragment_delay_ms = 0
        self.ir_skip_fragment = -1  # >= 0: skip this fragment once, in the frame after the first
        self.ir_ignore_resend = False
        self.ir_last_frag = 0
        self.ir_skip_after_register_write = False
        self.ir_skipped_for_register_write = False
        self.writes = 0
        self.spi_writes = 0
        self.stick_raw = None       # (x, y) raw 12-bit, both sticks (None: centered)
        self.imu_raw = None         # acc X, Y, Z, gyro X, Y, Z (None: a test pattern)
        self.rumbles = []           # Left rumble bytes of every report felt
        self.vibration = False
        self.closed = False
        self._last_report = 0.0
        self._clock = time.monotonic

        put = self._put
        put(0x0000, [0x01, 0x08, 0x00, 0xF0, 0x00, 0x00, 0x62, 0x08, 0xC0, 0x5D, 0x89, 0xFD, 0x04, 0x00, 0xFF, 0xFF,
                     0xFF, 0xFF, 0x40, 0x06])
        for i in range(6):
            self.spi[0x1A - i] = self.MAC[i]      # Stored reversed
        put(0x10000, [0x0A, 0xFB, 0x00, 0x00, 0x02, 0x0D])
        put(0x6000, [0x00, 0x00])
        sn = b"\0" * 14 if type_ == PROCON else b"XAW70012345678"
        put(0x6002, sn)
        put(0x6012, [type_, 0xA0])
        put(0x6020, [0xD3, 0xFF, 0xD5, 0xFF, 0x55, 0x01, 0x00, 0x40, 0x00, 0x40, 0x00, 0x40,
                     0x19, 0x00, 0xDD, 0xFF, 0xDC, 0xFF, 0x3B, 0x34, 0x3B, 0x34, 0x3B, 0x34])
        put(0x603D, [0xBA, 0xF5, 0x62, 0x6F, 0xC8, 0x77, 0xED, 0x95, 0x5B,
                     0x16, 0xD8, 0x7D, 0xF2, 0xB5, 0x5F, 0x86, 0x65, 0x5E])
        if type_ == PROCON:
            put(0x6050, [0x32, 0x32, 0x32, 0xFF, 0xFF, 0xFF, 0x32, 0x32, 0x32, 0x32, 0x32, 0x32])
        elif type_ == JOYCON_R:
            put(0x6050, [0xFF, 0x3C, 0x28, 0x1E, 0x0A, 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])
        else:
            put(0x6050, [0x0A, 0xB9, 0xE6, 0x00, 0x1E, 0x1E, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])
        put(0x6080, [0x50, 0xFD, 0x00, 0x00, 0xC6, 0x0F])
        params = [0x0F, 0x30, 0x61, 0x96, 0x30, 0xF3, 0xD4, 0x14, 0x54, 0x41, 0x15, 0x54, 0xC7, 0x79, 0x9C, 0x33, 0x36, 0x63]
        put(0x6086, params)
        put(0x6098, params)

    def _put(self, offset, data):
        self.spi[offset:offset + len(data)] = bytes(data)

    def device(self):
        """A jctool.hidio.Device on this controller."""
        return Device(self, self.type, "fake")

    def found(self):
        return Found("fake", self.type, "Emulated " + {JOYCON_L: "Joy-Con (L)", JOYCON_R: "Joy-Con (R)", PROCON: "Pro Controller"}[self.type])

    # ------------------------------------------------------------------------------------
    def _report(self, rid, length):
        r = bytearray(length)
        r[0] = rid
        r[1] = self.timer
        self.timer = (self.timer + 1) & 0xFF
        r[2] = 0x8E                  # Battery full, Bluetooth
        r[6:12] = bytes([0x6F, 0x8C, 0x77, 0xF2, 0xD5, 0x7D])   # Sticks at their centers
        pos = self.stick_raw
        if pos is not None:
            x, y = pos
            for o in (6, 9):
                r[o] = x & 0xFF
                r[o + 1] = ((x >> 8) & 0xF) | ((y & 0xF) << 4)
                r[o + 2] = (y >> 4) & 0xFF
        return r

    def _ack(self, subcmd, ack, data=b""):
        r = self._report(0x21, 49)
        r[13] = ack
        r[14] = subcmd
        data = bytes(data)[:49 - 15]
        r[15:15 + len(data)] = data
        self.replies.append(r)

    def write(self, data):
        with self._lock:
            return self._write(bytearray(data))

    def _write(self, d):
        if self.closed:
            raise IOError("closed")
        self.writes += 1
        cmd = d[0]
        if cmd == 0x11:
            return self._mcu_write(d)
        if cmd == 0x01 and d[10] == 0x48:
            self.vibration = d[11] == 0x01
        if cmd in (0x10, 0x01) and self.vibration:
            self.rumbles.append(bytes(d[2:6]))
        if cmd != 0x01:
            return len(d)
        sub = d[10]
        if sub == 0x02:
            self._ack(0x02, 0x82, bytes([0x03, 0x89, self.type, 0x02]) + self.MAC + b"\x01\x01")
        elif sub == 0x03:
            self.input_mode = d[11]
            self._ack(0x03, 0x80)
        elif sub == 0x10:
            offset, size = struct.unpack_from("<IB", d, 11)
            data = self.spi[offset:offset + size] if offset + size <= len(self.spi) else bytes(size)
            self._ack(0x10, 0x90, struct.pack("<IB", offset, size) + data)
        elif sub == 0x11:
            offset, size = struct.unpack_from("<IB", d, 11)
            self.spi_writes += 1
            if offset + size <= len(self.spi):
                self.spi[offset:offset + size] = d[16:16 + size]
            self._ack(0x11, 0x80, b"\x00")
        elif sub == 0x40:
            self.imu_on = d[11] != 0
            self._ack(0x40, 0x80)
        elif sub == 0x43:
            if d[11] == 0x10:
                self._ack(0x43, 0xC0, bytes([d[11], d[12], 0x30 if self.imu_on else 0x00]))
            else:
                self._ack(0x43, 0xC0, bytes([d[11], d[12], 0x60, 0x00]))   # 31.0 C
        elif sub == 0x21:
            self._mcu_config(d)
        elif sub == 0x22:
            self.mcu_state = 1 if d[11] != 0 else 0
            self._ack(0x22, 0x80)
        elif sub == 0x50:
            self._ack(0x50, 0xD0, b"\x10\x06")    # 0x610: 3.88V
        else:
            self._ack(sub, 0x80)
        return len(d)

    def _mcu_report(self, kind):
        r = self._report(0x31, 362)
        r[49] = kind
        return r

    def _mcu_config(self, d):
        r = self._report(0x21, 49)
        r[13], r[14] = 0xA0, 0x21
        if d[11] == 0x21:                       # Set MCU mode
            if self.mcu_state != 0:
                self.mcu_state = d[13]
            r[15] = 0x01
            r[22] = 0x01
        elif d[11] == 0x23 and d[12] == 0x01:   # Set IR mode
            self.ir_mode = d[13]
            self.ir_max_frag = d[14]
            self.ir_frames_sent = self.ir_frame_index = 0
            self.ir_mode_sets += 1
            self.ir_stale_run = self.ir_stale_captures > 0
            self.ir_stale_captures = max(0, self.ir_stale_captures - 1)
            self.ir_stuck_run = self.ir_stuck_captures > 0
            self.ir_stuck_captures = max(0, self.ir_stuck_captures - 1)
            r[15] = 0x0B
        elif d[11] == 0x23 and d[12] == 0x04:   # Write IR registers
            self.ir_register_writes += 1
            if self.mcu_state == 5 and self.ir_frames_sent > 0:
                self.ir_skip_after_register_write = True
            r[15], r[16] = 0x13, 0x00
            r[17] = 0x02 if self.ir_mode == 0x04 else self.ir_mode
        self.replies.append(r)

    def _nfc_report(self, state):
        r = self._mcu_report(0x2A)
        r[50], r[51], r[55], r[56] = 0x00, 0x05, 0x31, state
        if self.nfc_tag is not None and state in (0x09, 0x02, 0x04):
            tag_type, uid = self.nfc_tag
            r[60], r[61], r[62], r[63], r[64] = 0x01, 0x01, tag_type, 0x00, len(uid)
            r[65:65 + len(uid)] = uid
        return r

    def _mcu_write(self, d):
        if d[10] == 0x01:                       # MCU status
            r = self._mcu_report(0x01)
            r[56] = self.mcu_state
            self.replies.append(r)
        elif d[10] == 0x02 and self.mcu_state == 4:   # NFC command (only tag detection)
            if d[11] == 0x04:                   # Status: ready for a command
                self.replies.append(self._nfc_report(0x00))
            elif d[11] == 0x01:                 # Start polling: tag detected, or polling
                self.replies.append(self._nfc_report(0x09 if self.nfc_tag is not None else 0x01))
            elif d[11] == 0x06:                 # Read: this emulation has no tag contents
                self.nfc_reads += 1
                self.replies.append(self._nfc_report(0x02))
            else:
                self.replies.append(self._nfc_report(0x00))
        elif d[10] == 0x03 and d[11] == 0x00 and self.mcu_state == 5 and self.ir_mode != 0:
            mf = self.ir_max_frag
            frag = d[13] if d[12] == 0x01 else (0 if self.ir_frames_sent == 0 else (d[14] + 1) % (mf + 1))
            if d[12] == 0x01 and (self.ir_ignore_resend or self.ir_skipped_for_register_write):
                frag = (self.ir_last_frag + 1) % (mf + 1)
            if frag == self.ir_skip_fragment and self.ir_frames_sent > mf + 1:
                frag += 1
                self.ir_skip_fragment = -1
            self.ir_skipped_for_register_write = False
            if self.ir_skip_after_register_write and d[12] != 0x01 and self.ir_frames_sent > 0:
                frag = (frag + 1) % (mf + 1)
                self.ir_skip_after_register_write = False
                self.ir_skipped_for_register_write = True
            if frag == 0 and self.ir_frames_sent > 0:
                self.ir_frame_index += 1        # A new frame starts
            self.ir_last_frag = frag
            r = self._mcu_report(0x03)
            r[50], r[51], r[52] = 0x00, self.ir_mode, frag
            placeholder = self.ir_frame_index == 0 or self.ir_stale_run
            r[53] = 31 if placeholder else 0x40
            white = 5600 if placeholder else self.ir_white_pixels
            r[55], r[56] = white & 0xFF, (white >> 8) & 0xFF
            if self.ir_mode == 0x07 and self.ir_stuck_run:
                for i in range(300):            # 320 pixel rows: left half dark, right half bright
                    r[59 + i] = 30 if ((frag * 300 + i) % 320) < 160 else 200
            elif self.ir_mode == 0x07:
                width = 160 if mf == 0x3F else 80 if mf == 0x0F else 40 if mf == 0x03 else 320
                height = (mf + 1) * 300 // width
                bump = 5 if self.ir_frame_index == 0 else 0
                for i in range(300):
                    x, y = (frag * 300 + i) % width, (frag * 300 + i) // width
                    r[59 + i] = 0xFF if (x % 20) < 2 else min(255, y * 200 // height + x * 50 // width + bump)
            if self.ir_fragment_delay_ms > 0:
                time.sleep(self.ir_fragment_delay_ms / 1000.0)
            self.replies.append(r)
            self.ir_frames_sent += 1
        return len(d)

    def read(self, length, timeout_ms):
        with self._lock:
            if self.closed:
                raise IOError("closed")
            if self.replies:
                r = self.replies.popleft()
                return list(r[:length])
            ir_idle = self.input_mode == 0x31 and self.mcu_state == 5 and self.ir_frames_sent > 0 and timeout_ms > 0
            full = self.input_mode == 0x30
        if ir_idle:
            # A real Joy-Con keeps sending reports (empty IR data) every ~15ms
            time.sleep(min(timeout_ms, 15) / 1000.0)
            with self._lock:
                return list(self._mcu_report(0xFF)[:length])
        if full:
            now = self._clock()
            if timeout_ms == 0 and (now - self._last_report) < 0.015:
                return []
            if timeout_ms != 0:
                time.sleep(0.015)
            with self._lock:
                self._last_report = self._clock()
                r = self._report(0x30, 49)
                self.tick += 1
                r[3] = 0x08 if (self.tick // 20) % 2 == 0 else 0x00   # Blink the A button
                for i in range(13, 49):
                    r[i] = (i * 7 + self.tick) & 0xFF
                imu = self.imu_raw
                if imu is not None:
                    for s in range(3):
                        for i in range(6):
                            struct.pack_into("<h", r, 13 + s * 12 + i * 2, imu[i])
                return list(r[:length])
        if timeout_ms > 0:
            time.sleep(min(timeout_ms, 5) / 1000.0)
        return []

    def close(self):
        self.closed = True
