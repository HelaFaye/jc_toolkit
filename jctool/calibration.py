"""Calibration: reading and writing the user calibration (sticks, 6-axis) and the stick
device parameters, plus the guided calibrations (Linux build additions):

- StickWizard: like the Switch's "Calibrate Control Sticks": the center with the stick at
  rest, the range while it's rotated along its edge.
- MotionWizard: like "Calibrate Motion Controls": gyro and accelerometer offsets with the
  controller lying still, optionally in two positions turned 180 degrees apart, which
  cancels the surface's tilt.

SPI layout: factory stick calibration at 0x603D (left 9 bytes, right 9), user stick
calibration at 0x8010 (left: magic B2 A1 + 9 bytes; right at 0x801B), factory 6-axis at
0x6020 (24 bytes: acc origin, acc sensitivity, gyro origin, gyro sensitivity), user 6-axis
at 0x8026 (magic + the same 24), stick device parameters at 0x6086 / 0x6098.
"""
import math

from .core import decode_stick_params, encode_stick_params, int16, u16le
from .hidio import JOYCON_L, JOYCON_R, PROCON

MAGIC = b"\xB2\xA1"


class StickCal:
    """One stick's calibration: min / center / max per axis (raw 12-bit)."""

    def __init__(self, x_minus=0, x_center=0x7FF, x_plus=0xFFF, y_minus=0, y_center=0x7FF, y_plus=0xFFF, valid=True):
        self.x_minus, self.x_center, self.x_plus = x_minus, x_center, x_plus
        self.y_minus, self.y_center, self.y_plus = y_minus, y_center, y_plus
        self.valid = valid

    def as_list(self):
        return [self.x_minus, self.x_center, self.x_plus, self.y_minus, self.y_center, self.y_plus]

    @classmethod
    def from_list(cls, r):
        return cls(*r[:6])

    def __repr__(self):
        return "StickCal(%s)" % ",".join(str(v) for v in self.as_list())


def decode_stick(d, left):
    """9 bytes. Left stick: max-above-center, center, min-below-center; right stick:
    center, min-below-center, max-above-center."""
    p = decode_stick_params(d[0:3]) + decode_stick_params(d[3:6]) + decode_stick_params(d[6:9])
    center, minus, plus = (2, 4, 0) if left else (0, 2, 4)
    return StickCal(p[center] - p[minus], p[center], p[center] + p[plus],
                    p[center + 1] - p[minus + 1], p[center + 1], p[center + 1] + p[plus + 1])


def encode_stick(c, left):
    """9 bytes (see decode_stick)."""
    center = encode_stick_params(c.x_center, c.y_center)
    plus = encode_stick_params(c.x_plus - c.x_center, c.y_plus - c.y_center)
    minus = encode_stick_params(c.x_center - c.x_minus, c.y_center - c.y_minus)
    return (plus + center + minus) if left else (center + minus + plus)


class CalibrationInfo:
    """Everything calibration-related read from the controller."""

    def __init__(self, type_, factory_stick, user_stick, factory_sensor, user_sensor, sensor_model, stick_model):
        self.type = type_
        self.factory_stick = factory_stick       # 18 bytes
        self.user_stick = user_stick             # 22 bytes
        self.factory_sensor = factory_sensor     # 24 bytes
        self.user_sensor = user_sensor           # 26 bytes
        self.sensor_model = sensor_model         # 6 bytes
        self.stick_model = stick_model           # 36 bytes

    @property
    def has_left(self):
        return self.type != JOYCON_R

    @property
    def has_right(self):
        return self.type != JOYCON_L

    def user_left(self):
        return decode_stick(self.user_stick[2:11], True) if self.user_stick[0:2] == MAGIC else None

    def user_right(self):
        return decode_stick(self.user_stick[13:22], False) if self.user_stick[11:13] == MAGIC else None

    def factory_left(self):
        return decode_stick(self.factory_stick[0:9], True)

    def factory_right(self):
        return decode_stick(self.factory_stick[9:18], False)

    def user_sensor_valid(self):
        return self.user_sensor[0:2] == MAGIC

    def sensor_values(self, user):
        d = self.user_sensor[2:26] if user else self.factory_sensor
        return [int16(u16le(d, i * 2)) for i in range(12)]

    def sensor_origin(self):
        """acc origin XYZ, gyro origin XYZ (user calibration when present) as raw u16."""
        d = self.user_sensor[2:26] if self.user_sensor_valid() else self.factory_sensor
        return [u16le(d, 0), u16le(d, 2), u16le(d, 4), u16le(d, 12), u16le(d, 14), u16le(d, 16)]

    def stick_ranges(self):
        """(x_left, y_left, x_right, y_right) as [min, center, max], user calibration first."""
        def rng(c):
            return [c.x_minus, c.x_center, c.x_plus], [c.y_minus, c.y_center, c.y_plus]
        left = self.user_left() or self.factory_left()
        right = self.user_right() or self.factory_right()
        xl, yl = rng(left) if self.has_left else ([0, 0, 0], [0, 0, 0])
        xr, yr = rng(right) if self.has_right else ([0, 0, 0], [0, 0, 0])
        return xl, yl, xr, yr

    def stick_params(self, second=False):
        """(deadzone, range ratio) of the main (or Pro right) stick."""
        return decode_stick_params(self.stick_model[0x15:0x18] if second else self.stick_model[3:6])

    def status(self):
        """("Factory" | "User (...)", tooltip text)"""
        left = self.has_left and self.user_stick[0:2] == MAGIC
        right = self.has_right and self.user_stick[11:13] == MAGIC
        imu = self.user_sensor_valid()
        parts = []
        if left:
            parts.append("L stick" if self.type == PROCON else "stick")
        if right:
            parts.append("R stick" if self.type == PROCON else "stick")
        if imu:
            parts.append("motion")
        text = "Factory" if not parts else "User (%s)" % ", ".join(parts)
        tip = "Calibration in use (a user calibration overrides the factory one):\n"
        if self.has_left:
            tip += "  %s: %s\n" % ("Left stick" if self.type == PROCON else "Stick", "user" if left else "factory")
        if self.has_right:
            tip += "  %s: %s\n" % ("Right stick" if self.type == PROCON else "Stick", "user" if right else "factory")
        tip += "  Motion (6-axis): %s" % ("user" if imu else "factory")
        return text, tip

    def info_text(self):
        """The button test's calibration panel (like the original)."""
        sm, st = self.sensor_model, self.stick_model
        t = "Flat surface ACC Offset:\n%04X %04X %04X\n\n\nStick Parameters:\n%03X %03X\n%02X (Deadzone)\n%03X (Range ratio)" % (
            u16le(sm, 0), u16le(sm, 2), u16le(sm, 4),
            ((st[1] << 8) & 0xF00) | st[0], (st[2] << 4) | (st[1] >> 4),
            ((st[4] << 8) & 0xF00) | st[3], (st[5] << 4) | (st[4] >> 4))
        for i in range(0, 10, 3):
            t += "\n%03X %03X" % (((st[7 + i] << 8) & 0xF00) | st[6 + i], (st[8 + i] << 4) | (st[7 + i] >> 4))
        t += "\n\nStick Parameters 2:\n%03X %03X\n%02X (Deadzone)\n%03X (Range ratio)" % (
            ((st[19] << 8) & 0xF00) | st[18], (st[20] << 4) | (st[19] >> 4),
            ((st[22] << 8) & 0xF00) | st[21], (st[23] << 4) | (st[22] >> 4))
        for i in range(0, 10, 3):
            t += "\n%03X %03X" % (((st[25 + i] << 8) & 0xF00) | st[24 + i], (st[26 + i] << 4) | (st[25 + i] >> 4))

        def stick(name, c):
            if c is None:
                return "\n\n%s:\nNo calibration" % name
            return "\n\n%s:\nCenter X,Y: (%03X, %03X)\nX: [%03X - %03X] Y: [%03X - %03X]" % (
                name, c.x_center, c.y_center, c.x_minus, c.x_plus, c.y_minus, c.y_plus)
        t += stick("L Stick Factory", self.factory_left() if self.has_left else None)
        t += stick("R Stick Factory", self.factory_right() if self.has_right else None)
        t += stick("L Stick User", self.user_left())
        t += stick("R Stick User", self.user_right())

        def sensor(name, d):
            s = "\n\n%s:\nAcc:  " % name
            for i in range(0, 12, 6):
                s += "%04X %04X %04X\n      " % (u16le(d, i), u16le(d, i + 2), u16le(d, i + 4))
            s += "\nGyro: "
            for i in range(12, 24, 6):
                s += "%04X %04X %04X\n      " % (u16le(d, i), u16le(d, i + 2), u16le(d, i + 4))
            return s
        t += sensor("6-Axis Factory (XYZ)", self.factory_sensor)
        t += sensor("6-Axis User (XYZ)", self.user_sensor[2:26]) if self.user_sensor_valid() else "\n\n\n\nUser:\nNo calibration"
        return t

    def summary_text(self):
        """A readable summary (the CLI's "Read calibration data")."""
        def stick(name, c):
            if c is None:
                return "  %s: No calibration\n" % name
            return "  %s: center (%d, %d)  X [%d - %d]  Y [%d - %d]\n" % (
                name, c.x_center, c.y_center, c.x_minus, c.x_plus, c.y_minus, c.y_plus)

        def sensor(name, v):
            return ("  %s Acc:  origin %6d %6d %6d   sensitivity %6d %6d %6d\n" % ((name,) + tuple(v[0:6])) +
                    "  %s Gyro: origin %6d %6d %6d   sensitivity %6d %6d %6d\n" % ((name,) + tuple(v[6:12])))
        t = "Stick calibration (factory):\n"
        if self.has_left:
            t += stick("Left stick ", self.factory_left())
        if self.has_right:
            t += stick("Right stick", self.factory_right())
        t += "Stick calibration (user):\n"
        if self.has_left:
            t += stick("Left stick ", self.user_left())
        if self.has_right:
            t += stick("Right stick", self.user_right())
        t += "6-axis calibration:\n" + sensor("Factory", self.sensor_values(False))
        t += sensor("User   ", self.sensor_values(True)) if self.user_sensor_valid() else "  User: No calibration\n"
        dz, rr = self.stick_params()
        t += "Device parameters (factory):\n  Flat surface ACC offset: %04X %04X %04X\n" % (
            u16le(self.sensor_model, 0), u16le(self.sensor_model, 2), u16le(self.sensor_model, 4))
        t += "  %s: deadzone %d, range ratio %d\n" % ("Left stick " if self.type == PROCON else "Stick", dz, rr)
        if self.type == PROCON:
            dz, rr = self.stick_params(True)
            t += "  Right stick: deadzone %d, range ratio %d\n" % (dz, rr)
        return t


def read_all(jc):
    def spi(offset, length):
        d = jc.get_spi_data(offset, length)
        return d if d is not None else bytes(length)
    stick_model = spi(0x6086, 0x12) + spi(0x6098, 0x12)
    return CalibrationInfo(jc.type, spi(0x603D, 0x12), spi(0x8010, 0x16), spi(0x6020, 0x18),
                           spi(0x8026, 0x1A), spi(0x6080, 0x6), stick_model)


def read_status(jc):
    """The calibration status only (two small SPI reads)."""
    user = jc.get_spi_data(0x8010, 22) or b"\xFF" * 22
    sensor = jc.get_spi_data(0x8026, 2) or b"\xFF\xFF"
    info = CalibrationInfo(jc.type, bytes(18), user, bytes(24), sensor + bytes(24), bytes(6), bytes(36))
    return info.status()


def write_user_calibration(jc, left=None, right=None, sensor=None):
    """The Manual tab's Write Cal: writes both stick user calibrations (None erases one)
    and the 6-axis one (None erases; else (acc XYZ, gyro XYZ) origins). 0 or 1."""
    stick = bytearray(b"\xFF" * 22)
    if left is not None and jc.type != JOYCON_R:
        stick[0:11] = MAGIC + encode_stick(left, True)
    if right is not None and jc.type != JOYCON_L:
        stick[11:22] = MAGIC + encode_stick(right, False)
    sens = bytearray(b"\xFF" * 26)
    if sensor is not None:
        sens = bytearray(26)
        sens[0:2] = MAGIC
        for i in range(3):
            sens[2 + i * 2:4 + i * 2] = (sensor[i] & 0xFFFF).to_bytes(2, "little")
            sens[14 + i * 2:16 + i * 2] = (sensor[3 + i] & 0xFFFF).to_bytes(2, "little")
    res = jc.write_spi_data(0x8010, stick)
    if res == 0:
        res = jc.write_spi_data(0x8026, sens)
    return res


def write_stick_params(jc, main, pro_right=None):
    """Stick device parameters (deadzone, range ratio): factory values! 0 or 1."""
    res = jc.write_spi_data(0x6089, encode_stick_params(*main))
    if res == 0 and jc.type == PROCON and pro_right is not None:
        res = jc.write_spi_data(0x609B, encode_stick_params(*pro_right))
    return res


def save_stick(jc, left, cal):
    """Writes one stick's user calibration, keeping the other's. 0 or 1."""
    return jc.write_spi_data(0x8010 if left else 0x801B, MAGIC + encode_stick(cal, left))


def factory_stick(jc, left):
    """Erases one stick's user calibration (back to the factory one). 0 or 1."""
    return jc.write_spi_data(0x8010 if left else 0x801B, b"\xFF" * 11)


def save_motion(jc, origins):
    """Writes the 6-axis user calibration: measured origins (acc XYZ, gyro XYZ) with the
    factory sensitivities. 0 or 1."""
    factory = jc.get_spi_data(0x6020, 24) or bytes(24)
    cal = bytearray(26)
    cal[0:2] = MAGIC
    for i in range(3):
        cal[2 + i * 2:4 + i * 2] = (origins[i] & 0xFFFF).to_bytes(2, "little")
        cal[14 + i * 2:16 + i * 2] = (origins[3 + i] & 0xFFFF).to_bytes(2, "little")
    cal[8:14] = factory[6:12]                 # Acc sensitivity
    cal[20:26] = factory[18:24]               # Gyro sensitivity
    return jc.write_spi_data(0x8026, cal)


def factory_motion(jc):
    """Erases the 6-axis user calibration. 0 or 1."""
    return jc.write_spi_data(0x8026, b"\xFF" * 26)


# ----------------------------------------------------------------------------------------
# Guided calibrations. They take input reports (mode 0x30) and keep no device state, so a
# front end feeds them reports from wherever it reads them.

def stick_from_report(report, left):
    s = report[6:9] if left else report[9:12]
    return s[0] | ((s[1] & 0xF) << 8), (s[1] >> 4) | (s[2] << 4)


class StickWizard:
    SECTORS = 24               # Directions that must be reached while rotating
    CENTER_SAMPLES = 45        # ~0.7s of reports at rest
    CENTER_MAX_SPREAD = 0x60
    MIN_HALF_RANGE = 0x300     # Less than this from the center to an edge: not a real rotation

    IDLE, CENTER, ROTATE, DONE = range(4)

    def __init__(self, left):
        self.left = left
        self.reset()

    def reset(self):
        self.step = self.IDLE
        self.x = self.y = -1
        self.recent = []
        self.center = (0, 0)
        self.min_x = self.max_x = self.min_y = self.max_y = 0
        self.sectors = [False] * self.SECTORS
        self.trace = []
        self.result = None
        self.problem = ""

    def start(self):
        self.reset()
        self.step = self.CENTER

    def add_report(self, report):
        self.add_sample(*stick_from_report(report, self.left))

    def _still(self):
        xs = [p[0] for p in self.recent]
        ys = [p[1] for p in self.recent]
        return max(xs) - min(xs) <= self.CENTER_MAX_SPREAD and max(ys) - min(ys) <= self.CENTER_MAX_SPREAD

    def add_sample(self, x, y):
        self.x, self.y = x, y
        if self.step == self.CENTER:
            self.recent.append((x, y))
            if len(self.recent) > self.CENTER_SAMPLES:
                self.recent.pop(0)
            if not self._still():
                self.recent = self.recent[-1:]         # Moved: start over
            if len(self.recent) == self.CENTER_SAMPLES:
                n = self.CENTER_SAMPLES
                self.center = ((sum(p[0] for p in self.recent) + n // 2) // n,
                               (sum(p[1] for p in self.recent) + n // 2) // n)
                self.min_x = self.max_x = self.center[0]
                self.min_y = self.max_y = self.center[1]
                self.step = self.ROTATE
        elif self.step == self.ROTATE:
            self.min_x, self.max_x = min(self.min_x, x), max(self.max_x, x)
            self.min_y, self.max_y = min(self.min_y, y), max(self.max_y, y)
            dx, dy = x - self.center[0], y - self.center[1]
            if dx * dx + dy * dy > self.MIN_HALF_RANGE ** 2:
                a = math.atan2(dy, dx) + math.pi
                self.sectors[min(self.SECTORS - 1, int(a / (2 * math.pi) * self.SECTORS))] = True
            if len(self.trace) < 4000:
                self.trace.append((x, y))

    @property
    def hold_progress(self):
        return len(self.recent) / float(self.CENTER_SAMPLES)

    def sectors_done(self):
        return sum(self.sectors)

    @property
    def can_finish(self):
        return self.step == self.ROTATE and self.sectors_done() == self.SECTORS

    def finish(self):
        """True when measured (self.result: StickCal); else self.problem says why."""
        if not self.can_finish:
            return False
        cx, cy = self.center
        m = self.MIN_HALF_RANGE
        if cx - self.min_x < m or self.max_x - cx < m or cy - self.min_y < m or self.max_y - cy < m:
            self.problem = "The range is too small. Push the stick all the way to its edge while rotating."
            return False
        self.result = StickCal(self.min_x, cx, self.max_x, self.min_y, cy, self.max_y)
        self.step = self.DONE
        return True


class MotionWizard:
    NEED_SAMPLES = 300         # 3 per report: ~1.7s still
    GYRO_MAX_SPREAD = 40       # Raw counts; a still Joy-Con varies by ~10
    ACC_MAX_SPREAD = 150
    ONE_G = 4096               # Accelerometer at +-8G
    GYRO_DEG_PER_COUNT = 4000.0 / 65536   # +-2000 dps over 16 bits (a real 180 degree turn read 180)
    SAMPLE_SECONDS = 0.005     # 3 samples per 15ms report
    TURN_TOLERANCE = 25        # Degrees around 180

    IDLE, MEASURE_A, TURN, MEASURE_B, DONE = range(5)

    def __init__(self, two_positions=False):
        self.two_positions = two_positions
        self.reset()

    def reset(self):
        self.step = self.IDLE
        self.last = [0] * 6
        self.have_sample = False
        self.turned = 0.0
        self.first = [0.0] * 6
        self.result = None
        self.problem = ""
        self._restart()

    def _restart(self):
        self.sum = [0] * 6
        self.count = 0
        self.lo = [0] * 6
        self.hi = [0] * 6

    def start(self):
        self.reset()
        self.step = self.MEASURE_A

    def add_report(self, report):
        for s in range(3):
            base = 13 + s * 12
            self.add_sample([int16(u16le(report, base + i * 2)) for i in range(6)])

    def turn_in_range(self):
        return abs(abs(self.turned) - 180) <= self.TURN_TOLERANCE

    @property
    def can_confirm_turn(self):
        return self.step == self.TURN and self.turn_in_range()

    def confirm_turn(self):
        """The user confirms the 180 degree turn (the gyro must agree): second measurement."""
        if self.can_confirm_turn:
            self.step = self.MEASURE_B
            self._restart()
            return True
        return False

    def add_sample(self, values):
        self.last = list(values)
        self.have_sample = True
        if self.step in (self.TURN, self.MEASURE_B):
            self.turned += (self.last[5] - self.first[5]) * self.GYRO_DEG_PER_COUNT * self.SAMPLE_SECONDS
        if self.step not in (self.MEASURE_A, self.MEASURE_B):
            return
        if self.count == 0:
            self.lo = list(self.last)
            self.hi = list(self.last)
        for i in range(6):
            self.lo[i] = min(self.lo[i], self.last[i])
            self.hi[i] = max(self.hi[i], self.last[i])
            self.sum[i] += self.last[i]
        self.count += 1
        self.problem = ""
        for i in range(3):
            if self.hi[i] - self.lo[i] > self.ACC_MAX_SPREAD or self.hi[i + 3] - self.lo[i + 3] > self.GYRO_MAX_SPREAD:
                self.problem = "The controller moved. Keep it still.."
        z = abs(self.last[2])
        if not self.problem and (z < self.ONE_G * 3 // 4 or z > self.ONE_G * 5 // 4):
            self.problem = "Lay the controller flat (face up or down) on a level surface."
        if not self.problem and self.step == self.MEASURE_B:
            if not self.turn_in_range():
                self.problem = "Turn it back to 180\u00B0 (%d\u00B0 now)." % round(abs(self.turned))
            elif (self.last[2] > 0) != (self.first[2] > 0):
                self.problem = "Keep the same side facing up; only turn it around."
        if self.problem:
            self._restart()
            return
        if self.count < self.NEED_SAMPLES:
            return
        if self.step == self.MEASURE_A and self.two_positions:
            self.first = [s / float(self.count) for s in self.sum]
            self.turned = 0.0
            self.step = self.TURN
            self._restart()
            return
        self._finish()

    def _finish(self):
        mean = [s / float(self.count) for s in self.sum]
        if self.step == self.MEASURE_B:
            # Turned 180 degrees: the tilt's share of X/Y reversed, the sensor offsets didn't
            mean = [(mean[i] + self.first[i]) / 2 for i in range(6)]
        r = [int(round(v)) for v in mean]
        r[2] -= self.ONE_G if r[2] > 0 else -self.ONE_G   # At rest, Z reads 1G
        self.result = r
        self.step = self.DONE

    @property
    def progress(self):
        frac = min(1.0, self.count / float(self.NEED_SAMPLES))
        if self.step == self.DONE:
            return 1.0
        if self.step == self.MEASURE_A:
            return frac / (2 if self.two_positions else 1)
        if self.step == self.TURN:
            return 0.5
        if self.step == self.MEASURE_B:
            return 0.5 + frac / 2
        return 0.0
